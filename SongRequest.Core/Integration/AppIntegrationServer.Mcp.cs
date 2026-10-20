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
    private const string McpProtocolVersion = "2025-06-18";

    private sealed record McpTool(
        string Name,
        string Title,
        string Description,
        JObject InputSchema,
        Func<JObject, CancellationToken, Task<JToken?>> Handler);

    private List<McpTool>? _mcpTools;

    // ── HTTP transport ────────────────────────────────────────────────

    private async Task<HttpResponse?> HandleMcpHttpAsync(HttpRequest request)
    {
        try
        {
            if (string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                return HttpResponse.Empty(204);

            if (PathEquals(request.Path, "/health"))
                return HttpResponse.Json(new
                {
                    status = "ok",
                    service = "songrequest-mcp",
                    version = AppInfo.version,
                    protocolVersion = McpProtocolVersion,
                    transport = "Streamable HTTP (POST /mcp)"
                });

            var cfg = ConfigService.Instance.Current;
            if (!IsAuthorized(request, cfg.McpToken)) return Unauthorized();

            if (IsGet(request) && (PathEquals(request.Path, "/") || PathEquals(request.Path, "/mcp/info")))
                return HttpResponse.Json(McpInfo());

            if (IsGet(request) && PathEquals(request.Path, "/mcp/tools"))
                return HttpResponse.Json(new { tools = BuildTools().Select(ToolJson).ToArray() });

            if (IsGet(request) && PathEquals(request.Path, "/mcp"))
            {
                // Streamable HTTP servers may return 405 for GET when they do not
                // offer a server-to-client SSE stream. Live updates are available
                // through the API WebSocket instead.
                var response = HttpResponse.Json(new
                {
                    error = "Use POST /mcp for Streamable HTTP.",
                    liveUpdates = "Connect to the API WebSocket for live events."
                }, 405);
                response.Headers["Allow"] = "POST, OPTIONS";
                return response;
            }

            if (!IsPost(request))
            {
                var response = HttpResponse.Json(new { error = "Only POST is supported for MCP messages." }, 405);
                response.Headers["Allow"] = "POST, OPTIONS";
                return response;
            }

            if (!PathEquals(request.Path, "/mcp") && !PathEquals(request.Path, "/"))
                return HttpResponse.Json(new { error = "MCP endpoint is /mcp" }, 404);

            JToken body;
            try { body = JToken.Parse(request.BodyText()); }
            catch (Exception ex)
            {
                return JsonRpcError(null, -32700, "Parse error: " + ex.Message);
            }

            if (body is JArray batch)
            {
                var responses = new JArray();
                foreach (var item in batch)
                {
                    if (item is not JObject message) continue;
                    var response = await ProcessMcpMessageAsync(message).ConfigureAwait(false);
                    if (response != null) responses.Add(response);
                }

                return responses.Count == 0
                    ? HttpResponse.Empty(202)
                    : HttpResponse.JsonRaw(responses.ToString(Formatting.None));
            }

            if (body is not JObject single)
                return JsonRpcError(null, -32600, "Invalid Request");

            var result = await ProcessMcpMessageAsync(single).ConfigureAwait(false);
            return result == null ? HttpResponse.Empty(202) : HttpResponse.JsonRaw(result.ToString(Formatting.None));
        }
        catch (OperationCanceledException)
        {
            return HttpResponse.Json(new { error = "Request cancelled or timed out" }, 504);
        }
        catch (Exception ex)
        {
            return JsonRpcError(null, -32603, "Internal error: " + ex.Message);
        }
    }

    private object McpInfo() => new
    {
        name = "songrequest",
        version = AppInfo.version,
        protocolVersion = McpProtocolVersion,
        transport = "Streamable HTTP",
        endpoint = "/mcp",
        authentication = "Optional bearer token from Settings > API & MCP (Authorization: Bearer <token>).",
        capabilities = new
        {
            tools = true,
            resources = true,
            prompts = false,
            liveUpdates = "Use the HTTP API WebSocket at /api/v1/ws."
        },
        documentation = "docs/API-AND-MCP.md"
    };

    private static HttpResponse JsonRpcError(JToken? id, int code, string message)
        => HttpResponse.JsonRaw(JsonConvert.SerializeObject(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message }
        }));

    // ── JSON-RPC / MCP protocol ───────────────────────────────────────

    private async Task<JObject?> ProcessMcpMessageAsync(JObject message)
    {
        var id = message["id"];
        var method = message["method"]?.ToString();
        var isNotification = id == null || id.Type == JTokenType.Null;

        if (string.IsNullOrWhiteSpace(method))
            return isNotification ? null : Error(id, -32600, "Invalid Request: missing method");

        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(id, new JObject
                    {
                        ["protocolVersion"] = McpProtocolVersion,
                        ["capabilities"] = new JObject
                        {
                            ["tools"] = new JObject { ["listChanged"] = false },
                            ["resources"] = new JObject { ["subscribe"] = false, ["listChanged"] = false },
                            ["prompts"] = new JObject { ["listChanged"] = false }
                        },
                        ["serverInfo"] = new JObject
                        {
                            ["name"] = "songrequest",
                            ["title"] = "SongRequest",
                            ["version"] = AppInfo.version
                        },
                        ["instructions"] =
                            "Control the running SongRequest app. Use get_status for an overview, " +
                            "search_songs for send-in and library metadata, and run_songrequest_command " +
                            "for any command listed by get_capabilities."
                    });

                case "notifications/initialized":
                case "notifications/cancelled":
                case "notifications/roots/list_changed":
                    return null;

                case "ping":
                    return Result(id, new JObject());

                case "logging/setLevel":
                    return Result(id, new JObject());

                case "tools/list":
                    return Result(id, new JObject
                    {
                        ["tools"] = new JArray(BuildTools().Select(ToolJson))
                    });

                case "tools/call":
                    return await CallToolAsync(id, message["params"] as JObject).ConfigureAwait(false);

                case "resources/list":
                    return Result(id, new JObject
                    {
                        ["resources"] = BuildResources()
                    });

                case "resources/templates/list":
                    return Result(id, new JObject
                    {
                        ["resourceTemplates"] = new JArray
                        {
                            new JObject
                            {
                                ["uriTemplate"] = "songrequest://songs/{source}/{id}",
                                ["name"] = "Song",
                                ["description"] = "One send-in-list or library song by source and id.",
                                ["mimeType"] = "application/json"
                            }
                        }
                    });

                case "resources/read":
                    return await ReadResourceAsync(id, message["params"] as JObject).ConfigureAwait(false);

                case "prompts/list":
                    return Result(id, new JObject { ["prompts"] = new JArray() });

                case "completion/complete":
                    return Result(id, new JObject { ["completion"] = new JObject { ["values"] = new JArray() } });

                default:
                    return isNotification ? null : Error(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex)
        {
            return isNotification ? null : Error(id, -32603, "Internal error: " + ex.Message);
        }
    }

    private static JObject Result(JToken? id, JObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result
    };

    private static JObject Error(JToken? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JObject { ["code"] = code, ["message"] = message }
    };

    private async Task<JObject> CallToolAsync(JToken? id, JObject? parameters)
    {
        var name = parameters?["name"]?.ToString();
        var arguments = parameters?["arguments"] as JObject ?? new JObject();

        if (string.IsNullOrWhiteSpace(name))
            return Error(id, -32602, "Invalid params: tool name is required");

        var tool = BuildTools().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (tool == null)
            return Error(id, -32602, $"Unknown tool: {name}");

        try
        {
            var result = await tool.Handler(arguments, CancellationToken.None).ConfigureAwait(false);
            var text = result?.ToString(Formatting.Indented) ?? "null";
            var isError = result is JObject obj && obj["error"] != null;

            var content = new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = text
                    }
                },
                ["isError"] = isError
            };

            if (result is JObject structured)
                content["structuredContent"] = structured;

            return Result(id, content);
        }
        catch (Exception ex)
        {
            return Result(id, new JObject
            {
                ["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = "Tool failed: " + ex.Message
                    }
                },
                ["isError"] = true
            });
        }
    }

    private static JObject ToolJson(McpTool tool) => new()
    {
        ["name"] = tool.Name,
        ["title"] = tool.Title,
        ["description"] = tool.Description,
        ["inputSchema"] = tool.InputSchema
    };

    // ── Resources ─────────────────────────────────────────────────────

    private static JArray BuildResources()
    {
        JObject Resource(string uri, string name, string description) => new()
        {
            ["uri"] = uri,
            ["name"] = name,
            ["description"] = description,
            ["mimeType"] = "application/json"
        };

        return new JArray
        {
            Resource("songrequest://status", "Application status", "Version, servers, player, queue and library totals."),
            Resource("songrequest://capabilities", "Command catalogue", "Every command, parameter and WebSocket event."),
            Resource("songrequest://player", "Player", "Current track, transport state, volume and crossfade."),
            Resource("songrequest://queue", "Queue", "Play queue and playlist queue."),
            Resource("songrequest://requests", "Send-in list", "Current song requests with cached YouTube metadata."),
            Resource("songrequest://library", "Library", "Library songs with local metadata."),
            Resource("songrequest://library/stats", "Library statistics", "Totals, duplicates and source breakdown."),
            Resource("songrequest://playlists", "Playlists", "Manual and dynamic playlists."),
            Resource("songrequest://history", "Play history", "Recently played library songs."),
            Resource("songrequest://settings", "Settings", "Full application configuration."),
            Resource("songrequest://soundboard", "Soundboard", "Soundboard pages, buttons, volume and MIDI state."),
            Resource("songrequest://announcement", "Announcement", "Announcement state and settings."),
            Resource("songrequest://musicshare", "Music Share", "Music Share session status.")
        };
    }

    private async Task<JObject> ReadResourceAsync(JToken? id, JObject? parameters)
    {
        var uri = parameters?["uri"]?.ToString() ?? "";
        JToken? content;

        switch (uri.TrimEnd('/').ToLowerInvariant())
        {
            case "songrequest://status":
                content = BuildStatus();
                break;
            case "songrequest://capabilities":
                content = JObject.FromObject(Capabilities());
                break;
            case "songrequest://player":
                content = BuildPlayerState();
                break;
            case "songrequest://queue":
                content = BuildQueueState();
                break;
            case "songrequest://requests":
                content = await GetSongsAsync("sendinlist", null, 200, 0, false, false, CancellationToken.None)
                    .ConfigureAwait(false);
                break;
            case "songrequest://library":
                content = await GetSongsAsync("library", null, 200, 0, false, false, CancellationToken.None)
                    .ConfigureAwait(false);
                break;
            case "songrequest://library/stats":
                content = await ExecuteCommandAsync("getLibraryStats").ConfigureAwait(false);
                break;
            case "songrequest://playlists":
                content = await ExecuteCommandAsync("getPlaylists").ConfigureAwait(false);
                break;
            case "songrequest://history":
                content = await ExecuteCommandAsync("getPlayHistory", new JObject { ["count"] = 50 }).ConfigureAwait(false);
                break;
            case "songrequest://settings":
                content = await ExecuteCommandAsync("getSettings").ConfigureAwait(false);
                break;
            case "songrequest://soundboard":
                content = await ExecuteCommandAsync("soundboardGetState").ConfigureAwait(false);
                break;
            case "songrequest://announcement":
                content = await ExecuteCommandAsync("announcementGetState").ConfigureAwait(false);
                break;
            case "songrequest://musicshare":
                content = await ExecuteCommandAsync("musicShareGetStatus").ConfigureAwait(false);
                break;
            default:
                if (uri.StartsWith("songrequest://songs/", StringComparison.OrdinalIgnoreCase))
                {
                    var suffix = uri["songrequest://songs/".Length..];
                    var slash = suffix.IndexOf('/');
                    if (slash > 0)
                    {
                        var source = suffix[..slash];
                        var songId = Uri.UnescapeDataString(suffix[(slash + 1)..]);
                        var songs = await GetSongsAsync(source, null, 1000, 0, false, false, CancellationToken.None)
                            .ConfigureAwait(false);
                        var match = (songs["songs"] as JArray)?.FirstOrDefault(s =>
                            string.Equals(s["id"]?.ToString(), songId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s["ytid"]?.ToString(), songId, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                        {
                            content = match;
                            break;
                        }
                    }
                }
                return Error(id, -32602, $"Unknown resource: {uri}");
        }

        return Result(id, new JObject
        {
            ["contents"] = new JArray
            {
                new JObject
                {
                    ["uri"] = uri,
                    ["mimeType"] = "application/json",
                    ["text"] = content?.ToString(Formatting.Indented) ?? "null"
                }
            }
        });
    }

    // ── Tool catalogue ────────────────────────────────────────────────

    private List<McpTool> BuildTools()
    {
        if (_mcpTools != null) return _mcpTools;

        var tools = new List<McpTool>();

        McpTool Tool(string name, string title, string description, JObject schema,
            Func<JObject, CancellationToken, Task<JToken?>> handler)
            => new(name, title, description, schema, handler);

        McpTool Command(string name, string title, string description, string command, JObject? schema = null)
            => Tool(name, title, description, schema ?? AnySchema(),
                (args, ct) => ExecuteCommandAsync(command, args, ct));

        tools.Add(Tool("get_status", "Get status",
            "Version, server state, player, queue and library totals.",
            Schema(),
            (_, _) => Task.FromResult<JToken?>(BuildStatus())));
        tools.Add(Tool("get_capabilities", "Get capabilities",
            "List every SongRequest command, its parameters and the available WebSocket events.",
            Schema(),
            (_, _) => Task.FromResult<JToken?>(JObject.FromObject(Capabilities()))));

        tools.Add(Tool("search_songs", "Search songs",
            "Search the send-in list and/or the local library. Returns title, artist, album, genre, duration, message, artwork URL and source metadata.",
            Schema(null,
                ("query", "string", "Free-text search over title, artist, album, genre, message and id. Empty lists everything."),
                ("source", "string", "all (default), sendinlist or library."),
                ("limit", "integer", "Maximum results (1-1000, default 50)."),
                ("offset", "integer", "Result offset for paging (default 0)."),
                ("artwork", "boolean", "Include library artwork as a data URL (can be large)."),
                ("refresh", "boolean", "Bypass the 5-second send-in cache.")),
            async (args, ct) =>
            {
                var result = await GetSongsAsync(
                    args["source"]?.ToString() ?? "all",
                    args["query"]?.ToString(),
                    args["limit"]?.ToObject<int?>() ?? 50,
                    args["offset"]?.ToObject<int?>() ?? 0,
                    args["refresh"]?.ToObject<bool?>() ?? false,
                    args["artwork"]?.ToObject<bool?>() ?? false,
                    ct).ConfigureAwait(false);
                return result;
            }));

        tools.Add(Tool("get_song", "Get song",
            "Read one song from the send-in list or library by source and id.",
            Schema("source,id",
                ("source", "string", "sendinlist or library."),
                ("id", "string", "YouTube id for sendinlist, library song id for library.")),
            async (args, ct) =>
            {
                var source = args["source"]?.ToString() ?? "all";
                var id = args["id"]?.ToString() ?? "";
                var result = await GetSongsAsync(source, null, 1000, 0, false, false, ct).ConfigureAwait(false);
                var match = (result["songs"] as JArray)?.FirstOrDefault(s =>
                    string.Equals(s["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s["ytid"]?.ToString(), id, StringComparison.OrdinalIgnoreCase));
                return match ?? new JObject { ["error"] = "Song not found", ["id"] = id };
            }));

        tools.Add(Tool("list_requests", "List requests",
            "Current send-in list with YouTube metadata, approval state and submit messages.",
            Schema(null,
                ("query", "string", "Optional free-text filter."),
                ("limit", "integer", "Maximum results (default 100)."),
                ("offset", "integer", "Result offset."),
                ("refresh", "boolean", "Bypass the 5-second cache.")),
            async (args, ct) => await GetSongsAsync(
                "sendinlist",
                args["query"]?.ToString(),
                args["limit"]?.ToObject<int?>() ?? 100,
                args["offset"]?.ToObject<int?>() ?? 0,
                args["refresh"]?.ToObject<bool?>() ?? false,
                false,
                ct).ConfigureAwait(false)));
        tools.Add(Command("list_playlists", "List playlists", "List manual and dynamic playlists.", "getPlaylists"));
        tools.Add(Command("get_playlist", "Get playlist", "Read one playlist with its songs.",
            "getPlaylist", Schema("playlistId", ("playlistId", "string", "Playlist id."))));
        tools.Add(Command("create_playlist", "Create playlist",
            "Create a playlist. The icon is a Font Awesome class such as fa-list or fa-fire.",
            "createPlaylist", Schema("name",
                ("name", "string", "Playlist name."),
                ("icon", "string", "Font Awesome icon class (default fa-list)."),
                ("emoji", "string", "Optional legacy emoji."))));
        tools.Add(Command("update_playlist", "Update playlist",
            "Rename a playlist or change its icon.",
            "updatePlaylist", Schema("playlistId",
                ("playlistId", "string", "Playlist id."),
                ("name", "string", "New name."),
                ("icon", "string", "New Font Awesome icon class."),
                ("emoji", "string", "Optional legacy emoji."))));
        tools.Add(Command("play_playlist", "Play playlist", "Play a playlist now.",
            "playPlaylist", Schema("playlistId", ("playlistId", "string", "Playlist id."))));
        tools.Add(Command("queue_playlist", "Queue playlist", "Append a playlist to the queue.",
            "queuePlaylist", Schema("playlistId", ("playlistId", "string", "Playlist id."))));
        tools.Add(Command("delete_playlist", "Delete playlist", "Delete a playlist.",
            "deletePlaylist", Schema("playlistId", ("playlistId", "string", "Playlist id."))));
        tools.Add(Tool("get_player", "Get player",
            "Current track, playback state, volume, crossfade, queue counts, audio quality and lyrics status.",
            Schema(),
            (_, _) => Task.FromResult<JToken?>(BuildPlayerState())));
        tools.Add(Tool("get_queue", "Get queue",
            "Read the play queue and playlist queue.",
            Schema(),
            (_, _) => Task.FromResult<JToken?>(BuildQueueState())));

        tools.Add(Command("play_pause", "Play / pause", "Toggle playback.", "playPause"));
        tools.Add(Command("next", "Next", "Play the next queue item.", "skipNext"));
        tools.Add(Command("previous", "Previous", "Play the previous queue item.", "skipPrevious"));
        tools.Add(Command("stop_playback", "Stop", "Stop playback.", "stop"));
        tools.Add(Command("seek", "Seek", "Seek in the current track (normalized 0..1).",
            "seek", Schema("position", ("position", "number", "Normalized position, 0..1."))));
        tools.Add(Command("set_volume", "Set volume", "Set player volume (0..1).",
            "setVolume", Schema("value", ("value", "number", "Volume, 0..1."))));
        tools.Add(Command("set_crossfade", "Set crossfade", "Set crossfade duration in seconds.",
            "setCrossfade", Schema("value", ("value", "number", "Seconds."))));
        tools.Add(Command("toggle_repeat", "Toggle repeat", "Toggle repeat mode.", "toggleRepeat"));
        tools.Add(Command("shuffle_queue", "Shuffle queue", "Shuffle the play queue.", "shuffleQueue"));
        tools.Add(Command("clear_queue", "Clear queue", "Remove every song from the queue.", "clearQueue"));
        tools.Add(Command("queue_song", "Queue song", "Queue a downloaded YouTube song by id.",
            "queueSong", Schema("ytid", ("ytid", "string", "YouTube video id."))));
        tools.Add(Command("play_next", "Play next", "Insert a downloaded song after the current one.",
            "playNext", Schema("ytid", ("ytid", "string", "YouTube video id."))));

        tools.Add(Command("approve_request", "Approve request", "Approve a song request.",
            "approve", Schema("ytid", ("ytid", "string", "YouTube video id."))));
        tools.Add(Command("unapprove_request", "Unapprove request", "Withdraw approval from a request.",
            "unapprove", Schema("ytid", ("ytid", "string", "YouTube video id."))));
        tools.Add(Command("delete_request", "Delete request", "Delete a song request.", "delete",
            Schema("ytid", ("ytid", "string", "YouTube video id."))));
        tools.Add(Command("blacklist_request", "Blacklist request", "Blacklist a video on the server.",
            "blacklist", Schema("ytid", ("ytid", "string", "YouTube video id."))));
        tools.Add(Command("unblacklist_request", "Unblacklist request", "Remove a video from the server blacklist.",
            "unblacklist", Schema("ytid", ("ytid", "string", "YouTube video id."))));
        tools.Add(Command("refresh_requests", "Refresh requests", "Fetch the current send-in list from the companion server.",
            "fetchData"));

        tools.Add(Command("scan_library", "Scan library", "Scan all configured library folders.", "scanLibrary"));
        tools.Add(Command("get_library_songs", "List library songs", "Page through library songs.",
            "getLibrarySongs", Schema(null,
                ("page", "integer", "Page number, starting at 1."),
                ("pageSize", "integer", "Items per page."),
                ("sortBy", "string", "dateAdded, title, artist, album, duration, playCount or fileSize."),
                ("descending", "boolean", "Descending sort."),
                ("filterMissing", "boolean", "Only include present files."))));
        tools.Add(Command("get_library_stats", "Library statistics", "Library totals and duplicates.", "getLibraryStats"));
        tools.Add(Command("get_play_history", "Play history", "Recently played library songs.", "getPlayHistory"));
        tools.Add(Command("get_played_songs", "Played songs", "Server-side played-songs history.", "getPlayedSongs"));

        tools.Add(Command("search_youtube", "Search YouTube", "Search YouTube for videos.",
            "searchYoutube", Schema("query", ("query", "string", "Search query."))));
        tools.Add(Command("get_lyrics", "Get lyrics", "Get lyrics for a YouTube video.",
            "getLyrics", Schema("videoId", ("videoId", "string", "YouTube video id."), ("videoUrl", "string", "Optional video URL."))));

        tools.Add(Command("get_settings", "Get settings", "Read the full application configuration.", "getSettings"));
        tools.Add(Tool("update_settings", "Update settings",
            "Update any settings keys supported by saveSettings (for example {\"autoEnqueue\":true}).",
            new JObject
            {
                ["type"] = "object",
                ["description"] = "Settings object; the keys match get_settings.",
                ["additionalProperties"] = true
            },
            (args, ct) => ExecuteCommandAsync("saveSettings", new JObject { ["settings"] = args }, ct)));

        tools.Add(Command("soundboard_get_state", "Soundboard state", "Read soundboard pages and buttons.", "soundboardGetState"));
        tools.Add(Command("soundboard_play", "Soundboard play", "Play a soundboard button.", "soundboardPlay",
            Schema("row,col", ("row", "integer", "Grid row."), ("col", "integer", "Grid column."))));
        tools.Add(Command("soundboard_stop_all", "Soundboard stop all", "Stop every soundboard clip.", "soundboardStopAll"));
        tools.Add(Command("announcement_get_state", "Announcement state", "Read announcement state.", "announcementGetState"));
        tools.Add(Command("announcement_start", "Announcement start", "Start the microphone announcement.", "announcementStart"));
        tools.Add(Command("announcement_stop", "Announcement stop", "Stop the microphone announcement.", "announcementStop"));
        tools.Add(Command("music_share_status", "Music Share status", "Read Music Share session state.", "musicShareGetStatus"));
        tools.Add(Command("music_share_start", "Music Share start", "Start a Music Share session.", "musicShareStart"));
        tools.Add(Command("music_share_stop", "Music Share stop", "Stop the Music Share session.", "musicShareStop"));

        tools.Add(Tool("run_songrequest_command", "Run any SongRequest command",
            "Execute any command from get_capabilities. Use this for features without a dedicated tool.",
            new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["command"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Command name from get_capabilities.",
                        ["enum"] = new JArray(CommandCatalog.Names)
                    },
                    ["arguments"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "Command arguments as documented in get_capabilities.",
                        ["additionalProperties"] = true
                    }
                },
                ["required"] = new JArray("command"),
                ["additionalProperties"] = false
            },
            (args, ct) =>
            {
                var command = args["command"]?.ToString();
                if (string.IsNullOrWhiteSpace(command))
                    return Task.FromResult<JToken?>(new JObject { ["error"] = "command is required" });
                return ExecuteCommandAsync(command, args["arguments"] as JObject ?? new JObject(), ct);
            }));

        _mcpTools = tools;
        return tools;
    }

    private static JObject AnySchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JObject(),
        ["additionalProperties"] = true
    };

    private static JObject Schema(string? required = null,
        params (string Name, string Type, string Description)[] properties)
    {
        var props = new JObject();
        foreach (var property in properties)
        {
            props[property.Name] = new JObject
            {
                ["type"] = property.Type,
                ["description"] = property.Description
            };
        }

        var schema = new JObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false
        };

        if (!string.IsNullOrWhiteSpace(required))
            schema["required"] = new JArray(required.Split(',', StringSplitOptions.RemoveEmptyEntries));

        return schema;
    }
}
