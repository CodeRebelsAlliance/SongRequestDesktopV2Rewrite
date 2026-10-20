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
    public IUiShell? NewUiRef { get; set; }
    public Action? OnDataRefreshed { get; set; }
    public Action<string, object?>? EventSink { get; set; }
    public Action<string, string>? SongDownloaded { get; set; }

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
        var path = DownloadedAudio.PathFor(videoId);
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
    /// <summary>How often a failed automatic download is retried in one run.</summary>
    private const int MaxDownloadAttempts = 3;

    private readonly ConcurrentDictionary<string, int> _downloadAttempts = new(StringComparer.OrdinalIgnoreCase);

    public async Task AddVideoPanelAsync(string videoUrl, DateTime? timestamp = null, string? message = null, bool? isApproved = null)
    {
        string? videoId = null;
        try
        {
            videoId = YoutubeService.ExtractVideoId(videoUrl);
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

            void Progress(string stage, double progress, string? text = null)
                => EventSink?.Invoke("downloadProgress", new { videoId, stage, progress, message = text });

            if (string.IsNullOrWhiteSpace(data.Title))
            {
                Progress("metadata", 0, "Fetching metadata...");
                var (title, length, creator) = await YoutubeService.GetVideoMetadataAsync(videoUrl).ConfigureAwait(false);
                data.Title = title ?? videoId;
                data.Creator = creator ?? "";
                data.DurationTicks = length.Ticks;
                SaveVideoDataCache();
                Progress("metadata", 100, data.Title);
            }

            var mp3 = DownloadedAudio.PathFor(videoId);
            if (!DownloadedAudio.IsComplete(mp3))
            {
                // Bounded retries: a video that is permanently unavailable used to be
                // retried on every poll (or not at all, depending on the marker).
                var attempt = _downloadAttempts.AddOrUpdate(videoId, 1, (_, current) => current + 1);
                if (attempt > MaxDownloadAttempts)
                {
                    fetchedYtids.Add(videoId);
                    return;
                }

                Progress("downloading", 0, "Downloading...");

                await DownloadSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Re-check after waiting: another path (the UI's download button,
                    // the previous queue entry) may have finished the same video while
                    // this call sat on the semaphore. Without this the file was
                    // downloaded a second time and overwritten.
                    mp3 = DownloadedAudio.PathFor(videoId);
                    if (DownloadedAudio.IsComplete(mp3)) return;

                    if (!ProcessingVideos.TryAdd(videoId, 0))
                        return;
                    try { await YoutubeService.DownloadAndConvertVideoAsync(videoUrl, AppPaths.DownloadPath).ConfigureAwait(false); }
                    finally { ProcessingVideos.TryRemove(videoId, out _); }
                }
                finally
                {
                    DownloadSemaphore.Release();
                }

                Progress("downloading", 100, "Download complete");
            }

            // The automatic request pipeline downloads sent-in songs; cache their
            // YouTube captions here so the lyrics fallback works later without a
            // network request. Manual/local library songs are not touched.
            if (DownloadedAudio.IsComplete(mp3) && !data.SubtitlesFetched)
            {
                try
                {
                    var subtitles = await YoutubeService.TryGetSubtitlesAsync(videoUrl).ConfigureAwait(false);
                    data.SubtitleText = subtitles?.PlainText ?? string.Empty;
                    data.SubtitleTimedText = subtitles?.TimedLrcText ?? string.Empty;
                    data.SubtitlesFetched = true;
                    SaveVideoDataCache();
                }
                catch
                {
                    // Captions are optional; never fail the download over them.
                }
            }

            // The automatic fetch loop is the normal path for song requests, so it
            // must honour the "add downloads to the library automatically" setting
            // too (it used to be applied only by the manual download button).
            if (DownloadedAudio.IsComplete(mp3))
                SongDownloaded?.Invoke(videoId, mp3);

            if (InitialLoadComplete && ConfigService.Instance.Current?.AutoEnqueue == true && File.Exists(mp3))
            {
                var song = CreateSong(videoId);
                if (song != null) Music.AddSongExternal(song);
            }

            Progress("complete", 100, data.Title);
        }
        catch (Exception ex)
        {
            // Retry on a later fetch while the attempt budget lasts.
            if (videoId != null)
            {
                var attempt = _downloadAttempts.TryGetValue(videoId, out var count) ? count : 0;
                if (attempt >= MaxDownloadAttempts) fetchedYtids.Add(videoId);
                else fetchedYtids.Remove(videoId);
            }

            EventSink?.Invoke("downloadProgress", new
            {
                videoId,
                stage = "error",
                progress = 0,
                message = ex.Message
            });
        }
    }
}
