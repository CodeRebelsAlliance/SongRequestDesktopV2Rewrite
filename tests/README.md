# Tests

Automated tests for SongRequest Desktop. Three suites:

| Suite | Project | Needs | What |
|---|---|---|---|
| Unit | `SongRequestDesktopV2Rewrite.UnitTests` | nothing | fast, hermetic logic tests |
| Integration | `SongRequestDesktopV2Rewrite.IntegrationTests` | **FFmpeg**, OpenAL | real decode/output/library/MIDI/interop |
| JavaScript | `tests/js` | **Node 18+** | `newui/keys.js` gesture mapping + `newui/hostbridge.js` host bridge |

The full plan, feature matrix and remaining work live in
[`../docs/TESTING.md`](../docs/TESTING.md).

## Running

Everything (unit + integration + JS + coverage floors + summary):

```bash
./tests/run-tests.sh
```

Individual suites:

```bash
dotnet test SongRequestDesktopV2Rewrite.slnx          # all .NET tests
dotnet test tests/SongRequestDesktopV2Rewrite.UnitTests/UnitTests.csproj
dotnet test tests/SongRequestDesktopV2Rewrite.IntegrationTests/IntegrationTests.csproj
node --test tests/js
```

Coverage (coverlet, Cobertura):

```bash
dotnet test tests/SongRequestDesktopV2Rewrite.UnitTests/UnitTests.csproj \
  --collect:"XPlat Code Coverage" --results-directory TestResults/unit
python3 tests/check-coverage.py TestResults/unit 0.27
```

## Prerequisites

- **.NET 10 SDK**
- **FFmpeg** on the `PATH` for integration tests (they **skip** cleanly when it
  is missing, so unit-only machines still pass).
- **Node 18+** for the JS tests.
- OpenAL is provided by the `Silk.NET.OpenAL.Soft.Native` package. Every test run
  uses the headless `null` driver (`ALSOFT_DRIVERS=null`), applied in three places
  so it cannot be missed:
  1. `tests/coverage.runsettings` (`<RunConfiguration><EnvironmentVariables>`) —
     the test host applies it to the process environment **before** the test
     assembly is loaded;
  2. the `Tests` job environment in the workflow;
  3. a `[ModuleInitializer]` in each test project (also makes a bare
     `dotnet test` work without the settings file).

  The module initializers use `NativeEnvironment.Set` (libc `setenv`) on purpose:
  `Environment.SetEnvironmentVariable` only updates the *managed* environment on
  Unix, and OpenAL — being native — reads the real one via `getenv()`.

  If a machine still cannot open an OpenAL device at all, the audio tests
  **skip** instead of failing (`Fixtures.OpenAlAvailable`).

## Conventions

- Unit tests are **hermetic**: a `[ModuleInitializer]` points `AppPaths.AppDataDir`
  at a temp directory, and the soundboard/library fixtures are reset per test.
  Nothing touches your real config.
- Test parallelism is **disabled** (the app has process-wide singletons); each
  test class is independent.
- Shared doubles live in `Fakes.cs` (`FakeAudioOutput`, `FakeAudioSource`,
  `FakeMidiService`, `StubHttpHandler`) and `TestSupport.cs`.
- Integration fixtures are generated at test time with FFmpeg into a temp dir
  (nothing binary is committed); `Fixtures.Audio` is a lazy singleton.
- OS-dependent tests use `[SkippableFact]` + `Skip.If(...)` so they don't fail
  on machines without the resource.

## Coverage ratchet

`tests/check-coverage.py <results-dir> <floor>` fails if the coverlet line-rate
in that results directory is below the floor. Current floors:

| Suite | Floor | Typical (local) | Typical (CI) |
|---|---|---|---|
| Unit | **0.36** | ~0.51 | ~0.49 |
| Integration | **0.17** | ~0.23 | ~0.19 |

Raise the floors (in `tests/run-tests.sh` and `.github/workflows/release.yml`)
as coverage improves — never lower them.

The integration number is **lower on CI than locally** because several suites
depend on the machine: MIDI needs the ALSA sequencer (`/dev/snd/seq`), MPRIS needs
a session D-Bus, the cookie import needs `yt-dlp`, and the tray test needs
`libayatana-appindicator3`. Those tests **skip** when their prerequisite is
missing, so CI (a) runs the integration suite under `dbus-run-session`, (b) installs
`libayatana-appindicator3-1` and (c) tries `modprobe snd-seq`, to keep as many of
them running as possible. The floor is set for the case where the kernel module
cannot be loaded.

Coverage uses `tests/coverage.runsettings`, which excludes what cannot run
headless: the tray and global-hotkey P/Invoke shells (`Platform/Win32TrayIcon.cs`,
`Platform/LinuxTrayIcon.cs`, `Platform/Hotkeys/*Backend.cs`) and the Photino window
shell (`NewUiWindow.cs`) — their pure pieces are unit-tested elsewhere.

## CI and branch protection

The `Tests` job in `.github/workflows/release.yml` runs unit + integration + JS
with coverage, prints a summary (see below) and **fails if any suite fails or a
coverage floor is missed**. It runs on every push and pull request.

### Detailed failure reports

- Each `dotnet test` writes a **TRX** file; the job uploads `TestResults/**`
  (TRX + Cobertura coverage) as the `test-results` artifact.
- `tests/test-summary.py` writes a Markdown summary to the **GitHub Actions job
  summary**, listing every failed test with its error message and stack trace.

### Make it a required check (one-time, repo settings)

Branch protection is a GitHub setting, not something in the repo:

1. GitHub → **Settings → Branches → Add branch protection rule** (or edit the
   rule for `master`).
2. Enable **Require status checks to pass before merging**.
3. Search for and select the **`Tests`** check.
4. Save.

After that, a PR cannot merge while any test suite or coverage floor is failing.

## Adding tests

1. Pure logic → `SongRequestDesktopV2Rewrite.UnitTests`.
2. Anything needing FFmpeg/OpenAL/real files/MIDI → `...IntegrationTests`, using
   `Fixtures.Audio` (generate with `Fixtures`/`ffmpeg`).
3. If a class is `internal`, it is already visible to both test projects via
   `InternalsVisibleTo`; if it needs a seam (a factory, config accessor, clock),
   add a small injectable property/method rather than testing statics.
