using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite;

/// <summary>A request that appeared in the send-in list for the first time.</summary>
public sealed record NewRequestInfo(string Ytid, DateTime? SentInUtc, string Message, bool IsApproved);

public class YoutubeFormInterop
{
    private readonly ISongRequestHost _ytForm;
    private readonly YoutubeService _ytService;
    private readonly LibraryService _libraryService;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _commandResponses = new();

    public Action<string>? SendMessage { get; set; }

    /// <summary>
    /// Raised for every event the interop would send to the web UI. The local API
    /// and MCP servers use this to stream live updates to their clients.
    /// </summary>
    public event Action<string, object?>? EventRaised;

    /// <summary>
    /// Raised after a fetch of the request list (the send-in list) completed, so
    /// API/WebSocket clients can refresh without polling the companion server.
    /// </summary>
    public event Action? RequestsUpdated;

    /// <summary>
    /// Raised when a fetch found request ids that were not seen before. The local
    /// API forwards these as the <c>requests.new</c> WebSocket event.
    /// </summary>
    public event Action<IReadOnlyList<NewRequestInfo>>? NewRequestsDetected;

    /// <summary>Lyrics provider for the current track (empty until a lookup finishes).</summary>
    public string CurrentLyricsProvider { get; private set; } = string.Empty;

    /// <summary>Whether the current track has lyrics from any provider.</summary>
    public bool CurrentLyricsAvailable { get; private set; }

    /// <summary>Runs an action synchronously on the UI/host thread.</summary>
    public Action<Action> RunOnUi { get; set; } = a => a();

    /// <summary>Posts an action to the UI/host thread.</summary>
    public Action<Action> PostToUi { get; set; } = a => a();

    /// <summary>Shows a native open-file dialog. Args: (title, filter, multiselect) → selected paths or null.</summary>
    public Func<string, string, bool, string[]?>? ShowOpenFileDialog { get; set; }

    /// <summary>Shows a native folder picker. Args: (title) → the folder, or null.</summary>
    public Func<string, string?>? ShowOpenFolderDialog { get; set; }

    /// <summary>
    /// Reports that a download started (true) or finished (false), so a host can
    /// keep the process alive in the background (mobile runs a foreground service).
    /// </summary>
    public Action<bool, string>? DownloadActivityChanged { get; set; }

    /// <summary>Download progress (0..1) for the host's background notification.</summary>
    public Action<double>? DownloadProgressChanged { get; set; }

    /// <summary>
    /// In-app YouTube sign-in: the host shows its own login web view and returns
    /// the cookies it harvested (mobile), instead of importing them from a desktop
    /// browser with yt-dlp. Null on the desktop.
    /// </summary>
    public Func<Task<List<System.Net.Cookie>?>>? SignInAsync { get; set; }

    /// <summary>
    /// The browsers the host can import cookies from. The mobile host returns an
    /// empty list, so the UI offers the in-app sign-in instead.
    /// </summary>
    public Func<List<string>>? AuthBrowsersProvider { get; set; }

    /// <summary>
    /// Async pickers for hosts whose dialogs are asynchronous (mobile). Preferred
    /// by <c>importLibrary</c>; the synchronous delegates above remain for the
    /// legacy queue path.
    /// </summary>
    public Func<string, Task<string[]?>>? PickFilesAsync { get; set; }

    /// <summary>Single audio-file picker for the soundboard editor.</summary>
    public Func<string, Task<string?>>? PickSoundFile { get; set; }

    public Func<string, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>
    /// Prepares a picked folder for the library and returns the folder to scan.
    ///
    /// Mobile hosts copy it into the app's sandbox (the only writable place there,
    /// and where the scanner can read it); the desktop leaves it in place, so it
    /// stays null and the picked path is scanned directly.
    /// </summary>
    public Func<string, Task<string?>>? PrepareImportFolder { get; set; }

    /// <summary>Same for individually picked files; returns the folder to scan.</summary>
    public Func<string[], Task<string?>>? PrepareImportFiles { get; set; }

    /// <summary>
    /// Turns one picked file into a readable local path. Mobile pickers hand out
    /// <c>content://</c> URIs, which the soundboard editor used to pass straight to
    /// <c>File.Exists</c> — so selecting a sound file never applied.
    /// </summary>
    public Func<string, Task<string?>>? PrepareSoundFile { get; set; }

    public ISongRequestHost YtForm => _ytForm;
    public LibraryService LibraryService => _libraryService;

    /// <summary>
    /// Invoked when the UI asks the app to quit (the Quit buttons). The host window
    /// owns the shutdown so it can release the tray icon and the OS media session
    /// before the process exits.
    /// </summary>
    public Action? QuitRequested { get; set; }

    private string? _currentSongPath;

    // Artwork already pushed via queueUpdate / queueThumbnails, keyed by song path.
    // Bulk queue loading removes the repeated queue updates that used to act as
    // thumbnail retries while the background cache caught up, so missing artwork is
    // filled in by targeted queueThumbnails events instead.
    private readonly object _queueThumbnailSync = new();
    private readonly Dictionary<string, string> _queueThumbnailsSent = new(StringComparer.OrdinalIgnoreCase);
    private int _queueThumbnailRefreshScheduled;
    private int _queueThumbnailRefreshes;

    /// <summary>Stops a pathological loop if the cache never makes progress.</summary>
    private const int MaxQueueThumbnailRefreshes = 120;
    private bool _musicPlayerSubscribed;
    private bool _pendingMusicShareOpen;
    private LyricsService? _musicPlayerLyricsService;
    private readonly object _musicPlayerLyricsCacheLock = new();
    private readonly System.Collections.Generic.Dictionary<string, LyricsResult> _musicPlayerLyricsCache = new(StringComparer.OrdinalIgnoreCase);

    // MIDI assignment capture (remote control settings)
    private readonly object _midiCaptureLock = new();
    private bool _midiCaptureSubscribed;
    private readonly EventHandler<string> _midiActivityHandler;

    // Soundboard engine (lazily created when the soundboard is shown)
    private SoundboardEngine? _soundboard;
    private readonly object _soundboardLock = new();

    // Visualization audio streamer (lazily created when the visualization is shown)
    private VisualizationStreamer? _vizStreamer;
    private readonly object _vizLock = new();

    // Advanced audio: song analysis cache + event subscriptions for the sleep
    // timer and equalizer.
    private readonly SongAnalysisService _analysis = SongAnalysisService.Instance;
    private readonly LibraryAnalysisService _libraryAnalysis;

    public LibraryAnalysisService LibraryAnalysis => _libraryAnalysis;

    // Announcement engine (lazily created; kept alive across window open/close)
    private AnnouncementEngine? _announcement;
    private readonly object _announcementLock = new();

    // System media integration (MPRIS/SMTC/...). Started once the UI is up.
    private MediaSessionBridge? _mediaSessionBridge;
    private readonly object _mediaSessionLock = new();
    private Task? _mediaSessionStartTask;
    private IntPtr _mediaSessionWindowHandle;

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
    private DateTime _musicShareThumbnailRetryAt;
    private int _musicShareThumbnailRetries;
    private MusicShareMetadata? _musicShareLastMetadata;
    // What the presentation window currently shows for a received share, so the
    // 500 ms metadata poll does not rebuild (and re-scroll) the lyrics each time.
    private string? _musicSharePresentationLyricsKey;
    private MusicShareAudioPlayer? _musicShareAudioPlayer;
    private float _musicShareReceiveVolume = 1.0f;
    private sealed class CachedSubtitleData
    {
        public string SubtitleText { get; set; } = string.Empty;
        public string SubtitleTimedText { get; set; } = string.Empty;
    }

    public YoutubeFormInterop(ISongRequestHost ytForm, YoutubeService ytService)
    {
        _ytForm = ytForm;
        _ytService = ytService;
        _ytForm.EventSink = SendEvent;
        _ytForm.SongDownloaded = TryAutoAddDownloadedSong;
        _libraryService = new LibraryService();
        _libraryAnalysis = new LibraryAnalysisService(_libraryService, _analysis)
        {
            PlaybackActive = () => _ytForm.Music.IsPlaying
        };
        _libraryService.SongAdded += song =>
        {
            try
            {
                if (ConfigService.Instance.Current?.AutoAnalyzeLibraryOnAdd == true)
                    _libraryAnalysis.QueueSong(song);
            }
            catch { }
        };
        _libraryService.SongRemoved += song => _libraryAnalysis.RemoveSong(song);
        _libraryAnalysis.ProgressChanged += progress =>
        {
            var json = CamelCase(progress);
            if (json == null) return;
            try { SendEvent("libraryAnalysisProgress", json); } catch { }
            try { MusicPlayerSendEvent("libraryAnalysisProgress", json); } catch { }
        };
        try
        {
            if (ConfigService.Instance.Current?.AutoAnalyzeLibraryOnAlgorithmUpdate == true)
                _libraryAnalysis.StartIfOutdated();
        }
        catch { }

        // Marshal host operations through the host (WPF dispatcher today; headless host later).
        RunOnUi = ytForm.RunOnUi;
        PostToUi = ytForm.PostToUi;

        // Forward MIDI activity (processed remote-control input) to the web UI
        _midiActivityHandler = (_, message) => SendEvent("midiActivity", new { message });
        try { RemoteControlService.Instance.MidiActivity += _midiActivityHandler; } catch { }

        // Route legacy "open Music Share" calls to the Photino share modal.
        try
        {
            var mp = _ytForm.Music;
            if (mp != null)
            {
                mp.OpenMusicShareHandler = () => MusicPlayerSendEvent("openMusicShare", null);
                mp.OpenVisualizationHandler = ShowVisualization;
                mp.AnnouncementOpenHandler = () =>
                {
                    EnsureAnnouncement();
                    _ytForm.NewUiRef?.ShowAnnouncementWindow();
                };

                // Advanced audio state is pushed to the player pages and mirrored to
                // the WebSocket/API event stream by SendEvent. The strongly typed
                // snapshots are camel-cased to match the rest of the JSON protocol.
                mp.SleepTimer.StateChanged += state =>
                    MusicPlayerSendEvent("sleepTimerUpdate", CamelCase(state));
                mp.Equalizer.Changed += state =>
                    MusicPlayerSendEvent("equalizerUpdate", CamelCase(state));

                // Auto-analysis must not depend on the player page being open or
                // subscribed: track changes drive it directly from the engine.
                mp.NowPlayingChanged = song =>
                {
                    if (song != null) SendOrStartSongAnalysis(song);
                };
                _analysis.Progress += progress =>
                    MusicPlayerSendEvent("songAnalysisProgress", new
                    {
                        songPath = progress.SongPath,
                        progress = progress.Progress,
                        stage = progress.Stage
                    });
                _analysis.Analyzed += analysis =>
                {
                    MusicPlayerSendEvent("songAnalysisUpdate", CamelCase(analysis));
                    try
                    {
                        var current = _ytForm.Music.Queue.FirstOrDefault();
                        if (current != null &&
                            string.Equals(AppPaths.ResolvePortablePath(current.songPath),
                                          analysis.SongPath, StringComparison.OrdinalIgnoreCase))
                        {
                            PresentationSendEvent("songChaptersUpdate", new { parts = CamelCase(analysis.Parts) });

                            // When karaoke is on, refresh the lyrics so word timings
                            // can use the just-finished analysis timeline.
                            if (ConfigService.Instance.Current?.KaraokeMode == true)
                            {
                                var key = $"{current.songPath}|{current.Artist}|{current.Title}";
                                LyricsResult? cached = null;
                                lock (_musicPlayerLyricsCacheLock)
                                {
                                    _musicPlayerLyricsCache.TryGetValue(key, out cached);
                                }
                                if (cached != null)
                                {
                                    var mpSend = _ytForm.NewUiRef?.MusicPlayerSendMessage;
                                    if (mpSend != null) SendLyricsResult(mpSend, cached, current);
                                    PresentationSendLyricsResult(cached, current);
                                }
                            }
                        }
                    }
                    catch { }
                };
            }
        }
        catch { }
    }

