# Local API and MCP server

SongRequest can expose its full feature set to other programs on the same
machine (or, deliberately, on the LAN):

- an **HTTP API** with a **bidirectional WebSocket** for live updates, and
- an **MCP server** (Model Context Protocol over Streamable HTTP) for AI
  assistants and MCP clients.

Both servers are **off by default**. Both bind to `127.0.0.1` by default. Both
can require a bearer token. They share the same command dispatcher as the web
UI, so every command, status and setting the app itself uses is available
through them.

| Platform | Where to configure |
| --- | --- |
| Desktop (Windows, Linux, macOS) | **Settings → API & MCP** |
| Mobile (Android/iOS) | **Settings → API & MCP** (same panel); the music-mode settings sheet also has the server switches, addresses, ports and tokens |

Changes apply immediately; no restart is needed. The panel also shows whether
each server is running, its effective address/port, connected WebSocket clients
and the last bind error (for example a port already in use).

## Configuration

The settings are stored in `config.json` under the app data folder:

| Key | Default | Meaning |
| --- | --- | --- |
| `ApiEnabled` | `false` | Start the HTTP/WebSocket API. |
| `ApiBindAddress` | `127.0.0.1` | Interface to bind. `0.0.0.0` exposes it to the LAN. |
| `ApiPort` | `8730` | TCP port of the API. |
| `ApiToken` | empty | Optional bearer token. Empty disables authentication. |
| `McpEnabled` | `false` | Start the MCP server. |
| `McpBindAddress` | `127.0.0.1` | Interface to bind. |
| `McpPort` | `8731` | TCP port of the MCP server. |
| `McpToken` | empty | Optional MCP bearer token. Empty disables authentication. |

> **Security:** there is no TLS in the built-in server. Keep the bind address on
> `127.0.0.1` unless you need LAN access. If you expose a server, always set a
> token and use a firewall or a TLS-terminating reverse proxy. The token is
> compared in constant time, but it is stored in `config.json` like the app's
> companion-server token.

## Base URLs

With the defaults:

- API: `http://127.0.0.1:8730`
- API WebSocket: `ws://127.0.0.1:8730/api/v1/ws`
- MCP: `http://127.0.0.1:8731/mcp`
- OpenAPI: `http://127.0.0.1:8730/openapi.json`
- Health: `http://127.0.0.1:8730/health`
- Capabilities: `http://127.0.0.1:8730/api/v1/capabilities`

`/health` is the only route that never requires the token; it returns the app
version and `status: "ok"`.

## Authentication

When a token is configured, send it in one of these forms:

```http
Authorization: Bearer <token>
```

```http
X-Api-Token: <token>
```

Or, for WebSocket clients that cannot set headers:

```text
ws://127.0.0.1:8730/api/v1/ws?token=<token>
```

The API and MCP servers use **separate** tokens (`ApiToken` and `McpToken`).

## HTTP API

All responses are JSON. Successful command results are returned as-is; failures
use a non-2xx status and a body such as:

```json
{ "error": "Invalid JSON body", "message": "..." }
```

