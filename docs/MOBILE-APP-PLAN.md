# Mobile App Plan — Android & iOS with .NET 10

> Status: **agreed approach, implementation not started.** The UI/runtime choices
> and the distribution model below are decided (§12); the phases in §7 are the
> execution order. This document is the plan for a mobile companion to the
> desktop app with **feature parity**, built with .NET 10.
>
> Related: [`../README.md`](../README.md) · [`TESTING.md`](TESTING.md) ·
> [`REMOVED-FEATURES.md`](REMOVED-FEATURES.md) · [`library-data-models.md`](library-data-models.md)

---

## 1. Goal and scope

Build **Android and iOS apps** that do what the desktop app does: receive song
requests from the companion server, queue/approve them, download and play audio
(crossfade, normalisation, ducking), run the soundboard (pages, MIDI, fades),
show lyrics/visualisation/presentation, flood the queue, control by MIDI, and
share playback — all driven by the **same HTML/CSS/JS UI** the desktop already
uses, on the **same C# engine**.

Non-negotiables carried over from the desktop project:

- **No regression** on desktop: audio features, tests, coverage floors, `Tests`
  as the required check, warnings at 0.
- **Feature parity is measured, not claimed** — the parity matrix in §6 lists
  every feature with its mobile implementation and phase, and each phase has
  exit criteria.
- **The web UI stays the UI** — one HTML/CSS/JS codebase serves desktop
  (Photino) and mobile (WebView). Mobile-specific layout is CSS, not a rewrite.
- Multi-platform releases stay automated from `<Version>`; mobile artifacts join
  the same GitHub release flow.

Out of scope for this plan: a Linux/desktop rewrite, a re-implementation of the
companion server, smart-TV apps, and breaking changes to `library.json` that the
desktop could not read.

---

## 2. Executive summary

**Recommendation: .NET MAUI (net10.0-android + net10.0-ios) hosting the existing
web UI in a `HybridWebView`, over a shared `SongRequest.Core` library, with
platform-native audio I/O.**

Why this shape:

1. **The UI is already a web app.** 6 pages, ~12 000 lines of HTML/CSS/JS, talking
   to the host over a *single JSON message bus* (`{type:'request'|'event', …}`).
   `HybridWebView` is the direct mobile analogue of Photino: arbitrary
   HTML/JS/CSS in the platform WebView (Android WebView / iOS WKWebView), raw
   string messages both ways, plus `InvokeJavaScriptAsync` for host→page calls.
   Reuse is therefore **near-total for the UI**; the bridge needs one new adapter
   of ~40 lines.
2. **The engine is already platform-agnostic.** Only three seams touch the OS:
   `IAudioOutput`, `IAudioSource`, `IMidiService`, plus the window shell, the
   media session and the file dialogs. Everything else (queue, playlists,
   library, recommendations, lyrics, requests, soundboard model, crossfade,
   ducking, music share, backend HTTP) is plain .NET already compiled as
   `net10.0` and every non-native dependency is `netstandard2.0` or `net10.0`
   (verified: NAudio.Core, TagLibSharp, YoutubeExplode, Newtonsoft.Json, QRCoder,
   AngleSharp).
3. **Audio I/O is a bounded, known problem.** Implement the two interfaces on top
   of `AudioTrack` / `AVAudioEngine` and `MediaCodec`+`MediaExtractor` /
   `AVAssetReader`. This keeps the crossfade mixer, normalisation, ducking,
   soundboard and visualisation tap working unchanged.
4. **The OS integration points already have interfaces.** `SystemMediaSession`
   (MPRIS/SMTC today) becomes Android Media3 `MediaSession` and iOS
   `MPNowPlayingInfoCenter`; `IMidiService` (RtMidi today) becomes
   `MidiManager`/CoreMIDI; `ISongRequestHost` keeps the engines host-agnostic
   once `NewUiWindow` is replaced by an `IUiShell` seam.

**Distribution (decided, see §10):** **one full-featured build per platform,
delivered outside the public stores** — the Android APK on **GitHub Releases**
(the desktop's existing channel) and the iOS build via **TestFlight (internal
testers) / Ad Hoc**. Apple's guideline 5.2.3 and Google's IP/abuse policies make
a store build whose main feature is downloading YouTube audio very likely to be
rejected, so no store-compliant variant is maintained. Consequence: **no
downloader build flag, no second `ApplicationId`, no store listings** — the
policy work shrinks to privacy/security declarations and TestFlight metadata.

**Delivery order (decided):** **Android first**, all the way to parity
(buildable on Linux/Windows CI without a Mac), then **iOS** as a focused phase
that reuses the proven core, bridge, UI and acceptance criteria.

**Scope (decided):** **full feature parity in one programme** — phases M0–M8
below, in order. Each phase is still independently shippable and gated by its own
exit criteria, so the programme can stop or re-order at any phase boundary.

---

## 3. Options considered

### 3.1 UI approach

| # | Option | Reuse of `newui/` | Native feel | Effort | Risk |
|---|---|---|---|---|---|
| **A** | **MAUI + `HybridWebView` (recommended)** | ~95 % (HTML/CSS/JS + 1 bridge adapter) | Good (native shell around the web app) | **M** | HybridWebView is young (see §11 R3) |
| B | MAUI + Blazor Hybrid (Razor components) | ~0 % UI (port ~12 k lines to Razor) | Good | XL | Big rewrite, two UIs to keep in sync |
| C | MAUI + native XAML UI | 0 % | Best | XXL | Full second product |
| D | Avalonia 12 (XAML + its own WebView) | ~95 % via its WebView | Good, one XAML codebase | L | Desktop stays Photino → third UI framework in the repo; Avalonia mobile is the newer target (Tier 2) |
| E | Uno Platform (WinUI XAML) | ~0 % | Good | XL | XAML rewrite |
| F | Two thin native heads (`net10.0-android` + `net10.0-ios`) without MAUI, each with its own WebView + bridge | ~95 % | Best control, smallest app | M+ (shell written twice, ~700 lines each) | Two shells to maintain; no shared navigation/permissions/D.I. helpers |
| G | PWA / responsive web app served by the companion server | n/a (server work) | Poor (no local audio, no background) | M | Not "feature parity"; needs server-side command proxying |

**A wins**: it reuses the UI and the engine, gives one mobile shell for both
platforms, and keeps the option of dropping to native APIs per platform (MAUI's
`Platforms/` folders can use `Android.Media`, `AVFoundation`, … directly).
**Option F is the documented fallback** if HybridWebView proves unstable — the
shared core, the bridge protocol and the platform services are identical, only
the shell changes.

### 3.2 Mobile runtime / audio stack

| Concern | Option | Decision |
|---|---|---|
| Audio **output** | OpenAL Soft natives built for Android/OpenSL + iOS/CoreAudio (keeps `OpenAlAudioOutput` identical) | Viable fallback |
| | **Android `AudioTrack` / iOS `AVAudioEngine` behind `IAudioOutput`** | **Recommended** — first-party, low latency, background-friendly, no extra native dependency |
| | miniaudio via SoundFlow (one code path for mobile + desktop, AAudio/CoreAudio backends) | Alternative if we want to unify desktop later |
| Audio **decode** | Bundle FFmpeg as a native lib + `FFmpeg.AutoGen` | Optional phase; needed only for exotic codecs |
| | **Platform decoders (`MediaCodec`/`MediaExtractor`, `AVAssetReader`) behind `IAudioSource`** | **Recommended** for m4a/mp3/aac/flac/wav (and Opus on Android) |
| | Download the **m4a/AAC** audio stream from YouTube (itag 140) instead of WebM/Opus | **Recommended** — removes the codec gap at the source, and avoids muxing entirely |

---

## 4. Target architecture

### 4.1 Solution layout

```
SongRequest.Core/                      net10.0           engine, services, interop, models
SongRequest.Platform.Android/          net10.0-android   audio, midi, media session, storage, notifications
SongRequest.Platform.iOS/              net10.0-ios       audio, midi, media session, storage, notifications
SongRequest.Mobile/                    net10.0-android;net10.0-ios   MAUI shell + HybridWebView + mobile CSS
SongRequestDesktopV2Rewrite/           net10.0           desktop (Photino) — unchanged in behaviour
tests/…UnitTests, …IntegrationTests    net10.0           reference SongRequest.Core (namespace unchanged)
tests/SongRequest.Platform.Tests/      net10.0           pure-logic tests for the mobile adapters
```

`SongRequest.Core` keeps the existing namespace `SongRequestDesktopV2Rewrite` so
the move is mechanical and the test projects need only a `ProjectReference`
change. The desktop project becomes a thin host (Photino window + platform
services) referencing `SongRequest.Core`.

### 4.2 Seams to introduce (all small, all testable)

