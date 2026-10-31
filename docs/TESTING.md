# Testing Plan

Goal: **every feature has an automated test, so regressions are caught by CI**,
not by users. Three suites are implemented (unit, integration, JavaScript); this
document records the seams, the tooling, a feature-by-feature matrix and the
remaining work. For how to run them, see [`../tests/README.md`](../tests/README.md).

---

## 1. Current state

- **`tests/SongRequestDesktopV2Rewrite.UnitTests/`** (xUnit, `net10.0`) — **362 tests**.
- **`tests/SongRequestDesktopV2Rewrite.IntegrationTests/`** — **39 tests** (FFmpeg
  decode/seek, OpenAL Soft null driver, library scan/import/missing, tagged-lyrics,
  RtMidi, interop event bus, Linux MPRIS on the session bus, loopback checks that
  backend requests carry the identifying User-Agent; skipped automatically when
  FFmpeg/devices/a bus are absent).
- **`tests/js/`** — **10 tests** via `node --test tests/js`: `keys.test.js`
  (keyboard-gesture mapping) and `hostbridge.test.js` (the Photino host↔page
  message bridge).
- CI (`.github/workflows/release.yml`) runs a **`Tests` job** (unit + integration +
  JS + coverage floors) alongside the multi-platform `build`/`release` jobs, and
  writes a detailed failure report to the job summary.
- The architecture is testable: platform work sits behind `IAudioSource`,
  `IAudioOutput`, `IMidiService`; most business logic lives in plain classes.

Run everything locally:
```
./tests/run-tests.sh
```

### Implemented so far