### Status and discovery

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/` | Short index of the API. |
| `GET` | `/health` | Liveness probe (no auth). |
| `GET` | `/openapi.json` | Machine-readable OpenAPI 3.0 document. |
| `GET` | `/api/v1/status` | App version, platform, operating mode, player, queue, library, servers and selected settings. |
| `GET` | `/api/v1/capabilities` | Every command with category, description, parameters and the WebSocket event list. |
| `GET` | `/api/v1/version` | App version. |

### Songs, send-in list and library

These are the primary read endpoints for dashboards, request bots and MCP
clients. Both sources return a common metadata shape.

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/api/v1/songs` | Search send-in-list and/or library songs. |
| `GET` | `/api/v1/search` | Alias for `/songs` across both sources. |
| `GET` | `/api/v1/songs/{source}/{id}` | One song (`source` = `sendinlist` or `library`). |
| `GET` | `/api/v1/requests` | Send-in list with approval state and submit messages. |
| `GET` | `/api/v1/blacklist` | Server blacklist with cached metadata. |
| `GET` | `/api/v1/bad-words` | Blocked words. |
| `POST` | `/api/v1/bad-words` | Add a blocked word (`{ "word": "..." }`). |
| `DELETE` | `/api/v1/bad-words` | Remove a blocked word (`{ "word": "..." }`). |
| `GET` | `/api/v1/sendin` | Whether the companion server accepts new requests. |
| `POST` | `/api/v1/sendin` | Toggle the send-in permission. |
| `GET` | `/api/v1/library/songs` | Page through library songs (`page`, `pageSize`, `sortBy`, `descending`, `filterMissing`). |
| `GET` | `/api/v1/library/stats` | Totals, missing files and duplicates. |
| `GET` | `/api/v1/library/status` | Scan folders, extensions and last scan. |
| `POST` | `/api/v1/library/scan` | Start a library scan. |
| `DELETE` | `/api/v1/library/scan` | Cancel a running scan. |
| `GET` | `/api/v1/library/home` | Home feed: playlists, recommendations, recently played, new additions. |
| `GET` | `/api/v1/youtube/search?q=` | YouTube search. |
| `GET` | `/api/v1/lyrics?videoId=` | Lyrics for a video. |

`GET /api/v1/songs` query parameters:

| Parameter | Default | Meaning |
| --- | --- | --- |
| `q` | empty | Free-text search over title, artist, album, genre, submit message and id. |
| `source` | `all` | `all`, `sendinlist` or `library`. |
| `limit` | `100` | 1–1000. |
| `offset` | `0` | Paging offset. |
| `refresh` | `false` | Bypass the 5-second send-in cache. |
| `artwork` | `false` | Include library artwork as data URLs (can be large). |

The send-in list is fetched from the companion server and enriched with the
app's cached YouTube metadata (title, artist, duration). If metadata is not
cached yet, the API fills up to 15 missing entries per request (bounded
concurrency and an 8-second limit) so a first call is still useful. If the
companion server is temporarily unreachable, the last successful snapshot is
returned with a `warning` field.

#### Song object

```json
{
  "id": "dQw4w9WgXcQ",
  "source": "sendinlist",
  "ytid": "dQw4w9WgXcQ",
  "title": "Rick Astley - Never Gonna Give You Up",
  "artist": "Rick Astley",
  "album": "",
  "genre": "",
  "duration": 213,
  "durationDisplay": "03:33",
  "message": "Please play this!",
  "isApproved": true,
  "sentInUtc": "2026-10-20T18:12:00Z",
  "thumbnailUrl": "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg",
  "metadataCached": true
}
```

Library songs additionally include `songId`, `youTubeId`, `tags`, `filePath`,
`thumbnailPath`, `sourceUrl`, `songSource`, `fileStatus`, `isMissing`,
`playCount`, `dateAddedUtc`, `lastPlayedUtc`, `fileSizeBytes`, `bitrateKbps`
and (with `artwork=true`) an `artwork` data URL.

