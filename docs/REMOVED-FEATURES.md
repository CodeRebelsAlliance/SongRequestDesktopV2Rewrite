# Removed & Changed Features

The app was migrated from a **Windows-only WPF + CefSharp** UI to a
**cross-platform Photino (HTML/JS)** UI, and its audio/MIDI backends were
replaced with cross-platform ones. Most features are 1:1, but this document is
the authoritative list of what was **removed** or **changed**.

Related: [`docs/TESTING.md`](TESTING.md).

---

## Removed (no replacement)

### 1. System tray (partially restored)
- **Was**: `AppManager` created a WinForms `NotifyIcon` (tray icon, menu, balloon
  notifications).
- **Now**: a tray icon **is available on Windows** (`Shell_NotifyIcon`) and
  **Linux** (AppIndicator / StatusNotifierItem via `libayatana-appindicator3` or
  `libappindicator3`) — Settings → Startup & Window → Minimise to tray / Close to
  tray. Still **removed**: tray balloon notifications (`ShowNotification`); and
  there is **no tray on macOS**. See *Added* below.

### 2. App-wide keyboard shortcuts (now system-wide on Windows / Linux-X11)
- **Was**: `RemoteControlService` hooked `InputManager.PreProcessInput`, so the
  remote-control keybinds fired **while an app window was focused** (they were
  never OS-global).
- **Now**: the same keybinds (Settings → Remote Control) can be registered as
  **system-wide global hotkeys** — they work even when SongRequest is not focused
  — via **Settings → Remote Control → Enable system-wide keyboard shortcuts**.
  Supported on **Windows** (`RegisterHotKey`) and **Linux/X11** (`XGrabKey`);
  **not** on macOS or Linux/Wayland (the toggle is disabled there).

### 3. Embedded Google/YouTube sign-in window
- **Was**: a CefSharp (Chromium) window (`Authentication`) let you sign in to
  Google in-app; the app then read the resulting YouTube cookies.
- **Now**: the embedded browser is gone. Authentication is done by **importing
  cookies**:
  - via **yt-dlp** reading cookies from an installed browser
    (`--cookies-from-browser`), or
  - by **pasting** a Netscape `cookies.txt` or a raw `Cookie:` header
    (Settings → Security & Server → YouTube Authentication, or the startup step).
- **Why**: no cross-platform in-app browser exposes HttpOnly cookies.

### 4. Windows-only app helpers
`AppManager` also contained (with no callers) `ForceClose`,
`GracefulShutdown`, `Restart`, `ShutdownWithTimeout`, `IsRunningElevated`,
`GetAppInfo`, `ShowMainWindow`. All removed with the class — there is no
restart/force-close action exposed anywhere now.

---

## Changed (works, but behaves differently / needs setup)

### 1. FFmpeg is required for audio
- **Was**: on Windows, MP3/M4A/etc. decoded via **Windows Media Foundation** — no
  extra tools needed.
- **Now**: all decoding goes through **FFmpeg**, which handles every
  container/codec — including the **Opus-in-WebM** files YouTube downloads that
  are saved with a `.mp3` extension. Without `ffmpeg` the app cannot play audio.
  `ffprobe` supplies duration/rate/channels; if only `ffmpeg` is present,
  duration falls back to TagLib.
- Located in a bundled `tools/` folder, next to the app, or on `PATH`. See
  `SongRequestDesktopV2Rewrite/tools/README.md`.

### 2. External tools: now auto-downloadable
- The app **does not bundle** yt-dlp or ffmpeg/ffprobe, but it can now
  **download them for you** (on request) into its `tools` folder, with progress:
  - a prompt appears **on startup** when a tool is missing, and
  - **Settings → Security & Server → External Tools** shows the status of
    FFmpeg, FFprobe and yt-dlp and can download the missing ones.
- Works on all platforms (Windows/Linux/macOS static builds). You can still place
  them in `tools/` or on the `PATH` yourself instead.

### 3. Audio backend: WinMM/WaveOut → OpenAL Soft (all platforms)
- Windows no longer uses `WaveOutEvent`; every platform uses OpenAL Soft.
- **Device numbers changed meaning**: the saved output-device number now indexes
  the OpenAL device list. **Re-select your output device after upgrading**;
  `-1` still means the default device.
- Each concurrent output (music, chime, each soundboard button, Music Share)
  opens its own OpenAL device. On bare ALSA this may need a mixing backend
  (PulseAudio/PipeWire recommended).

### 4. MIDI backend: NAudio → RtMidi (all platforms)
- Windows no longer uses NAudio's WinMM MIDI. **MIDI device numbers now index the
  RtMidi device list — re-select your MIDI input/output after upgrading.**

### 5. "Bring window to front" is Windows-only
- `SetForegroundWindow` is a no-op on Linux/macOS; windows may not auto-focus
  there.

### 6. Updates
- Windows uses a generated `.bat`; Linux a generated `.sh`. The **macOS update
  path is unverified** (app bundles / `.dmg` are not handled).
- Release assets are per-platform: `SongRequest-<rid>.zip` (Windows) /
  `SongRequest-<rid>.tar.gz` (Linux/macOS).

### 7. Thumbnails
- The old WPF `Image`-based thumbnails are gone. Thumbnails now come from TagLib
  embedded art, YouTube data URLs, or the library's cached thumbnails.