| Area | Covered |
|---|---|
| Lyrics | `LyricsQueryNormalizer` title/artist/noise handling |
| MIDI | `MidiEventParser` + `MidiMessage` mapping |
| Auth cookies | `YoutubeCookieService` parse (Netscape + header), save/load/clear |
| Paths/models | `AppPaths`, `Song`, `VideoCache`, `SongRequestEngine` |
| Config | `ConfigService` defaults, patching, persistence, `PropertyChanged` |
| Music engine | queue/playlist/crossfade/volume/normalization/queue-timings |
| Soundboard | format detection, page/button model, master-volume clamp, pages, button update; **fade in/out applied to audio** (silent start + ramp, ramp-down before stop, loop re-fade, `FadeDurationMs = 0` disables) |
| Audio core | `AudioClip` metadata + `InMemoryAudioSource` read/seek/clamp/volume/independent cursors |
| Announcement | state machine, duck/restore, chime, push-to-talk vs toggle, settings |
| Visualization | frame format, 8-bit quantization/clamp, 30 fps throttle, dispose |
| Music Share | player warm-up, volume clamp/forwarding, stop |
| Remote control | MIDI mapping match; **action dispatch** (volume/crossfade/announcement toggles/trigger+release); **feedback snapshot** (values, change-suppression, no-output) |
| Lyrics | `ParseSyncedLines` (mm:ss + hh:mm:ss), `.lrc` file reading (synced/plain/missing), live fetch via stub `HttpClient` (success + 404); embedded TagLib lyrics (integration) |
| Server requests | `FetchingService.SendRequest`: endpoint/URL, auth header, multipart field per action (`word`/`sendin-allowed`/`now-playing-*`/`ytid`), error wrapping |
| Music Share | player warm-up, volume clamp/forwarding, stop; service queue/status transitions (`buffering`↔`streaming`), receive events, drain, stop |
| Updates | `UpdateService` version + platform/asset selection (per-RID `.zip`/`.tar.gz`); `UpdateInstaller` verify (magic/truncation), extract (zip + tar.gz), staged progress (`starting`…`done`/`error`), backup+rollback+relaunch script content, `DownloadAsync` streaming |
| Startup / window | `AutoStart` builders (Windows command, Linux `.desktop`, macOS plist) + paths; `AppLaunchArgs` `--minimized`; `Config` startup/window defaults + persistence |
| Tray | `TrayMenu` commands, `NullTrayIcon`, `TrayIconFactory` support |
| Global hotkeys | gesture parse/normalize + key tables (Windows VK, X11 keysym), action map, `GlobalHotkeyService` registration/dispatch, `RemoteControlService` hotkey → action |
| External tools | `ToolManager` per-platform sources (ffmpeg/ffprobe/yt-dlp), install orchestration (archive + raw binary, chmod), `ToolLocator` search, `ToolStatus` flags, `DownloadFileAsync` progress; interop `getToolStatus`/`downloadTools` |
| Linux tray (integration) | AppIndicator bindings load + all symbols resolve (`libayatana-appindicator3`/`libappindicator3`); factory support matches availability |
| System media (unit) | `MediaSessionBridge` engine→session (metadata/state/position, de-dup) and session→engine (play/pause/toggle/next/prev/stop/seek/volume/raise/quit); `MediaArtwork` data-URL decode, resolution order, video id, stable key; `NullSystemMediaSession`; `WindowsSmtcLoader` degradation + companion metadata contract (name / `ISystemMediaSession` / `Create` / Windows target) |
| Windows packaging (unit) | "Apps & features" registry values (name/publisher/version/uninstall strings/NoModify/NoRepair/InstallDate preserved), uninstaller script (registry + autostart cleanup, `/quiet`, confirmations, quoting of `&` in paths, self-delete), version parsing, install-folder size |
| Backend requests (unit + integration) | every backend call carries the identifying `User-Agent` (`SongRequest Desktop v<version>`) and bearer token — `BackendHttpClient` header application (unit) and the exact wire value received by a loopback listener from `FetchingService` and an interop command (integration) |
| Linux webview env (unit) | WebKitGTK/DMA-BUF mitigation matrix (`ComputeVariables`), NVIDIA/Wayland detection, safe-mode escalation after repeated interrupted starts, `--gpu`/`--safe-mode`/env overrides, user-set variables never overridden; `NativeEnvironment` proves values reach native `getenv()` |
| Photino host bridge (JavaScript) | `hostbridge.js` registers the host→page handler by *calling* `window.external.receiveMessage(callback)` (assigning to it replaces Photino's shim and silently breaks every response), falls back to `chrome.webview`, and reports when no channel exists |
| Visualization math (JavaScript) | `vizmath.js`: log-spaced band edges are strictly increasing (integer edges made the first 8 bars share one FFT bin, then 4, then 3…), each bar gets its own value, the frequency tilt keeps the bass from towering over the spectrum (a 1/f spectrum: bass/mid 16.3x + only 22/64 bars visible before, 2.0x + 64/64 after), and the waveform column→sample mapping spans the whole width (the old walk stopped at the buffer length: 51 % of the view) |
| Changelog rendering (JavaScript) | `markdown.js`: release notes are Markdown from GitHub — headings, bullet/ordered lists (incl. nesting, loose lists and task lists), emphasis, inline code, fenced code (verbatim, unformatted), links (unsafe schemes dropped), quotes, rules, tables, paragraphs; raw HTML is escaped, never executed; and the exact `## Changes` + `- commit` shape the release workflow produces renders as a heading with one list |
| Cookie import (unit) | `BuildYtDlpArguments` keeps the export to a single simulated/flat entry (pointing yt-dlp at the bare homepage walks the logged-in "recommended" feed and never finishes), YouTube-only domain filtering, and an import timeout below the UI's request timeout |
| Cookie import (integration) | real yt-dlp plumbing: a hung import is killed and reported before the UI gives up, and an unknown browser fails fast with a clear error |
| MIDI backend guard (unit) | `MidiServiceFactory` support matrix — RtMidi's ALSA backend *aborts the process* without `/dev/snd/seq`, so MIDI is reported unsupported there; override seam keeps the unit suite hermetic; `NullMidiService` no-op behaviour; soundboard state builds with no MIDI backend |
| Linux desktop integration (unit) | generated `.desktop` entry (name/`Exec` quoting/`Icon`/`StartupWMClass`/categories), XDG data-home path, and that the app-id/icon calls are safe no-ops when unavailable |
| Cookie browser preference (unit) | `saveCookieBrowser` persists the chosen browser and both `getStartupData` and `getSettings` report it, so the startup prompt and the settings picker default to it |
| Interop null tolerance (unit) | `saveSettings`/`saveRemoteConfig` accept `null` values from the web UI (a `<select>` with no MIDI device sends NaN → `null`, which previously failed the save with "Can not convert Null to Int32") |
| Playback tick (unit) | the engine drives its own `MusicEngine.ProcessTick()` while playing (the crossfade mixer pads with silence, so the output can never report EOF), so the music player gets position/now-playing updates and the queue advances at end of track |
| Queue timings (unit) | `ComputeQueueTimings` sets "Playing Now" + cumulative wait times, and the queue notification carries *fresh* wait times — the newly added (or shifted) item included, not the stale default from before the change |
| Recommendations (unit) | `RecommendationEngine`: the documented five weighted signals, affinity decay (recent plays beat stale ones), artist/genre ranking, fresh-discovery metadata quality, tag/album similarity, echo-chamber discount for its own skipped suggestions, missing files never suggested, the 3 most recently played excluded, scores 0–1 and ordered, count/limit, and a documented `Signal` + non-empty `Reason` per suggestion |
| Artwork store (unit + integration) | `ThumbnailStore`: converts artwork once and serves it from memory afterwards, honours the per-call conversion budget (feed-style calls pass 0 and get "not ready yet" + a background queue instead), inlines small artwork without a converter, skips oversized artwork when there is no converter, handles embedded-art bytes, prewarms in the background, and survives a failing converter; the integration test runs the real FFmpeg and asserts the result is smaller than the source (guards the converter arguments) |
| Playlist ordering (unit) | `LibraryService.MarkPlaylistPlayed` records the timestamp the home feed orders playlists by (and leaves untouched playlists at "never played"); the summary exposes `lastPlayedUtc` |
| Output device (unit) | `MusicEngine.SetOutputDevice` stores the choice when idle and re-opens the *running* output on the new device without restarting the track (`SetOutputDevice`, `getMusicAudioDevices`, `setMusicOutputDevice` interop contract) |
| Sub-window lifecycle (unit) | `StartupGuard` tray limits (`TrayDisabled`, `TrayUsedLastRun`, crash recovery) and that changing the tray settings clears the guard so the tray can be retried |
| Download gate (unit) | automatic downloads are deferred until the user made the YouTube-cookie decision (or cookies are saved); skipped ids stay out of `fetchedYtids` so the next fetch retries them; `completeSetup` opens the gate |
| System media (integration) | Linux **MPRIS**: service registration, per-interface `GetAll`/`Get`, metadata/state/position/artUrl round-trip, every transport command, `PropertiesChanged` signal |
| Library | playlist CRUD, play history, persistence across instances |
| Interop bus | request/response dispatch; read-only + safe-stateful command tables; **event envelope** (`SendEvent`) |
| Library (integration) | scan finds/indexes audio files, import + duplicate-by-hash rejection, persistence, missing-file marking + remove-on-scan, missing-folder error |
| Interop events (integration) | real `scanLibrary` emits `libraryScanCompleted` on the event bus |
| Library (integration) | scan finds/indexes audio files, import + duplicate-by-hash rejection, persistence |
| MIDI (integration) | device enumeration, bad-index rejection, no-op sends, **Midi Through loopback** (skips when unavailable, and skips entirely when the ALSA sequencer is missing) |
| FFmpeg decode (integration) | wav/mp3/m4a/flac + **Opus-in-WebM labelled `.mp3`**, seek forward/backward, byte positions |
| OpenAL output (integration) | null-driver play/pause/stop, `PlaybackStopped` at EOF **and** on Stop, device enumeration + selection, buffered feed, 4 concurrent outputs, and that a foreign current context (another output) cannot break a playing output |
| Clip cache (integration) | short → in-memory, long → streaming, independent cursors, preload |
| Track transitions (integration) | two real 3s songs + 1s crossfade through the real engine/output: the crossfade starts before the track ends, the queue advances (`First` → `Second`) and the queue drains — with no output EOF signal (the mixer pads with silence), so it fails if the playback tick is ever removed |
| JS | `keys.js` gesture mapping (letters/digits/F-keys/numpad/OEM/modifiers) |

### Test seams added

- `MusicEngine.ConfigAccessor`, `MusicEngine.ConfigUpdater`, `MusicEngine.Delay`
- `AnnouncementEngine.ConfigAccessor` / `ChimePath` / `SourceFactory` / `OutputFactory`
- `MusicShareAudioPlayer.OutputFactory`
- `MusicShareService.ProcessReceivedChunks` (internal, extracted from the poll loop)
- `VisualizationStreamer.OnSamplesCaptured` (internal)
- `RemoteControlService(IMidiService, IGlobalHotkeyBackend)` ctor, `ConfigAccessor`, `MatchMidiMapping` + `RemoteAction` + `PushFeedbackSnapshot` + `ApplyGlobalShortcuts` (internal); `IGlobalHotkeyBackend` (internal seam)
- `UpdateService.IsNewerVersion` (internal); `UpdateInstaller.BuildWindowsUpdaterScript` / `BuildUnixUpdaterScript` / `InstallCoreAsync` / `DownloadAsync(…, HttpClient)` / `VerifyArchive` / `ExtractArchiveAsync` / `ResolveLauncher` (internal); `UpdateService.CheckForUpdatesAsync(HttpClient)` (internal)
- `AutoStart` builders + paths (internal); `TrayMenu` public; `AppLaunchArgs` public
- `ToolManager` install orchestration / `DownloadFileAsync` / per-platform source builders (internal); `ToolLocator` (internal)
- `FetchingService(string, HttpClient)` internal ctor; `MusicShareService.ProcessReceivedChunks` (internal)
- `AppPaths.AppDataDir` internal setter; `ConfigService(string path)` internal ctor
- `YoutubeCookieService` path-parameter overloads
- `InternalsVisibleTo` for both test projects
- Tests run with a temp app-data dir (`[ModuleInitializer]`) and no parallelization;
  fakes for `IAudioOutput`/`IAudioSource`/`IMidiService` live in the test project.

## 2. Testability seams (already in place)

| Seam | Type | Use |
|------|------|-----|
| `IAudioSource` | interface | fake decoded audio (no FFmpeg) |
| `IAudioOutput` | interface | fake output (no OpenAL), capture `PlaybackStopped` |
| `AudioSourceFactory` / `AudioOutputFactory` | static factory | swap implementations |
| `IMidiService` | interface | fake MIDI in/out |
| `ISongRequestHost` / `HeadlessHost` | interface + impl | drive features with no UI |
| `YoutubeFormInterop` | JSON request/response bus | end-to-end command tests |
| `AudioClip` / `InMemoryAudioSource` | pure classes | memory playback, seek, loop |
| `YoutubeCookieService.ParseCookies` | pure static | cookie parsing |
| `LyricsQueryNormalizer`, `MidiEventParser`, `MidiMessage` | pure | parsing/normalization |

### Small seams to add for testability (P0)

1. **Inject config**: `MusicEngine`, `SoundboardEngine`, `AnnouncementEngine`,
   `RemoteControlService` read `ConfigService.Instance`. Introduce an
   injectable `IConfig` (or make `ConfigService.Instance` settable in tests and
   point it at a temp file).
2. **Inject factories/backs**:
   - `SoundboardEngine` should take an `IMidiService` (instead of calling
     `MidiServiceFactory.Create()` internally).
   - `MusicEngine` should accept `Func<IAudioOutput>` / `Func<IAudioSource>` (or
     an `IAudioBackend`) so tests can supply fakes.
   - `AnnouncementEngine` already receives `MusicEngine`; also inject the chime
     source/output.
3. **Inject time**: crossfade/ducking use `Task.Delay`. Add a `Func<int, Task>
   Delay` seam (default `Task.Delay`) for deterministic ramp tests.
4. **`InternalsVisibleTo`**: expose `internal` types (`FFmpegAudioSource`,
   `AudioClipCache`, `InMemoryAudioSource`, `RtMidiService`, `OpenAlDevices`,
   `SongRequestEngine` internals) to the test assembly.

## 3. Test project layout & tooling

```
tests/
  SongRequestDesktopV2Rewrite.UnitTests/        # fast, no OS deps
  SongRequestDesktopV2Rewrite.IntegrationTests/ # real FFmpeg / OpenAL-null / RtMidi loopback
  SongRequestDesktopV2Rewrite.JsTests/          # node --test for newui/*.js
SongRequestDesktopV2Rewrite.slnx                 # add projects
```

- **Framework**: xUnit (`xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`).
- **Fakes**: prefer hand-written fakes over a mocking framework — the interfaces
  are tiny. (`NSubstitute` is fine if preferred.)
- **Target**: `net10.0`, referencing the app project.
- **Run**: `dotnet test SongRequestDesktopV2Rewrite.slnx`.
- **Coverage**: `coverlet.collector` + a coverage threshold on the core
  namespaces (start at “no drop”, ratchet up); report with
  `dotnet test --collect:"XPlat Code Coverage"`.
- **JS**: `node --test newui/*.test.js` (only `keys.js` is pure enough today).

### Fakes to write once

- `FakeAudioSource` — configurable sample rate/channels, a known PCM pattern
  (e.g. ramp/silence/tone), seek + `CurrentTime`, EOF after N samples, records
  reads.
- `FakeAudioOutput` — records `Init/Play/Pause/Stop`, exposes `PlaybackStopped`
  raise helper, tracks `Volume`, `DeviceNumber`.
- `FakeMidiService` — records sends, lets tests `Raise(MidiMessage)`.
- `TempConfig` — points `ConfigService` at a temp dir, resets between tests.
- `FakeProcessRunner` — for `FFmpegLocator`/`YoutubeCookieService`/`UpdateInstaller`
  process invocation (inject a runner instead of `Process.Start`).

---

## 4. Feature-by-feature test matrix

Legend: **U** = unit (fast, no OS), **I** = integration (needs FFmpeg/OpenAL/RtMidi
or a temp network), **M** = manual/OS (documented, not automatable here).

### 4.1 Playback engine — `MusicEngine`

| Feature | Type | Concrete cases |
|---|---|---|
| Queue mutation | U | add/`AddSong`, insert-next (`AddNextSongExternal`), remove, move (never into slot 0), shuffle keeps now-playing, clear |
| Playlist queue | U | `LoadPlaylist` order/shuffle, `RefillQueueFromPlaylist` only when queue ≤ 1, `AdvanceFromPlaylistQueue` moves head + preloads next |
| Track advance | U | `Next` with 0/1/>1 items; crossfade branch vs immediate branch |
| Repeat | U | `ToggleRepeat`; repeat restarts current instead of advancing |
| Volume | U | `AdjustVolume` clamps 0–1 and is blocked when `NormalizeVolume`; `SetVolume`; MIDI `SetVolumeFromMidi` |
| Crossfade params | U | `AdjustCrossfade`/`SetCrossfade`/`SetCrossfadeFromMidi` clamp to `CrossfadeMax`; `CrossfadeNormalized` = seconds/max |
| **Normalization gain** | U | `CalculateNormalizedVolume` — disabled returns base; `DefaultVolume<0` returns base; `PerceivedLoudness<-50` returns base; gain = `DefaultVolume·10^((target−lufs)/20)` clamped |
| Loudness pass | I | `CalculateSongLoudness` on generated tone/silence → LUFS ≈ expected; missing file → `TargetLoudness` |
| Queue timings | U | `ComputeQueueTimings` sets “Playing Now”, cumulative wait times, mm:ss formatting; the `QueueChanged` announcement already carries the new timings (add + remove) |
| Tick transitions | U | `ProcessTick`: remaining ≤ crossfade → begin crossfade; ≤ 0.15 → immediate; single track + playlist; repeat restarts (use injected clock/source) |
| Ducking envelope | U | `FadeAnnouncementDuckingAsync` target factor = `10^(−|db|/20)` clamped 0.01–1; restore → 1; values applied to output over time (injected Delay) |
| Announcement remote ops | U | toggle play-sound / push-to-talk, dim db adjust/set clamp 0–50 |
| Capture tap | U | `CapturingProvider` created once; `AudioSamplesCaptured` fires with buffer copy |

### 4.2 Audio sources/outputs

| Feature | Type | Concrete cases |
|---|---|---|
| FFmpeg decode | I | decode wav/mp3/m4a/flac + **Opus-in-WebM renamed `.mp3`**; sample count ≈ duration·rate·ch; `Length == samples·4`; non-silent RMS |
| FFmpeg seek | I | forward `Position` skip (no restart), backward restart; `CurrentTime` set; seek to silence vs tone region |
| FFmpeg laziness | I | ctor does not spawn decoder; first `Read` does |
| `AudioClip` / `InMemoryAudioSource` | U | duration/frame math; `Read` clamps to end; independent cursors share PCM; seek; volume scaling |
| `AudioClipCache` | U/I | short clip → in-memory; > 60 s → streaming source; LRU eviction above 256 MB; `Preload` idempotent; duplicate cursor independence |
| OpenAL output | I | play/pause/stop state; `PlaybackStopped` on EOF **and** explicit Stop; all samples consumed; volume applied; device enumeration; play on selected device |
| `BufferedWaveProvider` feed | I | `ReadFully=true` keeps playing; `ReadFully=false` starves → `PlaybackStopped` |
| Device enumeration | I | `AudioDevices.Enumerate()` returns ≥ 1; `OpenAlDevices` double-NUL parsing (unit-test the parser with a crafted buffer) |
| `FFmpegLocator` | U | prefers `tools/`, then app dir, then PATH (with `FakeProcessRunner`) |
| `FFmpegProbe` | U/I | parses `key=value` output; TagLib duration fallback; malformed output → null |

### 4.3 Soundboard — `SoundboardEngine`

| Feature | Type | Concrete cases |
|---|---|---|
| Config load/save | U | round-trip; defaults; corrupt file → defaults; page defaults |
| Pages | U | add/remove/rename, reorder, current-page switch, column/row resize slot math |
| Buttons | U | `UpdateButton` clamps volume 0–3, sets filename/length/enabled; delete clears + stops playback + deletes unused file; copy/move |
| Playback | U | `PlayButton` starts instance; repeat-mode toggle stops existing; concurrent instances allowed; `StopAll` |
| Volume | U | `ApplyVolume` = button.volume × master clamp 0–3; `SetMasterVolume` clamp 0–3 re-applies |
| Loop | U | `PlaybackStopped` with `IsLooping` resets `Position=0` and replays; manual stop does not loop |
| Device selection | U | `CustomOutputDeviceNumber ?? config.OutputDeviceNumber` |
| Supported formats | U | `IsSupportedAudioFile` for each extension + unknown |
| MIDI mapping match | U | NoteOn/CC match by channel+note/controller+value>0; no match cases |
| MIDI feedback | U | `UpdateMidiFeedback` NoteOn/Off vs ControlChange velocities; empty-button scan |
| Assign mode | U | MIDI capture during assign emits `MidiCaptured`, does not trigger playback |
| Snapshot | U | `EmitPlaybackSnapshot` shape (elapsed/total/volume) |

### 4.4 Announcement — `AnnouncementEngine`

| Feature | Type | Concrete cases |
|---|---|---|
| State machine | U | `StartAsync` idle→preparing→live; `StopAsync` live→preparing→idle; double-start ignored; `_gate` serialization |
| Push-to-talk vs toggle | U | `PressOrToggleAsync`/`ReleaseAsync` for both modes |
| Ducking integration | U | start ducks via `MusicEngine`, stop restores (fake output) |
| Chime | U | `PlayChimeAsync` missing file → short delay; present → plays and waits `PlaybackStopped` (fake output) |
| Settings notify | U | `NotifySettingsChanged` re-publishes state; `GetState` shape |

### 4.5 Song requests & YouTube

| Feature | Type | Concrete cases |
|---|---|---|
| Video ID parsing | U | `YoutubeService.ExtractVideoId`/`GetYouTubeVideoId` for watch/short/embed/`youtu.be`/invalid |
| Stream selection | U | “original” audio preferred, else highest bitrate (fake stream list) |
| Fetching service | U | timers, threads, approval state, `fetchedYtids`/blacklist, autopilot enqueue (`InitialLoadComplete`) |
| Video cache | U | `GetOrCreateVideoData`, cached title/creator/duration, persistence round-trip, `Changed` event |
| Download throttle | U | `DownloadSemaphore` concurrency; `ProcessingVideos` dedupe |
| Lyrics | U | `LyricsQueryNormalizer` strips `(Official Video)`, `[HD]`, `feat.` etc. (table-driven); lyrics fetch parse (fake HTTP) |
| Subtitles | U | timed-text parse and mapping to lines |
| Titles/metadata | U | `Song` construction from video metadata; duration formatting |

### 4.6 Library

| Feature | Type | Concrete cases |
|---|---|---|
| Scan | I | temp folder with fixtures → songs discovered, allowed extensions honored, missing-flag set |
| Metadata read | I | TagLib title/artist/album/genre; duration via decoder; thumbnail extracted |
| Import | U | import local file/path; duplicate id handling |
| Play/queue | U | `PlayLibrarySong` / `QueueLibrarySong` / `PlayLibrarySongNext` command shapes |
| Playlists | U | create/rename/delete/add/remove/reorder; persistence |
| Play history | U | `GetPlayHistory` shape; `RemovePlayHistoryEntry`; `FlagLibrarySongMissing` |
| Recommendations | U | `LibraryRecommendationsEnabled` gating and candidate selection |
| Data models | U | round-trip of `docs/library-data-models.md` entities |

### 4.7 Music Share

| Feature | Type | Concrete cases |
|---|---|---|
| Service lifecycle | U | start/stop/connect; status transitions buffering↔streaming by buffer level |
| Send audio chunk | U | base64 encode of float chunk; sample rate/channels |
| Metadata send | U | payload shape |
| Receive buffer | U | `Enqueue`/`TryDequeue`/`GetBufferLevel`/`Clear` |
| Audio player | U | `Feed` warms up at bufferLevel ≥ 3 then `Play`; volume clamp; `Stop` |
| Receive-volume command | U | `MusicShareSetVolume` propagates to player |

### 4.8 Visualization & Presentation

| Feature | Type | Concrete cases |
|---|---|---|
| Streamer modes | U | 5 modes produce correct payload for a known PCM frame; sample downsampling |
| Sample→event | U | `AudioSamplesCaptured` → streamer payload; unsubscribe on dispose |
| Presentation source | U | player vs requests source switch payloads |
| Queue payload | U | queue → presentation items (title/artist/thumbnail) |
| Current-state push | U | `SendPresentationCurrentState` shape |

### 4.8b Advanced audio (equalizer, sleep timer, analysis)

| Feature | Type | Concrete cases |
|---|---|---|
| Equalizer presets | U | factory presets have 10 bands; apply/save/delete; band edits mark the curve Custom |
| Equalizer DSP | U | bypass is sample-accurate; +12 dB at 1 kHz raises that band > 1.7×; pre-amp attenuates; output stays in range |
| Sleep timer state | U | start/remaining/extend/cancel; fade stop; “play last song to the end” holds the queue until the track ends; fade composes with announcement ducking |
| Song analysis | U | synthetic 120 BPM clicks → 112–128 BPM + confidence; loudness/peak/quality/timeline; no false drops in constant audio; drop after a real energy jump |
| Tempo accuracy | U | click tracks at 88, 128 and 174 BPM detected within ±3 %, including octave correction |
| Song parts | U | structured intro/verse/chorus/verse/chorus/bridge/chorus/outro fixture detects all parts, labels them and keeps non-overlapping chapter ranges |
| Lyric-informed parts | U | repeated/unique timed lyric phrases split a 48 s “chorus” into verse/chorus chapters; YouTube captions are excluded |
| Chapter lyrics UI | J | desktop/mobile/presentation lyrics receive colour-coded chapter labels and per-line chapter data; timed lines carry their timestamps |
| Karaoke timing | U | syllable/novelty-based word timings stay monotonic inside lyric lines; long instrumental gaps become pauses; fallback works without analysis |
| Karaoke UI | J | shared module renders word spans, sung/active states and animated pause bars; pages load it and expose the persisted toggle |
| Vocal remover | U | stereo centre-channel suppression lowers a centred vocal and keeps side content; output is a playable WAV |
| Karaoke versions | U | generation links/versions the file, reuses current results, deletes on song removal, and playback waits for the prepared path |
| Car / car-screen layout | J/C# | car mode exposes Home/Library/Player only, hides stage/share tools, switches to MusicPlayer mode and shows a native waiting screen before startup completes |
| Auto-analysis setting | U | persisted through `saveSettings`/`getSettings`; cached/auto results are pushed for the current track |
| Analysis cache | U | fresh results are reused, persisted and re-loaded; `force` re-analyses; algorithm-version mismatch invalidates; deleting a library song deletes its analysis |
| Library pre-analysis | U | background pass analyzes missing songs, reports progress, skips current analyses, and stops/queues safely |
| Library analysis UI | J | desktop Settings→Library and mobile player settings offer start/stop, progress, auto-add and algorithm-update toggles |
| Command/API surface | U | `analyzeSong`/`getSongAnalysis` through `ExecuteCommandAsync`; HTTP equalizer, sleep-timer and analysis routes |
| Advanced-audio UI | J | desktop/mobile pages load the panel; equalizer graph/bands/presets; analysis timeline + quality radar; sleep pill in the player top bar; mobile menu entry points |
| Chapters / BPM UI | J | interactive part/drop markers on desktop + mobile progress bars; BPM badge next to the quality pill; auto-analysis setting on desktop and mobile |
| Mobile visualization | J/C# | `ResizeObserver` + per-frame canvas re-measure; `visualizationReady` handshake re-arms the tap after a hidden Android tab |

### 4.9 Remote control & MIDI

| Feature | Type | Concrete cases |
|---|---|---|
| MIDI mapping match | U | `MatchMidiMapping` type/channel/note + `IsConfigured` gating |
| Action dispatch | U | each `RemoteAction` in `HandleMidi` triggers the right engine call (fake engine/fakes) |
| Feedback snapshot | U | 150 ms timer payload: volume/crossfade/announcement dim normalized to 0–127 |
| LED feedback | U | NoteOn/Off vs CC send for pressed/unpressed velocities |
| `MidiMessage` mapping | U | NAudio-style and RtMidi-style → `MidiMessage` (channel 1–16, Data1/Data2, RawType) |
| `MidiEventParser` | U | `MidiMessage` → `{messageType,channel,value1,value2}` |
| RtMidi service | I | enumerate (may be 0), validation rejects bad indexes, no-op sends, disable disconnects |
| **Keyboard keybinds** | U/M | config persists; **execution is not implemented** — see `docs/REMOVED-FEATURES.md` |

### 4.10 Settings & config

| Feature | Type | Concrete cases |
|---|---|---|
| `ConfigService` | U | load/save/migrate; defaults; `Update` mutation + `Changed`; unknown fields tolerated |
| `SaveSettings` validation | U | clamping of threads/timer, URL/token fields, `defaultSubmitMethod` |
| Launch options | U | `SaveLaunchOptions`; startup-mode parsing |
| Remote config | U | `SaveRemoteConfig` round-trip keybinds/MIDI mappings |
| `useNewUI` | U | removed from UI; config field tolerated |

### 4.11 Authentication / cookies

| Feature | Type | Concrete cases |
|---|---|---|
| Cookie parse | U | Netscape `cookies.txt`; raw `Cookie:` header; comments/blank lines; invalid names skipped |
| Persistence | U | `Save`→`LoadSaved` round-trip; `Clear`; corrupt file → empty |
| Apply cookies | U | `YoutubeService.SetCookies` rebuilds client; empty clears |
| yt-dlp grab | U | with `FakeProcessRunner`: args correct; missing binary → error message; temp file parsed (temp dir) |
| Interop commands | U | `getAuthBrowsers`, `grabCookies`, `submitCookies`, `clearAuthCookies`, `triggerAuth` response shapes |

### 4.12 Updates

| Feature | Type | Concrete cases |
|---|---|---|
| Version compare | U | `UpdateService` newer/older/equal, prerelease handling |
| Changelog parse | U | release body/notes extraction |
| Download + progress | U | `UpdateInstaller` streams and reports 0–100% (fake HTTP) |
| Script generation | U | Windows `.bat` vs Unix `.sh` content contains extract path + exe path |
| Stop-on-close / install flow | U | interop command wiring |

### 4.13 Interop bus (JSON)

| Feature | Type | Concrete cases |
|---|---|---|
| Request dispatch | U | every `case "..."` returns the documented JSON shape (parametrized table) |
| Unknown method | U | returns error, does not throw |
| Response correlation | U | `id` echoed; `SendResponse` shape |
| Events | U | `SendEvent`/`SendMusicPlayerEvent` payload snapshots for key events (nowPlaying, queue, playState, soundboard playback, midi) |
| Threading | U | `RunOnUi`/`PostToUi` execute with `HeadlessHost` |

### 4.14 Utilities / app

| Feature | Type | Concrete cases |
|---|---|---|
| `AppInfo.Version` | U | parses version |
| `AppPaths` | U | `DownloadPath`, `AppDataDir` created, `YoutubeAuthPath` |
| `keys.js` gesture mapping | JS | `codeToWpfKey`/`gestureFromEvent` for letters, digits, F-keys, numpad, OEM keys, modifiers, WPF-name compatibility |
| Soundboard/music-player JS shortcuts | JS | key handling for stop-all/volume/page and space/n/p/arrows when a window is focused |

---

## 5. Mocking / determinism strategy

- **No real audio device in CI**: OpenAL runs with the headless `null` driver.
  `ALSOFT_DRIVERS=null` is applied by `tests/coverage.runsettings` (process
  environment), by the workflow's `Tests` job env and by a `[ModuleInitializer]`
  in both test projects — the latter through `NativeEnvironment.Set`, because
  `Environment.SetEnvironmentVariable` does **not** reach native `getenv()` on
  Unix. If a runner still cannot open a device, the OpenAL tests **skip** rather
  than fail. Real-device behaviour is
  manual.
- **FFmpeg**: CI runners have `ffmpeg`; generate fixtures **at test time** with
  `ffmpeg -f lavfi …` (sine/silence, silence+tone) and assert RMS/seek, not
  exact samples.
- **RtMidi**: CI has no MIDI hardware; assert enumeration/validation/no-op only.
  The ALSA “Midi Through” loopback test is a **local/dev** integration test
  (skip if no devices).
- **Time**: inject `Delay`/clock into `MusicEngine`, `AnnouncementEngine`,
  `RemoteControlService` feedback timer.
- **Files**: every test uses a unique temp directory; `ConfigService` is reset
  between tests.
- **Network**: never hit YouTube/Lyrics/API in unit tests — wrap HTTP in a
  `FakeHttpMessageHandler` or an injected client.

## 6. CI integration

`release.yml` runs a dedicated **`test` job** on `ubuntu-latest`:

```yaml
      - name: Install FFmpeg (integration tests)
        run: sudo apt-get update && sudo apt-get install -y ffmpeg

      - name: Unit tests
        run: dotnet test tests/SongRequestDesktopV2Rewrite.UnitTests/UnitTests.csproj --configuration Release --logger "trx" --collect:"XPlat Code Coverage" --results-directory TestResults/unit

      - name: Check unit coverage floor
        run: python3 tests/check-coverage.py TestResults/unit 0.36

      - name: Integration tests
        run: dbus-run-session -- dotnet test tests/SongRequestDesktopV2Rewrite.IntegrationTests/IntegrationTests.csproj --configuration Release --settings tests/coverage.runsettings --logger "trx" --collect:"XPlat Code Coverage" --results-directory TestResults/integration

      - name: Check integration coverage floor
        run: python3 tests/check-coverage.py TestResults/integration 0.17

      - name: JavaScript tests
        run: node --test tests/js
```

`tests/check-coverage.py` enforces a **coverage ratchet** from the coverlet
cobertura report; raise the floors as coverage grows.


Integration tests run on Linux (FFmpeg + OpenAL-null available) and skip
themselves when FFmpeg is missing; the cross-platform `build` jobs only compile.

**Diagnosing a failure in CI.** The `Tests` job steps use `continue-on-error: true`
so every suite still runs; that makes the *step icons* report success. Read the
job summary instead — `tests/test-summary.py` prints each suite's raw step outcome,
and it reports an `aborted` count separately from `failed`: a non-zero count means
the test host died part-way through, which also means no coverage was written
(0.0%) and the totals are partial. Both `dotnet test` invocations run with
`--blame`, so a `Sequence_*.xml` in the uploaded `test-results` artifact names the
test that was running when the host crashed.

## 7. Priorities / roadmap

**P0 — pure logic — DONE (100 unit tests)**
`MusicEngine` queue/playlist/timings/normalization/crossfade; soundboard
format/page/volume/button; `LyricsQueryNormalizer`; `MidiEventParser` +
`MidiMessage`; `YoutubeCookieService`; `AppPaths`; `Song`; `VideoCache`;
`SongRequestEngine`; `ConfigService`.

**P1 — service state machines with fakes — DONE**
`AnnouncementEngine`; `MusicShareAudioPlayer`; `VisualizationStreamer`;
`RemoteControlService` mapping; interop JSON dispatch.

**P2 — integration + JS — DONE**
FFmpeg decode/seek, OpenAL-null output, `AudioClipCache`, `keys.js`.

**Remaining / future**
- **Tray icons** are native P/Invoke (Windows `Shell_NotifyIcon`; Linux AppIndicator) and can't run on CI, so they are **compile-checked + symbol-checked only** — a real-desktop test is needed (Linux additionally needs a GTK display and a StatusNotifier host). Their testable pieces (`TrayMenu`, `NullTrayIcon`, factory support, AppIndicator symbol resolution) are covered.
- **Global hotkeys** native backends (Windows `RegisterHotKey`, Linux X11 `XGrabKey`) can't run on CI; the gesture/key-table/service logic is covered. Not supported on macOS or Linux/Wayland.
- **Autostart / minimise-to-tray / close-to-tray** need a real-desktop smoke test on each OS (registry, `.desktop`, LaunchAgent).
- Full per-command interop table (currently read-only/stateful subsets) and event-payload snapshots for window/presentation events (which need a `NewUiRef`).
- Coverage floors are currently unit **0.36** / integration **0.17** (ratchet them up as coverage grows). The integration floor reflects what CI can run: the tray, hotkey and Photino-window shells are excluded (see `tests/coverage.runsettings`), and the environment-dependent suites (MIDI, MPRIS, cookie import) skip when their prerequisites are missing — so CI runs the integration suite under `dbus-run-session` and installs `libayatana-appindicator3-1` to keep as many of them running as possible.
- Make the `test` job a **required** check in branch protection.

**Required PR check (manual, repo settings).** In GitHub → Settings → Branches → branch protection for `master`: enable “Require status checks to pass” and select the `Tests` job.

## 8. Explicitly *not* unit-tested (manual/OS)

Document these (also in `docs/REMOVED-FEATURES.md`):
- Real audio output on a physical device (OpenAL/PulseAudio/ALSA/CoreAudio/WASAPI).
- Real MIDI hardware in/out.
- Photino window behaviour, drag-drop, focus, multi-window.
- Update install on a real install (Windows `.bat`, macOS app bundle).
- YouTube authentication end-to-end (browser cookies / yt-dlp network flows).

**Photino closing contract** (verified by hand, not by CI — it needs a real GTK/WebKit
display): `WindowClosing` handlers must `return true`, because Photino reads that as
"do not close" (`NetClosingDelegate`). Returning false lets GTK destroy the widget, and
Photino wires *every* toplevel window's GTK "destroy" signal to `gtk_main_quit()` — one
closed sub-window then stops the single GTK main loop and the whole app exits. All
sub-windows go through `NewUiWindow.RegisterHideOnClose`, which hides the window, keeps
it for reopening and always returns true; the main window's handler does the same for
"close to tray" and calls `Quit` (which exits the process) otherwise.

**OpenAL current-context contract** (the announcement bug): with the OpenAL build we
ship, `alcMakeContextCurrent` is **process-wide**, not per-thread. The app runs the
music output and the announcement chime output as two contexts on one device, so every
AL call must (a) be serialized process-wide against every other output
(`OpenAlAudioOutput.AlGate`, which `OpenAlDevices` shares too) and (b) select its own
context immediately before the call — the feed loop decodes between AL calls, and the
other output's thread switches context in that window. Without it the calls fail with
`AL_INVALID_NAME`, the buffers stop being queued and the track "hangs"/loops while the
chime stays broken. Reproduced with a provider that makes a foreign context current from
inside `Read` (`ForeignCurrentContext_DoesNotBreakAPlayingOutput`) and with a two-output
harness over real FFmpeg sources.

**System media on Linux**: `PhotinoWindow.WindowHandle` throws on Linux, which used to
abort `StartSystemMediaIntegration` before the MPRIS session was created (settings
showed it enabled but inactive). The handle is now only resolved where it exists; MPRIS
needs none.

**Window fullscreen**: the sub-window pages asked for the DOM Fullscreen API
(`documentElement.requestFullscreen()`), which needs the embedder to handle WebKit's
fullscreen request — Photino does not, so the button silently did nothing (and the
"Open presentation in fullscreen mode" setting was never applied either). The pages now
ask the host (`setFullscreen` → `NewUiWindow.SetSubWindowFullscreen` → Photino's native
`SetFullScreen`), which works on every platform; Esc leaves fullscreen.

**Music player home tab** (verified with screenshots, data covered by unit tests): the four
horizontally scrolling rows come from one `getHomeFeed` call — playlists ordered by
`LastPlayedUtc`, recommendations (library songs only, each with a reason badge),
recently listened (from the Recently Played data) and the newest library additions — plus
the header stats. Rows with no content hide themselves.

Two things that made it feel broken were fixed and re-verified visually:

- The home view's `display` rule was scoped to the id, not to `.active`, so it outranked
  `.view { display: none }` and the home tab stayed rendered behind every other tab
  (`#view-home.active` now).
- Artwork was base64-inlined at full size (the library artwork averages ~120 KB, up to
  2 MB): one feed load was **63 MB** of IPC, re-encoded on the UI thread on every queue
  and now-playing update. `ThumbnailStore` now downscales once (320 px, FFmpeg, cached on
  disk), caches the encoded string in memory, never converts inside a feed call (0.02 MB
  on a cold cache, 4 ms) and the page fetches only the artwork it is showing.

**Output device menu** (verified with screenshots): the speaker button in the player bar
opens a menu listing `getMusicAudioDevices` with the current device ticked; picking one
sends `setMusicOutputDevice`, which switches the live output and persists the choice.

Verified live on the visualization window (screenshots analysed pixel-wise, before/after):
the waveform trace covered 51 % of the canvas before and 99.9 % after; the spectrum drew
13 identical bass bars pinned at the top of the canvas before (then runs of 3/3/2/2) and
all-distinct bars with the bass at ~half height after; the fullscreen button resized the
window from 1000x720 to the full 1920x1080 logical screen.

Verified live with a real `WM_DELETE_WINDOW` (what a title-bar "X" produces): before the
fix, closing the music player or visualization window exited the app; after it, the app
keeps running and the window can be reopened (it is only hidden).

