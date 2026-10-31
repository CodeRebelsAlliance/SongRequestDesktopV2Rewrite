using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SongRequestDesktopV2Rewrite.Integration;

/// <summary>
/// Owns the optional local integration servers: the HTTP/WebSocket API and the
/// MCP (Model Context Protocol) endpoint.
///
/// <para>
/// Both are off by default (see <see cref="Config.ApiEnabled"/> and
/// <see cref="Config.McpEnabled"/>), both bind to loopback unless the user
/// changes the bind address, and both can require a bearer token. The servers
/// execute commands through <see cref="YoutubeFormInterop.ExecuteCommandAsync"/>,
/// which is the same dispatcher the web UI uses, so every feature and status the
/// app has is reachable through either interface.
/// </para>
/// </summary>
public sealed partial class AppIntegrationServer : IDisposable
{
    public static AppIntegrationServer Instance { get; } = new();

    internal static readonly string[] KnownEvents =
    {
        "requests.new",
        "requests.updated",
        "queue.updated",
        "player.progress",
        "nowPlayingUpdate",
        "queueUpdate",
        "lyricsUpdate",
        "downloadProgress",
        "libraryScanProgress",
        "libraryScanCompleted",
        "libraryAnalysisProgress",
        "karaokeGenerationProgress",
        "libraryChanged",
        "midiActivity",
        "soundboardPlayback",
        "soundboardImportProgress",
        "soundboardMidiCaptured",
        "soundboardMidiError",
        "announcementState",
        "musicShareStatus",
        "musicShareMetadataReceived",
        "musicShareError",
        "presentationUpdate",
        "presentationQueueUpdate",
        "presentationSettings",
        "updateProgress",
        "toolProgress",
        "sleepTimerUpdate",
        "equalizerUpdate",
        "songAnalysisProgress",
        "songAnalysisUpdate"
    };

    private readonly object _sync = new();
    private readonly DateTime _createdUtc = DateTime.UtcNow;

    private ISongRequestHost? _host;
    private YoutubeFormInterop? _interop;
    private Config? _subscribedConfig;
    private bool _hostEventsSubscribed;
    private Func<string, JObject?, CancellationToken, Task<JToken?>>? _platformCommands;

    private IntegrationHttpServer? _apiServer;
    private IntegrationHttpServer? _mcpServer;
    private string? _apiError;
    private string? _mcpError;
    private DateTime? _apiStartedUtc;
    private DateTime? _mcpStartedUtc;
    private DateTime _lastProgressEventUtc = DateTime.MinValue;

    private AppIntegrationServer() { }

    /// <summary>True once a host and its interop have been attached.</summary>
    public bool IsInitialized
    {
        get { lock (_sync) return _host != null && _interop != null; }
    }

    /// <summary>
    /// Attaches the running application. Called once per host after the interop
    /// exists (desktop entry point and mobile AppHost).
    /// </summary>
    public void Initialize(ISongRequestHost host, YoutubeFormInterop interop)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(interop);

        lock (_sync)
        {
            if (!ReferenceEquals(_host, host) || !ReferenceEquals(_interop, interop))
            {
                UnsubscribeHostEventsLocked();
                _host = host;
                _interop = interop;
            }
        }

