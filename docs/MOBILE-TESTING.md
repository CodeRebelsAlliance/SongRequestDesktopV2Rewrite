# Mobile testing & toolchain

How to build, test and verify the mobile hosts (plan:
[`MOBILE-APP-PLAN.md`](MOBILE-APP-PLAN.md), architecture:
[`ARCHITECTURE.md`](ARCHITECTURE.md)).

---

## 1. What runs where

| Layer | Where it runs | Command |
|---|---|---|
| `SongRequest.Core` logic | **desktop** (hermetic, CI `Tests` job) | `./tests/run-tests.sh` |
| Web UI + host bridge (`newui/*.js`) | **desktop** (Node) | `node --test tests/js` (part of `run-tests.sh`) |
| Desktop host (Photino) | desktop, 6 RIDs | `dotnet publish SongRequestDesktopV2Rewrite/… -r <rid>` |
| Mobile host (MAUI) | Android emulator/device; iOS on macOS | see §2 |

The point of the split: **mobile must not need a device to stay green**. Engine,
interop, library, soundboard, recommendations, lyrics and the bridge protocol are
all covered by the desktop suites; the mobile projects add only the platform
adapters (audio, MIDI, media session, pickers) and the shell.

## 2. Building the mobile host

### Android (Linux, Windows or macOS)

```bash
dotnet workload install maui-android          # once per machine/CI runner
dotnet build SongRequest.Mobile/SongRequest.Mobile.csproj -f net10.0-android
dotnet publish SongRequest.Mobile/SongRequest.Mobile.csproj -c Release \
    -f net10.0-android -p:AndroidPackageFormat=apk -o publish-android
```

The Android SDK and a JDK are needed. On a machine without them, let the SDK
provision them (this is what this repo's development machine used — the system
SDK is not writable there, so a user-local .NET was installed first):

```bash
# user-local SDK when /usr/share/dotnet is not writable (no sudo):
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
"$HOME/.dotnet/dotnet" workload install maui-android

# Android SDK + JDK (downloads ~1.5 GB):
"$HOME/.dotnet/dotnet" build SongRequest.Mobile/SongRequest.Mobile.csproj \
    -t:InstallAndroidDependencies \
    -p:AndroidSdkDirectory="$HOME/Android/Sdk" \
    -p:JavaSdkDirectory="$HOME/Android/JDK" \
    -p:AcceptAndroidSDKLicenses=true
```

Install on a device/emulator:

```bash
adb install -r publish-android/net.bycra.songrequest-Signed.apk
adb logcat -s SongRequest:D mono-stdout:D   # app + console output
```

> **CI note (workload install failures).**
>
> - `Version X of package microsoft.net.sdk.*.manifest-10.0.100 is not found in
>   NuGet feeds`: the advertised workload *set* is ahead of nuget.org (recent sets
>   reference an unpublished iOS manifest). Switch to manifests mode
>   (`dotnet workload config --update-mode manifests`) and install `maui-android`
>   normally — it only resolves Android/Maui/Mono, never iOS.
> - `Unable to find package Microsoft.NETCore.App.Runtime.Mono.linux-x64`: the
>   Android workload is too old (36.1.2, the version bundled with the SDK). It
>   predates the fix that stops `dotnet publish` from adding the Linux host RID.
>   **Do not** install with `--skip-manifest-update`; the manifest update installs
>   36.1.118, which is fixed.

### iOS (macOS only)

`net10.0-ios` is only added to `TargetFrameworks` when building on macOS (the
iOS workload does not exist elsewhere), so the same project builds Android-only
on Linux/Windows without touching the file.

```bash
dotnet workload install maui-ios
dotnet build SongRequest.Mobile/SongRequest.Mobile.csproj -f net10.0-ios \
    -p:CodesignKey="Apple Development: …" -p:CodesignProvision="…"
```

## 3. Device checklist per milestone

Automated tests cannot cover a device's audio stack, background rules or the
web view. These are the manual checks that close each milestone (run on the
oldest and newest supported OS: **Android 8 (API 26) → 16 (API 36)**,
**iOS 15 → 18**).

