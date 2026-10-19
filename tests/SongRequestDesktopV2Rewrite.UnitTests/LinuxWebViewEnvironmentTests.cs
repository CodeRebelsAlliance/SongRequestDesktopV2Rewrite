using System;
using System.Collections.Generic;
using System.IO;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// The Linux webview environment workarounds (WebKitGTK DMA-BUF / NVIDIA) and the
/// startup guard that recovers from a crashed start.
/// </summary>
public class LinuxWebViewEnvironmentTests : IDisposable
{
    public LinuxWebViewEnvironmentTests()
    {
        ResetGuard();
        AppLaunchArgs.Parse(Array.Empty<string>());
    }

    public void Dispose()
    {
        ResetGuard();
        AppLaunchArgs.Parse(Array.Empty<string>());
    }

    private static void ResetGuard()
    {
        foreach (var path in new[] { StartupGuard.MarkerPath, StartupGuard.SafeModePath })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static IReadOnlyDictionary<string, string> Compute(
        bool safeMode = false,
        bool nvidia = false,
        bool x11 = false,
        bool wayland = false,
        bool preferGpu = false,
        bool isLinux = true,
        params (string Name, string Value)[] existing)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in existing) environment[name] = value;

        return LinuxWebViewEnvironment.ComputeVariables(new WebViewEnvironmentInput(
            IsLinux: isLinux,
            SafeMode: safeMode,
            NvidiaDriver: nvidia,
            ForceBackendX11: x11,
            ForceBackendWayland: wayland,
            PreferGpu: preferGpu,
            ReadEnvironment: name => environment.TryGetValue(name, out var v) ? v : null));
    }

    // ── variable computation ───────────────────────────────────────────────

    [Fact]
    public void NonLinux_AppliesNothing()
        => Assert.Empty(Compute(isLinux: false, nvidia: true, safeMode: true));

    [Fact]
    public void Default_DisablesTheDmabufRenderer()
    {
        var variables = Compute();

        Assert.Equal("1", variables[LinuxWebViewEnvironment.DmabufVariable]);
        Assert.False(variables.ContainsKey(LinuxWebViewEnvironment.CompositingVariable));
        Assert.False(variables.ContainsKey(LinuxWebViewEnvironment.GdkBackendVariable));
    }

    [Fact]
    public void Nvidia_AlsoDisablesExplicitSync()
    {
        var variables = Compute(nvidia: true);

        Assert.Equal("1", variables[LinuxWebViewEnvironment.DmabufVariable]);
        Assert.Equal("1", variables[LinuxWebViewEnvironment.ExplicitSyncVariable]);
    }

    [Fact]
    public void SafeMode_DisablesDmabufCompositingAndForcesX11()
    {
        var variables = Compute(safeMode: true);

        Assert.Equal("1", variables[LinuxWebViewEnvironment.DmabufVariable]);
        Assert.Equal("1", variables[LinuxWebViewEnvironment.CompositingVariable]);
        Assert.Equal("x11", variables[LinuxWebViewEnvironment.GdkBackendVariable]);
    }

    [Fact]
    public void SafeMode_WithExplicitWayland_DoesNotForceX11()
    {
        var variables = Compute(safeMode: true, wayland: true);

        Assert.Equal("wayland", variables[LinuxWebViewEnvironment.GdkBackendVariable]);
    }

    [Theory]
    [InlineData(true, false, "x11")]
    [InlineData(false, true, "wayland")]
    public void ExplicitBackend_IsHonoured(bool x11, bool wayland, string expected)
        => Assert.Equal(expected, Compute(x11: x11, wayland: wayland)[LinuxWebViewEnvironment.GdkBackendVariable]);

    [Fact]
    public void PreferGpu_KeepsTheAcceleratedPath()
    {
        var variables = Compute(preferGpu: true, nvidia: true);

        Assert.False(variables.ContainsKey(LinuxWebViewEnvironment.DmabufVariable));
        Assert.Equal("1", variables[LinuxWebViewEnvironment.ExplicitSyncVariable]); // still free/cheap
    }

    [Fact]
    public void ExistingEnvironmentValues_AreNeverOverridden()
    {
        var variables = Compute(
            existing: new[]
            {
                (LinuxWebViewEnvironment.DmabufVariable, "0"),
                (LinuxWebViewEnvironment.GdkBackendVariable, "wayland"),
            });

        Assert.False(variables.ContainsKey(LinuxWebViewEnvironment.DmabufVariable));
        Assert.False(variables.ContainsKey(LinuxWebViewEnvironment.GdkBackendVariable));
    }

    // ── detection ─────────────────────────────────────────────────────────

    [Fact]
    public void Detect_NoFailures_NoOverrides_AppliesDmabufFix()
    {
        var report = LinuxWebViewEnvironment.Detect(_ => null, isLinux: true);

        Assert.False(report.SafeMode);
        Assert.False(report.CrashRecovered);
        Assert.Equal("1", report.Variables[LinuxWebViewEnvironment.DmabufVariable]);
    }

    [Fact]
    public void Detect_SingleUnfinishedStart_DoesNotEscalate()
    {
        StartupGuard.Begin(); // simulating a start that was interrupted once

        var report = LinuxWebViewEnvironment.Detect(_ => null, isLinux: true);

        Assert.False(report.SafeMode);
    }

    [Fact]
    public void Detect_RepeatedUnfinishedStarts_EnterSafeModeAndRememberIt()
    {
        StartupGuard.Begin();
        StartupGuard.Begin();

        var report = LinuxWebViewEnvironment.Detect(_ => null, isLinux: true);

        Assert.True(report.SafeMode);
        Assert.True(report.CrashRecovered);
        Assert.True(StartupGuard.SafeModePersisted);
        Assert.Equal("x11", report.Variables[LinuxWebViewEnvironment.GdkBackendVariable]);
    }

    [Fact]
    public void Detect_PersistedSafeMode_IsUsedWithoutNewFailures()
    {
        StartupGuard.EnterSafeMode();

        var report = LinuxWebViewEnvironment.Detect(_ => null, isLinux: true);

        Assert.True(report.SafeMode);
        Assert.False(report.CrashRecovered);
    }

    [Fact]
    public void Detect_GpuOptOut_ClearsPersistedSafeMode()
    {
        StartupGuard.EnterSafeMode();
        AppLaunchArgs.Parse(new[] { AppLaunchArgs.GpuFlag });

        var report = LinuxWebViewEnvironment.Detect(_ => null, isLinux: true);

        Assert.False(report.SafeMode);
        Assert.False(StartupGuard.SafeModePersisted);
        Assert.False(report.Variables.ContainsKey(LinuxWebViewEnvironment.DmabufVariable));
    }

    [Fact]
    public void Detect_SafeModeEnvironmentVariable_ForcesSafeMode()
    {
        StartupGuard.Complete();

        var report = LinuxWebViewEnvironment.Detect(
            name => name == LinuxWebViewEnvironment.SafeModeVariable ? "1" : null,
            isLinux: true);

        Assert.True(report.SafeMode);
    }

    [Fact]
    public void Detect_PreferGpuEnvironmentVariable_KeepsGpuPath()
    {
        var report = LinuxWebViewEnvironment.Detect(
            name => name == LinuxWebViewEnvironment.PreferGpuVariable ? "1" : null,
            isLinux: true);

        Assert.False(report.SafeMode);
        Assert.False(report.Variables.ContainsKey(LinuxWebViewEnvironment.DmabufVariable));
    }

    [Fact]
    public void WaylandSession_IsDetectedFromSessionVariables()
    {
        Assert.True(LinuxWebViewEnvironment.WaylandSessionDetected(n => n == "WAYLAND_DISPLAY" ? "wayland-0" : null));
        Assert.True(LinuxWebViewEnvironment.WaylandSessionDetected(n => n == "XDG_SESSION_TYPE" ? "wayland" : null));
        Assert.False(LinuxWebViewEnvironment.WaylandSessionDetected(n => n == "XDG_SESSION_TYPE" ? "x11" : null));
        Assert.False(LinuxWebViewEnvironment.WaylandSessionDetected(_ => null));
    }

    [Fact]
    public void NvidiaDriverDetection_IsFalseOffLinux()
        => Assert.False(LinuxWebViewEnvironment.NvidiaDriverDetected(isLinux: false));

    [Fact]
    public void Describe_MentionsWhatWasApplied()
    {
        var report = LinuxWebViewEnvironment.Detect(
            name => name == "__NV_DRIVER_PRESENT" ? "1" : null,
            isLinux: true);

        var text = report.Describe();

        Assert.Contains("NVIDIA driver", text);
        Assert.Contains(LinuxWebViewEnvironment.DmabufVariable, text);
    }
}

