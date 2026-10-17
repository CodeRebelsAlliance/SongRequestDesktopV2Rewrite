using System.Net;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.1", "1.0", true)]
    [InlineData("1.0", "1.1", false)]
    [InlineData("1.0", "1.0", false)]
    [InlineData("v2.0", "1.9", true)]
    [InlineData("1.2.1", "1.2", true)]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.10", "1.9", true)]
    [InlineData("garbage", "1.0", false)]
    [InlineData("3.17", "3.17", false)]
    public void IsNewerVersion_ComparesNumerically(string latest, string current, bool expected)
        => Assert.Equal(expected, UpdateService.IsNewerVersion(latest, current));

    [Fact]
    public void CurrentPlatform_IsAKnownRuntimeIdentifier()
        => Assert.Matches("^(win|linux|osx)-(x64|x86|arm64|arm)$", UpdateService.CurrentPlatform());

    [Theory]
    [InlineData("win-x64", "SongRequest-win-x64.zip")]
    [InlineData("win-arm64", "SongRequest-win-arm64.zip")]
    [InlineData("linux-x64", "SongRequest-linux-x64.tar.gz")]
    [InlineData("osx-arm64", "SongRequest-osx-arm64.tar.gz")]
    public void ExpectedAssetName_UsesPlatformExtension(string platform, string expected)
        => Assert.Equal(expected, UpdateService.ExpectedAssetName(platform));

    [Fact]
    public void SelectAsset_MatchesThePlatformAsset()
    {
        var assets = JArray.Parse(
            "[{\"name\":\"SongRequest-linux-x64.tar.gz\",\"browser_download_url\":\"https://x/linux\"}," +
            " {\"name\":\"SongRequest-win-x64.zip\",\"browser_download_url\":\"https://x/win\"}]");

        var (name, url) = UpdateService.SelectAsset(assets, "linux-x64");

        Assert.Equal("SongRequest-linux-x64.tar.gz", name);
        Assert.Equal("https://x/linux", url);
    }

    [Fact]
    public void SelectAsset_ReturnsNullWhenNoAssetMatches()
    {
        var assets = JArray.Parse("[{\"name\":\"SongRequest-win-x64.zip\",\"browser_download_url\":\"https://x/win\"}]");

        var (name, url) = UpdateService.SelectAsset(assets, "linux-x64");

        Assert.Null(name);
        Assert.Null(url);
    }

    [Fact]
    public void SelectAsset_FallsBackToLegacyWindowsZipOnly()
    {
        var assets = JArray.Parse("[{\"name\":\"songrequest.zip\",\"browser_download_url\":\"https://x/legacy\"}]");

        var (winName, winUrl) = UpdateService.SelectAsset(assets, "win-x64");
        Assert.Equal("songrequest.zip", winName);
        Assert.Equal("https://x/legacy", winUrl);

        var (linuxName, _) = UpdateService.SelectAsset(assets, "linux-x64");
        Assert.Null(linuxName);
    }

    [Fact]
    public void SelectAsset_NullAssets_ReturnsNull()
        => Assert.Null(UpdateService.SelectAsset(null, "win-x64").Name);

    [Fact]
    public async Task CheckForUpdatesAsync_SelectsThePlatformAsset()
    {
        var asset = UpdateService.ExpectedAssetName(UpdateService.CurrentPlatform());
        var json = $"{{\"tag_name\":\"v99.0\",\"body\":\"notes\",\"assets\":[{{\"name\":\"{asset}\",\"browser_download_url\":\"https://x/a\"}}]}}";
        using var client = new HttpClient(new StubHttpHandler(HttpStatusCode.OK, json));

        var info = await UpdateService.CheckForUpdatesAsync(client);

        Assert.True(info.UpdateAvailable);
        Assert.Equal("v99.0", info.LatestVersion);
        Assert.Equal("https://x/a", info.DownloadUrl);
        Assert.Equal("notes", info.ReleaseNotes);
        Assert.Equal(UpdateService.CurrentPlatform(), info.Platform);
        Assert.Equal(asset, info.AssetName);
        Assert.Null(info.Error);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_NoPlatformAsset_ReportsError()
    {
        using var client = new HttpClient(new StubHttpHandler(HttpStatusCode.OK, "{\"tag_name\":\"v99.0\",\"assets\":[]}"));

        var info = await UpdateService.CheckForUpdatesAsync(client);

        Assert.False(info.UpdateAvailable);
        Assert.NotNull(info.Error);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_AlreadyUpToDate_NoUpdate()
    {
        var asset = UpdateService.ExpectedAssetName(UpdateService.CurrentPlatform());
        var json = $"{{\"tag_name\":\"v0.1\",\"assets\":[{{\"name\":\"{asset}\",\"browser_download_url\":\"https://x/a\"}}]}}";
        using var client = new HttpClient(new StubHttpHandler(HttpStatusCode.OK, json));

        var info = await UpdateService.CheckForUpdatesAsync(client);

        Assert.False(info.UpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_HttpFailure_ReturnsPlatformAndError()
    {
        using var client = new HttpClient(new StubHttpHandler(HttpStatusCode.InternalServerError, "boom"));

        var info = await UpdateService.CheckForUpdatesAsync(client);

        Assert.False(info.UpdateAvailable);
        Assert.Equal(UpdateService.CurrentPlatform(), info.Platform);
        Assert.NotNull(info.Error);
    }
}
