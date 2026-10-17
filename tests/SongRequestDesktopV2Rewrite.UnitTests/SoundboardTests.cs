using SongRequestDesktopV2Rewrite;
using NAudio.Wave.SampleProviders;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class SoundboardTests
{
    [Theory]
    [InlineData("a.mp3", true)]
    [InlineData("a.MP3", true)]
    [InlineData("a.wav", true)]
    [InlineData("a.m4a", true)]
    [InlineData("a.flac", true)]
    [InlineData("a.opus", false)] // not in the soundboard's supported set
    [InlineData("a.txt", false)]
    [InlineData("noext", false)]
    [InlineData("", false)]
    public void IsSupportedAudioFile_DetectsFormat(string path, bool expected)
        => Assert.Equal(expected, SoundboardEngine.IsSupportedAudioFile(path));

    [Fact]
    public void Page_TotalSlotsAndButtonRoundTrip()
    {
        var page = new SoundboardPage("P", 4, 3);
        Assert.Equal(12, page.TotalSlots);

        var button = new SoundboardButton { Name = "x" };
        page.SetButton(1, 2, button);

        Assert.Same(button, page.GetButton(1, 2));
        Assert.Equal(1 * 12 + 2, button.Index);
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(0, true)]
    [InlineData(127, true)]
    [InlineData(-1, false)]
    [InlineData(200, false)]
    public void MidiMapping_IsConfigured_ForNoteRange(int note, bool expected)
        => Assert.Equal(expected, new MidiMapping { Note = note }.IsConfigured);

    [Fact]
    public void SetMasterVolume_ClampsTo0To3()
    {
        TestSupport.ResetSoundboardConfig();
        using var engine = new SoundboardEngine();

        var high = engine.SetMasterVolume(5);
        Assert.Equal(3f, TestSupport.Prop<float>(high, "masterVolume"), 3);
        Assert.Equal(3f, engine.Config.MasterVolume, 3);

        engine.SetMasterVolume(-2);
        Assert.Equal(0f, engine.Config.MasterVolume, 3);
    }

    [Fact]
    public void BuildState_WithNoMidiBackend_WorksWithoutTouchingNativeMidi()
    {
        TestSupport.ResetSoundboardConfig();
        var saved = MidiServiceFactory.Override;
        MidiServiceFactory.Override = () => new NullMidiService();
        try
        {
            using var engine = new SoundboardEngine();

            // Regression: on a machine without the ALSA sequencer this used to abort
            // the process instead of returning an (empty) device list.
            var state = engine.BuildState();
            Assert.NotNull(state);
        }
        finally
        {
            MidiServiceFactory.Override = saved;
        }
    }

    [Fact]
    public void AddPage_IncreasesPageCount()    {
        TestSupport.ResetSoundboardConfig();
        using var engine = new SoundboardEngine();
        var before = engine.Config.Pages.Count;

        engine.AddPage("Page 2", 4, 3);

        Assert.Equal(before + 1, engine.Config.Pages.Count);
    }

    [Fact]
    public void DeletePage_KeepsAtLeastOnePage()
    {
        TestSupport.ResetSoundboardConfig();
        using var engine = new SoundboardEngine();

        var result = engine.DeletePage(0);

        Assert.NotNull(result);
        Assert.Single(engine.Config.Pages);
    }

    [Fact]
    public void UpdateButton_SetsMetadataAndClampsVolume()
    {
        TestSupport.ResetSoundboardConfig();
        using var engine = new SoundboardEngine();

        engine.UpdateButton(0, 0, "Boom", "\U0001F50A", "#FF0000", "",
            false, false, "none", volume: 9, customDevice: null, shortcutEnabled: false, shortcutGesture: "");

        var saved = engine.Config.GetCurrentPage().GetButton(0, 0);
        Assert.Equal("Boom", saved.Name);
        Assert.Equal(3f, saved.Volume, 3); // clamped 0..3
    }

    // ── Fade in / fade out ─────────────────────────────────────────────────
    // The per-button Fade In / Fade Out options must actually shape the audio
    // volume, so these tests drive playback through fake output/source seams.

    private static (SoundboardEngine Engine, FakeAudioOutput Output) PrepareFadeEngine(
        bool fadeIn, bool fadeOut, double volume = 1.0, int fadeMs = 150, string repeatMode = "none")
    {
        TestSupport.ResetSoundboardConfig();

        var engine = new SoundboardEngine { FadeDurationMs = fadeMs };

        var folder = SoundboardConfiguration.GetSoundboardFolder();
        Directory.CreateDirectory(folder);
        var fileName = "fade-test-" + Guid.NewGuid().ToString("N") + ".wav";
        File.WriteAllBytes(Path.Combine(folder, fileName), new byte[] { 0 });

        var output = new FakeAudioOutput();
        engine.OutputFactory = () => output;
        engine.SourceFactory = _ => new FakeAudioSource(frames: 44100);

        // Configure the button directly (avoids decoding the placeholder file).
        var page = engine.Config.GetCurrentPage();
        var button = page.GetButton(0, 0);
        button.Name = "Fade";
        button.SoundFile = fileName;
        button.IsEnabled = true;
        button.FadeIn = fadeIn;
        button.FadeOut = fadeOut;
        button.Volume = (float)volume;
        button.RepeatMode = repeatMode;
        page.SetButton(0, 0, button);
        engine.Config.Save();

        return (engine, output);
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }

        return condition();
    }

    private static VolumeSampleProvider VolumeOf(FakeAudioOutput output)
        => Assert.IsType<VolumeSampleProvider>(output.Source);

    [Fact]
    public void PlayButton_WithoutFadeIn_StartsAtTargetVolume()
    {
        var (engine, output) = PrepareFadeEngine(fadeIn: false, fadeOut: false, volume: 0.5);
        using (engine)
        {
            engine.PlayButton(0, 0);

            Assert.Equal(0.5f, VolumeOf(output).Volume, 3);
        }
    }

    [Fact]
    public void PlayButton_WithFadeIn_StartsSilentAndRampsUp()
    {
        var (engine, output) = PrepareFadeEngine(fadeIn: true, fadeOut: false, volume: 1.0, fadeMs: 200);
        using (engine)
        {
            engine.PlayButton(0, 0);

            var volume = VolumeOf(output);
            Assert.True(volume.Volume < 0.5f, $"expected a fade-in ramp to start near silence, got {volume.Volume}");

            Assert.True(WaitFor(() => volume.Volume >= 0.999f, 4000),
                $"volume never reached the target (was {volume.Volume})");
        }
    }

    [Fact]
    public void StopPlayback_WithFadeOut_RampsDownBeforeStopping()
    {
        var (engine, output) = PrepareFadeEngine(fadeIn: false, fadeOut: true, volume: 1.0, fadeMs: 200);
        using (engine)
        {
            engine.PlayButton(0, 0);
            var volume = VolumeOf(output);
            Assert.Equal(1f, volume.Volume, 3);

            engine.StopAll();

            // Still alive (fading), not stopped yet.
            Assert.Equal(0, output.StopCount);

            Assert.True(WaitFor(() => output.StopCount == 1, 4000), "fade-out never completed");
            Assert.True(volume.Volume <= 0.001f, $"expected silence at the end of the fade, got {volume.Volume}");
        }
    }

    [Fact]
    public void StopPlayback_WithoutFadeOut_StopsImmediately()
    {
        var (engine, output) = PrepareFadeEngine(fadeIn: false, fadeOut: false, volume: 1.0, fadeMs: 200);
        using (engine)
        {
            engine.PlayButton(0, 0);

            engine.StopAll();

            Assert.Equal(1, output.StopCount);
        }
    }

    [Fact]
    public void FadeDurationZero_DisablesBothFades()
    {
        var (engine, output) = PrepareFadeEngine(fadeIn: true, fadeOut: true, volume: 0.8, fadeMs: 0);
        using (engine)
        {
            engine.PlayButton(0, 0);
            Assert.Equal(0.8f, VolumeOf(output).Volume, 3); // no ramp in

            engine.StopAll();
            Assert.Equal(1, output.StopCount); // no ramp out
        }
    }

    [Fact]
    public void Looping_AppliesFadeInOnEveryIteration()
    {
        var (engine, output) = PrepareFadeEngine(fadeIn: true, fadeOut: false, volume: 1.0, fadeMs: 200, repeatMode: "loop");
        using (engine)
        {
            engine.PlayButton(0, 0);
            var volume = VolumeOf(output);
            Assert.True(WaitFor(() => volume.Volume >= 0.999f, 4000), "initial fade-in never finished");

            // Simulate the clip reaching its end: it should restart and fade in again.
            output.RaiseStopped();

            Assert.Equal(2, output.PlayCount);
            Assert.True(WaitFor(() => volume.Volume >= 0.999f, 4000),
                $"the looped iteration never faded back in (was {volume.Volume})");
        }
    }

    [Fact]
    public void AfterAllFadesFinish_ANewPressStillFades()
    {
        var (engine, output) = PrepareFadeEngine(fadeIn: true, fadeOut: true, volume: 1.0, fadeMs: 100);
        using (engine)
        {
            engine.PlayButton(0, 0);
            engine.StopAll();
            Assert.True(WaitFor(() => output.StopCount == 1, 4000), "the fade-out never finished");

            // Once the fade timer has stopped itself, a later press must still fade
            // in (this guards the timer restart path).
            engine.PlayButton(0, 0);

            Assert.Equal(2, output.PlayCount);
            Assert.True(WaitFor(() => VolumeOf(output).Volume >= 0.999f, 4000),
                "a press after the fade timer stopped no longer faded in");
        }
    }
}
