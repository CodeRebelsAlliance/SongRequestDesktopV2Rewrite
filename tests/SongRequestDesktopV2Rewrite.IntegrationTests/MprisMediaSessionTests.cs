using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SongRequestDesktopV2Rewrite;
using Tmds.DBus;
using Xunit;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

/// <summary>
/// Exercises the Linux MPRIS backend against the real session bus: registers the
/// service, reads the exported properties back through a D-Bus proxy, drives the
/// transport methods and observes the <c>PropertiesChanged</c> signal.
/// </summary>
public class MprisMediaSessionTests
{
    private static string NewServiceName()
        => "org.mpris.MediaPlayer2.songrequest.it" + Guid.NewGuid().ToString("N")[..8];

    [SkippableFact]
    public async Task Mpris_ExposesMetadataStateAndRootProperties()
    {
        if (!OperatingSystem.IsLinux()) Skip.If(true, "Linux only");
        if (string.IsNullOrEmpty(Address.Session)) Skip.If(true, "No D-Bus session bus");

        var service = NewServiceName();
        var session = await MprisMediaSession.TryCreateAsync(service);
        Skip.If(session == null, "Could not register MPRIS service on the session bus");

        var connection = new Connection(Address.Session!);
        await connection.ConnectAsync();
        try
        {
            var root = connection.CreateProxy<IMprisRoot>(service, MprisMediaSession.RootObjectPath);
            var player = connection.CreateProxy<IMprisPlayer>(service, MprisMediaSession.RootObjectPath);

            var rootProps = await root.GetAllRootAsync();
            Assert.Equal("SongRequest V2", rootProps["Identity"]);
            Assert.Equal(false, rootProps["CanRaise"]);

            var initial = await player.GetAllPlayerAsync();
            Assert.Equal("Stopped", initial["PlaybackStatus"]);
            Assert.Equal(true, initial["CanControl"]);

            session.UpdatePlayback(MediaPlaybackState.Playing, TimeSpan.FromSeconds(42));
            session.UpdateMetadata(new MediaMetadata(
                "Song Title", "Song Artist", "Song Album", TimeSpan.FromSeconds(200), "/tmp/art.png"));

            var props = await player.GetAllPlayerAsync();
            Assert.Equal("Playing", props["PlaybackStatus"]);
            Assert.Equal(42_000_000L, props["Position"]);

            var metadata = Assert.IsAssignableFrom<IDictionary<string, object>>(props["Metadata"]);
            Assert.Equal("Song Title", metadata["xesam:title"]);
            Assert.Equal(new[] { "Song Artist" }, Assert.IsType<string[]>(metadata["xesam:artist"]));
            Assert.Equal("Song Album", metadata["xesam:album"]);
            Assert.Equal(200_000_000L, metadata["mpris:length"]);
            Assert.Equal("file:///tmp/art.png", metadata["mpris:artUrl"]);
            Assert.IsType<ObjectPath>(metadata["mpris:trackid"]);

            // Single-property read.
            Assert.Equal("Playing", await player.GetPlayerAsync("PlaybackStatus"));
            Assert.Equal("SongRequest V2", await root.GetRootAsync("Identity"));
        }
        finally
        {
            connection.Dispose();
            session!.Dispose();
        }
    }

    [Fact]
    public void ArtworkUri_EscapesLocalPathsAndKeepsWebUrls()
    {
        var local = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "art work", "cover.png");
        var uri = MprisMediaSession.ToArtworkUri(local);

        Assert.NotNull(uri);
        Assert.StartsWith("file://", uri);
        Assert.Contains("%20", uri);
        Assert.EndsWith("/cover.png", uri);
        Assert.Equal("https://example.test/cover.png",
            MprisMediaSession.ToArtworkUri("https://example.test/cover.png"));
    }

    [SkippableFact]
    public async Task Mpris_TransportCommandsRaiseEvents()
    {
        if (!OperatingSystem.IsLinux()) Skip.If(true, "Linux only");
        if (string.IsNullOrEmpty(Address.Session)) Skip.If(true, "No D-Bus session bus");

        var service = NewServiceName();
        var session = await MprisMediaSession.TryCreateAsync(service);
        Skip.If(session == null, "Could not register MPRIS service on the session bus");

        var connection = new Connection(Address.Session!);
        await connection.ConnectAsync();
        try
        {
            var root = connection.CreateProxy<IMprisRoot>(service, MprisMediaSession.RootObjectPath);
            var player = connection.CreateProxy<IMprisPlayer>(service, MprisMediaSession.RootObjectPath);

            var raised = new List<string>();
            var seeks = new List<TimeSpan>();
            var positions = new List<TimeSpan>();

            session.PlayRequested += (_, _) => raised.Add("Play");
            session.PauseRequested += (_, _) => raised.Add("Pause");
            session.TogglePlayPauseRequested += (_, _) => raised.Add("PlayPause");
            session.NextRequested += (_, _) => raised.Add("Next");
            session.PreviousRequested += (_, _) => raised.Add("Previous");
            session.StopRequested += (_, _) => raised.Add("Stop");
            session.RaiseRequested += (_, _) => raised.Add("Raise");
            session.QuitRequested += (_, _) => raised.Add("Quit");
            session.SeekRequested += (_, offset) => seeks.Add(offset);
            session.PositionSetRequested += (_, position) => positions.Add(position);

            await player.PlayAsync();
            await player.PauseAsync();
            await player.PlayPauseAsync();
            await player.NextAsync();
            await player.PreviousAsync();
            await player.StopAsync();
            await player.SeekAsync(5_000_000);
            await player.SetPositionAsync(new ObjectPath("/org/mpris/MediaPlayer2/Track/1"), 10_000_000);
            await player.OpenUriAsync("https://example.com/song");
            await root.RaiseAsync();
            await root.QuitAsync();

            Assert.Equal(
                new[] { "Play", "Pause", "PlayPause", "Next", "Previous", "Stop", "Raise", "Quit" },
                raised.ToArray());
            Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(seeks));
            Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(positions));
        }
        finally
        {
            connection.Dispose();
            session!.Dispose();
        }
    }

    [SkippableFact]
    public async Task Mpris_EmitsPropertiesChangedSignal()
    {
        if (!OperatingSystem.IsLinux()) Skip.If(true, "Linux only");
        if (string.IsNullOrEmpty(Address.Session)) Skip.If(true, "No D-Bus session bus");

        var service = NewServiceName();
        var session = await MprisMediaSession.TryCreateAsync(service);
        Skip.If(session == null, "Could not register MPRIS service on the session bus");

        var connection = new Connection(Address.Session!);
        await connection.ConnectAsync();
        try
        {
            var player = connection.CreateProxy<IMprisPlayer>(service, MprisMediaSession.RootObjectPath);

            var changes = new List<PropertyChanges>();
            using var subscription = await player.WatchPlayerPropertiesAsync(changes.Add);

            session.UpdatePlayback(MediaPlaybackState.Playing, TimeSpan.Zero);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (changes.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.NotEmpty(changes);
            Assert.Contains(changes, c => c.Changed.Any(kv => kv.Key == "PlaybackStatus"));
        }
        finally
        {
            connection.Dispose();
            session!.Dispose();
        }
    }
}
