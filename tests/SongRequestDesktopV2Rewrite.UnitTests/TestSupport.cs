using System.Reflection;
using System.Runtime.CompilerServices;
using SongRequestDesktopV2Rewrite;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Shared helpers. The module initializer points the app-data folder at a temp
/// directory before any test runs, so nothing ever touches the real user config.
/// </summary>
internal static class TestSupport
{
    [ModuleInitializer]
    internal static void Bootstrap()
    {
        // Run OpenAL headless: the unit suite reaches audio-device enumeration
        // through the soundboard interop commands, which would otherwise touch a
        // real (absent) sound card on CI. The same value is applied by
        // tests/coverage.runsettings for every run that uses it.
        Environment.SetEnvironmentVariable("ALSOFT_DRIVERS", "null");

        AppPaths.AppDataDir = NewTempDir("appdata");
        AppPaths.LocalAppDataDir = NewTempDir("localappdata");
    }

    public static string NewTempDir(string prefix = "tmp")
    {
        var dir = Path.Combine(Path.GetTempPath(), "sr-unit-tests", prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Reads a property off an anonymous result object returned by the engine.</summary>
    public static T Prop<T>(object obj, string name)
    {
        var value = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
        return (T)value!;
    }

    public static string SoundboardConfigPath()
        => Path.Combine(AppContext.BaseDirectory, "data", "soundboard_config.json");

    /// <summary>Removes the soundboard config so a fresh engine starts from defaults.</summary>
    public static void ResetSoundboardConfig()
    {
        var path = SoundboardConfigPath();
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Removes the persisted library so a fresh service starts empty.</summary>
    public static void ResetLibrary()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "library", "library.json");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>A path that lives under the app's download folder (lyrics "sent in song" detection).</summary>
    public static string DownloadedSongPath(string file = "abc.mp3")
        => Path.Combine(Path.GetFullPath(AppPaths.DownloadPath), file);
}
