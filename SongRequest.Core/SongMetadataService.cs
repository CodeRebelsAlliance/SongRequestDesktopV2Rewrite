using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Reads and writes the metadata shown in the song metadata editor.
///
/// Metadata is written into the audio file's tags with TagLib. Formats that do
/// not support a field, read-only files and unsupported containers fall back to
/// <see cref="LibrarySong.MetadataStoredInLibrary"/>, so the edit is never lost.
/// Artwork and lyrics use the same file-first / library-fallback rule.
/// </summary>
public static class SongMetadataService
{
    public const int MaxArtworkBytes = 20 * 1024 * 1024;

    public static SongMetadataSnapshot Read(LibrarySong song)
    {
        var snapshot = new SongMetadataSnapshot
        {
            SongId = song.Id,
            FilePath = song.FilePath,
            FileExists = File.Exists(song.FilePath),
            FileFormat = Path.GetExtension(song.FilePath).TrimStart('.').ToUpperInvariant(),
            Duration = song.Duration,
            MetadataStoredInLibrary = song.MetadataStoredInLibrary,
            LyricsStoredInLibrary = song.LyricsStoredInLibrary,
            ArtworkStoredInLibrary = song.ArtworkStoredInLibrary,
            Providers = MetadataProviderSettings.FromConfig(ConfigService.Instance.Current),
        };

        TagData? tags = null;
        if (snapshot.FileExists && !song.MetadataStoredInLibrary)
        {
            try { tags = ReadTags(song.FilePath); }
            catch (Exception ex) { snapshot.Warning = "Could not read file tags: " + ex.Message; }
        }

        snapshot.CanWriteToFile = snapshot.FileExists && CanWrite(song.FilePath);

        // File tags win when the library is not authoritative; the persisted
        // library values are the fallback for fields the format cannot store.
        var source = tags;
        snapshot.Title = Pick(source?.Title, song.Title);
        snapshot.Artist = Pick(source?.Artist, song.Artist);
        snapshot.AlbumArtist = Pick(source?.AlbumArtist, song.AlbumArtist);
        snapshot.Album = Pick(source?.Album, song.Album);
        snapshot.Genre = Pick(source?.Genre, song.Genre);
        snapshot.Year = source is { Year: > 0 } ? source.Year : song.Year;
        snapshot.Track = source is { Track: > 0 } ? source.Track : song.Track;
        snapshot.Disc = source is { Disc: > 0 } ? source.Disc : song.Disc;
        snapshot.Bpm = source is { Bpm: > 0 } ? source.Bpm : song.Bpm;
        snapshot.Comment = Pick(source?.Comment, song.Comment);
        snapshot.Composer = Pick(source?.Composer, song.Composer);
        snapshot.Lyricist = Pick(source?.Lyricist, song.Lyricist);
        snapshot.Conductor = Pick(source?.Conductor, song.Conductor);
        snapshot.Publisher = Pick(source?.Publisher, song.Publisher);
        snapshot.Copyright = Pick(source?.Copyright, song.Copyright);
        snapshot.Isrc = Pick(source?.Isrc, song.Isrc);
        snapshot.Grouping = Pick(source?.Grouping, song.Grouping);
        if (source != null && source.Duration > TimeSpan.Zero) snapshot.Duration = source.Duration;

        // Lyrics: explicit library fallback first, then embedded tags/sidecar.
        if (song.LyricsStoredInLibrary || song.MetadataStoredInLibrary)
        {
            snapshot.Lyrics = song.Lyrics;
        }
        else
        {
            snapshot.Lyrics = source?.Lyrics ?? string.Empty;
            if (string.IsNullOrWhiteSpace(snapshot.Lyrics))
            {
                var sidecar = LyricsService.GetLrcFileLyrics(song.FilePath);
                snapshot.Lyrics = sidecar?.SyncedLyrics ?? sidecar?.PlainLyrics ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(snapshot.Lyrics))
                snapshot.Lyrics = song.Lyrics;
        }
        snapshot.LyricsAreSynced = LooksSynced(snapshot.Lyrics);

        // Artwork: library fallback first when it is authoritative; otherwise the
        // embedded picture, then the cached thumbnail.
        if ((song.ArtworkStoredInLibrary || song.MetadataStoredInLibrary) && SongThumbnailExists(song))
        {
            snapshot.ArtworkBytes = TryReadFile(song.ThumbnailPath);
            snapshot.ArtworkMimeType = MimeFromExtension(song.ThumbnailPath);
        }
        else if (source?.ArtworkBytes is { Length: > 0 })
        {
            snapshot.ArtworkBytes = source.ArtworkBytes;
            snapshot.ArtworkMimeType = source.ArtworkMimeType;
        }
        else if (SongThumbnailExists(song))
        {
            snapshot.ArtworkBytes = TryReadFile(song.ThumbnailPath);
            snapshot.ArtworkMimeType = MimeFromExtension(song.ThumbnailPath);
        }

        return snapshot;
    }

