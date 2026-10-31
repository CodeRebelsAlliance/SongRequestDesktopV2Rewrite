using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using System.Linq;

namespace SongRequestDesktopV2Rewrite
{
    public class Config : INotifyPropertyChanged
    {
        private int _fetchingTimer = 30;
        private int _threads = 5;
        private string _bearerToken = "Bearer Multicore2024SR";
        private string _address = "http://127.0.0.1:5000";
        private string _defaultSorting = "none";
        private bool _presentationFullscreen = false;
        private string _requestUrl = "https://example.com/request";
        private bool _normalizeVolume = false;
        private float _defaultVolume = -1f; // -1 means not set
        private bool _normalizationActive = false;
        private int _audioOutputDeviceNumber = -1;        private bool _autoEnqueue = false;
        private bool _useCaptionLyricsFallback = true;
        private bool _enableAnnouncements = true;
        private bool _useNewUI = false;
        private string _defaultSubmitMethod = "search";
        private double _announcementDimDb = 20.0;
        private bool _announcementPlaySound = true;
        private bool _announcementPushToTalk = true;
        private RemoteControlConfiguration _remoteControl = new RemoteControlConfiguration();
        private List<string> _libraryScanFolders = new List<string>();
        private List<string> _libraryAllowedExtensions = new List<string> { "mp3", "m4a", "wav", "flac", "ogg", "aac", "wma", "opus" };
        private bool _libraryAutoScanOnStartup = false;
        private bool _libraryAutoAddDownloads = true;
        private bool _libraryRemoveMissingOnScan = false;
        private bool _libraryRecommendationsEnabled = true;
        private bool _logPlayback = false;
        private float _lyricsFontScale = 1.0f;
        private bool _lyricsHidden = false;
        private string _presentationSource = "player";
        private double _presentationZoom = 1.0;
        private string _operatingMode = "SongRequests";
        private bool _autoStart = false;
        private bool _startMinimized = false;
        private bool _minimizeToTray = false;
        private bool _closeToTray = false;
        private bool _globalKeyboardShortcuts = false;
        private bool _toolPromptDismissed = false;
        private string _cookieBrowser = string.Empty;
        private bool _autoGrabCookies = false;
        private bool _systemMediaIntegration = true;
        private bool _autoAnalyzeSongs = true;
        private bool _autoAnalyzeLibraryOnAdd = true;
        private bool _autoAnalyzeLibraryOnAlgorithmUpdate = true;
        private bool _karaokeMode = false;
        private bool _playKaraokeVersions = false;

        // Local integration servers (HTTP/WebSocket API and MCP). Both are off by
        // default and bind to loopback only until the user explicitly changes it.
        private bool _apiEnabled = false;
        private string _apiBindAddress = "127.0.0.1";
        private int _apiPort = 8730;
        private string _apiToken = string.Empty;
        private bool _mcpEnabled = false;
        private string _mcpBindAddress = "127.0.0.1";
        private int _mcpPort = 8731;
        private string _mcpToken = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public int FetchingTimer
        {
            get => _fetchingTimer;
            set => SetField(ref _fetchingTimer, value);
        }

        public int Threads
        {
            get => _threads;
            set => SetField(ref _threads, value);
        }

        public string BearerToken
        {
            get => _bearerToken;
            set => SetField(ref _bearerToken, value);
        }

        public string Address
        {
            get => _address;
            set => SetField(ref _address, value);
        }

        public string DefaultSorting
        {
            get => _defaultSorting;
            set => SetField(ref _defaultSorting, value);
        }

        public bool PresentationFullscreen
        {
            get => _presentationFullscreen;
            set => SetField(ref _presentationFullscreen, value);
        }

        public string RequestUrl
        {
            get => _requestUrl;
            set => SetField(ref _requestUrl, value);
        }

        public bool NormalizeVolume
        {
            get => _normalizeVolume;
            set => SetField(ref _normalizeVolume, value);
        }

