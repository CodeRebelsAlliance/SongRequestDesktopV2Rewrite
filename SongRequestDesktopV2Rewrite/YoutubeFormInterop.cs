using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace SongRequestDesktopV2Rewrite;

public class YoutubeFormInterop
{
    private readonly YoutubeForm _ytForm;
    private readonly YoutubeService _ytService;
    private readonly LibraryService _libraryService;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();

    public Action<string>? SendMessage { get; set; }
    public YoutubeForm YtForm => _ytForm;
    public LibraryService LibraryService => _libraryService;

    private string? _currentSongPath;
    private bool _musicPlayerSubscribed;
    private LyricsService? _musicPlayerLyricsService;
    private readonly object _musicPlayerLyricsCacheLock = new();
    private readonly System.Collections.Generic.Dictionary<string, LyricsResult> _musicPlayerLyricsCache = new(StringComparer.OrdinalIgnoreCase);

    // MusicShare interop state
    private MusicShareService? _musicShareService;
    private bool _musicShareSharing;
    private bool _musicShareReceiving;
    private bool _musicShareMetadataOnly = true;
    private DateTime _musicShareSessionStart;
    private long _musicShareBytesSent;
    private readonly System.Collections.Generic.List<float> _musicShareAccumulatedSamples = new();
    private const int MUSIC_SHARE_TARGET_SAMPLES = 44100;
    private LyricsService? _musicShareLyricsService;
    private Song? _musicShareLastSong;
    private string? _musicShareCachedLyrics;
    private bool _musicShareCachedHasSyncedLyrics;
    private string? _musicShareCachedThumbnail;
    private sealed class CachedSubtitleData
    {
        public string SubtitleText { get; set; } = string.Empty;
        public string SubtitleTimedText { get; set; } = string.Empty;
    }

    public YoutubeFormInterop(YoutubeForm ytForm, YoutubeService ytService)
    {
        _ytForm = ytForm;
        _ytService = ytService;
        _libraryService = new LibraryService();
    }

    public async void HandleMessage(string json)
    {
        try
        {
            var msg = JObject.Parse(json);
            var type = msg["type"]?.ToString();

            switch (type)
            {
                case "request":
                    await HandleRequest(msg);
                    break;
                case "response":
                    HandleResponse(msg);
                    break;
            }
        }
        catch { }
    }

    /// <summary>
    /// Handle a message from a specific window, routing responses back to that window.
    /// </summary>
    public async void HandleMessage(string json, Action<string> responseTarget)
    {
        _responseTarget = responseTarget;
        try
        {
            var msg = JObject.Parse(json);
            var type = msg["type"]?.ToString();

            switch (type)
            {
                case "request":
                    await HandleRequest(msg);
                    break;
                case "response":
                    HandleResponse(msg);
                    break;
            }
        }
        catch { }
        finally
        {
            _responseTarget = null;
        }
    }

    [ThreadStatic]
    private static Action<string>? _responseTarget;

    private async Task HandleRequest(JObject msg)
    {
        var id = msg["id"]?.ToString();
        var method = msg["method"]?.ToString();
        var responseTarget = _responseTarget ?? SendMessage;

        try
        {
            switch (method)
            {
                case "showMusicPlayer":
                    ShowMusicPlayer();
                    SendResponse(id, new { success = true });
                    return;
                case "closeMusicPlayer":
                    CloseNewMusicPlayer();
                    return;
                case "showSoundboard":
                    ShowSoundboard();
                    SendResponse(id, new { success = true });
                    return;
                case "showSettings":
                    ShowSettings();
                    SendResponse(id, new { success = true });
                    return;
                case "playPause":
                    _ytForm.Dispatcher.BeginInvoke(() => _ytForm.MusicPlayer?.RemoteTogglePlayPause());
                    SendResponse(id, new { success = true });
                    return;
                case "skipNext":
                    _ytForm.Dispatcher.BeginInvoke(() => _ytForm.MusicPlayer?.RemoteSkipNext());
                    SendResponse(id, new { success = true });
                    return;
                case "skipPrevious":
                    _ytForm.Dispatcher.BeginInvoke(() => _ytForm.MusicPlayer?.RemotePrevious());
                    SendResponse(id, new { success = true });
                    return;
                case "stop":
                    _ytForm.Dispatcher.BeginInvoke(() => _ytForm.MusicPlayer?.RemoteStop());
                    SendResponse(id, new { success = true });
                    return;
                case "seek":
                {
                    var posVal = msg["position"]?.ToObject<double>();
                    if (posVal != null)
                    {
                        double pos = posVal.Value;
                        _ytForm.Dispatcher.BeginInvoke(() => _ytForm.MusicPlayer?.RemoteSeek(pos));
                    }
                    SendResponse(id, new { success = true });
                    return;
                }
                case "toggleRepeat":
                {
                    _ytForm.Dispatcher.BeginInvoke(() => _ytForm.MusicPlayer?.RemoteToggleRepeat());
                    SendResponse(id, new { success = true });
                    return;
                }
                case "clearQueue":
                    _ytForm.Dispatcher.BeginInvoke(() => _ytForm.MusicPlayer?.ClearQueue(false));
                    SendResponse(id, new { success = true });
                    return;
                case "musicPlayerReady":
                {
                    _ytForm.Dispatcher.BeginInvoke(() =>
                    {
                        SubscribeToMusicPlayer();
                        SendQueueUpdate();
                        double volInit = 0.8, cfInit = 4.0;
                        bool canControlInit = true;
                        var mp = _ytForm.MusicPlayer;
                        if (mp != null)
                        {
                            volInit = mp.RemoteVolumeValue;
                            cfInit = mp.CrossfadeSlider.Value;
                            canControlInit = mp.RemoteCanControlVolume;
                        }
                        SendResponse(id, new { success = true, volume = volInit, crossfade = cfInit, canControlVolume = canControlInit });
                        SendCurrentNowPlayingToNewUI();
                    });
                    return;
                }
                case "shuffleQueue":
                {
                    _ytForm.Dispatcher.BeginInvoke(() => _ytForm.MusicPlayer?.RemoteShuffleQueue());
                    SendResponse(id, new { success = true });
                    return;
                }
                case "setVolume":
                {
                    var volVal = msg["value"]?.ToObject<double>() ?? -1;
                    if (volVal >= 0)
                        _ytForm.Dispatcher.BeginInvoke(() =>
                        {
                            if (_ytForm.MusicPlayer != null)
                                _ytForm.MusicPlayer.VolumeSlider.Value = Math.Clamp(volVal, 0.0, 1.0);
                        });
                    SendResponse(id, new { success = true });
                    return;
                }
                case "setCrossfade":
                {
                    var cfVal = msg["value"]?.ToObject<double>() ?? -1;
                    if (cfVal >= 0)
                        _ytForm.Dispatcher.BeginInvoke(() =>
                        {
                            if (_ytForm.MusicPlayer != null)
                                _ytForm.MusicPlayer.CrossfadeSlider.Value = Math.Clamp(cfVal, 0.0, 10.0);
                        });
                    SendResponse(id, new { success = true });
                    return;
                }
                case "getVolumeCrossfade":
                {
                    double volVal = 0.8, cfVal2 = 4.0;
                    bool canControlVal = true;
                    _ytForm.Dispatcher.Invoke(() =>
                    {
                        var mp = _ytForm.MusicPlayer;
                        if (mp != null)
                        {
                            volVal = mp.RemoteVolumeValue;
                            cfVal2 = mp.CrossfadeSlider.Value;
                            canControlVal = mp.RemoteCanControlVolume;
                        }
                    });
                    SendResponse(id, new { volume = volVal, crossfade = cfVal2, canControlVolume = canControlVal });
                    return;
                }
                case "addLocalFiles":
                    var mode = msg["mode"]?.ToString() ?? "add";
                    _ytForm.Dispatcher.BeginInvoke(() =>
                    {
                        var dialog = new Microsoft.Win32.OpenFileDialog
                        {
                            Filter = "Audio files|*.mp3;*.wav;*.m4a;*.flac|All files|*.*",
                            Multiselect = true
                        };
                        if (dialog.ShowDialog() == true)
                        {
                            var mp = _ytForm.MusicPlayer;
                            if (mp != null)
                            {
                                foreach (var filePath in dialog.FileNames)
                                {
                                    if (mp.TryCreateSongFromFile(filePath, out var song, out _))
                                    {
                                        if (mode == "addNext")
                                            mp.AddNextSongExternal(song);
                                        else
                                            mp.AddSongExternal(song);
                                    }
                                }
                            }
                        }
                    });
                    SendResponse(id, new { success = true });
                    return;
                case "removeQueueItem":
                    var idx = msg["index"]?.ToObject<int>() ?? -1;
                    if (idx >= 0)
                        _ytForm.Dispatcher.BeginInvoke(() => _ytForm.MusicPlayer?.RemoveQueueItem(idx));
                    SendResponse(id, new { success = true });
                    return;
                case "moveQueueItem":
                    var fromIdx = msg["fromIndex"]?.ToObject<int>() ?? -1;
                    var toIdx = msg["toIndex"]?.ToObject<int>() ?? -1;
                    if (fromIdx >= 0 && toIdx >= 0)
                        _ytForm.Dispatcher.BeginInvoke(() => _ytForm.MusicPlayer?.MoveQueueItem(fromIdx, toIdx));
                    SendResponse(id, new { success = true });
                    return;
                case "requestLyrics":
                {
                    _ytForm.Dispatcher.BeginInvoke(() => _ = FetchAndSendLyricsForSongAsync());
                    SendResponse(id, new { success = true });
                    return;
                }
            }

            object result;
            switch (method)
            {
                case "fetchData":
                    result = await FetchDataAsync();
                    break;
                case "config":
                    result = GetConfig();
                    break;
                case "approve":
                    result = await SendApprovalAsync(msg["ytid"]?.ToString(), true);
                    break;
                case "unapprove":
                    result = await SendApprovalAsync(msg["ytid"]?.ToString(), false);
                    break;
                case "delete":
                    result = await SendOtherAsync("delete", msg["ytid"]?.ToString());
                    break;
                case "blacklist":
                    result = await SendOtherAsync("blacklist", msg["ytid"]?.ToString());
                    break;
                case "unblacklist":
                    result = await SendOtherAsync("unblacklist", msg["ytid"]?.ToString());
                    break;
                case "getBadWords":
                    result = await GetBadWordsAsync();
                    break;
                case "addBadWord":
                    result = await SendWordFilterAsync("add-bad-word", msg["word"]?.ToString());
                    break;
                case "deleteBadWord":
                    result = await SendWordFilterAsync("delete-bad-word", msg["word"]?.ToString());
                    break;
                case "getLyrics":
                    result = await GetLyricsAsync(msg["videoId"]?.ToString(), msg["videoUrl"]?.ToString());
                    break;
                case "searchLyrics":
                    result = SearchLyricsAsync(msg["query"]?.ToString());
                    break;
                case "getThumbnail":
                    result = await GetThumbnailBase64Async(msg["videoId"]?.ToString());
                    break;
                case "downloadVideo":
                    result = await DownloadVideoAsync(msg["videoUrl"]?.ToString());
                    break;
                case "searchYoutube":
                    result = await SearchYoutubeAsync(msg["query"]?.ToString());
                    break;
                case "submitSong":
                    result = await SubmitSongAsync(msg["videoId"]?.ToString(), msg["message"]?.ToString());
                    break;
                case "queueSong":
                    result = QueueSongAsync(msg["ytid"]?.ToString());
                    break;
                case "playNext":
                    result = PlayNextAsync(msg["ytid"]?.ToString());
                    break;
                case "getVersion":
                    result = new { version = About.version };
                    break;
                case "checkForUpdates":
                    result = await CheckForUpdatesAsync();
                    break;
                case "getSettings":
                    result = GetFullConfig();
                    break;
                case "saveSettings":
                    result = await SaveSettingsAsync(msg);
                    break;
                case "toggleSendin":
                    result = await ToggleSendinAsync();
                    break;
                case "getSendinStatus":
                    result = await GetSendinStatusAsync();
                    break;
                case "openAuthUrl":
                    result = OpenAuthUrl();
                    break;
                case "getRemoteConfig":
                    result = GetRemoteConfig();
                    break;
                case "getStartupData":
                    result = GetStartupData();
                    break;
                case "saveLaunchOptions":
                    result = SaveLaunchOptions(msg);
                    break;
                case "triggerAuth":
                    result = await TriggerAuthAsync();
                    break;
                case "completeSetup":
                    result = CompleteSetup(msg);
                    break;
                case "installUpdate":
                    result = await InstallUpdateAsync(msg);
                    break;

                // ── Library ──────────────────────────────
                case "scanLibrary":
                    result = await ScanLibraryAsync();
                    break;
                case "cancelLibraryScan":
                    _libraryService.CancelScan();
                    result = new { success = true };
                    break;
                case "getLibraryStatus":
                    result = GetLibraryStatus();
                    break;
                case "getLibrarySongs":
                    result = GetLibrarySongs(msg);
                    break;
                case "searchLibrary":
                    result = SearchLibrary(msg);
                    break;
                case "deleteLibrarySong":
                    result = DeleteLibrarySong(msg);
                    break;
                case "updateLibrarySongMetadata":
                    result = UpdateLibrarySongMetadata(msg);
                    break;
                case "addLibraryScanFolder":
                    result = AddLibraryScanFolder(msg);
                    break;
                case "removeLibraryScanFolder":
                    result = RemoveLibraryScanFolder(msg);
                    break;
                case "importToLibrary":
                    result = ImportToLibrary(msg);
                    break;
                case "getLibraryStats":
                    result = GetLibraryStats();
                    break;
                case "flagLibrarySongMissing":
                    result = FlagLibrarySongMissing(msg);
                    break;
                case "playLibrarySong":
                    result = PlayLibrarySong(msg);
                    break;
                case "queueLibrarySong":
                    result = QueueLibrarySong(msg);
                    break;
                case "playLibrarySongNext":
                    result = PlayLibrarySongNext(msg);
                    break;
                case "downloadAndPlayYouTube":
                    result = await DownloadAndPlayYouTubeAsync(msg);
                    break;

                case "musicShareStart":
                    result = await MusicShareStart(msg);
                    break;
                case "musicShareStop":
                    result = await MusicShareStop();
                    break;
                case "musicShareConnect":
                    result = await MusicShareConnect(msg);
                    break;
                case "musicShareDisconnect":
                    result = await MusicShareDisconnect();
                    break;
                case "musicShareGetStatus":
                    result = MusicShareGetStatus();
                    break;

                default:
                    result = new { error = $"Unknown method: {method}" };
                    break;
            }

            SendResponse(id, result, responseTarget);
        }
        catch (Exception ex)
        {
            SendResponse(id, new { error = ex.Message }, responseTarget);
        }
    }

