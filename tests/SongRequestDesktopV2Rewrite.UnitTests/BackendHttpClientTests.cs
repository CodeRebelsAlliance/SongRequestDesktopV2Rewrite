using System;
using System.Linq;
using System.Net.Http;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class BackendHttpClientTests
{
    [Fact]
    public void UserAgent_IdentifiesTheAppAndVersion()
    {
        Assert.Equal($"SongRequest Desktop v{AppInfo.version}", BackendHttpClient.UserAgent);
        Assert.StartsWith("SongRequest Desktop v", BackendHttpClient.UserAgent);
    }

    [Fact]
    public void Create_SetsUserAgentAndBearerToken()
    {
        using var client = BackendHttpClient.Create(new Config { BearerToken = "Bearer secret" }, TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
        Assert.Contains("SongRequest", client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Contains("Desktop", client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Contains("v" + AppInfo.version, client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Equal("Bearer secret", client.DefaultRequestHeaders.GetValues("Authorization").Single());
    }

    [Fact]
    public void Create_WithoutToken_OmitsAuthorization()
    {
        using var client = BackendHttpClient.Create(new Config { BearerToken = "" });

        Assert.False(client.DefaultRequestHeaders.Contains("Authorization"));
        Assert.NotEmpty(client.DefaultRequestHeaders.UserAgent);
    }

    [Fact]
    public void Create_NullConfig_StillSetsUserAgent()
    {
        using var client = BackendHttpClient.Create(null);

        Assert.NotEmpty(client.DefaultRequestHeaders.UserAgent);
        Assert.False(client.DefaultRequestHeaders.Contains("Authorization"));
    }

    [Fact]
    public void ApplyUserAgent_IsIdempotent()
    {
        using var once = new HttpClient();
        BackendHttpClient.ApplyUserAgent(once);
        var expected = string.Join(" ", once.DefaultRequestHeaders.UserAgent);

        using var twice = new HttpClient();
        BackendHttpClient.ApplyUserAgent(twice);
        BackendHttpClient.ApplyUserAgent(twice);

        // Applying twice must not duplicate the header or its values.
        Assert.Equal(expected, string.Join(" ", twice.DefaultRequestHeaders.UserAgent));
        Assert.Single(twice.DefaultRequestHeaders, h => h.Key == "User-Agent");
    }

    [Fact]
    public void ApplyUserAgent_NullClient_DoesNotThrow()
        => BackendHttpClient.ApplyUserAgent(null!);
}