    public static SongMetadataSaveResult Save(
        LibrarySong song,
        SongMetadataEdit edit,
        byte[]? artworkBytes,
        string? artworkMimeType,
        bool removeArtwork)
    {
        var result = new SongMetadataSaveResult();
        var fileExists = File.Exists(song.FilePath);

        if (artworkBytes is { Length: > MaxArtworkBytes })
        {
            result.Warning = "Artwork is larger than 20 MB and was not used.";
            artworkBytes = null;
            artworkMimeType = null;
        }

        ApplyEdit(song, edit);
        song.LastModifiedUtc = DateTime.UtcNow;

        var fileWritten = false;
        if (fileExists)
        {
            try
            {
                WriteTags(song.FilePath, edit, artworkBytes, artworkMimeType, removeArtwork);
                fileWritten = true;
            }
            catch (Exception ex)
            {
                result.Warning = "The audio file's tags could not be written. " +
                                 "The metadata is kept in the library instead. (" + ex.Message + ")";
            }
        }
        else
        {
            result.Warning = "The audio file is missing. The metadata is kept in the library.";
        }

        if (fileWritten)
        {
            song.MetadataStoredInLibrary = false;
            song.LyricsStoredInLibrary = false;
            song.ArtworkStoredInLibrary = false;
            TryWriteLrcSidecar(song.FilePath, edit.Lyrics);
            result.Target = MetadataWriteTarget.File;
        }
        else
        {
            song.MetadataStoredInLibrary = true;
            song.LyricsStoredInLibrary = true;
            if (artworkBytes != null || removeArtwork)
                song.ArtworkStoredInLibrary = true;
            result.Target = MetadataWriteTarget.Library;
        }

        // Always keep the cached thumbnail in sync. The editor and library use it
        // regardless of whether the tags or library.json are authoritative.
        if (artworkBytes is { Length: > 0 })
        {
            song.ThumbnailPath = AppPaths.LibraryDataThumbnailPath(song.Id, artworkMimeType);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(song.ThumbnailPath)!);
                File.WriteAllBytes(song.ThumbnailPath, artworkBytes);
            }
            catch
            {
                song.ThumbnailPath = null;
            }
        }
        else if (removeArtwork)
        {
            TryDelete(song.ThumbnailPath);
            song.ThumbnailPath = null;
        }

        result.Success = true;
        result.Metadata = Read(song);
        result.Metadata.Warning = result.Warning;
        return result;
    }

    /// <summary>Apply the editable values to the persisted library entry.</summary>
    public static void ApplyEdit(LibrarySong song, SongMetadataEdit edit)
    {
        song.Title = edit.Title?.Trim() ?? string.Empty;
        song.Artist = string.IsNullOrWhiteSpace(edit.Artist) ? "Unknown" : edit.Artist.Trim();
        song.AlbumArtist = edit.AlbumArtist?.Trim() ?? string.Empty;
        song.Album = edit.Album?.Trim() ?? string.Empty;
        song.Genre = edit.Genre?.Trim() ?? string.Empty;
        song.Year = edit.Year;
        song.Track = edit.Track;
        song.Disc = edit.Disc;
        song.Bpm = edit.Bpm;
        song.Comment = edit.Comment?.Trim() ?? string.Empty;
        song.Composer = edit.Composer?.Trim() ?? string.Empty;
        song.Lyricist = edit.Lyricist?.Trim() ?? string.Empty;
        song.Conductor = edit.Conductor?.Trim() ?? string.Empty;
        song.Publisher = edit.Publisher?.Trim() ?? string.Empty;
        song.Copyright = edit.Copyright?.Trim() ?? string.Empty;
        song.Isrc = edit.Isrc?.Trim() ?? string.Empty;
        song.Grouping = edit.Grouping?.Trim() ?? string.Empty;
        song.Lyrics = edit.Lyrics ?? string.Empty;
    }

    public static string BuildLrc(IEnumerable<(double Seconds, string Text)> lines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (seconds, text) in lines)
        {
            if (seconds < 0) continue;
            var ts = TimeSpan.FromSeconds(Math.Round(seconds, 2));
            var totalMinutes = (int)ts.TotalMinutes;
            sb.Append('[')
              .Append(totalMinutes.ToString("00")).Append(':')
              .Append(ts.Seconds.ToString("00")).Append('.')
              .Append(ts.Milliseconds.ToString("000"))
              .Append(']')
              .Append(text ?? string.Empty)
              .Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public static List<(double Seconds, string Text)> ParseLrc(string? text)
    {
        var result = new List<(double, string)>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith("[", StringComparison.Ordinal)) continue;
            var close = line.IndexOf(']');
            if (close <= 1) continue;
            var stamp = line[1..close];
            var content = line[(close + 1)..].Trim();
            if (!TryParseLrcTimestamp(stamp, out var seconds)) continue;
            result.Add((seconds, content));
        }

        return result.OrderBy(l => l.Item1).ToList();
    }

    internal static bool LooksSynced(string? text)
        => !string.IsNullOrWhiteSpace(text) && ParseLrc(text).Count > 0;

    // ── TagLib ─────────────────────────────────────────────────────────────

    private sealed class TagData
    {
        public string Title = string.Empty;
        public string Artist = string.Empty;
        public string AlbumArtist = string.Empty;
        public string Album = string.Empty;
        public string Genre = string.Empty;
        public uint Year;
        public uint Track;
        public uint Disc;
        public uint Bpm;
        public string Comment = string.Empty;
        public string Composer = string.Empty;
        public string Lyricist = string.Empty;
        public string Conductor = string.Empty;
        public string Publisher = string.Empty;
        public string Copyright = string.Empty;
        public string Isrc = string.Empty;
        public string Grouping = string.Empty;
        public string Lyrics = string.Empty;
        public TimeSpan Duration;
        public byte[]? ArtworkBytes;
        public string? ArtworkMimeType;
    }

    private static TagData ReadTags(string filePath)
    {
        using var file = global::TagLib.File.Create(filePath);
        var tag = file.Tag;
        var data = new TagData
        {
            Title = tag.Title ?? string.Empty,
            Artist = Join(tag.Performers),
            AlbumArtist = Join(tag.AlbumArtists),
            Album = tag.Album ?? string.Empty,
            Genre = tag.FirstGenre ?? string.Empty,
            Year = tag.Year,
            Track = tag.Track,
            Disc = tag.Disc,
            Bpm = tag.BeatsPerMinute,
            Comment = tag.Comment ?? string.Empty,
            Composer = Join(tag.Composers),
            // TagLib has no cross-format lyricist field; the value stays in library.json.
            Lyricist = string.Empty,
            Conductor = tag.Conductor ?? string.Empty,
            Publisher = tag.Publisher ?? string.Empty,
            Copyright = tag.Copyright ?? string.Empty,
            Isrc = tag.ISRC ?? string.Empty,
            Grouping = tag.Grouping ?? string.Empty,
            Lyrics = tag.Lyrics ?? string.Empty,
            Duration = file.Properties?.Duration ?? TimeSpan.Zero,
        };

        var picture = tag.Pictures?.FirstOrDefault();
        if (picture?.Data?.Data is { Length: > 0 } bytes)
        {
            data.ArtworkBytes = bytes;
            data.ArtworkMimeType = picture.MimeType;
        }

        return data;
    }

    private static void WriteTags(
        string filePath,
        SongMetadataEdit edit,
        byte[]? artworkBytes,
        string? artworkMimeType,
        bool removeArtwork)
    {
        using var file = global::TagLib.File.Create(filePath);
        var tag = file.Tag;

        tag.Title = edit.Title?.Trim() ?? string.Empty;
        tag.Performers = Split(edit.Artist, "Unknown");
        tag.AlbumArtists = Split(edit.AlbumArtist);
        tag.Album = edit.Album?.Trim() ?? string.Empty;
        tag.Genres = Split(edit.Genre);
        tag.Year = edit.Year;
        tag.Track = edit.Track;
        tag.Disc = edit.Disc;
        tag.BeatsPerMinute = edit.Bpm;
        tag.Comment = edit.Comment?.Trim() ?? string.Empty;
        tag.Composers = Split(edit.Composer);
        // TagLib does not expose a cross-format lyricist field; keep it in the
        // library record (SongMetadataService.Read merges it back in).
        tag.Conductor = edit.Conductor?.Trim() ?? string.Empty;
        tag.Publisher = edit.Publisher?.Trim() ?? string.Empty;
        tag.Copyright = edit.Copyright?.Trim() ?? string.Empty;
        tag.ISRC = edit.Isrc?.Trim() ?? string.Empty;
        tag.Grouping = edit.Grouping?.Trim() ?? string.Empty;
        tag.Lyrics = edit.Lyrics ?? string.Empty;

        if (artworkBytes is { Length: > 0 })
        {
            tag.Pictures = new global::TagLib.IPicture[]
            {
                new global::TagLib.Picture(new global::TagLib.ByteVector(artworkBytes))
                {
                    MimeType = string.IsNullOrWhiteSpace(artworkMimeType) ? "image/jpeg" : artworkMimeType,
                    Type = global::TagLib.PictureType.FrontCover,
                    Description = "Cover",
                }
            };
        }
        else if (removeArtwork)
        {
            tag.Pictures = Array.Empty<global::TagLib.IPicture>();
        }

        file.Save();
    }

    private static bool CanWrite(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return stream.CanWrite;
        }
        catch
        {
            return false;
        }
    }

    private static void TryWriteLrcSidecar(string filePath, string? lyrics)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory)) return;

            var lrcPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(filePath) + ".lrc");
            if (string.IsNullOrWhiteSpace(lyrics))
            {
                if (File.Exists(lrcPath)) File.Delete(lrcPath);
                return;
            }

            File.WriteAllText(lrcPath, lyrics.Trim() + Environment.NewLine);
        }
        catch
        {
            // The tags are the primary storage; a sidecar is a convenience.
        }
    }

    private static bool TryParseLrcTimestamp(string stamp, out double seconds)
    {
        seconds = 0;
        try
        {
            var parts = stamp.Split(':');
            if (parts.Length != 2) return false;
            var minutes = int.Parse(parts[0]);
            var secondPart = parts[1].Replace(',', '.');
            var value = double.Parse(secondPart, System.Globalization.CultureInfo.InvariantCulture);
            seconds = minutes * 60 + value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Join(string[]? values)
        => values == null ? string.Empty : string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v)));

    private static string[] Split(string? value, string fallback = "")
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return string.IsNullOrWhiteSpace(text)
            ? Array.Empty<string>()
            : text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Pick(string? preferred, string? fallback)
        => string.IsNullOrWhiteSpace(preferred) ? (fallback ?? string.Empty) : preferred;

    private static bool SongThumbnailExists(LibrarySong song)
        => !string.IsNullOrWhiteSpace(song.ThumbnailPath) && File.Exists(song.ThumbnailPath);

    private static byte[]? TryReadFile(string? path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : File.ReadAllBytes(path); }
        catch { return null; }
    }

    private static string? MimeFromExtension(string? path)
        => Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };

    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