### Playback and queue

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/api/v1/player` | Current track, position, volume, crossfade, repeat, output device, queue counts, audio quality (`fileType`, `bitrateKbps`, `lossless`, `quality`) and lyrics status (`lyricsProvider`, `lyricsAvailable`). |
| `GET` | `/api/v1/queue` | Play queue and playlist queue. |
| `POST` | `/api/v1/player/play-pause` | Toggle playback. |
| `POST` | `/api/v1/player/next` | Next track. |
| `POST` | `/api/v1/player/previous` | Previous track. |
| `POST` | `/api/v1/player/stop` | Stop. |
| `POST` | `/api/v1/player/repeat` | Toggle repeat. |
| `POST` | `/api/v1/player/seek` | `{ "position": 0.5 }` (normalized). |
| `POST` | `/api/v1/player/volume` | `{ "value": 0.8 }` (0–1). |
| `POST` | `/api/v1/player/crossfade` | `{ "value": 4 }` seconds. |
| `POST` | `/api/v1/queue` | Queue by `ytid`, `songId` or `videoUrl`. |
| `POST` | `/api/v1/queue/shuffle` | Shuffle the queue. |
| `DELETE` | `/api/v1/queue` | Clear the queue. |
| `DELETE` | `/api/v1/queue/{index}` | Remove one queue item. |
| `GET` | `/api/v1/devices/audio` | List output devices. |
| `POST` | `/api/v1/devices/audio` | `{ "deviceNumber": -1 }` (`-1` = system default). |

The player's `quality` object has `format`, `bitrateKbps`, `lossless`, `text` and
`tier` (`is-lossless`, `is-high`, `is-good`, `is-low` or `is-unknown`), matching
the badges in the music player UI.

### Advanced audio: equalizer, sleep timer and analysis

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/api/v1/equalizer` | 10-band curve (`enabled`, `preampDb`, `preset`, `bands`) plus all factory/user `presets`. |
| `PUT` | `/api/v1/equalizer` | Apply `enabled`, `preampDb`, `preset` and/or a `gains` array in one call. |
| `GET` | `/api/v1/equalizer/presets` | List presets (same payload as `getEqualizer`). |
| `POST` | `/api/v1/equalizer/presets` | Save the current curve as `{ "name": "..." }`. |
| `DELETE` | `/api/v1/equalizer/presets/{name}` | Delete a user preset. |
| `POST` | `/api/v1/equalizer/reset` | Reset the curve and disable the equalizer. |
| `GET` | `/api/v1/sleep-timer` | State: `isActive`, `phase`, `remainingSeconds`, `fadeOutSeconds`, `playLastSongToEnd`. |
| `POST` | `/api/v1/sleep-timer` | Start: `{ "minutes": 30, "fadeOutSeconds": 10, "playLastSongToEnd": true }`. |
| `DELETE` | `/api/v1/sleep-timer` | Cancel and restore full volume. |
| `POST` | `/api/v1/sleep-timer/extend` | `{ "minutes": 5 }`. |
| `POST` | `/api/v1/sleep-timer/options` | Change `fadeOutSeconds` / `playLastSongToEnd` while running. |
| `GET` | `/api/v1/analysis?songId=...` | Cached BPM/loudness/quality/drop analysis (or `?songPath=...`). |
| `POST` | `/api/v1/analysis` | Run analysis for `{ "songId": "..." }` or `{ "songPath": "..." }`; pass `force: true` to re-analyse. |
| `GET` | `/api/v1/analysis/cache` | All cached analyses. |
| `GET` | `/api/v1/analysis/{key}` | One cached analysis by the `key` returned from a POST. |

The analysis result contains `bpm`, `bpmConfidence`, `beatOffsetSeconds`,
`loudnessLufs`, `loudnessRangeLu`, `peakDb`, `dynamicRangeDb`, a `quality` object
(`score`, `tier`, codec, bitrate, sample rate, clipping, dynamic range), detected
`drops` (time, confidence, type/label), a per-window `timeline` (RMS/peak/low/mid/
high energy and novelty) and the decoder `analysisPath`. Analysis runs in the
background; progress arrives as the `songAnalysisProgress` WebSocket event, and
a finished run emits `songAnalysisUpdate`.

