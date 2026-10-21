using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Serves the presentation page over the local network (and to a second display).
///
/// The desktop shows the presentation in its own native window. A phone has no
/// second window, and casting needs a receiver app, so instead the app runs this
/// tiny HTTP server: any TV browser — or an HDMI display through a WebView — opens
/// <see cref="Url"/> and gets the *same* presentation page, fed by the same engine.
///
/// The page is served unchanged; only its host bridge is swapped (the injected
/// <c>bridge-http.js</c> replaces <c>SongRequestHost</c> with an HTTP implementation
/// that polls <c>/state</c>), so the TV view cannot drift from the desktop's.
/// </summary>
internal sealed class StageServer : IDisposable
{
    private readonly MusicEngine _engine;
    private readonly LyricsService _lyrics;
    private readonly string _webRoot;
    private readonly Func<string?, string?>? _thumbnailResolver;

    private readonly Dictionary<string, LyricsResult?> _lyricsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private string? _qrCacheUrl;
    private string? _qrCacheData;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public StageServer(MusicEngine engine, string webRoot, Func<string?, string?>? thumbnailResolver = null)
    {
        _engine = engine;
        _webRoot = webRoot;
        _thumbnailResolver = thumbnailResolver;
        _lyrics = new LyricsService();
    }

    /// <summary>LAN URL a TV should open, e.g. <c>http://192.168.1.5:8099/</c>.</summary>
    public string? Url { get; private set; }

    /// <summary>
    /// Called when a TV browser changes the presentation zoom, so the in-app page
    /// and the HDMI mirror can be updated too.
    /// </summary>
    public Action<double>? ZoomChanged { get; set; }

    public bool IsRunning => _listener != null;

    /// <summary>Starts the server (idempotent) and returns its URL.</summary>
    public string? Start()
    {
        if (_listener != null) return Url;

        try
        {
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var address = LanAddress() ?? "127.0.0.1";
            Url = $"http://{address}:{port}/";

            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            Android.Util.Log.Debug("SongRequest", $"stage server listening on {Url}");

            return Url;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"stage server failed: {ex.Message}");
            _listener = null;
            return null;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
        Url = null;
    }

    public void Dispose() => Stop();

    // ── Server loop ─────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
            catch { return; }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using var _ = client;
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 10000;

