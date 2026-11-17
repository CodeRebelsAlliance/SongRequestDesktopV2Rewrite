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

public class MetadataOnlineServiceTests
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
    public async Task Search_ParsesITunesResultsAndUpscalesArtwork()
    {
        var handler = new RoutingHandler(_ => Json("""
        {"results":[{"trackId":1,"trackName":"Song","artistName":"Artist","collectionName":"Album",
        "primaryGenreName":"Pop","releaseDate":"2024-05-01T07:00:00Z","trackNumber":4,"discNumber":2,
        "trackTimeMillis":201000,"artworkUrl100":"https://is1.mzstatic.com/image/100x100bb.jpg",
        "trackViewUrl":"https://music.apple.com/song"}]}
        """));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            ITunes = true, MusicBrainz = false, Deezer = false, CoverArtArchive = false
        });

        var result = await service.SearchAsync("Artist Song", TimeSpan.FromSeconds(201));

        Assert.Single(result.Candidates);
        var candidate = result.Candidates[0];
        Assert.Equal("iTunes", candidate.Provider);
        Assert.Equal("Song", candidate.Title);
        Assert.Equal("Artist", candidate.Artist);
        Assert.Equal((uint)2024, candidate.Year);
        Assert.Equal((uint)4, candidate.Track);
        Assert.Equal((uint)2, candidate.Disc);
        Assert.Equal("https://is1.mzstatic.com/image/600x600bb.jpg", candidate.ArtworkUrl);
        Assert.True(candidate.Score > 0.7);
    }

    [Fact]
    public async Task Search_ParsesDeezerResults()
    {
        var handler = new RoutingHandler(_ => Json("""
        {"data":[{"id":99,"title":"Song","duration":180,"track_position":2,"disk_number":1,
        "artist":{"name":"Artist"},"album":{"title":"Album","cover_xl":"https://cdn/cover.jpg"},
        "link":"https://deezer/song"}]}
        """));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            Deezer = true, MusicBrainz = false, ITunes = false, CoverArtArchive = false
        });

        var result = await service.SearchAsync("Artist Song", TimeSpan.FromSeconds(180));

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Deezer", candidate.Provider);
        Assert.Equal("Album", candidate.Album);
        Assert.Equal(TimeSpan.FromSeconds(180), candidate.Duration);
        Assert.Equal("https://cdn/cover.jpg", candidate.ArtworkUrl);
        Assert.Null(candidate.Error);
    }

    [Fact]
    public async Task Search_ParsesMusicBrainzAndAddsCoverArtArchiveArtwork()
    {
        var handler = new RoutingHandler(_ => Json("""
        {"recordings":[{"id":"rec-1","title":"Song","length":215000,
        "artist-credit":[{"name":"Artist"}],
        "releases":[{"id":"release-1","title":"Album","date":"2023-01-01"}]}]}
        """));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            MusicBrainz = true, CoverArtArchive = true, ITunes = false, Deezer = false
        });

        var result = await service.SearchAsync("Artist Song", TimeSpan.FromMilliseconds(215000));

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("MusicBrainz", candidate.Provider);
        Assert.Equal("Album", candidate.Album);
        Assert.Equal((uint)2023, candidate.Year);
        Assert.Equal("https://coverartarchive.org/release/release-1/front-500", candidate.ArtworkUrl);
        Assert.Contains("musicbrainz.org", handler.Urls[0]);
    }

    [Fact]
    public async Task Identify_UsesTheFileNameWhenMetadataIsMissing()
    {
        var handler = new RoutingHandler(request => request.RequestUri!.ToString().Contains("itunes")
            ? Json("""{"results":[{"trackName":"Real Title","artistName":"Real Artist","trackTimeMillis":120000}]}""")
            : Json("""{"recordings":[]}"""));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            ITunes = true, MusicBrainz = false, Deezer = false, CoverArtArchive = false
        });

        var result = await service.IdentifyAsync(
            title: "03 - track_name",
            artist: "Unknown",
            album: string.Empty,
            duration: TimeSpan.FromSeconds(120),
            filePath: "/music/Real Artist - Real Title.mp3");

        Assert.NotNull(result.Best);
        Assert.Equal("Real Title", result.Best!.Title);
        Assert.Contains("Real Artist", result.Query);
        Assert.True(result.Best.Score > 0.7);
    }

    [Theory]
    [InlineData("Song", "Artist", "Song", "Artist", 1.0)]
    [InlineData("Song (Official Video)", "Artist", "Song", "Artist", 1.0)]
    [InlineData("Completely different", "Other", "Song", "Artist", 0.0)]
    public void Similarity_IgnoresDecorativeSuffixes(string title, string artist, string wantedTitle, string wantedArtist, double expectedTitle)
    {
        var titleScore = MetadataOnlineService.Similarity(title, wantedTitle);
        var artistScore = MetadataOnlineService.Similarity(artist, wantedArtist);

        Assert.Equal(expectedTitle, titleScore, 2);
        Assert.Equal(expectedTitle, artistScore, 2);
    }

    [Fact]
    public async Task FetchArtwork_ReturnsBytesAndDetectsAMimeType()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        });
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings());

        var artwork = await service.FetchArtworkAsync("https://example.com/cover.png");

        Assert.True(artwork.Bytes is { Length: > 0 });
        Assert.Equal("image/png", artwork.MimeType);
    }

    [Fact]
    public async Task FetchArtwork_RejectsANonImagePayload()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 }),
        });
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings());

        var artwork = await service.FetchArtworkAsync("https://example.com/not-an-image");

        Assert.Null(artwork.Bytes);
        Assert.NotNull(artwork.Error);
    }

    [Fact]
    public void ParseAcoustId_MapsRecordingsAndReleaseGroupArtwork()
    {
        var json = Newtonsoft.Json.Linq.JObject.Parse("""
        {"results":[{"score":0.97,"recordings":[{"id":"rec-9","title":"Fingerprinted Song",
        "artists":[{"name":"Fingerprint Artist"}],
        "releasegroups":[{"id":"rg-9","title":"Fingerprint Album","type":"Album"}]}]}]}
        """);

        var candidates = MetadataOnlineService.ParseAcoustId(json);

        var candidate = Assert.Single(candidates);
        Assert.Equal("AcoustID", candidate.Provider);
        Assert.Equal("Fingerprinted Song", candidate.Title);
        Assert.Equal("Fingerprint Artist", candidate.Artist);
        Assert.Equal("Fingerprint Album", candidate.Album);
        Assert.Equal("https://coverartarchive.org/release-group/rg-9/front-500", candidate.ArtworkUrl);
        Assert.Equal(0.97, candidate.Score, 2);
    }

    [Fact]
    public async Task Search_ParsesDiscogsResults()
    {
        var handler = new RoutingHandler(_ => Json("""
        {"results":[{"id":123,"title":"Artist - Album","year":"2022","genre":["Electronic"],
        "style":["House"],"cover_image":"https://i.discogs.com/cover.jpg",
        "resource_url":"https://api.discogs.com/releases/123"}]}
        """));
        using var http = new HttpClient(handler);
        var service = new MetadataOnlineService(http, new MetadataProviderSettings
        {
            Discogs = true, MusicBrainz = false, ITunes = false, Deezer = false, CoverArtArchive = false
        });

        var result = await service.SearchAsync("Artist Album", TimeSpan.Zero);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Discogs", candidate.Provider);
        Assert.Equal("Artist", candidate.Artist);
        Assert.Equal("Album", candidate.Title);
        Assert.Equal((uint)2022, candidate.Year);
        Assert.Equal("https://i.discogs.com/cover.jpg", candidate.ArtworkUrl);
    }

    [Fact]
    public void NormalizePriority_KeepsKnownProvidersAndAppendsMissingOnes()
    {
        var priority = MetadataProviderSettings.NormalizePriority(new[] { "discogs", "iTunes", "Discogs", "Unknown" });

        Assert.Equal("Discogs", priority[0]);
        Assert.Equal("iTunes", priority[1]);
        Assert.Contains("MusicBrainz", priority);
        Assert.Contains("AcoustID", priority);
        Assert.DoesNotContain("Unknown", priority);
        Assert.Equal(priority.Count, priority.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ParseFileName_SplitsArtistAndTitle()
    {
        var (artist, title) = MetadataOnlineService.ParseFileName("/music/Artist - Title (Official Video).mp3");
        Assert.Equal("Artist", artist);
        Assert.Equal("Title", title);
    }
}