        public float DefaultVolume
        {
            get => _defaultVolume;
            set => SetField(ref _defaultVolume, value);
        }

        public bool NormalizationActive
        {
            get => _normalizationActive;
            set => SetField(ref _normalizationActive, value);
        }

        /// <summary>
        /// OpenAL output device used by the music player (-1 = the system default).
        /// Chosen with the speaker button in the player bar; applied live.
        /// </summary>
        public int AudioOutputDeviceNumber
        {
            get => _audioOutputDeviceNumber;
            set => SetField(ref _audioOutputDeviceNumber, value);
        }

        public bool AutoEnqueue
        {
            get => _autoEnqueue;
            set => SetField(ref _autoEnqueue, value);
        }

        public bool UseCaptionLyricsFallback
        {
            get => _useCaptionLyricsFallback;
            set => SetField(ref _useCaptionLyricsFallback, value);
        }

        public bool UseNewUI
        {
            get => _useNewUI;
            set => SetField(ref _useNewUI, value);
        }

        public string DefaultSubmitMethod
        {
            get => _defaultSubmitMethod;
            set => SetField(ref _defaultSubmitMethod, value);
        }

        public bool EnableAnnouncements
        {
            get => _enableAnnouncements;
            set => SetField(ref _enableAnnouncements, value);
        }

        public double AnnouncementDimDb
        {
            get => _announcementDimDb;
            set => SetField(ref _announcementDimDb, value);
        }

        public bool AnnouncementPlaySound
        {
            get => _announcementPlaySound;
            set => SetField(ref _announcementPlaySound, value);
        }

        public bool AnnouncementPushToTalk
        {
            get => _announcementPushToTalk;
            set => SetField(ref _announcementPushToTalk, value);
        }

        public RemoteControlConfiguration RemoteControl
        {
            get => _remoteControl;
            set => SetField(ref _remoteControl, RemoteControlConfiguration.Ensure(value));
        }

        public List<string> LibraryScanFolders
        {
            get => _libraryScanFolders;
            set => SetField(ref _libraryScanFolders, value ?? new List<string>());
        }

        public List<string> LibraryAllowedExtensions
        {
            get => _libraryAllowedExtensions;
            set => SetField(ref _libraryAllowedExtensions, value ?? new List<string> { "mp3", "m4a", "wav", "flac", "ogg", "aac", "wma", "opus" });
        }

        public bool LibraryAutoScanOnStartup
        {
            get => _libraryAutoScanOnStartup;
            set => SetField(ref _libraryAutoScanOnStartup, value);
        }

        public bool LibraryAutoAddDownloads
        {
            get => _libraryAutoAddDownloads;
            set => SetField(ref _libraryAutoAddDownloads, value);
        }

        public bool LibraryRemoveMissingOnScan
        {
            get => _libraryRemoveMissingOnScan;
            set => SetField(ref _libraryRemoveMissingOnScan, value);
        }

        public bool LibraryRecommendationsEnabled
        {
            get => _libraryRecommendationsEnabled;
            set => SetField(ref _libraryRecommendationsEnabled, value);
        }

        public bool LogPlayback
        {
            get => _logPlayback;
            set => SetField(ref _logPlayback, value);
        }

        public float LyricsFontScale
        {
            get => _lyricsFontScale;
            set => SetField(ref _lyricsFontScale, value);
        }

        public bool LyricsHidden
        {
            get => _lyricsHidden;
            set => SetField(ref _lyricsHidden, value);
        }

        public string PresentationSource
        {
            get => _presentationSource;
            set => SetField(ref _presentationSource, value);
        }

        /// <summary>
        /// Zoom factor for the presentation screen on a TV, an HDMI display and the
        /// in-app stage tab. Stored with the settings so every screen shows the same
        /// scale (a TV browser and the HDMI WebView cannot share storage).
        /// </summary>
        public double PresentationZoom
        {
            get => _presentationZoom;
            set => SetField(ref _presentationZoom, Math.Clamp(value, 0.5, 2.0));
        }

