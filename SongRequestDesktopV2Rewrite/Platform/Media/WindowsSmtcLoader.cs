using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Loads the optional Windows-only SMTC companion assembly
/// (<c>SongRequestDesktopV2Rewrite.Smtc.Windows.dll</c>) at runtime.
///
/// The companion targets <c>net10.0-windows10.0.19041.0</c> (WinRT/CsWinRT) and
/// is published only for <c>win-*</c> RIDs, so the main app stays a single
/// cross-platform <c>net10.0</c> assembly. It is loaded into a dedicated
/// <see cref="AssemblyLoadContext"/> so the WinRT projection assemblies stay
/// isolated, while the already-loaded app assembly is shared — that keeps type
/// identity for <see cref="ISystemMediaSession"/> so the result can be cast.
///
/// Absent the assembly (non-Windows, or a build without it) this simply reports
/// "not available" and the app falls back to <see cref="NullSystemMediaSession"/>.
/// </summary>
internal static class WindowsSmtcLoader
{
    internal const string AssemblyFileName = "SongRequestDesktopV2Rewrite.Smtc.Windows.dll";
    internal const string DepsFileName = "SongRequestDesktopV2Rewrite.Smtc.Windows.deps.json";
    internal const string TypeName = "SongRequestDesktopV2Rewrite.Smtc.SmtcMediaSession";
    internal const string EntryPointName = "Create";

    private static readonly Lazy<bool> _available = new(() =>
    {
        try
        {
            return OperatingSystem.IsWindows()
                && File.Exists(Path.Combine(AppContext.BaseDirectory, AssemblyFileName));
        }
        catch
        {
            return false;
        }
    });

    /// <summary>Whether the SMTC companion assembly ships next to the app.</summary>
    internal static bool Available => _available.Value;

    /// <summary>Resolves the companion assembly path (or null when it is not deployed).</summary>
    internal static string? ResolvePath()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, AssemblyFileName);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates the SMTC session for <paramref name="windowHandle"/>, or null when
    /// the companion is unavailable or SMTC cannot be attached.
    /// </summary>
    internal static ISystemMediaSession? TryCreate(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var path = ResolvePath();
            if (path == null) return null;

            var assembly = LoadCompanion(path);
            var type = assembly?.GetType(TypeName, throwOnError: false);
            var method = type?.GetMethod(
                EntryPointName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(IntPtr) },
                modifiers: null);

            if (method == null) return null;

            return method.Invoke(null, new object[] { windowHandle }) as ISystemMediaSession;
        }
        catch
        {
            return null;
        }
    }

    private static Assembly? LoadCompanion(string assemblyPath)
    {
        var depsPath = Path.Combine(Path.GetDirectoryName(assemblyPath) ?? ".", DepsFileName);

        // No dependency manifest (e.g. a loose copy): load it plainly.
        if (!File.Exists(depsPath))
            return Assembly.LoadFrom(assemblyPath);

        var resolver = new AssemblyDependencyResolver(assemblyPath);
        var context = new AssemblyLoadContext("SongRequest.Smtc", isCollectible: false);

        context.Resolving += (ctx, name) =>
        {
            // Share assemblies already loaded by the app (chiefly the app itself,
            // so `as ISystemMediaSession` keeps working).
            var shared = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
            if (shared != null) return shared;

            var resolved = resolver.ResolveAssemblyToPath(name);
            return resolved != null ? ctx.LoadFromAssemblyPath(resolved) : null;
        };

        return context.LoadFromAssemblyPath(assemblyPath);
    }
}