### M1 — shell, bridge, UI — ✅ verified on an Android 16 (API 36) emulator

Run on the built APK (`net.bycra.songrequest-Signed.apk`, arm64 + x86_64):

- [x] App installs and starts; the startup launcher, the YouTube-auth step and
      the optional-tools prompt all render and are dismissible.
- [x] Version badge shows the shared `<Version>` — the app is not offered
      the desktop installer, and `installUpdate` is refused on mobile.
- [x] Tabs switch: Requests → Play → Board → Stage → back; each page renders in
      the HybridWebView and keeps its state (lazy views, LRU of 2).
- [x] Requests tab: top bar, tab pills (scrollable), search + sort, list area,
      status bar with the disconnected state.
- [x] Play tab: sidebar strip, home hero, queue sheet, player bar, transport.
- [x] Board tab: 4×3 pad grid, toolbar, master volume + device selector.
- [x] Stage tab: presentation page with the request QR code.
- [x] No crashes / `FATAL` in `adb logcat` during the whole walk-through.
- [x] Icons (vendored Font Awesome) and artwork render offline.
- [ ] Settings modal on device, rotation, and a *reachable* server (the emulator
      cannot see the LAN host the app is configured for, so the requests list
      showed the connection-error state instead of live data).
- [ ] Playback is expected to *fail* in M1 (no audio backend yet) — M2.

Follow-ups found during the device run (tracked in the plan):
the desktop-only startup steps (import YouTube cookies from a browser, download
FFmpeg/yt-dlp) are meaningless on a phone and must be replaced by the in-app
login + platform decoders (M5); the empty/offline library shows "Loading your
library…" indefinitely instead of a friendly empty state (M3).

### M2 — audio, background, media session — ✅ verified on an Android 16 emulator

Method: the APK was driven through the app's own interop bridge with a temporary
page hook that logged host events (`console.log` → `logcat`), so the run was
repeatable and needed no manual tapping. `adb shell dumpsys` confirmed the service
and notification; `adb shell dumpsys activity services` reported
`isForeground=true types=0x00000002` (mediaPlayback).

- [x] Library scan of 6 files (AAC 44.1 kHz stereo, MP3 48 kHz, AAC mono) → 6 songs.
- [x] Playback: the reported position advanced 5 → 61 s at real-time rate.
- [x] Seek: `seek(0.5)` on a 90 s track reported exactly 45 s afterwards.
- [x] Crossfade setting read (4 s) and applied (2 s).
- [x] Track advance: `skipNext` moved the queue (2 → 1) and restarted at 0 s.
- [x] Media notification: title/artist, prev / play-pause / next, live seek bar.
- [x] Foreground playback service running with the mediaPlayback type.
- [x] **Background playback**: after HOME the position kept advancing (40 → 61 s).
- [ ] Still to check on real hardware: screen *off* for 20 min, Bluetooth/headset
      unplug, audio focus from another app, volume keys, battery drain, 1 h soak.
- [ ] Soundboard clips and the announcement chime on a device (they use their own
      `AudioTrack`s; the desktop path is unchanged).

### M3 — library, settings, home feed — ✅ verified on an Android 16 emulator

- [x] **Import a folder through SAF**: the picker granted access, 203 files were
      copied into the app sandbox and scanned (`filesFound 203`, `songsAdded 203`,
      no errors) in seconds.
- [x] **Dedupe + metadata**: 200 byte-identical test files collapsed to one song
      (hash dedupe); the tagged MP3 showed "Cover Song"/"Test Artist".
- [x] **Portable paths on disk**: `library.json` stores `~/music/M3Music/…` and
      `~/library/thumbnails/…`, so a moved sandbox still resolves (unit-tested too).
- [x] **Artwork without FFmpeg**: embedded cover art was extracted by the scan and
      converted by `AndroidArtworkConverter` into a JPEG data URL shown in the feed.
- [x] **Empty state**: with an empty library the feed shows "Your library is empty"
      plus the Import button at once (no stuck spinner), and it clears itself after
      the import (`libraryChanged` reaches the player page).