        /// <summary>
        /// Whether the user ever changed the presentation zoom. Mobile hosts use
        /// this to default to 50% (a phone's HDMI output is much smaller than the
        /// desktop window) without overriding a deliberate choice.
        /// </summary>
        public bool PresentationZoomCustomized { get; set; }

        /// <summary>
        /// The mode the UI is running in (a <see cref="StartupMode"/> name). Chosen
        /// at startup and switchable at runtime from Settings without a restart.
        /// </summary>
        public string OperatingMode
        {
            get => _operatingMode;
            set => SetField(ref _operatingMode, value);
        }

        /// <summary>Start the app automatically at login (see <see cref="AutoStart"/>).</summary>
        public bool AutoStart
        {
            get => _autoStart;
            set => SetField(ref _autoStart, value);
        }

        /// <summary>Start minimised (or hidden in the tray) — also settable with <c>--minimized</c>.</summary>
        public bool StartMinimized
        {
            get => _startMinimized;
            set => SetField(ref _startMinimized, value);
        }

        /// <summary>Hide to the tray instead of minimising (requires a tray icon).</summary>
        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set => SetField(ref _minimizeToTray, value);
        }

        /// <summary>Hide to the tray instead of closing (requires a tray icon).</summary>
        public bool CloseToTray
        {
            get => _closeToTray;
            set => SetField(ref _closeToTray, value);
        }

        /// <summary>Register the remote-control keybinds as system-wide global hotkeys.</summary>
        public bool GlobalKeyboardShortcuts
        {
            get => _globalKeyboardShortcuts;
            set => SetField(ref _globalKeyboardShortcuts, value);
        }

        /// <summary>Set once the user declines the "download FFmpeg / yt-dlp?" prompt.</summary>
        public bool ToolPromptDismissed
        {
            get => _toolPromptDismissed;
            set => SetField(ref _toolPromptDismissed, value);
        }

        /// <summary>
        /// Browser the user last imported YouTube cookies from. Used to pre-select the
        /// browser in the startup prompt and in Settings; empty means "not chosen yet".
        /// </summary>
        public string CookieBrowser
        {
            get => _cookieBrowser;
            set => SetField(ref _cookieBrowser, value ?? string.Empty);
        }

        /// <summary>
        /// When enabled, the desktop imports cookies from the selected browser
        /// automatically during startup/settings instead of waiting for a click.
        /// On mobile it makes the in-app YouTube sign-in press Done automatically
        /// once the logged-in YouTube cookies are present.
        /// </summary>
        public bool AutoGrabCookies
        {
            get => _autoGrabCookies;
            set => SetField(ref _autoGrabCookies, value);
        }

        /// <summary>
        /// Expose playback to the OS media controls (MPRIS on Linux, SMTC on
        /// Windows, MPNowPlaying on macOS) so the system shows the title, artwork
        /// and transport controls.
        /// </summary>
        public bool SystemMediaIntegration
        {
            get => _systemMediaIntegration;
            set => SetField(ref _systemMediaIntegration, value);
        }

        /// <summary>
        /// Analyze each track as it starts playing so its BPM, drops and song parts
        /// can be shown on the player progress bar without opening the analysis panel.
        /// Results are cached, so replaying a track costs nothing.
        /// </summary>
        public bool AutoAnalyzeSongs
        {
            get => _autoAnalyzeSongs;
            set => SetField(ref _autoAnalyzeSongs, value);
        }

        /// <summary>Analyze a library song automatically when it is imported/downloaded.</summary>
        public bool AutoAnalyzeLibraryOnAdd
        {
            get => _autoAnalyzeLibraryOnAdd;
            set => SetField(ref _autoAnalyzeLibraryOnAdd, value);
        }