| Seam | Today | After |
|---|---|---|
| Window shell | `ISongRequestHost.NewUiRef` typed `NewUiWindow?`, 28 call sites into `ShowMusicPlayerWindow()`, `MusicPlayerSendMessage`, `SetSubWindowFullscreen`… | **`IUiShell`** interface (`ShowPlayer/ShowSoundboard/ShowPresentation/ShowVisualization/ShowAnnouncement/Close…`, `SendTo(view, json)`, `SendEvent(json)`, `SetSubWindowFullscreen(name, bool)`, `QuitRequested`). Desktop: `NewUiWindow` implements it. Mobile: `MobileShell` maps views to tabs/full-screen pages. |
| Audio output | `AudioOutputFactory.Create()` static → `OpenAlAudioOutput` | `IAudioOutputFactory` injected; host picks OpenAL (desktop) / AudioTrack / AVAudioEngine |
| Audio decode | `AudioSourceFactory.Open(path)` static → FFmpeg subprocess | `IAudioSourceFactory` injected; host picks FFmpeg (desktop) / platform decoders |
| MIDI | `MidiServiceFactory` → RtMidi | add `AndroidMidiService`, `IosMidiService` to the same factory |
| Media session | `MediaSessionFactory` → MPRIS/SMTC | add Media3 `MediaSession` (Android) and `MPNowPlayingInfoCenter` (iOS) |
| File dialogs | `YoutubeFormInterop.ShowOpenFileDialog` delegate set by Photino | keep the delegate; mobile sets a `FilePicker`/`UIDocumentPicker` implementation |
| Updates | `UpdateService` + `UpdateInstaller` (archives, relaunch script) | `IUpdateProvider`; mobile = "store update" no-op, Android build additionally checks the GitHub release APK |
| Paths | `AppPaths.BaseDirectory`, `data/downloadedvideos` relative to the app dir | `AppPaths.DataRoot` (writable sandbox root: `FileSystem.AppDataDirectory`), **store library paths relative to it** (iOS container paths change across installs) |
| Tooling | `ToolLocator`/`ToolManager` (ffmpeg, yt-dlp download) | no-op on mobile (`IToolProvider`); mobile never shells out |

### 4.3 The bridge (one new adapter, no page rewrites)

The pages already funnel through `SongRequestHost.registerHostMessageHandler()`
and `window.external.sendMessage()` (with a WebView2 fallback). Mobile adds a
third transport to `newui/hostbridge.js`:

```js
// HybridWebView transport (added to hostbridge.js)
function registerHostMessageHandler(handler) {
  if (window.HybridWebView) {
    window.addEventListener('HybridWebViewMessageReceived',
      e => handler(e.detail.message));          // raw string, same JSON envelope
    return 'hybrid';
  }
  … existing external / chrome.webview paths …
}
function sendToHost(msg) {
  if (window.HybridWebView) { window.HybridWebView.SendRawMessage(msg); return true; }
  … existing paths …
}
```

Host side: `HybridWebView.RawMessageReceived` → `YoutubeFormInterop.HandleMessage`
(unchanged dispatcher, ~150 commands) and `HybridWebView.SendRawMessage(json)`
replaces `PhotinoWindow.SendWebMessage(json)`. The `_framework/hybridwebview.js`
script the control requires is loaded from the app bundle in `index.html`
(desktop ignores it behind a feature check or ships it harmlessly).
`tests/js/hostbridge.test.js` grows cases for the new transport.

### 4.4 UI shell and responsive layer

- **Native shell**: bottom navigation with 5 destinations — **Requests**
  (`index.html`), **Play** (`musicplayer.html`), **Soundboard**
  (`soundboard.html`), **Stage** (`presentation.html` / `visualization.html`),
  **Settings** (settings panel of `index.html`) — plus a compact mini-player bar
  that survives tab switches (native `Grid`, not a second WebView).
- **WebView strategy**: one `HybridWebView` per open tab, max 2 resident
  (destroy the rest); the player page is kept alive because the mini-player and
  the "now playing" state live there. Measure before committing to more.
- **Responsive CSS**: `newui/mobile.css` imported only when
  `document.documentElement.dataset.shell === 'mobile'` (set by the host at load):
  touch targets ≥ 44 px, single-column layout under 720 px, bottom sheets instead
  of dialogs, soundboard grid sized to the viewport, safe-area insets
  (`env(safe-area-inset-bottom)`), full-screen stage mode, no hover-only
  affordances. Existing CSS is desktop-first with only 2 media queries, so this is
  a real (but bounded) workstream — see §7 M1.
- **Desktop keeps multi-window**: `NewUiWindow` is untouched behaviourally.

### 4.5 Audio subsystem on mobile

```
MusicEngine (unchanged)
 ├── MixingSampleProvider  ──► MobileAudioOutput : IAudioOutput
 │      ├── Android: AudioTrack (PCM float32, Streaming, LowLatency,
 │      │            AudioAttributes USAGE_MEDIA, focus via AudioFocusRequest)
 │      └── iOS:     AVAudioEngine + AVAudioPlayerNode (schedule buffers,
 │                   AVAudioSession category .playback, background mode)
 ├── CurrentReader/NextReader : IAudioSource (crossfade unchanged; playback tick unchanged)
 │      ├── Android: MediaExtractor + MediaCodec → PCM float
 │      └── iOS:     AVAssetReader + AVAssetReaderTrackOutput (LPCM float32)
 └── AudioSamplesCaptured ──► VisualizationStreamer (unchanged) → WebView canvas
```

Decisions and details:

