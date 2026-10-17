using System.Net;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class FetchingServiceTests
{
    private static (FetchingService Svc, StubHttpHandler Handler) Create(HttpStatusCode status = HttpStatusCode.OK, string body = "ok")
    {
        var handler = new StubHttpHandler(status, body);
        var svc = new FetchingService("Bearer secret", new HttpClient(handler));
        return (svc, handler);
    }

    [Fact]
    public async Task SendRequest_PostsToConfiguredEndpointWithAuthHeader()
    {
        var (svc, handler) = Create();

        var result = await svc.SendRequest("get-bad-words");

        Assert.Equal("ok", result);
        Assert.Equal("POST", handler.LastMethod);
        Assert.Contains("/fetch?method=get-bad-words", handler.LastUrl);
        Assert.True(handler.LastHeaders.ContainsKey("Authorization"));
        Assert.Equal("Bearer secret", handler.LastHeaders["Authorization"]);
    }

    [Fact]
    public async Task SendRequest_IdentifiesTheAppWithACustomUserAgent()
    {
        var (svc, handler) = Create();

        await svc.SendRequest("get-bad-words");

        Assert.True(handler.LastHeaders.ContainsKey("User-Agent"), "no User-Agent was sent");
        var userAgent = handler.LastHeaders["User-Agent"];

        // .NET exposes the parsed products; the wire value is the space-joined form
        // "SongRequest Desktop v<version>" (verified by the integration test).
        Assert.Contains("SongRequest", userAgent);
        Assert.Contains("Desktop", userAgent);
        Assert.Contains("v" + AppInfo.version, userAgent);
    }

    [Fact]
    public async Task SendRequest_AddBadWord_SendsWordField()
    {
        var (svc, handler) = Create();

        await svc.SendRequest("add-bad-word", "badword");

        Assert.Contains("name=word", handler.LastBody);
        Assert.Contains("badword", handler.LastBody);
    }

    [Fact]
    public async Task SendRequest_SetSendinAllowed_SendsFlagField()
    {
        var (svc, handler) = Create();

        await svc.SendRequest("set-sendin-allowed", "true");

        Assert.Contains("name=sendin-allowed", handler.LastBody);
        Assert.Contains("true", handler.LastBody);
    }

    [Fact]
    public async Task SendRequest_SetNowPlaying_StripsTopicSuffixAndSendsBothFields()
    {
        var (svc, handler) = Create();

        await svc.SendRequest("set-now-playing", "Song Title", "Artist - Topic");

        Assert.Contains("name=now-playing-title", handler.LastBody);
        Assert.Contains("name=now-playing-artist", handler.LastBody);
        Assert.Contains("Song Title", handler.LastBody);
        Assert.Contains("Artist", handler.LastBody);
        Assert.DoesNotContain("Artist - Topic", handler.LastBody);
    }

    [Fact]
    public async Task SendRequest_DefaultAction_SendsYtidField()
    {
        var (svc, handler) = Create();

        await svc.SendRequest("approve", "abc123");

        Assert.Contains("name=ytid", handler.LastBody);
        Assert.Contains("abc123", handler.LastBody);
    }

    [Fact]
    public async Task SendRequest_NonSuccessStatus_ThrowsWrappedException()
    {
        var (svc, _) = Create(HttpStatusCode.InternalServerError, "boom");

        var ex = await Assert.ThrowsAsync<Exception>(() => svc.SendRequest("approve", "x"));

        Assert.Contains("Request failed", ex.Message);
    }
}
