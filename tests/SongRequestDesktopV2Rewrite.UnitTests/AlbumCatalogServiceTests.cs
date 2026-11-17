using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Album grouping, provider enrichment, cache-once behaviour and smart
/// local/provider track matching. These tests use a fake provider so they never
/// touch the network; album metadata parsing lives in
/// <see cref="AlbumMetadataProviderTests"/>.
/// </summary>
public class AlbumCatalogServiceTests
{
    private sealed class FakeAlbumProvider : IAlbumMetadataProvider
    {
        public AlbumProviderResult Result { get; set; } = new();
        public int FindCalls { get; private set; }
        public int ArtworkCalls { get; private set; }

        public Task<AlbumProviderResult> FindAlbumAsync(string album, string artist, uint year, CancellationToken ct = default)
        {
            FindCalls++;
            return Task.FromResult(Result);
        }

        public Task<ArtworkResult> FetchArtworkAsync(string url, CancellationToken ct = default)
        {
            ArtworkCalls++;
            return Task.FromResult(new ArtworkResult());
        }
    }

    private static LibraryService NewLibrary()
    {
        var library = new LibraryService();
        lock (library.Data)
        {
            library.Data.Songs.Clear();
            library.Data.Playlists.Clear();
            library.Data.PlayHistory.Clear();
            library.Data.RebuildIndex();
        }
        return library;
    }

    private static LibrarySong Song(
        string id,
        string title,
        string album,
        string artist = "Artist",
        string? albumArtist = null,
        uint track = 0,
        uint disc = 0,
        uint year = 0,
        string genre = "",
        FileStatus status = FileStatus.Present)
    {
        return new LibrarySong
        {
            Id = id,
            Title = title,
            Artist = artist,
            AlbumArtist = albumArtist ?? artist,
            Album = album,
            Genre = genre,
            Year = year,
            Track = track,
            Disc = disc,
            Duration = TimeSpan.FromMinutes(3),
            FilePath = "/music/" + id + ".mp3",
            FileStatus = status,
            DateAddedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    private static void Add(LibraryService library, params LibrarySong[] songs)
    {
        lock (library.Data)
        {
            foreach (var song in songs) library.Data.Songs.Add(song);
            library.Data.RebuildIndex();
        }
    }

    private static AlbumTrackInfo ProviderTrack(string title, uint track, uint disc = 1)
        => new()
        {
            Title = title,
            Artist = "Artist",
            Track = track,
            Disc = disc,
            Duration = TimeSpan.FromMinutes(3),
            IsLocal = false,
        };

    [Fact]
    public void Grouping_UsesNormalizedAlbumAndAlbumArtist_AndSkipsMissingOrAlbumlessSongs()
    {
        using var library = NewLibrary();
        Add(library,
            Song("a1", "Breathe", "The Dark Side of the Moon", "Pink Floyd", track: 1, year: 1973, genre: "Progressive Rock"),
            Song("a2", "Time", "the dark side of the moon", "Pink Floyd", track: 3),
            Song("b1", "Another Track", "The Dark Side of the Moon", "Some Cover Band", track: 1),
            Song("c1", "No Album", ""),
            Song("m1", "Missing", "The Dark Side of the Moon", "Pink Floyd", status: FileStatus.Missing));

        var albums = new AlbumCatalogService(library, new FakeAlbumProvider()).GetAlbums();

        Assert.Equal(2, albums.Count);
        var pinkFloyd = albums.Single(a => a.AlbumArtist == "Pink Floyd");
        Assert.Equal(2, pinkFloyd.LocalTrackCount);
        Assert.Equal((uint)1973, pinkFloyd.Year);
        Assert.Equal("Progressive Rock", pinkFloyd.Genre);
        Assert.Equal("Breathe", pinkFloyd.Tracks[0].Title);
        Assert.Equal("Time", pinkFloyd.Tracks[1].Title);
        Assert.DoesNotContain(albums, a => a.Title == "No Album");
        Assert.DoesNotContain(albums, a => a.LocalTrackCount == 3);
    }

    [Fact]
    public void Search_MatchesTitleArtistAndGenre_AndIsCaseInsensitive()
    {
        using var library = NewLibrary();
        Add(library,
            Song("a1", "Breathe", "The Dark Side of the Moon", "Pink Floyd", track: 1, genre: "Progressive Rock"),
            Song("b1", "Around the World", "Homework", "Daft Punk", track: 1, genre: "Electronic"));

        var service = new AlbumCatalogService(library, new FakeAlbumProvider());

        Assert.Single(service.Search("pink"));
        Assert.Single(service.Search("DAFT punk"));
        Assert.Single(service.Search("homework"));
        Assert.Single(service.Search("electronic"));
        Assert.Empty(service.Search("nothing matches this"));
    }

    [Fact]
    public async Task GetAlbum_ListsMissingProviderTracks_AndCachesProviderDataOnlyOnce()
    {
        using var library = NewLibrary();
        Add(library,
            Song("a1", "Alpha", "Album", "Artist", track: 1),
            Song("c1", "Gamma", "Album", "Artist", track: 3));

        var cacheDir = TestSupport.NewTempDir("album-cache");
        var cachePath = Path.Combine(cacheDir, "album-cache.json");
        var provider = new FakeAlbumProvider
        {
            Result = new AlbumProviderResult
            {
                Provider = "FakeCatalog",
                Title = "Album",
                Artist = "Artist",
                Year = 1999,
                Genre = "Rock",
                Tracks =
                {
                    ProviderTrack("Alpha", 1),
                    ProviderTrack("Beta", 2),
                    ProviderTrack("Gamma", 3),
                },
            },
        };

        var service = new AlbumCatalogService(library, provider, cachePath);
        var summary = Assert.Single(service.GetAlbums());

        var album = await service.GetAlbumAsync(summary.Id);

        Assert.NotNull(album);
        Assert.Equal("FakeCatalog", album!.Provider);
        Assert.Equal(3, album.Tracks.Count);
        Assert.Equal(2, album.LocalTrackCount);
        Assert.Equal(1, album.MissingTrackCount);
        Assert.False(album.IsComplete);
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, album.Tracks.Select(t => t.Title));

        var beta = album.Tracks.Single(t => t.Title == "Beta");
        Assert.True(beta.IsMissing);
        Assert.False(beta.IsLocal);
        Assert.Null(beta.SongId);

        var missingTrackCount = album.MissingTrackCount;
        var second = await service.GetAlbumAsync(summary.Id);
        Assert.Equal(1, provider.FindCalls);
        Assert.Equal(missingTrackCount, second!.MissingTrackCount);

        // Summaries (home/search) reuse the cached provider result too.
        Assert.Equal(1, service.GetAlbums().Single().MissingTrackCount);

        // A fresh service loads the cache from disk instead of hitting the provider again.
        var restartedProvider = new FakeAlbumProvider();
        var restarted = new AlbumCatalogService(library, restartedProvider, cachePath);
        var fromDisk = await restarted.GetAlbumAsync(summary.Id);
        Assert.Equal(0, restartedProvider.FindCalls);
        Assert.NotNull(fromDisk);
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, fromDisk!.Tracks.Select(t => t.Title));
        Assert.Equal(1, restarted.GetAlbums().Single().MissingTrackCount);
    }