- [x] **Settings on mobile**: the Settings sheet opens from the top bar; the
      Import Music card is present; Startup & Window, External Tools and the typed
      scan-folder row are hidden.
- [x] **Playlists + history**: create playlist → add song → list shows
      `M3 Test Playlist/1`; `getPlayHistory` returns entries.
- [ ] Still to check on real hardware: importing from Google Drive/other providers,
      a 5000-file folder, and the same flow on iOS (M7).

### M4 — soundboard, MIDI, announcements, share — ✅ verified on an Android 16 emulator

- [x] Soundboard import: the system picker returned a file; it was copied into the
      app's data folder (`files/data/soundboard/pad.wav`) and its duration read.
- [x] Pad assignment + fades persisted (`soundboard_config.json`: sound `pad.wav`,
      `FadeIn`/`FadeOut` true).
- [x] Pad playback: `soundboardPlayback` events advanced through a 2 s clip
      (0 → 1.58 s → 2.00 s → drained) — fades and the audio path included.
- [x] MIDI plumbing: the `MidiManager` backend initialises, enumerates devices,
      accepts enable/disable and applies device selections; a mapping made exactly
      like the assign dialog's is persisted with `IsConfigured: true`.
- [x] Announcements: the chime ships with the app, is materialised into the data
      root, decodes (`mime=audio/mpeg`, 1.88 s) and plays to EOF; the state API
      answers.
- [x] Music Share without the companion server: a graceful error, no crash.
- [ ] **Needs real hardware**: a USB/BLE pad controller firing a soundboard button
      (and the < 60 ms latency target), a controller driving remote control, and a
      second device receiving a Music Share stream (needs a reachable server).

### M5 — downloads, sign-in, offline — ✅ verified on an Android 16 emulator

- [x] **Download**: a YouTube video downloaded through the app's own pipeline as
      `iuJDhFRDx9M.m4a` (4.16 MB, AAC) with progress events, real metadata and
      auto-add to the library — no muxing step.
- [x] **Playback of the download**: the platform decoder handled it
      (`mime=audio/mp4a-latm`, 257 s) via `AudioTrack`.
- [x] **Offline**: airplane mode on → the app started, showed the song with its
      artwork and played it from local storage.
- [x] **In-app sign-in**: Google's login rendered in the app; "Done" harvested 4
      cookies, stored in `youtube_auth.json` and applied to the service.
- [x] **Startup**: no browser-cookie import step and no tools prompt on mobile.
- [ ] **Needs the companion server**: the automatic download of *requests* (the
      same pipeline, entered from the request flow) and a real Google sign-in
      (credentials).

### M6 — visualisation, presentation, TV — ✅ verified on an Android 16 emulator

- [x] **Stage server**: it listens on the phone's LAN address and serves the
      presentation page, its assets, `/state` and `/command`.
- [x] **A TV-like client**: Chrome (an independent browser) opened the URL and
      rendered the presentation — logo, request QR, now-playing/up-next from the
      HTTP bridge.
- [x] **Visualisation**: the engine's frames reach the page (1024 samples each,
      canvas sized to the screen) and the stage tab shows it full-screen.
- [ ] **Real hardware**: the HDMI/USB-C second-display path (`StageDisplay`) and a
      real TV browser over the LAN; the 30 fps target on GPU hardware (the emulator
      managed ~10 fps with software rendering).

### In-app updater (Android) — ✅ verified on an Android 16 emulator

- [x] `checkForUpdates` is mobile-aware: `platform: android`,
      `assetName`/`downloadUrl` from the release's `SongRequest-android.apk`, and
      `installKind: package`.
- [x] The release notes come back as Markdown and render in both the startup
      prompt and the About panel.
- [x] A real APK downloaded with progress (10 → 90 % → "Opening the installer…").
- [x] The **system package installer** opened (`PackageInstallerActivity`) — the
      user confirms there; nothing installs silently.
- [ ] Still to check on real hardware: installing over the previous version (the
      emulator session was abandoned), and the "install unknown apps" prompt on a
      device that has never allowed it.

### Post-porting regressions — ✅ covered

