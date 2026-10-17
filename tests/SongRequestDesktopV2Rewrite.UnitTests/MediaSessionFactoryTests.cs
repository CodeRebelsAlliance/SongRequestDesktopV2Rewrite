using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class MediaSessionFactoryTests
{
    [Fact]
    public void IsSupported_ReflectsPlatformAndCompanionAvailability()
    {
        Assert.Equal(OperatingSystem.IsLinux() || WindowsSmtcLoader.Available, MediaSessionFactory.IsSupported);
    }

    [Fact]
    public void SupportedBackendName_MatchesSupported()
    {
        var name = MediaSessionFactory.SupportedBackendName;

        if (!MediaSessionFactory.IsSupported)
            Assert.Equal("none", name);
        else
            Assert.Contains(name, new[] { "MPRIS", "SMTC" });
    }

    [Fact]
    public void NullSession_IsUnsupportedAndNoOps()
    {
        using var session = new NullSystemMediaSession();

        Assert.False(session.IsSupported);
        Assert.Equal("none", session.BackendName);

        // Must not throw.
        session.UpdateMetadata(new MediaMetadata("t", "a", "al", TimeSpan.FromSeconds(1), null));
        session.UpdatePlayback(MediaPlaybackState.Playing, TimeSpan.FromSeconds(2));
    }
}
