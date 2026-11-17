using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class MetadataProviderGovernorTests
{
    [Fact]
    public async Task Acquire_EnforcesTheMinimumInterval()
    {
        MetadataProviderGovernor.ResetForTests("AcoustID");
        try
        {
            Assert.True(await MetadataProviderGovernor.AcquireAsync("AcoustID", false, CancellationToken.None));

            var watch = Stopwatch.StartNew();
            Assert.True(await MetadataProviderGovernor.AcquireAsync("AcoustID", false, CancellationToken.None));
            watch.Stop();

            // AcoustID is configured for 550 ms between requests (below its 3/s limit).
            Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(450), $"waited only {watch.Elapsed}");

            var status = MetadataProviderGovernor.GetStatuses().First(s => s.Name == "AcoustID");
            Assert.Equal(2, status.RequestsInWindow);
            Assert.Equal(2, status.MaxRequests);
            Assert.NotNull(status.NextAllowedUtc);
            Assert.NotNull(status.LastRequestUtc);
            Assert.Contains("2 requests", status.LimitLabel);
        }
        finally
        {
            MetadataProviderGovernor.ResetForTests("AcoustID");
        }
    }

    [Fact]
    public async Task RateLimitedResponse_EntersCooldownAndSkipsRequests()
    {
        MetadataProviderGovernor.ResetForTests("AcoustID");
        try
        {
            MetadataProviderGovernor.ReportResponse("AcoustID", 429, TimeSpan.FromSeconds(10));

            var status = MetadataProviderGovernor.GetStatuses().First(s => s.Name == "AcoustID");
            Assert.Equal("rate_limited", status.State);
            Assert.False(status.Available);
            Assert.NotNull(status.CooldownUntilUtc);
            Assert.Contains("slow down", status.LastError);

            var watch = Stopwatch.StartNew();
            Assert.False(await MetadataProviderGovernor.AcquireAsync("AcoustID", false, CancellationToken.None));
            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "a long cooldown must skip immediately");
        }
        finally
        {
            MetadataProviderGovernor.ResetForTests("AcoustID");
        }
    }

    [Fact]
    public async Task UnauthorizedResponse_ReportsCredentialsAndCooldown()
    {
        MetadataProviderGovernor.ResetForTests("Discogs");
        try
        {
            MetadataProviderGovernor.ReportResponse("Discogs", 401, null);

            var status = MetadataProviderGovernor.GetStatuses().First(s => s.Name == "Discogs");
            Assert.Equal("unauthorized", status.State);
            Assert.Contains("credentials", status.LastError);
            Assert.False(await MetadataProviderGovernor.AcquireAsync("Discogs", true, CancellationToken.None));
        }
        finally
        {
            MetadataProviderGovernor.ResetForTests("Discogs");
        }
    }

    [Fact]
    public async Task Success_ClearsAnErrorCooldown()
    {
        MetadataProviderGovernor.ResetForTests("AcoustID");
        try
        {
            MetadataProviderGovernor.ReportException("AcoustID", new InvalidOperationException("network down"));
            Assert.Equal("error", MetadataProviderGovernor.GetStatuses().First(s => s.Name == "AcoustID").State);

            MetadataProviderGovernor.ReportResponse("AcoustID", 200, null);
            var status = MetadataProviderGovernor.GetStatuses().First(s => s.Name == "AcoustID");
            Assert.Equal("ready", status.State);
            Assert.Null(status.CooldownUntilUtc);
            Assert.Null(status.LastError);
        }
        finally
        {
            MetadataProviderGovernor.ResetForTests("AcoustID");
        }
    }

    [Fact]
    public void StatusCoverage_IncludesEveryConfiguredProvider()
    {
        var names = MetadataProviderGovernor.GetStatuses().Select(s => s.Name).ToArray();
        foreach (var expected in new[] { "MusicBrainz", "iTunes", "Deezer", "CoverArtArchive", "AcoustID", "Discogs" })
            Assert.Contains(expected, names);
    }
}