public class StartupGuardTests : IDisposable
{
    private readonly string _previousLocalData = AppPaths.LocalAppDataDir;

    public StartupGuardTests()
    {
        // Isolate the guard's markers: it must never touch the developer's (or a
        // test runner's) real state — a leftover marker disables the tray.
        var dir = Path.Combine(TestSupport.NewTempDir("guard"), "local");
        Directory.CreateDirectory(dir);
        AppPaths.LocalAppDataDir = dir;
        Cleanup();
    }

    public void Dispose()
    {
        Cleanup();
        AppPaths.LocalAppDataDir = _previousLocalData;
    }

    private static void Cleanup()
    {
        foreach (var name in new[]
                 {
                     StartupGuard.MarkerFileName,
                     StartupGuard.SafeModeFileName,
                     StartupGuard.TrayDisabledFileName,
                     "safe-mode-ok.marker",
                     "tray-used.marker",
                 })
        {
            try
            {
                var path = Path.Combine(AppPaths.LocalAppDataDir, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }

    [Fact]
    public void Begin_CountsUnfinishedStarts_AndCompleteResets()
    {
        Assert.Equal(0, StartupGuard.UnfinishedStarts);
        Assert.False(StartupGuard.RepeatedStartupFailure);

        StartupGuard.Begin();
        Assert.Equal(1, StartupGuard.UnfinishedStarts);
        Assert.False(StartupGuard.RepeatedStartupFailure); // one interruption is forgiven

        StartupGuard.Begin();
        Assert.Equal(2, StartupGuard.UnfinishedStarts);
        Assert.True(StartupGuard.RepeatedStartupFailure);

        StartupGuard.Complete();
        Assert.Equal(0, StartupGuard.UnfinishedStarts);
        Assert.False(StartupGuard.RepeatedStartupFailure);
    }

    [Fact]
    public void Complete_OnAFreshInstall_IsSafe()
        => StartupGuard.Complete();

    [Fact]
    public void EnterSafeMode_PersistsAndClearsTheMarker()
    {
        StartupGuard.Begin();
        StartupGuard.Begin();

        StartupGuard.EnterSafeMode();

        Assert.True(StartupGuard.SafeModePersisted);
        Assert.Equal(0, StartupGuard.UnfinishedStarts);
        Assert.False(StartupGuard.RepeatedStartupFailure);
    }

    [Fact]
    public void ClearSafeMode_ForgetsIt()
    {
        StartupGuard.EnterSafeMode();
        Assert.True(StartupGuard.SafeModePersisted);

        StartupGuard.ClearSafeMode();

        Assert.False(StartupGuard.SafeModePersisted);
    }

    [Fact]
    public void UnfinishedStarts_IgnoresACorruptMarker()
    {
        Directory.CreateDirectory(AppPaths.LocalAppDataDir);
        File.WriteAllText(StartupGuard.MarkerPath, "not-a-number");

        Assert.Equal(0, StartupGuard.UnfinishedStarts);
    }

    [Fact]
    public void TrayDisabled_IsRememberedUntilCleared()
    {
        Assert.False(StartupGuard.TrayDisabled);

        StartupGuard.DisableTray();
        Assert.True(StartupGuard.TrayDisabled);

        // Changing the tray settings in the UI lets it be tried again.
        StartupGuard.ClearTrayDisabled();
        Assert.False(StartupGuard.TrayDisabled);
    }

    [Fact]
    public void TrayUsed_IsTrackedSoOnlyTheTrayIsBlamedForItsOwnCrash()
    {
        Assert.False(StartupGuard.TrayUsedLastRun);

        StartupGuard.NoteTrayUsed();
        Assert.True(StartupGuard.TrayUsedLastRun);

        StartupGuard.ClearTrayUsed();
        Assert.False(StartupGuard.TrayUsedLastRun);
    }

    [Fact]
    public void Complete_ForgetsThatATrayWasUsed()
    {
        StartupGuard.NoteTrayUsed();
        Assert.True(StartupGuard.TrayUsedLastRun);

        // The UI rendered and the tray was set up: the tray did not crash, so a
        // later unrelated crash loop must not blame it.
        StartupGuard.Complete();

        Assert.False(StartupGuard.TrayUsedLastRun);
    }

    [Fact]
    public void SafeMode_IsRetriedAfterAFewCleanStarts()
    {
        StartupGuard.ClearSafeMode();
        StartupGuard.EnterSafeMode();
        Assert.True(StartupGuard.SafeModePersisted);

        for (int i = 1; i < StartupGuard.SafeModeRecoveryStarts; i++)
        {
            StartupGuard.Complete();
            Assert.True(StartupGuard.SafeModePersisted);
        }

        // The last clean start asks for the hardware path again.
        StartupGuard.Complete();
        Assert.False(StartupGuard.SafeModePersisted);
    }

    [Fact]
    public void ClearSafeMode_ResetsTheRecoveryCounter()
    {
        StartupGuard.ClearSafeMode();
        StartupGuard.EnterSafeMode();
        StartupGuard.Complete();       // one clean start on the old marker
        StartupGuard.ClearSafeMode();  // --gpu

        StartupGuard.EnterSafeMode();  // a new crash loop
        for (int i = 1; i < StartupGuard.SafeModeRecoveryStarts; i++)
        {
            StartupGuard.Complete();
            Assert.True(StartupGuard.SafeModePersisted, "the old counter must not count towards the new safe mode");
        }

        StartupGuard.Complete();
        Assert.False(StartupGuard.SafeModePersisted);
    }

    [Fact]
    public void EnterSafeMode_MarksThisRunAsACrashRecovery()
    {
        StartupGuard.EnterSafeMode();

        // Run-scoped flag (other tests may have set it already), so only assert the effect.
        Assert.True(StartupGuard.CrashRecoveryThisRun);
        Assert.True(StartupGuard.SafeModePersisted);
    }
}
