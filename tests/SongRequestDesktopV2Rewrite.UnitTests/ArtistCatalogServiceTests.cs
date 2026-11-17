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
/// Artist grouping, provider metadata, cache-once behaviour, album linking and
/// local track ordering. Provider parsing lives in
/// <see cref="ArtistMetadataProviderTests"/>.
/// </summary>
public class ArtistCatalogServiceTests
{
    private sealed class FakeArtistProvider : IArtistMetadataProvider
    {
        public ArtistProviderResult Result { get; set; } = new();
        public int FindCalls { get; private set; }
        public int ArtworkCalls { get; private set; }

        public Task<ArtistProviderResult> FindArtistAsync(string artist, CancellationToken ct = default)
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

    private sealed class FakeAlbumProvider : IAlbumMetadataProvider
    {
        public Task<AlbumProviderResult> FindAlbumAsync(string album, string artist, uint year, CancellationToken ct = default)
            => Task.FromResult(new AlbumProviderResult { Error = "No album match found." });

        public Task<ArtworkResult> FetchArtworkAsync(string url, CancellationToken ct = default)
            => Task.FromResult(new ArtworkResult());
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

    private static AlbumCatalogService NewAlbums(LibraryService library)
        => new(library, new FakeAlbumProvider(), Path.Combine(TestSupport.NewTempDir("artist-album-cache"), "album-cache.json"));

