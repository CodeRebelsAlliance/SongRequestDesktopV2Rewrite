using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class AnnouncementEngineTests
{
    private static string CreateChimeFile()
    {
        var path = Path.Combine(TestSupport.NewTempDir(), "announcement.mp3");
        File.WriteAllBytes(path, new byte[] { 0, 1, 2, 3 });
        return path;
    }

    private static (AnnouncementEngine Ann, MusicEngine Music, FakeAudioOutput Output, FakeAudioOutput Chime) Make(Config cfg)
    {
        var music = new MusicEngine { ConfigAccessor = () => cfg, Delay = _ => Task.CompletedTask };
        var output = new FakeAudioOutput();
        music.Output = output; // ducking fades this output

        var chime = new FakeAudioOutput { StopOnPlay = true };
        var ann = new AnnouncementEngine(music)
        {
            ConfigAccessor = () => cfg,
            ChimePath = CreateChimeFile(),
            SourceFactory = _ => new FakeAudioSource(),
            OutputFactory = () => chime,
        };
        return (ann, music, output, chime);
    }

    [Fact]
    public async Task Start_DucksMusic_AndPlaysChime()
    {
        var cfg = new Config { AnnouncementDimDb = 20, AnnouncementPlaySound = true, AnnouncementPushToTalk = true };
        var (ann, _, output, chime) = Make(cfg);
        try
        {
            await ann.StartAsync();

            Assert.True(ann.IsActive);
            Assert.Equal(1, chime.PlayCount);
            Assert.Equal(0.1f, output.Volume, 3); // 20 dB down
        }
        finally { ann.Dispose(); }
    }

    [Fact]
    public async Task Stop_RestoresMusicVolume()
    {
        var cfg = new Config { AnnouncementDimDb = 20, AnnouncementPlaySound = true, AnnouncementPushToTalk = true };
        var (ann, _, output, _) = Make(cfg);
        try
        {
            await ann.StartAsync();
            await ann.StopAsync();

            Assert.False(ann.IsActive);
            Assert.Equal(1f, output.Volume, 3);
        }
        finally { ann.Dispose(); }
    }

    [Fact]
    public async Task PlaySoundDisabled_SkipsChime()
    {
        var cfg = new Config { AnnouncementPlaySound = false, AnnouncementPushToTalk = true };
        var (ann, _, _, chime) = Make(cfg);
        try
        {
            await ann.StartAsync();

            Assert.True(ann.IsActive);
            Assert.Equal(0, chime.PlayCount);
        }
        finally { ann.Dispose(); }
    }

    [Fact]
    public async Task PushToTalk_PressStarts_ReleaseStops()
    {
        var cfg = new Config { AnnouncementPushToTalk = true, AnnouncementPlaySound = false };
        var (ann, _, _, _) = Make(cfg);
        try
        {
            await ann.PressOrToggleAsync();
            Assert.True(ann.IsActive);

            await ann.ReleaseAsync();
            Assert.False(ann.IsActive);
        }
        finally { ann.Dispose(); }
    }

    [Fact]
    public async Task ToggleMode_TogglesOnPress()
    {
        var cfg = new Config { AnnouncementPushToTalk = false, AnnouncementPlaySound = false };
        var (ann, _, _, _) = Make(cfg);
        try
        {
            await ann.PressOrToggleAsync();
            Assert.True(ann.IsActive);

            await ann.PressOrToggleAsync();
            Assert.False(ann.IsActive);

            // Release does nothing in toggle mode
            await ann.ReleaseAsync();
            Assert.False(ann.IsActive);
        }
        finally { ann.Dispose(); }
    }

    [Fact]
    public async Task StartTwice_IsIdempotent()
    {
        var cfg = new Config { AnnouncementPlaySound = true, AnnouncementPushToTalk = true };
        var (ann, _, _, chime) = Make(cfg);
        try
        {
            await ann.StartAsync();
            await ann.StartAsync();
            Assert.Equal(1, chime.PlayCount);
        }
        finally { ann.Dispose(); }
    }

    [Fact]
    public async Task StateChanged_FiresWithLiveState()
    {
        var cfg = new Config { AnnouncementPlaySound = false, AnnouncementPushToTalk = true };
        var (ann, _, _, _) = Make(cfg);
        try
        {
            var states = new List<object>();
            ann.StateChanged += s => states.Add(s);

            await ann.StartAsync();

            Assert.NotEmpty(states);
            Assert.Equal("live", TestSupport.Prop<string>(states[^1], "state"));
            Assert.True(TestSupport.Prop<bool>(states[^1], "isActive"));
        }
        finally { ann.Dispose(); }
    }
}
