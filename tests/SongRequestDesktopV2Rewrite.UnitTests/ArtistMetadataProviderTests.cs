using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Artist lookups against each enabled metadata provider, routed to canned
/// payloads so no real API is called.
/// </summary>
public class ArtistMetadataProviderTests
{
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<string> Urls { get; } = new();

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(_responder(request));
        }
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task MusicBrainz_ArtistLookup_ReturnsSortNameCountryAndGenre()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath.Contains("/artist/mb-1")
            ? Json("""
              {"id":"mb-1","name":"Pink Floyd","sort-name":"Floyd, Pink","country":"GB",
               "disambiguation":"English rock band","genres":[{"name":"progressive rock"}],
               "release-groups":[
                 {"id":"rg-1","title":"Wish You Were Here","first-release-date":"1975-09-12","primary-type":"Album"},
                 {"id":"rg-2","title":"Shine On","first-release-date":"1992-11-02","primary-type":"Single"}
               ]}
              """)
            : Json("""{"artists":[{"id":"mb-1","name":"Pink Floyd","sort-name":"Floyd, Pink"}]}"""));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            MusicBrainz = true, ITunes = false, Deezer = false, CoverArtArchive = true, Discogs = false
        });

        var artist = await service.FindArtistAsync("Pink Floyd");

        Assert.Equal("MusicBrainz", artist.Provider);
        Assert.Equal("Pink Floyd", artist.Name);
        Assert.Equal("Floyd, Pink", artist.SortName);
        Assert.Equal("GB", artist.Country);
        Assert.Equal("English rock band", artist.Description);
        Assert.Equal("progressive rock", artist.Genre);
        var album = Assert.Single(artist.Albums);
        Assert.Equal("Wish You Were Here", album.Title);
        Assert.Equal((uint)1975, album.Year);
        Assert.Contains("release-group/rg-1", album.ArtworkUrl);
        Assert.Contains(handler.Urls, u => u.Contains("inc=genres"));
        Assert.Contains(handler.Urls, u => u.Contains("release-groups"));
    }

    [Fact]
    public async Task Deezer_ArtistLookup_ReturnsArtwork()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath.Contains("/albums")
            ? Json("""
              {"data":[
                {"title":"Discovery","release_date":"2001-03-12","record_type":"album","cover_big":"https://cdn/discovery.jpg"},
                {"title":"One More Time","release_date":"2000-11-13","record_type":"single"}
              ]}
              """)
            : Json("""
              {"data":[{"id":27,"name":"Daft Punk","picture_xl":"https://cdn/artist.jpg"}]}
              """));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            MusicBrainz = false, ITunes = false, Deezer = true, CoverArtArchive = false, Discogs = false
        });

        var artist = await service.FindArtistAsync("Daft Punk");

        Assert.Equal("Deezer", artist.Provider);
        Assert.Equal("Daft Punk", artist.Name);
        Assert.Equal("https://cdn/artist.jpg", artist.ArtworkUrl);
        var album = Assert.Single(artist.Albums);
        Assert.Equal("Discovery", album.Title);
        Assert.Equal((uint)2001, album.Year);
        Assert.Equal("https://cdn/discovery.jpg", album.ArtworkUrl);
        Assert.Contains("search/artist", handler.Urls[0]);
        Assert.Contains(handler.Urls, u => u.Contains("/albums"));
    }

    [Fact]
    public async Task ITunes_ArtistLookup_ReturnsNameAndGenre()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.ToString().Contains("entity=album")
            ? Json("""
              {"results":[
                {"wrapperType":"collection","collectionType":"Album","collectionName":"Discovery",
                 "releaseDate":"2001-03-12T08:00:00Z","artworkUrl100":"https://is1.mzstatic.com/image/100x100bb.jpg"},
                {"wrapperType":"collection","collectionType":"Single","collectionName":"One More Time"}
              ]}
              """)
            : Json("""
              {"results":[{"artistName":"Daft Punk","primaryGenreName":"Electronic","artistId":5}]}
              """));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            MusicBrainz = false, ITunes = true, Deezer = false, CoverArtArchive = false, Discogs = false
        });

        var artist = await service.FindArtistAsync("Daft Punk");

        Assert.Equal("iTunes", artist.Provider);
        Assert.Equal("Daft Punk", artist.Name);
        Assert.Equal("Electronic", artist.Genre);
        Assert.Null(artist.ArtworkUrl);
        var album = Assert.Single(artist.Albums);
        Assert.Equal("Discovery", album.Title);
        Assert.Contains("600x600", album.ArtworkUrl);
        Assert.Contains("entity=musicArtist", handler.Urls[0]);
        Assert.Contains(handler.Urls, u => u.Contains("entity=album"));
    }

    [Fact]
    public async Task Discogs_ArtistLookup_ReturnsProfileAndImage()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath.Contains("/artists/17/releases")
            ? Json("""
              {"releases":[
                {"id":1,"title":"Discovery","year":2001,"role":"Main","type":"master","thumb":"https://img/discovery.jpg"},
                {"id":2,"title":"Featured Single","year":2002,"role":"TrackAppearance","type":"release"}
              ]}
              """)
            : request.RequestUri!.AbsolutePath.Contains("/artists/17")
              ? Json("""
                {"id":17,"name":"Daft Punk","profile":"French electronic duo",
                 "images":[{"uri":"https://img/discogs-artist.jpg"}]}
                """)
              : Json("""{"results":[{"id":17,"title":"Daft Punk"}]}"""));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            MusicBrainz = false, ITunes = false, Deezer = false, CoverArtArchive = false,
            Discogs = true, DiscogsToken = "token"
        });

        var artist = await service.FindArtistAsync("Daft Punk");

        Assert.Equal("Discogs", artist.Provider);
        Assert.Equal("Daft Punk", artist.Name);
        Assert.Equal("French electronic duo", artist.Description);
        Assert.Equal("https://img/discogs-artist.jpg", artist.ArtworkUrl);
        var album = Assert.Single(artist.Albums);
        Assert.Equal("Discovery", album.Title);
        Assert.Equal((uint)2001, album.Year);
        Assert.Equal("https://img/discovery.jpg", album.ArtworkUrl);
    }

    [Fact]
    public async Task ArtistLookup_MergesArtworkFromAnotherProvider()
    {
        var handler = new RoutingHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("musicbrainz.org"))
                return url.Contains("/artist/mb-1")
                    ? Json("""{"id":"mb-1","name":"Pink Floyd","genres":[{"name":"rock"}]}""")
                    : Json("""{"artists":[{"id":"mb-1","name":"Pink Floyd"}]}""");
            return Json("""{"data":[{"id":9,"name":"Pink Floyd","picture_xl":"https://cdn/pf.jpg"}]}""");
        });
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            MusicBrainz = true, ITunes = false, Deezer = true, CoverArtArchive = false, Discogs = false
        });

        var artist = await service.FindArtistAsync("Pink Floyd");

        Assert.Equal("Pink Floyd", artist.Name);
        Assert.Equal("rock", artist.Genre);
        Assert.Equal("https://cdn/pf.jpg", artist.ArtworkUrl);
    }
}
