using System;
using System.Collections.Generic;
using System.IO;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// The environment that will be handed to GTK/WebKitGTK, plus why.
/// </summary>
public sealed record WebViewEnvironmentReport(
    bool SafeMode,
    bool CrashRecovered,
    bool NvidiaDetected,
    bool WaylandSession,
    IReadOnlyDictionary<string, string> Variables)
{
    /// <summary>Short human-readable summary for the console log.</summary>
    public string Describe()
    {
        var reasons = new List<string>();
        if (SafeMode) reasons.Add(CrashRecovered ? "safe mode (repeated unfinished startup)" : "safe mode (requested)");
        if (NvidiaDetected) reasons.Add("NVIDIA driver");
        if (WaylandSession) reasons.Add("Wayland session");

        var applied = Variables.Count == 0
            ? "none"
            : string.Join(", ", System.Linq.Enumerable.Select(Variables, kv => $"{kv.Key}={kv.Value}"));

        return $"webview environment: applied [{applied}]"
            + (reasons.Count > 0 ? "; detected: " + string.Join(", ", reasons) : "");
    }
}

/// <summary>
/// Prepares the GTK/WebKitGTK environment on Linux **before** Photino creates its
/// window.
///
/// WebKitGTK 2.42+ renders through DMA-BUF by default, which fails on some
/// driver/compositor combinations — most notably the NVIDIA proprietary driver and
/// some virtual GPUs. The symptoms are either a crash right at startup
/// (<c>Gdk-Message: Error 71 (Protocol error) dispatching to Wayland display.</c>)
/// or a window that stays tiny/black while the console fills with
/// <c>Failed to create GBM buffer of size …: Invalid argument</c>.
///
/// These are the well-known mitigations for WebKitGTK apps (the same ones Tauri
/// documents), applied automatically so users do not have to export them by hand:
/// <list type="bullet">
///   <item><c>WEBKIT_DISABLE_DMABUF_RENDERER=1</c> — render through shared memory
///   instead of DMA-BUF (fixes the blank window and the Wayland startup crash);</item>
///   <item><c>__NV_DISABLE_EXPLICIT_SYNC=1</c> — on NVIDIA, the cheapest fix for
///   the Wayland <c>Error 71</c> crash, and it keeps the GPU path;</item>
///   <item><c>GDK_BACKEND=x11</c> — in safe mode, since <c>Error 71</c> is specific
///   to the Wayland backend;</item>
///   <item><c>WEBKIT_DISABLE_COMPOSITING_MODE=1</c> — deepest fallback, safe mode
///   only.</item>
/// </list>
///
/// Anything already present in the environment is left untouched, and the user can
/// opt out entirely with <c>--gpu</c> / <c>SONGREQUEST_WEBKIT_DMABUF=1</c> or force
/// safe mode with <c>--safe-mode</c> / <c>SONGREQUEST_SAFE_MODE=1</c>.
/// </summary>
public static class LinuxWebViewEnvironment
{
    internal const string DmabufVariable = "WEBKIT_DISABLE_DMABUF_RENDERER";
    internal const string CompositingVariable = "WEBKIT_DISABLE_COMPOSITING_MODE";
    internal const string ExplicitSyncVariable = "__NV_DISABLE_EXPLICIT_SYNC";
    internal const string GdkBackendVariable = "GDK_BACKEND";

    /// <summary>Set to <c>1</c> to keep the GPU/DMA-BUF path (opt out of the default fix).</summary>
    public const string PreferGpuVariable = "SONGREQUEST_WEBKIT_DMABUF";

