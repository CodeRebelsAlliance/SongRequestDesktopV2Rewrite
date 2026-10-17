using System;
using System.IO;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Cross-platform application paths used by the download engine and caches.
/// </summary>
public static class AppPaths
{
    public static string BaseDirectory => AppDomain.CurrentDomain.BaseDirectory;

    private static string _appDataDir = InitAppDataDir();

    /// <summary>Per-user application data folder. Settable in tests for isolation.</summary>
    public static string AppDataDir
    {
        get => _appDataDir;
        internal set
        {
            _appDataDir = value;
            try { Directory.CreateDirectory(value); } catch { }
        }
    }

    private static string InitAppDataDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SongRequestDesktopV2Rewrite");
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    /// <summary>Persisted YouTube auth cookies.</summary>
    public static string YoutubeAuthPath => Path.Combine(AppDataDir, "youtube_auth.json");

    private static string _localAppDataDir = InitLocalAppDataDir();

    /// <summary>
    /// Per-user, non-roaming application data folder (used for the generated
    /// Windows uninstaller, which must live outside the install folder and outside
    /// the roaming data folder it may delete). Settable in tests for isolation.
    /// </summary>
    public static string LocalAppDataDir
    {
        get => _localAppDataDir;
        internal set
        {
            _localAppDataDir = value;
            try { Directory.CreateDirectory(value); } catch { }
        }
    }

    private static string InitLocalAppDataDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SongRequestDesktopV2Rewrite");
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    /// <summary>Bundled tools folder next to the app (preferred download target).</summary>
    public static string ToolsDirectory => Path.Combine(BaseDirectory, "tools");

    /// <summary>Per-user tools folder (fallback when the app folder is not writable).</summary>
    public static string UserToolsDirectory => Path.Combine(AppDataDir, "tools");

    /// <summary>Folder where downloaded audio and caches are stored.</summary>
    public static string DownloadPath { get; } = Path.Combine("data", "downloadedvideos");

    public static string ThumbnailCachePath => Path.Combine(DownloadPath, "thumbnail-cache");

    /// <summary>Folder where now-playing artwork is cached for the OS media controls.</summary>
    public static string NowPlayingArtworkDir => Path.Combine(AppDataDir, "now-playing-artwork");

    public static string VideoMetadataCachePath => Path.Combine(DownloadPath, "video-metadata-cache.json");
}
