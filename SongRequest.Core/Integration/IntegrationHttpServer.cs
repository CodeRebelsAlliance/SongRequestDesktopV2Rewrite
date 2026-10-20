using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite.Integration;

/// <summary>
/// A small dependency-free HTTP/1.1 + WebSocket server used by the local API.
///
/// <para>
/// It is intentionally built on <see cref="TcpListener"/> rather than
/// <c>HttpListener</c> so the exact same code works on the desktop (Windows,
/// Linux, macOS) and inside the Android/iOS sandbox. Only the features the API
/// needs are implemented: JSON bodies, query strings, keep-alive, CORS and
/// RFC 6455 WebSockets. There is no TLS — bind to loopback or put a reverse
/// proxy in front for remote access.
/// </para>
/// </summary>
internal sealed class IntegrationHttpServer : IDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxBodyBytes = 8 * 1024 * 1024;
    private const int MaxWebSocketMessageBytes = 4 * 1024 * 1024;

    private static readonly byte[] ContinueResponse = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
    private static readonly byte[] CrLf = { (byte)'\r', (byte)'\n' };
    private static readonly string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<HttpRequest, Task<HttpResponse?>> _requestHandler;
    private readonly Func<IntegrationWebSocketClient, Task>? _connected;
    private readonly Func<IntegrationWebSocketClient, string, Task>? _message;
    private readonly Func<IntegrationWebSocketClient, Task>? _disconnected;
    private readonly ConcurrentDictionary<IntegrationWebSocketClient, byte> _webSockets = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private Task? _acceptLoop;

    public IntegrationHttpServer(
        IPAddress address,
        int port,
        Func<HttpRequest, Task<HttpResponse?>> requestHandler,
        Func<IntegrationWebSocketClient, Task>? connected = null,
        Func<IntegrationWebSocketClient, string, Task>? message = null,
        Func<IntegrationWebSocketClient, Task>? disconnected = null)
    {
        Address = address;
        RequestedPort = port;
        _requestHandler = requestHandler;
        _connected = connected;
        _message = message;
        _disconnected = disconnected;
        _listener = new TcpListener(address, port);
    }

    public IPAddress Address { get; }

    public int RequestedPort { get; }

    /// <summary>The actual port once started (useful when the requested port was 0).</summary>
    public int Port { get; private set; }

    public bool IsRunning { get; private set; }

    public int WebSocketClientCount => _webSockets.Count;

    public void Start()
    {
        if (IsRunning) return;
        try
        {
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }
        catch { }
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        IsRunning = true;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch
            {
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            _clients[client] = 0;
            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            var reader = new HttpReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var request = await reader.ReadRequestAsync(ct).ConfigureAwait(false);
                if (request == null) break;

                HttpResponse? response;
                try
                {
                    response = await _requestHandler(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response = HttpResponse.Json(new { error = ex.Message }, 500);
                }

                if (response == null)
                {
                    // A null response means "upgrade this request to a WebSocket".
                    if (request.IsWebSocket)
                    {
                        await UpgradeWebSocketAsync(stream, reader, request, client.Client.RemoteEndPoint?.ToString() ?? "", ct)
                            .ConfigureAwait(false);
                        return;
                    }

                    response = HttpResponse.Json(new { error = "Not found" }, 404);
                }

                await WriteResponseAsync(stream, request, response, ct).ConfigureAwait(false);

                if (!request.KeepAlive || response.CloseConnection) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[IntegrationHttpServer] {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(client, out _);
            try { client.Dispose(); } catch { }
        }
    }

    private async Task UpgradeWebSocketAsync(
        NetworkStream stream,
        HttpReader reader,
        HttpRequest request,
        string remoteAddress,
        CancellationToken ct)
    {
        var key = request.Header("Sec-WebSocket-Key");
        if (string.IsNullOrWhiteSpace(key))
        {
            await WriteRawAsync(stream, HttpResponse.Text("Missing Sec-WebSocket-Key", 400), request, ct).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.Header("Sec-WebSocket-Version"), "13", StringComparison.Ordinal))
        {
            await WriteRawAsync(stream, HttpResponse.Text("Unsupported WebSocket version", 426), request, ct).ConfigureAwait(false);
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));
        var headers = new StringBuilder()
            .Append("HTTP/1.1 101 Switching Protocols\r\n")
            .Append("Upgrade: websocket\r\n")
            .Append("Connection: Upgrade\r\n")
            .Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n")
            .Append("\r\n");
        var handshake = Encoding.ASCII.GetBytes(headers.ToString());
        await stream.WriteAsync(handshake, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        // Bytes the HTTP reader buffered after the request header belong to the
        // first WebSocket frame. Prepend them so no frame is lost.
        var prefix = reader.TakeBufferedBytes();
        Stream framedStream = prefix.Length == 0 ? stream : new PrefixedStream(prefix, stream);

        using var socket = WebSocket.CreateFromStream(
            framedStream,
            isServer: true,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(30));

        var client = new IntegrationWebSocketClient(socket, remoteAddress);
        _webSockets[client] = 0;
        try
        {
            if (_connected != null)
                await _connected(client).ConfigureAwait(false);

            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch { }
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary) continue; // API speaks JSON text only

                if (result.Count > 0)
                {
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxWebSocketMessageBytes)
                    {
                        try
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch { }
                        break;
                    }
                }

                if (!result.EndOfMessage) continue;

                if (message.Length > 0)
                {
                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                    if (_message != null)
                    {
                        try { await _message(client, text).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[IntegrationHttpServer] WebSocket handler: {ex.Message}");
                        }
                    }
                }
            }
        }
        finally
        {
            _webSockets.TryRemove(client, out _);
            try
            {
                if (_disconnected != null)
                    await _disconnected(client).ConfigureAwait(false);
            }
            catch { }
            try { client.Dispose(); } catch { }
        }
    }

    /// <summary>Sends a text frame to every connected WebSocket client matching the optional filter.</summary>
    public async Task BroadcastTextAsync(string text, Func<IntegrationWebSocketClient, bool>? filter = null)
    {
        foreach (var client in _webSockets.Keys.ToArray())
        {
            if (filter != null && !filter(client)) continue;
            var ok = await client.SendTextAsync(text).ConfigureAwait(false);
            if (!ok) _webSockets.TryRemove(client, out _);
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        var body = response.Body ?? Array.Empty<byte>();
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(ReasonPhrase(response.StatusCode)).Append("\r\n")
            .Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n")
            .Append("Server: SongRequest\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n");

        if (!string.IsNullOrWhiteSpace(response.ContentType))
            builder.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");

        if (!request.IsWebSocket)
            builder.Append("Connection: ").Append(request.KeepAlive && !response.CloseConnection ? "keep-alive" : "close").Append("\r\n");

        // The API is local/LAN-facing; browser clients need CORS for fetch requests.
        builder.Append("Access-Control-Allow-Origin: *\r\n")
            .Append("Access-Control-Allow-Headers: Authorization, Content-Type, Accept, X-Api-Token, Mcp-Session-Id, Mcp-Protocol-Version\r\n")
            .Append("Access-Control-Allow-Methods: GET, POST, PUT, PATCH, DELETE, OPTIONS\r\n")
            .Append("Access-Control-Max-Age: 600\r\n");

        foreach (var header in response.Headers)
            builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");

        builder.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteRawAsync(NetworkStream stream, HttpResponse response, HttpRequest request, CancellationToken ct)
        => await WriteResponseAsync(stream, request, response, ct).ConfigureAwait(false);

    private static string ReasonPhrase(int status) => status switch
    {
        101 => "Switching Protocols",
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        415 => "Unsupported Media Type",
        426 => "Upgrade Required",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Status"
    };

    public void Stop()
    {
        IsRunning = false;
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }

        foreach (var ws in _webSockets.Keys.ToArray())
        {
            try { ws.Dispose(); } catch { }
            _webSockets.TryRemove(ws, out _);
        }

        foreach (var client in _clients.Keys.ToArray())
        {
            try { client.Dispose(); } catch { }
            _clients.TryRemove(client, out _);
        }
    }

    public void Dispose()
    {
        Stop();
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    /// <summary>
    /// A Stream that first serves bytes the HTTP parser already buffered, then
    /// continues with the network stream. Needed so a client that sends its first
    /// WebSocket frame together with the upgrade request is not truncated.
    /// </summary>
    private sealed class PrefixedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _inner;
        private int _offset;

        public PrefixedStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < _prefix.Length)
            {
                var n = Math.Min(count, _prefix.Length - _offset);
                Array.Copy(_prefix, _offset, buffer, offset, n);
                _offset += n;
                return n;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_offset < _prefix.Length)
            {
                var n = Math.Min(count, _prefix.Length - _offset);
                Array.Copy(_prefix, _offset, buffer, offset, n);
                _offset += n;
                return n;
            }
            return await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                var n = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, n).CopyTo(buffer);
                _offset += n;
                return ValueTask.FromResult(n);
            }
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Buffered HTTP/1.1 request parser.</summary>
    private sealed class HttpReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        public HttpReader(Stream stream) => _stream = stream;

        public async Task<HttpRequest?> ReadRequestAsync(CancellationToken ct)
        {
            var requestLine = await ReadLineAsync(ct).ConfigureAwait(false);
            if (requestLine == null) return null;

            // Tolerate a stray blank line between keep-alive requests.
            while (requestLine.Length == 0)
            {
                requestLine = await ReadLineAsync(ct).ConfigureAwait(false);
                if (requestLine == null) return null;
            }

            var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                throw new IOException("Invalid HTTP request line");

            var method = parts[0].ToUpperInvariant();
            var version = parts[2].ToUpperInvariant();
            var target = parts[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var headerBytes = 0;
            while (true)
            {
                var line = await ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) throw new IOException("Unexpected end of headers");
                if (line.Length == 0) break;
                headerBytes += line.Length;
                if (headerBytes > MaxHeaderBytes) throw new IOException("Headers too large");

                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                headers[name] = headers.TryGetValue(name, out var existing)
                    ? existing + ", " + value
                    : value;
            }

            var (path, query) = ParseTarget(target);

            if (headers.TryGetValue("Expect", out var expect) &&
                expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                await _stream.WriteAsync(ContinueResponse, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }

            var body = await ReadBodyAsync(headers, ct).ConfigureAwait(false);

            var keepAlive = version switch
            {
                "HTTP/1.0" => headers.TryGetValue("Connection", out var c) &&
                              c.Contains("keep-alive", StringComparison.OrdinalIgnoreCase),
                _ => !headers.TryGetValue("Connection", out var c) ||
                     !c.Contains("close", StringComparison.OrdinalIgnoreCase)
            };

            return new HttpRequest(method, target, path, query, version, headers, body, keepAlive);
        }

        private async Task<byte[]> ReadBodyAsync(Dictionary<string, string> headers, CancellationToken ct)
        {
            if (headers.TryGetValue("Transfer-Encoding", out var te) &&
                te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                using var body = new MemoryStream();
                while (true)
                {
                    var sizeLine = await ReadLineAsync(ct).ConfigureAwait(false);
                    if (sizeLine == null) throw new IOException("Unexpected end of chunked body");
                    var semicolon = sizeLine.IndexOf(';');
                    if (semicolon >= 0) sizeLine = sizeLine[..semicolon];
                    if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size))
                        throw new IOException("Invalid chunk size");
                    if (size == 0)
                    {
                        // Trailer headers until an empty line.
                        while (true)
                        {
                            var trailer = await ReadLineAsync(ct).ConfigureAwait(false);
                            if (string.IsNullOrEmpty(trailer)) break;
                        }
                        break;
                    }
                    if (body.Length + size > MaxBodyBytes) throw new IOException("Body too large");
                    var chunk = await ReadExactAsync(size, ct).ConfigureAwait(false);
                    body.Write(chunk, 0, chunk.Length);
                    var crlf = await ReadExactAsync(2, ct).ConfigureAwait(false);
                    if (crlf.Length != 2 || crlf[0] != '\r' || crlf[1] != '\n')
                        throw new IOException("Invalid chunk terminator");
                }
                return body.ToArray();
            }

            if (!headers.TryGetValue("Content-Length", out var lengthText)) return Array.Empty<byte>();
            if (!int.TryParse(lengthText, out var length) || length < 0)
                throw new IOException("Invalid Content-Length");
            if (length == 0) return Array.Empty<byte>();
            if (length > MaxBodyBytes) throw new IOException("Body too large");
            return await ReadExactAsync(length, ct).ConfigureAwait(false);
        }

        private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
        {
            var result = new byte[count];
            var written = 0;
            while (written < count)
            {
                if (_start < _end)
                {
                    var n = Math.Min(count - written, _end - _start);
                    Array.Copy(_buffer, _start, result, written, n);
                    _start += n;
                    written += n;
                    continue;
                }

                var read = await _stream.ReadAsync(result.AsMemory(written, count - written), ct).ConfigureAwait(false);
                if (read <= 0) throw new IOException("Unexpected end of stream");
                written += read;
            }
            return result;
        }

        private async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            using var line = new MemoryStream();
            while (true)
            {
                if (_start >= _end)
                {
                    _start = 0;
                    _end = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
                    if (_end <= 0) return line.Length == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
                }

                var b = _buffer[_start++];
                if (b == '\n')
                {
                    var bytes = line.ToArray();
                    var length = bytes.Length;
                    if (length > 0 && bytes[length - 1] == '\r') length--;
                    return Encoding.ASCII.GetString(bytes, 0, length);
                }

                line.WriteByte(b);
                if (line.Length > MaxHeaderBytes) throw new IOException("Line too long");
            }
        }

        public byte[] TakeBufferedBytes()
        {
            if (_start >= _end) return Array.Empty<byte>();
            var result = new byte[_end - _start];
            Array.Copy(_buffer, _start, result, 0, result.Length);
            _start = _end = 0;
            return result;
        }

        private static (string Path, Dictionary<string, string> Query) ParseTarget(string target)
        {
            // Only an absolute-form proxy request ("GET http://host/path?q") needs
            // unwrapping. Origin-form targets ("/path?q") must keep their query
            // string — Uri.TryCreate would percent-encode the '?' into the path.
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
                    target = absolute.PathAndQuery;
            }

            var queryIndex = target.IndexOf('?');
            var path = queryIndex >= 0 ? target[..queryIndex] : target;
            var queryText = queryIndex >= 0 ? target[(queryIndex + 1)..] : "";
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in queryText.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                var name = equals >= 0 ? pair[..equals] : pair;
                var value = equals >= 0 ? pair[(equals + 1)..] : "";
                query[Decode(name)] = Decode(value);
            }
            return (path, query);
        }

        private static string Decode(string value)
        {
            try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
            catch { return value; }
        }
    }
}

