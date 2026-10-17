# SongRequestDesktopV2Rewrite

[![Status](https://img.shields.io/badge/status-stable-brightgreen)](https://github.com/CodeRebelsAlliance/SongRequestDesktopV2Rewrite/releases/latest)
[![Latest release](https://img.shields.io/github/v/release/CodeRebelsAlliance/SongRequestDesktopV2Rewrite?label=latest&sort=semver&color=blue)](https://github.com/CodeRebelsAlliance/SongRequestDesktopV2Rewrite/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/CodeRebelsAlliance/SongRequestDesktopV2Rewrite/total?label=downloads)](https://github.com/CodeRebelsAlliance/SongRequestDesktopV2Rewrite/releases)

> The **latest** badge above is read from the newest GitHub release, so it always
> matches the released build — no need to edit this file when releasing.
> The release tag comes from `<Version>` in
> [`SongRequestDesktopV2Rewrite.csproj`](SongRequestDesktopV2Rewrite/SongRequestDesktopV2Rewrite.csproj),
> which is also the version the app shows and updates from.

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
About dialog and in the update check always agree.

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
- [`docs/REMOVED-FEATURES.md`](docs/REMOVED-FEATURES.md) — everything removed or changed in the cross-platform migration (tray icon, keyboard remote shortcuts, embedded Google login, auto tool setup, …)
- [`SongRequestDesktopV2Rewrite/tools/README.md`](SongRequestDesktopV2Rewrite/tools/README.md) — FFmpeg / yt-dlp setup

## Testing

```bash
./tests/run-tests.sh        # unit + integration + JS + coverage floors + summary
```

Three suites run in CI (`Tests` job) on every push/PR: **unit** (hermetic logic),
**integration** (FFmpeg/OpenAL/library/MIDI), and **JavaScript** (`newui/keys.js`).
Coverage is ratcheted, failures are reported in the job summary, and the check is
meant to be a required branch-protection status. See [`tests/README.md`](tests/README.md).
