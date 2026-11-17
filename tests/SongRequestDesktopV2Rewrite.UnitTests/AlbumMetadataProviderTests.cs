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
/// Album (release) lookups against each enabled metadata provider. The HTTP
/// transport is routed to canned provider payloads, so no real API is called.
/// </summary>
public class AlbumMetadataProviderTests
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
    public async Task ITunes_AlbumLookup_ReturnsOrderedTracksAndMetadata()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.ToString().Contains("/lookup")
            ? Json("""
              {"results":[
                {"wrapperType":"collection","collectionId":123,"collectionName":"Album","artistName":"Artist"},
                {"wrapperType":"track","trackName":"One","artistName":"Artist","trackNumber":1,"discNumber":1,"trackTimeMillis":180000},
                {"wrapperType":"track","trackName":"Two","artistName":"Artist","trackNumber":2,"discNumber":1,"trackTimeMillis":210000}
              ]}
              """)
            : Json("""
              {"results":[{"collectionId":123,"collectionName":"Album","artistName":"Artist",
                "releaseDate":"2021-03-05T08:00:00Z","primaryGenreName":"Alternative",
                "artworkUrl100":"https://is1.mzstatic.com/image/100x100bb.jpg"}]}
              """));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            MusicBrainz = false, ITunes = true, Deezer = false, CoverArtArchive = false, Discogs = false
        });

        var album = await service.FindAlbumAsync("Album", "Artist", 2021);

        Assert.Equal("iTunes", album.Provider);
        Assert.Equal("Album", album.Title);
        Assert.Equal((uint)2021, album.Year);
        Assert.Equal("Alternative", album.Genre);
        Assert.Contains("600x600", album.ArtworkUrl);
        Assert.Equal(new[] { "One", "Two" }, album.Tracks.ConvertAll(t => t.Title));
        Assert.Equal(TimeSpan.FromMinutes(3), album.Tracks[0].Duration);
        Assert.Equal((uint)2, album.Tracks[1].Track);
        Assert.Contains(handler.Urls, u => u.Contains("entity=album"));
        Assert.Contains(handler.Urls, u => u.Contains("entity=song"));
    }

    [Fact]
    public async Task Deezer_AlbumLookup_ReturnsTracks()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath.Contains("/album/42")
            ? Json("""
              {"id":42,"title":"Album","artist":{"name":"Artist"},"release_date":"2019-09-13",
               "genres":{"data":[{"name":"Electronic"}]},"cover_xl":"https://cdn/cover.jpg",
               "tracks":{"data":[
                 {"title":"First","artist":{"name":"Artist"},"duration":190,"track_position":1,"disk_number":1},
                 {"title":"Second","artist":{"name":"Artist"},"duration":240,"track_position":2,"disk_number":1}]}}
              """)
            : Json("""{"data":[{"id":42,"title":"Album","artist":{"name":"Artist"}}]}"""));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            MusicBrainz = false, ITunes = false, Deezer = true, CoverArtArchive = false, Discogs = false
        });

        var album = await service.FindAlbumAsync("Album", "Artist", 2019);

        Assert.Equal("Deezer", album.Provider);
        Assert.Equal((uint)2019, album.Year);
        Assert.Equal("Electronic", album.Genre);
        Assert.Equal("https://cdn/cover.jpg", album.ArtworkUrl);
        Assert.Equal(2, album.Tracks.Count);
        Assert.Equal(TimeSpan.FromSeconds(240), album.Tracks[1].Duration);
        Assert.Equal((uint)2, album.Tracks[1].Track);
    }

    [Fact]
    public async Task MusicBrainz_AlbumLookup_ReturnsDiscAndTrackNumbersAndCoverArt()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath.Contains("/release/rel-1")
            ? Json("""
              {"id":"rel-1","title":"Album","date":"2015-06-01",
               "artist-credit":[{"name":"Artist"}],
               "tags":[{"name":"Rock"}],
               "media":[
                 {"position":1,"tracks":[
                   {"title":"Disc One Track One","number":"1","length":120000},
                   {"title":"Disc One Track Two","number":"2","length":125000}]},
                 {"position":2,"tracks":[
                   {"title":"Disc Two Track One","number":"1","length":130000}]}
               ]}
              """)
            : Json("""{"releases":[{"id":"rel-1","title":"Album","date":"2015-06-01"}]}"""));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            MusicBrainz = true, ITunes = false, Deezer = false, CoverArtArchive = true, Discogs = false
        });

        var album = await service.FindAlbumAsync("Album", "Artist", 2015);

        Assert.Equal("MusicBrainz", album.Provider);
        Assert.Equal("https://coverartarchive.org/release/rel-1/front-500", album.ArtworkUrl);
        Assert.Equal(3, album.Tracks.Count);
        Assert.Equal((uint)2, album.Tracks[2].Disc);
        Assert.Equal("Disc Two Track One", album.Tracks[2].Title);
        Assert.Contains("inc=recordings", handler.Urls[1]);
    }

    [Fact]
    public async Task Discogs_AlbumLookup_ParsesHeadingDiscsAndDurations()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.AbsolutePath.Contains("/releases/77")
            ? Json("""
              {"id":77,"title":"Album","year":"2001",
               "artists":[{"name":"Artist"}],
               "genres":["Rock"],
               "images":[{"uri":"https://img/discogs.jpg"}],
               "tracklist":[
                 {"type_":"heading","title":"Side A"},
                 {"type_":"track","position":"A1","title":"First","duration":"3:05"},
                 {"type_":"track","position":"A2","title":"Second","duration":"4:10"},
                 {"type_":"heading","title":"Side B"},
                 {"type_":"track","position":"B1","title":"Third","duration":"2:59"}
               ]}
              """)
            : Json("""{"results":[{"id":77,"title":"Album - Artist"}]}"""));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            MusicBrainz = false, ITunes = false, Deezer = false, CoverArtArchive = false,
            Discogs = true, DiscogsToken = "token"
        });

        var album = await service.FindAlbumAsync("Album", "Artist", 2001);

        Assert.Equal("Discogs", album.Provider);
        Assert.Equal((uint)2001, album.Year);
        Assert.Equal("Rock", album.Genre);
        Assert.Equal(3, album.Tracks.Count);
        Assert.Equal((uint)1, album.Tracks[0].Track);
        Assert.Equal((uint)1, album.Tracks[0].Disc);
        Assert.Equal((uint)2, album.Tracks[2].Disc);
        Assert.Equal("Third", album.Tracks[2].Title);
        Assert.Equal(TimeSpan.FromSeconds(185), album.Tracks[0].Duration);
    }
}