### Playlists and history

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/api/v1/playlists` | List manual and dynamic playlists. |
| `POST` | `/api/v1/playlists` | Create (`{ "name": "...", "icon": "fa-list" }`; `emoji` remains accepted for compatibility). |
| `GET` | `/api/v1/playlists/{id}` | Read one playlist (includes its `icon` Font Awesome class). |
| `PATCH` | `/api/v1/playlists/{id}` | Rename or change the icon (`{ "name": "...", "icon": "fa-fire" }`). |
| `DELETE` | `/api/v1/playlists/{id}` | Delete a playlist. |
| `POST` | `/api/v1/playlists/{id}/songs` | Add `{ "songId": "..." }`. |
| `DELETE` | `/api/v1/playlists/{id}/entries/{entryId}` | Remove an entry. |
| `GET` | `/api/v1/history` | Local play history. |
| `DELETE` | `/api/v1/history/{entryId}` | Remove one history entry. |
| `GET` | `/api/v1/played-songs` | Server played-songs history. |
| `DELETE` | `/api/v1/played-songs` | Clear the server history. |

### Settings, soundboard, announcements and Music Share

| Method | Path | Description |
| --- | --- | --- |
| `GET` | `/api/v1/settings` | Full configuration (same shape as `getSettings`). |
| `PUT`/`PATCH` | `/api/v1/settings` | Update settings. Send either the settings object directly or `{ "settings": { ... } }`. |
| `GET` | `/api/v1/soundboard` | Soundboard state (pages, buttons, volume, MIDI). |
| `POST` | `/api/v1/soundboard/{action}` | Action, e.g. `play`, `stop-all`, `set-page`, `set-master-volume`, `add-page`. |
| `GET` | `/api/v1/announcement` | Announcement state. |
| `POST` | `/api/v1/announcement/{action}` | `start`, `stop`, `press`, `release`, `set-dim-db`, `set-play-sound`, `set-push-to-talk`. |
| `GET` | `/api/v1/musicshare` | Music Share status. |
| `POST` | `/api/v1/musicshare/{action}` | `start`, `stop`, `connect`, `disconnect`, `set-volume`. |

### Run any command

Everything the app can do is a command. The catalogue is available at
`GET /api/v1/capabilities`, and any command can be invoked generically:

```http
POST /api/v1/commands/getLibrarySongs
Content-Type: application/json
Authorization: Bearer <token>

{ "page": 1, "pageSize": 25, "sortBy": "title" }
```

or

```http
POST /api/v1/commands
Content-Type: application/json

{ "command": "setVolume", "arguments": { "value": 0.7 } }
```

This is the escape hatch for features that do not have a dedicated route
(keybinds, MIDI capture, tool downloads, soundboard MIDI learn, update checks,
and so on). Commands marked `"dangerous": true` in the catalogue change or
delete data, quit the app, install updates or open interactive pickers.

On the mobile host, the stage commands `stageStatus`, `stageStart` and
`stageStop` are also in the catalogue; they control the LAN presentation server
and the HDMI mirror. On desktop they return "unknown method" because there is no
stage host there.

### Examples

```bash
# Status
curl -H "Authorization: Bearer secret" http://127.0.0.1:8730/api/v1/status

# Search both sources for "daft punk"
curl -H "Authorization: Bearer secret" \
  "http://127.0.0.1:8730/api/v1/songs?q=daft%20punk&source=all&limit=20"

# Approve a request
curl -X POST -H "Authorization: Bearer secret" \
  http://127.0.0.1:8730/api/v1/commands/approve \
  -H "Content-Type: application/json" -d '{"ytid":"dQw4w9WgXcQ"}'

# Queue a library song
curl -X POST -H "Authorization: Bearer secret" \
  http://127.0.0.1:8730/api/v1/queue \
  -H "Content-Type: application/json" -d '{"songId":"<library-id>"}'
```

## WebSocket API

Connect to `ws://<host>:<port>/api/v1/ws` (add `?token=<token>` when needed).
The socket is bidirectional:

- the client sends JSON commands and subscriptions,
- the server sends responses, live events and a `hello` snapshot.

### Client → server

```json
{ "type": "request", "id": "1", "method": "getLibrarySongs", "params": { "page": 1, "pageSize": 25 } }
```

```json
{ "type": "command", "id": "2", "method": "playPause" }
```

```json
{ "type": "subscribe", "events": ["requests.updated", "queue.updated"] }
```

```json
{ "type": "unsubscribe", "events": ["player.progress"] }
```

```json
{ "type": "status" }
```

```json
{ "type": "ping" }
```

Without a `subscribe`, every event is delivered. Sending
`{ "type": "subscribe", "events": ["*"] }` restores the default.

### Server → client

```json
{ "type": "hello", "version": "6.6", "protocol": 1, "status": { "...": "..." } }
```

