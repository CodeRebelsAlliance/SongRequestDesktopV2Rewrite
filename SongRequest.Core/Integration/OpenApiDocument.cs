using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SongRequestDesktopV2Rewrite.Integration;

/// <summary>
/// Machine-readable OpenAPI 3.0 description of the local HTTP API. Served at
/// <c>/openapi.json</c> and kept in sync with the route table by hand (the route
/// table is deliberately small and stable).
/// </summary>
internal static class OpenApiDocument
{
    private static string? _json;

    public static string BuildJson()
        => _json ??= Build().ToString(Formatting.Indented);

    private static JObject Build()
    {
        var spec = new JObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JObject
            {
                ["title"] = "SongRequest local API",
                ["version"] = AppInfo.version,
                ["description"] =
                    "HTTP + WebSocket access to every SongRequest feature and status. " +
                    "Authentication is optional: set a token in Settings > API & MCP and send " +
                    "'Authorization: Bearer <token>'. The send-in list and library can be searched " +
                    "with /api/v1/songs and /api/v1/search. Live updates are pushed over " +
                    "/api/v1/ws (WebSocket, JSON messages)."
            },
            ["servers"] = new JArray
            {
                new JObject { ["url"] = "http://127.0.0.1:8730", ["description"] = "Default local API" }
            },
            ["tags"] = new JArray
            {
                Tag("Status"), Tag("Songs"), Tag("Requests"), Tag("Playback"), Tag("Queue"),
                Tag("Library"), Tag("Playlists"), Tag("History"), Tag("Settings"), Tag("Integration"),
                Tag("Advanced Audio")
            },
            ["security"] = new JArray
            {
                new JObject { ["bearerAuth"] = new JArray() },
                new JObject()
            },
            ["paths"] = BuildPaths(),
            ["components"] = new JObject
            {
                ["securitySchemes"] = new JObject
                {
                    ["bearerAuth"] = new JObject
                    {
                        ["type"] = "http",
                        ["scheme"] = "bearer",
                        ["description"] = "The API token configured in Settings > API & MCP. Empty token disables authentication."
                    }
                },
                ["schemas"] = new JObject
                {
                    ["Error"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["error"] = new JObject { ["type"] = "string" },
                            ["message"] = new JObject { ["type"] = "string" }
                        }
                    },
                    ["Song"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "A send-in-list or library song with metadata.",
                        ["properties"] = new JObject
                        {
                            ["id"] = new JObject { ["type"] = "string" },
                            ["source"] = new JObject { ["type"] = "string", ["enum"] = new JArray("sendinlist", "library") },
                            ["ytid"] = new JObject { ["type"] = "string", ["nullable"] = true },
                            ["youTubeId"] = new JObject { ["type"] = "string", ["nullable"] = true },
                            ["title"] = new JObject { ["type"] = "string" },
                            ["artist"] = new JObject { ["type"] = "string" },
                            ["album"] = new JObject { ["type"] = "string" },
                            ["genre"] = new JObject { ["type"] = "string" },
                            ["duration"] = new JObject { ["type"] = "number" },
                            ["durationDisplay"] = new JObject { ["type"] = "string" },
                            ["message"] = new JObject { ["type"] = "string" },
                            ["isApproved"] = new JObject { ["type"] = "boolean" },
                            ["sentInUtc"] = new JObject { ["type"] = "string", ["format"] = "date-time", ["nullable"] = true },
                            ["thumbnailUrl"] = new JObject { ["type"] = "string", ["nullable"] = true },
                            ["artwork"] = new JObject { ["type"] = "string", ["nullable"] = true, ["description"] = "Data URL when artwork=true" },
                            ["fileStatus"] = new JObject { ["type"] = "string", ["nullable"] = true },
                            ["playCount"] = new JObject { ["type"] = "integer", ["nullable"] = true }
                        }
                    },
                    ["CommandRequest"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["command"] = new JObject { ["type"] = "string" },
                            ["arguments"] = new JObject { ["type"] = "object", ["additionalProperties"] = true }
                        },
                        ["required"] = new JArray("command")
                    }
                }
            }
        };

        // WebSocket is not part of OpenAPI; advertise it as an extension.
        spec["x-websocket"] = new JObject
        {
            ["path"] = "/api/v1/ws",
            ["protocol"] = "JSON text frames",
            ["authentication"] = "?token=<token> or Authorization header",
            ["events"] = new JArray(AppIntegrationServer.KnownEvents),
            ["clientRequest"] = new JObject
            {
                ["type"] = "request",
                ["id"] = "1",
                ["method"] = "getLibrarySongs",
                ["params"] = new JObject { ["page"] = 1, ["pageSize"] = 25 }
            }
        };

        return spec;
    }

    private static JObject BuildPaths()
    {
        var paths = new JObject();

        paths["/health"] = new JObject { ["get"] = Op("Liveness probe", "Status", null, null, auth: false) };
        paths["/openapi.json"] = new JObject { ["get"] = Op("This OpenAPI document", "Integration", null, null) };
        paths["/api/v1/status"] = new JObject { ["get"] = Op("Full application and server status", "Status", null, null) };
        paths["/api/v1/capabilities"] = new JObject { ["get"] = Op("Every command, parameter and event", "Integration", null, null) };

        paths["/api/v1/songs"] = new JObject
        {
            ["get"] = Op("Search send-in-list and/or library songs", "Songs", Params(
                ("q", "query", "string", "Free-text search"),
                ("source", "query", "string", "all, sendinlist or library"),
                ("limit", "query", "integer", "1-1000 (default 100)"),
                ("offset", "query", "integer", "Result offset"),
                ("refresh", "query", "boolean", "Bypass the send-in cache"),
                ("artwork", "query", "boolean", "Include library artwork data URLs")), null)
        };
        paths["/api/v1/search"] = new JObject { ["get"] = Op("Search both song sources (alias of /songs?source=all)", "Songs", Params(("q", "query", "string", "Free-text search")), null) };
        paths["/api/v1/songs/{source}/{id}"] = new JObject
        {
            ["get"] = Op("Read one song by source and id", "Songs", PathParams(
                ("source", "sendinlist or library"),
                ("id", "YouTube id or library song id")), null)
        };
        paths["/api/v1/requests"] = new JObject { ["get"] = Op("Send-in list with metadata and approval state", "Requests", Params(("q", "query", "string", "Filter"), ("limit", "query", "integer", "Maximum results"), ("refresh", "query", "boolean", "Bypass cache")), null) };
        paths["/api/v1/blacklist"] = new JObject { ["get"] = Op("Server blacklist with cached metadata", "Requests", null, null) };
        paths["/api/v1/bad-words"] = new JObject
        {
            ["get"] = Op("List blocked words", "Requests", null, null),
            ["post"] = Op("Add a blocked word", "Requests", null, Body(("word", "string"))),
            ["delete"] = Op("Remove a blocked word", "Requests", null, Body(("word", "string")))
        };
        paths["/api/v1/sendin"] = new JObject
        {
            ["get"] = Op("Read the server send-in permission", "Requests", null, null),
            ["post"] = Op("Toggle the server send-in permission", "Requests", null, null)
        };

        paths["/api/v1/player"] = new JObject { ["get"] = Op("Current track, transport state and queue counts", "Playback", null, null) };
        paths["/api/v1/player/play-pause"] = new JObject { ["post"] = Op("Toggle playback", "Playback", null, null) };
        paths["/api/v1/player/next"] = new JObject { ["post"] = Op("Skip to the next queue item", "Playback", null, null) };
        paths["/api/v1/player/previous"] = new JObject { ["post"] = Op("Go to the previous queue item", "Playback", null, null) };
        paths["/api/v1/player/stop"] = new JObject { ["post"] = Op("Stop playback", "Playback", null, null) };
        paths["/api/v1/player/repeat"] = new JObject { ["post"] = Op("Toggle repeat", "Playback", null, null) };
        paths["/api/v1/player/seek"] = new JObject { ["post"] = Op("Seek in the current track", "Playback", null, Body(("position", "number"))) };
        paths["/api/v1/player/volume"] = new JObject { ["post"] = Op("Set volume", "Playback", null, Body(("value", "number"))) };
        paths["/api/v1/player/crossfade"] = new JObject { ["post"] = Op("Set crossfade seconds", "Playback", null, Body(("value", "number"))) };

        paths["/api/v1/equalizer"] = new JObject
        {
            ["get"] = Op("Read the 10-band equalizer, presets and current curve", "Advanced Audio", null, null),
            ["put"] = Op("Apply equalizer enabled/preamp/preset/gains", "Advanced Audio", null,
                Body(("enabled", "boolean"), ("preampDb", "number"), ("preset", "string"), ("gains", "number[]")))
        };
        paths["/api/v1/equalizer/presets"] = new JObject
        {
            ["get"] = Op("List factory and user equalizer presets", "Advanced Audio", null, null),
            ["post"] = Op("Save the current curve as a named preset", "Advanced Audio", null, Body(("name", "string")))
        };
        paths["/api/v1/equalizer/presets/{name}"] = new JObject
        {
            ["delete"] = Op("Delete a user preset", "Advanced Audio", PathParams(("name", "Preset name")), null)
        };
        paths["/api/v1/equalizer/reset"] = new JObject
        {
            ["post"] = Op("Reset and disable the equalizer", "Advanced Audio", null, null)
        };
        paths["/api/v1/sleep-timer"] = new JObject
        {
            ["get"] = Op("Read sleep-timer state and remaining seconds", "Advanced Audio", null, null),
            ["post"] = Op("Start the sleep timer", "Advanced Audio", null,
                Body(("minutes", "number"), ("fadeOutSeconds", "number"), ("playLastSongToEnd", "boolean"))),
            ["delete"] = Op("Cancel the sleep timer", "Advanced Audio", null, null)
        };
        paths["/api/v1/sleep-timer/extend"] = new JObject
        {
            ["post"] = Op("Extend the running sleep timer", "Advanced Audio", null, Body(("minutes", "number")))
        };
        paths["/api/v1/sleep-timer/options"] = new JObject
        {
            ["post"] = Op("Change fade length or play-last-song-to-end", "Advanced Audio", null,
                Body(("fadeOutSeconds", "number"), ("playLastSongToEnd", "boolean")))
        };
        paths["/api/v1/analysis"] = new JObject
        {
            ["get"] = Op("Read a cached BPM/loudness/drop analysis", "Advanced Audio", Params(
                ("songId", "query", "string", "Library song id"), ("songPath", "query", "string", "Audio file path")), null),
            ["post"] = Op("Analyse a song (BPM, loudness, quality, drops, timeline)", "Advanced Audio", null,
                Body(("songId", "string"), ("songPath", "string"), ("force", "boolean")))
        };
        paths["/api/v1/analysis/cache"] = new JObject
        {
            ["get"] = Op("List every cached song analysis", "Advanced Audio", null, null),
            ["delete"] = Op("Delete every cached song analysis", "Advanced Audio", null, null)
        };

        paths["/api/v1/library/analysis"] = new JObject
        {
            ["get"] = Op("Read background library analysis progress", "Advanced Audio", null, null),
            ["post"] = Op("Start background library analysis", "Advanced Audio", null, Body(("force", "boolean"))),
            ["delete"] = Op("Stop background library analysis", "Advanced Audio", null, null)
        };

        paths["/api/v1/queue"] = new JObject
        {
            ["get"] = Op("Read play and playlist queues", "Queue", null, null),
            ["post"] = Op("Queue a song by ytid, songId or videoUrl", "Queue", null, Body(("ytid", "string"), ("songId", "string"), ("videoUrl", "string"))),
            ["delete"] = Op("Clear the play queue", "Queue", null, null)
        };
        paths["/api/v1/queue/{index}"] = new JObject { ["delete"] = Op("Remove a queue item", "Queue", PathParams(("index", "Queue index")), null) };
        paths["/api/v1/queue/shuffle"] = new JObject { ["post"] = Op("Shuffle the play queue", "Queue", null, null) };

        paths["/api/v1/library/songs"] = new JObject { ["get"] = Op("Page through library songs", "Library", Params(("page", "query", "integer", "Page"), ("pageSize", "query", "integer", "Page size"), ("sortBy", "query", "string", "Sort field"), ("descending", "query", "boolean", "Descending")), null) };
        paths["/api/v1/library/stats"] = new JObject { ["get"] = Op("Library totals and duplicates", "Library", null, null) };
        paths["/api/v1/library/status"] = new JObject { ["get"] = Op("Library scan status and folders", "Library", null, null) };
        paths["/api/v1/library/scan"] = new JObject
        {
            ["post"] = Op("Start a library scan", "Library", null, null),
            ["delete"] = Op("Cancel a library scan", "Library", null, null)
        };
        paths["/api/v1/library/home"] = new JObject { ["get"] = Op("Home feed: playlists, recommendations and history", "Library", Params(("count", "query", "integer", "Items per row")), null) };

        paths["/api/v1/playlists"] = new JObject
        {
            ["get"] = Op("List playlists", "Playlists", null, null),
            ["post"] = Op("Create a playlist (icon is a Font Awesome class such as fa-list)", "Playlists", null, Body(("name", "string"), ("icon", "string"), ("emoji", "string")))
        };
        paths["/api/v1/playlists/{playlistId}"] = new JObject
        {
            ["get"] = Op("Read one playlist", "Playlists", PathParams(("playlistId", "Playlist id")), null),
            ["patch"] = Op("Rename a playlist or change its icon", "Playlists", PathParams(("playlistId", "Playlist id")), Body(("name", "string"), ("icon", "string"), ("emoji", "string"))),
            ["delete"] = Op("Delete a playlist", "Playlists", PathParams(("playlistId", "Playlist id")), null)
        };
        paths["/api/v1/playlists/{playlistId}/songs"] = new JObject
        {
            ["post"] = Op("Add a library song to a playlist", "Playlists", PathParams(("playlistId", "Playlist id")), Body(("songId", "string")))
        };

        paths["/api/v1/history"] = new JObject { ["get"] = Op("Local play history", "History", Params(("count", "query", "integer", "Maximum entries")), null) };
        paths["/api/v1/history/{entryId}"] = new JObject { ["delete"] = Op("Remove one history entry", "History", PathParams(("entryId", "History entry id")), null) };
        paths["/api/v1/played-songs"] = new JObject
        {
            ["get"] = Op("Server played-songs history", "History", null, null),
            ["delete"] = Op("Clear the server played-songs history", "History", null, null)
        };

        paths["/api/v1/youtube/search"] = new JObject { ["get"] = Op("Search YouTube", "Songs", Params(("q", "query", "string", "Query")), null) };
        paths["/api/v1/lyrics"] = new JObject { ["get"] = Op("Lyrics for a video", "Songs", Params(("videoId", "query", "string", "YouTube id"), ("videoUrl", "query", "string", "Optional URL")), null) };

        paths["/api/v1/settings"] = new JObject
        {
            ["get"] = Op("Read the full configuration", "Settings", null, null),
            ["put"] = Op("Update configuration values", "Settings", null, new JObject { ["type"] = "object", ["additionalProperties"] = true }),
            ["patch"] = Op("Update configuration values", "Settings", null, new JObject { ["type"] = "object", ["additionalProperties"] = true })
        };

        paths["/api/v1/soundboard"] = new JObject { ["get"] = Op("Soundboard state", "Settings", null, null) };
        paths["/api/v1/soundboard/{action}"] = new JObject { ["post"] = Op("Soundboard action (play, stop-all, set-page, ...)", "Settings", PathParams(("action", "Action name")), new JObject { ["type"] = "object", ["additionalProperties"] = true }) };
        paths["/api/v1/announcement"] = new JObject { ["get"] = Op("Announcement state", "Settings", null, null) };
        paths["/api/v1/announcement/{action}"] = new JObject { ["post"] = Op("Announcement action", "Settings", PathParams(("action", "Action name")), new JObject { ["type"] = "object", ["additionalProperties"] = true }) };
        paths["/api/v1/musicshare"] = new JObject { ["get"] = Op("Music Share status", "Settings", null, null) };
        paths["/api/v1/musicshare/{action}"] = new JObject { ["post"] = Op("Music Share action", "Settings", PathParams(("action", "Action name")), new JObject { ["type"] = "object", ["additionalProperties"] = true }) };

        paths["/api/v1/commands"] = new JObject
        {
            ["get"] = Op("Command catalogue", "Integration", null, null),
            ["post"] = Op("Execute a command from the catalogue", "Integration", null, Ref("CommandRequest"))
        };
        paths["/api/v1/commands/{command}"] = new JObject
        {
            ["post"] = Op("Execute any command by name", "Integration", PathParams(("command", "Command name")), new JObject { ["type"] = "object", ["additionalProperties"] = true })
        };

        return paths;
    }

    private static JObject Op(string summary, string tag, JArray? parameters, JObject? requestBody, bool auth = true)
    {
        var operation = new JObject
        {
            ["summary"] = summary,
            ["tags"] = new JArray(tag),
            ["responses"] = new JObject
            {
                ["200"] = new JObject
                {
                    ["description"] = "Success",
                    ["content"] = new JObject
                    {
                        ["application/json"] = new JObject
                        {
                            ["schema"] = new JObject { ["type"] = "object", ["additionalProperties"] = true }
                        }
                    }
                },
                ["400"] = ErrorResponse("Bad request"),
                ["401"] = ErrorResponse("Unauthorized")
            }
        };
        if (!auth)
            operation["security"] = new JArray(new JObject());

        if (parameters != null) operation["parameters"] = parameters;
        if (requestBody != null)
        {
            operation["requestBody"] = new JObject
            {
                ["required"] = true,
                ["content"] = new JObject
                {
                    ["application/json"] = new JObject { ["schema"] = requestBody }
                }
            };
        }
        return operation;
    }

    private static JObject ErrorResponse(string description) => new()
    {
        ["description"] = description,
        ["content"] = new JObject
        {
            ["application/json"] = new JObject
            {
                ["schema"] = Ref("Error")
            }
        }
    };

    private static JObject Ref(string schema) => new() { ["$ref"] = "#/components/schemas/" + schema };

    private static JArray Params(params (string Name, string In, string Type, string Description)[] parameters)
    {
        var array = new JArray();
        foreach (var p in parameters)
        {
            array.Add(new JObject
            {
                ["name"] = p.Name,
                ["in"] = p.In,
                ["required"] = false,
                ["schema"] = new JObject { ["type"] = p.Type },
                ["description"] = p.Description
            });
        }
        return array;
    }

    private static JArray PathParams(params (string Name, string Description)[] parameters)
    {
        var array = new JArray();
        foreach (var p in parameters)
        {
            array.Add(new JObject
            {
                ["name"] = p.Name,
                ["in"] = "path",
                ["required"] = true,
                ["schema"] = new JObject { ["type"] = "string" },
                ["description"] = p.Description
            });
        }
        return array;
    }

    private static JObject Body(params (string Name, string Type)[] properties)
    {
        var props = new JObject();
        foreach (var property in properties)
            props[property.Name] = new JObject { ["type"] = property.Type };
        return new JObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = true
        };
    }

    private static JObject Tag(string name) => new() { ["name"] = name };
}
