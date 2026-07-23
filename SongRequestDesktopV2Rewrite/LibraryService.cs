using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Manages the local music library: scanning folders, reading metadata,
    /// computing file hashes, detecting duplicates, and persisting state.
    /// </summary>
    public sealed class LibraryService : IDisposable
    {
        private static readonly string DataFolder =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "library");
        private static readonly string LibraryFilePath =
            Path.Combine(DataFolder, "library.json");

        private readonly object _lock = new();
        private LibraryData _data = new();
        private CancellationTokenSource? _scanCts;

        /// <summary>Raised on the UI thread when a scan completes.</summary>
        public event Action<LibraryScanResult>? ScanCompleted;

        /// <summary>Raised with progress updates during scan (0..1).</summary>
        public event Action<double, string>? ScanProgress;

        public LibraryService()
        {
            EnsureFolders();
            Load();
        }

        // ──────────────────────────────────────────────────
        //  Persistence
        // ──────────────────────────────────────────────────

        private static void EnsureFolders()
        {
            if (!Directory.Exists(DataFolder))
                Directory.CreateDirectory(DataFolder);
        }

        private void Load()
        {
            try
            {
                if (File.Exists(LibraryFilePath))
                {
                    var json = File.ReadAllText(LibraryFilePath);
                    _data = Newtonsoft.Json.JsonConvert.DeserializeObject<LibraryData>(json)
                            ?? new LibraryData();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Library load failed: {ex.Message}");
                _data = new LibraryData();
            }

            // Apply config from main config
            try
            {
                var cfg = ConfigService.Instance.Current;
                if (cfg != null)
                {
                    _data.Config.ScanFolders = cfg.LibraryScanFolders ?? new List<string>();
                    _data.Config.AllowedExtensions = cfg.LibraryAllowedExtensions ??
                        new List<string> { "mp3", "m4a", "wav", "flac", "ogg", "aac", "wma", "opus" };
                    _data.Config.AutoScanOnStartup = cfg.LibraryAutoScanOnStartup;
                    _data.Config.AutoAddDownloadsToLibrary = cfg.LibraryAutoAddDownloads;
                    _data.Config.RemoveMissingOnScan = cfg.LibraryRemoveMissingOnScan;
                }
            }
            catch { /* config might not be ready yet */ }

            _data.RebuildIndex();
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    _data.LastModifiedUtc = DateTime.UtcNow;
                    EnsureFolders();
                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(_data, Newtonsoft.Json.Formatting.Indented);
                    File.WriteAllText(LibraryFilePath, json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Library save failed: {ex.Message}");
                }
            }
        }

        public string SaveSongThumbnail(string songId, byte[] imageBytes)
        {
            var thumbDir = Path.Combine(DataFolder, "thumbnails");
            if (!Directory.Exists(thumbDir))
                Directory.CreateDirectory(thumbDir);
            var thumbPath = Path.Combine(thumbDir, $"{songId}.jpg");
            File.WriteAllBytes(thumbPath, imageBytes);
            return thumbPath;
        }

        // ──────────────────────────────────────────────────
        //  Play history
        // ──────────────────────────────────────────────────

        /// <summary>
        /// Record that a song was played. Updates play count, last played timestamp,
        /// and adds a history entry.
        /// </summary>
        public void RecordPlay(string songId, double durationPlayedSeconds = 0, bool completed = false, string trigger = "queue")
        {
            lock (_lock)
            {
                var song = _data.FindSong(songId);
                if (song == null) return;

                song.PlayCount++;
                song.LastPlayedUtc = DateTime.UtcNow;

                _data.PlayHistory.Add(new PlayHistoryEntry
                {
                    SongId = songId,
                    PlayedUtc = DateTime.UtcNow,
                    DurationPlayedSeconds = durationPlayedSeconds,
                    Completed = completed,
                    Trigger = trigger
                });

                // Keep history from growing unbounded — retain last 500 entries
                if (_data.PlayHistory.Count > 500)
                    _data.PlayHistory.RemoveRange(0, _data.PlayHistory.Count - 500);
            }

            Save();
        }

        /// <summary>
        /// Returns the most recent play history entries, enriched with song metadata.
        /// </summary>
        public List<Dictionary<string, object>> GetRecentlyPlayed(int count = 50)
        {
            lock (_lock)
            {
                var entries = _data.PlayHistory
                    .OrderByDescending(e => e.PlayedUtc)
                    .Take(count)
                    .ToList();

                var result = new List<Dictionary<string, object>>();
                foreach (var entry in entries)
                {
                    var song = _data.FindSong(entry.SongId);
                    if (song == null) continue;

                    result.Add(new Dictionary<string, object>
                    {
                        ["entryId"] = entry.Id,
                        ["songId"] = song.Id,
                        ["title"] = song.Title,
                        ["artist"] = song.Artist,
                        ["thumbnailPath"] = song.ThumbnailPath,
                        ["filePath"] = song.FilePath,
                        ["duration"] = song.Duration.TotalSeconds,
                        ["durationDisplay"] = song.DurationDisplay,
                        ["playedUtc"] = entry.PlayedUtc,
                        ["completed"] = entry.Completed,
                        ["trigger"] = entry.Trigger
                    });
                }

                return result;
            }
        }

        public void RemovePlayHistoryEntry(string entryId)
        {
            lock (_lock)
            {
                _data.PlayHistory.RemoveAll(e => e.Id == entryId);
            }

            Save();
        }

        // ──────────────────────────────────────────────────
        //  Public access
        // ──────────────────────────────────────────────────

        public LibraryData Data
        {
            get { lock (_lock) return _data; }
        }

        // ──────────────────────────────────────────────────
        //  Playlists
        // ──────────────────────────────────────────────────

        public Playlist CreatePlaylist(string name, string? emoji = null)
        {
            lock (_lock)
            {
                var pl = new Playlist
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Untitled Playlist" : name.Trim(),
                    Emoji = emoji,
                    CreatedUtc = DateTime.UtcNow,
                    LastModifiedUtc = DateTime.UtcNow
                };
                _data.Playlists.Add(pl);
                Save();
                return pl;
            }
        }

        public Playlist? GetPlaylist(string playlistId)
        {
            lock (_lock)
            {
                return _data.Playlists.FirstOrDefault(p => p.Id == playlistId);
            }
        }

        public List<Dictionary<string, object>> GetPlaylistsSummary()
        {
            lock (_lock)
            {
                return _data.Playlists.Select(p => new Dictionary<string, object>
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["emoji"] = p.Emoji ?? "",
                    ["songCount"] = p.Entries.Count,
                    ["createdUtc"] = p.CreatedUtc,
                    ["lastModifiedUtc"] = p.LastModifiedUtc
                }).ToList();
            }
        }

        public Playlist? AddSongToPlaylist(string playlistId, string songId)
        {
            lock (_lock)
            {
                var pl = _data.Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (pl == null) return null;

                var maxOrder = pl.Entries.Count > 0 ? pl.Entries.Max(e => e.SortOrder) : 0;
                pl.Entries.Add(new PlaylistEntry
                {
                    SongId = songId,
                    SortOrder = maxOrder + 1,
                    AddedUtc = DateTime.UtcNow
                });
                pl.LastModifiedUtc = DateTime.UtcNow;
                Save();
                return pl;
            }
        }

        public bool RemovePlaylistEntry(string playlistId, string entryId)
        {
            lock (_lock)
            {
                var pl = _data.Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (pl == null) return false;
                var removed = pl.Entries.RemoveAll(e => e.Id == entryId) > 0;
                if (removed)
                {
                    pl.LastModifiedUtc = DateTime.UtcNow;
                    Save();
                }
                return removed;
            }
        }

        public bool ReorderPlaylistEntry(string playlistId, string entryId, int newSortOrder)
        {
            lock (_lock)
            {
                var pl = _data.Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (pl == null) return false;
                var entry = pl.Entries.FirstOrDefault(e => e.Id == entryId);
                if (entry == null) return false;
                entry.SortOrder = newSortOrder;
                pl.LastModifiedUtc = DateTime.UtcNow;
                Save();
                return true;
            }
        }

        public bool DeletePlaylist(string playlistId)
        {
            lock (_lock)
            {
                var removed = _data.Playlists.RemoveAll(p => p.Id == playlistId) > 0;
                if (removed) Save();
                return removed;
            }
        }

        public bool UpdatePlaylist(string playlistId, string? name = null, string? emoji = null)
        {
            lock (_lock)
            {
                var pl = _data.Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (pl == null) return false;
                if (name != null) pl.Name = name.Trim();
                if (emoji != null) pl.Emoji = emoji;
                pl.LastModifiedUtc = DateTime.UtcNow;
                Save();
                return true;
            }
        }

        /// <summary>
        /// Reload config from ConfigService and resync scan folders etc.
        /// </summary>
        public void SyncConfig()
        {
            var cfg = ConfigService.Instance.Current;
            if (cfg == null) return;
            lock (_lock)
            {
                _data.Config.ScanFolders = cfg.LibraryScanFolders ?? new List<string>();
                _data.Config.AllowedExtensions = cfg.LibraryAllowedExtensions ??
                    new List<string> { "mp3", "m4a", "wav", "flac", "ogg", "aac", "wma", "opus" };
                _data.Config.AutoScanOnStartup = cfg.LibraryAutoScanOnStartup;
                _data.Config.AutoAddDownloadsToLibrary = cfg.LibraryAutoAddDownloads;
                _data.Config.RemoveMissingOnScan = cfg.LibraryRemoveMissingOnScan;
            }
        }

        // ──────────────────────────────────────────────────
        //  Scanning
        // ──────────────────────────────────────────────────

        /// <summary>
        /// Start a library scan. Cancels any in-progress scan.
        /// </summary>
        public Task<LibraryScanResult> ScanAsync(CancellationToken? ct = null)
        {
            _scanCts?.Cancel();
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct ?? CancellationToken.None);
            return RunScanAsync(_scanCts.Token);
        }

        /// <summary>Cancel the current scan.</summary>
        public void CancelScan()
        {
            _scanCts?.Cancel();
        }

        private async Task<LibraryScanResult> RunScanAsync(CancellationToken ct)
        {
            var result = new LibraryScanResult();
            List<string> extensions;
            List<string> scanFolders;

            lock (_lock)
            {
                extensions = _data.Config.AllowedExtensions
                    .Select(e => e.Trim().ToLowerInvariant().TrimStart('.'))
                    .Where(e => !string.IsNullOrEmpty(e))
                    .Distinct()
                    .ToList();
                scanFolders = new List<string>(_data.Config.ScanFolders);
            }

            if (scanFolders.Count == 0)
            {
                result.Errors.Add("No scan folders configured.");
                return result;
            }

            // Collect all audio files from scan folders
            var allFiles = new List<string>();
            foreach (var folder in scanFolders)
            {
                if (!Directory.Exists(folder))
                {
                    result.Errors.Add($"Folder not found: {folder}");
                    continue;
                }

                try
                {
                    var patterns = extensions.Select(e => $"*.{e}").ToArray();
                    foreach (var pattern in patterns)
                    {
                        allFiles.AddRange(Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories));
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Error scanning {folder}: {ex.Message}");
                }
            }

            result.FilesFound = allFiles.Count;
            var totalFiles = allFiles.Count;
            if (totalFiles == 0) return result;

            // Build existing indexes
            Dictionary<string, LibrarySong> hashIndex;
            Dictionary<string, List<LibrarySong>> metadataIndex;

            lock (_lock)
            {
                hashIndex = _data.Songs
                    .Where(s => !string.IsNullOrEmpty(s.FileHash))
                    .GroupBy(s => s.FileHash, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                // Key = normalized "artist|title|duration_seconds"
                metadataIndex = new Dictionary<string, List<LibrarySong>>(StringComparer.OrdinalIgnoreCase);
                foreach (var song in _data.Songs)
                {
                    var metaKey = MetadataKey(song.Artist, song.Title, song.Duration);
                    if (!metadataIndex.TryGetValue(metaKey, out var list))
                    {
                        list = new List<LibrarySong>();
                        metadataIndex[metaKey] = list;
                    }
                    list.Add(song);
                }

                // Mark existing entries with correct file status
                foreach (var song in _data.Songs)
                {
                    var fileInfo = new FileInfo(song.FilePath);
                    if (fileInfo.Exists)
                    {
                        song.FileStatus = FileStatus.Present;
                        song.FileSizeBytes = fileInfo.Length;
                        song.LastVerifiedUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        song.FileStatus = FileStatus.Missing;
                    }
                }

                // Remove missing if configured
                if (_data.Config.RemoveMissingOnScan)
                {
                    _data.Songs.RemoveAll(s => s.FileStatus == FileStatus.Missing);
                }
            }

            int processed = 0;
            foreach (var filePath in allFiles)
            {
                ct.ThrowIfCancellationRequested();

                processed++;
                var progress = (double)processed / totalFiles;
                var fileName = Path.GetFileName(filePath);
                ScanProgress?.Invoke(progress, $"Processing {processed}/{totalFiles}: {fileName}");

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists || fileInfo.Length == 0) continue;

                    // Compute file hash
                    var hash = await ComputeFileHashAsync(filePath, ct).ConfigureAwait(false);

                    // Read metadata
                    var song = ReadMetadata(filePath, fileInfo, hash);

                    bool added = false;
                    bool updated = false;
                    bool hashDup = false;

                    lock (_lock)
                    {
                        // 1. Hash duplicate check — reject if hash already exists
                        if (hashIndex.ContainsKey(hash))
                        {
                            // Same hash = same file content. Keep the existing entry.
                            hashDup = true;
                            song.IsHashDuplicate = true;
                            result.SongsAdded++; // count as a "find" not an error
                            continue; // don't add
                        }

                        // 2. Check if this exact file path already exists (update)
                        var existing = _data.Songs.FirstOrDefault(s =>
                            string.Equals(s.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

                        if (existing != null)
                        {
                            // Update metadata if changed
                            if (existing.Title != song.Title ||
                                existing.Artist != song.Artist ||
                                existing.Album != song.Album ||
                                existing.Genre != song.Genre ||
                                existing.Duration != song.Duration ||
                                existing.BitrateKbps != song.BitrateKbps)
                            {
                                existing.Title = song.Title;
                                existing.Artist = song.Artist;
                                existing.Album = song.Album;
                                existing.Genre = song.Genre;
                                existing.Duration = song.Duration;
                                existing.BitrateKbps = song.BitrateKbps;
                                existing.SampleRateHz = song.SampleRateHz;
                                existing.FileHash = hash;
                                existing.FileSizeBytes = song.FileSizeBytes;
                                existing.LastModifiedUtc = DateTime.UtcNow;
                                existing.LastVerifiedUtc = DateTime.UtcNow;
                                existing.FileStatus = FileStatus.Present;
                                updated = true;
                                result.SongsUpdated++;
                            }
                            else
                            {
                                existing.LastVerifiedUtc = DateTime.UtcNow;
                                existing.FileStatus = FileStatus.Present;
                            }
                        }
                        else
                        {
                            // New file — add it
                            song.LastVerifiedUtc = DateTime.UtcNow;
                            _data.Songs.Add(song);
                            _data.AddToIndex(song);
                            hashIndex[hash] = song;
                            added = true;
                            result.SongsAdded++;
                        }
                    }

                    // 3. Metadata duplicate detection (outside lock for perf)
                    if (added || updated)
                    {
                        var metaKey = MetadataKey(song.Artist, song.Title, song.Duration);
                        lock (_lock)
                        {
                            if (!metadataIndex.TryGetValue(metaKey, out var dupList))
                            {
                                dupList = new List<LibrarySong>();
                                metadataIndex[metaKey] = dupList;
                            }

                            // Find all songs with the same metadata key
                            var matches = _data.Songs
                                .Where(s => MetadataKey(s.Artist, s.Title, s.Duration) == metaKey
                                            && s.Id != song.Id)
                                .ToList();

                            if (matches.Count > 0)
                            {
                                // Flag both sides
                                song.MetadataDuplicateIds = matches.Select(m => m.Id).ToList();
                                foreach (var match in matches)
                                {
                                    if (!match.MetadataDuplicateIds.Contains(song.Id))
                                        match.MetadataDuplicateIds.Add(song.Id);
                                }
                            }

                            dupList.Add(song);
                        }
                    }

                    // Yield periodically to keep UI responsive
                    if (processed % 20 == 0)
                        await Task.Delay(1, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{fileName}: {ex.Message}");
                }
            }

            // Post-scan: re-scan metadata duplicates across entire library
            // (files that were already in the library but had no match before)
            RebuildMetadataDuplicates();

            result.ScannedUtc = DateTime.UtcNow;
            _data.LastScanUtc = DateTime.UtcNow;

            Save();
            ScanCompleted?.Invoke(result);
            return result;
        }

        /// <summary>
        /// Walk the entire library and recompute metadata duplicate flags.
        /// Called after scan completes.
        /// </summary>
        private void RebuildMetadataDuplicates()
        {
            lock (_lock)
            {
                // Clear existing flags
                foreach (var song in _data.Songs)
                {
                    song.MetadataDuplicateIds.Clear();
                }

                // Group by metadata key
                var groups = _data.Songs
                    .Where(s => s.FileStatus == FileStatus.Present)
                    .GroupBy(s => MetadataKey(s.Artist, s.Title, s.Duration))
                    .Where(g => g.Count() > 1);

                foreach (var group in groups)
                {
                    var ids = group.Select(s => s.Id).ToList();
                    foreach (var song in group)
                    {
                        song.MetadataDuplicateIds = ids.Where(id => id != song.Id).ToList();
                    }
                }
            }
        }

        // ──────────────────────────────────────────────────
        //  Add downloaded song to library
        // ──────────────────────────────────────────────────

        /// <summary>
        /// Import a downloaded audio file into the library.
        /// Returns the created LibrarySong, or null if hash duplicate.
        /// </summary>
        public LibrarySong? ImportDownloadedSong(
            string filePath,
            string? youTubeId = null,
            string? videoUrl = null,
            string? desiredTitle = null,
            string? desiredArtist = null)
        {
            if (!File.Exists(filePath)) return null;

            var fileInfo = new FileInfo(filePath);
            var hash = ComputeFileHashAsync(filePath).GetAwaiter().GetResult();

            lock (_lock)
            {
                // Hash duplicate — skip
                if (_data.Songs.Any(s => string.Equals(s.FileHash, hash, StringComparison.OrdinalIgnoreCase)))
                {
                    Debug.WriteLine($"Library: Hash duplicate rejected for {filePath}");
                    return null;
                }

                // Path already in library — update
                var existing = _data.Songs.FirstOrDefault(s =>
                    string.Equals(s.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.LastVerifiedUtc = DateTime.UtcNow;
                    existing.FileStatus = FileStatus.Present;
                    existing.FileHash = hash;
                    existing.FileSizeBytes = fileInfo.Length;
                    if (youTubeId != null) existing.YouTubeId = youTubeId;
                    if (videoUrl != null) existing.SourceUrl = videoUrl;
                    Save();
                    return existing;
                }
            }

            // Read metadata
            var song = ReadMetadata(filePath, fileInfo, hash);
            song.Source = SongSource.Downloaded;
            song.YouTubeId = youTubeId;
            song.SourceUrl = videoUrl;
            song.DateAddedUtc = DateTime.UtcNow;

            // Apply user overrides
            if (!string.IsNullOrWhiteSpace(desiredTitle)) song.Title = desiredTitle;
            if (!string.IsNullOrWhiteSpace(desiredArtist)) song.Artist = desiredArtist;

            lock (_lock)
            {
                _data.Songs.Add(song);
                _data.AddToIndex(song);

                // Check metadata duplicates
                var metaKey = MetadataKey(song.Artist, song.Title, song.Duration);
                var matches = _data.Songs
                    .Where(s => MetadataKey(s.Artist, s.Title, s.Duration) == metaKey
                                && s.Id != song.Id)
                    .ToList();

                if (matches.Count > 0)
                {
                    song.MetadataDuplicateIds = matches.Select(m => m.Id).ToList();
                    foreach (var match in matches)
                    {
                        if (!match.MetadataDuplicateIds.Contains(song.Id))
                            match.MetadataDuplicateIds.Add(song.Id);
                    }
                }
            }

            Save();
            return song;
        }

        // ──────────────────────────────────────────────────
        //  File hash
        // ──────────────────────────────────────────────────

        private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct = default)
        {
            using var sha = SHA256.Create();
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var hashBytes = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexString(hashBytes);
        }

        // ──────────────────────────────────────────────────
        //  Metadata reading
        // ──────────────────────────────────────────────────

        private static LibrarySong ReadMetadata(string filePath, FileInfo fileInfo, string fileHash)
        {
            var song = new LibrarySong
            {
                FilePath = filePath,
                FileHash = fileHash,
                FileSizeBytes = fileInfo.Length,
                Source = SongSource.Local,
                FileStatus = FileStatus.Present
            };

            try
            {
                using var tagFile = global::TagLib.File.Create(filePath);

                // Title
                if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title))
                    song.Title = tagFile.Tag.Title;
                else
                    song.Title = Path.GetFileNameWithoutExtension(filePath);

                // Artist
                if (tagFile.Tag.Performers != null && tagFile.Tag.Performers.Length > 0)
                {
                    var artists = tagFile.Tag.Performers
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .ToList();
                    song.Artist = artists.Count > 0 ? string.Join(", ", artists) : "Unknown";
                }
                else
                {
                    song.Artist = "Unknown";
                }

                // Album
                if (!string.IsNullOrWhiteSpace(tagFile.Tag.Album))
                    song.Album = tagFile.Tag.Album;

                // Genre
                if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstGenre))
                    song.Genre = tagFile.Tag.FirstGenre;

                // Duration — TagLib can read wrong values for webm/opus (common with YouTube
                // downloads saved as .mp3). Always try NAudio which reads the actual audio stream.
                song.Duration = tagFile.Properties.Duration;
                try
                {
                    using var reader = new NAudio.Wave.AudioFileReader(filePath);
                    if (reader.TotalTime > TimeSpan.Zero)
                    {
                        song.Duration = reader.TotalTime;
                        if (song.BitrateKbps <= 0)
                            song.BitrateKbps = (int)(reader.WaveFormat.AverageBytesPerSecond * 8.0 / 1000.0);
                    }
                }
                catch { }

                // Audio properties
                if (song.BitrateKbps <= 0)
                    song.BitrateKbps = (int)(tagFile.Properties.AudioBitrate);
                song.SampleRateHz = tagFile.Properties.AudioSampleRate;

                // Extract and cache thumbnail
                var pic = tagFile.Tag.Pictures?.FirstOrDefault();
                if (pic != null && pic.Data?.Data != null && pic.Data.Data.Length > 0)
                {
                    var thumbDir = Path.Combine(DataFolder, "thumbnails");
                    if (!Directory.Exists(thumbDir))
                        Directory.CreateDirectory(thumbDir);

                    var thumbPath = Path.Combine(thumbDir, $"{song.Id}{GetThumbExtension(pic.MimeType)}");
                    File.WriteAllBytes(thumbPath, pic.Data.Data);
                    song.ThumbnailPath = thumbPath;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Metadata read failed for {filePath}: {ex.Message}");
                // Use filename as title fallback
                song.Title = Path.GetFileNameWithoutExtension(filePath);
            }

            return song;
        }

        private static string GetThumbExtension(string? mimeType)
        {
            if (string.IsNullOrEmpty(mimeType)) return ".jpg";
            return mimeType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/bmp" => ".bmp",
                _ => ".jpg"
            };
        }

        // ──────────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────────

        private static string MetadataKey(string artist, string title, TimeSpan duration)
        {
            var a = (artist ?? "").Trim().ToLowerInvariant();
            var t = (title ?? "").Trim().ToLowerInvariant();
            // Normalize common variations
            a = a.Replace(" & ", " and ").Replace(".", "").Replace(",", "");
            t = t.Replace(" & ", " and ").Replace(".", "").Replace(",", "");
            // Round duration to nearest second for fuzzy matching
            var d = (int)duration.TotalSeconds;
            return $"{a}|{t}|{d}";
        }

        public void Dispose()
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            Save();
        }
    }
}