            using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream).ConfigureAwait(false);
            if (request == null) return;

            var (method, path, query, body) = request.Value;

            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                await WriteTextAsync(stream, "text/html; charset=utf-8", BuildPresentationPage()).ConfigureAwait(false);
            }
            else if (method == "GET" && path == "/bridge-http.js")
            {
                await WriteTextAsync(stream, "application/javascript; charset=utf-8", BridgeScript).ConfigureAwait(false);
            }
            else if (method == "GET" && path == "/state")
            {
                await WriteTextAsync(stream, "application/json; charset=utf-8", BuildState()).ConfigureAwait(false);
            }
            else if (method == "POST" && path == "/command")
            {
                // Everything the page displays is pushed by the state poll, but the
                // zoom is a setting the TV itself may change. It is the only command
                // accepted from the LAN — this port must never be a remote control.
                HandleCommand(body);
                await WriteTextAsync(stream, "application/json; charset=utf-8", "{\"success\":true}").ConfigureAwait(false);
            }
            else if (method == "GET")
            {
                // Everything else is a file from the web root, addressed the way the
                // page addresses it (relative: "presentation.css", "vendor/…").
                var relative = path.StartsWith("/assets/", StringComparison.Ordinal) ? path["/assets/".Length..] : path.TrimStart('/');
                await WriteAssetAsync(stream, relative).ConfigureAwait(false);
            }
            else
            {
                await WriteAsync(stream, 404, "text/plain", "not found").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"stage request failed: {ex.Message}");
        }
    }

    private static async Task<(string Method, string Path, string Query, string Body)?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var received = new MemoryStream();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var headerEnd = -1;
        string text = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            int read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read <= 0) break;

            received.Write(buffer, 0, read);
            text = Encoding.ASCII.GetString(received.ToArray());
            headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd >= 0) break;
            if (received.Length > 32 * 1024) break;
        }

        if (headerEnd < 0) return null;

        var lines = text[..headerEnd].Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        // Only the command body is read (bounded): every other request is a GET.
        var contentLength = 0;
        foreach (var line in lines)
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(line["Content-Length:".Length..].Trim(), out var parsed) && parsed > 0)
                contentLength = Math.Min(parsed, 64 * 1024);
            break;
        }

        var body = new MemoryStream();
        var alreadyRead = received.Length - (headerEnd + 4);
        if (alreadyRead > 0)
        {
            var bytes = received.ToArray();
            body.Write(bytes, headerEnd + 4, (int)Math.Min(alreadyRead, bytes.Length - (headerEnd + 4)));
        }

        while (body.Length < contentLength)
        {
            int read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read <= 0) break;
            body.Write(buffer, 0, read);
        }

        var target = first[1];
        var q = target.IndexOf('?');
        var path = q >= 0 ? target[..q] : target;
        var query = q >= 0 ? target[(q + 1)..] : string.Empty;

        var bodyText = body.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(body.ToArray(), 0, (int)Math.Min(body.Length, contentLength));

        return (first[0], path, query, bodyText);
    }

    /// <summary>
    /// Accepts exactly one command from a LAN client: the presentation zoom.
    /// Everything else still comes from the phone, so the stage port cannot be
    /// used to control playback, delete requests or change settings.
    /// </summary>
    private void HandleCommand(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;

        try
        {
            var message = Newtonsoft.Json.Linq.JObject.Parse(body);
            if (!string.Equals(message["method"]?.ToString(), "setPresentationZoom", StringComparison.Ordinal)) return;

            var value = message["zoom"] ?? message["data"]?["zoom"];
            if (value == null) return;

            var zoom = Math.Clamp(value.ToObject<double>(), 0.5, 2.0);
            ConfigService.Instance.Update(cfg => cfg.PresentationZoom = zoom);
            ZoomChanged?.Invoke(zoom);
        }
        catch
        {
            // A malformed command must never take the server down.
        }
    }

    private async Task WriteAssetAsync(NetworkStream stream, string relative)
    {
        try
        {
            // Never escape the web root.
            var safe = relative.Replace('\\', '/').TrimStart('/');
            if (safe.Contains("..", StringComparison.Ordinal)) { await WriteAsync(stream, 403, "text/plain", "forbidden").ConfigureAwait(false); return; }

            var full = Path.GetFullPath(Path.Combine(_webRoot, safe));
            if (!full.StartsWith(Path.GetFullPath(_webRoot), StringComparison.Ordinal) || !File.Exists(full))
            {
                await WriteAsync(stream, 404, "text/plain", "not found").ConfigureAwait(false);
                return;
            }

            var bytes = await File.ReadAllBytesAsync(full).ConfigureAwait(false);
            await WriteAsync(stream, 200, ContentTypeFor(full), bytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"stage asset failed: {ex.Message}");
        }
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        ".ttf" => "font/ttf",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };

    private static Task WriteTextAsync(NetworkStream stream, string contentType, string body)
        => WriteAsync(stream, 200, contentType, Encoding.UTF8.GetBytes(body));

    private static Task WriteAsync(NetworkStream stream, int status, string contentType, string body)
        => WriteAsync(stream, status, contentType, Encoding.UTF8.GetBytes(body));

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(status == 200 ? " OK" : " ERR").Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header)).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    // ── Page + state ────────────────────────────────────────────────────────

    /// <summary>The presentation page, with the HTTP bridge injected after hostbridge.js.</summary>
    private string BuildPresentationPage()
    {
        var path = Path.Combine(_webRoot, "presentation.html");
        var html = File.Exists(path) ? File.ReadAllText(path) : "<html><body>presentation.html missing</body></html>";

        const string marker = "<script src=\"hostbridge.js\"></script>";
        var bridge = "<script src=\"hostbridge.js\"></script>\n<script src=\"bridge-http.js\"></script>";

        return html.Contains(marker, StringComparison.Ordinal)
            ? html.Replace(marker, bridge, StringComparison.Ordinal)
            : html.Replace("</body>", bridge + "\n</body>", StringComparison.Ordinal);
    }

    private string BuildState()
    {
        var queue = _engine.Queue.ToList();
        var current = queue.Count > 0 ? queue[0] : null;

        object? nowPlaying = null;
        if (current != null)
        {
            var total = SafeTotalTime();
            var position = SafeCurrentTime();

            nowPlaying = new
            {
                title = current.Title,
                artist = current.Artist,
                duration = Format(total > TimeSpan.Zero ? total : current.Duration),
                totalTime = total.TotalSeconds,
                currentTime = position.TotalSeconds,
                isPlaying = _engine.IsPlaying,
                thumbnail = Thumbnail(current.thumbnail as string, current.songPath),
            };
        }

        var upcoming = queue.Skip(1).Take(3).Select(song => new
        {
            title = song.Title,
            artist = song.Artist,
            thumbnail = Thumbnail(song.thumbnail as string, song.songPath),
            estimatedStart = song.EstimatedStartDisplay ?? string.Empty,
        }).ToList();

        var cfg = ConfigService.Instance.Current;
        var requestUrl = cfg?.RequestUrl ?? string.Empty;

        var payload = new
        {
            nowPlaying,
            queue = upcoming,
            lyrics = current != null ? LyricsFor(current) : null,
            settings = new
            {
                // Names match what presentation.js reads (lyricsFontScale /
                // lyricsHidden, not the internal fontScale / hidden).
                lyricsFontScale = (double)(cfg?.LyricsFontScale ?? 1.0f),
                lyricsHidden = cfg?.LyricsHidden ?? false,
                presentationZoom = Math.Clamp(cfg?.PresentationZoom ?? 1.0, 0.5, 2.0),
                presentationSource = cfg?.PresentationSource ?? "player",
                requestUrl,
                // QRCoder on the phone: a TV may have no internet access, and the
                // page's CDN copy of qrcode.js would then render an empty white box.
                requestQr = QrDataUrl(requestUrl),
                isPlaying = _engine.IsPlaying,
            },
        };

        return Newtonsoft.Json.JsonConvert.SerializeObject(payload);
    }

    private TimeSpan SafeTotalTime()
    {
        try { return _engine.CurrentReader?.TotalTime ?? TimeSpan.Zero; }
        catch { return TimeSpan.Zero; }
    }

    private TimeSpan SafeCurrentTime()
    {
        try { return _engine.CurrentReader?.CurrentTime ?? TimeSpan.Zero; }
        catch { return TimeSpan.Zero; }
    }

    private static string Format(TimeSpan time)
        => TimeDisplay.Clock(time);

    /// <summary>Data URL for artwork, using the same store the desktop uses.</summary>
    private string? Thumbnail(string? inline, string? songPath)
    {
        // Library songs carry no inline artwork on their Song object; the shared
        // resolver (backed by LibraryService/ThumbnailStore) knows their file path.
        if (!string.IsNullOrWhiteSpace(songPath) && _thumbnailResolver != null)
        {
            try
            {
                var resolved = _thumbnailResolver(songPath);
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
            }
            catch { /* fall through to the inline value */ }
        }

        if (string.IsNullOrWhiteSpace(inline)) return null;
        try
        {
            // Inline values are already data URLs (MusicEngine) or a thumbnail file.
            return inline.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? inline
                : ThumbnailStore.Instance.GetDataUrl(inline);
        }
        catch { return null; }
    }

    /// <summary>Request-page QR as a data URL, so the TV needs no CDN/network.</summary>
    private string? QrDataUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        lock (_cacheLock)
        {
            // Generating a PNG every 500 ms poll would be wasteful; the request URL
            // changes rarely, so cache by value.
            if (string.Equals(_qrCacheUrl, url, StringComparison.Ordinal))
                return _qrCacheData;

            try
            {
                using var generator = new QRCoder.QRCodeGenerator();
                using var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
                var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
                _qrCacheData = "data:image/png;base64," + Convert.ToBase64String(png);
            }
            catch { _qrCacheData = null; }

            _qrCacheUrl = url;
            return _qrCacheData;
        }
    }

    /// <summary>Lyrics for the current song (fetched once, then cached).</summary>
    private object? LyricsFor(Song song)
    {
        LyricsResult? result;
        lock (_cacheLock)
        {
            if (!_lyricsCache.TryGetValue(song.songPath, out result))
            {
                result = null;
                try
                {
                    // Same order as the desktop presentation: embedded sidecar/inline
                    // first (works offline), then the host cache, then the network.
                    var embedded = LyricsService.GetEmbeddedLyrics(song.songPath);
                    var lrc = embedded == null || !embedded.Found
                        ? LyricsService.GetLrcFileLyrics(song.songPath)
                        : null;

                    if (embedded != null && embedded.Found) result = embedded;
                    else if (lrc != null && lrc.Found) result = lrc;

                    if (result == null)
                    {
                        var cached = _lyrics.GetCachedLyricsAsync(
                                song.Artist ?? "", song.Title ?? "", song.Duration)
                            .GetAwaiter().GetResult();
                        if (cached != null && cached.Found) result = cached;
                    }

                    if (result == null)
                    {
                        result = _lyrics.GetLyricsAsync(
                                song.Artist ?? "", song.Title ?? "", song.Duration)
                            .GetAwaiter().GetResult();
                    }
                }
                catch { result = null; }

                _lyricsCache[song.songPath] = result;
            }
        }

        if (result == null || !result.Found) return null;

        // The page wants { time, text } lines; the service keeps the LRC text.
        var synced = result.ParseSyncedLines()
            .Select(line => new { time = line.Time.TotalSeconds, text = line.Text })
            .ToList();

        return new
        {
            syncedLyrics = synced,
            plainLyrics = result.PlainLyrics,
            provider = result.Provider.ToString(),
        };
    }

    /// <summary>
    /// The phone's LAN address (what a TV must open). Android's host-name lookup is
    /// unreliable, so the network interfaces are the source of truth.
    /// </summary>
    private static string? LanAddress()
    {
        // Android knows the active network's address; the managed view of the
        // interfaces is unreliable there (Mono reports none on some devices).
        try
        {
            var context = Android.App.Application.Context;
            var connectivity = (Android.Net.ConnectivityManager?)context.GetSystemService(Android.Content.Context.ConnectivityService);
            var network = connectivity?.ActiveNetwork;
            var properties = network != null ? connectivity?.GetLinkProperties(network) : null;

            foreach (var link in properties?.LinkAddresses ?? new System.Collections.Generic.List<Android.Net.LinkAddress>())
            {
                // Java's InetAddress: an IPv4 host address has no ':' in it.
                var text = link.Address?.HostAddress;
                if (string.IsNullOrEmpty(text)) continue;
                if (text.Contains(':')) continue;                                    // IPv6
                if (text.StartsWith("127.", StringComparison.Ordinal)) continue;      // loopback
                if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;  // link-local

                return text;
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"Android LAN address lookup failed: {ex.Message}");
        }

        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                // Android reports OperationalStatus as "Unknown" for every interface,
                // so only loopback can be filtered out reliably.
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = info.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address)) continue;
                    if (address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;   // link-local

                    return address.ToString();
                }
            }

            // Fall back to the DNS view, then to loopback (still useful for the
            // HDMI/second-display case, which loads the page from this device).
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    return address.ToString();
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"LAN address lookup failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// The TV-side host bridge: polls <c>/state</c> and turns it into the events the
    /// presentation page already handles.
    /// </summary>
    private const string BridgeScript = """
(function (global) {
  'use strict';

  var last = { nowPlaying: null, queue: null, lyrics: null, settings: null };

  function emit(handler, eventName, data) {
    handler(JSON.stringify({ type: 'event', eventName: eventName, data: data }));
  }

  // Emit a section only when *that section* changed. The old bridge compared the
  // whole state document, so the ticking currentTime made the queue, lyrics and
  // settings look new every poll: the TV rebuilt its Up Next list and reset the
  // lyrics scroll twice a second.
  function changed(key, value) {
    var text = JSON.stringify(value == null ? null : value);
    if (text === last[key]) return false;
    last[key] = text;
    return true;
  }

  function poll(handler) {
    fetch('state', { cache: 'no-store' })
      .then(function (r) { return r.text(); })
      .then(function (text) {
        var state = JSON.parse(text);

        if (state.nowPlaying != null && changed('nowPlaying', state.nowPlaying)) {
          emit(handler, 'nowPlayingUpdate', state.nowPlaying);
        }

        var queue = state.queue || [];
        if (changed('queue', queue)) {
          emit(handler, 'presentationQueueUpdate', { queue: queue });
        }

        // A missing lyrics object means "this song has none"; send an empty one so
        // the page clears the previous song's lines instead of keeping them.
        var lyrics = state.lyrics || { syncedLyrics: [], plainLyrics: [], provider: '' };
        if (changed('lyrics', lyrics)) {
          emit(handler, 'lyricsUpdate', lyrics);
        }

        if (state.settings != null && changed('settings', state.settings)) {
          emit(handler, 'presentationSettings', state.settings);
        }

      })
      .catch(function () { /* the phone may be gone; keep polling quietly */ });
  }

  var api = {
    registerHostMessageHandler: function (handler) {
      // The page's own registration drives the poll: nothing is pushed to it.
      setInterval(function () { poll(handler); }, 500);
      poll(handler);
      return 'http';
    },
    sendToHost: function (message) {
      try {
        fetch('command', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: typeof message === 'string' ? message : JSON.stringify(message)
        });
      } catch (e) { /* fire and forget */ }
      return true;
    },
    transportName: function () { return 'http'; }
  };

  global.SongRequestHost = api;
})(typeof window !== 'undefined' ? window : globalThis);
""";
}
