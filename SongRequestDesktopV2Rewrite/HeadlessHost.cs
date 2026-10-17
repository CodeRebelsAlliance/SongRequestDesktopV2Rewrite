using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// WPF-free host for the Song Requests engine and the Photino web UI.
///
/// Used when the app boots the Photino UI directly (no WPF <c>Application</c>),
/// which is what enables a cross-platform build. It replaces the hidden WPF
/// <c>YoutubeForm</c> host.
/// </summary>
public sealed class HeadlessHost : ISongRequestHost
{
    public SongRequestEngine Engine { get; } = new();
    public MusicEngine Music { get; } = new();
    public YoutubeService YoutubeService { get; }
    public NewUiWindow? NewUiRef { get; set; }
    public Action? OnDataRefreshed { get; set; }

    public bool InitialLoadComplete
    {
        get => Engine.InitialLoadComplete;
        set => Engine.InitialLoadComplete = value;
    }

    public HashSet<string> fetchedYtids
    {
        get => Engine.FetchedYtids;
        set => Engine.FetchedYtids = value;
    }

    public ConcurrentDictionary<string, byte> ProcessingVideos => Engine.ProcessingVideos;
    public SemaphoreSlim DownloadSemaphore => Engine.DownloadSemaphore;

    /// <summary>
    /// Set once the user has finished the startup steps (including the decision about
    /// importing YouTube cookies). Until then — and while no cookies are saved — the
    /// app must not talk to YouTube.
    /// </summary>
    public bool SetupComplete { get; set; }

    /// <summary>
    /// Whether automatic downloads may start: either the user finished the startup
    /// steps (imported cookies or chose to skip) or cookies are already saved.
    /// </summary>
    private bool ReadyForDownloads => SetupComplete || YoutubeCookieService.LoadSaved().Count > 0;

    public HeadlessHost(IReadOnlyList<System.Net.Cookie>? cookies = null)
    {
        var effective = (cookies != null && cookies.Count > 0) ? cookies : YoutubeCookieService.LoadSaved();
        YoutubeService = new YoutubeService(effective);
        Engine.Cache.Changed += () => OnDataRefreshed?.Invoke();

    }


    // No WPF dispatcher: the interop runs on the Photino thread, so run inline.
    public void RunOnUi(Action action) => action();
    public void PostToUi(Action action) => action();

    public void SaveVideoDataCache() => Engine.Cache.Save();
    public VideoData GetOrCreateVideoData(string videoId) => Engine.Cache.GetOrCreate(videoId);
    public string? GetCachedTitle(string videoId) => Engine.Cache.GetCachedTitle(videoId);
    public string? GetCachedCreator(string videoId) => Engine.Cache.GetCachedCreator(videoId);
    public long GetCachedDurationTicks(string videoId) => Engine.Cache.GetCachedDurationTicks(videoId);

    public void HideHostWindow() { }

    public void ShowMusicPlayer() => NewUiRef?.ShowMusicPlayerWindow();
    public void ShowSoundboard() => NewUiRef?.ShowSoundboardWindow();

    public void ShowMusicShare()
    {
        ShowMusicPlayer();
        try
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(new { type = "event", eventName = "openMusicShare" });
            NewUiRef?.MusicPlayerSendMessage?.Invoke(json);
        }
        catch { }
    }

    private Song? CreateSong(string videoId)
    {
        var path = Path.Combine(AppPaths.DownloadPath, $"{videoId}.mp3");
        if (!File.Exists(path)) return null;

        var data = Engine.Cache.GetOrCreate(videoId);
        var title = string.IsNullOrWhiteSpace(data.Title) ? videoId : data.Title;
        var artist = data.Creator ?? "";
        var duration = data.DurationTicks > 0 ? TimeSpan.FromTicks(data.DurationTicks) : TimeSpan.Zero;
        return new Song(title, artist, null, duration, path);
    }

    public Task QueueSongById(string videoId)
    {
        var song = CreateSong(videoId);
        if (song != null)
        {
            Music.AddSongExternal(song);
            ShowMusicPlayer();
        }
        return Task.CompletedTask;
    }

    public Task PlayNextById(string videoId)
    {
        var song = CreateSong(videoId);
        if (song != null)
        {
            Music.AddNextSongExternal(song);
            ShowMusicPlayer();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Processes a newly sent-in request: fetch metadata, download the audio,
    /// cache it and (in Autopilot mode) queue it. No WPF panels.
    /// </summary>
    public async Task AddVideoPanelAsync(string videoUrl, DateTime? timestamp = null, string? message = null, bool? isApproved = null)
    {
        try
        {
            var videoId = YoutubeService.ExtractVideoId(videoUrl);
            if (string.IsNullOrWhiteSpace(videoId) || videoId == "404") return;

            // Never touch YouTube before the user has decided about cookies (or when
            // cookies are already saved): without them downloads fail for restricted
            // videos with a "video unavailable" error, and the user may not even want
            // this one. Leaving the id out of fetchedYtids makes the next fetch retry it.
            if (!ReadyForDownloads)
            {
                fetchedYtids.Remove(videoId);
                return;
            }

            fetchedYtids.Add(videoId);
            var data = GetOrCreateVideoData(videoId);
            data.IsApproved = isApproved ?? false;

            if (string.IsNullOrWhiteSpace(data.Title))
            {
                var (title, length, creator) = await YoutubeService.GetVideoMetadataAsync(videoUrl).ConfigureAwait(false);
                data.Title = title ?? videoId;
                data.Creator = creator ?? "";
                data.DurationTicks = length.Ticks;
                SaveVideoDataCache();
            }

            var mp3 = Path.Combine(AppPaths.DownloadPath, $"{videoId}.mp3");
            if (!File.Exists(mp3))
            {
                await DownloadSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!ProcessingVideos.TryAdd(videoId, 0))
                        return;
                    try { await YoutubeService.DownloadAndConvertVideoAsync(videoUrl, AppPaths.DownloadPath).ConfigureAwait(false); }
                    finally { ProcessingVideos.TryRemove(videoId, out _); }
                }
                finally
                {
                    DownloadSemaphore.Release();
                }
            }

            if (InitialLoadComplete && ConfigService.Instance.Current?.AutoEnqueue == true && File.Exists(mp3))
            {
                var song = CreateSong(videoId);
                if (song != null) Music.AddSongExternal(song);
            }
        }
        catch { }
    }
}