    /// <summary>Set to <c>1</c> to force the conservative/safe webview configuration.</summary>
    public const string SafeModeVariable = "SONGREQUEST_SAFE_MODE";

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Computes the variables to set. Pure, so the decision table is unit-testable.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ComputeVariables(WebViewEnvironmentInput input)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!input.IsLinux) return variables;

        void Set(string name, string value)
        {
            // Never fight the user: an explicit value always wins.
            if (!string.IsNullOrEmpty(input.ReadEnvironment(name))) return;
            variables[name] = value;
        }

        // Explicit backend request (Wins over the safe-mode default below.)
        if (input.ForceBackendX11) Set(GdkBackendVariable, "x11");
        else if (input.ForceBackendWayland) Set(GdkBackendVariable, "wayland");

        // On NVIDIA this is free (it keeps the GPU path) and fixes the Wayland
        // "Error 71" crash caused by explicit-sync without an acquire point.
        if (input.NvidiaDriver) Set(ExplicitSyncVariable, "1");

        if (input.SafeMode)
        {
            Set(DmabufVariable, "1");
            Set(CompositingVariable, "1");
            // The startup crash is specific to the Wayland backend.
            if (!input.ForceBackendWayland) Set(GdkBackendVariable, "x11");
            return variables;
        }

        if (input.PreferGpu) return variables;

        // Default: avoid the DMA-BUF renderer. A needlessly disabled renderer only
        // costs some CPU, whereas leaving it on breaks the app completely on the
        // affected setups.
        Set(DmabufVariable, "1");
        return variables;
    }

    /// <summary>Reads the real environment/driver state and returns the report.</summary>
    internal static WebViewEnvironmentReport Detect(Func<string, string?> readEnvironment, bool isLinux)
    {
        var repeatedFailure = StartupGuard.RepeatedStartupFailure;
        if (repeatedFailure) StartupGuard.EnterSafeMode();

        var preferGpu = AppLaunchArgs.PreferGpu
            || IsEnabled(readEnvironment(PreferGpuVariable));

        var safeMode = AppLaunchArgs.SafeMode
            || repeatedFailure
            || StartupGuard.SafeModePersisted
            || IsEnabled(readEnvironment(SafeModeVariable));

        if (preferGpu)
        {
            // Explicit opt-in to the GPU path also clears a persisted safe mode and
            // a tray the crash guard switched off — "--gpu" means "give me the
            // normal configuration again".
            safeMode = false;
            StartupGuard.ClearSafeMode();
            StartupGuard.ClearTrayDisabled();
        }

        var nvidia = readEnvironment("__NV_DRIVER_PRESENT") == "1" || NvidiaDriverDetected(isLinux);
        var wayland = WaylandSessionDetected(readEnvironment);

        var variables = ComputeVariables(new WebViewEnvironmentInput(
            IsLinux: isLinux,
            SafeMode: safeMode,
            NvidiaDriver: nvidia,
            ForceBackendX11: AppLaunchArgs.ForceX11,
            ForceBackendWayland: AppLaunchArgs.ForceWayland,
            PreferGpu: preferGpu,
            ReadEnvironment: readEnvironment));

        return new WebViewEnvironmentReport(safeMode, repeatedFailure, nvidia, wayland, variables);
    }

    /// <summary>
    /// Applies the webview environment for this run. Call this **first thing** in
    /// <c>Main</c>, before anything touches GTK/WebKit.
    /// </summary>
    public static WebViewEnvironmentReport Apply()
    {
        if (!OperatingSystem.IsLinux())
            return new WebViewEnvironmentReport(false, false, false, false, Empty);

        var report = Detect(Environment.GetEnvironmentVariable, isLinux: true);

        foreach (var (name, value) in report.Variables)
        {
            // Native setenv: GTK/WebKitGTK read these through getenv() and would not
            // see a managed-only assignment.
            NativeEnvironment.Set(name, value);
        }

        return report;
    }

    // ── Detection helpers ──────────────────────────────────────────────────

    /// <summary>True when the NVIDIA kernel driver is loaded (proprietary or open).</summary>
    internal static bool NvidiaDriverDetected(bool isLinux = true)
    {
        if (!isLinux) return false;
        try
        {
            return File.Exists("/proc/driver/nvidia/version") || Directory.Exists("/sys/module/nvidia");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the session is Wayland.</summary>
    internal static bool WaylandSessionDetected(Func<string, string?> readEnvironment)
    {
        try
        {
            if (!string.IsNullOrEmpty(readEnvironment("WAYLAND_DISPLAY"))) return true;
            return string.Equals(readEnvironment("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEnabled(string? value)
        => value is not null && (value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Inputs for <see cref="LinuxWebViewEnvironment.ComputeVariables"/> (pure).</summary>
internal sealed record WebViewEnvironmentInput(
    bool IsLinux,
    bool SafeMode,
    bool NvidiaDriver,
    bool ForceBackendX11,
    bool ForceBackendWayland,
    bool PreferGpu,
    Func<string, string?> ReadEnvironment);
