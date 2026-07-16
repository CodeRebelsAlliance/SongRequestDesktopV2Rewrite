using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SongRequestDesktopV2Rewrite
{
    // ────────────────────────────────────────────────────────
    //  Enums
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Where the audio file originated from.
    /// </summary>
    public enum SongSource
    {
        /// <summary>User scanned a folder and the file was found locally.</summary>
        Local = 0,

        /// <summary>Downloaded from YouTube via the app.</summary>
        Downloaded = 1,

        /// <summary>Added by URL but the file lives outside the library folder.</summary>
        External = 2
    }

    /// <summary>
    /// Current state of a library entry's file on disk.
    /// </summary>
    public enum FileStatus
    {
        /// <summary>File exists and is playable.</summary>
        Present = 0,

        /// <summary>File was moved, renamed or deleted since last scan.</summary>
        Missing = 1,

        /// <summary>File exists but is corrupt or unreadable.</summary>
        Unreadable = 2
    }

    /// <summary>
    /// How a smart playlist sorts its results.
    /// </summary>
    public enum SmartSortField
    {
        DateAdded,
        LastPlayed,
        PlayCount,
        Title,
        Artist,
        Album,
        Duration,
        Random
    }

    // ────────────────────────────────────────────────────────
    //  Library Song (core entity)
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// A single track in the user's permanent library.
    /// <para>
    /// This is <b>not</b> the same as <see cref="Song"/> which is a
    /// lightweight runtime queue item.  LibrarySong is the persisted,
    /// canonical record that survives restarts and drives the
    /// Library / Playlist / Recommendation features.
    /// </para>
    /// </summary>
    public class LibrarySong
    {
        // ── Identity ──────────────────────────────────────

        /// <summary>Stable GUID, never reused.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// YouTube video ID when <see cref="Source"/> is <c>Downloaded</c>,
        /// <c>null</c> for local / external files.
        /// </summary>
        public string? YouTubeId { get; set; }

        // ── Metadata ──────────────────────────────────────

        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = "Unknown";
        public string Album { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }

        /// <summary>Free-form tags the user can attach (mood, activity …).</summary>
        public List<string> Tags { get; set; } = new();

        // ── File info ─────────────────────────────────────

        /// <summary>Absolute path to the audio file on disk.</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Path to the locally-cached thumbnail, if any.</summary>
        public string? ThumbnailPath { get; set; }

        /// <summary>SHA-256 hash of the file contents. Used for deduplication.</summary>
        public string FileHash { get; set; } = string.Empty;

        public long FileSizeBytes { get; set; }
        public int BitrateKbps { get; set; }
        public int SampleRateHz { get; set; }

        // ── Source tracking ───────────────────────────────

        public SongSource Source { get; set; }

        /// <summary>YouTube URL the file was downloaded from (null for Local).</summary>
        public string? SourceUrl { get; set; }

        // ── File health ───────────────────────────────────

        public FileStatus FileStatus { get; set; } = FileStatus.Present;

        /// <summary>
        /// Last time a file scan verified this entry still exists.
        /// Scanning sets this to <c>DateTime.UtcNow</c>.
        /// </summary>
        public DateTime LastVerifiedUtc { get; set; } = DateTime.UtcNow;

        // ── Duplicate detection ───────────────────────────

        /// <summary>
        /// IDs of other library songs that share the same title+artist+duration.
        /// Populated during scan. Empty = no metadata duplicates detected.
        /// </summary>
        public List<string> MetadataDuplicateIds { get; set; } = new();

        /// <summary>True if this entry's hash collides with another (only one kept).</summary>
        public bool IsHashDuplicate { get; set; }

        // ── Statistics ────────────────────────────────────

        public int PlayCount { get; set; }
        public DateTime DateAddedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastPlayedUtc { get; set; }

        /// <summary>Integrated loudness in LUFS.  -1 = not yet measured.</summary>
        public double PerceivedLoudness { get; set; } = -1;

        // ── Convenience ───────────────────────────────────

        [JsonIgnore] public bool IsMissing => FileStatus != FileStatus.Present;

        [JsonIgnore] public bool HasMetadataDuplicates => MetadataDuplicateIds.Count > 0;

        [JsonIgnore] public string DurationDisplay => Duration.ToString(@"mm\:ss");

        /// <summary>
        /// Returns <c>true</c> if the file can be read right now.
        /// </summary>
        public bool FileExists()
        {
            return FileStatus == FileStatus.Present && System.IO.File.Exists(FilePath);
        }

        /// <summary>
        /// Refresh <see cref="FileStatus"/> by checking the file on disk.
        /// Returns <c>true</c> if status changed.
        /// </summary>
        public bool RefreshFileStatus()
        {
            var prev = FileStatus;
            if (System.IO.File.Exists(FilePath))
            {
                FileStatus = FileStatus.Present;
                FileSizeBytes = new System.IO.FileInfo(FilePath).Length;
            }
            else
            {
                FileStatus = FileStatus.Missing;
            }
            return FileStatus != prev;
        }
    }

    // ────────────────────────────────────────────────────────
    //  Playlist
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// A user-created collection of <see cref="LibrarySong"/> references.
    /// <para>
    /// Playlists are either <b>manual</b> (user adds/removes songs) or
    /// <b>smart</b> (auto-populated from <see cref="SmartPlaylistRules"/>).
    /// Smart playlists recompute their item list every time they are opened.
    /// </para>
    /// </summary>
    public class Playlist
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Untitled Playlist";
        public string? Description { get; set; }
        public string? Emoji { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Ordered list of entries (manual playlists).</summary>
        public List<PlaylistEntry> Entries { get; set; } = new();

        /// <summary>
        /// When non-null the playlist is a <em>smart playlist</em> and
        /// <see cref="Entries"/> is the last-resolved snapshot.
        /// </summary>
        public SmartPlaylistRules? SmartRules { get; set; }

        [JsonIgnore] public bool IsSmart => SmartRules != null;

        [JsonIgnore] public int SongCount => Entries.Count;

        /// <summary>
        /// Snapshot of resolved song IDs (set by the service layer after
        /// evaluating smart rules).  Serialised alongside the playlist.
        /// </summary>
        public List<string> ResolvedSongIds { get; set; } = new();
    }

    /// <summary>
    /// One entry inside a <see cref="Playlist"/>.
    /// The same song can appear in a playlist more than once.
    /// </summary>
    public class PlaylistEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Foreign key → <see cref="LibrarySong.Id"/>.</summary>
        public string SongId { get; set; } = string.Empty;

        public int SortOrder { get; set; }
        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Optional note the user can attach (e.g. "intro for livestream").</summary>
        public string? Note { get; set; }
    }

    // ────────────────────────────────────────────────────────
    //  Smart Playlist Rules
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Declarative filter that populates a smart playlist from the
    /// entire library.  Every list field is an OR-set (match any);
    /// null means "don't filter on this field".
    /// </summary>
    public class SmartPlaylistRules
    {
        // ── Include / Exclude filters ─────────────────────

        /// <summary>Only include songs with at least one of these genres.</summary>
        public List<string>? IncludeGenres { get; set; }

        /// <summary>Exclude songs with any of these genres.</summary>
        public List<string>? ExcludeGenres { get; set; }

        /// <summary>Only include songs whose artist is in this list.</summary>
        public List<string>? IncludeArtists { get; set; }

        /// <summary>Exclude songs whose artist is in this list.</summary>
        public List<string>? ExcludeArtists { get; set; }

        /// <summary>Only include songs that have at least one of these tags.</summary>
        public List<string>? IncludeTags { get; set; }

        /// <summary>Exclude songs that have any of these tags.</summary>
        public List<string>? ExcludeTags { get; set; }

        // ── Range filters ─────────────────────────────────

        public TimeSpan? MinDuration { get; set; }
        public TimeSpan? MaxDuration { get; set; }
        public int? MinPlayCount { get; set; }
        public int? MaxPlayCount { get; set; }
        public DateTime? AfterDateAdded { get; set; }
        public DateTime? BeforeDateAdded { get; set; }
        public DateTime? AfterLastPlayed { get; set; }

        /// <summary>Include songs whose file is missing (default false).</summary>
        public bool IncludeMissing { get; set; } = false;

        /// <summary>Only include songs from a specific source.</summary>
        public SongSource? SourceFilter { get; set; }

        // ── Sort / Limit ──────────────────────────────────

        public SmartSortField SortBy { get; set; } = SmartSortField.DateAdded;
        public bool SortDescending { get; set; } = true;

        /// <summary>
        /// Maximum number of songs.  <c>null</c> or <c>0</c> = unlimited.
        /// </summary>
        public int? Limit { get; set; }
    }

    // ────────────────────────────────────────────────────────
    //  Play History (per-song event log)
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// A single playback event.  Used for statistics and the
    /// recommendation engine.
    /// </summary>
    public class PlayHistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Foreign key → <see cref="LibrarySong.Id"/>.</summary>
        public string SongId { get; set; } = string.Empty;

        public DateTime PlayedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>How many seconds the user actually listened to.</summary>
        public double DurationPlayedSeconds { get; set; }

        /// <summary>Did playback reach the end of the track?</summary>
        public bool Completed { get; set; }

        /// <summary>
        /// Why this song was played:
        /// <list type="bullet">
        ///   <item><c>"queue"</c> — user queued it manually</item>
        ///   <item><c>"playlist"</c> — played from a playlist</item>
        ///   <item><c>"recommendation"</c> — surfaced by the engine</item>
        ///   <item><c>"autopilot"</c> — auto-queue from sent-in songs</item>
        /// </list>
        /// </summary>
        public string Trigger { get; set; } = "queue";
    }

    // ────────────────────────────────────────────────────────
    //  Recommendation Models
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Aggregated artist listening statistics derived from
    /// <see cref="PlayHistoryEntry"/> records.
    /// </summary>
    public class ArtistStat
    {
        public string Artist { get; set; } = string.Empty;
        public int PlayCount { get; set; }
        public int CompletedCount { get; set; }
        public DateTime LastPlayedUtc { get; set; }

        /// <summary>0..1, how "into" this artist the user is right now.</summary>
        public double Affinity { get; set; }
    }

    /// <summary>
    /// Aggregated genre listening statistics.
    /// </summary>
    public class GenreStat
    {
        public string Genre { get; set; } = string.Empty;
        public int PlayCount { get; set; }
        public double Affinity { get; set; }
    }

    /// <summary>
    /// A single recommendation with a score explaining why it was suggested.
    /// </summary>
    public class Recommendation
    {
        public LibrarySong Song { get; set; } = null!;

        /// <summary>
        /// 0..1 composite score.  Higher = more relevant.
        /// </summary>
        public double Score { get; set; }

        /// <summary>Human-readable reason shown in the UI.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Which sub-signal contributed most to the score.
        /// <list type="bullet">
        ///   <item><c>"artist_affinity"</c> — user plays this artist a lot</item>
        ///   <item><c>"genre_affinity"</c> — matches preferred genre</item>
        ///   <item><c>"collaborative"</c> — similar listeners enjoy this</item>
        ///   <item><c>"fresh_discovery"</c> — high rating, not yet played</item>
        ///   <item><c>"recently_enjoyed"</c> — similar to recently completed tracks</item>
        /// </list>
        /// </summary>
        public string Signal { get; set; } = string.Empty;
    }

    // ────────────────────────────────────────────────────────
    //  Download Task (YouTube → Library)
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Represents a YouTube download that will be imported into the library.
    /// </summary>
    public class DownloadTask
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string YouTubeId { get; set; } = string.Empty;
        public string VideoTitle { get; set; } = string.Empty;
        public string VideoUrl { get; set; } = string.Empty;

        /// <summary>Desired metadata (may differ from YouTube title).</summary>
        public string DesiredTitle { get; set; } = string.Empty;
        public string DesiredArtist { get; set; } = string.Empty;
        public string DesiredAlbum { get; set; } = string.Empty;

        public DateTime RequestedUtc { get; set; } = DateTime.UtcNow;

        public DownloadState State { get; set; } = DownloadState.Pending;
        public double Progress { get; set; } // 0..1
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Once completed, the <see cref="LibrarySong.Id"/> of the
        /// newly created library entry.
        /// </summary>
        public string? ResultSongId { get; set; }
    }

    public enum DownloadState
    {
        Pending,
        Downloading,
        Converting,
        Importing,
        Completed,
        Failed
    }

    // ────────────────────────────────────────────────────────
    //  Library Scan Result
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Summary returned by a library folder scan.
    /// </summary>
    public class LibraryScanResult
    {
        public int FilesFound { get; set; }
        public int SongsAdded { get; set; }
        public int SongsUpdated { get; set; }
        public int SongsMissing { get; set; }
        public List<string> Errors { get; set; } = new();
        public DateTime ScannedUtc { get; set; } = DateTime.UtcNow;
    }

    // ────────────────────────────────────────────────────────
    //  Library Root Configuration
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Top-level configuration for the library feature.
    /// Stored in <c>data/library/library.json</c>.
    /// </summary>
    public class LibraryConfiguration
    {
        /// <summary>
        /// Folders the user wants scanned for audio.
        /// Paths are absolute.
        /// </summary>
        public List<string> ScanFolders { get; set; } = new();

        /// <summary>
        /// File extensions to include during a scan (without the dot).
        /// </summary>
        public List<string> AllowedExtensions { get; set; } = new()
        {
            "mp3", "m4a", "wav", "flac", "ogg", "aac", "wma", "opus"
        };

        /// <summary>
        /// When true, the library auto-scans on app startup.
        /// </summary>
        public bool AutoScanOnStartup { get; set; }

        /// <summary>
        /// When true, downloaded songs are automatically added to the library
        /// instead of just to the queue.
        /// </summary>
        public bool AutoAddDownloadsToLibrary { get; set; } = true;

        /// <summary>
        /// When true, missing files are removed from the library during scan.
        /// When false they are just flagged as <see cref="FileStatus.Missing"/>.
        /// </summary>
        public bool RemoveMissingOnScan { get; set; }
    }

    // ────────────────────────────────────────────────────────
    //  Library Data Store (serialised to disk)
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// The entire library state, persisted as a single JSON file
    /// at <c>data/library/library.json</c>.
    /// <para>
    /// For large libraries the service layer should maintain an
    /// in-memory index (Dictionary&lt;string, LibrarySong&gt;) and
    /// use this class only for serialisation.
    /// </para>
    /// </summary>
    public class LibraryData
    {
        public LibraryConfiguration Config { get; set; } = new();
        public List<LibrarySong> Songs { get; set; } = new();
        public List<Playlist> Playlists { get; set; } = new();
        public List<PlayHistoryEntry> PlayHistory { get; set; } = new();

        /// <summary>
        /// Cached artist statistics, rebuilt on scan or periodically.
        /// </summary>
        public List<ArtistStat> ArtistStats { get; set; } = new();

        /// <summary>
        /// Cached genre statistics.
        /// </summary>
        public List<GenreStat> GenreStats { get; set; } = new();

        public DateTime LastScanUtc { get; set; }
        public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;

        // ── Quick lookups (populated at load time) ────────

        [JsonIgnore] private Dictionary<string, LibrarySong>? _songIndex;

        /// <summary>
        /// Build an in-memory index for O(1) lookups by song ID.
        /// Call after loading from disk.
        /// </summary>
        public void RebuildIndex()
        {
            _songIndex = new Dictionary<string, LibrarySong>(Songs.Count);
            foreach (var song in Songs)
            {
                _songIndex[song.Id] = song;
            }
        }

        /// <summary>Get a song by ID.  O(1) after index is built.</summary>
        public LibrarySong? FindSong(string songId)
        {
            if (_songIndex == null) RebuildIndex();
            return _songIndex!.GetValueOrDefault(songId);
        }

        /// <summary>
        /// Search songs by title, artist, album, or tag.
        /// Uses case-insensitive substring matching.
        /// Returns up to <paramref name="maxResults"/>.
        /// </summary>
        public List<LibrarySong> Search(string query, int maxResults = 50)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<LibrarySong>();

            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var results = new List<LibrarySong>();

            foreach (var song in Songs)
            {
                if (song.IsMissing) continue;

                bool match = true;
                foreach (var term in terms)
                {
                    bool termMatch =
                        song.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        song.Artist.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        song.Album.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        song.Genre.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        song.Tags.Exists(t => t.Contains(term, StringComparison.OrdinalIgnoreCase));

                    if (!termMatch) { match = false; break; }
                }

                if (match)
                {
                    results.Add(song);
                    if (results.Count >= maxResults) break;
                }
            }

            return results;
        }
    }
}
