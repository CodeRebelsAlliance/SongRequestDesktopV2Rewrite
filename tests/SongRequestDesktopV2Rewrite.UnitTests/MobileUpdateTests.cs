using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// How each platform applies an update: the desktop swaps its own archive
/// (<see cref="UpdateInstallerTests"/> covers that), Android downloads the release
/// APK and hands it to the system package installer, and iOS leaves updates to the
/// App Store.
/// </summary>
public class MobileUpdateTests
{
    private static (YoutubeFormInterop Interop, ConcurrentQueue<string> Responses) Create()
    {
        var host = new HeadlessHost(new List<Cookie>());
        var interop = new YoutubeFormInterop(host, host.YoutubeService);
        var responses = new ConcurrentQueue<string>();
        interop.SendMessage = responses.Enqueue;
        return (interop, responses);
    }

    [Fact]
    public void ExpectedAssetName_UsesThePackageThePlatformPublishes()
    {
        // Desktop archives (per architecture).
        Assert.Equal("SongRequest-win-x64.zip", UpdateService.ExpectedAssetName("win-x64"));
        Assert.Equal("SongRequest-linux-arm64.tar.gz", UpdateService.ExpectedAssetName("linux-arm64"));
        Assert.Equal("SongRequest-osx-arm64.tar.gz", UpdateService.ExpectedAssetName("osx-arm64"));

        // Mobile packages: one APK / IPA for every device.
        Assert.Equal("SongRequest-android.apk", UpdateService.ExpectedAssetName("android"));
        Assert.Equal("SongRequest-ios.ipa", UpdateService.ExpectedAssetName("ios"));
    }

    [Fact]
    public void SelectAsset_FindsTheAndroidApk()
    {
        var assets = new JArray
        {
            new JObject { ["name"] = "SongRequest-linux-x64.tar.gz", ["browser_download_url"] = "https://example.invalid/linux.tar.gz" },
            new JObject { ["name"] = "SongRequest-android.apk", ["browser_download_url"] = "https://example.invalid/app.apk" },
        };

        var (name, url) = UpdateService.SelectAsset(assets, "android");

        Assert.Equal("SongRequest-android.apk", name);
        Assert.Equal("https://example.invalid/app.apk", url);
    }

    [Fact]
    public void SelectAsset_WithoutAMatchingAsset_ReportsNothing()
    {
        var assets = new JArray
        {
            new JObject { ["name"] = "SongRequest-win-x64.zip", ["browser_download_url"] = "https://example.invalid/win.zip" },
        };

        var (name, url) = UpdateService.SelectAsset(assets, "android");

        Assert.Null(name);
        Assert.Null(url);
    }

    [Fact]
    public async Task InstallUpdate_UsesTheHostsPackageInstallerAndReportsProgress()
    {
        var (interop, responses) = Create();
        var savedInstaller = UpdateService.PackageInstaller;
        var savedSupported = UpdateService.SelfUpdateSupported;

        try
        {
            // What the Android host registers: download the APK, then the installer.
            string? installed = null;
            UpdateService.SelfUpdateSupported = true;
            UpdateService.PackageInstaller = (url, progress) =>
            {
                installed = url;
                progress.Report(new UpdateService.UpdateProgress("downloading", 50, "halfway"));
                progress.Report(new UpdateService.UpdateProgress("installing", 100, "opening the installer"));
                return Task.FromResult<string?>(null);
            };

            interop.HandleMessage(new JObject
            {
                ["type"] = "request",
                ["id"] = "u1",
                ["method"] = "installUpdate",
                ["downloadUrl"] = "https://example.invalid/SongRequest-android.apk",
            }.ToString(Formatting.None));

            for (int i = 0; i < 300; i++)
            {
                if (responses.Count(r => r.Contains("updateProgress")) >= 2) break;
                await Task.Delay(10);
            }

            Assert.Equal("https://example.invalid/SongRequest-android.apk", installed);

            // Progress reaches the UI in the shape it already renders.
            var progressEvents = responses.Where(e => e.Contains("updateProgress")).ToList();
            Assert.Contains(progressEvents, e => e.Contains("halfway"));
            Assert.Contains(progressEvents, e => e.Contains("opening the installer"));
        }
        finally
        {
            UpdateService.PackageInstaller = savedInstaller;
            UpdateService.SelfUpdateSupported = savedSupported;
        }
    }

    [Fact]
    public async Task InstallUpdate_ReportsAPackageInstallersFailure()
    {
        var (interop, responses) = Create();
        var savedInstaller = UpdateService.PackageInstaller;
        var savedSupported = UpdateService.SelfUpdateSupported;

        try
        {
            UpdateService.SelfUpdateSupported = true;
            UpdateService.PackageInstaller = (_, _) => Task.FromResult<string?>("not enough space");

            interop.HandleMessage(new JObject
            {
                ["type"] = "request",
                ["id"] = "u2",
                ["method"] = "installUpdate",
                ["downloadUrl"] = "https://example.invalid/app.apk",
            }.ToString(Formatting.None));

            for (int i = 0; i < 300; i++)
            {
                if (responses.Any(r => r.Contains("not enough space"))) break;
                await Task.Delay(10);
            }

            Assert.Contains(responses, r => r.Contains("not enough space") && r.Contains("updateProgress"));
        }
        finally
        {
            UpdateService.PackageInstaller = savedInstaller;
            UpdateService.SelfUpdateSupported = savedSupported;
        }
    }

    [Fact]
    public async Task InstallUpdate_OnAStoreOnlyHost_IsRefused()
    {
        var (interop, responses) = Create();
        var savedSupported = UpdateService.SelfUpdateSupported;

        try
        {
            UpdateService.SelfUpdateSupported = false;   // iOS: the App Store updates it

            interop.HandleMessage(new JObject
            {
                ["type"] = "request",
                ["id"] = "u3",
                ["method"] = "installUpdate",
                ["downloadUrl"] = "https://example.invalid/app.apk",
            }.ToString(Formatting.None));

            for (int i = 0; i < 200 && responses.IsEmpty; i++) await Task.Delay(10);

            var response = responses.Select(JObject.Parse).First(o => o["id"]?.ToString() == "u3");
            Assert.Contains("app store", response["result"]!["error"]!.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            UpdateService.SelfUpdateSupported = savedSupported;
        }
    }
}