internal sealed class HttpRequest
{
    public HttpRequest(
        string method,
        string target,
        string path,
        Dictionary<string, string> query,
        string version,
        Dictionary<string, string> headers,
        byte[] body,
        bool keepAlive)
    {
        Method = method;
        Target = target;
        Path = path;
        Query = query;
        Version = version;
        Headers = headers;
        Body = body;
        KeepAlive = keepAlive;
    }

    public string Method { get; }
    public string Target { get; }
    public string Path { get; }
    public Dictionary<string, string> Query { get; }
    public string Version { get; }
    public Dictionary<string, string> Headers { get; }
    public byte[] Body { get; }
    public bool KeepAlive { get; }

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public string? QueryValue(string name) => Query.TryGetValue(name, out var value) ? value : null;

    public bool IsWebSocket =>
        Header("Upgrade")?.Contains("websocket", StringComparison.OrdinalIgnoreCase) == true;

    public string BodyText() => Body.Length == 0 ? "" : Encoding.UTF8.GetString(Body);
}

internal sealed class HttpResponse
{
    public int StatusCode { get; set; } = 200;
    public string? ContentType { get; set; } = "application/json; charset=utf-8";
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public bool CloseConnection { get; set; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static HttpResponse Json(object? value, int status = 200)
        => new()
        {
            StatusCode = status,
            ContentType = "application/json; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(value))
        };

    public static HttpResponse JsonRaw(string json, int status = 200)
        => new()
        {
            StatusCode = status,
            ContentType = "application/json; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(json)
        };

    public static HttpResponse Text(string text, int status = 200, string contentType = "text/plain; charset=utf-8")
        => new()
        {
            StatusCode = status,
            ContentType = contentType,
            Body = Encoding.UTF8.GetBytes(text)
        };

    public static HttpResponse Empty(int status = 204)
        => new() { StatusCode = status, ContentType = null, Body = Array.Empty<byte>() };
}

/// <summary>One connected WebSocket client of the local API.</summary>
internal sealed class IntegrationWebSocketClient : IDisposable
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private HashSet<string>? _subscriptions;