        /// <summary>
        /// When a newer analysis algorithm is shipped, re-analyze library songs whose
        /// cached result came from an older version.
        /// </summary>
        public bool AutoAnalyzeLibraryOnAlgorithmUpdate
        {
            get => _autoAnalyzeLibraryOnAlgorithmUpdate;
            set => SetField(ref _autoAnalyzeLibraryOnAlgorithmUpdate, value);
        }

        /// <summary>
        /// Enables word-by-word lyric animation and animated pause bars. When off,
        /// the lyrics keep the classic line-by-line display.
        /// </summary>
        public bool KaraokeMode
        {
            get => _karaokeMode;
            set => SetField(ref _karaokeMode, value);
        }

        /// <summary>
        /// Play the generated instrumental/karaoke file instead of the original when
        /// one is available (and generate it first when missing).
        /// </summary>
        public bool PlayKaraokeVersions
        {
            get => _playKaraokeVersions;
            set => SetField(ref _playKaraokeVersions, value);
        }

        /// <summary>
        /// Enables the local HTTP + WebSocket API (off by default). The server starts
        /// immediately when this changes; no restart is needed.
        /// </summary>
        public bool ApiEnabled
        {
            get => _apiEnabled;
            set => SetField(ref _apiEnabled, value);
        }

        /// <summary>
        /// Interface the API server binds to. <c>127.0.0.1</c> keeps it on this
        /// machine; <c>0.0.0.0</c> exposes it to the LAN (set a token first).
        /// </summary>
        public string ApiBindAddress
        {
            get => _apiBindAddress;
            set => SetField(ref _apiBindAddress, string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim());
        }

        /// <summary>TCP port of the API server.</summary>
        public int ApiPort
        {
            get => _apiPort;
            set => SetField(ref _apiPort, Math.Clamp(value, 1, 65535));
        }

        /// <summary>
        /// Optional bearer token for the API. Empty means no authentication — only
        /// safe while the server stays bound to loopback.
        /// </summary>
        public string ApiToken
        {
            get => _apiToken;
            set => SetField(ref _apiToken, value?.Trim() ?? string.Empty);
        }

        /// <summary>Enables the MCP (Model Context Protocol) server (off by default).</summary>
        public bool McpEnabled
        {
            get => _mcpEnabled;
            set => SetField(ref _mcpEnabled, value);
        }

        /// <summary>Interface the MCP server binds to (loopback by default).</summary>
        public string McpBindAddress
        {
            get => _mcpBindAddress;
            set => SetField(ref _mcpBindAddress, string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim());
        }

        /// <summary>TCP port of the MCP server.</summary>
        public int McpPort
        {
            get => _mcpPort;
            set => SetField(ref _mcpPort, Math.Clamp(value, 1, 65535));
        }

        /// <summary>Optional bearer token for MCP clients. Empty disables authentication.</summary>
        public string McpToken
        {
            get => _mcpToken;
            set => SetField(ref _mcpToken, value?.Trim() ?? string.Empty);
        }

