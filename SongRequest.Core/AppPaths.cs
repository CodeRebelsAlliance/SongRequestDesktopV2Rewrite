using System;
using System.Collections.Generic;
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

    /// <summary>Disk cache of BPM/loudness/drop analyses (one JSON document).</summary>
    public static string AnalysisCachePath => Path.Combine(AppDataDir, "song-analysis-cache.json");

    /// <summary>Folder holding generated karaoke/instrumental versions.</summary>
    public static string KaraokePath => Path.Combine(DataRoot, "karaoke");

    /// <summary>Index linking source songs to generated karaoke versions.</summary>
    public static string KaraokeIndexPath => Path.Combine(AppDataDir, "karaoke-versions.json");

    /// <summary>
    /// The pre-announcement chime. Ships next to the desktop executable; on mobile
    /// the host materialises it from the app package into the data root, which is
    /// the only place the app can read it from.
    /// </summary>
    public static string ChimePath => Path.Combine(DataRoot, "announcement.mp3");

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

    /// <summary>
    /// Root of every writable data folder (downloads, caches, library).
    ///
    /// Desktop: the application directory, so the historical relative
    /// <see cref="DownloadPath"/> keeps resolving to the same place. Mobile hosts
    /// call <see cref="UseDataRoot"/> with their sandbox root (the app bundle is
    /// read-only there) before anything else touches these paths.
    /// </summary>
    public static string DataRoot { get; internal set; } = BaseDirectory;

    /// <summary>Folder where downloaded audio and caches are stored.</summary>
    public static string DownloadPath { get; internal set; } = Path.Combine("data", "downloadedvideos");

    /// <summary>Folder holding the library database and artwork.</summary>
    public static string LibraryDataDir { get; internal set; } = Path.Combine(BaseDirectory, "data", "library");

    public static string ThumbnailCachePath => Path.Combine(DownloadPath, "thumbnail-cache");

    /// <summary>Folder where now-playing artwork is cached for the OS media controls.</summary>
    public static string NowPlayingArtworkDir => Path.Combine(AppDataDir, "now-playing-artwork");

    public static string VideoMetadataCachePath => Path.Combine(DownloadPath, "video-metadata-cache.json");

    /// <summary>
    /// Prefix marking a path stored relative to <see cref="DataRoot"/>.
    ///
    /// Mobile sandboxes move between installs (iOS changes the container path), so
    /// files the app imported into its own storage are persisted as <c>~/…</c> and
    /// resolved again on load. Paths outside the data root (a user's own music
    /// folder) stay absolute — they are the user's, not ours to rewrite.
    /// </summary>
    public const string PortablePrefix = "~/";

    /// <summary>Makes a path portable when it lives under the data root.</summary>
    public static string ToPortablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;
        if (path.StartsWith(PortablePrefix, StringComparison.Ordinal)) return path;

        try
        {
            var full = Path.GetFullPath(path);

            // The same directory can be spelled more than one way (Android exposes
            // the app sandbox as both /data/data/<pkg> and /data/user/0/<pkg>), so
            // compare against every spelling of the root.
            foreach (var root in RootCandidates())
            {
                if (full.StartsWith(root, StringComparison.Ordinal))
                {
                    var relative = full[root.Length..].Replace(Path.DirectorySeparatorChar, '/');
                    return PortablePrefix + relative;
                }
            }
        }
        catch
        {
            // Unresolvable path: keep it as it is.
        }

        return path;
    }

    /// <summary>The data root in every spelling the platform may hand us.</summary>
    private static IEnumerable<string> RootCandidates()
    {
        var roots = new List<string>(3);

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            try
            {
                var normalized = Path.GetFullPath(candidate);
                if (!normalized.EndsWith(Path.DirectorySeparatorChar))
                    normalized += Path.DirectorySeparatorChar;
                if (!roots.Contains(normalized)) roots.Add(normalized);
            }
            catch
            {
                // Ignore unusable candidates.
            }
        }

        Add(DataRoot);

        try
        {
            // Resolves a symlinked sandbox (Android's /data/data vs /data/user/0).
            var resolved = Directory.ResolveLinkTarget(DataRoot, returnFinalTarget: true);
            Add(resolved?.FullName);
        }
        catch
        {
            // Not a link, or not supported: the plain root is enough.
        }

        return roots;
    }

    /// <summary>Resolves a portable path back to an absolute one.</summary>
    public static string ResolvePortablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;
        if (!path.StartsWith(PortablePrefix, StringComparison.Ordinal)) return path;

        var relative = path[PortablePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(DataRoot, relative);
    }

    /// <summary>
    /// Points every writable folder at a host-provided root and creates it.
    ///
    /// Used by the mobile hosts, whose writable storage lives in a sandbox the
    /// app must be told about. The desktop never calls this: leaving
    /// <see cref="DownloadPath"/> relative preserves the behaviour of existing
    /// installations exactly.
    /// </summary>
    public static void UseDataRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A data root is required.", nameof(root));

        DataRoot = root;
        DownloadPath = Path.Combine(root, "downloadedvideos");
        LibraryDataDir = Path.Combine(root, "library");
        AppDataDir = root;
        LocalAppDataDir = root;

        foreach (var dir in new[] { DownloadPath, LibraryDataDir, AppDataDir, LocalAppDataDir, KaraokePath })
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
    }
}