    public IntegrationWebSocketClient(WebSocket socket, string remoteAddress)
    {
        _socket = socket;
        RemoteAddress = remoteAddress;
        Id = Guid.NewGuid().ToString("N");
    }

    public string Id { get; }
    public string RemoteAddress { get; }
    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;

    /// <summary>Replaces the event filter. <c>null</c> or a set containing <c>*</c> means all events.</summary>
    public void Subscribe(IEnumerable<string> events)
    {
        var set = new HashSet<string>(events.Where(e => !string.IsNullOrWhiteSpace(e)), StringComparer.OrdinalIgnoreCase);
        _subscriptions = set.Count == 0 ? null : set;
    }

    public void Unsubscribe(IEnumerable<string> events, IEnumerable<string> allEvents)
    {
        var set = _subscriptions == null
            ? new HashSet<string>(allEvents, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(_subscriptions, StringComparer.OrdinalIgnoreCase);

        foreach (var name in events)
            set.Remove(name);

        _subscriptions = set;
    }

    public bool WantsEvent(string eventName)
    {
        var subscriptions = _subscriptions;
        return subscriptions == null || subscriptions.Contains("*") || subscriptions.Contains(eventName);
    }

    public async Task<bool> SendTextAsync(string text)
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open) return false;
            var bytes = Encoding.UTF8.GetBytes(text);
            await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        try { _socket.Dispose(); } catch { }
        try { _sendLock.Dispose(); } catch { }
    }
}
