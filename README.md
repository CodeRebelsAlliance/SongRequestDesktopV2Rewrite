# SongRequestDesktopV2Rewrite

[![Status](https://img.shields.io/badge/status-stable-brightgreen)](https://github.com/CodeRebelsAlliance/SongRequestDesktopV2Rewrite/releases/latest)
[![Latest release](https://img.shields.io/github/v/release/CodeRebelsAlliance/SongRequestDesktopV2Rewrite?label=latest&sort=semver&color=blue)](https://github.com/CodeRebelsAlliance/SongRequestDesktopV2Rewrite/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/CodeRebelsAlliance/SongRequestDesktopV2Rewrite/total?label=downloads)](https://github.com/CodeRebelsAlliance/SongRequestDesktopV2Rewrite/releases)

> The **latest** badge above is read from the newest GitHub release, so it always
> matches the released build — no need to edit this file when releasing.
> The release tag comes from `<Version>` in
> [`Directory.Build.props`](Directory.Build.props), which is also the version the
> app shows and updates from.

![avast](https://i.ibb.co/pr2hn5z/Avast-Safe2.png) *
# Desktop Application for managing Song Requests

> [!IMPORTANT]
> The program will not work without the companion web server available under [this github repository](https://github.com/CodeRebelsAlliance/NewSongRequests). The server needs to be reachable in the network this desktop application runs in.

## Installation

1. Download the archive for your platform from the Releases page
   (`SongRequest-win-x64.zip`, `SongRequest-linux-x64.tar.gz`,
   `SongRequest-osx-arm64.tar.gz`, …)
2. Extract the archive
3. Run `SongRequest V2.exe` (Windows) or `./SongRequest V2` (Linux/macOS)

### Windows: install / uninstall

On Windows the app registers itself (per user, **no administrator rights**) in
**Settings → Apps → Installed apps** as **SongRequest Desktop** by
**CodeRebelsAlliance UG**, with the current version. It re-registers on every
launch, so the listed version stays correct after an in-app update.

Uninstall from Windows Settings, or by running
`%LOCALAPPDATA%\SongRequestDesktopV2Rewrite\uninstaller\uninstall.cmd`. The
uninstaller removes the app from Settings, removes the autostart entry, and asks
whether to also delete your settings/library/downloads.

Releases are tagged from the app's `<Version>`, so the version in Settings, in the
About dialog and in the update check always agree. A release is published automatically
when a push to `master`/`main` bumps `<Version>` **and** the `Tests` job passes; a manual
run of the *Build and Release* workflow publishes the current version as well. An
already-tagged version is never re-released by a push — bump `<Version>` to ship again.

### Prerequisites

SongRequest needs **FFmpeg** (with `ffprobe`) for audio playback and metadata.
Put `ffmpeg`/`ffprobe` in the bundled `tools/` folder, or have them on your
`PATH`. `yt-dlp` is optional and only used to import YouTube cookies from a
browser. See
[`SongRequestDesktopV2Rewrite/tools/README.md`](SongRequestDesktopV2Rewrite/tools/README.md).

## Troubleshooting
- **Error on the bottom right of the screen**
  
  Make sure you have an active internet connection and that your host is configured and running. You can open the settings of the Application and adjust the host URL

- **Error 401: Unauthorized**
  
  Double-Check that you have authenticated in the Application Settings

- **No Lyrics available for this video**
  
  There is nothing you can do as the song does not have YT Subtitles and is not registered in the LyricsOVH API

- **Error on Download**
  
  Either the URL was invalid, or it is other Content that is hosted as a livestream or community post, or you have reached a rate limit

- **Songs from requests are not downloaded**

  Automatic downloads only start once you have finished the startup step — importing
  YouTube cookies **or** choosing *Skip* — or when cookies are already saved, and the
  app remembers what it has not fetched yet, so anything skipped is picked up again on
  the next refresh. Without cookies YouTube rejects age-restricted and
  "confirm you're not a bot" videos with a *video unavailable* error, which is why the
  app waits for your decision instead of hammering YouTube first.

- **A sub-window (Visualization / Soundboard / Music Player …) does not close**

  Fixed: closing a sub-window now hides it (and reopening shows it again instantly)
  instead of tearing the web view down, which used to leave the window on screen with
  the whole UI no longer reacting.

- **The app does not start any more, or no window appears**

  If a previous start died before the UI came up, the app protects itself: two
  unfinished starts in a row switch on a conservative webview setup, and if the crash
  happened while the tray icon was being created the tray is left off — the next start
  works again. Re-enable the tray with **Settings → Startup & Window** (changing either
  tray option clears the guard).

- **The window closed but the app is still running**

  With **Settings → Startup & Window → Close to tray** enabled, clicking the window's
  X hides the app to the tray instead of quitting (that is what the option is for).
  To quit for real use the **Quit** button in the top bar of the main page — the same
  button is available on the loading/launch/authentication screens — or the tray
  menu's **Quit**, or turn *Close to tray* off.

  The Quit button releases the tray icon, the OS "now playing" entry and the MIDI
  devices before exiting, so nothing is left behind.

- **Linux: the window stays black/tiny, or the app exits immediately with
  `Gdk-Message: … Error 71 (Protocol error) dispatching to Wayland display.`**
  
  This is a WebKitGTK 2.4x issue with its DMA-BUF renderer on some
  driver/compositor combinations (most often the **NVIDIA** driver, also some
  virtual GPUs) — it happens inside the native webview, so the app cannot catch
  it. The console usually also shows `Failed to create GBM buffer of size …:
  Invalid argument`.

  SongRequest applies the known-good mitigations automatically before the webview
  is created (`WEBKIT_DISABLE_DMABUF_RENDERER=1`, plus
  `__NV_DISABLE_EXPLICIT_SYNC=1` on NVIDIA). If you still hit it, use these:

  | Command / variable | Effect |
  | --- | --- |
  | `./SongRequest\ V2 --safe-mode` (or `SONGREQUEST_SAFE_MODE=1`) | no DMA-BUF, no GPU compositing, X11 backend |
  | `./SongRequest\ V2 --x11` / `--wayland` | force the GTK backend |
  | `./SongRequest\ V2 --gpu` (or `SONGREQUEST_WEBKIT_DMABUF=1`) | keep the hardware-accelerated path |

  If two starts in a row are interrupted before the UI loads, the app switches to
  safe mode by itself and remembers it (so you don't crash on every other launch);
  `--gpu` clears that again. The chosen settings are printed on startup:
  `[SongRequest] webview environment: applied […]`.

  Anything you export yourself always wins — the app never overrides a variable
  that is already set.

- **Linux: the window has no icon in the dock/task bar**
  Wayland has no per-window icon protocol: the compositor resolves the icon from a
  `.desktop` file whose id matches the window's app id. The app therefore sets its
  app id to `songrequest` and installs
  `~/.local/share/applications/songrequest.desktop` (with `Icon=` pointing at the
  bundled `SRappIcon.png`) on every start; on X11 the GTK window icon is set too.
  Delete that file if you don't want the app menu entry.

## Libraries and Dependencies

- .NET Runtime is **not** required separately — the self-contained release includes it
- YouTube Explode: downloader and YouTube API library (included)
- Newtonsoft.Json: JSON library (included)
- Photino.NET: cross-platform native webview host for the UI (included)
- NAudio.Core: portable audio sample graph (included)
- OpenAL Soft (`Silk.NET.OpenAL`): audio output (included)
- RtMidi.Core: cross-platform MIDI input/output (included)
- TagLibSharp: audio tag / metadata (included)
- FFmpeg / FFprobe: **external, not bundled by default** — see Prerequisites
- yt-dlp: **external, optional** — used only for YouTube cookie import

*SR Desktop has been scanned by Avast Premium Antivirus many times during development

## Documentation

- [`tests/README.md`](tests/README.md) — how to run the test suite, conventions, coverage ratchet and branch protection
- [`docs/TESTING.md`](docs/TESTING.md) — the full test plan / feature matrix
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — the layering: `SongRequest.Core`, the desktop/mobile hosts, and the platform provider seams
- [`docs/API-AND-MCP.md`](docs/API-AND-MCP.md) — the optional local HTTP/WebSocket API and MCP server (off by default): settings, endpoints, song metadata, live events and client examples
- [`docs/openapi.json`](docs/openapi.json) — machine-readable OpenAPI 3.0 document for the local API (also served at `/openapi.json`)
- [`docs/MOBILE-APP-PLAN.md`](docs/MOBILE-APP-PLAN.md) — the Android/iOS plan (options, parity matrix, milestones, policy/licensing)
- [`docs/MOBILE-TESTING.md`](docs/MOBILE-TESTING.md) — building the mobile hosts and the per-milestone device checklists
- [`docs/REMOVED-FEATURES.md`](docs/REMOVED-FEATURES.md) — everything removed or changed in the cross-platform migration (tray icon, keyboard remote shortcuts, embedded Google login, auto tool setup, …)
- [`SongRequestDesktopV2Rewrite/tools/README.md`](SongRequestDesktopV2Rewrite/tools/README.md) — FFmpeg / yt-dlp setup

## Mobile (Android & iOS)

The same engine and the same web UI also run as a mobile app
([`SongRequest.Mobile/`](SongRequest.Mobile), .NET MAUI + `HybridWebView`):
the phone hosts the identical HTML/CSS/JS pages, and `SongRequest.Core` is the
shared, platform-neutral engine behind both hosts.

Current state: **M1–M6 done** — the shell with native tabs, the host bridge, the
responsive layout layer, sandbox paths and the Android APK build; **audio
playback** with crossfade/normalisation/ducking, background playback and the
lock-screen/notification media controls; **library parity** (system-picker import
copied into the app sandbox, portable library paths, album artwork without FFmpeg,
playlists, history, home feed, mobile-aware settings); and **live-event parity** —
the soundboard with its own import and fades, **MIDI** controllers over USB and
Bluetooth LE, remote-control mappings, announcements with the chime, and Music
Share; and **downloads** — in-app YouTube sign-in, AAC/m4a downloads with a
background service and progress notification, and offline playback from the app's
own storage; and **the stage** — the visualisation and the presentation page, which
the app also serves over the LAN (with a QR for a TV) and mirrors onto a connected
HDMI display; plus an **in-app updater** on Android (the release APK, downloaded
with progress and handed to the system installer). The iOS host follows in M7; the
plan and the milestone state live in
[`docs/MOBILE-APP-PLAN.md`](docs/MOBILE-APP-PLAN.md), the build/test commands and
device checklists in [`docs/MOBILE-TESTING.md`](docs/MOBILE-TESTING.md).

## Advanced audio

The player (desktop and mobile) shares the same three audio tools:

- **10-band equalizer** with a live graphical curve, pre-amp, factory presets
  (Flat, Rock, Pop, Jazz, Classical, Electronic, Hip-Hop, Acoustic, Vocal, Bass
  Boost, Treble Boost, Loudness) and saved user presets. It is applied in the
  audio pipeline before the output/capture tap, so it also affects the
  visualization and Music Share.
- **Sleep timer** with 15–120 minute shortcuts and custom durations, a
  configurable fade-out, and *play the last song to the end*. On desktop the
  remaining time is shown in the player bar (and the toolbar moon button stays
  highlighted); on mobile the player options start it and an extra pill in the
  player's top bar shows the countdown. Click either to edit/extend/cancel.
- **Song analysis** for BPM (with confidence and beat offset), integrated
  loudness, peak, dynamic range, quality score/codec/bitrate/clipping, an audio
  path/energy timeline, a quality radar and smart drop/build-up detection. It
  also detects **verse, chorus, bridge, intro, outro and pre-chorus chapters**.
  It is available from the player toolbar/options and per song in the context
  menu; every result is also served by the local API.
- **Automatic analysis** (on by default, toggle in Settings on desktop or the
  player Options/settings sheet on mobile) analyses each track as it starts.
  Cached results appear as interactive chapter/drop markers on the expanded
  desktop progress bar and the mobile player seek bar, and the detected BPM is
  shown next to the quality badge. A spinner/progress badge shows while the
  automatic analysis is running.

All three are exposed through the command dispatcher (and therefore the HTTP
API, WebSocket events and MCP) — see
[`docs/API-AND-MCP.md`](docs/API-AND-MCP.md#advanced-audio-equalizer-sleep-timer-and-analysis).

## Local API & MCP (optional, off by default)
Settings → **API & MCP** can start two independent local servers:

- an **HTTP API** (`http://127.0.0.1:8730`) with a bidirectional **WebSocket**
  (`/api/v1/ws`) for live updates such as newly sent-in songs, and
- an **MCP server** (`http://127.0.0.1:8731/mcp`) that exposes every command and
  status as tools and resources for AI assistants.

Both are disabled by default, bind to loopback unless changed, and accept an
optional bearer token. The API can search the send-in list and the library and
returns both with full metadata. Every feature of the app is reachable through
the generic command endpoint; the complete reference, security notes and client
examples are in [`docs/API-AND-MCP.md`](docs/API-AND-MCP.md), with an OpenAPI
document at [`docs/openapi.json`](docs/openapi.json).

## Testing

```bash
./tests/run-tests.sh        # unit + integration + JS + coverage floors + summary
```

Three suites run in CI (`Tests` job) on every push/PR: **unit** (hermetic logic),
**integration** (FFmpeg/OpenAL/library/MIDI), and **JavaScript** (`newui/keys.js`).
Coverage is ratcheted, failures are reported in the job summary, and the check is
meant to be a required branch-protection status. See [`tests/README.md`](tests/README.md).