- [x] Library artwork survives saves (unit test: save → in-memory paths stay
      absolute → disk stays portable → reload resolves).
- [x] Bearer tokens are normalised to `Bearer <token>` on save (unit test), and
      saving settings clears the auth overlay.
- [x] YouTube downloads of challenge-protected videos fall back to yt-dlp with
      progress (verified end-to-end: a video that fails through the direct client
      downloads through the fallback).

### Downloads, tray and UI refresh — ✅ covered

- [x] An existing download is reused (unit test: `DownloadVideoAsync` returns the
      file before YouTube is contacted; the whole pipeline succeeds from cache for
      a downloaded song with no network).
- [x] Thumbnails are served from `AppPaths.ThumbnailCachePath` (unit test), and the
      thumbnail URL is derived from the id (no metadata request).
- [x] Lyrics come from `VideoData` when already looked up (unit test).
- [x] A clean start clears the "tray was used" marker; safe mode is retried after
      three clean starts (unit tests).
- [x] Linux smoke test: start the app, expect `[SongRequest] tray: created` and no
      `*.marker` files left in `~/.local/share/SongRequestDesktopV2Rewrite`.

### Shell polish — ✅ verified on the emulator

- [x] Bottom bar renders as an M3 navigation bar (active pill indicator, icon +
      label, correct colours) and each tab switches instantly.
- [x] State survives tab switches: type in the Requests search box, visit Play and
      Board, come back — the text and active chip are still there (the old build
      reloaded the page).
- [x] Stage zoom: `−`/`100%`/`+` scale the presentation, the value is stored
      (`PresentationZoom` in `config.json`), and after a restart the page restores
      it. `/state` reports it and `POST /command {"method":"setPresentationZoom",
      "zoom":1.2}` from a browser updates all screens; `/command` ignores every
      other method.
- [x] A TV browser (Chrome on the emulator) renders the served page with the
      zoom control and the stored scale.

### Mode-aware UI — ✅ verified on the emulator

- [x] Choosing **Music Player** at startup switches the bottom bar to
      Home / Library / Player / Queue, landing on Home with its search bar.
- [x] Player layout: big centred artwork, title/artist, seek, controls; Lyrics
      hidden until the top-right toggle is pressed, then artwork shrinks to the
      top-left and the lyrics area fills the screen.
- [x] Queue opens as a right-hand sidebar over the current screen; the Queue tab
      toggles it and closing restores the previous tab.
- [x] Automatic request downloads appear in the library when
      *Settings → Library → add downloads automatically* is on (host event
      `SongDownloaded`).

### Runtime modes, stage console and fixes — largely verified on the emulator

- [x] Switching the operating mode from the music settings sheet or the Stage
      console rebuilds the bottom navigation and the open page without a restart.
- [x] Stage console: Start/Stop publishes the LAN URL with QR, the live 16:9
      preview loads at the stored zoom, and the default zoom is **50%**.
- [x] Player artwork for request downloads now resolves via
      `AppPaths.ThumbnailCachePath` (code path; needs a downloaded request to see).
- [x] Android update signing: CI must keep one key. Add `ANDROID_KEYSTORE_BASE64`,
      `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`
      secrets, or rely on the cached debug keystore. The first release after this
      change may still need one manual install (old key vs new key).
- [x] Soundboard file pick: `PrepareSoundFile` materialises `content://` picks.

### Touch variants, playlists and stage link — automated + device checklist

- [x] `node --test tests/js` covers the new bridge diff (`stagebridge.test.js`)
      and the mobile page wiring (`mobilepages.test.js`).
- [x] Desktop regression suite green with the new pages: 512 unit, 44/45
      integration (1 environment skip, FFmpeg/cookies), 54 JS tests; mobile
      Android Release build 0 warnings.
- [x] **Requests (device)**: Requests/Pending/Blacklist/Word Filter/Dashboard
      tabs scroll as chips; tapping a card opens the M3 action sheet; Submit Song,
      Settings, About and the startup launch flow open as full-screen pages with
      a back button.