- **Format**: keep 44.1 kHz stereo float32 everywhere (the engine's `Format`);
  convert to the device mix format inside the output only if needed.
- **Latency**: buffer ≈ 2× `GetMinBufferSize` (Android) / 3 scheduled
  `AVAudioPCMBuffer`s (iOS); the render thread must never allocate or block on the
  UI thread. Soundboard latency budget target: **< 60 ms** end-to-end on a
  mid-range device.
- **Audio focus / interruptions**: Android `AUDIOFOCUS_GAIN` (pause on transient
  loss, duck on `AUDIOFOCUS_LOSS_TRANSIENT_CAN_DUCK`); iOS
  `AVAudioSession.interruptionNotification` → pause/resume, and route changes
  (Bluetooth/headphones) reflected into the settings "output device" list
  (`getMusicAudioDevices` returns the available routes, `setMusicOutputDevice`
  selects one — the same contract the desktop's Chromecast-icon menu uses).
- **Soundboard + announcement chime**: today these open their **own**
  `IAudioOutput`s. On mobile, creating 3–4 simultaneous `AudioTrack`s is wasteful
  and hurts the mixer tap; **route them through the engine mixer** (one small,
  desktop-compatible refactor: `SoundboardEngine`/`AnnouncementEngine` take an
  `ISampleProvider` sink when the host provides one). This also makes the
  visualisation react to soundboard hits, matching what users expect.
- **Background playback**: Android foreground service type `mediaPlayback`
  (`FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_MEDIA_PLAYBACK`, `POST_NOTIFICATIONS`)
  with a Media3 `MediaSessionService` notification; iOS `UIBackgroundModes: audio`
  + `AVAudioSession` + `MPNowPlayingInfoCenter`/`MPRemoteCommandCenter`.
  `MediaSessionBridge` (already written and unit-tested) drives both.
- **Fallback plan**: if `AudioTrack` misbehaves on a class of devices, drop in
  `miniaudio` (SoundFlow) or a mobile OpenAL Soft build without touching the
  engine — the seam is one interface.

### 4.6 Platform services

| Service | Android | iOS |
|---|---|---|
| Audio output | `AudioTrack` (+ `AudioManager` routes) | `AVAudioEngine` (+ `AVAudioSession`) |
| Audio decode | `MediaCodec` + `MediaExtractor` | `AVAssetReader` |
| Media session | Media3 `MediaSession`/`MediaSessionService` (`Xamarin.AndroidX.Media3.Session`) | `MPNowPlayingInfoCenter`, `MPRemoteCommandCenter` |
| MIDI | `Android.Media.Midi.MidiManager` (USB + BLE) | CoreMIDI (USB/BLE/Network MIDI; `MIDIEventList` sends) |
| Files | Storage Access Framework (tree picker + persisted URI) and/or import-into-app-storage; `MediaStore` for on-device music | Documents dir + `UIFileSharingEnabled` + `UIDocumentPicker`; import-into-app-storage |
| Notifications | Notification channels + Media3 notification | `UNUserNotificationCenter` |
| YouTube cookies | In-app WebView login → `CookieManager.GetCookie` | In-app WKWebView login → `WKWebsiteDataStore.HttpCookieStore` |
| Server auth | In-app WebView/Chrome Custom Tabs → token redirect | `ASWebAuthenticationSession` |
| Downloads in background | Foreground service type `dataSync` | `NSUrlSession` background (best effort) |
| Stage/TV | In-app full-screen; `Presentation` API on HDMI; optional Cast (`Xamarin.GooglePlayServices.Cast`) | In-app full-screen; external `UIWindowScene`; Cast requires an iOS binding of the (commercial) Cast SDK — deferred |
| Updates | Play (AAB) or in-app APK check for the GitHub build | App Store / TestFlight only |

### 4.7 Presentation & visualisation on mobile

Three tiers, in order:

1. **In-app stage** (MVP): full-screen presentation page on the phone/tablet;
   visualisation reuses `vizmath.js` + the `VisualizationStreamer` tap.
2. **External display / TV** (M2): Android `DisplayManager` + `Presentation`,
   iOS external `UIScene` — an HDMI/USB-C cable gives a real projector output
   with zero server work.
3. **Network cast** (M3, cross-repo): the companion server hosts the presentation
   page (its `presentation.html` + a WebSocket state feed) so any TV browser,
   Android TV, or "cast a tab" works — and the desktop gains the same option.
   Native Cast SDKs are deliberately **not** in the critical path (iOS needs a
   binding for a commercial pod; both need a registered Cast receiver app id).

---

## 5. Shared vs platform-specific code

| Area | Reuse | Notes |
|---|---|---|
| Requests, approve/blacklist, bad words, sendins, remote config, dashboard | **100 %** | `SongRequestEngine`, `FetchingService`, `BackendHttpClient` |
| Queue, playlists, play history, home feed, recommendations | **100 %** | `MusicEngine` queue, `LibraryService`, `LibraryRecommendations` |
| Lyrics (LRCLIB + captions + LRC parsing) | **100 %** | `LyricsService` (needs a `.lrc` sidecar path fix — see §11 R9) |
| Crossfade, normalisation, ducking, progress/now-playing | **100 %** | `MusicEngine` + NAudio.Core sample graph (netstandard2.0) |
| Soundboard model, pages, buttons, fade, MIDI mapping | **95 %** | `SoundboardEngine` + `IMidiService` implementation |
| Announcements | **95 %** | `AnnouncementEngine`, chime routed through the mixer on mobile |
| Music Share | **100 %** | HTTP against the backend (`ShareAudioPlayer` / `MusicShareService`) |
| Library scan/import/hash/thumbnails | **80 %** | `LibraryService`, `ThumbnailStore` (no FFmpeg → relies on inline art + platform decode) |
| Music player/presentation/visualisation UI | **~95 %** | same pages, mobile CSS, native shell |
| Downloads | **70 %** | `YoutubeService` + `YoutubeExplode`; no muxer needed; background rules differ |
| Auth/cookies | **60 %** | service-level reuse; acquisition path is per-platform |
| Updates, tools, tray, autostart, global hotkeys | **0 %** | replaced by store/OS equivalents (see §6) |

---

## 6. Feature parity matrix

Legend: ✅ full · 🟡 equivalent/different mechanism · ⛔ removed where unsupported
(disabled in the UI, exactly like the desktop greys out macOS/Wayland
unsupported options today) · **P**_n_ = phase.

| # | Feature | Desktop today | Mobile | P |
|---|---|---|---|---|
| 1 | Server connection, host setting, bearer token | ✅ | ✅ same | M1 |
| 2 | Startup/launch-mode flow, auth URL | ✅ | 🟡 in-app WebView / `ASWebAuthenticationSession` | M1 |
| 3 | Song requests list, approve/unapprove, blacklist, delete | ✅ | ✅ same UI | M1 |
| 4 | Bad-word filter, sendin allowed, remote config | ✅ | ✅ same | M1 |
| 5 | Dashboard/stats, console, logging | ✅ | ✅ same | M1 |
| 6 | Play queue, shuffle, repeat, reorder, "playing now" wait times | ✅ | ✅ same | M2 |
| 7 | Auto-download requested songs (YouTube) | ✅ | 🟡 Android full / Apple: see §10 | M5 |
| 8 | Playback: play/pause, seek, next/prev, volume, crossfade | ✅ (OpenAL) | 🟡 `AudioTrack`/`AVAudioEngine` | M2 |
| 9 | Volume normalisation (LUFS), clipping guard | ✅ | ✅ engine unchanged | M2 |
| 10 | Output-device menu (speaker/Chromecast icon) | ✅ | 🟡 route list (Bluetooth/headset/AirPlay) | M2 |
| 11 | Background playback while screen off / app backgrounded | ✅ (desktop window) | 🟡 foreground service / `UIBackgroundModes: audio` | M2 |
| 12 | OS media controls + lock screen / notification / headset buttons | ✅ MPRIS/SMTC | 🟡 Media3 / `MPRemoteCommandCenter` | M2 |
| 13 | Lyrics (synced + plain, scrolling, offset, font size) | ✅ | ✅ same JS | M2 |
| 14 | Music player expanded view | ✅ | ✅ same page, mobile CSS | M2 |
| 15 | Library: folders, scan, import, hash dedupe, missing files | ✅ | 🟡 SAF picker / Documents import, path rewriting | M3 |
| 16 | Library edit metadata, thumbnails, search, missing-flag | ✅ | ✅ same | M3 |
| 17 | Playlists (CRUD, reorder, queue, shuffle) | ✅ | ✅ same | M3 |
| 18 | Play history / recently played / clear | ✅ | ✅ same | M3 |
| 19 | Home feed: last played, recommended, recently listened, new | ✅ | ✅ same | M3 |
| 20 | Soundboard: pages, grid, buttons, colours, rename, reorder | ✅ | ✅ same + touch layout | M4 |
| 21 | Soundboard playback: fade in/out, loops, stop-all, master volume | ✅ | ✅ mixer path | M4 |
| 22 | Soundboard add-file / browse file | ✅ | 🟡 platform picker | M4 |
| 23 | Soundboard YouTube import (ytid drag/import) | ✅ | 🟡 downloader policy (§10) | M5 |
| 24 | MIDI: device list, input/output, per-button assignment, test | ✅ RtMidi | 🟡 `MidiManager` / CoreMIDI | M4 |
| 25 | Remote control: MIDI mappings + feedback snapshot | ✅ | ✅ same (MIDI) | M4 |
| 26 | Remote control: global keyboard shortcuts | 🟡 Win/X11 only | ⛔ (media buttons instead) | — |
| 27 | Announcement: start/stop, push-to-talk, dim, chime, toggle | ✅ | ✅ same (chime via mixer) | M4 |
| 28 | Music capture (cue sheet/sample export) | ✅ | ✅ same | M4 |
| 29 | Visualisation (bars, waveform, full-screen) | ✅ | ✅ same JS + streamer | M6 |
| 30 | Presentation window (artwork, lyrics, queue, QR, fullscreen) | ✅ | ✅ in-app; 🟡 TV via external display / server page | M6 |
| 31 | Music Share: start/stop, volume, status, receive | ✅ | ✅ same | M4 |
| 32 | Settings: General, Security & Server, Remote, Console, Library, Logging | ✅ | ✅ same + mobile sections | M3 |
| 33 | Settings: Startup & Window (tray, minimise, autostart, close-to-tray) | ✅ desktop | 🟡 Android: "start on boot"/"keep playing"; iOS: background audio only | M3 |
| 34 | Remote Control: "system-wide keyboard shortcuts" toggle | ✅ Win/X11 | ⛔ disabled | — |
| 35 | About, What's new (Markdown changelog), version | ✅ | ✅ same (`markdown.js`) | M1 |
| 36 | Update check / apply | ✅ archives + relaunch | 🟡 store update; Android GitHub build = APK install intent | M7 |
| 37 | Tools status / download FFmpeg+y t-dlp | ✅ | ⛔ (bundled natives, no exec) | — |
| 38 | Tray icon, window geometry, quit-to-tray | ✅ | ⛔ | — |
| 39 | Offline/error surfacing, retry, backoff | ✅ | ✅ same | M1 |
| 40 | Multi-window (player, soundboard, presentation… as OS windows) | ✅ | 🟡 native tabs + full-screen modes (one window) | M1 |

**Parity score for a completed effort: ~38/40 full or equivalent, 2 disabled
where the OS has no equivalent** (global hotkeys, tray) — the same rule the
desktop already applies to macOS/Wayland.

---

## 7. Phases, deliverables, exit criteria

Estimates are rough **developer-weeks for one experienced dev**, including tests
and docs, excluding app-store review time.

### M0 — Shared-core extraction (no user-visible change) · 2–3 w ✅ **done**
- **Delivered**: `SongRequest.Core` (net10.0) now holds the engines, services,
  models, the interop dispatcher and the platform abstractions. `IUiShell`
  replaces `NewUiWindow` in the host contract (`ISongRequestHost.NewUiRef`).
  Provider hooks were added for audio output + device list
  (`AudioOutputFactory`), audio decode (`AudioSourceFactory`), MIDI
  (`MidiServiceFactory`), system media session (`MediaSessionFactory`),
  global hotkeys (`GlobalHotkeyBackendFactory`) and the tray
  (`TrayIconFactory`); `AudioDevices`/`NullAudioOutput` keep the engines working
  with no backend. The desktop implements them in `DesktopPlatform.Register()`,
  called by `Program.Main` and by both test hosts, so behaviour is unchanged.
- **Exit (verified)**: all 6 RIDs publish with 0 warnings/errors; 454 unit +
  44 integration + 33 JS tests green (coverage 53.3 % / 23.1 %, floors 36/17);
  live smoke run unchanged (UI, home feed, artwork, playback, position updates).
- **Deviations from the original bullet list**: `AutoStart` stayed in Core (it is
  already cross-platform and inert on mobile because `IsSupported` is false);
  `IUpdateProvider`/`IToolProvider` were deferred to M8/M5 — nothing needs them
  before the mobile head exists. `Platform/Tools` (ffmpeg/yt-dlp downloader) also
  stayed in Core: pure managed code, hidden on mobile by the settings UI.

### M1 — Mobile shell + bridge + Android skeleton · 3–4 w ✅ **done**
- **Delivered**:
  - `SongRequest.Mobile` (MAUI; `net10.0-android`, plus `net10.0-ios` on macOS):
    `MainPage` with a native tab bar (Requests / Play / Board / Stage) and one
    `HybridWebView` per page, created lazily with an LRU budget of 2 so a phone
    never keeps six web views resident; `MobileShell : IUiShell` maps the
    desktop's sub-windows onto tabs/fullscreen and `AppHost` wires the same
    host + interop objects the desktop uses, with per-page response routing.
  - **Bridge**: `hostbridge.js` gained the HybridWebView transport
    (`HybridWebViewMessageReceived` + `SendRawMessage`), a single `sendToHost`
    helper and automatic `data-shell="mobile"` marking; every page now sends
    through it instead of repeating the Photino/WebView2 fallback chain.
  - **Responsive UI**: `mobile.css` (scoped to `[data-shell="mobile"]`) — single
    column, 16 px inputs, ≥40 px touch targets, sheets instead of dialogs,
    compact player bar, safe-area insets. Font Awesome is **vendored** (404 KB,
    woff2) so icons work offline on both hosts; page images are referenced
    relative to `newui/` so one asset tree serves both apps.
  - **Sandbox paths**: `AppPaths.UseDataRoot(root)` moves downloads, caches,
    library and config under the app sandbox (`FileSystem.AppDataDirectory`);
    `LibraryService` follows it. The desktop never calls it, so its relative
    `data/downloadedvideos` behaviour is unchanged.
  - **CI**: `build-android` job (ubuntu) publishing `SongRequest-android.apk`,
    required by `release` and attached to the GitHub release.
- **Exit (verified)**: Android APK builds with **0 warnings** (35.6 MB,
  arm64-v8a + x86_64) and contains the shared UI (30 `assets/wwwroot` files);
  **run on an Android 16 (API 36) emulator** — installs, starts, walks all four
  tabs (requests list, home/queue/player, pad grid, presentation + QR) with no
  crashes, offline icons/artwork and the shared version number; desktop
  unchanged — 503 .NET + 39 JS tests green, 0 warnings, all 6 RIDs publish,
  live smoke run of the player *and* the soundboard unchanged. Full checklist in
  [`MOBILE-TESTING.md`](MOBILE-TESTING.md).
- **Deviations**: the stage/visualization tab is re-pointed rather than adding a
  sixth tab; the mobile mini-player (native, survives tab switches) is deferred
  to M2 where the audio backend lands; the Settings tab became the existing
  settings sheet inside the Requests page (one WebView fewer).

### M2 — Audio playback, background, media session · 4–5 w ✅ **done**
- **Delivered**:
  - `AndroidAudioOutput` (`SongRequest.Mobile/Platforms/Android/Audio/`): a
    streaming `AudioTrack` (float PCM, 16-bit fallback, ~250 ms buffer) fed by a
    dedicated render thread that pulls the engine's mixer — so crossfade,
    normalisation, ducking and the visualisation tap are unchanged. Audio focus
    (pause on loss, resume on transient gain, duck on `CAN_DUCK`) and output
    routing (`SetPreferredDevice`) live here too.
  - `AndroidAudioSource`: `MediaExtractor` + `MediaCodec` → float32 samples with
    frame-based position accounting, seek (`SeekTo` + `Flush`), and an internal
    mono→stereo/resample chain when a decoder reports a different layout. The
    engine's own `GetMixerInput` still handles 48 kHz→44.1 kHz and mono→stereo, so
    the exposed format stays the track format (cheap enough for library scans).
  - `AndroidAudioDevices`: `AudioManager.GetDevices(Outputs)` mapped onto the
    core's device numbering, so the player's device menu works like the desktop's.
  - System media controls: the framework `MediaSession` (lock screen, headset and
    Bluetooth buttons, "media button session") + a **foreground playback service**
    (`mediaPlayback` type) with a `MediaStyle` notification (title/artist/artwork,
    prev / play-pause / next, seek bar). Wired through the existing
    `MediaSessionFactory`/`MediaSessionBridge` seam, so the settings UI reports it
    like it reports MPRIS/SMTC.
  - `MainPage` now marshals every shell entry point onto the UI thread: the interop
    dispatches from worker threads, and creating a web view there has no Looper.
- **Deviations**: (1) soundboard clips and the announcement chime keep their *own*
  outputs (each is an `AudioTrack`) exactly as on the desktop, instead of being
  mixed into the engine — the mixer unification is deferred to a later phase since
  it would change desktop behaviour; (2) the media session uses the framework
  `MediaSession` rather than Media3, because Media3's session wants an ExoPlayer
  `Player` we deliberately do not use.
- **Exit (verified on an Android 16 emulator)** — see
  [`MOBILE-TESTING.md`](MOBILE-TESTING.md) for the method: a 6-file library scan
  (AAC 44.1 kHz stereo, MP3 48 kHz, AAC mono) indexed correctly; playback with the
  position advancing 5 → 61 s; `seek(0.5)` landing exactly on 45 s of a 90 s track;
  `skipNext` advancing the queue and restarting at 0 s; the media notification
  present with three actions and a live seek bar; the foreground service running
  with `types=0x2` (mediaPlayback); and **playback continuing after HOME** (the
  position kept advancing while backgrounded). No crashes; 0 build warnings.

### M3 — Library, playlists, home feed, settings parity · 2–3 w ✅ **done**
- **Delivered**:
  - **Import music** (`importLibrary`, files or a folder): the host picks with its
    native dialog (Android SAF tree picker / MAUI file picker), copies the music
    into `<data root>/music` (the only place the scanner can read on mobile) and
    scans it. The desktop shares the command and scans the picked folder in place.
    The UI has an "Import Music" card in Settings → Library and an "Import music"
    button in the home feed's empty state.
  - **Portable library paths**: songs and artwork under the data root are stored
    as `~/…` and resolved against the current root on load, so the library
    survives a sandbox move (iOS changes its container on reinstall). Paths the
    user owns stay absolute; the root is matched in every spelling the platform
    uses (`/data/data/<pkg>` vs `/data/user/0/<pkg>`).
  - **Artwork without FFmpeg**: `AndroidArtworkConverter` (BitmapFactory,
    subsampled decode → JPEG) is registered through the existing
    `ThumbnailStore` seams, so embedded cover art is downscaled in process.
  - **Empty/offline state**: the feed answers from the local library count, so an
    empty library shows the empty state (with its import button) immediately
    instead of a spinner that never clears; forced reloads supersede in-flight
    requests, and a `libraryChanged` event keeps the player's feed in step with an
    import made on another page.
  - **Settings on mobile**: desktop-only cards (Startup & Window, External Tools)
    and the typed scan-folder row are hidden — deterministically in `mobile.css`
    (so an unreachable server cannot leave dead options on screen) and again from
    the capability flags (`isMobile`, `selfUpdateSupported`) the interop reports.
    The Settings button is reachable from the top bar again.
  - The notification permission is now requested when playback first starts, not
    at launch, so a system dialog never blocks the first interaction.
- **Exit (verified on an Android 16 emulator)**: a SAF folder import copied and
  scanned 203 files in seconds (well under the 60 s budget for 200), the 200
  identical test files deduped by hash to one song, metadata came from the tags
  ("Cover Song"/"Test Artist"), embedded artwork was converted and rendered in the
  feed, paths on disk are portable (`~/music/…`, `~/library/thumbnails/…`),
  playlists and play history work, and the empty state appeared before the import
  and cleared itself afterwards. Desktop unchanged: 512 .NET + 39 JS tests green,
  0 warnings, all RIDs publish.

### M4 — Soundboard, MIDI, remote, announcements, share · 3–4 w ✅ **done**
- **Delivered**:
  - **MIDI backend** (`AndroidMidiService` over `MidiManager`): USB and Bluetooth
    LE controllers, hot-plug callbacks, device enumeration, input/output ports,
    note/CC/pitch-wheel sending. The byte→message mapping lives in the core
    (`MidiBytesParser`, including running status), so the soundboard pads and the
    remote-control mappings behave exactly as they do with RtMidi on the desktop.
    `MidiServiceFactory` gained a `BackendSupported` seam so the settings UI and
    the integration tests report the *registered* backend (the desktop keeps its
    ALSA-sequencer guard).
  - **Soundboard import on mobile**: "Add Sound" and "Add files" use the async
    platform pickers; the soundboard's own copy step puts the sound in the app's
    data folder (verified on device: `files/data/soundboard/pad.wav` +
    `soundboard_config.json`).
  - **Announcements**: the chime now ships with the app and is materialised into
    the data root at startup (`AppPaths.ChimePath`), so ducking + chime work on
    mobile exactly as on the desktop.
  - **Announcement page reachable**: it shares the stage tab (it is a
    press/release control panel), and its messages are routed to it.
  - **Hidden-view refresh nudge**: Android pauses JavaScript in a hidden web view,
    so a page could miss host events (and its promises time out) while another tab
    was in front. Showing a page now sends it a `refresh` event, which the pages
    answer by re-requesting their state.
- **Deviations**: MIDI *hardware* latency (< 60 ms with a real pad controller)
  and a second device receiving a Music Share stream cannot be exercised on an
  emulator — both need real hardware and a reachable companion server. Everything
  up to the device boundary is verified (below), and the byte mapping is
  unit-tested.
- **Exit (verified on an Android 16 emulator)**:
  - Soundboard: imported a sound through the system picker (copied into the app
    data folder), assigned it to a pad with fade-in/out, and **played it to
    completion with live progress** (`soundboardPlayback` events advancing
    0 → 1.58 s → 2.00 s of a 2 s clip, then draining).
  - MIDI: the backend initialises, enumerates (0 devices on the emulator),
    accepts enable/disable, applies device selections without error, and a
    mapping created exactly as the assign dialog does it is **persisted**
    (`{"Channel":0,"Note":60,"MessageType":"NoteOn",…,"IsConfigured":true}`).
  - Announcements: the chime was extracted, decoded (`mime=audio/mpeg`, 1.88 s)
    and played to EOF; the duck/state API responds.
  - Music Share: reports a graceful error without the companion server.
  - Remote control: `applyMidiDevices` succeeds; global keyboard shortcuts are
    correctly reported as unsupported on mobile.

### M5 — Downloads, cookies, offline · 2–3 w ✅ **done**
- **Delivered**:
  - **In-app YouTube sign-in**: the mobile host hosts Google's login in a web view
    and harvests its cookie store (`CookieManager`) when the user taps Done. The
    cookies go through the same parser/storage the desktop's paste flow uses
    (`YoutubeCookieService`) and are applied to the live `YoutubeService`, so the
    login survives a restart.
  - **Startup flow per platform**: `getAuthBrowsers` now reports the host's
    browsers *and* whether in-app sign-in exists; mobile shows a "Sign in with
    YouTube" button instead of the browser list, and the tools prompt is skipped
    because `getToolStatus` reports `supported: false` (`ToolManager.IsSupported`).
  - **AAC/m4a downloads**: `DownloadedAudio.PreferredContainer = "mp4"` on mobile
    makes the stream selection prefer the AAC-in-MP4 audio stream, and new
    downloads are written as `<id>.m4a` — the platform decoders play it natively,
    with no muxing step and no FFmpeg. Existing `.mp3` downloads are still found
    (a song's file is looked up under every extension ever used), so a library
    built by the desktop is untouched.
  - **Background downloads**: a `dataSync` foreground service with a progress
    notification, started/stopped by the interop's download-activity hooks (both
    the request pipeline and the direct "download & play" path).
  - **Offline playback**: downloaded songs live in the app's data folder and play
    from there with no network — verified in airplane mode.
- **Deviations**: the *auto-download of server requests* (the request pipeline)
  needs the companion server, which the emulator cannot reach; the same pipeline
  was therefore exercised through its "download & play" entry point, which shares
  the download, thumbnail, metadata, progress and activity-hook code. A real
  Google sign-in needs credentials; the harvest mechanism is verified with the
  cookies Google sets on the login page itself.
- **Exit (verified on an Android 16 emulator)**:
  - **Download**: `iuJDhFRDx9M` → `/…/downloadedvideos/iuJDhFRDx9M.m4a`
    (4.16 MB) with progress events, the real title/artist from YouTube metadata,
    auto-added to the library — and **no muxing step** (the AAC stream is the file).
  - **Playback of the download**: the platform decoder opened it
    (`mime=audio/mp4a-latm`, 257 s) through `AudioTrack` — no FFmpeg involved.
  - **Offline**: with airplane mode on, the app started, showed the downloaded
    song with its artwork, and played it from local storage.
  - **Sign-in**: the in-app page loaded Google's login, "Done" harvested 4
    cookies (`__Host-GAPS`, `OTZ`, `__Secure-ENID`…), they were stored in
    `youtube_auth.json` and applied to the service.
  - **Startup**: no browser-import step and no tools prompt on mobile.

### M6 — Visualisation, presentation, TV · 2–3 w ✅ **done**
- **Delivered**:
  - **A stage server** (`StageServer`): the app serves the *same* presentation page
    over the local network, so any TV browser (or a cast tab) shows the stage with
    no receiver app and no backend changes. The page is served unchanged — only its
    host bridge is swapped by an injected `bridge-http.js` that polls `/state` — so
    the TV view cannot drift from the desktop's presentation.
    Routes: `/` (page + bridge), the web root for relative assets, `/state`
    (now playing, queue, lyrics, settings — built from the engine, with artwork via
    the shared `ThumbnailStore` and lyrics via the shared `LyricsService`) and
    `/command` (the page's own requests).
  - **The web assets are extracted once** from the app package into the data root
    (`StageAssets`), because assets inside an APK are not readable as files.
  - **External display (HDMI/USB-C)**: when a second display is connected, the
    presentation is mirrored onto it through a `Presentation` window with a web
    view pointed at the same server URL (`StageDisplay`).
  - **The phone advertises the URL**: the presentation page shows a small "Open on
    a TV" footer with the LAN address and a QR code (generated with the same
    QRCoder the desktop uses), shown only when a host publishes one.
  - **Visualisation**: the page and the engine tap are shared with the desktop; the
    stage tab shows it full-screen, and the fullscreen button hides the native tab
    bar through the existing `IUiShell` seam.
  - **Audio chunk size**: the output's read chunk also sets the visualisation's
    ceiling (the tap fires per read). 8192 samples capped it at ~10 fps; it is now
    1024 (~12 ms), leaving the 30 fps target headroom.
- **Exit (verified on an Android 16 emulator)**:
  - The stage server listens and reports the phone's **LAN address**
    (`http://10.0.2.16:38905/`), and Chrome — an independent client, exactly like a
    TV — rendered the presentation page from it: the logo, the request QR and the
    live "now playing / up next" state, driven by the HTTP bridge.
  - The visualisation received the engine's audio frames (`sampleCount` 1024 each,
    canvas sized to the screen). Measured **~10 fps in the software-rendered
    emulator**, where the page's phone-sized canvas is the bottleneck; the frame
    source and the 33 ms throttle are the shared, desktop-verified code, so 30 fps
    on real GPU hardware remains the target (noted, not claimed).
  - No second display exists on an emulator, so the HDMI path is code-complete but
    needs real hardware to confirm (`StageDisplay` logs "no second display" and the
    phone keeps working).

### M7 — iOS port · 3–4 w
- `SongRequest.Platform.iOS` (AVAudioEngine, AVAssetReader, CoreMIDI,
  `MPNowPlayingInfoCenter`), `net10.0-ios` target, macOS CI, signing,
  `PrivacyInfo.xcprivacy`, TestFlight build.
- **Exit**: same M1–M6 acceptance on an iPhone/iPad; no decode gaps for the
  default download format; background audio survives 20 min locked.

### M8 — Release engineering, QA, polish · 2–3 w

**Post-porting regression fixes** ✅:
- **Thumbnails stopped loading after the first library save** (both platforms): the
  portable-path change rewrote `ThumbnailPath` to `~/…` for the file but restored
  only `FilePath` in memory, so `ThumbnailStore` (which reads the path from disk)
  found nothing until a restart. Both paths are restored now; a regression test
  covers the save→memory→reload round trip. Existing `library.json` files were
  written correctly, so a restart (or this build) brings the artwork back.
- **Desktop YouTube downloads of some videos failed with
  `VideoUnavailableException`** ("unauthenticated"): the cookies were in fact
  applied correctly (verified: the app imports 23 cookies including `SID`,
  and the same requests fail with the *full* 671-cookie jar too). YouTube now
  serves those videos only to clients that solve its current player challenges —
  `dQw4w9WgXcQ` still downloads fine with no cookies at all. The app therefore
  falls back to **yt-dlp** (already bundled for cookie import) when the direct
  client reports a video unavailable, reusing the app's cookies and reporting its
  progress into the same UI. Without yt-dlp (mobile), the original error stands.
- **Mobile showed "Authentication Required" with a correct token**: a token pasted
  without the `Bearer ` scheme (easy on a phone keyboard) produced an invalid
  `Authorization` header and a 401; saving settings also never cleared the overlay
  (a successful reload does not hide it). Tokens are normalised to
  `Bearer <token>` when saved, and saving settings clears the overlay before
  reloading.

**Tray, download-cache and UI-refresh fixes** ✅ (found while hardening the
port; they affect the desktop app too):
- **Linux tray icon disappeared after a crash/forced exit**: the crash guard wrote
  a `tray-disabled.marker` when a start that had *ever* created a tray was
  interrupted twice — the "a tray was used" marker from an old successful run was
  never cleared, so an unrelated crash loop disabled the tray permanently. A clean
  start now clears that marker (`StartupGuard.Complete`), `--gpu` clears both the
  safe-mode and tray markers, and a clean tray creation is logged
  (`[SongRequest] tray: created`). The settings toggle still re-enables it.
- **Safe mode was permanent**: once the webview had crashed twice in a row, the
  software-rendered path (DMA-BUF and compositing disabled — noticeably less
  smooth) stayed on until `--gpu` was passed. It is now retried after
  `SafeModeRecoveryStarts` (3) clean starts; a renewed crash re-enters it.
- **Download pipeline no longer re-does cached work**:
  - Audio: every download entry point (`DownloadAndProcess`, `AddVideoPanel`, the
    library/music-player import) first checks for an existing file in every
    container ever used, and re-checks it *after* waiting for the download
    semaphore — two paths for the same request could previously both download and
    overwrite. Stub files left by an interrupted download (< 16 KiB) are treated as
    missing so they heal instead of being played forever.
  - Metadata: reused from `VideoCache` when title + duration are known (one
    `Videos.GetAsync` per request instead of one per pass).
  - Thumbnails: one cache (`AppPaths.ThumbnailCachePath`, `.jpg`) shared by the
    song lists, download pipeline and library import; the deterministic
    `i.ytimg.com` URL replaces a full metadata request per card, duplicate cards
    coalesce their in-flight fetch, and the image is downloaded once per video
    (stage 5 no longer fetches it again).
  - Lyrics/captions: `VideoData.LyricsText`/`LyricsFetched`,
    `SubtitleText`/`SubtitleTimedText`/`SubtitlesFetched` make repeat lookups
    instant — including "no captions", which used to be retried every time.
- **Automatic send-in downloads report progress**: the fetch loop's download
  (`AddVideoPanelAsync`) now emits the same `downloadProgress` events as a manual
  queue action (metadata/downloading/complete/error) through a new
  `ISongRequestHost.EventSink` wired to the interop, so a new request no longer
  downloads silently. Failed downloads retry on later polls, bounded to three
  attempts per run.
- **Send-in list refresh is now stable**: the 30 s poll and the host `refresh`
  events (which fire on every metadata cache save) rebuilt all three lists, which
  flickered, reloaded artwork and reset scroll. Host refreshes are debounced, a
  reload requested during a fetch is queued instead of dropped, a list is only
  re-rendered when its rows/search/sort actually changed, background refreshes no
  longer write status/log lines, download progress updates every card of the same
  request, action buttons ignore a second click while their request is in flight,
  and a long download no longer trips the 60 s request timeout (30 min for
  `downloadAndProcess`). The word filter no longer re-registers its click handler
  on every poll.

**Mobile shell polish** ✅:
- **Tabs keep their state**: all four tab pages (plus the alternate stage pages)
  stay resident. The old two-view LRU reloaded a page — losing scroll position,
  search text, active sub-tab and filters — as soon as a third tab had been
  opened. Hidden views are now only dropped when Android reports memory pressure
  (`MainActivity.OnTrimMemory`), and when the budget is exceeded the alternate
  stage pages (visualization/announcement) are evicted before a tab destination.
- **Material Design 3 navigation bar**: 80dp surface-container bar with four equal
  destinations, a 64×32 active indicator in the secondary-container role, 24dp
  icons and 12sp labels (on-surface / on-surface-variant), a short press
  animation and accessibility labels. Icons use the Font Awesome solid TTF the
  web UI already vendors (`Resources/Fonts/FaSolid.ttf`, OFL text beside it).
- **Tab activation no longer rebuilds unchanged pages**: the player feed and the
  soundboard grid re-render only when the library/state really changed; the
  presentation page re-requests its current state (now playing, lyrics, settings).
- **Presentation zoom (50–200%)**: the stage page has on-screen − / 100% / +
  controls and `+`/`-`/`0` shortcuts. The value is stored in
  `ConfigService.PresentationZoom`, so the in-app stage tab, the HDMI mirror and
  a TV browser agree: `/state` carries it, a TV can change it through
  `POST /command` (the only command the stage server accepts from the LAN), and
  the in-app page is updated by broadcast. The stage content scales from the
  top-left (`--pres-zoom`), while the controls stay at normal size.

**Mode-aware mobile UI** ✅:
- **Operating mode drives the navigation.** `IUiShell.SetOperatingMode(StartupMode)`
  is applied from `completeSetup`, and `MainPage` rebuilds the bar for it:
  - Music Player → **Home / Library / Player / Queue** (all one touch-first page),
  - Song Requests → Requests / Player / Board / Stage,
  - Soundboard → Board / Player / Stage,
  - Music Share keeps the desktop player page (its share modal lives there).
- **`newui/mobilemusic.html` is a purpose-built touch UI** (not the desktop layout in
  a phone): compact 44px headers, one search bar, horizontally scrolling rows,
  a compact mini player for browsing, and a right-hand **queue sidebar** (the Queue
  tab opens it; tapping it again or the scrim closes it and returns to the
  previous tab).
- **Expanded player**: lyrics hidden by default; large centred artwork over the
  animated two-colour background, title/artist below, seek bar below, controls at
  the bottom. The Lyrics toggle (top right) shrinks the artwork into the top-left,
  moves title/artist beside it and gives the rest of the screen to the lyrics
  (synced highlighting + auto-scroll).
- **Navigation bar colours follow the song**: the page extracts the same two
  dominant colours the player background uses and sends them to the shell
  (`setNavColors`); the native M3 bar animates its surface tint, active indicator
  and active icon/label to them (Material colours when nothing is playing).
- **Automatic downloads now honour "add to library"**: the fetch loop raised no
  library import at all (only the manual download button did). `HeadlessHost`
  raises `SongDownloaded`, the interop imports the song (with cached artwork) and
  notifies the music page — on both platforms.

**Runtime mode switching + mode-specific UI** ✅:
- The operating mode is now stored (`ConfigService.OperatingMode`), applied from
  `completeSetup`, and switchable at runtime with `setOperatingMode` (Settings →
  Operating Mode, the music page's settings sheet, and the Stage console). The
  mobile navigation rebuilds instantly; the desktop brings the mode's window
  forward. No restart.
- Navigation per mode: Music Player → Home/Library/Player/Queue/Stage, Music Share
  → Share/Player/Queue/Stage, Soundboard → Board/Player/Stage, Song Requests →
  Requests/Player/Board/Stage.
- The touch music page gained the player's advanced features: playback sheet
  (volume, crossfade, normalization, audio output device), Presentation /
  Visualization / Music Share / Announcements, shuffle + repeat, song actions on
  long-press (play now / play next / queue / add to playlist / remove), playlist
  creation and YouTube search + download.
- Music Share has a real mobile surface (start/stop sharing, session code, join
  with a 6-digit code, receive audio, volume) instead of the desktop modal.
- The Stage tab is a dedicated **console** (`mobilestage.html`): live 16:9 preview
  of the served presentation, TV/HDMI link with QR + copy, zoom (50–200%, 50%
  default on mobile), source selection and the operating-mode switch. The preview
  loads the LAN URL from the WebView (mixed content allowed for it).
- **Fixes in this round**:
  - Player artwork for downloaded requests: the third artwork fallback still looked
    in a hardcoded `data/downloadedvideos/thumbnail-cache` relative to the
    executable; it now uses `AppPaths.ThumbnailCachePath` (`.jpg`/`.png`), which is
    where mobile stores them. Library songs were unaffected because they have a
    stored `ThumbnailPath`.
  - Soundboard file selection: the editor passed the picker's `content://` URI to
    `File.Exists`; a new `PrepareSoundFile` seam copies it to a readable file with
    its real name before the soundboard imports it.
  - Android in-app updates: releases were signed with a fresh debug key on every
    CI run, so Android rejected them ("App not installed"). CI now uses a release
    keystore from secrets when available and otherwise caches the debug keystore so
    it stays stable. The updater also verifies the download (size, zip header),
    the package name, version code and signing certificate before launching the
    installer, with actionable messages instead of Android's generic failure.
  - Mobile presentation default zoom is now 50% (configurable per user; the stage
    console shows and changes it).

**In-app updater (Android) — done ahead of the rest of M8** ✅:
- `UpdateService` knows the mobile platforms: `CurrentPlatform()` returns
  `android`/`ios`, and the expected release asset is `SongRequest-android.apk`
  (the CI job already attaches it). `SelectAsset` therefore finds the APK.
- Android self-updates: `SelfUpdateSupported` is true there and the host registers
  `UpdateService.PackageInstaller`, which downloads the release APK with real
  progress and hands it to the system package installer through a `FileProvider`
  (`AndroidApkUpdater`). The user confirms in Android's own prompt; the app never
  installs silently. iOS keeps `SelfUpdateSupported = false` — updates belong to
  the App Store.
- The update UI (startup prompt and About panel) already renders the release notes
  as Markdown and shows a progress bar; `checkForUpdates` now also reports
  `installKind` (`package` on Android) so the flow says "Android will ask you to
  confirm the install" and lets the app continue, instead of staying paused for a
  desktop-style self-restart.
- **Cleartext hosts**: Android blocks plain HTTP by default, which would have made
  self-hosted (LAN, http://) companion servers unreachable *and* broken the APK
  download over http. A network security config now permits cleartext, matching
  what the desktop has always allowed; everything the app talks to on its own
  (GitHub, lyrics, YouTube) stays HTTPS.
- **Verified on an Android 16 emulator**: `checkForUpdates` reported
  `platform: android`, `installKind: package` and the Markdown release notes; a
  real 36 MB APK was downloaded from a local server with progress events
  (10 → 30 → 50 → 70 → 90 % → "Opening the installer…") and the **system package
  installer opened** (`PackageInstallerActivity`), which is where the user
  confirms.
- Release workflow extension (APK/AAB/IPA artifacts, `versionCode` mapping),
  store listings, privacy policy, data-safety declarations, device matrix
  testing, crash/perf dashboards, docs (`docs/MOBILE.md`, README section).
- **Exit**: one push that bumps `<Version>` publishes desktop archives **and**
  mobile artifacts through the existing release job; `Tests` (desktop) and
  `Tests-Mobile` both required and green.

**Touch variants for every mode** ✅:
- **Song Requests** now has a purpose-built `mobilerequests.html` (+
  `mobilerequests.css`/`.js`). It keeps the complete desktop feature set — All
  Songs / Pending / Blacklist, search + sort, approve/unapprove/blacklist/delete,
  queue/play-next, Submit Song (URL and YouTube search), the word filter, the
  dashboard/console, Settings, About/updates and the startup flow. Cards open a
  Material bottom action sheet; dialogs (Settings, Submit, About, key/MIDI
  capture) are full-screen pages with an app bar and back button, not modal
  boxes. The desktop keeps `index.html` untouched.
- **Soundboard** is now a real Material page app (`mobilesoundboard.html` +
  `mobilesoundboard.css`/`.js`): app bar, page chips, touch pads, a single bottom
  action sheet, and **dedicated pages** for editing a sound, managing pages,
  grid size, MIDI, keyboard shortcuts, MIDI assign and key capture. It supports
  all desktop features (file/YouTube import, colours, icons, fades, playback
  modes, per-pad volume/device, MIDI mapping/assign/feedback, pages, grid,
  shortcuts, master volume, output device, stop all) and swipe paging.
- The soundboard file picker now uses a dedicated single-file, `audio/*` picker
  and materialises provider results with no usable `FullPath`; selecting a sound
  file applies on mobile.
- Player artwork now resets **before** the new thumbnail is applied, so the
  expanded player and the mini player update immediately on a song change
  instead of waiting for a tab switch/state catch-up.
- Both pages are wired into `MainPage` (Requests/Board destinations in the
  request and soundboard modes) and the host routes broadcast events and
  soundboard playback to them.
- **Playlist contents can be browsed and modified on mobile**: tapping a playlist
  opens a detail view with Play/Shuffle, per-entry move up/down and remove, plus
  rename, add-songs and delete in its action sheet. The player's repeat button now
  reports `aria-pressed` and follows the host's `repeatEnabled` state.
- **Stage/TV fixes**: the HTTP bridge diffs each section instead of the whole
  `/state` document, so the Up Next list and lyrics are not rebuilt every tick
  (lyrics now advance with the clock); library artwork is resolved through the
  shared thumbnail store; `/state` uses the field names `presentation.js` reads
  and supplies a server-generated QR data URL, so a TV with no internet still
  shows the request QR instead of a white tile.

- **Settings & search polish**: the Requests settings page is a real Material
  page (two-column tab grid, one scrolling surface, sticky footer, M3 fields and
  cards) instead of a horizontally scrolling modal. Library import/scan now
  emits `libraryScanProgress` with the current file to both the requests page and
  the player, and the player's settings sheet shows a progress bar/current item
  and the final result. The player search has an explicit YouTube toggle (and
  keeps the “search YouTube for …” fallback), so external tracks are reachable
  from the same search field as the library.
- **Blacklist metadata**: blacklisted ids have no download/panel that would
  populate the video cache, so `FetchDataAsync` now fetches their title, creator
  and duration once (bounded concurrency, `MetadataFetched` persisted). Desktop
  and mobile blacklist rows show real metadata instead of only artwork + id.
- **Startup & navigation**: the native tab bar stays hidden until the launch
  mode and cookie decision are complete. If Android recreates the Requests
  webview (memory pressure/tab switch), the page skips the splash/launch/auth
  sequence; tab destinations are also no longer evicted by the memory-trim
  callback.
- **Android signing**: the CI fallback key is now generated once in
  `.android-signing/debug.keystore` and cached there. The old cache pointed at
  `~/.android/debug.keystore`, but .NET Android actually writes the debug key
  under `~/.local/share/Xamarin/Mono for Android/`, so every run generated a new
  key and in-app updates were rejected. Repository secrets still take precedence
  for a proper release keystore.

- **Onboarding & settings polish**: the startup splash/update/launch/auth flow is
  now a Material page on mobile (safe-area insets, cards, progress, 2-column mode
  picker). The requests tab switcher is a wrapping icon grid instead of a
  horizontal scroller, the settings page uses a two-column tab grid and fills the
  viewport (no dead space under the save bar), and the Remote Control tab renders
  each mapping as a mobile card with labelled key/MIDI fields.
- **Automatic cookie import**: `AutoGrabCookies` is a persisted setting (desktop
  and mobile). On desktop the startup flow and browser selection import cookies
  automatically; on mobile the in-app sign-in page watches successful YouTube
  navigations and presses Done once the signed-in session cookies exist.
- **Playback / presentation fixes**: the last queue song now refills the next slot
  from the playlist before the crossfade check, and loudness analysis no longer
  blocks a transition (it runs in the background), so queue → playlist crossfades
  start on time. Switching the stage source back to Player now re-pushes the
  player state instead of leaving the stale Music Share view. Presentation zoom
  controls hide until hovered on mouse devices and stay available on touch/TV.
- **Library / soundboard fixes**: removing a song from the library works even when
  it is still in the sent-in list (the file is kept for sent-in, the library row
  is removed). Async interop responses (soundboard file picker, library import,
  sign-in) now keep their per-page response target across awaits, which fixes
  selecting a local sound file from the soundboard editor.

- **Visualization / soundboard**: the mobile stage visualization now uses the same
  Material app bar, mode-chip grid and canvas layout as the rest of the mobile UI.
  Soundboard deletes use an in-app confirmation sheet (Android WebView's
  `confirm()` was unreliable), and Android soundboard clips no longer request
  audio focus per instance — overlay clips now mix instead of pausing/restarting
  each other. Overlay has a dedicated engine test.
- **Requests / library**: request and blacklist rows gained **Play Now** on both
  desktop and mobile (downloads if needed, then replaces the queue and starts).
  Library/playlist deletes on mobile use the same confirmation sheet. Playlist
  **Play/Shuffle** now starts the first track immediately instead of only filling
  the playlist buffer. Recently Played rows show “Played … ago” timestamps.

**Milestone totals for the chosen full-parity programme:** Android-only through
M6 ≈ 16–22 dev-weeks; +iOS ≈ 19–26; +M8 ≈ 21–29. (M0–M2 alone — shared core,
mobile shell, native audio — is a usable "control the queue and play from the
phone" build at ≈ 9–12 dev-weeks, which is the first point a real device set can
be run; it is a checkpoint on the way to parity, not a separate scope.)

---

## 8. Testing strategy

Keeps the desktop guarantees, extends them:

- **Shared logic**: the existing unit projects reference `SongRequest.Core`
  unchanged; the ISuite stays the desktop `Tests` check. Add
  `tests/SongRequest.Platform.Tests` (net10.0) for adapter logic that can be pure:
  PCM conversion/clamping, buffer sizing, MIDI byte↔event mapping, storage
  path/URI mapping, `versionCode` mapping, update-provider no-op.
- **JS**: extend `tests/js/hostbridge.test.js` with the HybridWebView transport,
  plus the existing `vizmath`, `markdown`, `keys` suites (unchanged).
- **Integration on device** (new `Tests-Mobile` CI job, Android emulator on
  Linux with KVM, plus a small real-device smoke set):
  decode a real m4a + mp3 + webm/opus, seek, output start/stop, crossfade between
  two 3 s tracks (the desktop has exactly this test — port it), background
  service start/stop, media-session round-trip, MIDI loopback via a virtual port.
- **UI**: Playwright-style automation is not available for MAUI; use the
  existing web UI tests against the **same pages served headlessly** (they are
  plain HTML/JS — run them in Chromium in CI) plus manual device checklists per
  milestone (`docs/MOBILE-TESTING.md`).
- **Performance gates**: cold start < 2.5 s (mid-range), playback battery
  < 8 %/h, soundboard latency < 60 ms, home feed < 4 s cold / < 500 ms warm,
  APK size < 90 MB, no ANRs in a 1-hour soak.
- **Test seams** already in place (`ConfigAccessor`, `Delay`,
  `SourceFactory`, `OutputFactory`, `OnSamplesCaptured`, `ProcessReceivedChunks`)
  are what make the mobile adapters testable; keep them.

---

## 9. CI/CD and release

Extend `.github/workflows/release.yml` (existing `build` → `test` → `version` →
`release` chain, auto-release when `<Version>` is untagged):

```yaml
build-android:            # ubuntu-latest (Android SDK + JDK 21 preinstalled)
  run: dotnet workload install maui-android   # 'android' if MAUI workload is unavailable
  run: dotnet publish SongRequest.Mobile -f net10.0-android -c Release \
         -p:AndroidPackageFormat=apk -p:AndroidKeyStore=true …   # signed with a repo secret
  artifacts: SongRequest-android.apk        # arm64-v8a + armeabi-v7a + x86_64 for emulators

build-ios:                # macos-latest + Xcode; needs an Apple Developer account
  run: dotnet publish SongRequest.Mobile -f net10.0-ios -c Release -p:BuildIpa=true
  artifacts: SongRequest-ios.ipa            # then uploaded to TestFlight (internal testers)
```

- Artifacts join the existing `release` job; update the "6-archive guard"
  (`release.yml` currently fails if fewer than 6 `SongRequest-*` files are in
  `dist/`) to the new count so a partial run still fails loudly.
- **Version mapping** (implemented in M1): `<Version>` lives in
  [`Directory.Build.props`](../Directory.Build.props) and is inherited by the
  desktop host, `SongRequest.Core` and the mobile host alike; CI reads it from
  there. Android `versionName`/iOS `CFBundleShortVersionString` follow it
  automatically (`ApplicationDisplayVersion=$(Version)`), while the Android
  `versionCode` stays an explicit, monotonically increasing integer
  (major×10000 + minor×100 + patch).
- **Signing**: Android keystore and the iOS certificate/provisioning profile live
  in GitHub secrets. One signing identity per platform — no store/full split.
- **TestFlight**: uploaded with `xcrun altool`/`notarytool` or Fastlane
  (`upload_to_testflight`), to **internal testers** first (no Beta App Review);
  external tester groups are a separate, later decision (§10).
- Note on CI capacity: the current `build` matrix already uses macOS runners for
  osx RIDs, so the iOS job does not introduce a new class of runner.

---

## 10. Legal, policy and licensing (decisive constraints)

1. **Apple App Store guideline 5.2.3 — audio/video downloading**: apps "should
   not … include the ability to save, convert, or download media from third-party
   sources (such as … YouTube …) without explicit authorization". **Decided**: the
   iOS app is **not submitted to the App Store**; it is distributed through
   **TestFlight** and Ad Hoc/Xcode. Nuance to plan around: Apple runs a (lighter)
   **Beta App Review** for **external** TestFlight testers, while **internal
   testers** (up to 100, App Store Connect users) get builds with no review — so
   external TestFlight groups are opened only if Beta Review clears it, with
   Ad Hoc/enterprise (or a self-signed build) as the fallback for wider
   distribution.
2. **Google Play**: IP/abuse policies plus the general "facilitate copyright
   infringement" line, enforced post-publish. **Decided**: the Android app is
   distributed as an **APK on GitHub Releases** (the same channel the desktop
   archives already use), not on Play. If a Play listing is ever wanted, it is a
   *new* decision that reopens the downloader-disabling work (one injection
   point, §7 M5) — the code keeps the downloader behind a single seam so that
   stays cheap.
3. **FFmpeg licensing**: if we ever bundle FFmpeg, ship an **LGPL** build
   (no `--enable-gpl`, dynamically linked, licence text + source offer). GPL
   builds are a distribution/compliance hazard for store apps. The recommended
   path (§3.2) avoids FFmpeg on mobile entirely by using platform decoders and
   the m4a/AAC download format — also avoiding the post-FFmpegKit
   ([retired Jan 2025, archived Jul 2026](https://github.com/arthenica/ffmpeg-kit))
   DIY-build burden.
4. **Codec patents**: AAC/MP3 patent pools have expired; keep to platform codecs
   and royalty-free formats where possible (one more reason to prefer platform
   decoders over a bundled FFmpeg with extra codecs).
5. **Play compliance deadlines (2026)**: target **API 36** (Android 16) — required
   for new apps/updates since 31 Aug 2026; **16 KB page size** support for native
   libraries (enforced since Nov 2025; Google's current guidance lists 1 Feb 2027
   as the final date for remaining updates) — every `.so` we ship (even
   `libopenal`/`libminiaudio` if used) must be built with 16 KB alignment.
6. **Privacy/data safety**: `PrivacyInfo.xcprivacy` (iOS), Play Data Safety form,
   microphone (announcements), notifications, Bluetooth/USB (MIDI), network
   security config for LAN HTTP to the companion server
   (`usesCleartextTraffic`/ATS exception for the user's own host), and the
   YouTube cookie storage (already in app data).
7. **Branding/trademark**: keep the YouTube name usage factual ("plays audio from
   YouTube") and never imply Google endorsement.

---

## 11. Risks and mitigations

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| R1 | **Store rejection over YouTube downloading** | High | **Removed from the critical path**: distribution is GitHub APK + TestFlight/Ad Hoc (§10), no store submissions. Keep the downloader behind one injection point so a future Play listing stays cheap |
| R2 | Audio glitches/latency on Android (underruns, OEM power management) | High | Low-latency `AudioTrack` + tuned buffers; foreground service with `mediaPlayback`; the `IAudioOutput` seam allows dropping in miniaudio/OpenAL Soft; soak tests per phase |
| R3 | `HybridWebView` is young (experimental-era API; .NET 11 stabilises trimming/AOT support) | Medium | Keep `newui/` framework-agnostic; bridge is ~40 lines; documented fallback = thin native heads (option F) with the same core/services; pin the MAUI workload version |
| R4 | iOS decode gap (no WebM/Opus) | Medium | Download m4a/AAC by default; import path warns/converts; optional audio-only LGPL FFmpeg later |
| R5 | iOS build/dev requires macOS + paid Apple account | Medium | Android-first order; the iOS phase is scheduled after Android parity; GitHub macOS runners already in the release matrix |
| R6 | Desktop regressions from the core extraction | High | M0 is behaviour-preserving only; full desktop test suite + live smoke as the exit gate; extraction is a move, not a rewrite |
| R7 | Scoped storage / sandbox path changes | Medium | Import-into-app-sandbox as the default; relative paths in `library.json`; SAF URIs only for in-place access; test iOS reinstall |
| R8 | Background execution limits (Android 12+ FGS restrictions, iOS suspension) | Medium | FGS type declared + started from a user action; iOS category `.playback` with `audio` background mode; degrade gracefully |
| R9 | Library lyrics/thumbnails depend on FFmpeg and the `.lrc` sidecar path is currently dead code | Low | `ThumbnailStore` already degrades without a converter; wire `LyricsService.GetLrcFileLyrics` in (already implemented, no callers) so both mobile and desktop gain offline lyrics |
| R10 | App size / battery | Low–Medium | No bundled FFmpeg in the MVP; single WebView resident; AOT/trimming once the HybridWebView trim story allows it; measure per milestone |
| R11 | CI cost and flakiness (emulators) | Low | Emulator smoke tests only; the bulk of logic stays in desktop-runnable unit tests |
| R12 | Two UIs drifting (desktop vs mobile CSS) | Medium | One `newui/` tree, one bridge protocol, one JS test suite; mobile CSS is additive and gated by `data-shell` |

---

## 12. Decisions (locked) and remaining questions

**Locked (agreed for this plan):**

| Decision | Choice | Consequence |
|---|---|---|
| Distribution | **GitHub Releases APK + TestFlight (internal) / Ad Hoc**; no public store submissions | No downloader flag, no second `ApplicationId`, no store listings; policy work = privacy/security declarations + TestFlight metadata; R1 leaves the critical path |
| Scope | **Full feature parity in one programme** (M0–M8) | Phase boundaries remain shippable checkpoints; M0–M2 is the first real-device validation point |
| Audio stack | **Platform-native** (`AudioTrack`/`AVAudioEngine`, `MediaCodec`/`AVAssetReader`) behind the existing interfaces | No bundled native audio library; `IAudioOutput`/`IAudioSource` keep miniaudio/OpenAL Soft available as a contingency |
| Delivery order | **Android to parity first, then iOS** | No macOS or Apple account needed for M0–M6; iOS reuses the proven core, bridge, UI and acceptance criteria |

**Still open (needed before or during the phase noted):**

1. **Repo layout** (M0): same repository/solution — one `SongRequest.Core` and two
   hosts (recommended here) — or a separate mobile repository consuming the core
   as a package.
2. **Stage/TV priority** (M6): is HDMI external-display output enough, or is
   Chromecast required (adds a binding for a commercial iOS pod plus a registered
   Cast receiver app id; the backend-hosted presentation page is the cheaper
   alternative)?
3. **Minimum OS versions** (M1): Android 8.0 (API 26) vs **Android 10+ (API 29,
   recommended)**; iOS 15 vs **iOS 16 (recommended)**.
4. **External TestFlight testers** (M8): stay internal-only, or accept Apple Beta
   App Review for external groups (§10.1)?

---

## 13. Appendix — APIs, packages, references

**NuGet (mobile)**
- `Microsoft.Maui.*` (net10.0), `Microsoft.Maui.Controls.HybridWebView` (in-box)
- Android: `Xamarin.AndroidX.Media3.Session`, `.ExoPlayer`/`.Common` (only if we
  use Media3 for playback; the MVP uses `AudioTrack`),
  `Xamarin.GooglePlayServices.Cast.*` (optional, §4.7)
- iOS: bindings are in-box (`AVFoundation`, `MediaPlayer`, `CoreMidi`,
  `UniformTypeIdentifiers`); Cast would need a custom binding
- `CommunityToolkit.Maui` (optional: pickers, status bar), `Plugin.LocalNotification` (optional)
- Keep: `Newtonsoft.Json`, `YoutubeExplode`, `TagLibSharp`, `NAudio.Core`, `QRCoder`

**Platform APIs used**
- Android: `Android.Media.AudioTrack`, `AudioManager`/`AudioFocusRequest`,
  `MediaExtractor`, `MediaCodec`, `Android.Media.Midi.MidiManager`,
  `DisplayManager`+`Presentation`, `UsbManager`, `CookieManager`,
  `AndroidX.Media3.Session.MediaSession`, `NotificationCompat`
- iOS: `AVFoundation.AVAudioEngine`/`AVAudioSession`, `AVAssetReader`,
  `CoreMidi`, `MediaPlayer` (`MPNowPlayingInfoCenter`, `MPRemoteCommandCenter`),
  `UIKit.UIScene` (external display), `AuthenticationServices`, `WebKit`

**Facts verified while writing this plan**
- `HybridWebView` in .NET 10: raw string messages (`HybridWebView.SendRawMessage`
  in the page, `RawMessageReceived` in C#), `InvokeJavaScriptAsync`,
  `WebViewInitializing/Initialized` events, `AddHybridWebViewDeveloperTools`,
  framework script `_framework/hybridwebview.js`; not available under full
  trimming/Native AOT until .NET 11.
- .NET 10 mobile: `net10.0-android36`, JDK 21, `net10.0-ios` (iOS 12.2 floor,
  Xcode 26 toolchain); iOS uses NativeAOT by default, Android uses Mono +
  profiled AOT with CoreCLR experimental (default in .NET 11); MAUI can build
  **Android** workloads on Linux (iOS needs macOS).
- Play: target API 36 by 31 Aug 2026 (extensions to 1 Nov 2026); 16 KB page
  support for native libs since Nov 2025 / final 1 Feb 2027.
- FFmpegKit retired Jan 2025 (archived Jul 2026); continuation is source-only
  (`FFmpegKitNext`); OpenAL Soft and miniaudio both support Android + iOS.
- Apple guideline 5.2.3 (audio/video downloading) quoted in §10.
- Every current non-native dependency of the engine is `netstandard2.0`/`net10.0`
  and therefore mobile-compatible (checked in the restored NuGet cache).