```json
{ "type": "response", "id": "1", "result": { "...": "..." } }
```

```json
{
  "type": "event",
  "event": "requests.updated",
  "eventName": "requests.updated",
  "data": { "atUtc": "2026-10-20T18:12:00Z" },
  "sentUtc": "2026-10-20T18:12:00Z"
}
```

```json
{ "type": "pong", "utcNow": "2026-10-20T18:12:00Z" }
```

Live events include:

| Event | Meaning |
| --- | --- |
| `requests.new` | A fetch found new send-in requests. Payload: `count` and `requests` (`ytid`, `sentInUtc`, `message`, `isApproved`). |
| `requests.updated` | A fetch of the send-in list completed. Re-read `/api/v1/requests` or send a command. |
| `queue.updated` | Queue or playlist queue changed. Payload contains both queues. |
| `player.progress` | Playback progress (throttled to one per second), including audio-quality and lyrics metadata. |
| `lyricsUpdate` | Lyrics for the current track were resolved, including the provider (`Embedded`, `LRC File`, `LRCLIB` or `YouTube captions`). |
| `nowPlayingUpdate` | The track changed (forwarded from the app engine). |
| `queueUpdate` | Raw queue update forwarded from the web UI bridge. |
| `downloadProgress` | Request download progress (`videoId`, `stage`, `progress`). |
| `libraryScanProgress` / `libraryScanCompleted` | Library scanning. |
| `libraryChanged` | The library database changed. |
| `midiActivity` | MIDI input activity. |
| `soundboardPlayback`, `soundboardImportProgress`, `soundboardMidiCaptured`, `soundboardMidiError` | Soundboard events. |
| `announcementState` | Announcement state changes. |
| `musicShareStatus`, `musicShareMetadataReceived`, `musicShareError` | Music Share events. |
| `presentationUpdate`, `presentationQueueUpdate`, `presentationSettings` | Presentation updates. |
| `sleepTimerUpdate` | Sleep-timer countdown, fade and finishing-track state. |
| `equalizerUpdate` | Equalizer enabled flag, pre-amp or band curve changed. |
| `songAnalysisProgress` / `songAnalysisUpdate` | Background analysis progress and the finished BPM/loudness/drop result. |
| `updateProgress`, `toolProgress` | Self-update and tool-download progress. |

Example browser client:

```js
const ws = new WebSocket("ws://127.0.0.1:8730/api/v1/ws?token=secret");
ws.onmessage = event => {
  const message = JSON.parse(event.data);
  if (message.type === "event" && message.event === "requests.updated") {
    fetch("http://127.0.0.1:8730/api/v1/requests", {
      headers: { Authorization: "Bearer secret" }
    }).then(r => r.json()).then(data => console.log(data.requests));
  }
};
ws.onopen = () => ws.send(JSON.stringify({
  type: "request", id: "1", method: "getVersion"
}));
```

## MCP server

The MCP server speaks the **Streamable HTTP** transport (JSON-RPC 2.0 over
`POST /mcp`). Authentication uses the MCP token from Settings.

### Client configuration

Most desktop MCP clients expect a command instead of a URL. `mcp-remote` bridges
Streamable HTTP to stdio:

```json
{
  "mcpServers": {
    "songrequest": {
      "command": "npx",
      "args": [
        "-y",
        "mcp-remote",
        "http://127.0.0.1:8731/mcp",
        "--header",
        "Authorization: Bearer ${SONGREQUEST_MCP_TOKEN}"
      ],
      "env": {
        "SONGREQUEST_MCP_TOKEN": "your-token"
      }
    }
  }
}
```

Clients with native HTTP/Streamable-HTTP support can point directly at
`http://127.0.0.1:8731/mcp` and send the same `Authorization` header.

### Tools

The server publishes:

- `get_status` – version, servers, player, queue and library totals.
- `get_capabilities` – every command with parameters and events.
- `search_songs` – search `sendinlist`, `library` or `all`, with metadata.
- `get_song` – one song by source and id.
- `list_requests`, `approve_request`, `unapprove_request`, `delete_request`,
  `blacklist_request`, `unblacklist_request`, `refresh_requests`.
