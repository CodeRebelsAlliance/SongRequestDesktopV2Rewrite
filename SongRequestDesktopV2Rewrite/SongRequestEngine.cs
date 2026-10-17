using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Engine state for the Song Requests feature: the video metadata cache, the
/// fetched / blacklist sets, in-flight processing tracking and the download
/// throttle. WPF-free.
/// </summary>
public sealed class SongRequestEngine
{
    public VideoCache Cache { get; } = new();

    public HashSet<string> FetchedYtids { get; set; } = new();

    public HashSet<string> FetchedBlacklist { get; set; } = new();

    public ConcurrentDictionary<string, byte> ProcessingVideos { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SemaphoreSlim DownloadSemaphore { get; }

    public bool InitialLoadComplete { get; set; }

    public int ConcurrentTaskNumber => Math.Max(1, ConfigService.Instance.Current?.Threads ?? 5);

    public static string DownloadPath => AppPaths.DownloadPath;

    public SongRequestEngine()
    {
        DownloadSemaphore = new SemaphoreSlim(ConcurrentTaskNumber, ConcurrentTaskNumber);
    }
}