    [Fact]
    public async Task GetAlbum_KeepsLocalOnlyTracks_AndMatchesByTrackNumberWhenTitlesDiffer()
    {
        using var library = NewLibrary();
        Add(library,
            Song("a1", "Alpha (Local Edit)", "Album", track: 1),
            Song("b1", "Bonus Track", "Album", track: 2));

        var provider = new FakeAlbumProvider
        {
            Result = new AlbumProviderResult
            {
                Provider = "FakeCatalog",
                Title = "Album",
                Artist = "Artist",
                Tracks = { ProviderTrack("Alpha", 1) },
            },
        };

        var service = new AlbumCatalogService(library, provider, Path.Combine(TestSupport.NewTempDir("album-cache"), "album-cache.json"));
        var summary = Assert.Single(service.GetAlbums());

        var album = await service.GetAlbumAsync(summary.Id);

        Assert.NotNull(album);
        Assert.Equal(2, album!.Tracks.Count);
        Assert.All(album.Tracks, t => Assert.True(t.IsLocal));
        Assert.Equal(2, album.LocalTrackCount);
        Assert.Equal(0, album.MissingTrackCount);
        Assert.True(album.IsComplete);
        Assert.Equal("Alpha (Local Edit)", album.Tracks[0].Title);
        Assert.Equal("Bonus Track", album.Tracks[1].Title);
    }

    [Fact]
    public async Task GetAlbumForSong_ResolvesTheContainingAlbum_AndInvalidateRefetches()
    {
        using var library = NewLibrary();
        Add(library, Song("a1", "Alpha", "Album", track: 1));

        var provider = new FakeAlbumProvider
        {
            Result = new AlbumProviderResult
            {
                Provider = "FakeCatalog",
                Title = "Album",
                Artist = "Artist",
                Tracks = { ProviderTrack("Alpha", 1) },
            },
        };
        var service = new AlbumCatalogService(library, provider, Path.Combine(TestSupport.NewTempDir("album-cache"), "album-cache.json"));

        var album = await service.GetAlbumForSongAsync("a1");
        Assert.NotNull(album);
        Assert.Equal("Album", album!.Title);
        Assert.Equal(1, provider.FindCalls);

        service.InvalidateForSong("a1");
        await service.GetAlbumForSongAsync("a1");
        Assert.Equal(2, provider.FindCalls);
    }

    [Fact]
    public void GetLocalSongs_ReturnsOnlyPresentTracksInTrackOrder()
    {
        using var library = NewLibrary();
        Add(library,
            Song("a2", "Second", "Album", track: 2),
            Song("a1", "First", "Album", track: 1),
            Song("a3", "Third", "Album", track: 3, status: FileStatus.Missing));

        var service = new AlbumCatalogService(library, new FakeAlbumProvider());
        var album = Assert.Single(service.GetAlbums());

        var songs = service.GetLocalSongs(album.Id);

        Assert.Equal(new[] { "a1", "a2" }, songs.Select(s => s.Id));
    }
}
