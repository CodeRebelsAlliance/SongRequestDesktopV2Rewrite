# Architecture

How the code is layered after the mobile-ready core extraction (plan:
[`MOBILE-APP-PLAN.md`](MOBILE-APP-PLAN.md), milestone M0).

```
┌──────────────────────────────┐        ┌───────────────────────────────┐
│ SongRequestDesktopV2Rewrite  │        │ SongRequest.Mobile  (M1+)     │
│ (net10.0, Photino, WinExe)   │        │ (net10.0-android/-ios, MAUI)  │
│  Program, NewUiWindow        │        │  MAUI shell + HybridWebView   │
│  DesktopPlatform.Register()  │        │  MobilePlatform.Register()    │
└──────────────┬───────────────┘        └───────────────┬───────────────┘
               │                                        │
               ▼                                        ▼
        ┌──────────────────────────────────────────────────────┐
        │ SongRequest.Core  (net10.0, no native dependencies)  │
        │  engines: MusicEngine, SoundboardEngine,             │
        │           AnnouncementEngine, MusicShare*,           │
        │           SongRequestEngine, RemoteControlService    │
        │  services: LibraryService, LyricsService,            │
        │           ConfigService, YoutubeService,             │
        │           FetchingService, UpdateService,            │
        │           ThumbnailStore, LibraryRecommendations     │
        │  interop: YoutubeFormInterop (the ~150 commands)     │
        │  abstractions + provider hooks (see below)           │
        └──────────────────────────────────────────────────────┘
               ▲                                        ▲
               │  newui/*.html,*.js,*.css (the UI)      │
        ┌──────┴────────────────────────────────────────┴──────┐
        │ Desktop: Photino windows  │  Mobile: HybridWebView   │
        │ one window per view       │  one shell, tabs/sheets  │
        └──────────────────────────────────────────────────────┘
```

## Host contract

The interop never talks to a windowing technology. It talks to `IUiShell`
(`SongRequest.Core/IUiShell.cs`), reached through
`ISongRequestHost.NewUiRef`:

| Member | Desktop | Mobile |
|---|---|---|
| `ShowMusicPlayerWindow()` / `CloseMusicPlayerWindow()` | Photino window | tab / sheet |
| … the same for soundboard, presentation, visualization, announcement | | |
| `MusicPlayerSendMessage` … `AnnouncementSendMessage` | `PhotinoWindow.SendWebMessage` | `HybridWebView.SendRawMessage` |
| `SetSubWindowFullscreen(name, enabled)` | native Photino fullscreen | in-app fullscreen page |
| `Close()` | quit (releases tray, media session, MIDI) | quit |

## Platform provider hooks

Core is free of native dependencies. Each seam has a static provider that the
host registers once at startup; without a registration the core degrades
gracefully instead of throwing.

| Seam | Core type | Desktop registration | Mobile (planned) |
|---|---|---|---|
| Audio output + device list | `AudioOutputFactory.Provider`, `.DeviceEnumerator` | OpenAL Soft (`DesktopPlatform`) | `AudioTrack` + `AudioManager` (`AndroidPlatform`, done) / `AVAudioEngine` (M7) |
| Audio decode | `AudioSourceFactory.Provider` (default: FFmpeg subprocess) | FFmpeg (default) | `MediaExtractor`+`MediaCodec` (done) / `AVAssetReader` (M7) |
| MIDI in/out | `MidiServiceFactory.BackendFactory` | RtMidi, with the `/dev/snd/seq` guard | `MidiManager` (M4) / CoreMIDI (M7) |
| System media session | `MediaSessionFactory.Register(...)` | MPRIS / SMTC | framework `MediaSession` + foreground service (done) / `MPNowPlayingInfoCenter` (M7) |
| Global hotkeys | `GlobalHotkeyBackendFactory.BackendFactory` | `RegisterHotKey` / `XGrabKey` | none (disabled) |
| Tray | `TrayIconFactory.IsSupportedProvider`, `.BackendFactory` | Win32 / AppIndicator | none (disabled) |
| Update provider | `UpdateService` (desktop archives) | built in | store/APK check (M8) |

Graceful defaults: `NullAudioOutput` (silent, reports state), `NullMidiService`,
`NullSystemMediaSession`, `NullGlobalHotkeyBackend`, `NullTrayIcon`,
`AudioDevices.Enumerate()` → empty, `AudioSourceFactory` → FFmpeg if present.

`AutoStart` and `StartupGuard` live in Core because they are cross-platform
already; on mobile `AutoStart.IsSupported` is false, so the settings toggle
disables itself.

## Rules for new code

1. **Platform code belongs in a host**, behind an interface or a provider hook —
   never in Core.
2. **No native NuGet dependencies in Core** (OpenAL, RtMidi, Photino, Tmds.DBus,
   GTK stay in the desktop project; mobile equivalents go in the mobile
   projects).
3. **The UI is shared**: pages talk JSON over the host bridge
   (`newui/hostbridge.js`). A new host adds a transport there, not a new UI.
4. **Desktop behaviour is the regression baseline**: the `Tests` job must stay
   green on every change to Core.
5. Provider registrations happen **once, before anything touches them**
   (desktop: `Program.Main` → `DesktopPlatform.Register()`; tests: a module
   initializer; mobile: `MauiProgram`).