    private void HandleResponse(JObject msg)
    {
        var id = msg["id"]?.ToString();
        if (id != null && _pendingRequests.TryRemove(id, out var tcs))
        {
            tcs.TrySetResult(msg["result"]?.ToString(Formatting.None) ?? "null");
        }
    }

    private void SendResponse(string? id, object? result, Action<string>? target = null)
    {
        if (id == null) return;
        var json = JsonConvert.SerializeObject(new { type = "response", id, result });
        (target ?? _responseTarget ?? SendMessage)?.Invoke(json);
    }

    public void SendEvent(string eventName, object? data)
    {
        var json = JsonConvert.SerializeObject(new { type = "event", eventName, data });
        SendMessage?.Invoke(json);
    }

    private async Task<object> FetchDataAsync()
    {
        var cfg = ConfigService.Instance.Current;
        if (cfg == null) return new { error = "No config" };

        var baseUrl = (cfg.Address ?? "http://127.0.0.1:5000") + "/fetch?method=";
        string dbJson, blacklistJson;

        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) })
        {
            client.DefaultRequestHeaders.Add("Authorization", cfg.BearerToken);

            var dbResponse = await client.PostAsync(baseUrl + "get-database", new MultipartFormDataContent()).ConfigureAwait(false);
            dbResponse.EnsureSuccessStatusCode();
            dbJson = await dbResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            var blResponse = await client.PostAsync(baseUrl + "get-blacklist", new MultipartFormDataContent()).ConfigureAwait(false);
            blResponse.EnsureSuccessStatusCode();
            blacklistJson = await blResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        // Server returns positional arrays: [ytid, message, isApproved, unixTimestamp]
        var db = JArray.Parse(dbJson);
        var databaseList = new List<object>();
        foreach (var entry in db)
        {
            var arr = entry as JArray;
            if (arr == null || arr.Count < 1) continue;
            var ytid = arr[0]?.ToString() ?? "";
            var message = arr.Count > 1 ? arr[1]?.ToString() ?? "" : "";
            var isApproved = arr.Count > 2 ? arr[2]?.ToObject<bool>() ?? false : false;
            var timestamp = arr.Count > 3 ? arr[3]?.ToObject<double?>() : null;

            var title = _ytForm.GetCachedTitle(ytid) ?? ytid;
            var creator = _ytForm.GetCachedCreator(ytid) ?? "";
            var durationTicks = _ytForm.GetCachedDurationTicks(ytid);

            databaseList.Add(new
            {
                ytid,
                title,
                creator,
                isApproved,
                message,
                sentInTimestamp = timestamp,
                durationTicks
            });
        }

        // Server returns array of strings; enrich with cached metadata
        var blacklistArr = JArray.Parse(blacklistJson);
        var blacklistItems = new List<object>();
        foreach (var item in blacklistArr)
        {
            var ytid = item.ToString();
            blacklistItems.Add(new
            {
                ytid,
                title = _ytForm.GetCachedTitle(ytid) ?? ytid,
                creator = _ytForm.GetCachedCreator(ytid) ?? "",
                isApproved = false,
                sentInTimestamp = (double?)null,
                durationTicks = _ytForm.GetCachedDurationTicks(ytid)
            });
        }

        return new { database = databaseList, blacklist = blacklistItems };
    }

    private object GetConfig()
    {
        var cfg = ConfigService.Instance.Current;
        if (cfg == null) return new { };
        return new
        {
            address = cfg.Address,
            fetchingTimer = cfg.FetchingTimer,
            threads = cfg.Threads,
            autoEnqueue = cfg.AutoEnqueue,
            requestUrl = cfg.RequestUrl,
            presentationFullscreen = cfg.PresentationFullscreen,
            normalizeVolume = cfg.NormalizeVolume
        };
    }

    private async Task<object> SendApprovalAsync(string? ytid, bool approve)
    {
        var cfg = ConfigService.Instance.Current;
        if (cfg == null || string.IsNullOrEmpty(ytid)) return new { error = "Invalid params" };
        var fts = new FetchingService(cfg.BearerToken);
        var result = await fts.SendRequest(approve ? "approve" : "unapprove", ytid).ConfigureAwait(false);
        return new { success = true, response = result };
    }

    private async Task<object> SendOtherAsync(string action, string? ytid)
    {
        var cfg = ConfigService.Instance.Current;
        if (cfg == null || string.IsNullOrEmpty(ytid)) return new { error = "Invalid params" };
        var fts = new FetchingService(cfg.BearerToken);
        var result = await fts.SendRequest(action, ytid).ConfigureAwait(false);
        return new { success = true, response = result };
    }

    private async Task<object> GetBadWordsAsync()
    {
        var cfg = ConfigService.Instance.Current;
        if (cfg == null) return new { words = new string[0] };
        var fts = new FetchingService(cfg.BearerToken);
        var result = await fts.SendRequest("get-bad-words").ConfigureAwait(false);
        var arr = JArray.Parse(result);
        return new { words = arr.Select(t => t.ToString()).ToArray() };
    }

    private async Task<object> SendWordFilterAsync(string action, string? word)
    {
        var cfg = ConfigService.Instance.Current;
        if (cfg == null || string.IsNullOrEmpty(word)) return new { error = "Invalid params" };
        var fts = new FetchingService(cfg.BearerToken);
        var result = await fts.SendRequest(action, word).ConfigureAwait(false);
        return new { success = true, response = result };
    }

    private async Task<object> GetLyricsAsync(string? videoId, string? videoUrl)
    {
        if (string.IsNullOrEmpty(videoId)) return new { error = "No videoId" };
        try
        {
            var title = _ytForm.GetCachedTitle(videoId) ?? "";
            var artist = _ytForm.GetCachedCreator(videoId) ?? "";
            var lyrics = await _ytService.GetLyricsAsync(artist, title, videoId).ConfigureAwait(false);
            return new { lyrics = lyrics ?? "", title, artist };
        }
        catch (Exception ex)
        {
            try
            {
                var subtitles = await _ytService.TryGetSubtitlesAsync(videoId).ConfigureAwait(false);
                var subtitleText = subtitles?.PlainText ?? "";
                return new { lyrics = subtitleText, source = "subtitles" };
            }
            catch
            {
                return new { error = ex.Message };
            }
        }
    }

    private object SearchLyricsAsync(string? query)
    {
        if (string.IsNullOrEmpty(query)) return new { error = "No query" };
        var url = $"https://duckduckgo.com/?q={Uri.EscapeDataString(query)}";
        return new { url };
    }

    private async Task<object> GetThumbnailBase64Async(string? videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return new { error = "No videoId" };
        try
        {
            var cachePath = Path.Combine("data", "downloadedvideos", "thumbnail-cache", $"{videoId}.png");
            if (File.Exists(cachePath))
            {
                var bytes = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
                return new { mime = "image/png", data = Convert.ToBase64String(bytes) };
            }
            var thumbUrl = await _ytService.GetThumbnailUrlAsync(videoId).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(thumbUrl))
            {
                using var client = new System.Net.Http.HttpClient();
                var bytes = await client.GetByteArrayAsync(thumbUrl).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
                return new { mime = "image/jpeg", data = Convert.ToBase64String(bytes) };
            }
            return new { error = "No thumbnail" };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task<object> DownloadVideoAsync(string? videoUrl)
    {
        if (string.IsNullOrEmpty(videoUrl)) return new { error = "No URL" };
        try
        {
            var downloadPath = YoutubeForm.downloadPath;
            var result = await _ytService.DownloadAndConvertVideoAsync(videoUrl, downloadPath).ConfigureAwait(false);

            // Auto-add to library if enabled
            if (ConfigService.Instance.Current?.LibraryAutoAddDownloads == true
                && !string.IsNullOrEmpty(result) && File.Exists(result))
            {
                var youTubeId = YoutubeService.ExtractVideoId(videoUrl);
                _libraryService.ImportDownloadedSong(result, youTubeId, videoUrl);
            }

            return new { success = true, path = result };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task<object> SearchYoutubeAsync(string? query, int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(query)) return new { error = "Query is empty" };
        var results = await _ytService.SearchAsync(query, maxResults);
        return new
        {
            results = results.Select(r => new
            {
                videoId = r.VideoId,
                title = r.Title,
                author = r.Author,
                duration = r.Duration?.ToString() ?? ""
            }).ToList()
        };
    }

    private static string NormalizeVideoId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var trimmed = input.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(trimmed,
            @"^(?:https?://)?(?:www\.)?(?:youtube\.com/(?:watch\?v=|embed/|v/)|youtu\.be/)([a-zA-Z0-9_-]{11})");
        if (m.Success) return m.Groups[1].Value;
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[a-zA-Z0-9_-]{11}$"))
            return trimmed;
        return trimmed;
    }

    private async Task<object> SubmitSongAsync(string? videoId, string? message)
    {
        var ytid = NormalizeVideoId(videoId);
        if (string.IsNullOrEmpty(ytid)) return new { error = "No videoId" };
        var cfg = ConfigService.Instance.Current;
        if (cfg == null) return new { error = "No config" };
        try
        {
            using var httpClient = new HttpClient();
            var url = $"{cfg.Address}/fetch?method=add";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", cfg.BearerToken);
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(ytid), "ytid");
            request.Content = content;
            var response = await httpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private void ShowMusicPlayer()
    {
        if (ConfigService.Instance.Current.UseNewUI)
        {
            SubscribeToMusicPlayer();
            _ytForm.NewUiRef?.ShowMusicPlayerWindow();
        }
        else
        {
            _ytForm.Dispatcher.Invoke(() => _ytForm.ShowMusicPlayer());
        }
    }

    private void CloseNewMusicPlayer()
    {
        _ytForm.NewUiRef?.CloseMusicPlayerWindow();
    }

    private void SubscribeToMusicPlayer()
    {
        if (_musicPlayerSubscribed) return;
        var mp = _ytForm.MusicPlayer;
        if (mp == null) return;
        _musicPlayerSubscribed = true;
        mp.NowPlayingTick += OnNowPlayingTick;
        mp.QueueChanged += OnQueueChanged;
    }

    private void OnNowPlayingTick(object? sender, MusicPlayer.NowPlayingEventArgs args)
    {
        var mpSend = _ytForm.NewUiRef?.MusicPlayerSendMessage;
        if (mpSend == null) return;

        try
        {
            var data = new System.Collections.Generic.Dictionary<string, object>
            {
                ["currentTime"] = args.CurrentTime.TotalSeconds,
                ["totalTime"] = args.TotalTime.TotalSeconds,
                ["isPlaying"] = _ytForm.MusicPlayer?.RemoteIsPlaying ?? false,
                ["isPlaybackActive"] = _ytForm.MusicPlayer?.RemoteIsPlaybackActive ?? false
            };

            var current = args.Current;
            if (current != null && current.songPath != _currentSongPath)
            {
                _currentSongPath = current.songPath;
                data["title"] = current.Title ?? "";
                data["artist"] = current.Artist ?? "";
                data["duration"] = current.length ?? "";

                var thumb = GetThumbnailForSongPath(current.songPath);
                if (thumb != null)
                    data["thumbnail"] = thumb;

                // Library-enriched audio quality metadata
                if (!string.IsNullOrEmpty(current.songPath))
                {
                    var libSong = _libraryService.Data.Songs.FirstOrDefault(s => string.Equals(s.FilePath, current.songPath, StringComparison.OrdinalIgnoreCase));
                    if (libSong != null)
                    {
                        var ext = System.IO.Path.GetExtension(current.songPath).TrimStart('.').ToUpperInvariant();
                        data["fileType"] = ext;
                        if (libSong.BitrateKbps > 0) data["bitrateKbps"] = libSong.BitrateKbps;
                    }
                }
            }

            if (current == null)
                _currentSongPath = null;

            data["currentSongPath"] = _currentSongPath;

            var json = JsonConvert.SerializeObject(new
            {
                type = "event",
                eventName = "nowPlayingUpdate",
                data
            });
            mpSend(json);

            if (current != null && current.songPath == _currentSongPath && data.ContainsKey("title"))
            {
                _ = FetchAndSendLyricsForSongAsync(current);
            }
        }
        catch { }
    }

    private void SendCurrentNowPlayingToNewUI()
    {
        var mpSend = _ytForm.NewUiRef?.MusicPlayerSendMessage;
        if (mpSend == null) return;

        try
        {
            var mp = _ytForm.MusicPlayer;
            if (mp == null) return;

            Song? current = null;
            _ytForm.Dispatcher.Invoke(() => { current = mp.Queue?.FirstOrDefault(); });

            if (current == null) return;

            var data = new System.Collections.Generic.Dictionary<string, object>
            {
                ["title"] = current.Title ?? "",
                ["artist"] = current.Artist ?? "",
                ["duration"] = current.length ?? "",
                ["isPlaying"] = mp.RemoteIsPlaying,
                ["isPlaybackActive"] = mp.RemoteIsPlaybackActive
            };

            var thumb = GetThumbnailForSongPath(current.songPath);
            if (thumb != null)
                data["thumbnail"] = thumb;

            // Library-enriched audio quality metadata
            if (!string.IsNullOrEmpty(current.songPath))
            {
                var libSong = _libraryService.Data.Songs.FirstOrDefault(s => string.Equals(s.FilePath, current.songPath, StringComparison.OrdinalIgnoreCase));
                if (libSong != null)
                {
                    var ext = System.IO.Path.GetExtension(current.songPath).TrimStart('.').ToUpperInvariant();
                    data["fileType"] = ext;
                    if (libSong.BitrateKbps > 0) data["bitrateKbps"] = libSong.BitrateKbps;
                }
            }

            var json = JsonConvert.SerializeObject(new
            {
                type = "event",
                eventName = "nowPlayingUpdate",
                data
            });
            mpSend(json);

            _ = FetchAndSendLyricsForSongAsync(current);
        }
        catch { }
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        SendQueueUpdate();
        _ = PrefetchNextLyricsAsync();
    }

    private void SendQueueUpdate()
    {
        var mpSend = _ytForm.NewUiRef?.MusicPlayerSendMessage;
        if (mpSend == null) return;

        try
        {
            var mp = _ytForm.MusicPlayer;
            if (mp == null) return;

            // Build a path→library lookup for enriching queue items
            Dictionary<string, LibrarySong> libByPath = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var s in _libraryService.Data.Songs)
                    if (!string.IsNullOrEmpty(s.FilePath))
                        libByPath[s.FilePath] = s;
            }
            catch { }

            var queueData = mp.Queue.Select((song, i) =>
            {
                string? thumb = null;
                if (!string.IsNullOrEmpty(song.songPath))
                    thumb = GetThumbnailForSongPath(song.songPath);

                LibrarySong? lib = null;
                if (!string.IsNullOrEmpty(song.songPath))
                    libByPath.TryGetValue(song.songPath, out lib);

                var ext = !string.IsNullOrEmpty(song.songPath)
                    ? System.IO.Path.GetExtension(song.songPath).TrimStart('.').ToUpperInvariant()
                    : null;

                return new
                {
                    title = song.Title ?? "",
                    artist = song.Artist ?? "",
                    duration = song.length ?? "",
                    songPath = song.songPath ?? "",
                    estimatedStart = song.EstimatedStartDisplay ?? "",
                    index = i,
                    thumbnail = thumb,
                    // Library-enriched metadata
                    isMissing = lib?.IsMissing ?? false,
                    isHashDuplicate = lib?.IsHashDuplicate ?? false,
                    hasMetadataDuplicates = lib?.HasMetadataDuplicates ?? false,
                    metadataDuplicateCount = lib?.MetadataDuplicateIds.Count ?? 0,
                    bitrateKbps = lib?.BitrateKbps ?? 0,
                    fileType = ext
                };
            }).ToList();

            var json = JsonConvert.SerializeObject(new
            {
                type = "event",
                eventName = "queueUpdate",
                data = new
                {
                    queue = queueData,
                    currentSongPath = _currentSongPath,
                    isPlaying = mp.RemoteIsPlaybackActive
                }
            });
            mpSend(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SendQueueUpdate] {ex.Message}");
        }
    }

    private string? GetThumbnailForSongPath(string songPath)
    {
        try
        {
            // 1. Try embedded album art (local files and YouTube downloads with embedded art)
            try
            {
                using var tfile = global::TagLib.File.Create(songPath);
                var pic = tfile.Tag.Pictures?.FirstOrDefault();
                if (pic != null && pic.Data != null && pic.Data.Data != null && pic.Data.Data.Length > 0)
                {
                    var mime = pic.MimeType ?? "image/jpeg";
                    var b64 = Convert.ToBase64String(pic.Data.Data);
                    return "data:" + mime + ";base64," + b64;
                }
            }
            catch { /* no embedded art, fall through */ }

            // 2. Try YouTube thumbnail cache
            var videoId = System.IO.Path.GetFileNameWithoutExtension(songPath);
            if (string.IsNullOrEmpty(videoId)) return null;

            var cacheDir = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "data", "downloadedvideos", "thumbnail-cache");
            var cachePath = System.IO.Path.Combine(cacheDir, $"{videoId}.png");

            if (File.Exists(cachePath))
            {
                var bytes = File.ReadAllBytes(cachePath);
                return "data:image/png;base64," + Convert.ToBase64String(bytes);
            }

            var jpgPath = System.IO.Path.Combine(cacheDir, $"{videoId}.jpg");
            if (File.Exists(jpgPath))
            {
                var bytes = File.ReadAllBytes(jpgPath);
                return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
            }
        }
        catch { }
        return null;
    }

    private void ShowSoundboard()
    {
        _ytForm.Dispatcher.Invoke(() => _ytForm.ShowSoundboard());
    }

    private void ShowSettings()
    {
        _ytForm.Dispatcher.Invoke(() =>
        {
            var settings = new Settings();
            settings.Show();
        });
    }

    private Task<object> QueueSongAsync(string? videoId)
    {
        var ytid = NormalizeVideoId(videoId);
        if (string.IsNullOrEmpty(ytid)) return Task.FromResult<object>(new { error = "No videoId" });
        try
        {
            _ytForm.Dispatcher.InvokeAsync(() => _ytForm.QueueSongById(ytid));
            return Task.FromResult<object>(new { success = true });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new { error = ex.Message });
        }
    }

    private Task<object> PlayNextAsync(string? videoId)
    {
        var ytid = NormalizeVideoId(videoId);
        if (string.IsNullOrEmpty(ytid)) return Task.FromResult<object>(new { error = "No videoId" });
        try
        {
            _ytForm.Dispatcher.InvokeAsync(() => _ytForm.PlayNextById(ytid));
            return Task.FromResult<object>(new { success = true });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new { error = ex.Message });
        }
    }

    private object GetFullConfig()
    {
        var cfg = ConfigService.Instance.Current;
        if (cfg == null) return new { };
        return new
        {
            address = cfg.Address ?? "",
            fetchingTimer = cfg.FetchingTimer,
            threads = cfg.Threads,
            autoEnqueue = cfg.AutoEnqueue,
            requestUrl = cfg.RequestUrl ?? "",
            presentationFullscreen = cfg.PresentationFullscreen,
            normalizeVolume = cfg.NormalizeVolume,
            useCaptionLyricsFallback = cfg.UseCaptionLyricsFallback,
            enableAnnouncements = cfg.EnableAnnouncements,
            bearerToken = cfg.BearerToken ?? "",
            defaultSorting = cfg.DefaultSorting ?? "",
            defaultSubmitMethod = cfg.DefaultSubmitMethod ?? "search",
            libraryScanFolders = cfg.LibraryScanFolders ?? new List<string>(),
            libraryAllowedExtensions = cfg.LibraryAllowedExtensions ?? new List<string>(),
            libraryAutoScanOnStartup = cfg.LibraryAutoScanOnStartup,
            libraryAutoAddDownloads = cfg.LibraryAutoAddDownloads,
            libraryRemoveMissingOnScan = cfg.LibraryRemoveMissingOnScan,
            libraryRecommendationsEnabled = cfg.LibraryRecommendationsEnabled
        };
    }

    private object GetRemoteConfig()
    {
        var cfg = ConfigService.Instance.Current;
        if (cfg == null) return new { };
        var remote = RemoteControlConfiguration.Ensure(cfg.RemoteControl);
        static string ShortcutLabel(KeyboardShortcutConfig? k) =>
            k != null && k.Enabled && !string.IsNullOrEmpty(k.Gesture) ? k.Gesture : "";
        static string MidiLabel(MidiMapping? m) =>
            m != null && m.IsConfigured ? $"Ch{m.Channel} {m.MessageType} {m.Note}" : "";
        return new
        {
            midiEnabled = remote.MidiEnabled,
            midiInputDevice = remote.MidiInputDevice,
            midiOutputDevice = remote.MidiOutputDevice,
            mappings = new
            {
                playPause = new { keybind = ShortcutLabel(remote.PlayPauseKeybind), midi = MidiLabel(remote.PlayPauseMidi) },
                skipNext = new { keybind = ShortcutLabel(remote.SkipNextKeybind), midi = MidiLabel(remote.SkipNextMidi) },
                previous = new { keybind = ShortcutLabel(remote.PreviousKeybind), midi = MidiLabel(remote.PreviousMidi) },
                stop = new { keybind = ShortcutLabel(remote.StopKeybind), midi = MidiLabel(remote.StopMidi) },
                volumeUp = new { keybind = ShortcutLabel(remote.VolumeUpKeybind), midi = "" },
                volumeDown = new { keybind = ShortcutLabel(remote.VolumeDownKeybind), midi = "" },
                crossfadeUp = new { keybind = ShortcutLabel(remote.CrossfadeUpKeybind), midi = "" },
                crossfadeDown = new { keybind = ShortcutLabel(remote.CrossfadeDownKeybind), midi = "" },
                announcement = new { keybind = ShortcutLabel(remote.AnnouncementKeybind), midi = MidiLabel(remote.AnnouncementMidi) },
                announcementSound = new { keybind = ShortcutLabel(remote.AnnouncementPlaySoundToggleKeybind), midi = MidiLabel(remote.AnnouncementPlaySoundToggleMidi) },
                announcementPushToTalk = new { keybind = ShortcutLabel(remote.AnnouncementPushToTalkToggleKeybind), midi = MidiLabel(remote.AnnouncementPushToTalkToggleMidi) },
                announcementDimUp = new { keybind = ShortcutLabel(remote.AnnouncementDimDbUpKeybind), midi = "" },
                announcementDimDown = new { keybind = ShortcutLabel(remote.AnnouncementDimDbDownKeybind), midi = "" }
            }
        };
    }

    private async Task<object> SaveSettingsAsync(JObject msg)
    {
        try
        {
            var settings = msg["settings"] as JObject;
            if (settings == null) return new { error = "No settings provided" };

            ConfigService.Instance.Update(cfg =>
            {
                if (settings.TryGetValue("address", out var a)) cfg.Address = a.ToString();
                if (settings.TryGetValue("fetchingTimer", out var ft)) cfg.FetchingTimer = Math.Max(1, ft.ToObject<int>());
                if (settings.TryGetValue("threads", out var t)) cfg.Threads = Math.Max(1, t.ToObject<int>());
                if (settings.TryGetValue("autoEnqueue", out var ae)) cfg.AutoEnqueue = ae.ToObject<bool>();
                if (settings.TryGetValue("requestUrl", out var ru)) cfg.RequestUrl = ru.ToString();
                if (settings.TryGetValue("presentationFullscreen", out var pf)) cfg.PresentationFullscreen = pf.ToObject<bool>();
                if (settings.TryGetValue("normalizeVolume", out var nv)) cfg.NormalizeVolume = nv.ToObject<bool>();
                if (settings.TryGetValue("useCaptionLyricsFallback", out var cl)) cfg.UseCaptionLyricsFallback = cl.ToObject<bool>();
                if (settings.TryGetValue("enableAnnouncements", out var ea)) cfg.EnableAnnouncements = ea.ToObject<bool>();
                if (settings.TryGetValue("bearerToken", out var bt)) cfg.BearerToken = bt.ToString();
                if (settings.TryGetValue("defaultSorting", out var ds)) cfg.DefaultSorting = ds.ToString();
                if (settings.TryGetValue("defaultSubmitMethod", out var dsm))
                {
                    var val = dsm.ToString();
                    if (val == "url" || val == "search") cfg.DefaultSubmitMethod = val;
                }
                if (settings.TryGetValue("libraryScanFolders", out var lsf))
                {
                    cfg.LibraryScanFolders = lsf.ToObject<List<string>>() ?? new List<string>();
                }
                if (settings.TryGetValue("libraryAllowedExtensions", out var lae))
                {
                    cfg.LibraryAllowedExtensions = lae.ToObject<List<string>>() ?? new List<string> { "mp3", "m4a", "wav", "flac", "ogg", "aac", "wma", "opus" };
                }
                if (settings.TryGetValue("libraryAutoScanOnStartup", out var las)) cfg.LibraryAutoScanOnStartup = las.ToObject<bool>();
                if (settings.TryGetValue("libraryAutoAddDownloads", out var lad)) cfg.LibraryAutoAddDownloads = lad.ToObject<bool>();
                if (settings.TryGetValue("libraryRemoveMissingOnScan", out var lrm)) cfg.LibraryRemoveMissingOnScan = lrm.ToObject<bool>();
                if (settings.TryGetValue("libraryRecommendationsEnabled", out var lre)) cfg.LibraryRecommendationsEnabled = lre.ToObject<bool>();

                if (settings.TryGetValue("normalizeVolume", out var nv2) && !nv2.ToObject<bool>())
                {
                    cfg.NormalizationActive = false;
                }
            });

            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task<object> ToggleSendinAsync()
    {
        try
        {
            var cfg = ConfigService.Instance.Current;
            if (cfg == null) return new { error = "No config" };
            var fts = new FetchingService(cfg.BearerToken);
            var result = await fts.SendRequest("toggle").ConfigureAwait(false);
            return new { success = true, response = result };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task<object> GetSendinStatusAsync()
    {
        try
        {
            var cfg = ConfigService.Instance.Current;
            if (cfg == null) return new { error = "No config" };
            var fts = new FetchingService(cfg.BearerToken);
            var result = await fts.SendRequest("get-sendin-allowed").ConfigureAwait(false);
            var jsonArray = Newtonsoft.Json.Linq.JArray.Parse(result);
            bool allowed = jsonArray.Count > 0 && jsonArray[0].ToObject<bool>();
            return new { sendinAllowed = allowed };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object OpenAuthUrl()
    {
        try
        {
            var address = ConfigService.Instance.Current?.Address ?? "http://127.0.0.1:5000";
            var url = $"https://schuelerapp.by-cra.net/sign-jwt?audience={Uri.EscapeDataString(address)}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object GetStartupData()
    {
        var state = LaunchOptionsStorage.Load();
        return new
        {
            version = About.version,
            launchOptions = new
            {
                rememberSelection = state.RememberSelection,
                lastSelectedMode = state.LastSelectedMode.ToString()
            }
        };
    }

    private object SaveLaunchOptions(JObject msg)
    {
        try
        {
            var state = LaunchOptionsStorage.Load();
            if (msg.TryGetValue("rememberSelection", out var rs))
                state.RememberSelection = rs.ToObject<bool>();
            if (msg.TryGetValue("lastSelectedMode", out var lm))
            {
                if (Enum.TryParse<StartupMode>(lm.ToString(), true, out var mode))
                    state.LastSelectedMode = mode;
            }
            LaunchOptionsStorage.Save(state);
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private Task<object> TriggerAuthAsync()
    {
        var tcs = new TaskCompletionSource<object>();
        System.Windows.Application.Current.Dispatcher.Invoke(async () =>
        {
            try
            {
                var authForm = new Authentication();
                authForm.CookiesRetrieved += cookies =>
                {
                    tcs.TrySetResult(new { success = true, cookieCount = cookies.Count });
                };
                authForm.Show();
                // If auth window closes without retrieving cookies, treat as failure
                authForm.Closed += (s, e) =>
                {
                    if (!tcs.Task.IsCompleted)
                        tcs.TrySetResult(new { success = false, error = "Authentication window closed" });
                };
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new { success = false, error = ex.Message });
            }
        });
        return tcs.Task;
    }

    private object CompleteSetup(JObject msg)
    {
        try
        {
            var mode = StartupMode.SongRequests;
            if (msg.TryGetValue("startupMode", out var sm))
                Enum.TryParse<StartupMode>(sm.ToString(), true, out mode);

            // Apply startup mode for the web UI
            if (_ytForm != null)
            {
                _ytForm.Dispatcher.Invoke(() =>
                {
                    switch (mode)
                    {
                        case StartupMode.MusicPlayer:
                            _ytForm.ShowMusicPlayer();
                            break;
                        case StartupMode.MusicShare:
                            _ytForm.ShowMusicPlayer();
                            _ytForm.ShowMusicShare();
                            break;
                        case StartupMode.Soundboard:
                            _ytForm.ShowSoundboard();
                            break;
                    }
                });
            }

            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task<object> InstallUpdateAsync(JObject msg)
    {
        try
        {
            var downloadUrl = msg["downloadUrl"]?.ToString();
            if (string.IsNullOrEmpty(downloadUrl))
                return new { error = "No download URL" };

            var cfg = ConfigService.Instance.Current;
            var result = await UpdateService.CheckForUpdatesAsync().ConfigureAwait(false);
            if (!result.UpdateAvailable)
                return new { error = "No update available" };

            // Run through the WPF UpdatePrompt flow on the UI thread
            var tcs = new TaskCompletionSource<bool>();
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var prompt = new UpdatePrompt(downloadUrl, result.LatestVersion, result.CurrentVersion, result.ReleaseNotes);
                var result2 = prompt.ShowDialog();
                tcs.SetResult(result2 ?? false);
            });
            return new { success = await tcs.Task.ConfigureAwait(false) };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task<object> CheckForUpdatesAsync()
    {
        try
        {
            var info = await UpdateService.CheckForUpdatesAsync().ConfigureAwait(false);
            return new
            {
                updateAvailable = info.UpdateAvailable,
                latestVersion = info.LatestVersion,
                currentVersion = info.CurrentVersion,
                downloadUrl = info.DownloadUrl,
                releaseNotes = info.ReleaseNotes
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task FetchAndSendLyricsForSongAsync(Song? song = null)
    {
        var mpSend = _ytForm.NewUiRef?.MusicPlayerSendMessage;
        if (mpSend == null) return;

        if (song == null)
        {
            _ytForm.Dispatcher.Invoke(() =>
            {
                song = _ytForm.MusicPlayer?.Queue?.FirstOrDefault();
            });
        }

        if (song == null)
        {
            SendEmptyLyrics(mpSend);
            return;
        }

        var songKey = $"{song.songPath}|{song.Artist}|{song.Title}";
        lock (_musicPlayerLyricsCacheLock)
        {
            if (_musicPlayerLyricsCache.TryGetValue(songKey, out var cached))
            {
                SendLyricsResult(mpSend, cached);
                return;
            }
        }

        var lyricsService = _musicPlayerLyricsService ??= new LyricsService();
        LyricsResult result;

        try
        {
            var query = LyricsQueryNormalizer.Build(song);
            result = await lyricsService.GetCachedLyricsAsync(query.Artist, query.Title, song.Duration).ConfigureAwait(false);
            if (!result.Found)
            {
                result = await lyricsService.GetLyricsAsync(query.Artist, query.Title, song.Duration).ConfigureAwait(false);
            }
        }
        catch
        {
            result = new LyricsResult();
        }

        var fallbackEnabled = ConfigService.Instance.Current?.UseCaptionLyricsFallback ?? true;
        if (fallbackEnabled
            && (!result.Found || (!result.HasSynced && string.IsNullOrWhiteSpace(result.PlainLyrics)))
            && TryGetCachedSubtitleFallback(song.songPath, out var timedSub, out var plainSub))
        {
            result = new LyricsResult
            {
                Found = true,
                SyncedLyrics = timedSub ?? string.Empty,
                PlainLyrics = plainSub ?? string.Empty
            };
        }

        lock (_musicPlayerLyricsCacheLock)
        {
            _musicPlayerLyricsCache[songKey] = result;
        }

        SendLyricsResult(mpSend, result);

        _ = PrefetchNextLyricsAsync();
    }

    private async Task PrefetchNextLyricsAsync()
    {
        try
        {
            var mp = _ytForm.MusicPlayer;
            if (mp == null) return;

            Song? next = null;
            _ytForm.Dispatcher.Invoke(() =>
            {
                if (mp.Queue.Count > 1)
                    next = mp.Queue[1];
            });

            if (next == null) return;

            var songKey = $"{next.songPath}|{next.Artist}|{next.Title}";
            lock (_musicPlayerLyricsCacheLock)
            {
                if (_musicPlayerLyricsCache.ContainsKey(songKey)) return;
            }

            var lyricsService = _musicPlayerLyricsService ??= new LyricsService();
            var query = LyricsQueryNormalizer.Build(next);
            var result = await lyricsService.GetCachedLyricsAsync(query.Artist, query.Title, next.Duration).ConfigureAwait(false);
            if (!result.Found)
            {
                result = await lyricsService.GetLyricsAsync(query.Artist, query.Title, next.Duration).ConfigureAwait(false);
            }

            var fallbackEnabled = ConfigService.Instance.Current?.UseCaptionLyricsFallback ?? true;
            if (fallbackEnabled
                && (!result.Found || (!result.HasSynced && string.IsNullOrWhiteSpace(result.PlainLyrics)))
                && TryGetCachedSubtitleFallback(next.songPath, out var timedSub, out var plainSub))
            {
                result = new LyricsResult
                {
                    Found = true,
                    SyncedLyrics = timedSub ?? string.Empty,
                    PlainLyrics = plainSub ?? string.Empty
                };
            }

            lock (_musicPlayerLyricsCacheLock)
            {
                _musicPlayerLyricsCache[songKey] = result;
            }
        }
        catch { }
    }

    private void SendLyricsResult(Action<string> mpSend, LyricsResult result)
    {
        string provider = "";
        var data = new System.Collections.Generic.Dictionary<string, object>();

        if (result.Found && result.HasSynced)
        {
            var syncedLines = result.ParseSyncedLines();
            data["syncedLyrics"] = syncedLines.Select(l => new { time = l.Time.TotalSeconds, text = l.Text }).ToList();
            data["hasSynced"] = true;
            provider = "LRCLIB";
        }
        else if (result.Found && !string.IsNullOrWhiteSpace(result.PlainLyrics))
        {
            var plainLines = result.PlainLyrics
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            data["plainLyrics"] = plainLines;
            data["hasSynced"] = false;
            provider = "LRCLIB";
        }
        else
        {
            data["hasSynced"] = false;
        }

        data["provider"] = provider;

        try
        {
            var json = JsonConvert.SerializeObject(new
            {
                type = "event",
                eventName = "lyricsUpdate",
                data
            });
            mpSend(json);
        }
        catch { }
    }

    private void SendEmptyLyrics(Action<string> mpSend)
    {
        try
        {
            var json = JsonConvert.SerializeObject(new
            {
                type = "event",
                eventName = "lyricsUpdate",
                data = new { provider = "", hasSynced = false, syncedLyrics = new object[0], plainLyrics = new string[0] }
            });
            mpSend(json);
        }
        catch { }
    }

    private static bool TryGetCachedSubtitleFallback(string? songPath, out string timedSubtitleText, out string plainSubtitleText)
    {
        timedSubtitleText = string.Empty;
        plainSubtitleText = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(songPath)) return false;
            var videoId = Path.GetFileNameWithoutExtension(songPath);
            if (string.IsNullOrWhiteSpace(videoId)) return false;

            var cachePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, YoutubeForm.downloadPath, "video-metadata-cache.json"));
            if (!File.Exists(cachePath)) return false;

            var json = File.ReadAllText(cachePath);
            var cache = JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, CachedSubtitleData>>(json);
            if (cache == null) return false;
            if (!cache.TryGetValue(videoId, out var cached)) return false;
            if (string.IsNullOrWhiteSpace(cached?.SubtitleText) && string.IsNullOrWhiteSpace(cached?.SubtitleTimedText)) return false;

            timedSubtitleText = cached.SubtitleTimedText;
            plainSubtitleText = cached.SubtitleText;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var kv in _pendingRequests)
        {
            kv.Value.TrySetCanceled();
        }
        _pendingRequests.Clear();

        lock (_musicPlayerLyricsCacheLock)
        {
            _musicPlayerLyricsCache.Clear();
        }

        if (_musicPlayerSubscribed)
        {
            var mp = _ytForm.MusicPlayer;
            if (mp != null)
            {
                mp.NowPlayingTick -= OnNowPlayingTick;
                mp.QueueChanged -= OnQueueChanged;
            }
            _musicPlayerSubscribed = false;
        }
    }

    // ──────────────────────────────────────────────────
    //  Library interop methods
    // ──────────────────────────────────────────────────

    private async Task<object> ScanLibraryAsync()
    {
        _libraryService.SyncConfig();

        // Subscribe to progress events and forward them to the admin window
        void OnProgress(double pct, string msg)
        {
            SendEvent("libraryScanProgress", new { progress = pct, message = msg });
        }
        void OnCompleted(LibraryScanResult res)
        {
            _libraryService.ScanProgress -= OnProgress;
            _libraryService.ScanCompleted -= OnCompleted;
            SendEvent("libraryScanCompleted", new
            {
                filesFound = res.FilesFound,
                songsAdded = res.SongsAdded,
                songsUpdated = res.SongsUpdated,
                songsMissing = res.SongsMissing,
                errorCount = res.Errors.Count,
                errors = res.Errors.Take(20).ToList()
            });
        }

        _libraryService.ScanProgress += OnProgress;
        _libraryService.ScanCompleted += OnCompleted;

        var result = await _libraryService.ScanAsync().ConfigureAwait(false);

        // Safety: ensure unsubscription if event already fired
        _libraryService.ScanProgress -= OnProgress;
        _libraryService.ScanCompleted -= OnCompleted;

        return new
        {
            success = true,
            filesFound = result.FilesFound,
            songsAdded = result.SongsAdded,
            songsUpdated = result.SongsUpdated,
            songsMissing = result.SongsMissing,
            errorCount = result.Errors.Count,
            errors = result.Errors.Take(20).ToList()
        };
    }

    private object GetLibraryStatus()
    {
        var data = _libraryService.Data;
        return new
        {
            totalSongs = data.Songs.Count,
            totalMissing = data.Songs.Count(s => s.IsMissing),
            totalHashDuplicates = data.Songs.Count(s => s.IsHashDuplicate),
            totalMetadataDuplicates = data.Songs.Count(s => s.HasMetadataDuplicates),
            lastScanUtc = data.LastScanUtc,
            scanFolders = data.Config.ScanFolders,
            extensions = data.Config.AllowedExtensions,
            autoScanOnStartup = data.Config.AutoScanOnStartup,
            autoAddDownloads = data.Config.AutoAddDownloadsToLibrary,
            removeMissing = data.Config.RemoveMissingOnScan,
            recommendationsEnabled = data.Config.AutoScanOnStartup
        };
    }

    private object GetLibrarySongs(JObject msg)
    {
        var data = _libraryService.Data;
        var page = msg["page"]?.ToObject<int>() ?? 1;
        var pageSize = msg["pageSize"]?.ToObject<int>() ?? 50;
        var sortBy = msg["sortBy"]?.ToString() ?? "dateAdded";
        var desc = msg["descending"]?.ToObject<bool>() ?? true;
        var filterMissing = msg["filterMissing"]?.ToObject<bool>() ?? false;
        var filterDuplicates = msg["filterDuplicates"]?.ToString() ?? "all"; // all, hash, metadata, none

        IEnumerable<LibrarySong> query = data.Songs;

        if (filterMissing)
            query = query.Where(s => !s.IsMissing);

        query = filterDuplicates switch
        {
            "hash" => query.Where(s => s.IsHashDuplicate),
            "metadata" => query.Where(s => s.HasMetadataDuplicates),
            "none" => query.Where(s => !s.IsHashDuplicate && !s.HasMetadataDuplicates),
            _ => query
        };

        query = sortBy.ToLowerInvariant() switch
        {
            "title" => desc ? query.OrderByDescending(s => s.Title) : query.OrderBy(s => s.Title),
            "artist" => desc ? query.OrderByDescending(s => s.Artist) : query.OrderBy(s => s.Artist),
            "album" => desc ? query.OrderByDescending(s => s.Album) : query.OrderBy(s => s.Album),
            "duration" => desc ? query.OrderByDescending(s => s.Duration) : query.OrderBy(s => s.Duration),
            "playcount" => desc ? query.OrderByDescending(s => s.PlayCount) : query.OrderBy(s => s.PlayCount),
            "filesize" => desc ? query.OrderByDescending(s => s.FileSizeBytes) : query.OrderBy(s => s.FileSizeBytes),
            _ => desc ? query.OrderByDescending(s => s.DateAddedUtc) : query.OrderBy(s => s.DateAddedUtc)
        };

        var total = query.Count();
        var songs = query.Skip((page - 1) * pageSize).Take(pageSize).Select(s => new
        {
            id = s.Id,
            title = s.Title,
            artist = s.Artist,
            album = s.Album,
            genre = s.Genre,
            duration = s.Duration.TotalSeconds,
            durationDisplay = s.DurationDisplay,
            thumbnail = GetLibrarySongThumbnail(s),
            filePath = s.FilePath,
            source = s.Source.ToString(),
            fileStatus = s.FileStatus.ToString(),
            isMissing = s.IsMissing,
            isHashDuplicate = s.IsHashDuplicate,
            hasMetadataDuplicates = s.HasMetadataDuplicates,
            metadataDuplicateCount = s.MetadataDuplicateIds.Count,
            playCount = s.PlayCount,
            dateAdded = s.DateAddedUtc,
            lastPlayed = s.LastPlayedUtc,
            fileSizeBytes = s.FileSizeBytes,
            bitrateKbps = s.BitrateKbps,
            youTubeId = s.YouTubeId
        }).ToList();

        return new { total, page, pageSize, songs };
    }

    private string? GetLibrarySongThumbnail(LibrarySong song)
    {
        try
        {
            if (!string.IsNullOrEmpty(song.ThumbnailPath) && File.Exists(song.ThumbnailPath))
            {
                var bytes = File.ReadAllBytes(song.ThumbnailPath);
                var ext = Path.GetExtension(song.ThumbnailPath).ToLowerInvariant();
                var mime = ext == ".png" ? "image/png" : "image/jpeg";
                return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
            }
        }
        catch { }
        return null;
    }

    private object SearchLibrary(JObject msg)
    {
        var query = msg["query"]?.ToString() ?? "";
        var maxResults = msg["maxResults"]?.ToObject<int>() ?? 50;
        var results = _libraryService.Data.Search(query, maxResults);
        return new
        {
            results = results.Select(s => new
            {
                id = s.Id,
                title = s.Title,
                artist = s.Artist,
                album = s.Album,
                duration = s.Duration.TotalSeconds,
                durationDisplay = s.DurationDisplay,
                thumbnail = GetLibrarySongThumbnail(s),
                filePath = s.FilePath,
                source = s.Source.ToString(),
                isMissing = s.IsMissing,
                isHashDuplicate = s.IsHashDuplicate,
                hasMetadataDuplicates = s.HasMetadataDuplicates,
                metadataDuplicateCount = s.MetadataDuplicateIds.Count,
                playCount = s.PlayCount
            }).ToList(),
            total = results.Count
        };
    }

    private object DeleteLibrarySong(JObject msg)
    {
        var songId = msg["songId"]?.ToString();
        if (string.IsNullOrEmpty(songId)) return new { error = "No songId" };

        lock (_libraryService.Data)
        {
            var song = _libraryService.Data.FindSong(songId);
            if (song == null) return new { error = "Song not found" };
            _libraryService.Data.Songs.Remove(song);
        }

        _libraryService.Save();
        return new { success = true };
    }

    private object UpdateLibrarySongMetadata(JObject msg)
    {
        var songId = msg["songId"]?.ToString();
        if (string.IsNullOrEmpty(songId)) return new { error = "No songId" };

        lock (_libraryService.Data)
        {
            var song = _libraryService.Data.FindSong(songId);
            if (song == null) return new { error = "Song not found" };

            if (msg.TryGetValue("title", out var t)) song.Title = t.ToString();
            if (msg.TryGetValue("artist", out var a)) song.Artist = a.ToString();
            if (msg.TryGetValue("album", out var al)) song.Album = al.ToString();
            if (msg.TryGetValue("genre", out var g)) song.Genre = g.ToString();
            if (msg.TryGetValue("tags", out var tags)) song.Tags = tags.ToObject<System.Collections.Generic.List<string>>() ?? new();
            song.LastModifiedUtc = DateTime.UtcNow;
        }

        _libraryService.Save();
        return new { success = true };
    }

    private object AddLibraryScanFolder(JObject msg)
    {
        var folder = msg["folder"]?.ToString();
        if (string.IsNullOrWhiteSpace(folder)) return new { error = "No folder path" };

        lock (_libraryService.Data)
        {
            if (!_libraryService.Data.Config.ScanFolders.Contains(folder))
                _libraryService.Data.Config.ScanFolders.Add(folder);
        }

        // Also persist to main config
        ConfigService.Instance.Update(cfg =>
        {
            cfg.LibraryScanFolders = new System.Collections.Generic.List<string>(_libraryService.Data.Config.ScanFolders);
        });

        _libraryService.Save();
        return new { success = true, scanFolders = _libraryService.Data.Config.ScanFolders };
    }

    private object RemoveLibraryScanFolder(JObject msg)
    {
        var folder = msg["folder"]?.ToString();
        if (string.IsNullOrEmpty(folder)) return new { error = "No folder path" };

        lock (_libraryService.Data)
        {
            _libraryService.Data.Config.ScanFolders.Remove(folder);
        }

        ConfigService.Instance.Update(cfg =>
        {
            cfg.LibraryScanFolders = new System.Collections.Generic.List<string>(_libraryService.Data.Config.ScanFolders);
        });

        _libraryService.Save();
        return new { success = true, scanFolders = _libraryService.Data.Config.ScanFolders };
    }

    private object ImportToLibrary(JObject msg)
    {
        var filePath = msg["filePath"]?.ToString();
        var youTubeId = msg["youTubeId"]?.ToString();
        var videoUrl = msg["videoUrl"]?.ToString();
        var title = msg["title"]?.ToString();
        var artist = msg["artist"]?.ToString();

        if (string.IsNullOrEmpty(filePath)) return new { error = "No filePath" };

        var song = _libraryService.ImportDownloadedSong(filePath, youTubeId, videoUrl, title, artist);
        if (song == null) return new { success = false, reason = "Hash duplicate — file already in library" };

        return new
        {
            success = true,
            song = new
            {
                id = song.Id,
                title = song.Title,
                artist = song.Artist,
                hasMetadataDuplicates = song.HasMetadataDuplicates,
                metadataDuplicateCount = song.MetadataDuplicateIds.Count
            }
        };
    }

    private object GetLibraryStats()
    {
        var data = _libraryService.Data;
        return new
        {
            totalSongs = data.Songs.Count,
            presentSongs = data.Songs.Count(s => !s.IsMissing),
            missingSongs = data.Songs.Count(s => s.IsMissing),
            hashDuplicates = data.Songs.Count(s => s.IsHashDuplicate),
            metadataDuplicates = data.Songs.Count(s => s.HasMetadataDuplicates),
            bySource = new
            {
                local = data.Songs.Count(s => s.Source == SongSource.Local),
                downloaded = data.Songs.Count(s => s.Source == SongSource.Downloaded),
                external = data.Songs.Count(s => s.Source == SongSource.External)
            },
            totalPlayCount = data.Songs.Sum(s => s.PlayCount),
            lastScanUtc = data.LastScanUtc,
            scanFolders = data.Config.ScanFolders.Count
        };
    }

    private object FlagLibrarySongMissing(JObject msg)
    {
        var songId = msg["songId"]?.ToString();
        if (string.IsNullOrEmpty(songId)) return new { error = "No songId" };

        lock (_libraryService.Data)
        {
            var song = _libraryService.Data.FindSong(songId);
            if (song == null) return new { error = "Song not found" };
            song.FileStatus = FileStatus.Missing;
        }

        _libraryService.Save();
        return new { success = true };
    }

    private object PlayLibrarySong(JObject msg)
    {
        var songId = msg["songId"]?.ToString();
        if (string.IsNullOrEmpty(songId)) return new { error = "No songId" };

        LibrarySong? libSong;
        lock (_libraryService.Data)
        {
            libSong = _libraryService.Data.FindSong(songId);
        }
        if (libSong == null) return new { error = "Song not found in library" };
        if (!System.IO.File.Exists(libSong.FilePath)) return new { error = "File not found on disk" };

        _ytForm.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var mp = _ytForm.MusicPlayer;
                if (mp == null) return;
                mp.ClearQueue(true);
                if (mp.TryCreateSongFromFile(libSong.FilePath, out var song, out var errMsg) && song != null)
                {
                    mp.Queue.Add(song);
                    mp.ComputeQueueTimings();
                    mp.AnimateListItem(song);
                    mp.PlaySong(mp.Queue[0]);
                }
            }
            catch { }
        });

        return new { success = true };
    }

    private object QueueLibrarySong(JObject msg)
    {
        var songId = msg["songId"]?.ToString();
        if (string.IsNullOrEmpty(songId)) return new { error = "No songId" };

        LibrarySong? libSong;
        lock (_libraryService.Data)
        {
            libSong = _libraryService.Data.FindSong(songId);
        }
        if (libSong == null) return new { error = "Song not found in library" };
        if (!System.IO.File.Exists(libSong.FilePath)) return new { error = "File not found on disk" };

        _ytForm.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var mp = _ytForm.MusicPlayer;
                if (mp == null) return;
                if (mp.TryCreateSongFromFile(libSong.FilePath, out var song, out var errMsg) && song != null)
                    mp.AddSongExternal(song);
            }
            catch { }
        });

        return new { success = true };
    }

    private object PlayLibrarySongNext(JObject msg)
    {
        var songId = msg["songId"]?.ToString();
        if (string.IsNullOrEmpty(songId)) return new { error = "No songId" };

        LibrarySong? libSong;
        lock (_libraryService.Data)
        {
            libSong = _libraryService.Data.FindSong(songId);
        }
        if (libSong == null) return new { error = "Song not found in library" };
        if (!System.IO.File.Exists(libSong.FilePath)) return new { error = "File not found on disk" };

        _ytForm.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var mp = _ytForm.MusicPlayer;
                if (mp == null) return;
                if (mp.TryCreateSongFromFile(libSong.FilePath, out var song, out var errMsg) && song != null)
                    mp.AddNextSongExternal(song);
            }
            catch { }
        });

        return new { success = true };
    }

    private async Task<object> DownloadAndPlayYouTubeAsync(JObject msg)
    {
        var videoId = msg["videoId"]?.ToString();
        var action = msg["action"]?.ToString() ?? "play";
        var title = msg["title"]?.ToString() ?? "";
        var author = msg["author"]?.ToString() ?? "";

        if (string.IsNullOrEmpty(videoId)) return new { error = "No videoId" };

        var videoUrl = $"https://www.youtube.com/watch?v={videoId}";
        var downloadPath = YoutubeForm.downloadPath;

        try
        {
            SendEvent("ytDownloadProgress", new { videoId, stage = "downloading", progress = 0 });

            var progress = new Progress<double>(pct =>
            {
                try { SendEvent("ytDownloadProgress", new { videoId, stage = "downloading", progress = (int)(pct * 100) }); }
                catch { }
            });

            var filePath = await _ytService.DownloadVideoWithProgressAsync(videoUrl, downloadPath, progress);

            SendEvent("ytDownloadProgress", new { videoId, stage = "importing", progress = 100 });

            var youTubeId = YoutubeService.ExtractVideoId(videoUrl);
            var libSong = _libraryService.ImportDownloadedSong(filePath, youTubeId, videoUrl, title, author);

            SendEvent("ytDownloadProgress", new { videoId, stage = "playing", progress = 100 });

            if (libSong == null || !System.IO.File.Exists(libSong.FilePath))
                return new { error = "Import failed" };

            string songId = libSong.Id;

            if (action == "play")
                PlayLibrarySong(new JObject { ["songId"] = songId });
            else if (action == "playNext")
                PlayLibrarySongNext(new JObject { ["songId"] = songId });
            else if (action == "queue")
                QueueLibrarySong(new JObject { ["songId"] = songId });

            SendEvent("ytDownloadProgress", new { videoId, stage = "done", progress = 100 });

            return new { success = true, songId, action };
        }
        catch (Exception ex)
        {
            SendEvent("ytDownloadProgress", new { videoId, stage = "error", progress = 0 });
            return new { error = ex.Message };
        }
    }

    // ────────────────────────────────────────────────────────
    //  MusicShare interop
    // ────────────────────────────────────────────────────────

    private void MusicShareSendEvent(string eventName, object? data)
    {
        var mpSend = _ytForm.NewUiRef?.MusicPlayerSendMessage;
        if (mpSend == null) return;
        try
        {
            var json = JsonConvert.SerializeObject(new { type = "event", eventName, data });
            mpSend(json);
        }
        catch { }
    }

    private void MusicShare_ServiceStatusChanged(object? sender, ShareStatus status)
    {
        MusicShareSendEvent("musicShareStatus", new
        {
            status = status.ToString().ToLowerInvariant(),
            sessionId = _musicShareService?.SessionId,
            sharing = _musicShareSharing,
            receiving = _musicShareReceiving,
            metadataOnly = _musicShareMetadataOnly
        });
    }

    private void MusicShare_ServiceErrorOccurred(object? sender, string error)
    {
        MusicShareSendEvent("musicShareError", new { error });
    }

    private void MusicShare_ServiceMetadataReceived(object? sender, MusicShareMetadata metadata)
    {
        MusicShareSendEvent("musicShareMetadataReceived", new
        {
            title = metadata.Title,
            artist = metadata.Artist,
            lyrics = metadata.Lyrics,
            hasSyncedLyrics = metadata.HasSyncedLyrics,
            elapsedSeconds = metadata.ElapsedSeconds,
            totalSeconds = metadata.TotalSeconds,
            thumbnailData = metadata.ThumbnailData
        });
    }

    private async void MusicShare_OnNowPlayingTick(object? sender, MusicPlayer.NowPlayingEventArgs e)
    {
        if (!_musicShareSharing || _musicShareService == null) return;
        try
        {
            if (_musicShareLastSong != e.Current)
            {
                _musicShareLastSong = e.Current;
                _musicShareCachedThumbnail = null;
                if (e.Current?.thumbnail?.Source is BitmapSource bitmapSource)
                    _musicShareCachedThumbnail = MusicShareService.BitmapSourceToBase64(bitmapSource);

                _musicShareCachedLyrics = null;
                _musicShareCachedHasSyncedLyrics = false;

                if (e.Current != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var lyricsResult = await (_musicShareLyricsService ?? new LyricsService()).GetLyricsAsync(
                                e.Current.Artist.Replace(" - Topic", ""),
                                e.Current.Title,
                                e.Current.Duration);
                            if (lyricsResult.Found)
                            {
                                if (lyricsResult.HasSynced)
                                {
                                    _musicShareCachedLyrics = lyricsResult.SyncedLyrics;
                                    _musicShareCachedHasSyncedLyrics = true;
                                }
                                else if (!string.IsNullOrWhiteSpace(lyricsResult.PlainLyrics))
                                {
                                    _musicShareCachedLyrics = lyricsResult.PlainLyrics;
                                    _musicShareCachedHasSyncedLyrics = false;
                                }
                            }
                        }
                        catch { }
                    });
                }
            }

            var metadata = new MusicShareMetadata
            {
                Title = e.Current?.Title ?? "Unknown",
                Artist = e.Current?.Artist ?? "Unknown",
                ElapsedSeconds = e.CurrentTime.TotalSeconds,
                TotalSeconds = e.TotalTime.TotalSeconds,
                ThumbnailData = _musicShareCachedThumbnail,
                Lyrics = _musicShareCachedLyrics,
                HasSyncedLyrics = _musicShareCachedHasSyncedLyrics
            };
            await _musicShareService.SendMetadataAsync(metadata);
        }
        catch { }
    }

    private async void MusicShare_OnAudioSamplesCaptured(object? sender, float[] samples)
    {
        if (!_musicShareSharing || _musicShareMetadataOnly || _musicShareService == null) return;
        try
        {
            var copy = new float[samples.Length];
            Array.Copy(samples, copy, samples.Length);
            _musicShareAccumulatedSamples.AddRange(copy);

            if (_musicShareAccumulatedSamples.Count >= MUSIC_SHARE_TARGET_SAMPLES)
            {
                var chunk = new float[MUSIC_SHARE_TARGET_SAMPLES];
                _musicShareAccumulatedSamples.CopyTo(0, chunk, 0, MUSIC_SHARE_TARGET_SAMPLES);
                _musicShareAccumulatedSamples.RemoveRange(0, MUSIC_SHARE_TARGET_SAMPLES);
                await _musicShareService.SendAudioChunkAsync(chunk, 44100, 2);
                _musicShareBytesSent += chunk.Length * sizeof(float);
            }
        }
        catch { }
    }

    private async Task<object> MusicShareStart(JObject msg)
    {
        if (_musicShareSharing || _musicShareReceiving)
            return new { error = "Already sharing or receiving" };

        var mp = _ytForm.MusicPlayer;
        if (mp == null) return new { error = "Music Player not open" };

        try
        {
            _musicShareMetadataOnly = msg["metadataOnly"]?.ToObject<bool>() ?? true;
            _musicShareService = new MusicShareService();
            _musicShareService.StatusChanged += MusicShare_ServiceStatusChanged;
            _musicShareService.ErrorOccurred += MusicShare_ServiceErrorOccurred;
            _musicShareLyricsService = new LyricsService();

            _musicShareSharing = true;
            var sessionId = await _musicShareService.StartSharingAsync();
            _musicShareSessionStart = DateTime.Now;
            _musicShareBytesSent = 0;
            _musicShareLastSong = null;
            _musicShareCachedLyrics = null;
            _musicShareCachedThumbnail = null;
            _musicShareAccumulatedSamples.Clear();

            mp.NowPlayingTick += MusicShare_OnNowPlayingTick;
            mp.AudioSamplesCaptured += MusicShare_OnAudioSamplesCaptured;

            return new { success = true, sessionId, metadataOnly = _musicShareMetadataOnly };
        }
        catch (Exception ex)
        {
            _musicShareSharing = false;
            if (_musicShareService != null)
            {
                _musicShareService.StatusChanged -= MusicShare_ServiceStatusChanged;
                _musicShareService.ErrorOccurred -= MusicShare_ServiceErrorOccurred;
                _musicShareService = null;
            }
            return new { error = ex.Message };
        }
    }

    private async Task<object> MusicShareStop()
    {
        if (!_musicShareSharing) return new { error = "Not sharing" };

        try
        {
            var mp = _ytForm.MusicPlayer;
            if (mp != null)
            {
                mp.NowPlayingTick -= MusicShare_OnNowPlayingTick;
                mp.AudioSamplesCaptured -= MusicShare_OnAudioSamplesCaptured;
            }

            if (_musicShareService != null)
            {
                _musicShareService.StatusChanged -= MusicShare_ServiceStatusChanged;
                _musicShareService.ErrorOccurred -= MusicShare_ServiceErrorOccurred;
                await _musicShareService.StopSharingAsync();
                _musicShareService = null;
            }

            _musicShareSharing = false;
            _musicShareAccumulatedSamples.Clear();
            _musicShareLastSong = null;
            _musicShareCachedLyrics = null;
            _musicShareCachedThumbnail = null;

            MusicShareSendEvent("musicShareStatus", new
            {
                status = "idle",
                sharing = false,
                receiving = false
            });

            return new { success = true };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    private async Task<object> MusicShareConnect(JObject msg)
    {
        if (_musicShareSharing || _musicShareReceiving)
            return new { error = "Already sharing or receiving" };

        var sessionId = msg["sessionId"]?.ToString();
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length != 6)
            return new { error = "Session ID must be 6 digits" };

        try
        {
            _musicShareService = new MusicShareService();
            _musicShareService.ReceiveAudio = msg["receiveAudio"]?.ToObject<bool>() ?? true;
            _musicShareService.StatusChanged += MusicShare_ServiceStatusChanged;
            _musicShareService.ErrorOccurred += MusicShare_ServiceErrorOccurred;
            _musicShareService.MetadataReceived += MusicShare_ServiceMetadataReceived;

            _musicShareReceiving = true;
            await _musicShareService.StartReceivingAsync(sessionId);

            return new { success = true, sessionId };
        }
        catch (Exception ex)
        {
            _musicShareReceiving = false;
            if (_musicShareService != null)
            {
                _musicShareService.StatusChanged -= MusicShare_ServiceStatusChanged;
                _musicShareService.ErrorOccurred -= MusicShare_ServiceErrorOccurred;
                _musicShareService.MetadataReceived -= MusicShare_ServiceMetadataReceived;
                _musicShareService = null;
            }
            return new { error = ex.Message };
        }
    }

    private async Task<object> MusicShareDisconnect()
    {
        if (!_musicShareReceiving) return new { error = "Not receiving" };

        try
        {
            if (_musicShareService != null)
            {
                _musicShareService.StatusChanged -= MusicShare_ServiceStatusChanged;
                _musicShareService.ErrorOccurred -= MusicShare_ServiceErrorOccurred;
                _musicShareService.MetadataReceived -= MusicShare_ServiceMetadataReceived;
                await _musicShareService.StopReceivingAsync();
                _musicShareService = null;
            }

            _musicShareReceiving = false;

            MusicShareSendEvent("musicShareStatus", new
            {
                status = "idle",
                sharing = false,
                receiving = false
            });

            return new { success = true };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    private object MusicShareGetStatus()
    {
        var elapsed = _musicShareSharing ? (DateTime.Now - _musicShareSessionStart) : TimeSpan.Zero;
        return new
        {
            sharing = _musicShareSharing,
            receiving = _musicShareReceiving,
            metadataOnly = _musicShareMetadataOnly,
            sessionId = _musicShareService?.SessionId,
            status = _musicShareService?.Status.ToString().ToLowerInvariant() ?? "idle",
            elapsed = elapsed.TotalSeconds,
            bytesSent = _musicShareBytesSent
        };
    }
}
