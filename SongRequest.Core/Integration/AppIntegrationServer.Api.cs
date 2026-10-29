using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SongRequestDesktopV2Rewrite.Integration;

public sealed partial class AppIntegrationServer
{
    private static readonly TimeSpan SendinCacheLifetime = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _sendinLock = new(1, 1);
    private List<SendinSong>? _sendinCache;
    private DateTime _sendinCacheUtc = DateTime.MinValue;
    private string? _sendinCacheWarning;

    private sealed class SendinSong
    {
        public string Ytid { get; init; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Message { get; init; } = "";
        public bool IsApproved { get; init; }
        public double? SentInTimestamp { get; init; }
        public DateTime? SentInUtc { get; init; }
        public double DurationSeconds { get; set; }
        public bool MetadataCached { get; set; }
    }

    // ── HTTP routing ──────────────────────────────────────────────────

    private async Task<HttpResponse?> HandleApiHttpAsync(HttpRequest request)
    {
        try
        {
            if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                return HttpResponse.Empty(204);

            if (PathEquals(request.Path, "/health"))
            {
                return HttpResponse.Json(new
                {
                    status = "ok",
                    service = "songrequest-api",
                    version = AppInfo.version,
                    utcNow = DateTime.UtcNow
                });
            }

            var cfg = ConfigService.Instance.Current;
            if (!IsAuthorized(request, cfg.ApiToken)) return Unauthorized();

            if (PathEquals(request.Path, "/api/v1/ws"))
            {
                // Returning null lets IntegrationHttpServer perform the upgrade.
                return request.IsWebSocket
                    ? null
                    : HttpResponse.Json(new { error = "Connect with a WebSocket client." }, 400);
            }

            if (request.Path is "/" or "" or "/api" or "/api/v1")
                return HttpResponse.Json(ApiIndex());

            if (PathEquals(request.Path, "/openapi.json") || PathEquals(request.Path, "/api/v1/openapi.json"))
                return HttpResponse.JsonRaw(OpenApiDocument.BuildJson());

            if (PathEquals(request.Path, "/api/v1/capabilities"))
                return HttpResponse.Json(Capabilities());

            if (PathEquals(request.Path, "/api/v1/status"))
                return HttpResponse.JsonRaw(BuildStatus().ToString(Formatting.None));

            if (PathEquals(request.Path, "/api/v1/version"))
                return await CommandResponseAsync("getVersion", new JObject(), request);

            if (PathEquals(request.Path, "/api/v1/songs"))
                return await SongsResponseAsync(request);

            if (PathEquals(request.Path, "/api/v1/search"))
                return await SongsResponseAsync(request, forceAllSources: true);

            if (PathEquals(request.Path, "/api/v1/requests"))
            {
                var result = await GetSongsAsync(
                    "sendinlist",
                    request.QueryValue("q") ?? request.QueryValue("query"),
                    QueryInt(request, "limit", 100),
                    QueryInt(request, "offset", 0),
                    QueryBool(request, "refresh", false),
                    QueryBool(request, "artwork", false),
                    CancellationToken.None);
                result["requests"] = result["songs"]?.DeepClone() ?? new JArray();
                return HttpResponse.JsonRaw(result.ToString(Formatting.None));
            }

            if (PathEquals(request.Path, "/api/v1/blacklist"))
                return HttpResponse.JsonRaw((await GetBlacklistAsync(
                    QueryBool(request, "refresh", false),
                    CancellationToken.None)).ToString(Formatting.None));

            if (PathEquals(request.Path, "/api/v1/player") && IsGet(request))
                return HttpResponse.JsonRaw(BuildPlayerState().ToString(Formatting.None));

            if (PathEquals(request.Path, "/api/v1/queue") && IsGet(request))
                return HttpResponse.JsonRaw(BuildQueueState().ToString(Formatting.None));

            if (PathEquals(request.Path, "/api/v1/queue"))
                return await QueueMutationAsync(request);

            if (PathEquals(request.Path, "/api/v1/queue/shuffle"))
                return await CommandResponseAsync("shuffleQueue", new JObject(), request);

            if (request.Path.StartsWith("/api/v1/queue/", StringComparison.OrdinalIgnoreCase) && IsDelete(request))
            {
                var suffix = request.Path["/api/v1/queue/".Length..];
                if (int.TryParse(suffix, out var index))
                    return await CommandResponseAsync("removeQueueItem", new JObject { ["index"] = index }, request);
            }

            if (PathEquals(request.Path, "/api/v1/player/play-pause"))
                return await CommandResponseAsync("playPause", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/player/next"))
                return await CommandResponseAsync("skipNext", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/player/previous"))
                return await CommandResponseAsync("skipPrevious", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/player/stop"))
                return await CommandResponseAsync("stop", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/player/repeat"))
                return await CommandResponseAsync("toggleRepeat", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/player/seek"))
                return await CommandResponseAsync("seek", ReadJsonBody(request), request);
            if (PathEquals(request.Path, "/api/v1/player/volume"))
                return await CommandResponseAsync("setVolume", ReadJsonBody(request), request);
            if (PathEquals(request.Path, "/api/v1/player/crossfade"))
                return await CommandResponseAsync("setCrossfade", ReadJsonBody(request), request);

            // ── Advanced audio: equalizer, sleep timer, song analysis ────────
            if (PathEquals(request.Path, "/api/v1/equalizer") && IsGet(request))
                return await CommandResponseAsync("getEqualizer", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/equalizer") &&
                (IsPost(request) || IsPut(request) || IsPatch(request)))
                return await CommandResponseAsync("setEqualizer", ReadJsonBody(request), request);
            if (PathEquals(request.Path, "/api/v1/equalizer/presets") && IsGet(request))
                return await CommandResponseAsync("getEqualizer", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/equalizer/presets") && IsPost(request))
                return await CommandResponseAsync("saveEqualizerPreset", ReadJsonBody(request), request);
            if (request.Path.StartsWith("/api/v1/equalizer/presets/", StringComparison.OrdinalIgnoreCase) && IsDelete(request))
            {
                var name = Uri.UnescapeDataString(request.Path["/api/v1/equalizer/presets/".Length..]);
                return await CommandResponseAsync("deleteEqualizerPreset", new JObject { ["name"] = name }, request);
            }
            if (PathEquals(request.Path, "/api/v1/equalizer/reset") && IsPost(request))
                return await CommandResponseAsync("resetEqualizer", new JObject(), request);

            if (PathEquals(request.Path, "/api/v1/sleep-timer") && IsGet(request))
                return await CommandResponseAsync("getSleepTimer", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/sleep-timer") && IsPost(request))
                return await CommandResponseAsync("startSleepTimer", ReadJsonBody(request), request);
            if (PathEquals(request.Path, "/api/v1/sleep-timer") && IsDelete(request))
                return await CommandResponseAsync("cancelSleepTimer", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/sleep-timer/extend") && IsPost(request))
                return await CommandResponseAsync("extendSleepTimer", ReadJsonBody(request), request);
            if (PathEquals(request.Path, "/api/v1/sleep-timer/options") && (IsPost(request) || IsPut(request)))
                return await CommandResponseAsync("setSleepTimerOptions", ReadJsonBody(request), request);

            if (PathEquals(request.Path, "/api/v1/analysis") && IsGet(request))
                return await CommandResponseAsync("getSongAnalysis", QueryObject(request), request);
            if (PathEquals(request.Path, "/api/v1/analysis") && IsPost(request))
                return await CommandResponseAsync("analyzeSong", ReadJsonBody(request), request);
            if (PathEquals(request.Path, "/api/v1/analysis/cache") && IsGet(request))
                return await CommandResponseAsync("getSongAnalysisCache", new JObject(), request);
            if (request.Path.StartsWith("/api/v1/analysis/", StringComparison.OrdinalIgnoreCase) && IsGet(request))
            {
                var key = Uri.UnescapeDataString(request.Path["/api/v1/analysis/".Length..]);
                return await CommandResponseAsync("getSongAnalysis", new JObject { ["analysisKey"] = key }, request);
            }

            if (PathEquals(request.Path, "/api/v1/library/songs"))
                return await CommandResponseAsync("getLibrarySongs", QueryObject(request), request);
            if (PathEquals(request.Path, "/api/v1/library/stats"))
                return await CommandResponseAsync("getLibraryStats", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/library/status"))
                return await CommandResponseAsync("getLibraryStatus", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/library/scan") && IsPost(request))
                return await CommandResponseAsync("scanLibrary", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/library/scan") && IsDelete(request))
                return await CommandResponseAsync("cancelLibraryScan", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/library/home"))
                return await CommandResponseAsync("getHomeFeed", QueryObject(request), request);
            if (PathEquals(request.Path, "/api/v1/library/thumbnails"))
                return await CommandResponseAsync("getThumbnails", ReadJsonBody(request), request);

            if (PathEquals(request.Path, "/api/v1/playlists") && IsGet(request))
                return await CommandResponseAsync("getPlaylists", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/playlists") && IsPost(request))
                return await CommandResponseAsync("createPlaylist", ReadJsonBody(request), request);

            if (request.Path.StartsWith("/api/v1/playlists/", StringComparison.OrdinalIgnoreCase))
                return await PlaylistRouteAsync(request);

            if (PathEquals(request.Path, "/api/v1/history"))
                return await CommandResponseAsync("getPlayHistory", QueryObject(request), request);
            if (request.Path.StartsWith("/api/v1/history/", StringComparison.OrdinalIgnoreCase) && IsDelete(request))
            {
                var entryId = request.Path["/api/v1/history/".Length..];
                return await CommandResponseAsync("removePlayHistoryEntry",
                    new JObject { ["entryId"] = entryId }, request);
            }

            if (PathEquals(request.Path, "/api/v1/played-songs") && IsGet(request))
                return await CommandResponseAsync("getPlayedSongs", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/played-songs") && IsDelete(request))
                return await CommandResponseAsync("clearPlayedSongs", new JObject(), request);

            if (PathEquals(request.Path, "/api/v1/youtube/search"))
                return await CommandResponseAsync("searchYoutube",
                    new JObject { ["query"] = request.QueryValue("q") ?? request.QueryValue("query") ?? "" }, request);
            if (PathEquals(request.Path, "/api/v1/lyrics"))
                return await CommandResponseAsync("getLyrics", QueryObject(request), request);

            if (PathEquals(request.Path, "/api/v1/settings") && IsGet(request))
                return await CommandResponseAsync("getSettings", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/settings") &&
                (IsPut(request) || IsPatch(request) || IsPost(request)))
            {
                var body = ReadJsonBody(request);
                var settings = body["settings"] as JObject ?? body;
                return await CommandResponseAsync("saveSettings", new JObject { ["settings"] = settings }, request);
            }

            if (PathEquals(request.Path, "/api/v1/sendin") && IsGet(request))
                return await CommandResponseAsync("getSendinStatus", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/sendin") && IsPost(request))
                return await CommandResponseAsync("toggleSendin", new JObject(), request);

            if (PathEquals(request.Path, "/api/v1/bad-words") && IsGet(request))
                return await CommandResponseAsync("getBadWords", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/bad-words") && IsPost(request))
                return await CommandResponseAsync("addBadWord", ReadJsonBody(request), request);
            if (PathEquals(request.Path, "/api/v1/bad-words") && IsDelete(request))
                return await CommandResponseAsync("deleteBadWord", ReadJsonBody(request), request);

            if (PathEquals(request.Path, "/api/v1/devices/audio") && IsGet(request))
                return await CommandResponseAsync("getMusicAudioDevices", new JObject(), request);
            if (PathEquals(request.Path, "/api/v1/devices/audio") && IsPost(request))
                return await CommandResponseAsync("setMusicOutputDevice", ReadJsonBody(request), request);

            if (PathEquals(request.Path, "/api/v1/soundboard") && IsGet(request))
                return await CommandResponseAsync("soundboardGetState", new JObject(), request);
            if (request.Path.StartsWith("/api/v1/soundboard/", StringComparison.OrdinalIgnoreCase) && IsPost(request))
            {
                var action = request.Path["/api/v1/soundboard/".Length..];
                var command = "soundboard" + ToPascalCase(action);
                return await CommandResponseAsync(command, ReadJsonBody(request), request);
            }

            if (PathEquals(request.Path, "/api/v1/announcement") && IsGet(request))
                return await CommandResponseAsync("announcementGetState", new JObject(), request);
            if (request.Path.StartsWith("/api/v1/announcement/", StringComparison.OrdinalIgnoreCase) && IsPost(request))
            {
                var action = request.Path["/api/v1/announcement/".Length..];
                var command = "announcement" + ToPascalCase(action);
                return await CommandResponseAsync(command, ReadJsonBody(request), request);
            }

            if (PathEquals(request.Path, "/api/v1/musicshare") && IsGet(request))
                return await CommandResponseAsync("musicShareGetStatus", new JObject(), request);
            if (request.Path.StartsWith("/api/v1/musicshare/", StringComparison.OrdinalIgnoreCase) && IsPost(request))
            {
                var action = request.Path["/api/v1/musicshare/".Length..];
                var command = "musicShare" + ToPascalCase(action);
                return await CommandResponseAsync(command, ReadJsonBody(request), request);
            }

            if (PathEquals(request.Path, "/api/v1/commands") && IsGet(request))
                return HttpResponse.Json(Capabilities());
            if (PathEquals(request.Path, "/api/v1/commands") && IsPost(request))
            {
                var body = ReadJsonBody(request);
                var method = body["command"]?.ToString() ?? body["method"]?.ToString();
                var parameters = body["arguments"] as JObject ?? body["params"] as JObject ?? new JObject();
                if (string.IsNullOrWhiteSpace(method))
                    return HttpResponse.Json(new { error = "Provide 'command' (or 'method') in the body." }, 400);
                return await CommandResponseAsync(method, parameters, request);
            }

            if (request.Path.StartsWith("/api/v1/commands/", StringComparison.OrdinalIgnoreCase) && IsPost(request))
            {
                var method = request.Path["/api/v1/commands/".Length..];
                var body = ReadJsonBody(request);
                var parameters = body["arguments"] as JObject ?? body["params"] as JObject ?? body;
                return await CommandResponseAsync(method, parameters, request);
            }

            // Specific song lookup: /api/v1/songs/{source}/{id}
            if (request.Path.StartsWith("/api/v1/songs/", StringComparison.OrdinalIgnoreCase) && IsGet(request))
                return await SongLookupAsync(request);

            return HttpResponse.Json(new
            {
                error = "Not found",
                path = request.Path,
                hint = "GET /api/v1/capabilities lists every command and event."
            }, 404);
        }
        catch (FormatException ex)
        {
            return HttpResponse.Json(new { error = "Invalid JSON body", message = ex.Message }, 400);
        }
        catch (OperationCanceledException)
        {
            return HttpResponse.Json(new { error = "Request cancelled or timed out" }, 504);
        }
        catch (Exception ex)
        {
            return HttpResponse.Json(new { error = "Request failed", message = ex.Message }, 500);
        }
    }

    private object ApiIndex() => new
    {
        name = "SongRequest local API",
        version = AppInfo.version,
        description = "HTTP + WebSocket access to every SongRequest feature and status.",
        authentication = "Optional. Set a token in Settings > API & MCP and send 'Authorization: Bearer <token>'.",
        endpoints = new
        {
            status = "/api/v1/status",
            capabilities = "/api/v1/capabilities",
            openApi = "/openapi.json",
            songs = "/api/v1/songs?q=&source=all|sendinlist|library",
            search = "/api/v1/search?q=",
            requests = "/api/v1/requests",
            library = "/api/v1/library/songs",
            player = "/api/v1/player",
            queue = "/api/v1/queue",
            settings = "/api/v1/settings",
            commands = "/api/v1/commands/{command}",
            webSocket = "/api/v1/ws"
        },
        documentation = "docs/API-AND-MCP.md"
    };

    private object Capabilities() => new
    {
        version = AppInfo.version,
        protocol = new
        {
            api = "HTTP/1.1 + JSON, WebSocket at /api/v1/ws",
            mcp = "Model Context Protocol over Streamable HTTP (POST /mcp)"
        },
        commands = CommandCatalog.All.Select(c => new
        {
            name = c.Name,
            category = c.Category,
            description = c.Description,
            parameters = c.Parameters,
            dangerous = c.Dangerous
        }),
        events = KnownEvents,
        webSocket = new
        {
            path = "/api/v1/ws",
            authentication = "Optional token via '?token=' or an Authorization header.",
            clientMessages = new object[]
            {
                new { type = "request", id = "1", method = "getLibrarySongs", @params = new { page = 1, pageSize = 25 } },
                new { type = "command", id = "2", method = "playPause" },
                new { type = "subscribe", events = new[] { "requests.updated", "queue.updated" } },
                new { type = "unsubscribe", events = new[] { "player.progress" } },
                new { type = "status" },
                new { type = "ping" }
            },
            serverMessages = new[] { "hello", "response", "event", "pong", "error" }
        }
    };

    // ── WebSocket protocol ────────────────────────────────────────────

    private async Task HandleApiWebSocketConnectedAsync(IntegrationWebSocketClient client)
    {
        var hello = JsonConvert.SerializeObject(new
        {
            type = "hello",
            version = AppInfo.version,
            protocol = 1,
            status = GetStatusObject(),
            sentUtc = DateTime.UtcNow
        });
        await client.SendTextAsync(hello).ConfigureAwait(false);
    }

    private async Task HandleApiWebSocketDisconnectedAsync(IntegrationWebSocketClient client)
        => await Task.CompletedTask;

    private async Task HandleApiWebSocketMessageAsync(IntegrationWebSocketClient client, string text)
    {
        JObject request;
        try { request = JObject.Parse(text); }
        catch
        {
            await SendWsErrorAsync(client, null, "Invalid JSON").ConfigureAwait(false);
            return;
        }

        var type = request["type"]?.ToString()?.ToLowerInvariant();
        switch (type)
        {
            case "ping":
                await client.SendTextAsync(JsonConvert.SerializeObject(new
                {
                    type = "pong",
                    utcNow = DateTime.UtcNow
                })).ConfigureAwait(false);
                return;

            case "subscribe":
            {
                var events = (request["events"] as JArray)?.Select(t => t.ToString()).ToArray() ?? new[] { "*" };
                client.Subscribe(events);
                await client.SendTextAsync(JsonConvert.SerializeObject(new
                {
                    type = "response",
                    id = request["id"],
                    result = new { success = true, events }
                })).ConfigureAwait(false);
                return;
            }

            case "unsubscribe":
            {
                var events = (request["events"] as JArray)?.Select(t => t.ToString()).ToArray() ?? Array.Empty<string>();
                client.Unsubscribe(events, KnownEvents);
                await client.SendTextAsync(JsonConvert.SerializeObject(new
                {
                    type = "response",
                    id = request["id"],
                    result = new { success = true, unsubscribed = events }
                })).ConfigureAwait(false);
                return;
            }

            case "status":
                await client.SendTextAsync(JsonConvert.SerializeObject(new
                {
                    type = "response",
                    id = request["id"],
                    result = BuildStatus()
                })).ConfigureAwait(false);
                return;

            case "request":
            case "command":
            {
                var id = request["id"];
                var method = request["method"]?.ToString() ?? request["command"]?.ToString();
                var parameters = request["params"] as JObject ?? request["arguments"] as JObject;
                if (parameters == null)
                {
                    // Accept the legacy web-UI shape too (arguments at the top level).
                    parameters = new JObject();
                    foreach (var property in request.Properties())
                    {
                        if (property.Name is "type" or "id" or "method" or "command" or "params" or "arguments")
                            continue;
                        parameters[property.Name] = property.Value.DeepClone();
                    }
                }

                if (string.IsNullOrWhiteSpace(method))
                {
                    await SendWsErrorAsync(client, id, "Missing method").ConfigureAwait(false);
                    return;
                }

                // Execute off the receive loop so a long scan/download does not block
                // pings or other commands on the same socket.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await ExecuteCommandAsync(method, parameters).ConfigureAwait(false);
                        await client.SendTextAsync(JsonConvert.SerializeObject(new
                        {
                            type = "response",
                            id,
                            result
                        })).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await SendWsErrorAsync(client, id, ex.Message).ConfigureAwait(false);
                    }
                });
                return;
            }

            default:
                await SendWsErrorAsync(client, request["id"], $"Unknown message type: {type}").ConfigureAwait(false);
                return;
        }
    }

    private static Task SendWsErrorAsync(IntegrationWebSocketClient client, JToken? id, string message)
        => client.SendTextAsync(JsonConvert.SerializeObject(new
        {
            type = "error",
            id,
            error = message
        }));

    // ── Songs, requests and metadata ──────────────────────────────────

    private async Task<HttpResponse> SongsResponseAsync(HttpRequest request, bool forceAllSources = false)
    {
        var source = forceAllSources
            ? "all"
            : request.QueryValue("source") ?? request.QueryValue("from") ?? "all";

        var result = await GetSongsAsync(
            source,
            request.QueryValue("q") ?? request.QueryValue("query"),
            QueryInt(request, "limit", 100),
            QueryInt(request, "offset", 0),
            QueryBool(request, "refresh", false),
            QueryBool(request, "artwork", false),
            CancellationToken.None);

        return HttpResponse.JsonRaw(result.ToString(Formatting.None));
    }

    private async Task<HttpResponse> SongLookupAsync(HttpRequest request)
    {
        var suffix = request.Path["/api/v1/songs/".Length..];
        var slash = suffix.IndexOf('/');
        if (slash <= 0) return HttpResponse.Json(new { error = "Use /api/v1/songs/{sendinlist|library}/{id}" }, 400);

        var source = NormalizeSource(suffix[..slash]);
        var id = Uri.UnescapeDataString(suffix[(slash + 1)..]);
        var includeArtwork = QueryBool(request, "artwork", false);

        if (source == "library")
        {
            var library = _interop?.LibraryService.Data;
            var song = library?.FindSong(id)
                       ?? library?.Songs.FirstOrDefault(s =>
                           string.Equals(s.YouTubeId, id, StringComparison.OrdinalIgnoreCase));
            return song == null
                ? HttpResponse.Json(new { error = "Song not found", source, id }, 404)
                : HttpResponse.JsonRaw(MapLibrarySong(song, includeArtwork).ToString(Formatting.None));
        }

        if (source == "sendinlist")
        {
            var sendin = await FetchSendinListAsync(QueryBool(request, "refresh", false), CancellationToken.None)
                .ConfigureAwait(false);
            var song = sendin.FirstOrDefault(s => string.Equals(s.Ytid, id, StringComparison.OrdinalIgnoreCase));
            return song == null
                ? HttpResponse.Json(new { error = "Song not found", source, id }, 404)
                : HttpResponse.JsonRaw(MapSendinSong(song).ToString(Formatting.None));
        }

        // "all": try the library first, then the send-in list.
        var fallbackLibrary = _interop?.LibraryService.Data;
        var fallbackSong = fallbackLibrary?.FindSong(id)
                           ?? fallbackLibrary?.Songs.FirstOrDefault(s =>
                               string.Equals(s.YouTubeId, id, StringComparison.OrdinalIgnoreCase));
        if (fallbackSong != null)
            return HttpResponse.JsonRaw(MapLibrarySong(fallbackSong, includeArtwork).ToString(Formatting.None));

        var fallbackSendin = await FetchSendinListAsync(false, CancellationToken.None).ConfigureAwait(false);
        var fallbackEntry = fallbackSendin.FirstOrDefault(s => string.Equals(s.Ytid, id, StringComparison.OrdinalIgnoreCase));
        return fallbackEntry == null
            ? HttpResponse.Json(new { error = "Song not found", id }, 404)
            : HttpResponse.JsonRaw(MapSendinSong(fallbackEntry).ToString(Formatting.None));
    }

    /// <summary>
    /// Unified song search across the send-in list and the library. This is the
    /// primary read interface for dashboards, bots and MCP clients.
    /// </summary>
    internal async Task<JObject> GetSongsAsync(
        string? source,
        string? query,
        int limit,
        int offset,
        bool refresh,
        bool includeArtwork,
        CancellationToken cancellationToken)
    {
        source = NormalizeSource(source);
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);

        var songs = new List<JObject>();
        string? warning = null;

        if (source is "all" or "sendinlist")
        {
            try
            {
                var sendin = await FetchSendinListAsync(refresh, cancellationToken).ConfigureAwait(false);
                warning ??= _sendinCacheWarning;
                foreach (var item in sendin)
                {
                    if (!Matches(query, item.Title, item.Artist, item.Message, item.Ytid)) continue;
                    songs.Add(MapSendinSong(item));
                }
            }
            catch (Exception ex)
            {
                warning = $"The send-in list could not be fetched: {ex.Message}";
            }
        }

        if (source is "all" or "library")
        {
            var library = _interop?.LibraryService.Data;
            if (library != null)
            {
                var snapshot = library.Songs.ToArray();
                foreach (var song in snapshot
                             .Where(s => Matches(query, s.Title, s.Artist, s.Album, s.Genre, s.Id, s.YouTubeId))
                             .OrderByDescending(s => s.DateAddedUtc))
                {
                    songs.Add(MapLibrarySong(song, includeArtwork));
                }
            }
        }

        var total = songs.Count;
        var page = songs.Skip(offset).Take(limit).ToArray();

        var result = new JObject
        {
            ["source"] = source,
            ["query"] = query ?? "",
            ["total"] = total,
            ["offset"] = offset,
            ["limit"] = limit,
            ["songs"] = new JArray(page)
        };
        if (!string.IsNullOrWhiteSpace(warning))
            result["warning"] = warning;
        return result;
    }

    private static string NormalizeSource(string? source)
    {
        var value = (source ?? "all").Trim().ToLowerInvariant();
        return value switch
        {
            "sendin" or "sendinlist" or "requests" or "request" => "sendinlist",
            "library" or "local" => "library",
            _ => "all"
        };
    }

    private static bool Matches(string? query, params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var needle = query.Trim();
        return values.Any(v => !string.IsNullOrEmpty(v) &&
                               v.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private JObject MapSendinSong(SendinSong item) => new()
    {
        ["id"] = item.Ytid,
        ["ytid"] = item.Ytid,
        ["source"] = "sendinlist",
        ["title"] = item.Title,
        ["artist"] = item.Artist,
        ["album"] = "",
        ["genre"] = "",
        ["duration"] = item.DurationSeconds,
        ["durationDisplay"] = TimeDisplay.Clock(TimeSpan.FromSeconds(item.DurationSeconds)),
        ["message"] = item.Message,
        ["isApproved"] = item.IsApproved,
        ["sentInTimestamp"] = item.SentInTimestamp,
        ["sentInUtc"] = item.SentInUtc,
        ["metadataCached"] = item.MetadataCached,
        ["thumbnailUrl"] = string.IsNullOrEmpty(item.Ytid)
            ? null
            : $"https://i.ytimg.com/vi/{item.Ytid}/hqdefault.jpg",
        ["sourceUrl"] = string.IsNullOrEmpty(item.Ytid)
            ? null
            : $"https://www.youtube.com/watch?v={item.Ytid}"
    };

    private JObject MapLibrarySong(LibrarySong song, bool includeArtwork) => new()
    {
        ["id"] = song.Id,
        ["songId"] = song.Id,
        ["youTubeId"] = song.YouTubeId,
        ["source"] = "library",
        ["title"] = song.Title,
        ["artist"] = song.Artist,
        ["album"] = song.Album,
        ["genre"] = song.Genre,
        ["duration"] = song.Duration.TotalSeconds,
        ["durationDisplay"] = song.DurationDisplay,
        ["tags"] = new JArray(song.Tags),
        ["filePath"] = song.FilePath,
        ["thumbnailPath"] = song.ThumbnailPath,
        ["artwork"] = includeArtwork ? _interop?.GetThumbnailDataUrl(song.FilePath) : null,
        ["sourceUrl"] = song.SourceUrl,
        ["songSource"] = song.Source.ToString(),
        ["fileStatus"] = song.FileStatus.ToString(),
        ["isMissing"] = song.IsMissing,
        ["playCount"] = song.PlayCount,
        ["dateAddedUtc"] = song.DateAddedUtc,
        ["lastPlayedUtc"] = song.LastPlayedUtc,
        ["fileSizeBytes"] = song.FileSizeBytes,
        ["bitrateKbps"] = song.BitrateKbps,
        ["metadataCached"] = true
    };

    private async Task<List<SendinSong>> FetchSendinListAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _sendinLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _sendinCache != null &&
                DateTime.UtcNow - _sendinCacheUtc < SendinCacheLifetime)
                return _sendinCache;

            try
            {
                var cfg = ConfigService.Instance.Current;
                var fetching = new FetchingService(cfg.BearerToken);
                var json = await fetching.SendRequest("get-database").ConfigureAwait(false);
                var array = JArray.Parse(json);
                var result = new List<SendinSong>();

                foreach (var entry in array)
                {
                    if (entry is not JArray row || row.Count == 0) continue;
                    var ytid = row[0]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(ytid)) continue;

                    var message = row.Count > 1 ? row[1]?.ToString() ?? "" : "";
                    var approved = row.Count > 2 && row[2]?.ToObject<bool>() == true;
                    var timestamp = row.Count > 3 ? row[3]?.ToObject<double?>() : null;
                    DateTime? sentUtc = timestamp.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds((long)timestamp.Value).UtcDateTime
                        : null;

                    var data = _host?.GetOrCreateVideoData(ytid);
                    result.Add(new SendinSong
                    {
                        Ytid = ytid,
                        Title = string.IsNullOrWhiteSpace(data?.Title) ? ytid : data!.Title!,
                        Artist = data?.Creator ?? "",
                        Message = message,
                        IsApproved = approved,
                        SentInTimestamp = timestamp,
                        SentInUtc = sentUtc,
                        DurationSeconds = data != null && data.DurationTicks > 0
                            ? TimeSpan.FromTicks(data.DurationTicks).TotalSeconds
                            : 0,
                        MetadataCached = data != null && !string.IsNullOrWhiteSpace(data.Title)
                    });
                }

                await EnrichSendinMetadataAsync(result, cancellationToken).ConfigureAwait(false);

                _sendinCache = result;
                _sendinCacheUtc = DateTime.UtcNow;
                _sendinCacheWarning = null;
                return result;
            }
            catch
            {
                // Keep serving the last successful snapshot when the companion
                // server is briefly unreachable, but say so in the response.
                if (_sendinCache != null)
                {
                    _sendinCacheWarning = "The companion server is unreachable; showing the last known send-in list.";
                    return _sendinCache;
                }
                throw;
            }
        }
        finally
        {
            _sendinLock.Release();
        }
    }

    private async Task EnrichSendinMetadataAsync(List<SendinSong> songs, CancellationToken cancellationToken)
    {
        var missing = songs.Where(s => !s.MetadataCached).Take(15).ToList();
        if (missing.Count == 0 || _host == null) return;

        using var gate = new SemaphoreSlim(3, 3);
        var tasks = missing.Select(async song =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (title, length, creator) = await _host.YoutubeService
                    .GetVideoMetadataAsync($"https://www.youtube.com/watch?v={song.Ytid}")
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(title))
                {
                    song.Title = title!;
                    song.Artist = creator ?? "";
                    song.DurationSeconds = length.TotalSeconds;
                    song.MetadataCached = true;

                    var data = _host.GetOrCreateVideoData(song.Ytid);
                    data.Title = title;
                    data.Creator = creator ?? "";
                    data.DurationTicks = length.Ticks;
                }
            }
            catch
            {
                // Private/deleted/blocked videos keep their id as the title.
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        // Metadata lookups can be slow when YouTube is rate-limiting. The API
        // should still answer promptly with the ids it has.
        var all = Task.WhenAll(tasks);
        var timeout = Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
        await Task.WhenAny(all, timeout).ConfigureAwait(false);

        // The lookups may still finish after the response was sent; observe their
        // exceptions so a rate-limited YouTube call never becomes unobserved.
        _ = all.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        try { _host.SaveVideoDataCache(); } catch { }
    }

    private async Task<JObject> GetBlacklistAsync(bool refresh, CancellationToken cancellationToken)
    {
        // The send-in cache lives for a few seconds; reuse its lifetime for the
        // blacklist too by simply fetching it alongside when refreshed.
        var cfg = ConfigService.Instance.Current;
        var fetching = new FetchingService(cfg.BearerToken);
        var json = await fetching.SendRequest("get-blacklist").ConfigureAwait(false);
        var array = JArray.Parse(json);

        var rows = new JArray();
        foreach (var entry in array)
        {
            var ytid = entry.ToString();
            if (string.IsNullOrWhiteSpace(ytid)) continue;
            var data = _host?.GetOrCreateVideoData(ytid);
            rows.Add(new JObject
            {
                ["id"] = ytid,
                ["ytid"] = ytid,
                ["title"] = string.IsNullOrWhiteSpace(data?.Title) ? ytid : data!.Title,
                ["artist"] = data?.Creator ?? "",
                ["thumbnailUrl"] = $"https://i.ytimg.com/vi/{ytid}/hqdefault.jpg"
            });
        }

        return new JObject
        {
            ["total"] = rows.Count,
            ["blacklist"] = rows
        };
    }

    // ── Small route helpers ───────────────────────────────────────────

    private async Task<HttpResponse> CommandResponseAsync(string method, JObject parameters, HttpRequest request)
    {
        var result = await ExecuteCommandAsync(method, parameters).ConfigureAwait(false);
        var json = result?.ToString(Formatting.None) ?? "null";
        var failed = result is JObject obj && obj["error"] != null;
        return HttpResponse.JsonRaw(json, failed ? 400 : 200);
    }

    private async Task<HttpResponse> QueueMutationAsync(HttpRequest request)
    {
        if (IsDelete(request))
            return await CommandResponseAsync("clearQueue", new JObject(), request);

        if (!IsPost(request))
            return HttpResponse.Json(new { error = "Use GET, POST or DELETE" }, 405);

        var body = ReadJsonBody(request);
        if (body["ytid"] != null)
            return await CommandResponseAsync("queueSong", body, request);
        if (body["songId"] != null)
            return await CommandResponseAsync("queueLibrarySong", body, request);
        if (body["videoUrl"] != null)
            return await CommandResponseAsync("downloadAndPlayYouTube", body, request);
        return HttpResponse.Json(new { error = "Provide ytid, songId or videoUrl" }, 400);
    }

    private async Task<HttpResponse> PlaylistRouteAsync(HttpRequest request)
    {
        var suffix = request.Path["/api/v1/playlists/".Length..];
        var parts = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return HttpResponse.Json(new { error = "Missing playlist id" }, 400);

        var playlistId = Uri.UnescapeDataString(parts[0]);
        var parameters = new JObject { ["playlistId"] = playlistId };

        if (parts.Length == 1 && IsGet(request))
            return await CommandResponseAsync("getPlaylist", parameters, request);

        if (parts.Length == 1 && (IsPatch(request) || IsPut(request) || IsPost(request)))
        {
            foreach (var property in ReadJsonBody(request).Properties())
                parameters[property.Name] = property.Value;
            return await CommandResponseAsync("updatePlaylist", parameters, request);
        }

        if (parts.Length == 1 && IsDelete(request))
            return await CommandResponseAsync("deletePlaylist", parameters, request);

        if (parts.Length == 2 && parts[1] == "songs" && IsPost(request))
        {
            var body = ReadJsonBody(request);
            parameters["songId"] = body["songId"];
            return await CommandResponseAsync("addSongToPlaylist", parameters, request);
        }

        if (parts.Length == 3 && parts[1] == "entries" && IsDelete(request))
        {
            parameters["entryId"] = Uri.UnescapeDataString(parts[2]);
            return await CommandResponseAsync("removePlaylistEntry", parameters, request);
        }

        if (parts.Length == 2 && parts[1] == "play" && IsPost(request))
            return await CommandResponseAsync("playPlaylist", parameters, request);
        if (parts.Length == 2 && parts[1] == "queue" && IsPost(request))
            return await CommandResponseAsync("queuePlaylist", parameters, request);

        return HttpResponse.Json(new { error = "Unsupported playlist route" }, 404);
    }

    private static JObject QueryObject(HttpRequest request)
    {
        var result = new JObject();
        foreach (var pair in request.Query)
            result[pair.Key] = pair.Value;

        // Convert common numeric/boolean query values so command handlers can use
        // them without each route spelling out every parameter.
        foreach (var property in result.Properties().ToList())
        {
            var value = property.Value.ToString();
            if (int.TryParse(value, out var i) &&
                property.Name is "page" or "pageSize" or "count" or "maxResults" or "index" or "deviceNumber")
                property.Value = i;
            else if (bool.TryParse(value, out var b))
                property.Value = b;
        }
        return result;
    }

    private static JObject ReadJsonBody(HttpRequest request)
    {
        if (request.Body.Length == 0) return new JObject();
        var token = JToken.Parse(request.BodyText());
        if (token is JObject obj) return obj;
        return new JObject { ["value"] = token };
    }

    private static int QueryInt(HttpRequest request, string name, int fallback)
        => int.TryParse(request.QueryValue(name), out var value) ? value : fallback;

    private static bool QueryBool(HttpRequest request, string name, bool fallback)
        => bool.TryParse(request.QueryValue(name), out var value) ? value : fallback;

    private static bool IsGet(HttpRequest request) => string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase);
    private static bool IsPost(HttpRequest request) => string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase);
    private static bool IsPut(HttpRequest request) => string.Equals(request.Method, "PUT", StringComparison.OrdinalIgnoreCase);
    private static bool IsPatch(HttpRequest request) => string.Equals(request.Method, "PATCH", StringComparison.OrdinalIgnoreCase);
    private static bool IsDelete(HttpRequest request) => string.Equals(request.Method, "DELETE", StringComparison.OrdinalIgnoreCase);

    private static bool PathEquals(string path, string expected)
        => string.Equals(path.TrimEnd('/'), expected.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string ToPascalCase(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var parts = value.Split(new[] { '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        foreach (var part in parts)
            builder.Append(char.ToUpperInvariant(part[0])).Append(part[1..]);
        return builder.ToString();
    }
}