        /// <summary>
        /// Persisted 10-band equalizer curve (enabled flag, pre-amp, band gains and
        /// user presets). Shared by desktop and mobile through config.json.
        /// </summary>
        public EqualizerConfiguration Equalizer { get; set; } = new();

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value!;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }

    public sealed class ConfigService
    {
        private static readonly object _sync = new object();
        private readonly string _configFilePath;

        public Config Current { get; private set; }

        // Singleton instance for easy access
        public static ConfigService Instance { get; } = new ConfigService();

        private ConfigService()
            : this(GetDefaultConfigPath())
        {
        }

        /// <summary>Creates a config service bound to an explicit file (used by tests).</summary>
        internal ConfigService(string configFilePath)
        {
            _configFilePath = configFilePath;
            Current = LoadOrCreateConfig();
            SubscribeToAutoSave(Current);
        }

        private void SubscribeToAutoSave(Config? config)
        {
            if (config == null) return;
            config.PropertyChanged += (s, e) =>
            {
                try
                {
                    SaveConfig();
                }
                catch
                {
                    // swallowing exceptions here to avoid UI disruption; callers can explicitly call SaveConfig
                }
            };
        }

        private static string GetDefaultConfigPath()
            => Path.Combine(AppPaths.AppDataDir, "config.json");

        private Config LoadOrCreateConfig()
        {
            lock (_sync)
            {
                try
                {
                    if (!File.Exists(_configFilePath))
                    {
                        var def = new Config();
                        File.WriteAllText(_configFilePath, JsonConvert.SerializeObject(def, Formatting.Indented));
                        return def;
                    }

                    var json = File.ReadAllText(_configFilePath);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        var def = new Config();
                        File.WriteAllText(_configFilePath, JsonConvert.SerializeObject(def, Formatting.Indented));
                        return def;
                    }

                    var cfg = JsonConvert.DeserializeObject<Config>(json);
                    if (cfg == null)
                    {
                        cfg = new Config();
                    }

                    // Validate/patch missing values with defaults
                    var patched = false;
                    if (cfg.FetchingTimer <= 0) { cfg.FetchingTimer = 30; patched = true; }
                    if (cfg.Threads <= 0) { cfg.Threads = 5; patched = true; }
                    if (string.IsNullOrWhiteSpace(cfg.BearerToken)) { cfg.BearerToken = "DefaultToken"; patched = true; }
                    if (string.IsNullOrWhiteSpace(cfg.Address)) { cfg.Address = "http://127.0.0.1:5000"; patched = true; }
                    if (string.IsNullOrWhiteSpace(cfg.DefaultSorting)) { cfg.DefaultSorting = "none"; patched = true; }
                    if (string.IsNullOrWhiteSpace(cfg.RequestUrl)) { cfg.RequestUrl = "https://example.com/request"; patched = true; }
                    if (string.IsNullOrWhiteSpace(cfg.ApiBindAddress)) { cfg.ApiBindAddress = "127.0.0.1"; patched = true; }
                    if (cfg.ApiPort is < 1 or > 65535) { cfg.ApiPort = 8730; patched = true; }
                    if (string.IsNullOrWhiteSpace(cfg.McpBindAddress)) { cfg.McpBindAddress = "127.0.0.1"; patched = true; }
                    if (cfg.McpPort is < 1 or > 65535) { cfg.McpPort = 8731; patched = true; }
                    var ensuredRemote = RemoteControlConfiguration.Ensure(cfg.RemoteControl);
                    if (!ReferenceEquals(cfg.RemoteControl, ensuredRemote))
                    {
                        cfg.RemoteControl = ensuredRemote;
                        patched = true;
                    }

                    if (patched)
                    {
                        File.WriteAllText(_configFilePath, JsonConvert.SerializeObject(cfg, Formatting.Indented));
                    }

                    return cfg;
                }
                catch
                {
                    // If any problem reading config, create a default one
                    try
                    {
                        var def = new Config();
                        File.WriteAllText(_configFilePath, JsonConvert.SerializeObject(def, Formatting.Indented));
                        return def;
                    }
                    catch
                    {
                        // As a last resort, return in-memory defaults
                        return new Config();
                    }
                }
            }
        }

        public void SaveConfig()
        {
            lock (_sync)
            {
                var json = JsonConvert.SerializeObject(Current, Formatting.Indented);
                File.WriteAllText(_configFilePath, json);
            }
        }

        public void Reload()
        {
            lock (_sync)
            {
                Current = LoadOrCreateConfig();
                SubscribeToAutoSave(Current);
            }
        }

        /// <summary>
        /// Helper to update the config in a thread-safe manner and persist immediately.
        /// </summary>
        public void Update(Action<Config> updater)
        {
            if (updater == null) return;
            lock (_sync)
            {
                updater(Current);
                SaveConfig();
            }
        }
    }
}
