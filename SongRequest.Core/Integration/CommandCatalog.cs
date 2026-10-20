using System;
using System.Collections.Generic;
using System.Linq;

namespace SongRequestDesktopV2Rewrite.Integration;

/// <summary>
/// One command exposed by the web-UI bridge and therefore by the local API and
/// MCP server. <see cref="Parameters"/> is documentation for the JSON properties
/// the command understands; it is not a validation schema.
/// </summary>
public sealed record CommandInfo(
    string Name,
    string Category,
    string Description,
    string[] Parameters,
    bool Dangerous = false);

/// <summary>
/// Machine-readable catalogue of every command the application accepts. The API
/// publishes it at <c>/api/v1/capabilities</c>, the MCP server exposes it through
/// the <c>get_capabilities</c> tool and <c>songrequest://capabilities</c>
/// resource, and <c>run_songrequest_command</c> accepts any name from it.
/// </summary>
public static class CommandCatalog
{
    private static readonly List<CommandInfo> Commands = Build();

    public static IReadOnlyList<CommandInfo> All => Commands;

    public static CommandInfo? Find(string? name) =>
        name == null
            ? null
            : Commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    public static IReadOnlyList<string> Names => Commands.Select(c => c.Name).ToArray();

    private static List<CommandInfo> Build()
    {
        var list = new List<CommandInfo>();

        void Add(string name, string category, string description, string[]? parameters = null, bool dangerous = false)
            => list.Add(new CommandInfo(name, category, description, parameters ?? Array.Empty<string>(), dangerous));

        // ── Windows and UI ────────────────────────────────────────────────
        Add("showMusicPlayer", "Windows", "Open the music player window or tab.", null);
        Add("closeMusicPlayer", "Windows", "Close the music player window or tab.", null);
        Add("showPresentation", "Windows", "Open the presentation window.", null);
        Add("closePresentation", "Windows", "Close the presentation window.", null);
        Add("presentationReady", "Windows", "Tell the app the presentation page finished loading and push its current state.", null);
        Add("showVisualization", "Windows", "Open the audio visualization window.", null);
        Add("closeVisualization", "Windows", "Close the visualization window.", null);
        Add("showSoundboard", "Windows", "Open the soundboard window or tab.", null);
        Add("soundboardClose", "Windows", "Close the soundboard window or tab.", null);
        Add("setFullscreen", "Windows", "Enter or leave fullscreen for a named sub-window.", new[] { "window", "enabled" });
        Add("setOperatingMode", "Windows", "Switch the app's operating mode live.", new[] { "mode" }, true);
        Add("stageStatus", "Stage", "Read the mobile stage (LAN presentation + HDMI mirror) status.", null);
        Add("stageStart", "Stage", "Start the mobile stage presentation server and HDMI mirror.", null);
        Add("stageStop", "Stage", "Stop the mobile stage presentation server.", null);

        // ── Playback ──────────────────────────────────────────────────────
        Add("playPause", "Playback", "Toggle playback.", null);
        Add("skipNext", "Playback", "Play the next queue item.", null);
        Add("skipPrevious", "Playback", "Go back to the previous queue item.", null);
        Add("stop", "Playback", "Stop playback.", null);
        Add("seek", "Playback", "Seek within the current track (normalized 0..1).", new[] { "position" });
        Add("toggleRepeat", "Playback", "Toggle repeat mode.", null);
        Add("setVolume", "Playback", "Set the player volume (0..1).", new[] { "value" });
        Add("setCrossfade", "Playback", "Set the crossfade duration in seconds.", new[] { "value" });
        Add("getVolumeCrossfade", "Playback", "Read volume, crossfade and whether volume is controllable.", null);
        Add("getMusicAudioDevices", "Playback", "List audio output devices.", null);
        Add("setMusicOutputDevice", "Playback", "Select the music player's audio output device (-1 = system default).", new[] { "deviceNumber" });
        Add("saveLyricsSettings", "Playback", "Persist lyrics font scale and visibility.", new[] { "fontScale", "hidden" });
        Add("requestLyrics", "Playback", "Fetch and send lyrics for the current song.", null);
        Add("musicPlayerReady", "Playback", "Signal that the music player page is ready.", null);

        // ── Queue ─────────────────────────────────────────────────────────
        Add("shuffleQueue", "Queue", "Shuffle the play queue.", null);
        Add("clearQueue", "Queue", "Remove every song from the play queue.", null);
        Add("removeQueueItem", "Queue", "Remove one play-queue item by index.", new[] { "index" });
        Add("moveQueueItem", "Queue", "Move a play-queue item.", new[] { "fromIndex", "toIndex" });
        Add("removePlaylistQueueItem", "Queue", "Remove one playlist-queue item by index.", new[] { "index" });
        Add("movePlaylistQueueItem", "Queue", "Move a playlist-queue item.", new[] { "fromIndex", "toIndex" });
        Add("clearPlaylistQueue", "Playlist", "Clear the playlist queue.", null);
        Add("queueSong", "Queue", "Queue a downloaded song by YouTube id.", new[] { "ytid" });
        Add("playNext", "Queue", "Insert a downloaded song after the current one.", new[] { "ytid" });
        Add("playLibrarySong", "Queue", "Play a library song now.", new[] { "songId" });
        Add("queueLibrarySong", "Queue", "Append a library song to the queue.", new[] { "songId" });
        Add("playLibrarySongNext", "Queue", "Insert a library song after the current one.", new[] { "songId" });
        Add("downloadAndPlayYouTube", "Queue", "Download a YouTube video and play or queue it.", new[] { "videoUrl", "action" });
        Add("downloadVideo", "Queue", "Download a YouTube video without queueing it.", new[] { "videoUrl" });
        Add("downloadAndProcess", "Queue", "Download a request and run a queue action on it.", new[] { "ytid", "action" });
        Add("isFileDownloaded", "Queue", "Check whether a YouTube video is already downloaded.", new[] { "ytid" });
        Add("addLocalFiles", "Queue", "Open the local-file picker and add the chosen files to the queue.", new[] { "mode" }, true);
        Add("importLibrary", "Library", "Open the platform picker and import music into the library.", new[] { "mode" }, true);

        // ── Requests and moderation ───────────────────────────────────────
        Add("fetchData", "Requests", "Refresh the request/send-in list and blacklist from the companion server.", null);
        Add("approve", "Requests", "Approve a request.", new[] { "ytid" });
        Add("unapprove", "Requests", "Withdraw approval from a request.", new[] { "ytid" });
        Add("delete", "Requests", "Delete a request.", new[] { "ytid" }, true);
        Add("blacklist", "Requests", "Add a video to the server blacklist.", new[] { "ytid" }, true);
        Add("unblacklist", "Requests", "Remove a video from the server blacklist.", new[] { "ytid" });
        Add("getBadWords", "Requests", "List the server's blocked words.", null);
        Add("addBadWord", "Requests", "Add a blocked word.", new[] { "word" });
        Add("deleteBadWord", "Requests", "Remove a blocked word.", new[] { "word" });
        Add("submitSong", "Requests", "Submit a song request to the companion server.", new[] { "videoId", "message" });
        Add("toggleSendin", "Requests", "Toggle whether the server accepts new song requests.", null);
        Add("getSendinStatus", "Requests", "Read the server's send-in permission.", null);
        Add("getLyrics", "YouTube", "Get lyrics for a video.", new[] { "videoId", "videoUrl" });
        Add("searchLyrics", "YouTube", "Search the lyrics provider.", new[] { "query" });
        Add("getThumbnail", "YouTube", "Get a video thumbnail as a data URL.", new[] { "videoId" });
        Add("searchYoutube", "YouTube", "Search YouTube.", new[] { "query" });

        // ── Library ───────────────────────────────────────────────────────
        Add("scanLibrary", "Library", "Scan all configured library folders.", null);
        Add("cancelLibraryScan", "Library", "Cancel a running library scan.", null);
        Add("getLibraryStatus", "Library", "Read library status and scan configuration.", null);
        Add("getLibrarySongs", "Library", "Page through library songs.", new[] { "page", "pageSize", "sortBy", "descending", "filterMissing", "filterDuplicates" });
        Add("searchLibrary", "Library", "Search library songs.", new[] { "query", "maxResults" });
        Add("deleteLibrarySong", "Library", "Delete a library song.", new[] { "songId", "deleteFile" }, true);
        Add("updateLibrarySongMetadata", "Library", "Edit a library song's metadata.", new[] { "songId", "title", "artist", "album", "genre", "tags" });
        Add("addLibraryScanFolder", "Library", "Add a folder to the library scan list.", new[] { "folder" });
        Add("removeLibraryScanFolder", "Library", "Remove a folder from the library scan list.", new[] { "folder" });
        Add("importToLibrary", "Library", "Register an existing file or downloaded video in the library.", new[] { "filePath", "youTubeId", "videoUrl", "title", "artist" });
        Add("getLibraryStats", "Library", "Library totals, duplicates and sources.", null);
        Add("flagLibrarySongMissing", "Library", "Mark a library song's file as missing.", new[] { "songId" });
        Add("getHomeFeed", "Library", "Home feed: playlists, recommendations, recently played and new additions.", new[] { "count" });
        Add("getThumbnails", "Library", "Resolve artwork data URLs for library songs.", new[] { "songIds" });

        // ── Playlists ─────────────────────────────────────────────────────
        Add("getPlaylists", "Playlists", "List manual and dynamic playlists.", null);
        Add("getPlaylist", "Playlists", "Read one playlist with its songs.", new[] { "playlistId" });
        Add("createPlaylist", "Playlists", "Create a playlist. The icon is a Font Awesome class such as fa-list or fa-fire.", new[] { "name", "icon", "emoji" });
        Add("addSongToPlaylist", "Playlists", "Add a library song to a playlist.", new[] { "playlistId", "songId" });
        Add("removePlaylistEntry", "Playlists", "Remove an entry from a playlist.", new[] { "playlistId", "entryId" });
        Add("reorderPlaylistEntry", "Playlists", "Move a playlist entry.", new[] { "playlistId", "entryId", "newSortOrder" });
        Add("deletePlaylist", "Playlists", "Delete a playlist.", new[] { "playlistId" }, true);
        Add("updatePlaylist", "Playlists", "Rename a playlist or change its icon (Font Awesome class, e.g. fa-list).", new[] { "playlistId", "name", "icon", "emoji" });
        Add("playPlaylist", "Playlists", "Play a playlist now.", new[] { "playlistId", "shuffle" });
        Add("queuePlaylist", "Playlists", "Append a playlist to the playlist queue.", new[] { "playlistId" });
        Add("shufflePlaylist", "Playlists", "Shuffle a playlist's resolved songs into the queue.", new[] { "playlistId" });

        // ── History ───────────────────────────────────────────────────────
        Add("getPlayHistory", "History", "Read local play history.", new[] { "count" });
        Add("removePlayHistoryEntry", "History", "Remove one local play-history entry.", new[] { "entryId" });
        Add("getPlayedSongs", "History", "Read the server's played-songs list.", null);
        Add("clearPlayedSongs", "History", "Clear the server's played-songs list.", null, true);

        // ── Settings, auth and tools ──────────────────────────────────────
        Add("getSettings", "Settings", "Read the full application configuration.", null);
        Add("saveSettings", "Settings", "Update configuration values.", new[] { "settings" });
        Add("reloadConfig", "Settings", "Reload config.json from disk and apply it.", null);
        Add("config", "Settings", "Read the small fetch configuration used by the main page.", null);
        Add("getRemoteConfig", "MIDI", "Read keybind and MIDI mappings plus device lists.", null);
        Add("saveRemoteConfig", "MIDI", "Save keybinds and MIDI mappings.", new[] { "midiEnabled", "midiInputDevice", "midiOutputDevice", "keybinds", "midis" });
        Add("applyMidiDevices", "MIDI", "Apply MIDI input/output devices live.", new[] { "midiEnabled", "midiInputDevice", "midiOutputDevice" });
        Add("testMidiFeedback", "MIDI", "Send a feedback test to the configured MIDI output.", null);
        Add("startMidiCapture", "MIDI", "Start capturing the next MIDI message for mapping.", null);
        Add("stopMidiCapture", "MIDI", "Stop the MIDI capture.", null);
        Add("getStartupData", "Settings", "Read startup-flow state and available choices.", null);
        Add("saveLaunchOptions", "Settings", "Persist launch/startup choices.", new[] { "remember", "mode", "openSoundboard", "openVisualization" });
        Add("getAuthBrowsers", "Authentication", "List browsers available for YouTube cookie import.", null);
        Add("openAuthUrl", "Authentication", "Open the SchülerApp authentication URL in the system browser.", null, true);
        Add("triggerAuth", "Authentication", "Run the authentication trigger flow.", null, true);
        Add("signIn", "Authentication", "Start the platform's in-app YouTube sign-in.", null, true);
        Add("grabCookies", "Authentication", "Import YouTube cookies from a browser with yt-dlp.", new[] { "browser" }, true);
        Add("saveCookieBrowser", "Authentication", "Remember the browser used for cookie import.", new[] { "browser" });
        Add("submitCookies", "Authentication", "Save pasted YouTube cookies.", new[] { "cookies" }, true);
        Add("clearAuthCookies", "Authentication", "Delete saved YouTube cookies.", null, true);
        Add("completeSetup", "Settings", "Mark the startup/setup flow as finished.", new[] { "mode", "remember" });
        Add("getToolStatus", "Tools", "Read FFmpeg/FFprobe/yt-dlp availability.", null);
        Add("downloadTools", "Tools", "Download FFmpeg and/or yt-dlp into the app's tools folder.", new[] { "ffmpeg", "ytdlp" }, true);
        Add("dismissToolPrompt", "Tools", "Remember that the tool download prompt was declined.", null);
        Add("getVersion", "Settings", "Read the application version.", null);
        Add("checkForUpdates", "Tools", "Check GitHub for a newer release.", null);
        Add("installUpdate", "Tools", "Download and install an available update.", null, true);
        Add("getIntegrationStatus", "Settings", "Read the API and MCP server status.", null);
        Add("quit", "Settings", "Quit SongRequest.", null, true);

        // ── Soundboard ────────────────────────────────────────────────────
        Add("soundboardGetState", "Soundboard", "Read the soundboard pages, buttons, volumes and MIDI state.", null);
        Add("soundboardPlay", "Soundboard", "Play one soundboard button.", new[] { "row", "col" });
        Add("soundboardStopAll", "Soundboard", "Stop every playing soundboard clip.", null);
        Add("soundboardSetMasterVolume", "Soundboard", "Set soundboard master volume (0..1).", new[] { "value" });
        Add("soundboardSetOutputDevice", "Soundboard", "Select the soundboard audio output device.", new[] { "deviceNumber" });
        Add("soundboardSetPage", "Soundboard", "Select a soundboard page.", new[] { "index" });
        Add("soundboardStepPage", "Soundboard", "Step the soundboard page forward or back.", new[] { "delta" });
        Add("soundboardAddPage", "Soundboard", "Add a soundboard page.", new[] { "name", "columns", "rows" });
        Add("soundboardDeletePage", "Soundboard", "Delete a soundboard page.", new[] { "index" }, true);
        Add("soundboardRenamePage", "Soundboard", "Rename a soundboard page.", new[] { "index", "name" });
        Add("soundboardMovePage", "Soundboard", "Move a soundboard page.", new[] { "index", "delta" });
        Add("soundboardSetGridSize", "Soundboard", "Change the current page's grid size.", new[] { "columns", "rows" });
        Add("soundboardBrowseFile", "Soundboard", "Pick a sound file for a button.", new[] { "row", "col" }, true);
        Add("soundboardUpdateButton", "Soundboard", "Edit a button's label, colour, file or volume.", new[] { "row", "col", "label", "color", "filePath", "volume", "playMode" });
        Add("soundboardDeleteButton", "Soundboard", "Clear a soundboard button.", new[] { "row", "col" }, true);
        Add("soundboardClearMidi", "Soundboard", "Clear a button's MIDI mapping.", new[] { "row", "col" });
        Add("soundboardSetMidiEnabled", "Soundboard", "Enable or disable soundboard MIDI control.", new[] { "enabled" });
        Add("soundboardSetMidiDevices", "Soundboard", "Choose soundboard MIDI devices.", new[] { "inputDevice", "outputDevice" });
        Add("soundboardSetAssignMode", "Soundboard", "Enable or disable MIDI learn mode.", new[] { "enabled" });
        Add("soundboardStartMidiAssign", "Soundboard", "Start MIDI learn for the selected button.", null);
        Add("soundboardSetButtonMidi", "Soundboard", "Assign a MIDI mapping to a button.", new[] { "row", "col", "channel", "note", "messageType", "velocityPressed", "velocityUnpressed" });
        Add("soundboardSetEmptyFeedback", "Soundboard", "Set the feedback velocity used for empty buttons.", new[] { "velocity" });
        Add("soundboardTestMidiFeedback", "Soundboard", "Test soundboard MIDI feedback LEDs.", null);
        Add("soundboardSaveKeyboardShortcuts", "Soundboard", "Save soundboard keyboard shortcuts.", new[] { "shortcuts" });
        Add("soundboardImportYoutube", "Soundboard", "Download a YouTube video into a soundboard button.", new[] { "url", "row", "col" }, true);

        // ── Announcement ──────────────────────────────────────────────────
        Add("announcementOpen", "Announcement", "Open the announcement window.", null);
        Add("announcementClose", "Announcement", "Close the announcement window.", null);
        Add("announcementGetState", "Announcement", "Read announcement state and settings.", null);
        Add("announcementStart", "Announcement", "Start the microphone announcement.", null);
        Add("announcementStop", "Announcement", "Stop the microphone announcement.", null);
        Add("announcementPress", "Announcement", "Push-to-talk press.", null);
        Add("announcementRelease", "Announcement", "Push-to-talk release.", null);
        Add("announcementSetDimDb", "Announcement", "Set music ducking depth in dB.", new[] { "value" });
        Add("announcementSetPlaySound", "Announcement", "Enable or disable the announcement chime.", new[] { "enabled" });
        Add("announcementSetPushToTalk", "Announcement", "Enable or disable push-to-talk.", new[] { "enabled" });

        // ── Music Share ───────────────────────────────────────────────────
        Add("musicShareStart", "Music Share", "Start hosting a Music Share session.", new[] { "metadataOnly", "port" });
        Add("musicShareStop", "Music Share", "Stop the Music Share session.", null);
        Add("musicShareConnect", "Music Share", "Connect to a Music Share session.", new[] { "host", "port" });
        Add("musicShareDisconnect", "Music Share", "Disconnect from a Music Share session.", null);
        Add("musicShareGetStatus", "Music Share", "Read Music Share state, peers and transfer counters.", null);
        Add("musicShareSetVolume", "Music Share", "Set the received Music Share volume (0..1).", new[] { "value" });

        // ── Presentation ──────────────────────────────────────────────────
        Add("setPresentationZoom", "Windows", "Set the presentation zoom (0.5..2.0).", new[] { "zoom" });
        Add("setPresentationSource", "Windows", "Choose what the presentation shows (player or music share).", new[] { "source" });

        return list
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(c => c.Category, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
    }
}