### 8. The web UI is the only UI
- All legacy WPF windows (`MainWindow`, `YoutubeForm`, `MusicPlayer`,
  `SoundboardWindow`, `Settings`, `About`, `Presentation`, `MusicShare`, and the
  dialogs) were deleted. The Photino/HTML UI is the only UI; the temporary
  "Use New UI" toggle was removed.

---

## Added (restored / new)

- **System tray** — a tray icon with a menu (**Show**, **Play/Pause**, **Next**,
  **Stop**, **Quit**) on **Windows** (Shell_NotifyIcon) and **Linux**
  (AppIndicator / StatusNotifierItem via libayatana-appindicator3 or
  libappindicator3), plus **Minimise to tray** and **Close to tray** options
  (Settings → Startup & Window). Requires a tray/StatusNotifier host on Linux
  (e.g. KDE, XFCE, or a GNOME extension). **No tray on macOS.**
- **Autostart** — *Start SongRequest when you log in*: Windows registry
  `…\CurrentVersion\Run`, Linux `~/.config/autostart/songrequest.desktop`,
  macOS `~/Library/LaunchAgents/com.songrequest.desktop.plist`.
- **Startup options** — *Start minimised* (also via the `--minimized` command-line
  flag, which the autostart entry passes).
- **System-wide keyboard shortcuts** — the remote-control keybinds (Play/Pause,
  Next, Previous, Stop, Volume, Crossfade, Announcement) can be registered as
  OS-wide hotkeys (Settings → Remote Control → *Enable system-wide keyboard
  shortcuts*). Windows + Linux/X11.
- **Automatic tool download** — if FFmpeg/FFprobe or yt-dlp are missing, the app
  offers (on startup and in Settings → Security & Server → *External Tools*) to
  download the right static build for the platform into its `tools` folder, with
  progress. Optional — the user can decline.
- **Updater rework** — the in-app updater now works on all three platforms:
  per-platform release assets (`SongRequest-<rid>.zip` on Windows,
  `SongRequest-<rid>.tar.gz` on Linux/macOS), download **verification**,
  **staged progress** (`starting`, `downloading`, `verifying`, `extracting`,
  `preparing`, `restarting`, `done`, `error`) reported to the UI, and an updater
  script that **backs up** the current install, applies the update, **verifies**
  the launcher and **rolls back** on failure before restarting.
- **System media controls (MPRIS / SMTC)** — the OS now shows the current title,
  artist, album, artwork and playback state, and can drive play/pause,
  next/previous, stop, seek and volume:
  - **Linux**: an **MPRIS** D-Bus service
    (`org.mpris.MediaPlayer2.songrequest`) — desktop media widget, lock screen
    and media keys.
  - **Windows**: **System Media Transport Controls** (media overlay, media keys,
    taskbar/lock screen), shipped as a Windows-only companion assembly
    (`net10.0-windows10.0.19041.0`, WinRT) that the cross-platform app loads at
    runtime.

  Toggle: **Settings → System Integration**. macOS (**MPNowPlaying**) uses the
  same abstraction and is still planned. See
  [`docs/SYSTEM-MEDIA-INTEGRATION.md`](SYSTEM-MEDIA-INTEGRATION.md).
- **Soundboard fade-in / fade-out (now applied)** — the per-button **Fade In** and
  **Fade Out** options were previously stored and shown in the UI but ignored
  during playback. They now shape the audio: a fade-in ramps the button from
  silence, and a fade-out ramps it down *before* the sound is stopped (including
  a re-fade-in on every loop iteration). The ramp length is `SoundboardEngine.FadeDurationMs`
  (default 300 ms; `0` restores the old instant behaviour).
- **Windows install/uninstall (Apps & features)** — on Windows the app registers
  itself **per user, without administrator rights** in *Settings → Apps →
  Installed apps* as **SongRequest Desktop** (publisher **CodeRebelsAlliance UG**)
  with the current version, and generates an uninstaller
  (`%LOCALAPPDATA%\SongRequestDesktopV2Rewrite\uninstaller\uninstall.cmd`) that
  Windows Settings links to. The entry is refreshed on each launch, so the listed
  version follows in-app updates. The uninstaller removes the entry and the
  autostart registration and offers to delete settings/library/downloads.

---

## Configured but not functional (pre-existing gaps)

- Nothing outstanding: the soundboard **fade-in / fade-out** options used to be
  stored but ignored — they are now applied to the audio (see *Added*). The
  remote-control keyboard keybinds are executed too (see *Added #2*).

---

## Not supported (never was)

- **OS media keys / media overlay** on **macOS** (MPNowPlaying). Linux (MPRIS)
  and Windows (SMTC) are implemented — see *Added*.
  See `SMTC_NOT_SUPPORTED_IN_WPF.md` and `docs/SYSTEM-MEDIA-INTEGRATION.md`.

---

## Migration checklist for users

1. Put **`ffmpeg`** + **`ffprobe`** in `tools/` (or on `PATH`).
2. Optionally put **`yt-dlp`** in `tools/` for browser cookie import, or paste
   cookies in *Settings → Security & Server*.
3. **Re-select** your audio output device and MIDI devices (numbering changed).
4. If you used keyboard shortcuts for remote control, switch to MIDI or the
   on-screen controls.
5. Expect no tray icon.