        SubscribeHostEvents();
        ApplyConfig();
    }

    /// <summary>
    /// Starts, stops or restarts the servers so they match the current config.
    /// Safe to call repeatedly and from the settings save path.
    /// </summary>
    public void ApplyConfig()
    {
        lock (_sync)
        {
            if (_host == null || _interop == null) return;

            var cfg = ConfigService.Instance.Current;
            EnsureConfigSubscription(cfg);

            SyncApiServer(cfg);
            SyncMcpServer(cfg);
        }
    }

    private void EnsureConfigSubscription(Config cfg)
    {
        if (ReferenceEquals(_subscribedConfig, cfg)) return;
        if (_subscribedConfig != null)
            _subscribedConfig.PropertyChanged -= OnConfigPropertyChanged;
        _subscribedConfig = cfg;
        _subscribedConfig.PropertyChanged += OnConfigPropertyChanged;
    }

    private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Config.ApiEnabled) or nameof(Config.ApiBindAddress) or nameof(Config.ApiPort) or
            nameof(Config.McpEnabled) or nameof(Config.McpBindAddress) or nameof(Config.McpPort)))
            return;

        ApplyConfig();
    }

    private void SyncApiServer(Config cfg)
    {
        var desiredEnabled = cfg.ApiEnabled;
        var desiredAddress = ParseBindAddress(cfg.ApiBindAddress);
        var desiredPort = cfg.ApiPort;

        if (!desiredEnabled)
        {
            if (_apiServer != null)
            {
                _apiServer.Dispose();
                _apiServer = null;
                _apiStartedUtc = null;
            }
            _apiError = null;
            return;
        }

        if (desiredAddress == null)
        {
            _apiServer?.Dispose();
            _apiServer = null;
            _apiStartedUtc = null;
            _apiError = $"Invalid bind address: {cfg.ApiBindAddress}";
            return;
        }

        if (_apiServer != null && _apiServer.IsRunning &&
            _apiServer.Address.Equals(desiredAddress) && _apiServer.RequestedPort == desiredPort)
            return;

        try
        {
            _apiServer?.Dispose();
            _apiServer = null;
            _apiError = null;

            _apiServer = new IntegrationHttpServer(
                desiredAddress,
                desiredPort,
                HandleApiHttpAsync,
                HandleApiWebSocketConnectedAsync,
                HandleApiWebSocketMessageAsync,
                HandleApiWebSocketDisconnectedAsync);
            _apiServer.Start();
            _apiStartedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _apiError = ex.Message;
            _apiServer?.Dispose();
            _apiServer = null;
            _apiStartedUtc = null;
        }
    }

    private void SyncMcpServer(Config cfg)
    {
        var desiredEnabled = cfg.McpEnabled;
        var desiredAddress = ParseBindAddress(cfg.McpBindAddress);
        var desiredPort = cfg.McpPort;

        if (!desiredEnabled)
        {
            if (_mcpServer != null)
            {
                _mcpServer.Dispose();
                _mcpServer = null;
                _mcpStartedUtc = null;
            }
            _mcpError = null;
            return;
        }

        if (desiredAddress == null)
        {
            _mcpServer?.Dispose();
            _mcpServer = null;
            _mcpStartedUtc = null;
            _mcpError = $"Invalid bind address: {cfg.McpBindAddress}";
            return;
        }

        if (_mcpServer != null && _mcpServer.IsRunning &&
            _mcpServer.Address.Equals(desiredAddress) && _mcpServer.RequestedPort == desiredPort)
            return;

        try
        {
            _mcpServer?.Dispose();
            _mcpServer = null;
            _mcpError = null;

            _mcpServer = new IntegrationHttpServer(
                desiredAddress,
                desiredPort,
                HandleMcpHttpAsync);
            _mcpServer.Start();
            _mcpStartedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _mcpError = ex.Message;
            _mcpServer?.Dispose();
            _mcpServer = null;
            _mcpStartedUtc = null;
        }
    }

    private static IPAddress? ParseBindAddress(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
        if (string.Equals(text, "localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (text is "*" or "0.0.0.0") return IPAddress.Any;
        if (text == "::") return IPAddress.IPv6Any;
        return IPAddress.TryParse(text, out var address) ? address : null;
    }

    /// <summary>
    /// Registers host-specific commands that do not live in the shared interop
    /// (the mobile stage server is currently the only one). Returning <c>null</c>
    /// from the handler means "not handled, use the shared command dispatcher".
    /// </summary>
    public void RegisterPlatformCommandHandler(
        Func<string, JObject?, CancellationToken, Task<JToken?>>? handler)
    {
        lock (_sync) _platformCommands = handler;
    }

    /// <summary>
    /// Executes any application command. Both the API and MCP use this method, so
    /// neither needs its own copy of the application logic.
    /// </summary>
    public async Task<JToken?> ExecuteCommandAsync(string method, JObject? parameters = null, CancellationToken cancellationToken = default)
    {
        var platformHandler = _platformCommands;
        if (platformHandler != null)
        {
            var platformResult = await platformHandler(method, parameters, cancellationToken).ConfigureAwait(false);
            if (platformResult != null) return platformResult;
        }

        var interop = _interop;
        if (interop == null)
            throw new InvalidOperationException("The integration server is not attached to a running application.");

        return await interop.ExecuteCommandAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Snapshot for the Settings panel and the API/MCP status tools.</summary>
    public object GetStatusObject()
    {
        lock (_sync)
        {
            var cfg = ConfigService.Instance.Current;
            return new
            {
                api = new
                {
                    enabled = cfg.ApiEnabled,
                    running = _apiServer?.IsRunning == true,
                    bindAddress = cfg.ApiBindAddress,
                    configuredPort = cfg.ApiPort,
                    port = _apiServer?.Port ?? cfg.ApiPort,
                    webSocketClients = _apiServer?.WebSocketClientCount ?? 0,
                    startedUtc = _apiStartedUtc,
                    error = _apiError
                },
                mcp = new
                {
                    enabled = cfg.McpEnabled,
                    running = _mcpServer?.IsRunning == true,
                    bindAddress = cfg.McpBindAddress,
                    configuredPort = cfg.McpPort,
                    port = _mcpServer?.Port ?? cfg.McpPort,
                    startedUtc = _mcpStartedUtc,
                    error = _mcpError
                },
                documentation = new
                {
                    human = "docs/API-AND-MCP.md",
                    openApi = "/openapi.json",
                    capabilities = "/api/v1/capabilities"
                }
            };
        }
    }

    // ── Authentication ────────────────────────────────────────────────

    private static bool IsAuthorized(HttpRequest request, string? configuredToken)
    {
        if (string.IsNullOrWhiteSpace(configuredToken)) return true;

        var provided = request.Header("Authorization");
        if (string.IsNullOrWhiteSpace(provided))
            provided = request.Header("X-Api-Token");
        if (string.IsNullOrWhiteSpace(provided))
            provided = request.QueryValue("token") ?? request.QueryValue("access_token");

        if (string.IsNullOrWhiteSpace(provided)) return false;

        provided = provided.Trim();
        if (provided.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            provided = provided[7..].Trim();

        // Hash both sides first so the comparison is constant-time regardless of
        // the supplied token's length.
        var expectedHash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(configuredToken.Trim()));
        var actualHash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(provided));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }

    private static HttpResponse Unauthorized()
    {
        var response = HttpResponse.Json(new
        {
            error = "Unauthorized",
            message = "Provide the configured token as 'Authorization: Bearer <token>', 'X-Api-Token', or '?token='."
        }, 401);
        response.Headers["WWW-Authenticate"] = "Bearer";
        return response;
    }

    // ── Events ────────────────────────────────────────────────────────

    private void SubscribeHostEvents()
    {
        if (_hostEventsSubscribed) return;
        _hostEventsSubscribed = true;

        if (_interop != null)
        {
            _interop.EventRaised += OnInteropEvent;
            _interop.RequestsUpdated += OnRequestsUpdated;
            _interop.NewRequestsDetected += OnNewRequestsDetected;
        }

        var music = _host?.Music;
        if (music != null)
        {
            music.QueueChanged += OnQueueChanged;
            music.PlaylistQueueChanged += OnQueueChanged;
            music.QueuesChanged += OnQueueChanged;
            music.NowPlayingTick += OnNowPlayingTick;
        }
    }

    private void UnsubscribeHostEventsLocked()
    {
        if (!_hostEventsSubscribed) return;
        _hostEventsSubscribed = false;

        if (_interop != null)
        {
            _interop.EventRaised -= OnInteropEvent;
            _interop.RequestsUpdated -= OnRequestsUpdated;
            _interop.NewRequestsDetected -= OnNewRequestsDetected;
        }

        var music = _host?.Music;
        if (music != null)
        {
            music.QueueChanged -= OnQueueChanged;
            music.PlaylistQueueChanged -= OnQueueChanged;
            music.QueuesChanged -= OnQueueChanged;
            music.NowPlayingTick -= OnNowPlayingTick;
        }
    }

    private void OnInteropEvent(string eventName, object? data)
        => _ = BroadcastEventAsync(eventName, data);

    private void OnRequestsUpdated()
    {
        // The UI just fetched a newer database; make the next API read fetch it
        // too instead of serving the previous 5-second snapshot.
        _sendinCacheUtc = DateTime.MinValue;
        _ = BroadcastEventAsync("requests.updated", new { atUtc = DateTime.UtcNow });
    }

    private void OnNewRequestsDetected(IReadOnlyList<NewRequestInfo> requests)
    {
        _sendinCacheUtc = DateTime.MinValue;
        _ = BroadcastEventAsync("requests.new", new
        {
            count = requests.Count,
            requests = requests.Select(r => new
            {
                ytid = r.Ytid,
                sentInUtc = r.SentInUtc,
                message = r.Message,
                isApproved = r.IsApproved
            }).ToArray()
        });
    }

    private void OnQueueChanged(object? sender, EventArgs e)
        => _ = BroadcastEventAsync("queue.updated", BuildQueueState());

    private void OnNowPlayingTick(object? sender, NowPlayingEventArgs e)
    {
        // Progress ticks at 100 ms in the engine; one event per second is enough
        // for dashboards and keeps the socket (and JSON serialiser) quiet.
        var now = DateTime.UtcNow;
        if ((now - _lastProgressEventUtc).TotalMilliseconds < 1000) return;
        _lastProgressEventUtc = now;

        var music = _host?.Music;
        if (music == null) return;

        Song? song = e.Current;
        if (song == null)
        {
            try { song = music.Queue.FirstOrDefault(); } catch { }
        }

        var quality = BuildAudioQuality(song);
        _ = BroadcastEventAsync("player.progress", new
        {
            currentTime = e.CurrentTime.TotalSeconds,
            totalTime = e.TotalTime.TotalSeconds,
            isPlaying = music.IsPlaying,
            isPlaybackActive = music.IsPlaybackActive,
            title = song?.Title ?? "",
            artist = song?.Artist ?? "",
            fileType = quality?["format"],
            bitrateKbps = quality?["bitrateKbps"],
            quality,
            lyricsProvider = _interop?.CurrentLyricsProvider ?? "",
            lyricsAvailable = _interop?.CurrentLyricsAvailable ?? false
        });
    }

    /// <summary>Audio-quality metadata for a queued/library song (mirrors the UI badges).</summary>
    private JObject? BuildAudioQuality(Song? song)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.songPath)) return null;

        var format = string.Empty;
        try { format = System.IO.Path.GetExtension(song.songPath).TrimStart('.').ToUpperInvariant(); }
        catch { }

        var bitrateKbps = 0;
        try
        {
            var librarySong = _interop?.LibraryService.Data.Songs.FirstOrDefault(s =>
                string.Equals(s.FilePath, song.songPath, StringComparison.OrdinalIgnoreCase));
            bitrateKbps = librarySong?.BitrateKbps ?? 0;
        }
        catch { }

        if (string.IsNullOrEmpty(format) && bitrateKbps <= 0) return null;

        var lossless = format is "FLAC" or "WAV" or "ALAC" or "AIFF" or "APE" or "DSF";
        var tier = lossless ? "is-lossless"
            : bitrateKbps >= 256 ? "is-high"
            : bitrateKbps >= 160 ? "is-good"
            : bitrateKbps > 0 ? "is-low"
            : "is-unknown";

        var text = string.IsNullOrEmpty(format) ? "Audio" : format;
        if (lossless) text += " · Lossless";
        else if (bitrateKbps > 0) text += " · " + bitrateKbps + " kbps";

        return new JObject
        {
            ["format"] = format,
            ["bitrateKbps"] = bitrateKbps,
            ["lossless"] = lossless,
            ["text"] = text,
            ["tier"] = tier
        };
    }

    private async Task BroadcastEventAsync(string eventName, object? data)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return;

        var envelope = JsonConvert.SerializeObject(new
        {
            type = "event",
            @event = eventName,
            eventName,
            data,
            sentUtc = DateTime.UtcNow
        });

        IntegrationHttpServer? api;
        lock (_sync) api = _apiServer;

        if (api == null) return;
        try { await api.BroadcastTextAsync(envelope, client => client.WantsEvent(eventName)).ConfigureAwait(false); }
        catch { }
    }

    // ── Snapshots shared by the API and MCP resources ─────────────────

    internal JObject BuildQueueState()
    {
        var music = _host?.Music;
        var queue = new JArray();
        var playlistQueue = new JArray();

        try
        {
            if (music != null)
            {
                int index = 0;
                foreach (var song in music.Queue.ToArray())
                {
                    queue.Add(new JObject
                    {
                        ["index"] = index++,
                        ["title"] = song.Title ?? "",
                        ["artist"] = song.Artist ?? "",
                        ["duration"] = song.Duration.TotalSeconds,
                        ["durationDisplay"] = song.length ?? "",
                        ["songPath"] = song.songPath ?? "",
                        ["estimatedStart"] = song.EstimatedStartDisplay ?? ""
                    });
                }

                index = 0;
                foreach (var song in music.PlaylistQueue.ToArray())
                {
                    playlistQueue.Add(new JObject
                    {
                        ["index"] = index++,
                        ["title"] = song.Title ?? "",
                        ["artist"] = song.Artist ?? "",
                        ["duration"] = song.Duration.TotalSeconds,
                        ["durationDisplay"] = song.length ?? "",
                        ["songPath"] = song.songPath ?? ""
                    });
                }
            }
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["queue"] = queue,
                ["playlistQueue"] = playlistQueue,
                ["error"] = ex.Message
            };
        }

        return new JObject
        {
            ["queue"] = queue,
            ["playlistQueue"] = playlistQueue,
            ["queueCount"] = queue.Count,
            ["playlistQueueCount"] = playlistQueue.Count,
            ["isPlaying"] = music?.IsPlaying ?? false,
            ["isPlaybackActive"] = music?.IsPlaybackActive ?? false,
            ["repeatEnabled"] = music?.RepeatEnabled ?? false
        };
    }

    internal JObject BuildPlayerState()
    {
        var music = _host?.Music;
        double currentTime = 0, totalTime = 0;
        try
        {
            if (music?.CurrentReader != null)
            {
                currentTime = music.CurrentReader.CurrentTime.TotalSeconds;
                totalTime = music.CurrentReader.TotalTime.TotalSeconds;
            }
        }
        catch { }

        JObject? current = null;
        Song? currentSong = null;
        try
        {
            currentSong = music?.Queue.FirstOrDefault();
            if (currentSong != null)
            {
                current = new JObject
                {
                    ["title"] = currentSong.Title ?? "",
                    ["artist"] = currentSong.Artist ?? "",
                    ["duration"] = currentSong.Duration.TotalSeconds,
                    ["durationDisplay"] = currentSong.length ?? "",
                    ["songPath"] = currentSong.songPath ?? "",
                    ["estimatedStart"] = currentSong.EstimatedStartDisplay ?? ""
                };
            }
        }
        catch { }

        var quality = BuildAudioQuality(currentSong);
        if (current != null && quality != null)
        {
            current["fileType"] = quality["format"];
            current["bitrateKbps"] = quality["bitrateKbps"];
            current["lossless"] = quality["lossless"];
        }

        var queue = BuildQueueState();
        return new JObject
        {
            ["isPlaying"] = music?.IsPlaying ?? false,
            ["isPlaybackActive"] = music?.IsPlaybackActive ?? false,
            ["repeatEnabled"] = music?.RepeatEnabled ?? false,
            ["volume"] = music?.VolumeValue ?? 0,
            ["crossfadeSeconds"] = music?.CrossfadeSeconds ?? 0,
            ["canControlVolume"] = music?.CanControlVolume ?? true,
            ["outputDevice"] = music?.OutputDeviceNumber ?? -1,
            ["currentTime"] = currentTime,
            ["totalTime"] = totalTime,
            ["current"] = current,
            ["fileType"] = quality?["format"],
            ["bitrateKbps"] = quality?["bitrateKbps"],
            ["lossless"] = quality?["lossless"],
            ["quality"] = quality,
            ["lyricsProvider"] = _interop?.CurrentLyricsProvider ?? "",
            ["lyricsAvailable"] = _interop?.CurrentLyricsAvailable ?? false,
            ["queueCount"] = queue["queueCount"],
            ["playlistQueueCount"] = queue["playlistQueueCount"]
        };
    }

    internal JObject BuildStatus()
    {
        var cfg = ConfigService.Instance.Current;
        var library = _interop?.LibraryService.Data;
        var settings = new JObject
        {
            ["address"] = cfg.Address,
            ["requestUrl"] = cfg.RequestUrl,
            ["fetchingTimer"] = cfg.FetchingTimer,
            ["threads"] = cfg.Threads,
            ["autoEnqueue"] = cfg.AutoEnqueue,
            ["operatingMode"] = cfg.OperatingMode,
            ["logPlayback"] = cfg.LogPlayback,
            ["normalizeVolume"] = cfg.NormalizeVolume,
            ["enableAnnouncements"] = cfg.EnableAnnouncements,
            ["systemMediaIntegration"] = cfg.SystemMediaIntegration
        };

        var status = new JObject
        {
            ["app"] = new JObject
            {
                ["name"] = "SongRequest",
                ["version"] = AppInfo.version,
                ["platform"] = OperatingSystem.IsAndroid() ? "android"
                    : OperatingSystem.IsIOS() ? "ios"
                    : OperatingSystem.IsWindows() ? "windows"
                    : OperatingSystem.IsMacOS() ? "macos"
                    : "linux",
                ["operatingMode"] = cfg.OperatingMode,
                ["setupComplete"] = _host?.SetupComplete ?? false,
                ["initialLoadComplete"] = _host?.InitialLoadComplete ?? false,
                ["startedUtc"] = _createdUtc,
                ["utcNow"] = DateTime.UtcNow
            },
            ["integration"] = JObject.FromObject(GetStatusObject()),
            ["player"] = BuildPlayerState(),
            ["settings"] = settings
        };

        if (library != null)
        {
            var librarySongs = library.Songs.ToArray();
            status["library"] = new JObject
            {
                ["totalSongs"] = librarySongs.Length,
                ["missingSongs"] = librarySongs.Count(s => s.IsMissing),
                ["playlists"] = library.Playlists.Count,
                ["playHistoryEntries"] = library.PlayHistory.Count,
                ["lastScanUtc"] = library.LastScanUtc
            };
        }

        return status;
    }

    public void Stop()
    {
        lock (_sync)
        {
            UnsubscribeHostEventsLocked();
            if (_subscribedConfig != null)
            {
                _subscribedConfig.PropertyChanged -= OnConfigPropertyChanged;
                _subscribedConfig = null;
            }

            _apiServer?.Dispose();
            _apiServer = null;
            _mcpServer?.Dispose();
            _mcpServer = null;
            _apiStartedUtc = null;
            _mcpStartedUtc = null;
            _apiError = null;
            _mcpError = null;
            _host = null;
            _interop = null;
            _platformCommands = null;
        }
    }

    public void Dispose() => Stop();
}
