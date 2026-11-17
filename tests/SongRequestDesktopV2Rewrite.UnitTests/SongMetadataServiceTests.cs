using System;
using System.IO;
using System.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class SongMetadataServiceTests
{
    private static string CopySampleAudio()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "announcement.mp3");
        var destination = Path.Combine(TestSupport.NewTempDir("metadata"), "song.mp3");
        File.Copy(source, destination);
        return destination;
    }

    private static LibrarySong SongAt(string path) => new()
    {
        Id = Guid.NewGuid().ToString(),
        FilePath = path,
        Title = "Old title",
        Artist = "Old artist",
        FileStatus = FileStatus.Present,
    };

    private static SongMetadataEdit Edit(string title = "New title") => new()
    {
        Title = title,
        Artist = "New artist",
        AlbumArtist = "Album artist",
        Album = "New album",
        Genre = "Electronic",
        Year = 2026,
        Track = 3,
        Disc = 1,
        Bpm = 128,
        Comment = "A comment",
        Composer = "Composer Name",
        Lyricist = "Lyricist Name",
        Conductor = "Conductor Name",
        Publisher = "Publisher",
        Copyright = "2026 CodeRebels",
        Isrc = "DEABC2600001",
        Grouping = "Live",
        Lyrics = "[00:01.00]First line\n[00:05.50]Second line",
    };

    [Fact]
    public void Save_WritesMetadataIntoTheAudioFile()
    {
        var path = CopySampleAudio();
        var song = SongAt(path);

        var result = SongMetadataService.Save(song, Edit(), null, null, false);

        Assert.True(result.Success);
        Assert.Equal(MetadataWriteTarget.File, result.Target);
        Assert.False(song.MetadataStoredInLibrary);

        using var file = TagLib.File.Create(path);
        Assert.Equal("New title", file.Tag.Title);
        Assert.Equal("New artist", file.Tag.FirstPerformer);
        Assert.Equal("New album", file.Tag.Album);
        Assert.Equal("Electronic", file.Tag.FirstGenre);
        Assert.Equal((uint)2026, file.Tag.Year);
        Assert.Equal((uint)3, file.Tag.Track);
        Assert.Equal((uint)128, file.Tag.BeatsPerMinute);
        Assert.Contains("First line", file.Tag.Lyrics);

        var lrc = Path.Combine(Path.GetDirectoryName(path)!, "song.lrc");
        Assert.True(File.Exists(lrc));
        Assert.Contains("[00:05.50]", File.ReadAllText(lrc));
    }

    [Fact]
    public void Read_MergesFileTagsWithLibraryFallbacks()
    {
        var path = CopySampleAudio();
        var song = SongAt(path);
        SongMetadataService.Save(song, Edit(), null, null, false);
        song.Lyricist = "Library-only lyricist";

        var snapshot = SongMetadataService.Read(song);

        Assert.Equal("New title", snapshot.Title);
        Assert.Equal("New artist", snapshot.Artist);
        Assert.Equal("Library-only lyricist", snapshot.Lyricist);
        Assert.True(snapshot.LyricsAreSynced);
        Assert.False(snapshot.MetadataStoredInLibrary);
    }

    [Fact]
    public void Save_WritesArtworkIntoTheAudioFile()
    {
        var path = CopySampleAudio();
        var song = SongAt(path);
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z0YQAAAAASUVORK5CYII=");

        var result = SongMetadataService.Save(song, Edit(), png, "image/png", false);

        Assert.Equal(MetadataWriteTarget.File, result.Target);
        using var file = TagLib.File.Create(path);
        var picture = Assert.Single(file.Tag.Pictures);
        Assert.Equal("image/png", picture.MimeType);
        Assert.Equal(png, picture.Data.Data);
        Assert.NotNull(song.ThumbnailPath);
        Assert.True(File.Exists(song.ThumbnailPath));
    }

    [Fact]
    public void Save_FallsBackToTheLibraryWhenTheFileIsGone()
    {
        var path = Path.Combine(TestSupport.NewTempDir("metadata-missing"), "missing.mp3");
        var song = SongAt(path);

        var result = SongMetadataService.Save(song, Edit("Library title"), null, null, false);

        Assert.True(result.Success);
        Assert.Equal(MetadataWriteTarget.Library, result.Target);
        Assert.True(song.MetadataStoredInLibrary);
        Assert.True(song.LyricsStoredInLibrary);
        Assert.NotNull(result.Warning);

        var snapshot = SongMetadataService.Read(song);
        Assert.Equal("Library title", snapshot.Title);
        Assert.Equal("New artist", snapshot.Artist);
        Assert.True(snapshot.MetadataStoredInLibrary);
    }

    [Fact]
    public void Save_KeepsArtworkInTheLibraryWhenTheFileIsMissing()
    {
        var path = Path.Combine(TestSupport.NewTempDir("metadata-art"), "missing.mp3");
        var song = SongAt(path);
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6, 7, 8 };

        SongMetadataService.Save(song, Edit(), bytes, "image/png", false);

        Assert.True(song.ArtworkStoredInLibrary);
        Assert.False(string.IsNullOrWhiteSpace(song.ThumbnailPath));
        Assert.Equal(bytes, File.ReadAllBytes(song.ThumbnailPath!));
    }

    [Fact]
    public void Save_RemovesAFileThumbnailInLibraryFallback()
    {
        var path = Path.Combine(TestSupport.NewTempDir("metadata-remove"), "missing.mp3");
        var song = SongAt(path);
        song.ThumbnailPath = Path.Combine(Path.GetDirectoryName(path)!, "thumb.png");
        File.WriteAllBytes(song.ThumbnailPath, new byte[] { 1, 2, 3 });

        SongMetadataService.Save(song, Edit(), null, null, removeArtwork: true);

        Assert.Null(song.ThumbnailPath);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "thumb.png")));
    }

    [Fact]
    public void LrcRoundTrip_PreservesTimestampsAndText()
    {
        var lrc = SongMetadataService.BuildLrc(new[]
        {
            (1.0, "First"),
            (65.25, "Second"),
            (3723.5, "Long"),
        });

        var parsed = SongMetadataService.ParseLrc(lrc);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(1.0, parsed[0].Seconds, 2);
        Assert.Equal("First", parsed[0].Text);
        Assert.Equal(65.25, parsed[1].Seconds, 2);
        Assert.Equal(3723.5, parsed[2].Seconds, 2);
        Assert.True(SongMetadataService.LooksSynced(lrc));
        Assert.False(SongMetadataService.LooksSynced("plain lyrics"));
    }

    [Fact]
    public void Read_OldLibraryEntries_HaveSafeDefaults()
    {
        var song = new LibrarySong { Title = "Old", Artist = "Old" };

        Assert.Equal(string.Empty, song.AlbumArtist);
        Assert.Equal(0u, song.Year);
        Assert.False(song.MetadataStoredInLibrary);
        Assert.False(song.LyricsStoredInLibrary);
        Assert.False(song.ArtworkStoredInLibrary);
    }
}