- `get_player`, `play_pause`, `next`, `previous`, `stop_playback`, `seek`,
  `set_volume`, `set_crossfade`, `toggle_repeat`, `shuffle_queue`, `clear_queue`,
  `queue_song`, `play_next`.
- `list_playlists`, `get_playlist`, `create_playlist`, `update_playlist`,
  `play_playlist`, `queue_playlist`, `delete_playlist`, `get_library_songs`,
  `get_library_stats`, `scan_library`, `get_play_history`, `get_played_songs`.
- `search_youtube`, `get_lyrics`.
- `get_settings`, `update_settings`.
- `soundboard_get_state`, `soundboard_play`, `soundboard_stop_all`.
- `announcement_get_state`, `announcement_start`, `announcement_stop`.
- `music_share_status`, `music_share_start`, `music_share_stop`.
- `run_songrequest_command` – any command from the catalogue, with an
  `arguments` object. This is how every remaining feature (MIDI, keybinds,
  updates, tool downloads, presentation, …) is reached.

Tool calls return MCP text content containing pretty-printed JSON, plus
`structuredContent` for object results and `isError` when the command failed.

### Resources

| URI | Content |
| --- | --- |
| `songrequest://status` | Application/server status. |
| `songrequest://capabilities` | Command catalogue and events. |
| `songrequest://player` | Current player state. |
| `songrequest://queue` | Both queues. |
| `songrequest://requests` | Send-in list with metadata. |
| `songrequest://library` | Library songs with metadata. |
| `songrequest://library/stats` | Library statistics. |
| `songrequest://playlists` | Playlists. |
| `songrequest://history` | Play history. |
| `songrequest://settings` | Full settings. |
| `songrequest://soundboard` | Soundboard state. |
| `songrequest://announcement` | Announcement state. |
| `songrequest://musicshare` | Music Share status. |
| `songrequest://songs/{source}/{id}` | One song by source and id. |

The MCP server does not expose a server-to-client SSE stream; live updates are
available through the API WebSocket. `GET /mcp` therefore returns `405 Method
Not Allowed` (as allowed by the Streamable HTTP specification), while
`POST /mcp` is the message endpoint.

### Example MCP exchange

```json
{ "jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {} }
```

```json
{ "jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {} }
```

```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "tools/call",
  "params": {
    "name": "search_songs",
    "arguments": { "query": "daft punk", "source": "all", "limit": 10 }
  }
}
```

```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "method": "tools/call",
  "params": {
    "name": "run_songrequest_command",
    "arguments": {
      "command": "soundboardPlay",
      "arguments": { "row": 0, "col": 1 }
    }
  }
}
```

## Machine-readable documentation

| Artefact | Location |
| --- | --- |
| OpenAPI 3.0 JSON | `GET /openapi.json` (also `docs/openapi.json` in the repository) |
| Command catalogue | `GET /api/v1/capabilities` |
| MCP tool schemas | MCP `tools/list` (or `GET /mcp/tools` while the server is enabled) |
| MCP resources | MCP `resources/list` / `resources/templates/list` |
| MCP metadata | `GET /mcp/info` |

## Troubleshooting

- **“Enabled but not running”** – the port is in use or the bind address is not
  available. Check the error shown in Settings; change the port or address.
- **Mobile/LAN access** – set the bind address to `0.0.0.0`, set a token, and
  allow the port through the device firewall. Android needs the normal
  `INTERNET` permission (already present).
- **401 Unauthorized** – the configured token is non-empty and the request did
  not send it. Use `Authorization: Bearer <token>`; WebSocket clients can use
  `?token=<token>`.
- **No live request events** – the app fetches the send-in list when the UI
  refreshes it. The API can also trigger a refresh with
  `POST /api/v1/commands/fetchData`, which emits `requests.updated`.
- **CORS** – browser requests are allowed from any origin because the API is
  intended for local tools. The token is still required when configured.
