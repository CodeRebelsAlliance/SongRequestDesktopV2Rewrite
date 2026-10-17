using System;
using System.Runtime.InteropServices;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Sets environment variables so that **native** libraries actually see them.
///
/// On Unix, <see cref="Environment.SetEnvironmentVariable(string,string)"/> only
/// updates the managed copy of the environment: a native <c>getenv()</c> still
/// returns null. GTK, WebKitGTK, OpenAL Soft and friends read the environment
/// through <c>getenv()</c>, so anything they need (the WebKit DMA-BUF workaround,
/// the headless OpenAL driver for tests) must be pushed through libc's
/// <c>setenv()</c> as well.
/// </summary>
internal static class NativeEnvironment
{
    private const int Overwrite = 1;

    [DllImport("libc", EntryPoint = "setenv", SetLastError = true)]
    private static extern int SetEnvNative(string name, string value, int overwrite);

    /// <summary>Sets <paramref name="name"/> for both the managed and native environment.</summary>
    public static void Set(string name, string value)
    {
        try { Environment.SetEnvironmentVariable(name, value); } catch { }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsBrowser())
        {
            try { SetEnvNative(name, value, Overwrite); } catch { }
        }
    }

    /// <summary>Reads the value native code would see (used by tests).</summary>
    internal static string? GetNative(string name)
    {
        if (OperatingSystem.IsWindows()) return Environment.GetEnvironmentVariable(name);
        try { return Marshal.PtrToStringAnsi(GetEnvNative(name)); } catch { return null; }
    }

    [DllImport("libc", EntryPoint = "getenv", SetLastError = true)]
    private static extern IntPtr GetEnvNative(string name);
}