    private static LibrarySong Song(
        string id,
        string title,
        string artist,
        string album = "Album",
        string? albumArtist = null,
        uint track = 0,
        uint disc = 0,
        uint year = 0,
        string genre = "",
        int playCount = 0,
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
            PlayCount = playCount,
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

    [Fact]
    public void Grouping_NormalizesArtistNames_AndSkipsMissingOrUntaggedSongs()
    {
        using var library = NewLibrary();
        Add(library,
            Song("a1", "Breathe", "Pink Floyd", track: 1, genre: "Progressive Rock"),
            Song("a2", "Time", "pink   floyd", track: 3),
            Song("b1", "Around the World", "Daft Punk", album: "Homework"),
            Song("m1", "Missing", "Pink Floyd", status: FileStatus.Missing),
            Song("u1", "Untagged", "", albumArtist: ""));

        var artists = new ArtistCatalogService(library, NewAlbums(library), new FakeArtistProvider()).GetArtists();

        Assert.Equal(2, artists.Count);
        var pinkFloyd = artists.Single(a => string.Equals(a.Name, "Pink Floyd", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, pinkFloyd.LocalTrackCount);
        Assert.Equal("Progressive Rock", pinkFloyd.Genre);
        Assert.DoesNotContain(artists, a => a.Name.Length == 0);
        Assert.DoesNotContain(artists, a => a.LocalTrackCount == 3);
    }

    [Fact]
    public void Grouping_FallsBackToAlbumArtist_WhenTrackArtistIsEmpty()
    {
        using var library = NewLibrary();
        Add(library, Song("a1", "Track", "", albumArtist: "Portishead"));

        var artist = Assert.Single(new ArtistCatalogService(library, NewAlbums(library), new FakeArtistProvider()).GetArtists());

        Assert.Equal("Portishead", artist.Name);
        Assert.Equal(1, artist.LocalTrackCount);
    }

    [Fact]
    public void Search_MatchesNameAndGenre_CaseInsensitively()
    {
        using var library = NewLibrary();
        Add(library,
            Song("a1", "Breathe", "Pink Floyd", genre: "Progressive Rock"),
            Song("b1", "Around the World", "Daft Punk", album: "Homework", genre: "Electronic"));

        var service = new ArtistCatalogService(library, NewAlbums(library), new FakeArtistProvider());

        Assert.Single(service.Search("pink"));
        Assert.Single(service.Search("DAFT punk"));
        Assert.Single(service.Search("electronic"));
        Assert.Empty(service.Search("nothing matches"));
    }

    [Fact]
    public async Task GetArtist_LinksAlbums_AndCachesProviderMetadataOnlyOnce()
    {
        using var library = NewLibrary();
        Add(library,
            Song("a1", "Breathe", "Pink Floyd", album: "The Dark Side of the Moon", track: 1, year: 1973, playCount: 1),
            Song("a2", "Time", "Pink Floyd", album: "The Dark Side of the Moon", track: 3, playCount: 1),
            Song("a3", "Shine On", "Pink Floyd", album: "Wish You Were Here", track: 1, year: 1975, playCount: 1));

        var albums = NewAlbums(library);
        var cachePath = Path.Combine(TestSupport.NewTempDir("artist-cache"), "artist-cache.json");
        var provider = new FakeArtistProvider
        {
            Result = new ArtistProviderResult
            {
                Provider = "FakeCatalog",
                Name = "Pink Floyd",
                SortName = "Floyd, Pink",
                Genre = "Progressive Rock",
                Country = "GB",
                Description = "English rock band",
                Albums =
                {
                    new ArtistProviderAlbum { Title = "Wish You Were Here", Year = 1975, Provider = "FakeCatalog" },
                    new ArtistProviderAlbum { Title = "Animals", Year = 1977, Provider = "FakeCatalog" },
                },
            },
        };
        var service = new ArtistCatalogService(library, albums, provider, cachePath);
        var summary = Assert.Single(service.GetArtists());

        var artist = await service.GetArtistAsync(summary.Id);

        Assert.NotNull(artist);
        Assert.Equal("FakeCatalog", artist!.Provider);
        Assert.Equal("Floyd, Pink", artist.SortName);
        Assert.Equal("Progressive Rock", artist.Genre);
        Assert.Equal("GB", artist.Country);
        Assert.Equal("English rock band", artist.Description);
        Assert.Equal(3, artist.LocalTrackCount);
        Assert.Equal(2, artist.AlbumCount);
        Assert.Equal(3, artist.PlayCount);
        Assert.Equal(2, artist.Albums.Count);
        Assert.Contains(artist.Albums, a => a.Title == "Wish You Were Here");
        Assert.Equal(3, artist.CatalogAlbums.Count);
        Assert.Equal(1, artist.MissingAlbumCount);
        var missingAlbum = artist.CatalogAlbums.Single(a => !a.IsLocal);
        Assert.Equal("Animals", missingAlbum.Title);
        Assert.Equal((uint)1977, missingAlbum.Year);
        Assert.Equal(new[] { "The Dark Side of the Moon", "The Dark Side of the Moon", "Wish You Were Here" },
            artist.Tracks.Select(t => t.Album));

        var second = await service.GetArtistAsync(summary.Id);
        Assert.Equal(1, provider.FindCalls);
        Assert.Equal("FakeCatalog", second!.Provider);

        // Summaries reuse the cached metadata too.
        Assert.Equal("Progressive Rock", service.GetArtists().Single().Genre);

        var restartedProvider = new FakeArtistProvider();
        var restarted = new ArtistCatalogService(library, albums, restartedProvider, cachePath);
        var fromDisk = await restarted.GetArtistAsync(summary.Id);
        Assert.Equal(0, restartedProvider.FindCalls);
        Assert.NotNull(fromDisk);
        Assert.Equal("FakeCatalog", fromDisk!.Provider);
        Assert.Equal("Progressive Rock", fromDisk.Genre);
        Assert.Equal(1, fromDisk.MissingAlbumCount);
    }

    [Fact]
    public async Task GetArtistForSong_ResolvesTheArtist_AndInvalidateRefetches()
    {
        using var library = NewLibrary();
        Add(library, Song("a1", "Breathe", "Pink Floyd"));

        var cachePath = Path.Combine(TestSupport.NewTempDir("artist-cache"), "artist-cache.json");
        var provider = new FakeArtistProvider
        {
            Result = new ArtistProviderResult { Provider = "FakeCatalog", Name = "Pink Floyd", Genre = "Rock" },
        };
        var service = new ArtistCatalogService(library, NewAlbums(library), provider, cachePath);

        var artist = await service.GetArtistForSongAsync("a1");
        Assert.NotNull(artist);
        Assert.Equal("Pink Floyd", artist!.Name);
        Assert.Equal(1, provider.FindCalls);

        service.InvalidateForSong("a1");
        await service.GetArtistForSongAsync("a1");
        Assert.Equal(2, provider.FindCalls);
    }

    [Fact]
    public void GetLocalSongs_ReturnsPresentTracksInAlbumThenTrackOrder()
    {
        using var library = NewLibrary();
        Add(library,
            Song("a2", "Second", "Artist", album: "Album", track: 2),
            Song("a1", "First", "Artist", album: "Album", track: 1),
            Song("b1", "Another Album", "Artist", album: "A Album", track: 1),
            Song("a3", "Missing", "Artist", album: "Album", track: 3, status: FileStatus.Missing));

        var service = new ArtistCatalogService(library, NewAlbums(library), new FakeArtistProvider());
        var artist = Assert.Single(service.GetArtists());

        var songs = service.GetLocalSongs(artist.Id);

        Assert.Equal(new[] { "b1", "a1", "a2" }, songs.Select(s => s.Id));
    }

    [Fact]
    public void RecommendedArtists_ReturnsLibraryArtistsWithReasons()
    {
        using var library = NewLibrary();
        Add(library,
            Song("a1", "One", "Artist A", genre: "Rock", playCount: 5),
            Song("b1", "Two", "Artist B", genre: "Pop"));

        var service = new ArtistCatalogService(library, NewAlbums(library), new FakeArtistProvider());
        var recommended = service.RecommendedArtists(10);

        Assert.Equal(2, recommended.Count);
        Assert.All(recommended, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason)));
    }
}