- [x] **Soundboard (device)**: pads are touch-sized, the board has an app bar and
      page bar, long-press opens the action sheet, and Edit sound / Pages / Grid
      size / MIDI / Shortcuts / Assign / Key capture are real pages with back
      navigation; swipe left/right changes page.
- [x] **Sound file picker (device)**: Browse opens a single audio picker
      (`audio/*`), the picked provider file is materialised into the cache, and
      Save applies it to the pad (title auto-filled when empty).
- [x] **Artwork (device)**: change track while on Home/Library; the mini player
      artwork updates immediately, and opening the Player tab shows the same
      artwork without switching away first.
- [x] **Playlists (device)**: tap a playlist → detail view; Play/Shuffle, move
      entries up/down, remove, rename, add songs and delete all round-trip
      through `getPlaylist` / `reorderPlaylistEntry` / `updatePlaylist` /
      `deletePlaylist`.
- [x] **Repeat**: the player button exposes `aria-pressed` and stays in sync with
      `repeatEnabled` from the host's `queueUpdate`/`musicPlayerReady` payload.
- [x] **Stage/TV**: with the stage running, load the LAN URL in a browser: Up
      Next keeps its thumbnails without flicker/tick rebuilds, lyrics highlight
      advances, and the request QR renders (server-generated data URL, no CDN).
- [x] **Startup/navigation**: the native tab bar is hidden during splash → mode
      → auth; after completing setup it appears. Recreating the Requests webview
      (memory pressure or a tab round-trip) skips the onboarding sequence.
- [x] **Settings/import**: the Requests settings page uses a two-column tab grid
      and one scrolling M3 surface with no dead space; importing music shows a
      progress bar with the current file in both Requests settings and the
      player's settings sheet, followed by the result summary.
- [x] **YouTube search (player)**: the Home search field has a YouTube toggle;
      typing searches YouTube directly and results download/play like the
      desktop search view.
- [x] **Blacklist metadata** (desktop + mobile): blacklisted ids are enriched
      with title/creator/duration on first fetch and cached; rows no longer show
      only artwork and the id.
- [x] **Android signing**: CI generates/caches one fallback key at
      `.android-signing/debug.keystore`; re-running the workflow produces the
      same certificate (or uses the repository release keystore secrets).
- [x] **Onboarding**: splash/update/launch/auth are Material pages on mobile; the
      native navbar stays hidden until setup completes, then appears in the chosen
      mode.
- [x] **Auto cookie import**: enable the setting, pick a browser and reload the
      desktop app — cookies import without pressing “Grab cookies”. On mobile,
      sign-in continues automatically once the signed-in YouTube page has loaded.
- [x] **Requests tabs/settings**: tabs wrap as an icon grid; settings uses a
      two-column tab grid, fills the viewport and has no dead space below the
      footer; Remote Control mappings render as labelled cards.
- [x] **Soundboard file pick**: Browse → choose an audio file → the response
      reaches the soundboard page and the file name/selected state is applied.
- [x] **Library removal**: removing a sent-in song from the library removes the
      row and keeps the file for sent-in.
- [x] **Crossfade**: last queue song → first playlist song starts a crossfade
      before the track ends; loudness analysis no longer delays it.
- [x] **Stage source**: Player → Music Share → Player restores the player
      track/lyrics; zoom controls are hidden until hovered on desktop.
- [ ] Still to check on real hardware: HDMI second display, a TV browser over
      Wi-Fi, long-press timing on low-end phones and the MIDI assign page with a
      physical controller.

## 4. Known toolchain constraints

- **iOS builds need macOS** — the workload is unavailable elsewhere; the
  project's target list is conditioned on the host OS.
- **`UseMaui` no longer implies package references** (since .NET 8): the project
  references `Microsoft.Maui.Controls` explicitly, so the build is warning-free.
- **JDK version**: .NET for Android expects JDK 17/21; a newer system JDK may
  warn. The provisioning command above installs a matching JDK into
  `~/Android/JDK`.
- **Play Store / App Store**: the app is distributed as a GitHub APK and through
  TestFlight/Ad Hoc (see the plan §10) — no store submission is planned, so the
  downloader needs no build flag.