    public async void HandleMessage(string json)
    {
        try
        {
            var msg = JObject.Parse(json);
            var type = msg["type"]?.ToString();

            if (type == "request") DropNullValues(msg);

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
    /// Removes JSON properties whose value is null, treating them as "not provided".
    ///
    /// The web UI cannot express "no value" for a number: <c>parseInt('')</c> is NaN,
    /// and <c>JSON.stringify(NaN)</c> is <c>null</c> — which Newtonsoft turns into
    /// "Can not convert Null to Int32" if a handler reads it (e.g. saving settings
    /// with no MIDI device selected). Handlers already treat a missing property as
    /// "keep the default", so dropping nulls makes a null behave the same way.
    /// </summary>
    private static void DropNullValues(JToken token)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var property in obj.Properties()
                             .Where(p => p.Value.Type is JTokenType.Null or JTokenType.Undefined)
                             .ToList())
                {
                    property.Remove();
                }

                foreach (var property in obj.Properties().ToList())
                    DropNullValues(property.Value);
                break;

            case JArray array:
                foreach (var item in array)
                    DropNullValues(item);
                break;
        }
    }

    /// <summary>
    /// Handle a message from a specific window, routing responses back to that window.
    /// </summary>
    public async void HandleMessage(string json, Action<string> responseTarget)
    {
        _responseTarget.Value = responseTarget;
        try
        {
            var msg = JObject.Parse(json);
            var type = msg["type"]?.ToString();

            if (type == "request") DropNullValues(msg);

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
            _responseTarget.Value = null;
        }
    }

    private static readonly AsyncLocal<Action<string>?> _responseTarget = new();

    /// <summary>
    /// Executes any command the web UI can send and returns its result. This is the
    /// single seam used by the local API and MCP server, so both expose exactly the
    /// same features as the application itself.
    /// </summary>
    /// <param name="method">Command name (see <see cref="Integration.CommandCatalog"/>).</param>
    /// <param name="parameters">Command arguments, or <c>null</c> for none.</param>
    /// <param name="cancellationToken">Cancels waiting for the response.</param>
    public async Task<JToken?> ExecuteCommandAsync(
        string method,
        JObject? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(method))
            throw new ArgumentException("A command name is required.", nameof(method));

        var id = "srapi-" + Guid.NewGuid().ToString("N");
        var message = parameters == null ? new JObject() : (JObject)parameters.DeepClone();
        message["type"] = "request";
        message["id"] = id;
        message["method"] = method;

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commandResponses[id] = completion;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));

        try
        {
            HandleMessage(message.ToString(Formatting.None));
            var json = await completion.Task.ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(json) ? null : JToken.Parse(json);
        }
        finally
        {
            _commandResponses.TryRemove(id, out _);
        }
    }

    private async Task HandleRequest(JObject msg)
    {
        var id = msg["id"]?.ToString();
        var method = msg["method"]?.ToString();
        var responseTarget = _responseTarget.Value ?? SendMessage;

        // Friendly aliases for API/MCP clients that use feature-prefixed names.
        method = method switch
        {
            "sleepTimerGetState" => "getSleepTimer",
            "sleepTimerStart" => "startSleepTimer",
            "sleepTimerCancel" => "cancelSleepTimer",
            "sleepTimerExtend" => "extendSleepTimer",
            "sleepTimerSetOptions" => "setSleepTimerOptions",
            "getEqualizerState" => "getEqualizer",
            "setEqualizerGains" => "setEqualizer",
            "getSongAnalysisResult" => "getSongAnalysis",
            "analyseSong" => "analyzeSong",
            _ => method
        };

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
                    SendResponse(id, new { success = true });
                    return;
                case "showPresentation":
                    _ytForm.NewUiRef?.ShowPresentationWindow();
                    SendResponse(id, new { success = true });
                    return;
                case "closePresentation":
                    _ytForm.NewUiRef?.ClosePresentationWindow();
                    SendResponse(id, new { success = true });
                    return;
                case "presentationReady":
                {
                    PostToUi(() =>
                    {
                        SubscribeToMusicPlayer();
                        SendPresentationCurrentState();
                    });
                    SendResponse(id, new { success = true });
                    return;
                }
                case "setOperatingMode":
                {
                    var modeName = msg["mode"]?.ToString() ?? "SongRequests";
                    if (!Enum.TryParse<StartupMode>(modeName, true, out var requestedMode))
                    {
                        SendResponse(id, new { error = "Unknown operating mode" });
                        return;
                    }

                    ConfigService.Instance.Update(cfg => cfg.OperatingMode = requestedMode.ToString());
                    RunOnUi(() => _ytForm.NewUiRef?.SetOperatingMode(requestedMode));
                    SendResponse(id, new { success = true, mode = requestedMode.ToString() });
                    return;
                }
                case "setPresentationZoom":
                {
                    var requested = msg["zoom"] ?? msg["data"]?["zoom"];
                    var zoom = requested == null ? 1.0 : requested.ToObject<double>();
                    SetPresentationZoom(zoom);
                    SendResponse(id, new { success = true, zoom = Math.Clamp(zoom, 0.5, 2.0) });
                    return;
                }
                case "setPresentationSource":
                {
                    var source = msg["source"]?.ToString() ?? "player";
                    ConfigService.Instance.Update(cfg => cfg.PresentationSource = source);

                    // Notify the presentation window about the source change
                    PresentationSendEvent("presentationSettings", new
                    {
                        presentationSource = source
                    });
                    // Switching back to "player" must also push the player's current
                    // track/lyrics again; previously only the visibility changed and
                    // the window kept showing stale (or empty) music-share state.
                    SendPresentationCurrentState();

                    SendResponse(id, new { success = true });
                    return;
                }
                case "showSoundboard":
                    ShowSoundboard();
                    SendResponse(id, new { success = true });
                    return;
                case "showVisualization":
                    ShowVisualization();
                    SendResponse(id, new { success = true });
                    return;
                case "visualizationReady":
                    // The page (desktop window or mobile stage tab) is ready for
                    // frames. Starting/refreshing here also covers a page that was
                    // constructed hidden before playback started.
                    EnsureVisualization();
                    SendResponse(id, new { success = true, capturing = _ytForm.Music.CapturingProvider != null });
                    return;
                case "closeVisualization":
                    _ytForm.NewUiRef?.CloseVisualizationWindow();
                    SendResponse(id, new { success = true });
                    return;
                case "setFullscreen":
                {
                    // The sub-window pages ask the host: the DOM Fullscreen API needs the
                    // embedder to handle WebKit's fullscreen request, which Photino does
                    // not, so the button used to do nothing at all.
                    bool wanted = (msg["enabled"] ?? msg["data"]?["enabled"])?.ToObject<bool>() ?? false;
                    string? windowName = (msg["window"] ?? msg["data"]?["window"])?.ToString();
                    bool applied = _ytForm.NewUiRef?.SetSubWindowFullscreen(windowName, wanted) ?? false;
                    SendResponse(id, new { success = applied, fullscreen = applied && wanted });
                    return;
                }
                case "playPause":
                    PostToUi(() => _ytForm.Music.TogglePlayPause());
                    SendResponse(id, new { success = true });
                    return;
                case "skipNext":
                    PostToUi(() => _ytForm.Music.Next());
                    SendResponse(id, new { success = true });
                    return;
                case "skipPrevious":
                    PostToUi(() => _ytForm.Music.Previous());
                    SendResponse(id, new { success = true });
                    return;
                case "stop":
                    PostToUi(() => _ytForm.Music.Stop());
                    SendResponse(id, new { success = true });
                    return;
                case "seek":
                {
                    var posVal = msg["position"]?.ToObject<double>();
                    if (posVal != null)
                    {
                        double pos = posVal.Value;
                        PostToUi(() => _ytForm.Music.Seek(pos));
                    }
                    SendResponse(id, new { success = true });
                    return;
                }
                case "toggleRepeat":
                {
                    PostToUi(() =>
                    {
                        _ytForm.Music.ToggleRepeat();
                        // Repeat is a player state, not a queue mutation: tell every
                        // UI about it explicitly so toggle buttons can report it.
                        SendQueueUpdate();
                    });
                    SendResponse(id, new { success = true });
                    return;
                }
                case "clearQueue":
                    PostToUi(() => _ytForm.Music.ClearQueue());
                    SendResponse(id, new { success = true });
                    return;
                case "musicPlayerReady":
                {
                    PostToUi(() =>
                    {
                        SubscribeToMusicPlayer();
                        SendQueueUpdate();
                        double volInit = 0.8, cfInit = 4.0;
                        bool canControlInit = true;
                        bool repeatInit = false;
                        var mp = _ytForm.Music;
                        if (mp != null)
                        {
                            volInit = mp.VolumeValue;
                            cfInit = mp.CrossfadeSeconds;
                            canControlInit = mp.CanControlVolume;
                            repeatInit = mp.RepeatEnabled;
                        }
                        SendResponse(id, new { success = true, volume = volInit, crossfade = cfInit, canControlVolume = canControlInit, repeatEnabled = repeatInit, lyricsFontScale = (double)ConfigService.Instance.Current.LyricsFontScale, lyricsHidden = ConfigService.Instance.Current.LyricsHidden, sleepTimer = CamelCase(_ytForm.Music.SleepTimer.GetState()), equalizer = CamelCase(_ytForm.Music.Equalizer.GetState()) });
                        SendCurrentNowPlayingToNewUI();

                        MusicPlayerSendEvent("musicPlayerConfig", new
                        {
                            enableAnnouncements = ConfigService.Instance.Current?.EnableAnnouncements ?? true
                        });

                        if (_pendingMusicShareOpen)
                        {
                            _pendingMusicShareOpen = false;
                            MusicPlayerSendEvent("openMusicShare", null);
                        }
                    });
                    return;
                }
                case "shuffleQueue":
                {
                    PostToUi(() => _ytForm.Music.ShuffleQueue());
                    SendResponse(id, new { success = true });
                    return;
                }
                case "setVolume":
                {
                    var volVal = msg["value"]?.ToObject<double>() ?? -1;
                    if (volVal >= 0)
                        PostToUi(() =>
                        {
                            if (_ytForm.Music != null)
                                _ytForm.Music.SetVolume((float)volVal);
                        });
                    SendResponse(id, new { success = true });
                    return;
                }
                case "setCrossfade":
                {
                    var cfVal = msg["value"]?.ToObject<double>() ?? -1;
                    if (cfVal >= 0)
                        PostToUi(() =>
                        {
                            if (_ytForm.Music != null)
                                _ytForm.Music.SetCrossfade(cfVal);
                        });
                    SendResponse(id, new { success = true });
                    return;
                }
                case "getVolumeCrossfade":
                {
                    double volVal = 0.8, cfVal2 = 4.0;
                    bool canControlVal = true;
                    RunOnUi(() =>
                    {
                        var mp = _ytForm.Music;
                        if (mp != null)
                        {
                            volVal = mp.VolumeValue;
                            cfVal2 = mp.CrossfadeSeconds;
                            canControlVal = mp.CanControlVolume;
                        }
                    });
                    SendResponse(id, new { volume = volVal, crossfade = cfVal2, canControlVolume = canControlVal });
                    return;
                }
                case "saveLyricsSettings":
                {
                    var fontScale = msg["fontScale"]?.ToObject<float>() ?? 1.0f;
                    var hidden = msg["hidden"]?.ToObject<bool>() ?? false;
                    ConfigService.Instance.Update(cfg =>
                    {
                        cfg.LyricsFontScale = fontScale;
                        cfg.LyricsHidden = hidden;
                    });
                    SendResponse(id, new { success = true });
                    return;
                }
                case "importLibrary":
                {
                    // Async (it may copy the picked music into the host's storage),
                    // so answer directly instead of going through `result`.
                    var importResult = await ImportLibraryAsync(msg);
                    SendResponse(id, importResult);
                    return;
                }
                case "addLocalFiles":
                {
                    var mode = msg["mode"]?.ToString() ?? "add";
                    var files = PickFilesAsync != null
                        ? await PickFilesAsync("Add Audio Files").ConfigureAwait(false)
                        : ShowOpenFileDialog?.Invoke("Add Audio Files",
                            "*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac;*.wma", true);

                    if (files is { Length: > 0 } && PrepareImportFiles != null)
                    {
                        // Mobile: the picked file may be a cache copy that vanishes.
                        var folder = await PrepareImportFiles(files).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(folder))
                            files = System.Linq.Enumerable.ToArray(
                                System.Linq.Enumerable.Select(files, f => System.IO.Path.Combine(folder, System.IO.Path.GetFileName(f))));
                    }

                    if (files != null && files.Length > 0)
                    {
                        PostToUi(() =>
                        {
                            var mp = _ytForm.Music;
                            if (mp != null)
                            {
                                foreach (var filePath in files)
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
                        });
                    }
                    SendResponse(id, new { success = true });
                    return;
                }
                case "removeQueueItem":
                    var idx = msg["index"]?.ToObject<int>() ?? -1;
                    if (idx >= 0)
                        PostToUi(() => _ytForm.Music.RemoveQueueItem(idx));
                    SendResponse(id, new { success = true });
                    return;
                case "removePlaylistQueueItem":
                    var plIdx = msg["index"]?.ToObject<int>() ?? -1;
                    if (plIdx >= 0)
                        PostToUi(() => _ytForm.Music.RemovePlaylistQueueItem(plIdx));
                    SendResponse(id, new { success = true });
                    return;
                case "movePlaylistQueueItem":
                    var plFromIdx = msg["fromIndex"]?.ToObject<int>() ?? -1;
                    var plToIdx = msg["toIndex"]?.ToObject<int>() ?? -1;
                    if (plFromIdx >= 0 && plToIdx >= 0)
                        PostToUi(() => _ytForm.Music.MovePlaylistQueueItem(plFromIdx, plToIdx));
                    SendResponse(id, new { success = true });
                    return;
                case "moveQueueItem":
                    var fromIdx = msg["fromIndex"]?.ToObject<int>() ?? -1;
                    var toIdx = msg["toIndex"]?.ToObject<int>() ?? -1;
                    if (fromIdx >= 0 && toIdx >= 0)
                        PostToUi(() => _ytForm.Music.MoveQueueItem(fromIdx, toIdx));
                    SendResponse(id, new { success = true });
                    return;
                case "requestLyrics":
                {
                    PostToUi(() => _ = FetchAndSendLyricsForSongAsync());
                    SendResponse(id, new { success = true });
                    return;
                }
                case "getEqualizer":
                    SendResponse(id, CamelCase(_ytForm.Music.Equalizer.GetState()));
                    return;
                case "setEqualizer":
                {
                    var eq = _ytForm.Music.Equalizer;
                    var enabled = msg["enabled"]?.ToObject<bool?>();
                    var preamp = msg["preampDb"]?.ToObject<double?>() ?? msg["preamp"]?.ToObject<double?>();
                    var preset = msg["preset"]?.ToString();
                    var gains = msg["gains"]?.ToObject<double[]>();
                    SendResponse(id, CamelCase(eq.Apply(enabled, preamp, preset, gains)));
                    return;
                }
                case "setEqualizerEnabled":
                    SendResponse(id, CamelCase(_ytForm.Music.Equalizer.SetEnabled(msg["enabled"]?.ToObject<bool>() ?? true)));
                    return;
                case "setEqualizerBand":
                {
                    var band = msg["index"]?.ToObject<int>() ?? -1;
                    var gain = msg["gainDb"]?.ToObject<double?>() ?? msg["gain"]?.ToObject<double>() ?? 0;
                    SendResponse(id, CamelCase(_ytForm.Music.Equalizer.SetBandGain(band, gain)));
                    return;
                }
                case "setEqualizerPreset":
                    SendResponse(id, CamelCase(_ytForm.Music.Equalizer.ApplyPreset(msg["preset"]?.ToString() ?? msg["name"]?.ToString() ?? "Flat")));
                    return;
                case "saveEqualizerPreset":
                    SendResponse(id, CamelCase(_ytForm.Music.Equalizer.SavePreset(msg["name"]?.ToString() ?? "Custom")));
                    return;
                case "deleteEqualizerPreset":
                    SendResponse(id, CamelCase(_ytForm.Music.Equalizer.DeletePreset(msg["name"]?.ToString() ?? "")));
                    return;
                case "resetEqualizer":
                    SendResponse(id, CamelCase(_ytForm.Music.Equalizer.Reset()));
                    return;
                case "getSleepTimer":
                    SendResponse(id, CamelCase(_ytForm.Music.SleepTimer.GetState()));
                    return;
                case "startSleepTimer":
                {
                    var minutes = msg["minutes"]?.ToObject<double?>()
                        ?? msg["durationMinutes"]?.ToObject<double?>()
                        ?? (msg["seconds"]?.ToObject<double?>() is double seconds ? seconds / 60.0
                            : msg["durationSeconds"]?.ToObject<double?>() is double durationSeconds ? durationSeconds / 60.0
                            : 30);
                    var fade = msg["fadeOutSeconds"]?.ToObject<double?>() ?? msg["fadeSeconds"]?.ToObject<double?>();
                    var lastSong = msg["playLastSongToEnd"]?.ToObject<bool?>() ?? msg["playToEnd"]?.ToObject<bool?>();
                    SendResponse(id, CamelCase(_ytForm.Music.SleepTimer.Start(minutes, fade, lastSong)));
                    return;
                }
                case "cancelSleepTimer":
                    SendResponse(id, CamelCase(_ytForm.Music.SleepTimer.Cancel()));
                    return;
                case "extendSleepTimer":
                {
                    var minutes = msg["minutes"]?.ToObject<double?>() ?? 5;
                    SendResponse(id, CamelCase(_ytForm.Music.SleepTimer.Extend(TimeSpan.FromMinutes(minutes))));
                    return;
                }
                case "setSleepTimerOptions":
                {
                    var fade = msg["fadeOutSeconds"]?.ToObject<double?>() ?? msg["fadeSeconds"]?.ToObject<double?>();
                    var lastSong = msg["playLastSongToEnd"]?.ToObject<bool?>() ?? msg["playToEnd"]?.ToObject<bool?>();
                    SendResponse(id, CamelCase(_ytForm.Music.SleepTimer.SetOptions(fade, lastSong)));
                    return;
                }
                case "getSongAnalysis":
                    SendResponse(id, CamelCase(GetSongAnalysis(msg)));
                    return;
                case "analyzeSong":
                {
                    var analysis = await AnalyzeSongAsync(msg).ConfigureAwait(false);
                    SendResponse(id, CamelCase(analysis));
                    return;
                }
                case "getSongAnalysisCache":
                    SendResponse(id, CamelCase(new
                    {
                        analyses = _analysis.Cached
                            .Where(a => string.Equals(a.AlgorithmVersion, SongAnalyzer.CurrentAlgorithmVersion,
                                StringComparison.Ordinal))
                            .ToList()
                    }));
                    return;
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
                    result = await QueueSongAsync(msg["ytid"]?.ToString());
                    break;
                case "playNext":
                    result = await PlayNextAsync(msg["ytid"]?.ToString());
                    break;
                case "downloadAndProcess":
                    result = await DownloadAndProcessSongAsync(msg["ytid"]?.ToString(), msg["action"]?.ToString());
                    break;
                case "isFileDownloaded":
                    result = IsFileDownloaded(msg["ytid"]?.ToString());
                    break;
                case "getVersion":
                    result = new { version = AppInfo.version };
                    break;
                case "checkForUpdates":
                    result = await CheckForUpdatesAsync();
                    break;
                case "getSettings":
                    result = GetFullConfig();
                    break;
                case "getIntegrationStatus":
                    result = Integration.AppIntegrationServer.Instance.GetStatusObject();
                    break;
                case "reloadConfig":
                    result = ReloadConfig();
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
                case "saveRemoteConfig":
                    result = SaveRemoteConfig(msg);
                    break;
                case "applyMidiDevices":
                    result = ApplyMidiDevices(msg);
                    break;
                case "testMidiFeedback":
                    result = TestMidiFeedback();
                    break;
                case "startMidiCapture":
                    result = StartMidiCapture();
                    break;
                case "stopMidiCapture":
                    result = StopMidiCapture();
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
                case "getAuthBrowsers":
                    result = GetAuthBrowsers();
                    break;
                case "getToolStatus":
                    result = GetToolStatus();
                    break;
                case "downloadTools":
                    result = StartToolDownload(msg);
                    break;
                case "dismissToolPrompt":
                    ConfigService.Instance.Update(c => c.ToolPromptDismissed = true);
                    result = new { success = true };
                    break;
                case "signIn":
                {
                    var signIn = SignInAsync;
                    if (signIn == null)
                    {
                        SendResponse(id, new { error = "Sign-in is not available on this platform." });
                        return;
                    }

                    try
                    {
                        var cookies = await signIn().ConfigureAwait(false);
                        if (cookies == null || cookies.Count == 0)
                        {
                            SendResponse(id, new { cancelled = true });
                            return;
                        }

                        YoutubeCookieService.Save(cookies);
                        _ytService.SetCookies(cookies);
                        SendResponse(id, new { success = true, cookieCount = cookies.Count });
                    }
                    catch (Exception ex)
                    {
                        SendResponse(id, new { error = ex.Message });
                    }
                    return;
                }
                case "grabCookies":
                    result = await GrabCookiesAsync(msg);
                    break;
                case "saveCookieBrowser":
                    result = SaveCookieBrowser(msg);
                    break;
                case "submitCookies":
                    result = SubmitCookies(msg);
                    break;
                case "quit":
                    QuitRequested?.Invoke();
                    result = new { success = true };
                    break;

                case "clearAuthCookies":
                    result = ClearAuthCookies();
                    break;
                case "completeSetup":
                    result = CompleteSetup(msg);
                    break;
                case "installUpdate":
                    result = await StartUpdate(msg);
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
                case "getLibraryAnalysisStatus":
                    result = CamelCase(_libraryAnalysis.GetStatus())!;
                    break;
                case "startLibraryAnalysis":
                    _libraryAnalysis.Start(msg["force"]?.ToObject<bool>() ?? false);
                    result = CamelCase(_libraryAnalysis.GetStatus())!;
                    break;
                case "stopLibraryAnalysis":
                    _libraryAnalysis.Stop();
                    result = CamelCase(_libraryAnalysis.GetStatus())!;
                    break;
                case "clearSongAnalysisCache":
                    result = new { success = true, removed = _analysis.ClearAll() };
                    break;
                case "getPlayHistory":
                    result = GetPlayHistory(msg);
                    break;
                case "removePlayHistoryEntry":
                    result = RemovePlayHistoryEntry(msg);
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

                // ── Played Songs Logging ──────────────────
                case "getPlayedSongs":
                    result = await GetPlayedSongs();
                    break;
                case "clearPlayedSongs":
                    result = await ClearPlayedSongs();
                    break;

                // ── Playlists ─────────────────────────────
                case "getPlaylists":
                    result = GetPlaylists();
                    break;
                case "getHomeFeed":
                    result = GetHomeFeed(msg);
                    break;
                case "getThumbnails":
                    result = GetThumbnails(msg);
                    break;
                case "getMusicAudioDevices":
                    result = GetMusicAudioDevices();
                    break;
                case "setMusicOutputDevice":
                    result = SetMusicOutputDevice(msg);
                    break;
                case "getPlaylist":
                    result = GetPlaylist(msg);
                    break;
                case "createPlaylist":
                    result = CreatePlaylist(msg);
                    break;
                case "addSongToPlaylist":
                    result = AddSongToPlaylist(msg);
                    break;
                case "removePlaylistEntry":
                    result = RemovePlaylistEntry(msg);
                    break;
                case "reorderPlaylistEntry":
                    result = ReorderPlaylistEntry(msg);
                    break;
                case "deletePlaylist":
                    result = DeletePlaylist(msg);
                    break;
                case "updatePlaylist":
                    result = UpdatePlaylist(msg);
                    break;
                case "playPlaylist":
                    result = PlayPlaylist(msg);
                    break;
                case "queuePlaylist":
                    result = QueuePlaylist(msg);
                    break;
                case "shufflePlaylist":
                    result = ShufflePlaylist(msg);
                    break;
                case "clearPlaylistQueue":
                    result = ClearPlaylistQueue(msg);
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
                case "musicShareSetVolume":
                    result = MusicShareSetVolume(msg);
                    break;

                // ── Announcement ──────────────────────────
                case "announcementOpen":
                    result = OpenAnnouncement();
                    break;
                case "announcementClose":
                    result = CloseAnnouncement();
                    break;
                case "announcementGetState":
                    result = EnsureAnnouncement().GetState();
                    break;
                case "announcementStart":
                    result = await AnnouncementStartAsync();
                    break;
                case "announcementStop":
                    result = await AnnouncementStopAsync();
                    break;
                case "announcementPress":
                    result = await AnnouncementPressAsync();
                    break;
                case "announcementRelease":
                    result = await AnnouncementReleaseAsync();
                    break;
                case "announcementSetDimDb":
                    result = AnnouncementSetDimDb(msg);
                    break;
                case "announcementSetPlaySound":
                    result = AnnouncementSetPlaySound(msg);
                    break;
                case "announcementSetPushToTalk":
                    result = AnnouncementSetPushToTalk(msg);
                    break;

                // ── Soundboard ────────────────────────────
                case "soundboardGetState":
                    result = EnsureSoundboard().BuildState();
                    break;
                case "soundboardPlay":
                    result = EnsureSoundboard().PlayButton(msg["row"]?.ToObject<int>() ?? -1, msg["col"]?.ToObject<int>() ?? -1);
                    break;
                case "soundboardStopAll":
                    result = EnsureSoundboard().StopAll();
                    break;
                case "soundboardSetMasterVolume":
                    result = EnsureSoundboard().SetMasterVolume(msg["value"]?.ToObject<double>() ?? 1.0);
                    break;
                case "soundboardSetOutputDevice":
                    result = EnsureSoundboard().SetOutputDevice(msg["deviceNumber"]?.ToObject<int>() ?? -1);
                    break;
                case "soundboardSetPage":
                    result = EnsureSoundboard().SetCurrentPage(msg["index"]?.ToObject<int>() ?? 0);
                    break;
                case "soundboardStepPage":
                    result = EnsureSoundboard().StepPage(msg["delta"]?.ToObject<int>() ?? 0);
                    break;
                case "soundboardAddPage":
                    result = EnsureSoundboard().AddPage(msg["name"]?.ToString() ?? "Untitled Page",
                        msg["columns"]?.ToObject<int>() ?? 4, msg["rows"]?.ToObject<int>() ?? 3);
                    break;
                case "soundboardDeletePage":
                    result = EnsureSoundboard().DeletePage(msg["index"]?.ToObject<int>() ?? -1);
                    break;
                case "soundboardRenamePage":
                    result = EnsureSoundboard().RenamePage(msg["index"]?.ToObject<int>() ?? -1, msg["name"]?.ToString() ?? "");
                    break;
                case "soundboardMovePage":
                    result = EnsureSoundboard().MovePage(msg["index"]?.ToObject<int>() ?? -1, msg["delta"]?.ToObject<int>() ?? 0);
                    break;
                case "soundboardSetGridSize":
                    result = EnsureSoundboard().SetGridSize(msg["columns"]?.ToObject<int>() ?? 4, msg["rows"]?.ToObject<int>() ?? 3);
                    break;
                case "soundboardBrowseFile":
                {
                    // Async: the picker is asynchronous on mobile.
                    var browseResult = await BrowseSoundboardFileAsync(
                        msg["row"]?.ToObject<int>() ?? -1, msg["col"]?.ToObject<int>() ?? -1);
                    SendResponse(id, browseResult);
                    return;
                }
                case "soundboardUpdateButton":
                    result = UpdateSoundboardButton(msg);
                    break;
                case "soundboardDeleteButton":
                    result = EnsureSoundboard().DeleteButton(msg["row"]?.ToObject<int>() ?? -1, msg["col"]?.ToObject<int>() ?? -1);
                    break;
                case "soundboardClearMidi":
                    result = EnsureSoundboard().ClearMidiMapping(msg["row"]?.ToObject<int>() ?? -1, msg["col"]?.ToObject<int>() ?? -1);
                    break;
                case "soundboardSetMidiEnabled":
                    result = EnsureSoundboard().SetMidiEnabled(msg["enabled"]?.ToObject<bool>() ?? false);
                    break;
                case "soundboardSetMidiDevices":
                    result = EnsureSoundboard().SetMidiDevices(
                        msg["inputDevice"]?.ToObject<int>() ?? -1, msg["outputDevice"]?.ToObject<int>() ?? -1);
                    break;
                case "soundboardSetAssignMode":
                    result = EnsureSoundboard().SetAssignMode(msg["enabled"]?.ToObject<bool>() ?? false);
                    break;
                case "soundboardStartMidiAssign":
                    result = EnsureSoundboard().StartMidiAssign();
                    break;
                case "soundboardSetButtonMidi":
                    result = SetSoundboardButtonMidi(msg);
                    break;
                case "soundboardSetEmptyFeedback":
                    result = EnsureSoundboard().SetEmptyFeedbackVelocity(msg["velocity"]?.ToObject<int>() ?? 10);
                    break;
                case "soundboardTestMidiFeedback":
                    result = EnsureSoundboard().TestMidiFeedback();
                    break;
                case "soundboardSaveKeyboardShortcuts":
                    result = EnsureSoundboard().SaveKeyboardShortcuts(msg["shortcuts"] ?? new JObject());
                    break;
                case "soundboardImportYoutube":
                    result = await ImportYoutubeToSoundboardAsync(msg);
                    break;
                case "soundboardClose":
                    _ytForm.NewUiRef?.CloseSoundboardWindow();
                    result = new { success = true };
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

        // API/MCP commands are awaited outside the web-UI message bus. Completing
        // the pending task here also covers responses produced later from a
        // PostToUi callback (mobile), where no AsyncLocal response target exists.
        if (_commandResponses.TryRemove(id, out var completion))
            completion.TrySetResult(result == null ? "null" : JToken.FromObject(result).ToString(Formatting.None));

        (target ?? _responseTarget.Value ?? SendMessage)?.Invoke(json);
    }

    public void SendEvent(string eventName, object? data)
    {
        var json = JsonConvert.SerializeObject(new { type = "event", eventName, data });
        SendMessage?.Invoke(json);

        // Library import/scan can be started from the music page too; the main
        // window is not resident in that mode, so send progress to the player as
        // well. Harmless on desktop (its player shows the same state).
        if (eventName is "libraryScanProgress" or "libraryScanCompleted")
            MusicPlayerSendEvent(eventName, data);

        try { EventRaised?.Invoke(eventName, data); } catch { }
    }

    private void MusicPlayerSendEvent(string eventName, object? data)
    {
        try
        {
            var json = JsonConvert.SerializeObject(new { type = "event", eventName, data });
            _ytForm.NewUiRef?.MusicPlayerSendMessage?.Invoke(json);
        }
        catch { }

        // Advanced-audio events are part of the documented WebSocket event list;
        // forward them to API/MCP clients as well as the player page.
        if (eventName is "sleepTimerUpdate" or "equalizerUpdate" or "songAnalysisProgress" or "songAnalysisUpdate")
        {
            try { EventRaised?.Invoke(eventName, data); } catch { }
        }
    }

    private async Task<object> FetchDataAsync()
    {
        var cfg = ConfigService.Instance.Current;
        if (cfg == null) return new { error = "No config" };

        var baseUrl = (cfg.Address ?? "http://127.0.0.1:5000") + "/fetch?method=";
        string dbJson, blacklistJson;

        using (var client = BackendHttpClient.Create(cfg, TimeSpan.FromSeconds(45)))
        {
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
        var knownYtids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newEntries = new List<(string ytid, DateTime? timestampUtc, string message, bool isApproved)>();
        foreach (var entry in db)
        {
            var arr = entry as JArray;
            if (arr == null || arr.Count < 1) continue;
            var ytid = arr[0]?.ToString() ?? "";
            if (string.IsNullOrEmpty(ytid)) continue;
            knownYtids.Add(ytid);

            var message = arr.Count > 1 ? arr[1]?.ToString() ?? "" : "";
            var isApproved = arr.Count > 2 ? arr[2]?.ToObject<bool>() ?? false : false;
            var timestamp = arr.Count > 3 ? arr[3]?.ToObject<double?>() : null;

            DateTime? timestampUtc = null;
            if (timestamp.HasValue)
                timestampUtc = DateTimeOffset.FromUnixTimeSeconds((long)timestamp.Value).UtcDateTime;

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

            // Track entries that the WPF FetchData hasn't processed yet
            if (!_ytForm.fetchedYtids.Contains(ytid))
            {
                newEntries.Add((ytid, timestampUtc, message, isApproved));
            }
        }

        // Process new entries on the WPF dispatcher (downloads, panels, etc.)
        foreach (var (ytid, timestampUtc, message, isApproved) in newEntries)
        {
            var videoUrl = $"https://www.youtube.com/watch?v={ytid}";
            PostToUi(async () =>
            {
                try
                {
                    await _ytForm.AddVideoPanelAsync(videoUrl, timestampUtc, message, isApproved);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FetchDataAsync] Error processing new entry {ytid}: {ex.Message}");
                }
            });
        }

        // Keep fetchedYtids in sync so the library delete guard works
        _ytForm.fetchedYtids = knownYtids;

        // Mark that the initial load finished so downloads auto-enqueue under Autopilot.
        _ytForm.InitialLoadComplete = true;

        // Server returns array of strings; enrich with cached metadata. Blacklist
        // entries have no download/panel that would populate the cache, so fetch
        // their metadata here once (otherwise the UI only had the artwork and an
        // id where the title/creator should be).
        var blacklistArr = JArray.Parse(blacklistJson);
        await EnrichBlacklistMetadataAsync(blacklistArr).ConfigureAwait(false);
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

        if (newEntries.Count > 0)
        {
            var newRequests = newEntries
                .Select(e => new NewRequestInfo(e.ytid, e.timestampUtc, e.message, e.isApproved))
                .ToArray();
            try { NewRequestsDetected?.Invoke(newRequests); } catch { }
        }

        try { RequestsUpdated?.Invoke(); } catch { }

        return new { database = databaseList, blacklist = blacklistItems };
    }

    /// <summary>
    /// Fetches title/creator/duration for blacklisted ids that do not have cached
    /// metadata yet. Limited concurrency keeps a large blacklist from flooding
    /// YouTube or delaying the first list render too much.
    /// </summary>
    private async Task EnrichBlacklistMetadataAsync(JArray blacklist)
    {
        var missing = new List<string>();
        foreach (var item in blacklist)
        {
            var ytid = item.ToString();
            if (string.IsNullOrWhiteSpace(ytid)) continue;

            var cached = _ytForm.GetOrCreateVideoData(ytid);
            if (cached.MetadataFetched || !string.IsNullOrWhiteSpace(cached.Title)) continue;
            missing.Add(ytid);
        }

        if (missing.Count == 0) return;

        // Bound the work per poll; the next refresh continues with the rest.
        missing = missing.Take(30).ToList();

        using var gate = new SemaphoreSlim(4, 4);
        var tasks = missing.Select(async ytid =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var data = _ytForm.GetOrCreateVideoData(ytid);
                if (data.MetadataFetched || !string.IsNullOrWhiteSpace(data.Title)) return;
                try
                {
                    var (title, length, creator) = await _ytService
                        .GetVideoMetadataAsync($"https://www.youtube.com/watch?v={ytid}")
                        .ConfigureAwait(false);
                    data.Title = title ?? string.Empty;
                    data.Creator = creator ?? string.Empty;
                    data.DurationTicks = length.Ticks;
                }
                catch
                {
                    // Unavailable/private/blocked: still mark it so we do not retry
                    // on every poll; the row keeps its id as the title.
                }
                finally
                {
                    data.MetadataFetched = true;
                }
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
            _ytForm.SaveVideoDataCache();
        }
        catch { }
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

        var cachedData = _ytForm.GetOrCreateVideoData(videoId);

        // Lyrics were looked up before (found or not): answer from the cache. The
        // lyrics panel used to hit lyrics.ovh — and fall back to a full YouTube
        // description fetch — every time it was opened.
        if (cachedData.LyricsFetched)
        {
            return new
            {
                lyrics = cachedData.LyricsText ?? string.Empty,
                title = cachedData.Title ?? string.Empty,
                artist = cachedData.Creator ?? string.Empty,
                cached = true
            };
        }

        try
        {
            var title = _ytForm.GetCachedTitle(videoId) ?? "";
            var artist = _ytForm.GetCachedCreator(videoId) ?? "";
            var lyrics = await _ytService.GetLyricsAsync(artist, title, videoId).ConfigureAwait(false);

            cachedData.LyricsText = lyrics ?? string.Empty;
            cachedData.LyricsFetched = true;
            _ytForm.SaveVideoDataCache();

            return new { lyrics = lyrics ?? "", title, artist };
        }
        catch (Exception ex)
        {
            try
            {
                var subtitles = await _ytService.TryGetSubtitlesAsync(videoId).ConfigureAwait(false);
                var subtitleText = subtitles?.PlainText ?? "";

                cachedData.LyricsText = subtitleText;
                cachedData.LyricsFetched = true;
                cachedData.SubtitleText = subtitleText;
                if (!string.IsNullOrWhiteSpace(subtitles?.TimedLrcText))
                    cachedData.SubtitleTimedText = subtitles!.TimedLrcText!;
                cachedData.SubtitlesFetched = true;
                _ytForm.SaveVideoDataCache();

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
            var bytes = await GetOrDownloadThumbnailBytesAsync(videoId).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return new { error = "No thumbnail" };
            return new { mime = "image/jpeg", data = Convert.ToBase64String(bytes) };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    /// <summary>In-flight thumbnail downloads, so duplicate cards share one request.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<byte[]?>> ThumbnailFetches = new();

    /// <summary>
    /// Thumbnail bytes from the on-disk cache, downloaded (and cached) only when
    /// missing. Shared by the lists, the download pipeline and the library import,
    /// so one video's artwork is fetched exactly once. The previous code used three
    /// different folders/spellings and re-downloaded the image in each stage.
    /// </summary>
    private async Task<byte[]?> GetOrDownloadThumbnailBytesAsync(string videoId)
    {
        var directory = AppPaths.ThumbnailCachePath;

        foreach (var name in new[] { videoId + ".jpg", videoId + ".png" })
        {
            try
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                var cached = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                if (cached.Length > 0) return cached;
            }
            catch
            {
                // Unreadable cache entry: fall through to a fresh download.
            }
        }

        var bytes = await ThumbnailFetches
            .GetOrAdd(videoId, id => _ytService.DownloadThumbnailBytesAsync(id))
            .ConfigureAwait(false);
        ThumbnailFetches.TryRemove(videoId, out _);

        if (bytes == null || bytes.Length == 0) return null;

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, videoId + ".jpg"), bytes).ConfigureAwait(false);
        }
        catch
        {
            // A read-only folder still lets the caller use the bytes for this pass.
        }

        return bytes;
    }

    private async Task<object> DownloadVideoAsync(string? videoUrl)
    {
        if (string.IsNullOrEmpty(videoUrl)) return new { error = "No URL" };
        try
        {
            var downloadPath = AppPaths.DownloadPath;
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

    private object IsFileDownloaded(string? videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return new { downloaded = false };
        var mp3Path = DownloadedAudio.PathFor(videoId);
        return new { downloaded = DownloadedAudio.IsComplete(mp3Path) };
    }

    private async Task<object> DownloadAndProcessSongAsync(string? videoId, string? action)
    {
        if (string.IsNullOrEmpty(videoId)) return new { error = "No videoId" };
        var videoUrl = $"https://www.youtube.com/watch?v={videoId}";
        var downloadPath = AppPaths.DownloadPath;
        var mp3Path = DownloadedAudio.PathFor(videoId, downloadPath);
        var actionToTake = action ?? "queue";

        void SendProgress(string stage, double progress, string? message = null)
        {
            SendEvent("downloadProgress", new { videoId, stage, progress, message });
        }

        if (!_ytForm.ProcessingVideos.TryAdd(videoId, 0))
        {
            return new { success = true, message = "Already being processed by another path" };
        }

        await _ytForm.DownloadSemaphore.WaitAsync();
        try
        {
            // Re-check after waiting for the semaphore: another path (the fetch
            // loop, a second click) may have finished this video in the meantime,
            // and the path computed before the wait could belong to another
            // container than the file that now exists.
            mp3Path = DownloadedAudio.PathFor(videoId, downloadPath);

            var cachedData = _ytForm.GetOrCreateVideoData(videoId);

            // Stage 1: Metadata — reuse the cache. Re-processing a request (a poll
            // after a download, the user pressing Queue on an existing song) used to
            // cost a full YouTube metadata request every time.
            string title;
            string creator;
            TimeSpan length;
            if (!string.IsNullOrWhiteSpace(cachedData.Title) && cachedData.DurationTicks > 0)
            {
                title = cachedData.Title;
                creator = cachedData.Creator ?? string.Empty;
                length = TimeSpan.FromTicks(cachedData.DurationTicks);
                SendProgress("metadata", 100, title);
            }
            else
            {
                SendProgress("metadata", 0, "Fetching metadata...");
                var metadata = await _ytService.GetVideoMetadataAsync(videoUrl).ConfigureAwait(false);
                title = metadata.Title ?? string.Empty;
                creator = metadata.Creator ?? string.Empty;
                length = metadata.Length;

                cachedData.Title = title;
                cachedData.Creator = creator;
                cachedData.DurationTicks = length.Ticks;
                _ytForm.SaveVideoDataCache();

                SendProgress("metadata", 100, title.Length > 0 ? title : videoId);
            }

            if (DownloadedAudio.IsComplete(mp3Path))
            {
                // Already downloaded — skip to post-processing
                SendProgress("downloading", 100, "Already downloaded");
            }
            else
            {
                // Stage 2: Download
                SendProgress("downloading", 0, "Downloading...");
                DownloadActivityChanged?.Invoke(true, title ?? videoId);
                try
                {
                    var progress = new Progress<double>(pct =>
                    {
                        DownloadProgressChanged?.Invoke(pct);
                        SendProgress("downloading", pct * 100, $"Downloading... {pct:P0}");
                    });
                    var result = await _ytService.DownloadVideoWithProgressAsync(videoUrl, downloadPath, progress).ConfigureAwait(false);
                    SendProgress("downloading", 100, "Download complete");
                }
                finally
                {
                    DownloadActivityChanged?.Invoke(false, title ?? videoId);
                }
            }

            // Stage 3: Thumbnail — read it from the cache when it is already there.
            // The old code downloaded the image (and its URL, via a full metadata
            // request) on every pass, and did it a second time in stage 5.
            SendProgress("thumbnail", 0, "Fetching thumbnail...");
            byte[]? thumbnailBytes = null;
            try
            {
                thumbnailBytes = await GetOrDownloadThumbnailBytesAsync(videoId).ConfigureAwait(false);
                SendProgress("thumbnail", 100,
                    thumbnailBytes != null && thumbnailBytes.Length > 0 ? "Thumbnail cached" : "Thumbnail unavailable");
            }
            catch
            {
                SendProgress("thumbnail", 100, "Thumbnail skipped");
            }

            // Stage 4: Subtitles — cached results (including "this video has no
            // captions") are reused instead of being looked up again.
            SendProgress("subtitles", 0, "Caching subtitles...");
            if (cachedData.SubtitlesFetched)
            {
                SendProgress("subtitles", 100, "Subtitles cached");
            }
            else
            {
                try
                {
                    var subtitleResult = await _ytService.TryGetSubtitlesAsync(videoUrl).ConfigureAwait(false);
                    cachedData.SubtitleText = subtitleResult?.PlainText ?? string.Empty;
                    cachedData.SubtitleTimedText = subtitleResult?.TimedLrcText ?? string.Empty;
                    cachedData.SubtitlesFetched = true;
                    _ytForm.SaveVideoDataCache();
                    SendProgress("subtitles", 100, "Subtitles cached");
                }
                catch
                {
                    SendProgress("subtitles", 100, "Subtitles skipped");
                }
            }

            // Stage 5: Auto-add to library
            if (ConfigService.Instance.Current?.LibraryAutoAddDownloads == true
                && File.Exists(mp3Path))
            {
                var libSong = _libraryService.ImportDownloadedSong(mp3Path, videoId, videoUrl, title, creator);
                if (libSong != null)
                {
                    try
                    {
                        // Reuse the bytes cached in stage 3: the old code asked
                        // YouTube for the URL again and downloaded the image a
                        // second time.
                        if (thumbnailBytes != null && thumbnailBytes.Length > 0)
                        {
                            var thumbPath = _libraryService.SaveSongThumbnail(libSong.Id, thumbnailBytes);
                            libSong.ThumbnailPath = thumbPath;
                        }
                    }
                    catch { }
                    _libraryService.Save();
                }
            }

            // Stage 6: Auto-enqueue / auto-play-next / play now
            if (File.Exists(mp3Path))
            {
                var song = new Song(title ?? videoId, creator ?? "", null, length, mp3Path);
                if (actionToTake == "play")
                {
                    PostToUi(() =>
                    {
                        var mp = _ytForm.Music;
                        if (mp == null) return;
                        mp.ClearQueue();
                        mp.Queue.Add(song);
                        mp.PlaySong(song);
                    });
                }
                else if (_ytForm.InitialLoadComplete && ConfigService.Instance.Current?.AutoEnqueue == true)
                {
                    PostToUi(() =>
                    {
                        if (actionToTake == "playNext")
                            _ytForm.Music.AddNextSongExternal(song);
                        else
                            _ytForm.Music.AddSongExternal(song);
                    });
                }
            }

            // Done
            SendProgress("complete", 100, title ?? videoId);

            return new { success = true, title = title ?? videoId, creator, length = length.ToString(), path = mp3Path };
        }
        catch (Exception ex)
        {
            SendProgress("error", 0, ex.Message);
            return new { error = ex.Message };
        }
        finally
        {
            _ytForm.ProcessingVideos.TryRemove(videoId, out _);
            _ytForm.DownloadSemaphore.Release();
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
            using var httpClient = BackendHttpClient.Create(cfg);
            var url = $"{cfg.Address}/fetch?method=add";
            var request = new HttpRequestMessage(HttpMethod.Post, url);
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
        SubscribeToMusicPlayer();
        _ytForm.NewUiRef?.ShowMusicPlayerWindow();
    }

    private void CloseNewMusicPlayer()
    {
        _ytForm.NewUiRef?.CloseMusicPlayerWindow();
    }

    private void SubscribeToMusicPlayer()
    {
        if (_musicPlayerSubscribed) return;
        var mp = _ytForm.Music;
        if (mp == null) return;
        _musicPlayerSubscribed = true;
        mp.NowPlayingTick += OnNowPlayingTick;
        mp.QueueChanged += OnQueueChanged;
        mp.PlaylistQueueChanged += OnQueueChanged;
        mp.QueuesChanged += OnQueueChanged;
    }

    private void OnNowPlayingTick(object? sender, NowPlayingEventArgs args)
    {
        var mpSend = _ytForm.NewUiRef?.MusicPlayerSendMessage;
        if (mpSend == null) return;

        try
        {
            var data = new System.Collections.Generic.Dictionary<string, object>
            {
                ["currentTime"] = args.CurrentTime.TotalSeconds,
                ["totalTime"] = args.TotalTime.TotalSeconds,
                ["isPlaying"] = _ytForm.Music.IsPlaying,
                ["isPlaybackActive"] = _ytForm.Music.IsPlaybackActive
            };

            var current = args.Current;
            if (current != null && current.songPath != _currentSongPath)
            {
                _currentSongPath = current.songPath;
                CurrentLyricsProvider = string.Empty;
                CurrentLyricsAvailable = false;
                data["title"] = current.Title ?? "";
                data["artist"] = current.Artist ?? "";
                data["duration"] = current.length ?? "";

                var thumb = GetThumbnailForSongPath(current.songPath);
                if (thumb != null)
                    data["thumbnail"] = thumb;

                // Library-enriched audio quality metadata
                LibrarySong? currentLibSong = null;
                if (!string.IsNullOrEmpty(current.songPath))
                {
                    currentLibSong = _libraryService.Data.Songs.FirstOrDefault(s => string.Equals(s.FilePath, current.songPath, StringComparison.OrdinalIgnoreCase));
                    if (currentLibSong != null)
                    {
                        var ext = System.IO.Path.GetExtension(current.songPath).TrimStart('.').ToUpperInvariant();
                        data["fileType"] = ext;
                        if (currentLibSong.BitrateKbps > 0) data["bitrateKbps"] = currentLibSong.BitrateKbps;

                        // Record play history
                        _libraryService.RecordPlay(currentLibSong.Id);
                    }
                }

                // Log playback to server if enabled
                var platform = currentLibSong != null && currentLibSong.Source == SongSource.Downloaded ? "youtube" : "spotify";
                LogPlaybackAsync(current.Title ?? "", current.Artist ?? "", platform);

                // Show cached BPM/parts immediately, or start a background analysis
                // when the user enabled auto-analysis. The result is pushed as
                // songAnalysisUpdate and drives the player progress chapters.
                SendOrStartSongAnalysis(current);
            }

            if (current == null)
                _currentSongPath = null;

            data["currentSongPath"] = _currentSongPath ?? string.Empty;

            var json = JsonConvert.SerializeObject(new
            {
                type = "event",
                eventName = "nowPlayingUpdate",
                data
            });
            mpSend(json);

            // Forward to presentation window if source is "player"
            var source = ConfigService.Instance.Current?.PresentationSource ?? "player";
            if (source == "player")
            {
                var presData = new Dictionary<string, object>(data);
                presData.Remove("isPlaybackActive");
                presData.Remove("currentSongPath");
                PresentationSendEvent("presentationUpdate", presData);
            }

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
            var mp = _ytForm.Music;
            if (mp == null) return;

            Song? current = null;
            RunOnUi(() => { current = mp.Queue?.FirstOrDefault(); });

            if (current == null) return;

            var data = new System.Collections.Generic.Dictionary<string, object>
            {
                ["title"] = current.Title ?? "",
                ["artist"] = current.Artist ?? "",
                ["duration"] = current.length ?? "",
                ["isPlaying"] = mp.IsPlaying,
                ["isPlaybackActive"] = mp.IsPlaybackActive
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
        SendPresentationQueueUpdate();
        _ = PrefetchNextLyricsAsync();
    }

    private void SendQueueUpdate()
    {
        var mpSend = _ytForm.NewUiRef?.MusicPlayerSendMessage;
        if (mpSend == null) return;

        try
        {
            var mp = _ytForm.Music;
            if (mp == null) return;

            // Build a path→library lookup for enriching queue items
            var libByPath = BuildLibraryLookupByPath();

            bool anyThumbnailMissing = false;

            var queueData = mp.Queue.Select((song, i) =>
            {
                LibrarySong? lib = null;
                if (!string.IsNullOrEmpty(song.songPath))
                    libByPath.TryGetValue(song.songPath, out lib);

                var thumb = GetThumbnailForSongPath(song.songPath, lib);
                if (thumb == null) anyThumbnailMissing = true;
                else RememberQueueThumbnail(song.songPath, thumb);

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

            var playlistQueueData = mp.PlaylistQueue.Select((song, i) =>
            {
                LibrarySong? lib = null;
                if (!string.IsNullOrEmpty(song.songPath))
                    libByPath.TryGetValue(song.songPath, out lib);

                var thumb = GetThumbnailForSongPath(song.songPath, lib);
                if (thumb == null) anyThumbnailMissing = true;
                else RememberQueueThumbnail(song.songPath, thumb);

                var ext = !string.IsNullOrEmpty(song.songPath)
                    ? System.IO.Path.GetExtension(song.songPath).TrimStart('.').ToUpperInvariant()
                    : null;

                return new
                {
                    title = song.Title ?? "",
                    artist = song.Artist ?? "",
                    duration = song.length ?? "",
                    songPath = song.songPath ?? "",
                    index = i,
                    thumbnail = thumb,
                    isMissing = lib?.IsMissing ?? false,
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
                    playlistQueue = playlistQueueData,
                    currentSongPath = _currentSongPath,
                    isPlaying = mp.IsPlaybackActive,
                    repeatEnabled = mp.RepeatEnabled
                }
            });
            mpSend(json);

            // A single batched queue update no longer repeats while artwork is still
            // being downscaled in the background; kick off a targeted retry for the
            // entries that had no thumbnail yet.
            _queueThumbnailRefreshes = 0;
            if (anyThumbnailMissing) ScheduleQueueThumbnailRefresh();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SendQueueUpdate] {ex.Message}");
        }
    }

    private Dictionary<string, LibrarySong> BuildLibraryLookupByPath()
    {
        var byPath = new Dictionary<string, LibrarySong>(StringComparer.OrdinalIgnoreCase);
        try
        {
            lock (_libraryService.Data)
            {
                foreach (var song in _libraryService.Data.Songs)
                    if (!string.IsNullOrEmpty(song.FilePath))
                        byPath[song.FilePath] = song;
            }
        }
        catch { }
        return byPath;
    }

    private void RememberQueueThumbnail(string? songPath, string thumbnail)
    {
        if (string.IsNullOrEmpty(songPath)) return;
        lock (_queueThumbnailSync) _queueThumbnailsSent[songPath] = thumbnail;
    }

    /// <summary>Repeats a small thumbnail-only update while the cache is still filling.</summary>
    private void ScheduleQueueThumbnailRefresh()
    {
        if (_queueThumbnailRefreshes >= MaxQueueThumbnailRefreshes) return;
        if (Interlocked.CompareExchange(ref _queueThumbnailRefreshScheduled, 1, 0) != 0) return;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await System.Threading.Tasks.Task.Delay(800).ConfigureAwait(false); }
            catch { }

            void Run()
            {
                Interlocked.Exchange(ref _queueThumbnailRefreshScheduled, 0);
                SendQueueThumbnailUpdate();
            }

            // Reading the queue collections must happen on the shell's UI thread.
            try
            {
                var shell = _ytForm.NewUiRef;
                if (shell != null) shell.PostToUi(Run);
                else _ytForm.PostToUi(Run);
            }
            catch { Interlocked.Exchange(ref _queueThumbnailRefreshScheduled, 0); }
        });
    }

    /// <summary>
    /// Sends only the queue artwork that became available since the last update
    /// (<c>queueThumbnails</c>). Keeps the page in sync without re-sending the whole
    /// queue for every image the background converter finishes.
    /// </summary>
    private void SendQueueThumbnailUpdate()
    {
        var mpSend = _ytForm.NewUiRef?.MusicPlayerSendMessage;
        var mp = _ytForm.Music;
        if (mpSend == null || mp == null)
        {
            _queueThumbnailRefreshes = 0;
            return;
        }

        _queueThumbnailRefreshes++;

        try
        {
            var libByPath = BuildLibraryLookupByPath();
            var items = new List<object>();
            bool anyMissing = false;

            void Add(Song song)
            {
                var path = song.songPath;
                if (string.IsNullOrEmpty(path)) return;

                lock (_queueThumbnailSync)
                    if (_queueThumbnailsSent.ContainsKey(path)) return;

                LibrarySong? lib = null;
                libByPath.TryGetValue(path, out lib);

                var thumb = GetThumbnailForSongPath(path, lib);
                if (thumb == null)
                {
                    anyMissing = true;
                    return;
                }

                lock (_queueThumbnailSync) _queueThumbnailsSent[path] = thumb;
                items.Add(new { songPath = path, thumbnail = thumb });
            }

            foreach (var song in mp.Queue) Add(song);
            foreach (var song in mp.PlaylistQueue) Add(song);

            if (items.Count > 0)
            {
                var json = JsonConvert.SerializeObject(new
                {
                    type = "event",
                    eventName = "queueThumbnails",
                    data = new { items }
                });
                mpSend(json);
            }

            if (anyMissing && Thumbnails.PendingCount > 0)
            {
                ScheduleQueueThumbnailRefresh();
            }
            else
            {
                _queueThumbnailRefreshes = 0;
            }
        }
        catch
        {
            _queueThumbnailRefreshes = 0;
        }
    }

    private void SendPresentationQueueUpdate()
    {
        try
        {
            var source = ConfigService.Instance.Current?.PresentationSource ?? "player";
            if (source != "player") return;

            var mp = _ytForm.Music;
            if (mp == null) return;

            var queue = mp.Queue;

            // Skip index 0 (now playing), take next 3
            var upcoming = new List<object>();
            if (queue != null)
            {
                for (int i = 1; i < queue.Count && upcoming.Count < 3; i++)
                {
                    var song = queue[i];
                    string? thumb = null;
                    if (!string.IsNullOrEmpty(song.songPath))
                        thumb = GetThumbnailForSongPath(song.songPath);

                    upcoming.Add(new
                    {
                        title = song.Title ?? "",
                        artist = song.Artist ?? "",
                        thumbnail = thumb,
                        estimatedStart = song.EstimatedStartDisplay ?? ""
                    });
                }
            }

            PresentationSendEvent("presentationQueueUpdate", new { queue = upcoming, currentIndex = 0 });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SendPresentationQueueUpdate] {ex.Message}");
        }
    }

    /// <summary>
    /// Artwork for a song path as a data URL. Public seam for the mobile stage
    /// server, which renders queue thumbnails outside the web UI process.
    /// </summary>
    public string? GetThumbnailDataUrl(string? songPath) => GetThumbnailForSongPath(songPath);

    private string? GetThumbnailForSongPath(string? songPath)
    {
        LibrarySong? libSong = null;
        try
        {
            lock (_libraryService.Data)
            {
                libSong = _libraryService.Data.Songs.FirstOrDefault(s =>
                    string.Equals(s.FilePath, songPath, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { /* fall through to the other sources */ }

        return GetThumbnailForSongPath(songPath, libSong);
    }

    /// <summary>
    /// Artwork resolver for callers that already have the library entry. Queue updates
    /// used to call the single-argument overload per item, scanning the whole library
    /// again for every queue entry.
    /// </summary>
    private string? GetThumbnailForSongPath(string? songPath, LibrarySong? libSong)
    {
        try
        {
            // 1. Try library thumbnail (saved during download/import)
            if (libSong != null && !string.IsNullOrEmpty(libSong.ThumbnailPath) && File.Exists(libSong.ThumbnailPath))
            {
                // Downscaled + cached: the raw artwork is often ~1 MB and inlining it
                // made every queue/home update push tens of megabytes to the webview.
                return Thumbnails.GetDataUrl(libSong.ThumbnailPath);
            }

            // 2. Try embedded album art (local files and YouTube downloads with embedded art)
            try
            {
                using var tfile = global::TagLib.File.Create(songPath);
                var pic = tfile.Tag.Pictures?.FirstOrDefault();
                if (pic != null && pic.Data != null && pic.Data.Data != null && pic.Data.Data.Length > 0)
                {
                    return Thumbnails.GetDataUrl(pic.Data.Data, songPath ?? "");
                }
            }
            catch { /* no embedded art, fall through */ }

            // 3. Try the shared YouTube thumbnail cache. This is the only artwork a
            // download of an audio-only stream has (no embedded cover), and the path
            // used to be hardcoded relative to the executable — which is wrong on
            // mobile, where the cache lives under the app's data root.
            var videoId = System.IO.Path.GetFileNameWithoutExtension(songPath);
            if (!string.IsNullOrEmpty(videoId))
            {
                var cacheDir = AppPaths.ThumbnailCachePath;

                foreach (var extension in new[] { ".jpg", ".png" })
                {
                    var cachePath = System.IO.Path.Combine(cacheDir, videoId + extension);
                    if (File.Exists(cachePath)) return Thumbnails.GetDataUrl(cachePath);
                }
            }
        }
        catch { }
        return null;
    }

    private void ShowSoundboard()
    {
        _ = EnsureSoundboard();
        _ytForm.NewUiRef?.ShowSoundboardWindow();
    }

    // ──────────────────────────────────────────────────
    //  Soundboard helpers
    // ──────────────────────────────────────────────────

    private SoundboardEngine EnsureSoundboard()
    {
        lock (_soundboardLock)
        {
            if (_soundboard == null)
            {
                _soundboard = new SoundboardEngine();
                _soundboard.PlaybackUpdated += data => SoundboardSendEvent("soundboardPlayback", data);
                _soundboard.MidiCaptured += data => SoundboardSendEvent("soundboardMidiCaptured", data);
                _soundboard.MidiError += message => SoundboardSendEvent("soundboardMidiError", new { message });
            }
            return _soundboard;
        }
    }

    /// <summary>Stops playback and releases MIDI when the soundboard window closes.</summary>
    public void OnSoundboardClosed()
    {
        SoundboardEngine? engine;
        lock (_soundboardLock)
        {
            engine = _soundboard;
            _soundboard = null;
        }
        engine?.Dispose();
    }

    private void SoundboardSendEvent(string eventName, object? data)
    {
        try
        {
            var json = JsonConvert.SerializeObject(new { type = "event", eventName, data });
            _ytForm.NewUiRef?.SoundboardSendMessage?.Invoke(json);
        }
        catch { }
        try { EventRaised?.Invoke(eventName, data); } catch { }
    }

    // ──────────────────────────────────────────────────
    //  Visualization helpers
    // ──────────────────────────────────────────────────

    private void ShowVisualization()
    {
        // The streamer is started by the window once its native handle exists
        // (see NewUiWindow.WindowCreated); pushing frames into a half-created
        // WebView2 window crashes on Windows.
        _ytForm.NewUiRef?.ShowVisualizationWindow();
    }

    /// <summary>Starts the 30 fps audio feed once the visualization window is ready.</summary>
    public void StartVisualizationStreaming() => EnsureVisualization();

    private VisualizationStreamer? EnsureVisualization()
    {
        lock (_vizLock)
        {
            var mp = _ytForm.Music;
            if (mp == null) return null;
            if (_vizStreamer == null)
            {
                _vizStreamer = new VisualizationStreamer(_ytForm.Music);
                _vizStreamer.FrameReady += data => VisualizationSendEvent("audioSamples", data);
            }
            return _vizStreamer;
        }
    }

    /// <summary>Releases the audio stream subscription when the visualization window closes.</summary>
    public void OnVisualizationClosed()
    {
        VisualizationStreamer? streamer;
        lock (_vizLock)
        {
            streamer = _vizStreamer;
            _vizStreamer = null;
        }
        streamer?.Dispose();
    }

    private void VisualizationSendEvent(string eventName, object? data)
    {
        try
        {
            var json = JsonConvert.SerializeObject(new { type = "event", eventName, data });
            _ytForm.NewUiRef?.VisualizationSendMessage?.Invoke(json);
        }
        catch { }
        try { EventRaised?.Invoke(eventName, data); } catch { }
    }

    // ──────────────────────────────────────────────────
    //  Announcement helpers
    // ──────────────────────────────────────────────────

    private AnnouncementEngine EnsureAnnouncement()
    {
        lock (_announcementLock)
        {
            if (_announcement == null)
            {
                var mp = _ytForm.Music ?? throw new InvalidOperationException("Music player unavailable");
                var engine = new AnnouncementEngine(_ytForm.Music);
                engine.StateChanged += data => AnnouncementSendEvent("announcementState", data);

                // Route remote-control announcement actions to the web window.
                mp.RemoteAnnouncementHandler = async () =>
                {
                    EnsureAnnouncement();
                    _ytForm.NewUiRef?.ShowAnnouncementWindow();
                    await _announcement!.PressOrToggleAsync();
                };
                mp.RemoteAnnouncementReleaseHandler = async () =>
                {
                    if (_announcement != null) await _announcement.ReleaseAsync();
                };
                mp.RemoteAnnouncementActiveProvider = () => _announcement?.IsActive ?? false;
                mp.AnnouncementSettingsChanged = () => _announcement?.NotifySettingsChanged();

                _announcement = engine;
            }
            return _announcement;
        }
    }

    private object OpenAnnouncement()
    {
        var engine = EnsureAnnouncement();
        _ytForm.NewUiRef?.ShowAnnouncementWindow();
        return engine.GetState();
    }

    private object CloseAnnouncement()
    {
        _ytForm.NewUiRef?.CloseAnnouncementWindow();
        return new { success = true };
    }

    private async Task<object> AnnouncementStartAsync()
    {
        await EnsureAnnouncement().StartAsync().ConfigureAwait(false);
        return new { success = true };
    }

    private async Task<object> AnnouncementStopAsync()
    {
        await EnsureAnnouncement().StopAsync().ConfigureAwait(false);
        return new { success = true };
    }

    private async Task<object> AnnouncementPressAsync()
    {
        await EnsureAnnouncement().PressOrToggleAsync().ConfigureAwait(false);
        return new { success = true };
    }

    private async Task<object> AnnouncementReleaseAsync()
    {
        await EnsureAnnouncement().ReleaseAsync().ConfigureAwait(false);
        return new { success = true };
    }

    /// <summary>Restores the music if an announcement is in progress when the window closes.</summary>
    public void OnAnnouncementClosed()
    {
        var engine = _announcement;
        if (engine != null) _ = engine.OnClosedAsync();
    }

    private void AnnouncementSendEvent(string eventName, object? data)
    {
        try
        {
            var json = JsonConvert.SerializeObject(new { type = "event", eventName, data });
            _ytForm.NewUiRef?.AnnouncementSendMessage?.Invoke(json);
        }
        catch { }
        try { EventRaised?.Invoke(eventName, data); } catch { }
    }

    private object AnnouncementSetDimDb(JObject msg)
    {
        var value = msg["value"]?.ToObject<double>() ?? 20.0;
        ConfigService.Instance.Update(cfg => cfg.AnnouncementDimDb = Math.Clamp(value, 0.0, 50.0));
        _announcement?.NotifySettingsChanged();
        return new { success = true };
    }

    private object AnnouncementSetPlaySound(JObject msg)
    {
        var enabled = msg["enabled"]?.ToObject<bool>() ?? true;
        ConfigService.Instance.Update(cfg => cfg.AnnouncementPlaySound = enabled);
        _announcement?.NotifySettingsChanged();
        return new { success = true };
    }

    private object AnnouncementSetPushToTalk(JObject msg)
    {
        var enabled = msg["enabled"]?.ToObject<bool>() ?? true;
        ConfigService.Instance.Update(cfg => cfg.AnnouncementPushToTalk = enabled);
        _announcement?.NotifySettingsChanged();
        return new { success = true };
    }

    private async Task<object> BrowseSoundboardFileAsync(int row, int col)
    {
        try
        {
            string? chosen;
            if (PickSoundFile != null)
            {
                chosen = await PickSoundFile("Select Sound File").ConfigureAwait(false);
            }
            else
            {
                var files = PickFilesAsync != null
                    ? await PickFilesAsync("Select Sound File").ConfigureAwait(false)
                    : ShowOpenFileDialog?.Invoke("Select Sound File",
                        "*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac;*.wma", false);

                chosen = files != null && files.Length > 0 ? files[0] : null;
            }

            if (string.IsNullOrEmpty(chosen))
                return new { cancelled = true };

            // Mobile pickers return content:// URIs, which File.Exists cannot read.
            // Materialise the pick first (the host copies it to a real file); the
            // soundboard then copies that into its own folder.
            var source = chosen;
            if (!System.IO.File.Exists(source) && PrepareSoundFile != null)
                source = await PrepareSoundFile(chosen).ConfigureAwait(false);

            if (string.IsNullOrEmpty(source) || !System.IO.File.Exists(source))
                return new { error = "The selected file could not be read." };

            if (!SoundboardEngine.IsSupportedAudioFile(source))
                return new { error = "Unsupported file format." };

            var fileName = SoundboardEngine.CopyIntoSoundboardFolder(source);
            var duration = SoundboardEngine.ReadAudioDuration(
                Path.Combine(SoundboardConfiguration.GetSoundboardFolder(), fileName));

            if (!string.Equals(source, chosen, StringComparison.Ordinal))
            {
                try { System.IO.File.Delete(source); } catch { }
            }
            return new { success = true, fileName, duration, name = Path.GetFileNameWithoutExtension(fileName) };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object UpdateSoundboardButton(JObject msg)
    {
        var row = msg["row"]?.ToObject<int>() ?? -1;
        var col = msg["col"]?.ToObject<int>() ?? -1;
        int? customDevice = msg["customOutputDeviceNumber"] == null || msg["customOutputDeviceNumber"]!.Type == JTokenType.Null
            ? null
            : msg["customOutputDeviceNumber"]!.ToObject<int>();

        return EnsureSoundboard().UpdateButton(
            row, col,
            msg["name"]?.ToString() ?? "",
            msg["icon"]?.ToString() ?? "",
            msg["color"]?.ToString() ?? "",
            msg["soundFile"]?.ToString() ?? "",
            msg["fadeIn"]?.ToObject<bool>() ?? false,
            msg["fadeOut"]?.ToObject<bool>() ?? false,
            msg["repeatMode"]?.ToString() ?? "none",
            msg["volume"]?.ToObject<double>() ?? 1.0,
            customDevice,
            msg["shortcutEnabled"]?.ToObject<bool>() ?? false,
            msg["shortcutGesture"]?.ToString() ?? "");
    }

    private object SetSoundboardButtonMidi(JObject msg)
    {
        var row = msg["row"]?.ToObject<int>() ?? -1;
        var col = msg["col"]?.ToObject<int>() ?? -1;
        var mapping = msg["mapping"] as JObject;
        MidiMapping? result = null;
        if (mapping != null)
        {
            result = new MidiMapping
            {
                Channel = mapping["channel"]?.ToObject<int>() ?? 1,
                Note = mapping["note"]?.ToObject<int>() ?? 0,
                MessageType = mapping["messageType"]?.ToString() ?? "NoteOn",
                VelocityPressed = mapping["velocityPressed"]?.ToObject<int>() ?? 127,
                VelocityUnpressed = mapping["velocityUnpressed"]?.ToObject<int>() ?? 0
            };
        }
        return EnsureSoundboard().SetButtonMidi(row, col, result);
    }

    private async Task<object> ImportYoutubeToSoundboardAsync(JObject msg)
    {
        var row = msg["row"]?.ToObject<int>() ?? -1;
        var col = msg["col"]?.ToObject<int>() ?? -1;
        var url = msg["url"]?.ToString();
        if (string.IsNullOrWhiteSpace(url)) return new { error = "No URL" };

        try
        {
            SoundboardSendEvent("soundboardImportProgress", new { row, col, stage = "downloading", progress = 0 });
            var downloadPath = AppPaths.DownloadPath;
            var result = await _ytService.DownloadAndConvertVideoAsync(url, downloadPath).ConfigureAwait(false);
            if (string.IsNullOrEmpty(result) || !File.Exists(result))
                return new { error = "Download failed" };

            SoundboardSendEvent("soundboardImportProgress", new { row, col, stage = "importing", progress = 100 });
            var fileName = SoundboardEngine.CopyIntoSoundboardFolder(result);
            var duration = SoundboardEngine.ReadAudioDuration(
                Path.Combine(SoundboardConfiguration.GetSoundboardFolder(), fileName));
            SoundboardSendEvent("soundboardImportProgress", new { row, col, stage = "done", progress = 100 });
            return new { success = true, fileName, duration, name = Path.GetFileNameWithoutExtension(fileName) };
        }
        catch (Exception ex)
        {
            SoundboardSendEvent("soundboardImportProgress", new { row, col, stage = "error", progress = 0, message = ex.Message });
            return new { error = ex.Message };
        }
    }

    private Task<object> QueueSongAsync(string? videoId)
    {
        var ytid = NormalizeVideoId(videoId);
        if (string.IsNullOrEmpty(ytid)) return Task.FromResult<object>(new { error = "No videoId" });
        try
        {
            PostToUi(() => _ytForm.QueueSongById(ytid));
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
            PostToUi(() => _ytForm.PlayNextById(ytid));
            return Task.FromResult<object>(new { success = true });
        }
        catch (Exception ex)
        {
            return Task.FromResult<object>(new { error = ex.Message });
        }
    }

    // ──────────────────────────────────────────────────
    //  Advanced audio: song analysis commands
    // ──────────────────────────────────────────────────

    /// <summary>Resolves a request to an audio file (library id, path, or current track).</summary>
    private (string Path, string Title, string Artist)? ResolveAnalysisTarget(JObject msg)
    {
        string? path = msg["songPath"]?.ToString() ?? msg["path"]?.ToString() ?? msg["filePath"]?.ToString();
        string title = msg["title"]?.ToString() ?? "";
        string artist = msg["artist"]?.ToString() ?? "";

        var songId = msg["songId"]?.ToString() ?? msg["id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(songId))
        {
            var librarySong = _libraryService.Data.FindSong(songId);
            if (librarySong != null)
            {
                path = librarySong.FilePath;
                if (string.IsNullOrWhiteSpace(title)) title = librarySong.Title;
                if (string.IsNullOrWhiteSpace(artist)) artist = librarySong.Artist;
            }
        }

        // The API also exposes /api/v1/analysis/{key}; resolve that stable id
        // against the analysis cache when it is not a library song id.
        var analysisKey = msg["analysisKey"]?.ToString();
        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(analysisKey))
        {
            var cached = _analysis.Cached.FirstOrDefault(a =>
                string.Equals(SongAnalysisService.KeyFor(a.SongPath), analysisKey, StringComparison.OrdinalIgnoreCase));
            if (cached != null)
            {
                path = cached.SongPath;
                if (string.IsNullOrWhiteSpace(title)) title = cached.Title;
                if (string.IsNullOrWhiteSpace(artist)) artist = cached.Artist;
            }
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            var current = _ytForm.Music.Queue.FirstOrDefault();
            if (current != null)
            {
                path = current.songPath;
                if (string.IsNullOrWhiteSpace(title)) title = current.Title;
                if (string.IsNullOrWhiteSpace(artist)) artist = current.Artist;
            }
        }

        if (string.IsNullOrWhiteSpace(path)) return null;
        path = AppPaths.ResolvePortablePath(path);
        return (path, title, artist);
    }

    private SongAnalysis? GetSongAnalysis(JObject msg)
    {
        var target = ResolveAnalysisTarget(msg);
        if (target == null) return null;
        // Stale algorithm versions are deliberately treated as missing so the
        // auto/library analyzer regenerates them.
        return _analysis.GetFresh(target.Value.Path);
    }

    /// <summary>Chapters for a song when a current cached analysis exists, for lyric pages.</summary>
    private IReadOnlyList<SongPart>? ChaptersForSong(Song? song)
    {
        try
        {
            if (song == null || string.IsNullOrWhiteSpace(song.songPath)) return null;
            var path = AppPaths.ResolvePortablePath(song.songPath);
            var analysis = _analysis.GetFresh(path);
            return analysis?.Parts is { Count: > 0 } parts ? parts : null;
        }
        catch { return null; }
    }

    private SongAnalysis? AnalysisForSong(Song? song)
    {
        try
        {
            if (song == null || string.IsNullOrWhiteSpace(song.songPath)) return null;
            return _analysis.GetFresh(AppPaths.ResolvePortablePath(song.songPath));
        }
        catch { return null; }
    }

    private object? BuildKaraokePayload(Song? song, IReadOnlyList<(TimeSpan Time, string Text)> syncedLines)
    {
        if (syncedLines.Count == 0) return null;
        var analysis = AnalysisForSong(song);
        double duration = analysis?.DurationSeconds ?? (song?.Duration.TotalSeconds ?? 0);
        var karaoke = KaraokeLyrics.Build(syncedLines, analysis, duration);
        return new
        {
            lines = karaoke.Lines.Select(line => new
            {
                time = line.Time,
                end = line.End,
                text = line.Text,
                words = line.Words.Select(word => new
                {
                    start = word.Start,
                    end = word.End,
                    text = word.Text
                }).ToList()
            }).ToList(),
            pauses = karaoke.Pauses.Select(pause => new
            {
                start = pause.Start,
                end = pause.End,
                duration = pause.Duration
            }).ToList()
        };
    }

    /// <summary>
    /// Feeds non-caption timed lyrics into chapter detection. YouTube captions are
    /// deliberately excluded: they are often auto-generated and their line breaks
    /// do not reflect the song structure.
    /// </summary>
    private void RegisterTimedLyrics(Song song, LyricsResult result)
    {
        try
        {
            if (!result.Found || !result.HasSynced || result.Provider == LyricsProvider.YouTubeCaptions)
                return;
            if (string.IsNullOrWhiteSpace(song.songPath)) return;

            var lines = result.ParseSyncedLines()
                .Where(l => !string.IsNullOrWhiteSpace(l.Text))
                .Select(l => new TimedLyricLine(l.Time.TotalSeconds, l.Text))
                .ToList();
            if (lines.Count < 4) return;

            var path = AppPaths.ResolvePortablePath(song.songPath);
            _analysis.SetTimedLyrics(path, lines);
            _ = Task.Run(() => _analysis.RefineWithLyrics(path, lines));
        }
        catch { }
    }

    /// <summary>
    /// Pushes a cached analysis for the just-started track, or starts one in the
    /// background when the auto-analysis setting is on. Failures stay silent: a
    /// track that cannot be decoded simply has no chapters.
    /// </summary>
    private void SendOrStartSongAnalysis(Song current)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(current.songPath)) return;
            var path = AppPaths.ResolvePortablePath(current.songPath);
            if (!File.Exists(path))
            {
                MusicPlayerSendEvent("songAnalysisUnavailable", new { songPath = path });
                return;
            }

            var cached = _analysis.GetFresh(path);
            if (cached != null)
            {
                MusicPlayerSendEvent("songAnalysisUpdate", CamelCase(cached));
                return;
            }

            if (ConfigService.Instance.Current?.AutoAnalyzeSongs != true) return;
            _ = Task.Run(async () =>
            {
                // One retry covers the common mobile case where the platform
                // decoder is briefly unavailable while the track is starting.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        await _analysis.AnalyzeAsync(path, current.Title, current.Artist, force: attempt > 0)
                            .ConfigureAwait(false);
                        return;
                    }
                    catch
                    {
                        if (attempt == 0) await Task.Delay(1200).ConfigureAwait(false);
                    }
                }

                MusicPlayerSendEvent("songAnalysisUnavailable", new { songPath = path });
            });
        }
        catch { }
    }

    private async Task<object> AnalyzeSongAsync(JObject msg)
    {
        var target = ResolveAnalysisTarget(msg);
        if (target == null)
            return new { success = false, error = "No song to analyse. Pass songId/songPath or start playback first." };

        try
        {
            bool force = msg["force"]?.ToObject<bool>() ?? false;
            var analysis = await _analysis.AnalyzeAsync(
                target.Value.Path, target.Value.Title, target.Value.Artist, force).ConfigureAwait(false);

            return new
            {
                success = true,
                key = SongAnalysisService.KeyFor(analysis.SongPath),
                songPath = analysis.SongPath,
                title = analysis.Title,
                artist = analysis.Artist,
                bpm = analysis.Bpm,
                bpmConfidence = analysis.BpmConfidence,
                beatOffsetSeconds = analysis.BeatOffsetSeconds,
                loudnessLufs = analysis.IntegratedLoudnessLufs,
                loudnessRangeLu = analysis.LoudnessRangeLu,
                peakDb = analysis.PeakDb,
                dynamicRangeDb = analysis.DynamicRangeDb,
                durationSeconds = analysis.DurationSeconds,
                isLowConfidence = analysis.IsLowConfidence,
                quality = analysis.Quality,
                drops = analysis.Drops,
                parts = analysis.Parts,
                timeline = analysis.Timeline,
                analysisPath = analysis.AnalysisPath,
                analyzedUtc = analysis.AnalyzedUtc,
                algorithmVersion = analysis.AlgorithmVersion,
                lyricsUsedForParts = analysis.LyricsUsedForParts
            };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message };
        }
    }

    private static readonly JsonSerializerSettings CamelCaseSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    /// <summary>
    /// Serializes a strongly-typed result with camelCase names, matching every
    /// hand-built anonymous object in the command API (the web UI and API clients
    /// expect lower-case properties such as <c>isActive</c> and <c>bands</c>).
    /// </summary>
    private static JToken? CamelCase(object? value)
        => value == null ? null : JToken.FromObject(value, JsonSerializer.Create(CamelCaseSettings));

    private object GetFullConfig()
    {
        var cfg = ConfigService.Instance.Current;
        if (cfg == null) return new { };
        return new
        {            address = cfg.Address ?? "",
            fetchingTimer = cfg.FetchingTimer,
            threads = cfg.Threads,
            autoEnqueue = cfg.AutoEnqueue,
            requestUrl = cfg.RequestUrl ?? "",
            presentationFullscreen = cfg.PresentationFullscreen,
            normalizeVolume = cfg.NormalizeVolume,
            equalizer = CamelCase(cfg.Equalizer),
            useCaptionLyricsFallback = cfg.UseCaptionLyricsFallback,            enableAnnouncements = cfg.EnableAnnouncements,
            bearerToken = cfg.BearerToken ?? "",
            defaultSorting = cfg.DefaultSorting ?? "",
            defaultSubmitMethod = cfg.DefaultSubmitMethod ?? "search",
            libraryScanFolders = cfg.LibraryScanFolders ?? new List<string>(),
            libraryAllowedExtensions = cfg.LibraryAllowedExtensions ?? new List<string>(),
            libraryAutoScanOnStartup = cfg.LibraryAutoScanOnStartup,
            libraryAutoAddDownloads = cfg.LibraryAutoAddDownloads,
            operatingMode = cfg.OperatingMode ?? "SongRequests",
            presentationZoom = Math.Clamp(cfg.PresentationZoom, 0.5, 2.0),
            libraryRemoveMissingOnScan = cfg.LibraryRemoveMissingOnScan,
            libraryRecommendationsEnabled = cfg.LibraryRecommendationsEnabled,
            logPlayback = cfg.LogPlayback,
            useNewUI = cfg.UseNewUI,
            autoStart = cfg.AutoStart,
            startMinimized = cfg.StartMinimized,
            minimizeToTray = cfg.MinimizeToTray,
            closeToTray = cfg.CloseToTray,
            traySupported = TrayIconFactory.IsSupported,
            autoStartSupported = AutoStart.IsSupported,
            // Mobile hosts: hide the desktop-only settings (tray, autostart, tool
            // downloads, in-app updates) instead of offering dead options.
            isMobile = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS(),
            selfUpdateSupported = UpdateService.SelfUpdateSupported,
            globalKeyboardShortcuts = cfg.GlobalKeyboardShortcuts,
            globalShortcutsSupported = RemoteControlService.Instance.GlobalShortcutsSupported,
            registeredGlobalShortcuts = RemoteControlService.Instance.RegisteredGlobalShortcuts,
            toolPromptDismissed = cfg.ToolPromptDismissed,
            cookieBrowser = cfg.CookieBrowser ?? string.Empty,
            autoGrabCookies = cfg.AutoGrabCookies,
            systemMediaIntegration = cfg.SystemMediaIntegration,
            autoAnalyzeSongs = cfg.AutoAnalyzeSongs,
            autoAnalyzeLibraryOnAdd = cfg.AutoAnalyzeLibraryOnAdd,
            autoAnalyzeLibraryOnAlgorithmUpdate = cfg.AutoAnalyzeLibraryOnAlgorithmUpdate,
            karaokeMode = cfg.KaraokeMode,
            systemMediaSupported = MediaSessionFactory.IsSupported,
            systemMediaBackend = MediaSessionFactory.SupportedBackendName,
            systemMediaActive = SystemMediaActive,
            apiEnabled = cfg.ApiEnabled,
            apiBindAddress = cfg.ApiBindAddress,
            apiPort = cfg.ApiPort,
            apiToken = cfg.ApiToken,
            mcpEnabled = cfg.McpEnabled,
            mcpBindAddress = cfg.McpBindAddress,
            mcpPort = cfg.McpPort,
            mcpToken = cfg.McpToken,
            integrationStatus = Integration.AppIntegrationServer.Instance.GetStatusObject()
        };
    }

    private object GetRemoteConfig()
    {
        var cfg = ConfigService.Instance.Current;
        if (cfg == null) return new { };
        var remote = RemoteControlConfiguration.Ensure(cfg.RemoteControl);

        // Keybinds keyed by action id (matches the web UI action ids)
        var keybinds = new Dictionary<string, object>
        {
            ["PlayPause"] = KeybindData(remote.PlayPauseKeybind),
            ["SkipNext"] = KeybindData(remote.SkipNextKeybind),
            ["Previous"] = KeybindData(remote.PreviousKeybind),
            ["Stop"] = KeybindData(remote.StopKeybind),
            ["VolumeUp"] = KeybindData(remote.VolumeUpKeybind),
            ["VolumeDown"] = KeybindData(remote.VolumeDownKeybind),
            ["CrossfadeUp"] = KeybindData(remote.CrossfadeUpKeybind),
            ["CrossfadeDown"] = KeybindData(remote.CrossfadeDownKeybind),
            ["Announcement"] = KeybindData(remote.AnnouncementKeybind),
            ["AnnouncementPlaySoundToggle"] = KeybindData(remote.AnnouncementPlaySoundToggleKeybind),
            ["AnnouncementPushToTalkToggle"] = KeybindData(remote.AnnouncementPushToTalkToggleKeybind),
            ["AnnouncementDimDbUp"] = KeybindData(remote.AnnouncementDimDbUpKeybind),
            ["AnnouncementDimDbDown"] = KeybindData(remote.AnnouncementDimDbDownKeybind)
        };

        var midis = new Dictionary<string, object?>
        {
            ["PlayPause"] = MidiData(remote.PlayPauseMidi),
            ["SkipNext"] = MidiData(remote.SkipNextMidi),
            ["Previous"] = MidiData(remote.PreviousMidi),
            ["Stop"] = MidiData(remote.StopMidi),
            ["Volume"] = MidiData(remote.VolumeMidi),
            ["Crossfade"] = MidiData(remote.CrossfadeMidi),
            ["Announcement"] = MidiData(remote.AnnouncementMidi),
            ["AnnouncementPlaySoundToggle"] = MidiData(remote.AnnouncementPlaySoundToggleMidi),
            ["AnnouncementPushToTalkToggle"] = MidiData(remote.AnnouncementPushToTalkToggleMidi),
            ["AnnouncementDimDb"] = MidiData(remote.AnnouncementDimDbMidi)
        };

        return new
        {
            midiEnabled = remote.MidiEnabled,
            midiInputDevice = remote.MidiInputDevice,
            midiOutputDevice = remote.MidiOutputDevice,
            inputs = GetMidiInputDeviceList(),
            outputs = GetMidiOutputDeviceList(),
            keybinds,
            midis
        };
    }

    private static object KeybindData(KeyboardShortcutConfig? k) => new
    {
        enabled = k?.Enabled == true && !string.IsNullOrWhiteSpace(k.Gesture),
        gesture = k?.Gesture ?? string.Empty
    };

    private static object? MidiData(MidiMapping? m)
    {
        if (m == null || !m.IsConfigured) return null;
        return new
        {
            channel = m.Channel,
            note = m.Note,
            messageType = m.MessageType,
            velocityPressed = m.VelocityPressed,
            velocityUnpressed = m.VelocityUnpressed
        };
    }

    private List<object> GetMidiInputDeviceList() =>
        RemoteControlService.Instance.MidiService.GetInputDevices()
            .Select(d => (object)new { number = d.DeviceNumber, name = d.Name })
            .ToList();

    private List<object> GetMidiOutputDeviceList() =>
        RemoteControlService.Instance.MidiService.GetOutputDevices()
            .Select(d => (object)new { number = d.DeviceNumber, name = d.Name })
            .ToList();

    private object ReloadConfig()
    {
        try
        {
            ConfigService.Instance.Reload();
            RemoteControlService.Instance.ApplyConfig();
            try { Integration.AppIntegrationServer.Instance.ApplyConfig(); } catch { }
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object SaveRemoteConfig(JObject msg)
    {
        try
        {
            ConfigService.Instance.Update(cfg =>
            {
                var remote = RemoteControlConfiguration.Ensure(cfg.RemoteControl);

                if (msg.TryGetValue("midiEnabled", out var me)) remote.MidiEnabled = me.ToObject<bool>();
                if (msg.TryGetValue("midiInputDevice", out var mi)) remote.MidiInputDevice = mi.ToObject<int>();
                if (msg.TryGetValue("midiOutputDevice", out var mo)) remote.MidiOutputDevice = mo.ToObject<int>();

                if (msg["keybinds"] is JObject kb)
                {
                    ApplyKeybind(kb, "PlayPause", remote.PlayPauseKeybind);
                    ApplyKeybind(kb, "SkipNext", remote.SkipNextKeybind);
                    ApplyKeybind(kb, "Previous", remote.PreviousKeybind);
                    ApplyKeybind(kb, "Stop", remote.StopKeybind);
                    ApplyKeybind(kb, "VolumeUp", remote.VolumeUpKeybind);
                    ApplyKeybind(kb, "VolumeDown", remote.VolumeDownKeybind);
                    ApplyKeybind(kb, "CrossfadeUp", remote.CrossfadeUpKeybind);
                    ApplyKeybind(kb, "CrossfadeDown", remote.CrossfadeDownKeybind);
                    ApplyKeybind(kb, "Announcement", remote.AnnouncementKeybind);
                    ApplyKeybind(kb, "AnnouncementPlaySoundToggle", remote.AnnouncementPlaySoundToggleKeybind);
                    ApplyKeybind(kb, "AnnouncementPushToTalkToggle", remote.AnnouncementPushToTalkToggleKeybind);
                    ApplyKeybind(kb, "AnnouncementDimDbUp", remote.AnnouncementDimDbUpKeybind);
                    ApplyKeybind(kb, "AnnouncementDimDbDown", remote.AnnouncementDimDbDownKeybind);
                }

                if (msg["midis"] is JObject midi)
                {
                    ApplyMidi(midi, "PlayPause", m => remote.PlayPauseMidi = m);
                    ApplyMidi(midi, "SkipNext", m => remote.SkipNextMidi = m);
                    ApplyMidi(midi, "Previous", m => remote.PreviousMidi = m);
                    ApplyMidi(midi, "Stop", m => remote.StopMidi = m);
                    ApplyMidi(midi, "Volume", m => remote.VolumeMidi = m);
                    ApplyMidi(midi, "Crossfade", m => remote.CrossfadeMidi = m);
                    ApplyMidi(midi, "Announcement", m => remote.AnnouncementMidi = m);
                    ApplyMidi(midi, "AnnouncementPlaySoundToggle", m => remote.AnnouncementPlaySoundToggleMidi = m);
                    ApplyMidi(midi, "AnnouncementPushToTalkToggle", m => remote.AnnouncementPushToTalkToggleMidi = m);
                    ApplyMidi(midi, "AnnouncementDimDb", m => remote.AnnouncementDimDbMidi = m);
                }
            });

            RemoteControlService.Instance.ApplyConfig();
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private static void ApplyKeybind(JObject keybinds, string action, KeyboardShortcutConfig target)
    {
        if (keybinds[action] is not JObject o) return;
        target.Gesture = o["gesture"]?.ToString() ?? string.Empty;
        target.Enabled = (o["enabled"]?.ToObject<bool>() ?? false) && !string.IsNullOrWhiteSpace(target.Gesture);
    }

    private static void ApplyMidi(JObject midis, string action, Action<MidiMapping?> setter)
    {
        if (!midis.ContainsKey(action)) return;
        if (midis[action] is not JObject o)
        {
            setter(null);
            return;
        }
        setter(new MidiMapping
        {
            Channel = o["channel"]?.ToObject<int>() ?? 1,
            Note = o["note"]?.ToObject<int>() ?? 0,
            MessageType = o["messageType"]?.ToString() ?? "NoteOn",
            VelocityPressed = o["velocityPressed"]?.ToObject<int>() ?? 127,
            VelocityUnpressed = o["velocityUnpressed"]?.ToObject<int>() ?? 0
        });
    }

    private object ApplyMidiDevices(JObject msg)
    {
        try
        {
            var enabled = msg["enabled"]?.ToObject<bool>() ?? false;
            var input = msg["inputDevice"]?.ToObject<int>() ?? -1;
            var output = msg["outputDevice"]?.ToObject<int>() ?? -1;
            RemoteControlService.Instance.ApplyMidiDevices(input, output, enabled);
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object TestMidiFeedback()
    {
        try
        {
            var svc = RemoteControlService.Instance.MidiService;
            if (!svc.IsEnabled || svc.OutputDeviceNumber == null)
                return new { error = "Please enable MIDI and select an output device first." };

            for (int note = 0; note < 128; note++) svc.SendNoteOn(1, note, 64);
            _ = Task.Delay(500).ContinueWith(_ =>
            {
                for (int note = 0; note < 128; note++) svc.SendNoteOff(1, note);
            });
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object StartMidiCapture()
    {
        try
        {
            var svc = RemoteControlService.Instance.MidiService;
            lock (_midiCaptureLock)
            {
                if (!_midiCaptureSubscribed)
                {
                    svc.MidiMessageReceived += OnMidiCaptureMessage;
                    _midiCaptureSubscribed = true;
                }
            }
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object StopMidiCapture()
    {
        try
        {
            UnsubscribeMidiCapture();
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private void UnsubscribeMidiCapture()
    {
        try
        {
            var svc = RemoteControlService.Instance.MidiService;
            lock (_midiCaptureLock)
            {
                if (!_midiCaptureSubscribed) return;
                svc.MidiMessageReceived -= OnMidiCaptureMessage;
                _midiCaptureSubscribed = false;
            }
        }
        catch { }
    }

    private void OnMidiCaptureMessage(object? sender, MidiMessage message)
    {
        var parsed = MidiEventParser.Parse(message);
        if (parsed != null) SendEvent("midiInput", parsed);
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
                if (settings.TryGetValue("equalizer", out var eqJson) && eqJson is JObject eqObj)
                {
                    var next = eqObj.ToObject<EqualizerConfiguration>() ?? new EqualizerConfiguration();
                    cfg.Equalizer = next.Normalized();
                }
                if (settings.TryGetValue("useCaptionLyricsFallback", out var cl)) cfg.UseCaptionLyricsFallback = cl.ToObject<bool>();
                if (settings.TryGetValue("enableAnnouncements", out var ea)) cfg.EnableAnnouncements = ea.ToObject<bool>();
                if (settings.TryGetValue("bearerToken", out var bt))
                {
                    // The header must be "<scheme> <token>". A token pasted without
                    // "Bearer " (easy to do on a phone) produced an invalid
                    // Authorization header and a 401 that looked like a bad token.
                    var token = (bt?.ToString() ?? string.Empty).Trim();
                    if (token.Length > 0 && !token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        token = "Bearer " + token;
                    cfg.BearerToken = token;
                }
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
                if (settings.TryGetValue("logPlayback", out var lp)) cfg.LogPlayback = lp.ToObject<bool>();
                if (settings.TryGetValue("useNewUI", out var un)) cfg.UseNewUI = un.ToObject<bool>();
                if (settings.TryGetValue("autoStart", out var asu)) cfg.AutoStart = asu.ToObject<bool>();
                if (settings.TryGetValue("startMinimized", out var sm)) cfg.StartMinimized = sm.ToObject<bool>();
                if (settings.TryGetValue("minimizeToTray", out var mtt)) cfg.MinimizeToTray = mtt.ToObject<bool>();
                if (settings.TryGetValue("closeToTray", out var ctt)) cfg.CloseToTray = ctt.ToObject<bool>();

                // Changing the tray settings is the user's way of retrying it after it
                // was switched off by the crash guard.
                if (settings.ContainsKey("minimizeToTray") || settings.ContainsKey("closeToTray"))
                {
                    try { StartupGuard.ClearTrayDisabled(); } catch { }
                }
                if (settings.TryGetValue("globalKeyboardShortcuts", out var gks)) cfg.GlobalKeyboardShortcuts = gks.ToObject<bool>();
                if (settings.TryGetValue("systemMediaIntegration", out var smi)) cfg.SystemMediaIntegration = smi.ToObject<bool>();
                if (settings.TryGetValue("autoAnalyzeSongs", out var aas)) cfg.AutoAnalyzeSongs = aas.ToObject<bool>();
                if (settings.TryGetValue("autoAnalyzeLibraryOnAdd", out var aala)) cfg.AutoAnalyzeLibraryOnAdd = aala.ToObject<bool>();
                if (settings.TryGetValue("autoAnalyzeLibraryOnAlgorithmUpdate", out var aalau)) cfg.AutoAnalyzeLibraryOnAlgorithmUpdate = aalau.ToObject<bool>();
                if (settings.TryGetValue("karaokeMode", out var karaoke)) cfg.KaraokeMode = karaoke.ToObject<bool>();
                if (settings.TryGetValue("autoGrabCookies", out var agc)) cfg.AutoGrabCookies = agc.ToObject<bool>();

                // Local API / MCP servers. ApplyConfig is called after the update;
                // these assignments only persist the values.
                if (settings.TryGetValue("apiEnabled", out var apiEn)) cfg.ApiEnabled = apiEn.ToObject<bool>();
                if (settings.TryGetValue("apiBindAddress", out var apiBind)) cfg.ApiBindAddress = apiBind.ToString();
                if (settings.TryGetValue("apiPort", out var apiPort)) cfg.ApiPort = apiPort.ToObject<int>();
                if (settings.TryGetValue("apiToken", out var apiToken)) cfg.ApiToken = apiToken.ToString();
                if (settings.TryGetValue("mcpEnabled", out var mcpEn)) cfg.McpEnabled = mcpEn.ToObject<bool>();
                if (settings.TryGetValue("mcpBindAddress", out var mcpBind)) cfg.McpBindAddress = mcpBind.ToString();
                if (settings.TryGetValue("mcpPort", out var mcpPort)) cfg.McpPort = mcpPort.ToObject<int>();
                if (settings.TryGetValue("mcpToken", out var mcpToken)) cfg.McpToken = mcpToken.ToString();

                if (settings.TryGetValue("normalizeVolume", out var nv2) && !nv2.ToObject<bool>())
                {
                    cfg.NormalizationActive = false;
                }
            });

            // Apply the OS-level autostart registration (best-effort).
            ApplyAutoStart();

            // Turning auto-analysis on should also analyze the track that is
            // already playing, not only future tracks.
            if (settings.TryGetValue("autoAnalyzeSongs", out var autoAnalyze) &&
                autoAnalyze.ToObject<bool>() &&
                _ytForm.Music.Queue.FirstOrDefault() is { } playing)
            {
                SendOrStartSongAnalysis(playing);
            }

            if (settings.TryGetValue("autoAnalyzeLibraryOnAlgorithmUpdate", out var updateLibrary) &&
                updateLibrary.ToObject<bool>())
            {
                try { _libraryAnalysis.StartIfOutdated(); } catch { }
            }

            if (settings.ContainsKey("karaokeMode"))
            {
                var enabled = ConfigService.Instance.Current?.KaraokeMode == true;
                MusicPlayerSendEvent("karaokeModeChanged", new { enabled });
                PresentationSendEvent("karaokeModeChanged", new { enabled });
            }

            // Re-register global hotkeys if the toggle changed.
            if (settings.ContainsKey("globalKeyboardShortcuts"))
            {
                try { RemoteControlService.Instance.ApplyConfig(); } catch { }
            }

            // Start/stop the OS media-control integration if the toggle changed.
            if (settings.ContainsKey("systemMediaIntegration"))
            {
                try { ApplySystemMediaIntegration(); } catch { }
            }

            // Apply API/MCP enable/port/bind/token changes immediately.
            if (settings.ContainsKey("apiEnabled") || settings.ContainsKey("apiBindAddress") ||
                settings.ContainsKey("apiPort") || settings.ContainsKey("apiToken") ||
                settings.ContainsKey("mcpEnabled") || settings.ContainsKey("mcpBindAddress") ||
                settings.ContainsKey("mcpPort") || settings.ContainsKey("mcpToken"))
            {
                try { Integration.AppIntegrationServer.Instance.ApplyConfig(); } catch { }
            }

            // Keep the (already open) music player's Announcement button in sync.
            MusicPlayerSendEvent("musicPlayerConfig", new
            {
                enableAnnouncements = ConfigService.Instance.Current?.EnableAnnouncements ?? true
            });

            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    /// <summary>Applies the OS-level autostart registration from the current config (best-effort).</summary>
    private void ApplyAutoStart()
    {
        try
        {
            var cfg = ConfigService.Instance.Current;
            if (cfg == null) return;
            AutoStart.SetEnabled(cfg.AutoStart, cfg.StartMinimized);
        }
        catch { }
    }

    /// <summary>Whether the OS media-control integration is currently connected.</summary>
    public bool SystemMediaActive
    {
        get { lock (_mediaSessionLock) return _mediaSessionBridge != null; }
    }

    /// <summary>The latest metadata received from a Music Share, if any.</summary>
    public MusicShareMetadata? LastMusicShareMetadata => _musicShareLastMetadata;

    /// <summary>Re-sends the current presentation state after a window is shown again.</summary>
    public void RefreshPresentation() => SendPresentationCurrentState();

    /// <summary>
    /// Starts the OS media-control integration (MPRIS/SMTC/...) if it is enabled
    /// and supported. Called after the UI is up — never from the constructor, so
    /// headless tests do not open a D-Bus service.
    /// </summary>
    /// <param name="windowHandle">
    /// The top-level window handle (required by SMTC on Windows; ignored elsewhere).
    /// Remembered so the toggle can restart the session later.
    /// </param>
    public void StartSystemMediaIntegration(IntPtr windowHandle = default)
    {
        try
        {
            if (windowHandle != IntPtr.Zero) _mediaSessionWindowHandle = windowHandle;

            if (ConfigService.Instance.Current?.SystemMediaIntegration != true) return;
            if (!MediaSessionFactory.IsSupported) return;

            lock (_mediaSessionLock)
            {
                if (_mediaSessionBridge != null || _mediaSessionStartTask != null) return;
                _mediaSessionStartTask = StartSystemMediaIntegrationAsync(_mediaSessionWindowHandle);
            }
        }
        catch { }
    }

    private async Task StartSystemMediaIntegrationAsync(IntPtr windowHandle)
    {
        try
        {
            var session = await MediaSessionFactory.CreateAsync(windowHandle).ConfigureAwait(false);
            if (!session.IsSupported)
            {
                try { session.Dispose(); } catch { }
                return;
            }

            var bridge = new MediaSessionBridge(
                _ytForm.Music,
                session,
                song => MediaArtwork.Resolve(song, AppPaths.NowPlayingArtworkDir),
                _ytForm.RunOnUi);

            bridge.RaiseRequested += (_, _) => { try { _ytForm.ShowMusicPlayer(); } catch { } };
            bridge.QuitRequested += (_, _) => { try { RunOnUi(() => _ytForm.NewUiRef?.Close()); } catch { } };

            lock (_mediaSessionLock) { _mediaSessionBridge = bridge; }
            bridge.Refresh();
        }
        catch
        {
            // Media integration is optional; never let it break playback.
        }
    }

    /// <summary>Starts or stops the media integration to match the current config.</summary>
    public void ApplySystemMediaIntegration()
    {
        if (ConfigService.Instance.Current?.SystemMediaIntegration == true)
            StartSystemMediaIntegration();
        else
            StopSystemMediaIntegration();
    }

    private void StopSystemMediaIntegration()
    {
        MediaSessionBridge? bridge;
        lock (_mediaSessionLock)
        {
            bridge = _mediaSessionBridge;
            _mediaSessionBridge = null;
            _mediaSessionStartTask = null;
        }

        try { bridge?.Dispose(); } catch { }
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
            version = AppInfo.version,
            // True once the startup flow (mode + cookie decision) has completed.
            // A page that is recreated later in the same app session must not run
            // the splash/launch/auth sequence again.
            setupComplete = _ytForm.SetupComplete,
            cookieBrowser = ConfigService.Instance.Current?.CookieBrowser ?? string.Empty,
            autoGrabCookies = ConfigService.Instance.Current?.AutoGrabCookies ?? false,
            launchOptions = new
            {
                rememberSelection = state.RememberSelection,
                lastSelectedMode = state.LastSelectedMode.ToString()
            }
        };
    }

    /// <summary>Remembers which browser the user imports YouTube cookies from.</summary>
    private object SaveCookieBrowser(JObject msg)
    {
        try
        {
            var browser = msg["browser"]?.ToString() ?? string.Empty;
            ConfigService.Instance.Update(cfg => cfg.CookieBrowser = browser);

            // Keep the settings picker in sync with the startup prompt.
            SendEvent("cookieBrowserChanged", new { browser });
            return new { success = true, browser };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
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

    private async Task<object> TriggerAuthAsync()
    {
        // Browser selection is required; the web UI shows a picker.
        await Task.CompletedTask;
        return new { success = false, needsBrowser = true, browsers = YoutubeCookieService.SupportedBrowsers };
    }

    private object GetAuthBrowsers()
    {
        // Mobile has no desktop browsers to import from: it signs in in-app.
        var browsers = AuthBrowsersProvider?.Invoke() ?? new List<string>(YoutubeCookieService.SupportedBrowsers);
        return new { browsers, signInSupported = SignInAsync != null };
    }

    private object GetToolStatus()
    {
        if (!ToolManager.IsSupported)
        {
            // Nothing to install on this host: report "nothing missing" so no
            // prompt appears, and say why for the settings UI.
            return new
            {
                supported = false,
                ffmpeg = new { found = false, path = "" },
                ffprobe = new { found = false, path = "" },
                ytDlp = new { found = false, path = "" },
                audioReady = true,
                allPresent = true,
                toolsDirectory = ""
            };
        }

        var status = ToolManager.GetStatus();
        return new
        {
            supported = true,
            ffmpeg = new { found = status.Ffmpeg.Found, path = status.Ffmpeg.Path ?? "" },
            ffprobe = new { found = status.Ffprobe.Found, path = status.Ffprobe.Path ?? "" },
            ytDlp = new { found = status.YtDlp.Found, path = status.YtDlp.Path ?? "" },
            audioReady = status.AudioReady,
            allPresent = status.AllPresent,
            toolsDirectory = ToolManager.ResolveTargetDirectory()
        };
    }

    private object StartToolDownload(JObject msg)
    {
        try
        {
            bool ffmpeg = msg["ffmpeg"]?.ToObject<bool>() ?? false;
            bool ytDlp = msg["ytDlp"]?.ToObject<bool>() ?? false;
            if (!ffmpeg && !ytDlp) return new { error = "Nothing selected to download" };

            _ = Task.Run(async () =>
            {
                try
                {
                    if (ffmpeg) await ToolManager.InstallFfmpegAsync(p => SendEvent("toolProgress", p)).ConfigureAwait(false);
                    if (ytDlp) await ToolManager.InstallYtDlpAsync(p => SendEvent("toolProgress", p)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    SendEvent("toolProgress", new ToolInstallProgress { Tool = "tools", Stage = "error", Message = ex.Message });
                }
                finally
                {
                    SendEvent("toolStatus", GetToolStatus());
                }
            });

            return new { success = true, started = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }    private async Task<object> GrabCookiesAsync(JObject msg)
    {
        var browser = msg["browser"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(browser)) return new { error = "No browser specified" };

        var (ok, cookies, error) = await YoutubeCookieService.GrabFromBrowserAsync(browser).ConfigureAwait(false);
        if (!ok || cookies.Count == 0)
            return new { error = error ?? "No cookies found" };

        ApplyCookies(cookies);
        return new { success = true, cookieCount = cookies.Count };
    }

    private object SubmitCookies(JObject msg)
    {
        var text = msg["text"]?.ToString() ?? "";
        var cookies = YoutubeCookieService.ParseCookies(text);
        if (cookies.Count == 0) return new { error = "No valid cookies found in the provided text" };
        ApplyCookies(cookies);
        return new { success = true, cookieCount = cookies.Count };
    }

    private void ApplyCookies(IReadOnlyList<System.Net.Cookie> cookies)
    {
        try
        {
            YoutubeCookieService.Save(cookies);
            _ytService.SetCookies(cookies);
        }
        catch { }
    }

    private object ClearAuthCookies()
    {
        try
        {
            YoutubeCookieService.Clear();
            _ytService.SetCookies(Array.Empty<System.Net.Cookie>());
            return new { success = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object CompleteSetup(JObject msg)
    {
        try
        {
            var mode = StartupMode.SongRequests;
            if (msg.TryGetValue("startupMode", out var sm))
                Enum.TryParse<StartupMode>(sm.ToString(), true, out mode);

            // The user has now made the cookie decision (imported or skipped), so
            // automatic downloads may start from here on.
            try { _ytForm.SetupComplete = true; } catch { }

            // Remember the mode so the Settings selector reflects it and a runtime
            // switch starts from the same place next launch.
            try { ConfigService.Instance.Update(cfg => cfg.OperatingMode = mode.ToString()); } catch { }

            // Apply startup mode for the web UI
            if (_ytForm != null)
            {
                RunOnUi(() =>
                {
                    // Mobile shells choose their navigation from the mode before the
                    // mode itself opens its first window/tab.
                    try { _ytForm.NewUiRef?.SetOperatingMode(mode); } catch { }

                    switch (mode)
                    {
                        case StartupMode.MusicPlayer:
                            ShowMusicPlayer();
                            break;
                        case StartupMode.MusicShare:
                            ShowMusicPlayer();
                            _pendingMusicShareOpen = true;
                            break;
                        case StartupMode.Soundboard:
                            ShowSoundboard();
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

    private async Task<object> StartUpdate(JObject msg)
    {
        try
        {
            if (!UpdateService.SelfUpdateSupported)
                return new { error = "This platform updates through the app store, not in-app." };

            var downloadUrl = msg["downloadUrl"]?.ToString();
            if (string.IsNullOrEmpty(downloadUrl))
                return new { error = "No download URL" };

            var packageInstaller = UpdateService.PackageInstaller;
            if (packageInstaller != null)
            {
                // Mobile: stay with the updater until the download has finished,
                // the APK has been verified and the system installer has opened.
                // Returning immediately let the startup flow continue while the
                // download was still running in the background.
                var progress = new Progress<UpdateService.UpdateProgress>(p =>
                    SendEvent("updateProgress", new { stage = p.Stage, progress = p.Percent, message = p.Message }));

                var error = await packageInstaller(downloadUrl, progress).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(error))
                {
                    SendEvent("updateProgress", new { stage = "error", progress = 0, message = error });
                    return new { error };
                }

                SendEvent("updateProgress", new { stage = "installing", progress = 100, message = "Installer opened. Complete the update there." });
                return new { success = true, completed = true, installKind = "package" };
            }

            // Desktop: the installer replaces the process and restarts it. Start it
            // in the background so the UI can stay on the update panel while the
            // process is torn down.
            _ = Task.Run(async () =>
            {
                try
                {
                    await UpdateInstaller.InstallAsync(downloadUrl, p =>
                        SendEvent("updateProgress", new { stage = p.Stage, progress = p.Percent, message = p.Message }));
                }
                catch (Exception ex)
                {
                    SendEvent("updateProgress", new { stage = "error", progress = 0, message = ex.Message });
                }
            });

            return new { success = true, started = true };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task<object> CheckForUpdatesAsync()
    {
        // Mobile hosts install updates through the store / a new APK: the release
        // assets are desktop archives, so offering them here would be wrong (and
        // the "Install Now" button would try to run the desktop installer).
        if (!UpdateService.SelfUpdateSupported)
        {
            return new
            {
                updateAvailable = false,
                selfUpdate = false,
                currentVersion = AppInfo.version,
                message = "Updates are installed with the app on this platform."
            };
        }

        try
        {
            var info = await UpdateService.CheckForUpdatesAsync().ConfigureAwait(false);
            return new
            {
                updateAvailable = info.UpdateAvailable,
                latestVersion = info.LatestVersion,
                currentVersion = info.CurrentVersion,
                downloadUrl = info.DownloadUrl,
                releaseNotes = info.ReleaseNotes,
                platform = info.Platform,
                assetName = info.AssetName,
                // How the update would be applied: the platform installer (the APK
                // on Android) or the desktop's archive swap + relaunch.
                installKind = UpdateService.PackageInstaller != null ? "package" : "archive",
                error = info.Error
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
            RunOnUi(() =>
            {
                song = _ytForm.Music.Queue?.FirstOrDefault();
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
                RegisterTimedLyrics(song, cached);
                SendLyricsResult(mpSend, cached, song);
                var src = ConfigService.Instance.Current?.PresentationSource ?? "player";
                if (src == "player") PresentationSendLyricsResult(cached, song);
                return;
            }
        }

        var lyricsService = _musicPlayerLyricsService ??= new LyricsService();
        var result = await ResolveLyricsAsync(song, lyricsService).ConfigureAwait(false);

        lock (_musicPlayerLyricsCacheLock)
        {
            _musicPlayerLyricsCache[songKey] = result;
        }

        RegisterTimedLyrics(song, result);
        SendLyricsResult(mpSend, result, song);

        var source = ConfigService.Instance.Current?.PresentationSource ?? "player";
        if (source == "player") PresentationSendLyricsResult(result, song);

        _ = PrefetchNextLyricsAsync();
    }

    /// <summary>
    /// The single lyrics resolution order used by playback, prefetch, presentation
    /// and the legacy lyrics panel:
    ///   embedded tag LRC → sidecar .lrc → cached LRCLIB → live LRCLIB (cached on
    ///   success) → cached YouTube captions (sent-in downloads only).
    /// </summary>
    private async Task<LyricsResult> ResolveLyricsAsync(Song song, LyricsService lyricsService)
    {
        try
        {
            var embedded = LyricsService.GetEmbeddedLyrics(song.songPath);
            if (embedded != null && embedded.Found) return embedded;

            var lrcFile = LyricsService.GetLrcFileLyrics(song.songPath);
            if (lrcFile != null && lrcFile.Found) return lrcFile;

            var persistent = GetCachedLrcLib(song);
            if (persistent != null) return persistent;

            var query = LyricsQueryNormalizer.Build(song);
            var result = await lyricsService
                .GetCachedLyricsAsync(query.Artist, query.Title, song.Duration)
                .ConfigureAwait(false);

            if (!result.Found)
            {
                result = await lyricsService
                    .GetLyricsAsync(query.Artist, query.Title, song.Duration)
                    .ConfigureAwait(false);
            }

            if (result.Found)
            {
                CacheLrcLib(song, result);
                return result;
            }
        }
        catch
        {
            // Fall through to captions / no lyrics.
        }

        var fallbackEnabled = ConfigService.Instance.Current?.UseCaptionLyricsFallback ?? true;
        if (fallbackEnabled &&
            TryGetCachedSubtitleFallback(song.songPath, out var timedSub, out var plainSub))
        {
            return new LyricsResult
            {
                Found = true,
                SyncedLyrics = timedSub ?? string.Empty,
                PlainLyrics = plainSub ?? string.Empty,
                Provider = LyricsProvider.YouTubeCaptions
            };
        }

        return new LyricsResult();
    }

    private LyricsResult? GetCachedLrcLib(Song song)
    {
        try
        {
            var videoId = Path.GetFileNameWithoutExtension(song.songPath);
            if (string.IsNullOrWhiteSpace(videoId)) return null;

            var data = _ytForm.GetOrCreateVideoData(videoId);
            if (!string.Equals(data.LyricsProvider, "LRCLIB", StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.IsNullOrWhiteSpace(data.LyricsSyncedText) &&
                string.IsNullOrWhiteSpace(data.LyricsText))
                return null;

            return new LyricsResult
            {
                Found = true,
                SyncedLyrics = data.LyricsSyncedText ?? string.Empty,
                PlainLyrics = data.LyricsText ?? string.Empty,
                Provider = LyricsProvider.LRCLIB
            };
        }
        catch { return null; }
    }

    private void CacheLrcLib(Song song, LyricsResult result)
    {
        try
        {
            if (!result.Found) return;
            var videoId = Path.GetFileNameWithoutExtension(song.songPath);
            if (string.IsNullOrWhiteSpace(videoId)) return;

            var data = _ytForm.GetOrCreateVideoData(videoId);
            data.LyricsSyncedText = result.SyncedLyrics ?? string.Empty;
            data.LyricsText = result.PlainLyrics ?? string.Empty;
            data.LyricsProvider = "LRCLIB";
            data.LyricsFetched = true;
            _ytForm.SaveVideoDataCache();
        }
        catch { }
    }

    private async Task PrefetchNextLyricsAsync()
    {
        try
        {
            var mp = _ytForm.Music;
            if (mp == null) return;

            Song? next = null;
            RunOnUi(() =>
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
            var result = await ResolveLyricsAsync(next, lyricsService).ConfigureAwait(false);
            RegisterTimedLyrics(next, result);

            lock (_musicPlayerLyricsCacheLock)
            {
                _musicPlayerLyricsCache[songKey] = result;
            }
        }
        catch { }
    }

    private void SendLyricsResult(Action<string> mpSend, LyricsResult result, Song? song = null)
    {
        var provider = result.Provider switch
        {
            LyricsProvider.FileEmbedded => "Embedded",
            LyricsProvider.LrcFile => "LRC File",
            LyricsProvider.LRCLIB => "LRCLIB",
            LyricsProvider.YouTubeCaptions => "YouTube captions",
            _ => ""
        };
        var data = new System.Collections.Generic.Dictionary<string, object>();
        var syncedLines = result.Found && result.HasSynced
            ? result.ParseSyncedLines()
            : new List<(TimeSpan Time, string Text)>();

        if (syncedLines.Count > 0)
        {
            data["syncedLyrics"] = syncedLines.Select(l => new { time = l.Time.TotalSeconds, text = l.Text }).ToList();
            data["hasSynced"] = true;

            var karaoke = BuildKaraokePayload(song, syncedLines);
            if (karaoke != null) data["karaoke"] = karaoke;
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
        }
        else
        {
            data["hasSynced"] = false;
        }

        data["provider"] = provider;
        data["karaokeMode"] = ConfigService.Instance.Current?.KaraokeMode == true;
        var chapters = ChaptersForSong(song);
        var chaptersJson = chapters == null ? null : CamelCase(chapters);
        if (chaptersJson != null) data["chapters"] = chaptersJson;

        CurrentLyricsProvider = result.Found ? provider : string.Empty;
        CurrentLyricsAvailable = result.Found;

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

        try { EventRaised?.Invoke("lyricsUpdate", data); } catch { }
    }

    private void SendEmptyLyrics(Action<string> mpSend)
    {
        CurrentLyricsProvider = string.Empty;
        CurrentLyricsAvailable = false;

        var data = new { provider = "", hasSynced = false, syncedLyrics = new object[0], plainLyrics = new string[0] };
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

        try { EventRaised?.Invoke("lyricsUpdate", data); } catch { }
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

            var cachePath = AppPaths.VideoMetadataCachePath;
            if (!File.Exists(cachePath)) return false;

            var json = File.ReadAllText(cachePath);
            var cache = JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, VideoData>>(json);
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
        UnsubscribeMidiCapture();
        try { RemoteControlService.Instance.MidiActivity -= _midiActivityHandler; } catch { }
        try { StopSystemMediaIntegration(); } catch { }
        try { _soundboard?.Dispose(); _soundboard = null; } catch { }
        try { _vizStreamer?.Dispose(); _vizStreamer = null; } catch { }
        try { TeardownMusicShareAudio(); } catch { }
        try
        {
            _announcement?.Dispose();
            _announcement = null;
            var mp = _ytForm.Music;
            if (mp != null)
            {
                mp.RemoteAnnouncementHandler = null;
                mp.RemoteAnnouncementReleaseHandler = null;
                mp.RemoteAnnouncementActiveProvider = null;
                mp.AnnouncementSettingsChanged = null;
                mp.OpenMusicShareHandler = null;
                mp.OpenVisualizationHandler = null;
                mp.AnnouncementOpenHandler = null;
            }
        }
        catch { }

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
            var mp = _ytForm.Music;
            if (mp != null)
            {
                mp.NowPlayingTick -= OnNowPlayingTick;
                mp.QueueChanged -= OnQueueChanged;
                mp.PlaylistQueueChanged -= OnQueueChanged;
                mp.QueuesChanged -= OnQueueChanged;
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
            NotifyLibraryScanned(res);
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
            recommendationsEnabled = ConfigService.Instance.Current?.LibraryRecommendationsEnabled ?? true
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
                return Thumbnails.GetDataUrl(song.ThumbnailPath);
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

        bool stillInSentIn = false;
        lock (_libraryService.Data)
        {
            var song = _libraryService.Data.FindSong(songId);
            if (song == null) return new { error = "Song not found" };

            // The sent-in list still needs the downloaded file, but the user asked
            // to remove the entry from the *library*. Keep the file/artwork in that
            // case; remove everything otherwise.
            stillInSentIn = !string.IsNullOrEmpty(song.YouTubeId) &&
                            _ytForm.fetchedYtids.Contains(song.YouTubeId);
        }

        string? filePath = null;
        string? thumbPath = null;
        LibrarySong? removedSong = null;
        lock (_libraryService.Data)
        {
            var song = _libraryService.Data.FindSong(songId);
            if (song == null) return new { error = "Song not found" };
            filePath = song.FilePath;
            thumbPath = song.ThumbnailPath;
            removedSong = song;
            _libraryService.Data.Songs.Remove(song);
        }

        _libraryService.Save();
        if (removedSong != null)
        {
            try { _libraryService.NotifySongRemoved(removedSong); } catch { }
        }

        if (!stillInSentIn)
        {
            // Delete file from disk
            try
            {
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch { }

            // Delete thumbnail from disk
            try
            {
                if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
                    File.Delete(thumbPath);
            }
            catch { }
        }

        return new { success = true, keptFile = stillInSentIn };
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

        AddScanFolderPath(folder);
        return new { success = true, scanFolders = _libraryService.Data.Config.ScanFolders };
    }

    /// <summary>Registers a scan folder (library model + main config) and persists it.</summary>
    private void AddScanFolderPath(string folder)
    {
        lock (_libraryService.Data)
        {
            if (!_libraryService.Data.Config.ScanFolders.Contains(folder))
                _libraryService.Data.Config.ScanFolders.Add(folder);
        }

        ConfigService.Instance.Update(cfg =>
        {
            cfg.LibraryScanFolders = new System.Collections.Generic.List<string>(_libraryService.Data.Config.ScanFolders);
        });

        _libraryService.Save();
    }

    /// <summary>
    /// Tells both pages about a finished scan: the requests/settings page shows the
    /// result, and the music player owns the home feed, which is built from the
    /// library — without this an import made on one page would not appear on the
    /// other until a reload.
    /// </summary>
    private void NotifyLibraryScanned(LibraryScanResult res)
    {
        SendEvent("libraryScanCompleted", new
        {
            filesFound = res.FilesFound,
            songsAdded = res.SongsAdded,
            songsUpdated = res.SongsUpdated,
            songsMissing = res.SongsMissing,
            errorCount = res.Errors.Count,
            errors = res.Errors.Take(20).ToList()
        });

        try
        {
            _ytForm.NewUiRef?.MusicPlayerSendMessage?.Invoke(Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                type = "event",
                eventName = "libraryChanged",
                data = new { songsAdded = res.SongsAdded, songsUpdated = res.SongsUpdated }
            }));
        }
        catch { }
    }

    /// <summary>
    /// "Import music" for the library: pick files or a folder, let the host place
    /// them where it can read them, then register the folder and scan it.
    /// </summary>
    private async Task<object> ImportLibraryAsync(JObject msg)
    {
        var mode = msg["mode"]?.ToString() ?? "files";
        string? folder;

        try
        {
            if (mode == "folder")
            {
                var picked = PickFolderAsync != null
                    ? await PickFolderAsync("Import music folder").ConfigureAwait(false)
                    : ShowOpenFolderDialog?.Invoke("Import music folder");

                if (string.IsNullOrWhiteSpace(picked)) return new { cancelled = true };

                SendEvent("libraryScanProgress", new { progress = 0.01, message = "Copying music into the app…" });
                folder = PrepareImportFolder != null
                    ? await PrepareImportFolder(picked).ConfigureAwait(false)
                    : picked;
            }
            else
            {
                var files = PickFilesAsync != null
                    ? await PickFilesAsync("Import music").ConfigureAwait(false)
                    : ShowOpenFileDialog?.Invoke("Import music",
                        "*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.flac;*.wma", true);

                if (files == null || files.Length == 0) return new { cancelled = true };

                SendEvent("libraryScanProgress", new { progress = 0.01, message = "Copying music into the app…" });
                folder = PrepareImportFiles != null
                    ? await PrepareImportFiles(files).ConfigureAwait(false)
                    : System.IO.Path.GetDirectoryName(files[0]);
            }
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }

        if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder))
            return new { error = "The picked music could not be read." };

        AddScanFolderPath(folder);

        void OnProgress(double pct, string msg)
            => SendEvent("libraryScanProgress", new { progress = pct, message = msg });

        _libraryService.ScanProgress += OnProgress;
        LibraryScanResult scan;
        try
        {
            scan = await _libraryService.ScanAsync().ConfigureAwait(false);
        }
        finally
        {
            _libraryService.ScanProgress -= OnProgress;
        }

        NotifyLibraryScanned(scan);

        return new
        {
            success = true,
            folder,
            filesFound = scan.FilesFound,
            songsAdded = scan.SongsAdded,
            songsUpdated = scan.SongsUpdated,
            songsMissing = scan.SongsMissing,
            errorCount = scan.Errors.Count,
            errors = scan.Errors,
        };
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

    /// <summary>
    /// Everything the music player home tab needs, in one round trip: playlists ordered
    /// by when they were last listened to, recommendations, recently played songs and
    /// the newest library additions.
    /// </summary>
    private object GetHomeFeed(JObject msg)
    {
        var count = msg["count"]?.ToObject<int>() ?? 20;
        if (count <= 0) count = 20;
        count = Math.Min(count, 100);

        // 0 inline conversions: the feed must return immediately. Anything not ready yet
        // is queued below and collected by the page with getThumbnails.
        Thumbnails.BeginRequest(0);
        var data = _libraryService.Data;

        // ── Playlists: dynamic mixes first, then most recently listened/changed ──
        var playlists = new List<object>();
        var presentCount = data.Songs.Count(s => !s.IsMissing);
        foreach (var dynamicPlaylist in DynamicPlaylists)
        {
            playlists.Add(new
            {
                id = dynamicPlaylist.Id,
                name = dynamicPlaylist.Name,
                icon = dynamicPlaylist.Icon,
                description = dynamicPlaylist.Description,
                songCount = Math.Min(100, presentCount),
                lastPlayedUtc = (DateTime?)null,
                thumbnail = (string?)null,
                isDynamic = true
            });
        }
        playlists.AddRange(data.Playlists
            .OrderByDescending(p => p.LastPlayedUtc ?? DateTime.MinValue)
            .ThenByDescending(p => p.LastModifiedUtc)
            .Take(count)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                icon = p.Icon ?? "",
                songCount = p.Entries.Count,
                lastPlayedUtc = p.LastPlayedUtc,
                thumbnail = p.Entries
                    .Select(e => data.FindSong(e.SongId))
                    .Where(s => s != null && !string.IsNullOrEmpty(s!.ThumbnailPath))
                    .Select(s => GetLibrarySongThumbnail(s!))
                    .FirstOrDefault(t => t != null),
                isDynamic = false
            }));

        // ── Songs recommended to you (library-indexed songs only) ──
        var recommendedEnabled = ConfigService.Instance.Current?.LibraryRecommendationsEnabled ?? true;
        var recommended = new List<object>();
        if (recommendedEnabled)
        {
            foreach (var r in RecommendationEngine.Recommend(data, count))
            {
                recommended.Add(new
                {
                    songId = r.Song.Id,
                    title = r.Song.Title,
                    artist = r.Song.Artist,
                    album = r.Song.Album,
                    genre = r.Song.Genre,
                    duration = r.Song.Duration.TotalSeconds,
                    durationDisplay = r.Song.DurationDisplay,
                    thumbnail = GetLibrarySongThumbnail(r.Song),
                    filePath = r.Song.FilePath,
                    score = Math.Round(r.Score, 4),
                    reason = r.Reason,
                    signal = r.Signal
                });
            }
        }

        // ── Recently played: the Recently Played tab's newest entries, one per song ──
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recentlyPlayed = new List<object>();
        foreach (var entry in _libraryService.GetRecentlyPlayed(Math.Max(count * 3, 30)))
        {
            var songId = entry.TryGetValue("songId", out var idObj) ? idObj?.ToString() : null;
            if (string.IsNullOrEmpty(songId) || !seen.Add(songId!)) continue;

            var filePath = entry.TryGetValue("filePath", out var fpObj) ? fpObj?.ToString() : null;
            recentlyPlayed.Add(new
            {
                songId,
                title = entry.TryGetValue("title", out var titleObj) ? titleObj?.ToString() : "",
                artist = entry.TryGetValue("artist", out var artistObj) ? artistObj?.ToString() : "",
                durationDisplay = entry.TryGetValue("durationDisplay", out var durObj) ? durObj?.ToString() : "",
                playedUtc = entry.TryGetValue("playedUtc", out var playedObj) ? playedObj : null,
                thumbnail = GetThumbnailForSongPath(filePath)
            });

            if (recentlyPlayed.Count >= count) break;
        }

        // ── Newly added to the library ──
        // warm the cache in the background so the artwork is there next time
        try
        {
            Thumbnails.Prewarm(data.Songs
                .Where(s => !string.IsNullOrEmpty(s.ThumbnailPath))
                .Take(240)
                .Select(s => s.ThumbnailPath!));
        }
        catch { }

        var newlyAdded = data.Songs
            .Where(s => s.FileStatus == FileStatus.Present)
            .OrderByDescending(s => s.DateAddedUtc)
            .Take(count)
            .Select(s => new
            {
                songId = s.Id,
                title = s.Title,
                artist = s.Artist,
                album = s.Album,
                durationDisplay = s.DurationDisplay,
                thumbnail = GetLibrarySongThumbnail(s),
                filePath = s.FilePath,
                dateAddedUtc = s.DateAddedUtc,
                playCount = s.PlayCount
            })
            .ToList();

        return new
        {
            playlists,
            recommended,
            recommendedEnabled,
            recentlyPlayed,
            newlyAdded,
            stats = new
            {
                totalSongs = data.Songs.Count,
                presentSongs = data.Songs.Count(s => s.FileStatus == FileStatus.Present),
                totalPlaylists = data.Playlists.Count,
                totalPlays = data.PlayHistory.Count
            }
        };
    }

    /// <summary>Output devices for the music player's speaker menu.</summary>
    /// <summary>Small, cached artwork data URLs (downscaled once, then reused).</summary>
    internal ThumbnailStore Thumbnails { get; set; } = ThumbnailStore.Instance;

    /// <summary>
    /// Looks up artwork for a set of library songs. The UI calls this with the cards it
    /// is actually showing: the main feed response deliberately omits artwork that is not
    /// ready yet instead of blocking on converting dozens of images at once.
    /// </summary>
    private object GetThumbnails(JObject msg)
    {
        var ids = msg["songIds"] as JArray;
        var wanted = new List<string>();
        if (ids != null)
        {
            foreach (var id in ids)
            {
                var value = id?.ToString();
                if (!string.IsNullOrEmpty(value) && wanted.Count < 64) wanted.Add(value!);
            }
        }

        Thumbnails.BeginRequest();
        var data = _libraryService.Data;
        var result = new Dictionary<string, string>();
        foreach (var id in wanted)
        {
            var song = data.FindSong(id);
            if (song == null || string.IsNullOrEmpty(song.ThumbnailPath) || !File.Exists(song.ThumbnailPath)) continue;

            var url = Thumbnails.GetDataUrl(song.ThumbnailPath);
            if (url != null) result[id] = url;
        }

        return new { thumbs = result, pending = Thumbnails.PendingCount };
    }

    private object GetMusicAudioDevices()
    {
        var devices = new List<object> { new { number = -1, name = "System Default" } };
        try
        {
            foreach (var device in AudioDevices.Enumerate())
                devices.Add(new { number = device.Number, name = device.Name });
        }
        catch { }

        return new
        {
            devices,
            selected = _ytForm.Music?.OutputDeviceNumber ?? -1
        };
    }

    /// <summary>Switches the music output device (the change is audible immediately).</summary>
    private object SetMusicOutputDevice(JObject msg)
    {
        var deviceNumber = msg["deviceNumber"]?.ToObject<int?>() ?? -1;
        var engine = _ytForm.Music;
        if (engine == null) return new { success = false, error = "No music engine" };

        try
        {
            PostToUi(() => engine.SetOutputDevice(deviceNumber));
            ConfigService.Instance.Update(cfg => cfg.AudioOutputDeviceNumber = deviceNumber);
            return new { success = true, selected = deviceNumber };
        }
        catch (Exception ex)
        {
            return new { success = false, error = ex.Message };
        }
    }

    private object GetPlayHistory(JObject msg)
    {
        var count = msg["count"]?.ToObject<int?>() ?? 50;
        var history = _libraryService.GetRecentlyPlayed(count);

        foreach (var entry in history)
        {
            var filePath = entry.TryGetValue("filePath", out var fpObj) ? fpObj?.ToString() : null;
            var thumb = GetThumbnailForSongPath(filePath);
            if (thumb != null)
                entry["thumbnail"] = thumb;
        }

        return new { history };
    }

    private object RemovePlayHistoryEntry(JObject msg)
    {
        var entryId = msg["entryId"]?.ToString();
        if (string.IsNullOrEmpty(entryId)) return new { error = "No entryId" };

        _libraryService.RemovePlayHistoryEntry(entryId);
        return new { success = true };
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

    private static Song CreateSongFromLibrary(LibrarySong libSong)
    {
        var title = !string.IsNullOrWhiteSpace(libSong.Title) ? libSong.Title : Path.GetFileNameWithoutExtension(libSong.FilePath);
        var artist = !string.IsNullOrWhiteSpace(libSong.Artist) ? libSong.Artist : "Unknown";
        var duration = libSong.Duration > TimeSpan.Zero ? libSong.Duration : TimeSpan.FromSeconds(1);

        // The thumbnail is resolved for the web UI via GetThumbnailForSongPath; the
        // platform thumbnail stays null so this stays WPF-free.
        return new Song(title, artist, null, duration, libSong.FilePath);
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

        PostToUi(() =>
        {
            try
            {
                var mp = _ytForm.Music;
                if (mp == null) { System.Diagnostics.Debug.WriteLine("PlayLibrarySong: MusicPlayer is null"); return; }
                mp.ClearQueue();
                var song = CreateSongFromLibrary(libSong);
                mp.Queue.Add(song);
                mp.ComputeQueueTimings();
                mp.PlaySong(mp.Queue[0]);
                System.Diagnostics.Debug.WriteLine($"PlayLibrarySong: Playing '{song.Title}' by '{song.Artist}' from {song.songPath}");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PlayLibrarySong FAILED: {ex}"); }
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

        PostToUi(() =>
        {
            try
            {
                var mp = _ytForm.Music;
                if (mp == null) { System.Diagnostics.Debug.WriteLine("QueueLibrarySong: MusicPlayer is null"); return; }
                var song = CreateSongFromLibrary(libSong);
                mp.AddSongExternal(song);
                System.Diagnostics.Debug.WriteLine($"QueueLibrarySong: Queued '{song.Title}' by '{song.Artist}' from {song.songPath}");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"QueueLibrarySong FAILED: {ex}"); }
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

        PostToUi(() =>
        {
            try
            {
                var mp = _ytForm.Music;
                if (mp == null) { System.Diagnostics.Debug.WriteLine("PlayLibrarySongNext: MusicPlayer is null"); return; }
                var song = CreateSongFromLibrary(libSong);
                mp.AddNextSongExternal(song);
                System.Diagnostics.Debug.WriteLine($"PlayLibrarySongNext: Added next '{song.Title}' by '{song.Artist}' from {song.songPath}");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PlayLibrarySongNext FAILED: {ex}"); }
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
        var downloadPath = AppPaths.DownloadPath;

        // Same background-download reporting as the request pipeline, so a host can
        // keep the process alive while this download runs.
        DownloadActivityChanged?.Invoke(true, string.IsNullOrWhiteSpace(title) ? videoId : title);

        void SendMpEvent(string eventName, object data)
        {
            try
            {
                var mpSend = _ytForm.NewUiRef?.MusicPlayerSendMessage;
                if (mpSend == null) return;
                var json = JsonConvert.SerializeObject(new { type = "event", eventName, data });
                mpSend(json);
            }
            catch { }
        }

        try
        {
            if (!Directory.Exists(downloadPath))
                Directory.CreateDirectory(downloadPath);

            SendMpEvent("ytDownloadProgress", new { videoId, stage = "downloading", progress = 0 });

            var progress = new Progress<double>(pct =>
            {
                DownloadProgressChanged?.Invoke(pct);
                SendMpEvent("ytDownloadProgress", new { videoId, stage = "downloading", progress = (int)(pct * 100) });
            });

            string filePath;
            try
            {
                filePath = await _ytService.DownloadVideoWithProgressAsync(videoUrl, downloadPath, progress);
            }
            finally
            {
                // The bytes are on disk: the host can stop keeping the process alive.
                DownloadActivityChanged?.Invoke(false, string.IsNullOrWhiteSpace(title) ? videoId : title);
            }

            SendMpEvent("ytDownloadProgress", new { videoId, stage = "importing", progress = 100 });

            var youTubeId = YoutubeService.ExtractVideoId(videoUrl);
            var libSong = _libraryService.ImportDownloadedSong(filePath, youTubeId, videoUrl, title, author);

            if (libSong != null)
            {
                if (!string.IsNullOrWhiteSpace(title))
                    libSong.Title = title;
                if (!string.IsNullOrWhiteSpace(author))
                    libSong.Artist = author;

                try
                {
                    var thumbBytes = await GetOrDownloadThumbnailBytesAsync(youTubeId);
                    if (thumbBytes != null && thumbBytes.Length > 0)
                    {
                        var thumbPath = _libraryService.SaveSongThumbnail(libSong.Id, thumbBytes);
                        libSong.ThumbnailPath = thumbPath;
                    }
                }
                catch { }

                _libraryService.Save();
            }

            SendMpEvent("ytDownloadProgress", new { videoId, stage = "playing", progress = 100 });

            if (libSong == null || !System.IO.File.Exists(libSong.FilePath))
            {
                System.Diagnostics.Debug.WriteLine($"DownloadAndPlayYouTube: Import failed - libSong is null: {libSong == null}, file exists: {libSong != null && System.IO.File.Exists(libSong.FilePath)}");
                return new { error = "Import failed" };
            }

            string songId = libSong.Id;
            System.Diagnostics.Debug.WriteLine($"DownloadAndPlayYouTube: {action} songId={songId}, title='{libSong.Title}', artist='{libSong.Artist}', filePath='{libSong.FilePath}', fileExists={System.IO.File.Exists(libSong.FilePath)}");

            if (action == "play")
                PlayLibrarySong(new JObject { ["songId"] = songId });
            else if (action == "playNext")
                PlayLibrarySongNext(new JObject { ["songId"] = songId });
            else if (action == "queue")
                QueueLibrarySong(new JObject { ["songId"] = songId });

            SendMpEvent("ytDownloadProgress", new { videoId, stage = "done", progress = 100 });

            return new { success = true, songId, action };
        }
        catch (Exception ex)
        {
            SendMpEvent("ytDownloadProgress", new { videoId, stage = "error", progress = 0 });
            return new { error = ex.Message };
        }
    }

    // ────────────────────────────────────────────────────────
    //  Playlist interop
    // ────────────────────────────────────────────────────────

    private const string DynamicRandomMixId = "dynamic:random-mix";
    private const string DynamicMostPlayedId = "dynamic:most-played";
    private const string DynamicRecentlyAddedId = "dynamic:recently-added";

    private sealed record DynamicPlaylistDefinition(string Id, string Name, string Icon, string Description);

    private static readonly DynamicPlaylistDefinition[] DynamicPlaylists =
    {
        new(DynamicRandomMixId, "Random Mix", "fa-dice", "100 random songs from your library"),
        new(DynamicMostPlayedId, "Most Played", "fa-fire", "Your 100 most played songs"),
        new(DynamicRecentlyAddedId, "Recently Added", "fa-clock-rotate-left", "Your 100 newest songs"),
    };

    private static bool IsDynamicPlaylist(string? id)
        => !string.IsNullOrEmpty(id) && DynamicPlaylists.Any(p => p.Id == id);

    private List<LibrarySong> SelectDynamicSongs(string id, bool shuffle)
    {
        List<LibrarySong> available;
        lock (_libraryService.Data)
        {
            available = _libraryService.Data.Songs
                .Where(s => !s.IsMissing)
                .ToList();
        }

        IEnumerable<LibrarySong> selected = id switch
        {
            DynamicRandomMixId => available.OrderBy(_ => Random.Shared.Next()),
            DynamicMostPlayedId => available
                .OrderByDescending(s => s.PlayCount)
                .ThenByDescending(s => s.LastPlayedUtc ?? DateTime.MinValue),
            DynamicRecentlyAddedId => available.OrderByDescending(s => s.DateAddedUtc),
            _ => Enumerable.Empty<LibrarySong>(),
        };

        var songs = selected.Take(100).ToList();
        if (shuffle || id == DynamicRandomMixId)
            songs = songs.OrderBy(_ => Random.Shared.Next()).ToList();
        return songs;
    }

    private object BuildDynamicPlaylist(string id)
    {
        var definition = DynamicPlaylists.First(p => p.Id == id);
        var songs = new List<object>();
        foreach (var song in SelectDynamicSongs(id, shuffle: false))
        {
            songs.Add(new
            {
                entryId = "dynamic:" + song.Id,
                songId = song.Id,
                title = song.Title,
                artist = song.Artist,
                duration = song.Duration.TotalSeconds,
                durationDisplay = song.DurationDisplay,
                thumbnail = GetThumbnailForSongPath(song.FilePath),
                filePath = song.FilePath,
                isMissing = song.IsMissing
            });
        }

        return new
        {
            id = definition.Id,
            name = definition.Name,
            icon = definition.Icon,
            description = definition.Description,
            songCount = songs.Count,
            songs,
            isDynamic = true,
            canModify = false
        };
    }

    private object GetPlaylists()
    {
        var playlists = new List<object>();

        int presentCount;
        lock (_libraryService.Data)
            presentCount = _libraryService.Data.Songs.Count(s => !s.IsMissing);

        foreach (var dynamicPlaylist in DynamicPlaylists)
        {
            playlists.Add(new
            {
                id = dynamicPlaylist.Id,
                name = dynamicPlaylist.Name,
                icon = dynamicPlaylist.Icon,
                description = dynamicPlaylist.Description,
                songCount = Math.Min(100, presentCount),
                createdUtc = "",
                lastModifiedUtc = "",
                lastPlayedUtc = "",
                isDynamic = true
            });
        }

        foreach (var playlist in _libraryService.GetPlaylistsSummary())
        {
            playlist["isDynamic"] = false;
            playlists.Add(playlist);
        }

        return new { playlists };
    }

    private object GetPlaylist(JObject msg)
    {
        var playlistId = msg["playlistId"]?.ToString();
        if (string.IsNullOrEmpty(playlistId)) return new { error = "No playlistId" };

        if (IsDynamicPlaylist(playlistId))
            return BuildDynamicPlaylist(playlistId);

        var pl = _libraryService.GetPlaylist(playlistId);
        if (pl == null) return new { error = "Playlist not found" };

        var songs = new List<object>();
        foreach (var entry in pl.Entries.OrderBy(e => e.SortOrder))
        {
            var song = _libraryService.Data.FindSong(entry.SongId);
            if (song == null) continue;
            var thumb = GetThumbnailForSongPath(song.FilePath);
            songs.Add(new
            {
                entryId = entry.Id,
                songId = song.Id,
                title = song.Title,
                artist = song.Artist,
                duration = song.Duration.TotalSeconds,
                durationDisplay = song.DurationDisplay,
                thumbnail = thumb,
                filePath = song.FilePath,
                isMissing = song.IsMissing
            });
        }

        return new
        {
            id = pl.Id,
            name = pl.Name,
            icon = pl.Icon ?? "",
            songCount = songs.Count,
            songs,
            isDynamic = false,
            canModify = true
        };
    }

    private object CreatePlaylist(JObject msg)
    {
        var name = msg["name"]?.ToString() ?? "Untitled Playlist";
        var icon = msg["icon"]?.ToString();
        var emoji = msg["emoji"]?.ToString();
        var pl = _libraryService.CreatePlaylist(name, emoji, icon);
        return new
        {
            success = true,
            id = pl.Id,
            name = pl.Name,
            icon = pl.Icon ?? "",
            songCount = 0
        };
    }

    private object AddSongToPlaylist(JObject msg)
    {
        var playlistId = msg["playlistId"]?.ToString();
        var songId = msg["songId"]?.ToString();
        if (string.IsNullOrEmpty(playlistId) || string.IsNullOrEmpty(songId))
            return new { error = "Missing playlistId or songId" };
        var pl = _libraryService.AddSongToPlaylist(playlistId, songId);
        return pl != null ? new { success = true, songCount = pl.Entries.Count } : new { error = "Playlist not found" };
    }

    private object RemovePlaylistEntry(JObject msg)
    {
        var playlistId = msg["playlistId"]?.ToString();
        var entryId = msg["entryId"]?.ToString();
        if (string.IsNullOrEmpty(playlistId) || string.IsNullOrEmpty(entryId))
            return new { error = "Missing playlistId or entryId" };
        return new { success = _libraryService.RemovePlaylistEntry(playlistId, entryId) };
    }

    private object ReorderPlaylistEntry(JObject msg)
    {
        var playlistId = msg["playlistId"]?.ToString();
        var entryId = msg["entryId"]?.ToString();
        var newOrder = msg["newSortOrder"]?.ToObject<int>() ?? 0;
        if (string.IsNullOrEmpty(playlistId) || string.IsNullOrEmpty(entryId))
            return new { error = "Missing playlistId or entryId" };
        return new { success = _libraryService.ReorderPlaylistEntry(playlistId, entryId, newOrder) };
    }

    private object DeletePlaylist(JObject msg)
    {
        var playlistId = msg["playlistId"]?.ToString();
        if (string.IsNullOrEmpty(playlistId)) return new { error = "No playlistId" };
        if (IsDynamicPlaylist(playlistId)) return new { error = "Dynamic playlists cannot be deleted" };
        return new { success = _libraryService.DeletePlaylist(playlistId) };
    }

    private object UpdatePlaylist(JObject msg)
    {
        var playlistId = msg["playlistId"]?.ToString();
        if (string.IsNullOrEmpty(playlistId)) return new { error = "No playlistId" };
        if (IsDynamicPlaylist(playlistId)) return new { error = "Dynamic playlists cannot be renamed" };
        var name = msg["name"]?.ToString();
        var emoji = msg["emoji"]?.ToString();
        var icon = msg["icon"]?.ToString();
        return new { success = _libraryService.UpdatePlaylist(playlistId, name, emoji, icon) };
    }

    private object PlayPlaylist(JObject msg)
    {
        var playlistId = msg["playlistId"]?.ToString();
        if (string.IsNullOrEmpty(playlistId)) return new { error = "No playlistId" };

        List<Song> songs;
        if (IsDynamicPlaylist(playlistId))
        {
            songs = SelectDynamicSongs(playlistId, shuffle: playlistId == DynamicRandomMixId)
                .Select(CreateSongFromLibrary)
                .ToList();
        }
        else
        {
            var playlist = _libraryService.GetPlaylist(playlistId);
            if (playlist == null) return new { error = "Playlist not found" };
            songs = ResolvePlaylistToSongs(playlist);
            if (songs.Count > 0) _libraryService.MarkPlaylistPlayed(playlist.Id);
        }

        if (songs.Count == 0) return new { error = "Playlist is empty" };

        PostToUi(() =>
        {
            var mp = _ytForm.Music;
            // One combined queue update for the whole playlist; playback starts
            // before that update is serialized.
            using (mp.BeginBulkUpdate())
            {
                mp.ClearQueue();
                mp.LoadPlaylist(songs, false);
                // "Play playlist" means start now, not just append to the playlist
                // buffer. Move the first track into the queue and begin playback.
                mp.AdvanceFromPlaylistQueue();
            }
        });
        return new { success = true, count = songs.Count };
    }

    private object QueuePlaylist(JObject msg)
    {
        var playlistId = msg["playlistId"]?.ToString();
        if (string.IsNullOrEmpty(playlistId)) return new { error = "No playlistId" };

        List<Song> songs;
        if (IsDynamicPlaylist(playlistId))
        {
            songs = SelectDynamicSongs(playlistId, shuffle: playlistId == DynamicRandomMixId)
                .Select(CreateSongFromLibrary)
                .ToList();
        }
        else
        {
            var playlist = _libraryService.GetPlaylist(playlistId);
            if (playlist == null) return new { error = "Playlist not found" };
            songs = ResolvePlaylistToSongs(playlist);
        }

        if (songs.Count == 0) return new { error = "Playlist is empty" };

        PostToUi(() =>
        {
            _ytForm.Music.AddSongs(songs);
        });
        return new { success = true, count = songs.Count };
    }

    private object ShufflePlaylist(JObject msg)
    {
        var playlistId = msg["playlistId"]?.ToString();
        if (string.IsNullOrEmpty(playlistId)) return new { error = "No playlistId" };

        List<Song> songs;
        if (IsDynamicPlaylist(playlistId))
        {
            songs = SelectDynamicSongs(playlistId, shuffle: true)
                .Select(CreateSongFromLibrary)
                .ToList();
        }
        else
        {
            var playlist = _libraryService.GetPlaylist(playlistId);
            if (playlist == null) return new { error = "Playlist not found" };
            songs = ResolvePlaylistToSongs(playlist);
            if (songs.Count > 0) _libraryService.MarkPlaylistPlayed(playlist.Id);
        }

        if (songs.Count == 0) return new { error = "Playlist is empty" };

        PostToUi(() =>
        {
            var mp = _ytForm.Music;
            using (mp.BeginBulkUpdate())
            {
                mp.ClearQueue();
                mp.LoadPlaylist(songs, true);
                mp.AdvanceFromPlaylistQueue();
            }
        });
        return new { success = true, count = songs.Count };
    }

    private object ClearPlaylistQueue(JObject msg)
    {
        PostToUi(() => _ytForm.Music.ClearPlaylistQueue());
        return new { success = true };
    }

    private List<Song> ResolvePlaylistToSongs(Playlist playlist)
    {
        var songs = new List<Song>(playlist.Entries.Count);

        // Index the library once instead of scanning every song for every entry:
        // a large playlist in a large library used to cost entries × songs string
        // comparisons before playback could even start.
        var byId = new Dictionary<string, LibrarySong>(StringComparer.Ordinal);
        lock (_libraryService.Data)
        {
            foreach (var s in _libraryService.Data.Songs)
                if (!string.IsNullOrEmpty(s.Id)) byId[s.Id] = s;
        }

        foreach (var entry in playlist.Entries)
        {
            if (!byId.TryGetValue(entry.SongId, out var libSong)) continue;

            songs.Add(new Song(
                libSong.Title ?? "Unknown",
                libSong.Artist ?? "Unknown",
                null,
                libSong.Duration,
                libSong.FilePath ?? ""
            ));
        }
        return songs;
    }

    // ────────────────────────────────────────────────────────
    //  MusicShare interop
    // ────────────────────────────────────────────────────────

    private void MusicShareSendEvent(string eventName, object? data)
    {
        try { EventRaised?.Invoke(eventName, data); } catch { }

        var mpSend = _ytForm.NewUiRef?.MusicPlayerSendMessage;
        if (mpSend == null) return;
        try
        {
            var json = JsonConvert.SerializeObject(new { type = "event", eventName, data });
            mpSend(json);
        }
        catch { }
    }

    /// <summary>
    /// Reports a start/connect failure to the share panel. The UI uses these
    /// events (not request responses) to restore its buttons, so returning only
    /// <c>{ error }</c> used to leave "Connecting..." disabled forever.
    /// </summary>
    private void MusicShareReportFailure(string error)
    {
        MusicShareSendEvent("musicShareStatus", new
        {
            status = "error",
            sharing = false,
            receiving = false,
            sessionId = _musicShareService?.SessionId,
            metadataOnly = _musicShareMetadataOnly
        });
        MusicShareSendEvent("musicShareError", new { error });
    }

    /// <summary>
    /// Extracts the base64 payload from a song's platform thumbnail. Thumbnails
    /// are stored as data-URL strings (e.g. "data:image/jpeg;base64,...").
    /// A plain file path is not artwork data and must not be sent as base64.
    /// </summary>
    private static string? ThumbnailToBase64(object? thumbnail)
    {
        if (thumbnail is not string s || string.IsNullOrWhiteSpace(s)) return null;

        var idx = s.IndexOf(";base64,", StringComparison.Ordinal);
        if (idx >= 0) return s.Substring(idx + 8);

        // Some sources hand over bare base64 instead of a data URL.
        try
        {
            return Convert.FromBase64String(s).Length > 0 ? s.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Artwork for the track being shared. <see cref="Song.thumbnail"/> is only
    /// populated for songs that were loaded with embedded art; library/queue songs
    /// rely on the library thumbnail or the YouTube thumbnail cache instead, which
    /// is why a share used to announce artwork it never sent.
    /// </summary>
    private string? ResolveMusicShareArtwork(Song? song)
    {
        if (song == null) return null;
        return ThumbnailToBase64(song.thumbnail)
            ?? ThumbnailToBase64(GetThumbnailForSongPath(song.songPath));
    }

    /// <summary>
    /// "Add downloads to the library automatically": applies to the automatic
    /// request pipeline as well as the manual download button.
    /// </summary>
    private void TryAutoAddDownloadedSong(string videoId, string filePath)
    {
        if (ConfigService.Instance.Current?.LibraryAutoAddDownloads != true) return;
        if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        try
        {
            var data = _ytForm.GetOrCreateVideoData(videoId);
            var videoUrl = $"https://www.youtube.com/watch?v={videoId}";

            var song = _libraryService.ImportDownloadedSong(
                filePath, videoId, videoUrl, data.Title, data.Creator);

            if (song == null) return;   // already in the library

            // Artwork from the shared cache (already downloaded for the list).
            _ = AttachLibraryArtworkAsync(song.Id, videoId);
            MusicPlayerSendEvent("libraryChanged", new { songId = song.Id });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Auto-add to library failed: {ex.Message}");
        }
    }

    private async Task AttachLibraryArtworkAsync(string songId, string videoId)
    {
        try
        {
            var bytes = await GetOrDownloadThumbnailBytesAsync(videoId).ConfigureAwait(false);
            if (bytes == null || bytes.Length == 0) return;

            var song = _libraryService.Data.FindSong(songId);
            if (song == null) return;

            song.ThumbnailPath = _libraryService.SaveSongThumbnail(songId, bytes);
            _libraryService.Save();
            MusicPlayerSendEvent("libraryChanged", new { songId });
        }
        catch
        {
            // Artwork is optional; the song is already in the library.
        }
    }

    /// <summary>
    /// Persists the presentation zoom (0.5–2.0) and tells the open presentation
    /// page about it. TV/HDMI clients pick it up from the stage server's state.
    /// </summary>
    public void SetPresentationZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, 0.5, 2.0);
        ConfigService.Instance.Update(cfg =>
        {
            cfg.PresentationZoom = zoom;
            cfg.PresentationZoomCustomized = true;
        });
        PresentationSendEvent("presentationSettings", new { presentationZoom = zoom });
    }

    private void PresentationSendEvent(string eventName, object? data)
    {
        var presSend = _ytForm.NewUiRef?.PresentationSendMessage;
        if (presSend == null) return;
        try
        {
            var json = JsonConvert.SerializeObject(new { type = "event", eventName, data });
            presSend(json);
        }
        catch { }
    }

    /// <summary>Request URL as a QR data URL (no CDN/network needed on the display).</summary>
    private static string? QrDataUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            using var generator = new global::QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(url, global::QRCoder.QRCodeGenerator.ECCLevel.Q);
            var png = new global::QRCoder.PngByteQRCode(data).GetGraphic(6);
            return "data:image/png;base64," + Convert.ToBase64String(png);
        }
        catch { return null; }
    }

    private void SendPresentationCurrentState()
    {
        var presSend = _ytForm.NewUiRef?.PresentationSendMessage;
        if (presSend == null) return;

        // Send persisted lyrics settings
        var cfg = ConfigService.Instance.Current;
        PresentationSendEvent("presentationSettings", new
        {
            lyricsFontScale = (double)(cfg?.LyricsFontScale ?? 1.0f),
            lyricsHidden = cfg?.LyricsHidden ?? false,
            requestUrl = cfg?.RequestUrl ?? "",
            // A data URL means the QR still renders when the machine has no
            // internet (the page otherwise loads qrcode.js from a CDN).
            requestQr = QrDataUrl(cfg?.RequestUrl),
            presentationSource = cfg?.PresentationSource ?? "player",
            presentationZoom = Math.Clamp(cfg?.PresentationZoom ?? 1.0, 0.5, 2.0)
        });

        var source = cfg?.PresentationSource ?? "player";

        if (source == "musicshare")
        {
            var lastMeta = _musicShareLastMetadata;
            if (lastMeta != null)
            {
                PresentationSendEvent("presentationUpdate", MusicSharePresentation.BuildNowPlaying(lastMeta));

                // Forced: a newly opened/reloaded page has nothing yet, even when
                // the dedupe key matches the last broadcast.
                SendMusicShareLyricsToPresentation(lastMeta, force: true);
            }
            else
            {
                // No share is connected: clear the player's old track instead of
                // leaving the previous source's metadata on screen.
                PresentationSendEvent("presentationUpdate", MusicSharePresentation.EmptyNowPlaying());
                PresentationSendEvent("presentationLyricsUpdate", MusicSharePresentation.BuildLyrics(new MusicShareMetadata()));
            }
            return;
        }

        // Default: player source
        Song? current = null;
        bool isPlaying = false;
        RunOnUi(() =>
        {
            var mp = _ytForm.Music;
            if (mp != null)
            {
                var q = mp.Queue;
                if (q != null && q.Count > 0) current = q[0];
                isPlaying = mp.IsPlaying;
            }
        });

        var updateData = new Dictionary<string, object>
        {
            ["currentTime"] = 0,
            ["totalTime"] = 0,
            ["isPlaying"] = isPlaying
        };

        if (current != null)
        {
            updateData["title"] = current.Title ?? "";
            updateData["artist"] = current.Artist ?? "";
            updateData["duration"] = current.length ?? "";

            var thumb = GetThumbnailForSongPath(current.songPath);
            if (thumb != null)
                updateData["thumbnail"] = thumb;
        }
        else
        {
            // Empty player: clear whatever the other source left on screen.
            updateData["title"] = "";
            updateData["artist"] = "";
            updateData["duration"] = "";
        }

        PresentationSendEvent("presentationUpdate", updateData);

        // Send initial queue preview
        SendPresentationQueueUpdate();

        // Send current lyrics
        if (current != null)
        {
            _ = FetchAndSendLyricsForSongPresentationAsync(current);
        }
        else
        {
            PresentationSendEvent("presentationLyricsUpdate", new
            {
                syncedLyrics = new List<object>(),
                hasSynced = false,
                provider = ""
            });
        }
    }

    private async Task FetchAndSendLyricsForSongPresentationAsync(Song song)
    {
        var presSend = _ytForm.NewUiRef?.PresentationSendMessage;
        if (presSend == null) return;

        var lyricsService = _musicPlayerLyricsService ??= new LyricsService();
        var result = await ResolveLyricsAsync(song, lyricsService).ConfigureAwait(false);
        PresentationSendLyricsResult(result, song);
    }

    private void PresentationSendLyricsResult(LyricsResult result, Song? song = null)
    {
        var provider = result.Provider switch
        {
            LyricsProvider.FileEmbedded => "Embedded",
            LyricsProvider.LrcFile => "LRC File",
            LyricsProvider.LRCLIB => "LRCLIB",
            LyricsProvider.YouTubeCaptions => "YouTube captions",
            _ => ""
        };
        var data = new Dictionary<string, object>();
        var syncedLines = result.Found && result.HasSynced
            ? result.ParseSyncedLines()
            : new List<(TimeSpan Time, string Text)>();

        if (syncedLines.Count > 0)
        {
            data["syncedLyrics"] = syncedLines.Select(l => new { time = l.Time.TotalSeconds, text = l.Text }).ToList();
            data["hasSynced"] = true;
            var karaoke = BuildKaraokePayload(song, syncedLines);
            if (karaoke != null) data["karaoke"] = karaoke;
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
        }
        else
        {
            data["hasSynced"] = false;
        }

        data["provider"] = provider;
        data["karaokeMode"] = ConfigService.Instance.Current?.KaraokeMode == true;
        var presentationChapters = ChaptersForSong(song);
        var presentationChaptersJson = presentationChapters == null ? null : CamelCase(presentationChapters);
        if (presentationChaptersJson != null) data["chapters"] = presentationChaptersJson;

        PresentationSendEvent("presentationLyricsUpdate", data);
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
        _musicShareLastMetadata = metadata;

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

        // Forward to presentation if source is musicshare
        var source = ConfigService.Instance.Current?.PresentationSource ?? "player";
        if (source == "musicshare")
        {
            PresentationSendEvent("presentationUpdate", MusicSharePresentation.BuildNowPlaying(metadata));

            // Lyrics arrive a moment after the track (the sharer fetches them
            // asynchronously), so the presentation must be told about them here,
            // not only when it first asks for the current state.
            SendMusicShareLyricsToPresentation(metadata, force: false);
        }
    }

    /// <summary>
    /// Sends a received share's lyrics to the presentation window using the same
    /// field names as the player source. Deduplicated because the receiver polls
    /// metadata every 500 ms and re-rendering would reset the scroll position.
    /// </summary>
    private void SendMusicShareLyricsToPresentation(MusicShareMetadata metadata, bool force)
    {
        var lyrics = metadata.Lyrics ?? string.Empty;
        var key = (metadata.Title ?? string.Empty) + "\u001f" +
                  (metadata.Artist ?? string.Empty) + "\u001f" +
                  metadata.HasSyncedLyrics + "\u001f" + lyrics;

        if (!force && string.Equals(key, _musicSharePresentationLyricsKey, StringComparison.Ordinal))
            return;

        _musicSharePresentationLyricsKey = key;

        PresentationSendEvent("presentationLyricsUpdate", MusicSharePresentation.BuildLyrics(metadata));
    }

    private async void MusicShare_OnNowPlayingTick(object? sender, NowPlayingEventArgs e)
    {
        if (!_musicShareSharing || _musicShareService == null) return;
        try
        {
            if (_musicShareLastSong != e.Current)
            {
                _musicShareLastSong = e.Current;
                _musicShareCachedThumbnail = ResolveMusicShareArtwork(e.Current);
                // Artwork may still be converting in the background. Retry briefly
                // instead of keeping "no artwork" for the whole song.
                _musicShareThumbnailRetries = _musicShareCachedThumbnail == null ? 0 : -1;
                _musicShareThumbnailRetryAt = DateTime.UtcNow.AddSeconds(2);

                _musicShareCachedLyrics = null;
                _musicShareCachedHasSyncedLyrics = false;

                var requestedSong = e.Current;
                if (requestedSong != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var lyricsResult = await (_musicShareLyricsService ?? new LyricsService()).GetLyricsAsync(
                                requestedSong.Artist.Replace(" - Topic", ""),
                                requestedSong.Title,
                                requestedSong.Duration);

                            // The track may have changed while LRCLIB was answering.
                            if (!_musicShareSharing || !ReferenceEquals(_musicShareLastSong, requestedSong))
                                return;

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
            else if (_musicShareCachedThumbnail == null &&
                     _musicShareThumbnailRetries >= 0 && _musicShareThumbnailRetries < 10 &&
                     DateTime.UtcNow >= _musicShareThumbnailRetryAt)
            {
                _musicShareThumbnailRetryAt = DateTime.UtcNow.AddSeconds(3);
                _musicShareThumbnailRetries++;
                _musicShareCachedThumbnail = ResolveMusicShareArtwork(e.Current);
                if (_musicShareCachedThumbnail != null) _musicShareThumbnailRetries = -1;
            }

            var metadata = new MusicShareMetadata
            {
                Title = e.Current?.Title ?? "Unknown",
                Artist = e.Current?.Artist ?? "Unknown",
                ElapsedSeconds = e.CurrentTime.TotalSeconds,
                TotalSeconds = e.TotalTime.TotalSeconds,
                ThumbnailData = _musicShareCachedThumbnail,
                Lyrics = _musicShareCachedLyrics ?? string.Empty,
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
        {
            MusicShareReportFailure("Already sharing or receiving");
            return new { error = "Already sharing or receiving" };
        }

        var mp = _ytForm.Music;
        if (mp == null)
        {
            MusicShareReportFailure("Music Player not open");
            return new { error = "Music Player not open" };
        }

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
            _musicShareCachedHasSyncedLyrics = false;
            _musicShareCachedThumbnail = null;
            _musicShareThumbnailRetries = -1;
            _musicSharePresentationLyricsKey = null;
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
            MusicShareReportFailure(ex.Message);
            return new { error = ex.Message };
        }
    }

    private async Task<object> MusicShareStop()
    {
        if (!_musicShareSharing) return new { error = "Not sharing" };

        try
        {
            var mp = _ytForm.Music;
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
            _musicShareCachedHasSyncedLyrics = false;
            _musicShareCachedThumbnail = null;
            _musicShareThumbnailRetries = -1;
            _musicSharePresentationLyricsKey = null;
            _musicShareLastMetadata = null;

            MusicShareSendEvent("musicShareStatus", new
            {
                status = "idle",
                sharing = false,
                receiving = false
            });

            // A share page must not keep showing a track from the ended session.
            if ((ConfigService.Instance.Current?.PresentationSource ?? "player") == "musicshare")
                SendPresentationCurrentState();

            return new { success = true };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    private async Task<object> MusicShareConnect(JObject msg)
    {
        if (_musicShareSharing || _musicShareReceiving)
        {
            MusicShareReportFailure("Already sharing or receiving");
            return new { error = "Already sharing or receiving" };
        }

        var sessionId = msg["sessionId"]?.ToString();
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length != 6)
        {
            MusicShareReportFailure("Session ID must be 6 digits");
            return new { error = "Session ID must be 6 digits" };
        }

        try
        {
            _musicShareService = new MusicShareService();
            _musicShareService.ReceiveAudio = msg["receiveAudio"]?.ToObject<bool>() ?? true;
            _musicShareService.StatusChanged += MusicShare_ServiceStatusChanged;
            _musicShareService.ErrorOccurred += MusicShare_ServiceErrorOccurred;
            _musicShareService.MetadataReceived += MusicShare_ServiceMetadataReceived;

            // Set up received-audio playback when the receiver requested audio.
            if (_musicShareService.ReceiveAudio)
            {
                _musicShareService.AudioChunkReceived += MusicShare_ServiceAudioChunkReceived;
                _musicShareAudioPlayer = new MusicShareAudioPlayer { Volume = _musicShareReceiveVolume };
                _musicShareAudioPlayer.Start();
            }

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
            TeardownMusicShareAudio();
            MusicShareReportFailure(ex.Message);
            return new { error = ex.Message };
        }
    }

    private void TeardownMusicShareAudio()
    {
        try { if (_musicShareService != null) _musicShareService.AudioChunkReceived -= MusicShare_ServiceAudioChunkReceived; } catch { }
        try { _musicShareAudioPlayer?.Dispose(); } catch { }
        _musicShareAudioPlayer = null;
    }

    private void MusicShare_ServiceAudioChunkReceived(object? sender, AudioChunk chunk)
    {
        _musicShareAudioPlayer?.Feed(chunk.Data, _musicShareService?.GetBufferLevel() ?? 0);
    }

    private object MusicShareSetVolume(JObject msg)
    {
        _musicShareReceiveVolume = (float)Math.Clamp(msg["value"]?.ToObject<double>() ?? 1.0, 0.0, 1.0);
        if (_musicShareAudioPlayer != null) _musicShareAudioPlayer.Volume = _musicShareReceiveVolume;
        return new { success = true, volume = _musicShareReceiveVolume };
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

            TeardownMusicShareAudio();
            _musicShareReceiving = false;
            _musicShareLastMetadata = null;
            _musicSharePresentationLyricsKey = null;

            MusicShareSendEvent("musicShareStatus", new
            {
                status = "idle",
                sharing = false,
                receiving = false
            });

            // A share page must not keep showing a track from the ended session.
            if ((ConfigService.Instance.Current?.PresentationSource ?? "player") == "musicshare")
                SendPresentationCurrentState();

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
            bytesSent = _musicShareBytesSent,
            receiveVolume = _musicShareReceiveVolume
        };
    }

    private async Task<object> GetPlayedSongs()
    {
        try
        {
            var cfg = ConfigService.Instance.Current;
            var address = cfg?.Address ?? "http://127.0.0.1:5000";
            using var client = BackendHttpClient.Create(cfg, TimeSpan.FromSeconds(10));

            var response = await client.GetAsync($"{address}/api/played-songs").ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var parsed = JObject.Parse(json);
            var songs = parsed["songs"]?.ToObject<List<object>>() ?? new List<object>();
            return new { success = true, songs };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task<object> ClearPlayedSongs()
    {
        try
        {
            var cfg = ConfigService.Instance.Current;
            var address = cfg?.Address ?? "http://127.0.0.1:5000";
            using var client = BackendHttpClient.Create(cfg, TimeSpan.FromSeconds(10));

            var response = await client.PostAsync($"{address}/api/played-songs/clear", null).ConfigureAwait(false);
            return new { success = response.IsSuccessStatusCode };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private void LogPlaybackAsync(string title, string artist, string platform = "songrequest")
    {
        Task.Run(async () =>
        {
            try
            {
                var cfg = ConfigService.Instance.Current;
                if (cfg == null || !cfg.LogPlayback) return;
                var address = cfg.Address ?? "http://127.0.0.1:5000";

                using var client = BackendHttpClient.Create(cfg, TimeSpan.FromSeconds(10));

                var payload = JsonConvert.SerializeObject(new { title, artist, platform, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                await client.PostAsync($"{address}/api/played-songs/add", content).ConfigureAwait(false);
            }
            catch { }
        });
    }
}
