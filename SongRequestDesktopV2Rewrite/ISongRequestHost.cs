using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Abstraction over the object that hosts the Song Requests engine and the web
/// UI bridge.
///
/// Implemented by <see cref="HeadlessHost"/> so the app boots the Photino UI
/// directly, without any WPF/Windows dependency.
/// </summary>
public interface ISongRequestHost
{
    YoutubeService YoutubeService { get; }

    /// <summary>WPF-free player engine (queue, playback, remote state).</summary>
    MusicEngine Music { get; }

    NewUiWindow? NewUiRef { get; set; }

    SongRequestEngine Engine { get; }

    Action? OnDataRefreshed { get; set; }

    bool InitialLoadComplete { get; set; }
    HashSet<string> fetchedYtids { get; set; }
    ConcurrentDictionary<string, byte> ProcessingVideos { get; }
    SemaphoreSlim DownloadSemaphore { get; }

    /// <summary>Runs an action synchronously on the host/UI thread.</summary>
    void RunOnUi(Action action);

    /// <summary>Posts an action to the host/UI thread.</summary>
    void PostToUi(Action action);

    void SaveVideoDataCache();
    VideoData GetOrCreateVideoData(string videoId);
    string? GetCachedTitle(string videoId);
    string? GetCachedCreator(string videoId);
    long GetCachedDurationTicks(string videoId);

    Task AddVideoPanelAsync(string videoUrl, DateTime? timestamp = null, string? message = null, bool? isApproved = null);
    Task QueueSongById(string videoId);
    Task PlayNextById(string videoId);

    void ShowMusicPlayer();
    void ShowSoundboard();
    void ShowMusicShare();

    /// <summary>Hides the host's legacy window (no-op for a headless host).</summary>
    void HideHostWindow();
}
