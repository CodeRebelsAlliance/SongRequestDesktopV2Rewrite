# System Media Integration

SongRequest V2 exposes its playback to the **operating system's media controls**,
so the desktop / lock screen / media keys show the current title, artist, album
and artwork, and can drive play/pause, next/previous, stop, seek and volume.

Toggle: **Settings → System Integration → Integrate with system media controls**
(on by default where supported). The hint under the toggle shows the active
backend.

---

## Architecture

Everything platform-specific sits behind one interface, so the player core has no
notion of MPRIS/SMTC:

| Type | Role |
|---|---|
| `ISystemMediaSession` (`Platform/Media/SystemMediaSession.cs`) | Backend contract: `UpdateMetadata`, `UpdatePlayback`, plus `Play/Pause/TogglePlayPause/Next/Previous/Stop/Seek/SetPosition/Volume/Raise/Quit` events. |
| `MediaMetadata` | Immutable now-playing record: title, artist, album, duration, local artwork path. |
| `MediaSessionBridge` (`Platform/Media/MediaSessionBridge.cs`) | Platform-free glue: pushes the engine's now-playing/state/position to the session and turns OS commands back into `MusicEngine` calls. |
| `MediaArtwork` (`Platform/Media/MediaArtwork.cs`) | Resolves a local image for the artwork field. |
| `MediaSessionFactory` | Picks the backend for the current OS (falls back to `NullSystemMediaSession`). |
| `MprisMediaSession` (`Platform/Media/MprisMediaSession.cs`) | **Linux** backend: an MPRIS D-Bus service (see below). |

`YoutubeFormInterop.StartSystemMediaIntegration()` creates the bridge once the
window is up (never from the constructor, so headless tests don't open a D-Bus
service), and `ApplySystemMediaIntegration()` starts/stops it when the setting
changes.

### Artwork resolution (`MediaArtwork.Resolve`)

1. the song's cached thumbnail **data URL** (decoded to a file in
   `<app-data>/now-playing-artwork/`);
2. the **YouTube thumbnail cache** (`data/downloadedvideos/thumbnail-cache/<videoId>.png|jpg`);
3. **embedded album art** (via TagLib).

---

## Linux — MPRIS

[MPRIS](https://specifications.freedesktop.org/mpris-spec/latest/) is the
cross-desktop standard for media players. The backend:

- registers the service name **`org.mpris.MediaPlayer2.songrequest`** on the
  session bus at the object path **`/org/mpris/MediaPlayer2`**;
- exports `org.mpris.MediaPlayer2` and `org.mpris.MediaPlayer2.Player` (with a
  correct introspection description and per-interface property sets);
- implements `Properties.Get` / `GetAll` / `Set` and emits
  **`PropertiesChanged`** so the desktop updates live;
- maps transport methods to bridge events and the OS volume slider to the app
  volume.

Implemented with the managed **`Tmds.DBus`** library (no native D-Bus dependency).

Verified end-to-end against a real session bus by the integration tests
(`tests/SongRequestDesktopV2Rewrite.IntegrationTests/MprisMediaSessionTests.cs`),
which register the service, read the properties back through a D-Bus proxy,
drive every transport method and assert the `PropertiesChanged` signal arrives.
Any desktop environment or tool (`playerctl`, GNOME/KDE media widgets, media
keys) can consume it.

Notes:
- requires a **session bus** (present on any normal desktop session). In a bare
  container/SSH session the service simply falls back to no-op.
- the `DesktopEntry` property is set to `songrequest`; installing a matching
  `songrequest.desktop` lets the desktop show the app's icon/name.

## Windows — SMTC

Windows uses **System Media Transport Controls** (SMTC): the media overlay that
appears when you press a media key, the taskbar/lock-screen media widget and the
hardware media keys.

Because SMTC is a WinRT API (CsWinRT projection), it lives in a **Windows-only
companion assembly**:

| | |
|---|---|
| Project | `SongRequestDesktopV2Rewrite.Smtc.Windows/` |
| TFM | `net10.0-windows10.0.19041.0` (with `EnableWindowsTargeting`, so it still builds on Linux/macOS CI) |
| Type | `SongRequestDesktopV2Rewrite.Smtc.SmtcMediaSession` |
| Entry point | `static ISystemMediaSession? Create(IntPtr windowHandle)` |

The main app stays a single cross-platform `net10.0` assembly and has **no
compile-time reference** to it. `Platform/Media/WindowsSmtcLoader.cs`:

- only activates on Windows when `SongRequestDesktopV2Rewrite.Smtc.Windows.dll`
  sits next to the app (otherwise the no-op session is used);
- loads it into a dedicated `AssemblyLoadContext` whose resolver shares
  assemblies already loaded by the app (so the result still casts to
  `ISystemMediaSession`) and resolves the WinRT projection
  (`Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll`) from the companion's
  `.deps.json`.

The companion is published next to the app automatically for `win-*` RIDs by the
`PublishWindowsSmtcCompanion` MSBuild target in the main `.csproj`.

What it maps:

| SMTC | App |
|---|---|
| `MusicProperties` Title/Artist/Album + `Thumbnail` | current track metadata + resolved artwork |
| `PlaybackStatus` + `UpdateTimelineProperties` | playback state + position (timeline throttled to ~1 s) |
| `ButtonPressed` Play/Pause/Stop/Next/Previous | transport commands |
| `ButtonPressed` FastForward/Rewind | relative seek (±10 s) |
| `PlaybackPositionChangeRequested` | absolute seek |

Differences from MPRIS: SMTC has no per-player volume slider, no "raise window"
and no "quit" request, so those events are never raised on Windows; and the
session must be created **after** the top-level window exists (the interop passes
`PhotinoWindow.WindowHandle`).

## macOS — MPNowPlaying (planned)

`MPNowPlayingInfoCenter` + `MPRemoteCommandCenter` via Objective-C interop is
the equivalent; not implemented yet (the factory returns the no-op session). The
companion-assembly pattern used for Windows is the intended shape.

---

## Testing

- **Unit** (`MediaSessionBridgeTests`, `MediaArtworkTests`,
  `MediaSessionFactoryTests`, `WindowsSmtcLoaderTests`): bridge → engine and
  engine → bridge mapping, metadata de-duplication, artwork resolution order,
  data-URL decoding, null backend, and the Windows loader's graceful degradation
  plus a metadata-only **contract test** on the built companion assembly (name,
  `ISystemMediaSession`, `Create` entry point, Windows target platform).
- **Integration** (`MprisMediaSessionTests`): live MPRIS service on the session
  bus (skipped automatically when there is no bus).
- **Compile coverage**: the companion project is part of the solution, so the
  SMTC code is compiled on every CI run (including Linux/macOS) even though it
  only ever *runs* on Windows.
