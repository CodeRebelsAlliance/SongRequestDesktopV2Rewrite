using NAudio.Wave;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class EqualizerTests
{
    private static EqualizerService Service(out Config config)
    {
        var cfg = new Config();
        var service = new EqualizerService
        {
            ConfigAccessor = () => cfg,
            ConfigUpdater = update => update(cfg)
        };
        config = cfg;
        return service;
    }

    [Fact]
    public void FactoryPresets_AllHaveTenBands()
    {
        var service = Service(out _);
        var presets = service.GetPresets();

        Assert.Contains(presets, p => p.Name == "Flat");
        Assert.Contains(presets, p => p.Name == "Rock");
        Assert.Contains(presets, p => p.Name == "Bass Boost");
        Assert.Contains(presets, p => p.Name == "Vocal");
        Assert.All(presets, p => Assert.Equal(EqualizerService.BandCount, p.Gains.Length));
    }

    [Fact]
    public void ApplyPreset_SetsGainsAndName_AndClampsBands()
    {
        var service = Service(out var config);

        var state = service.ApplyPreset("Bass Boost");

        Assert.Equal("Bass Boost", state.Preset);
        Assert.Equal("Bass Boost", config.Equalizer.Preset);
        Assert.Equal(7, config.Equalizer.Gains[0]);
        Assert.All(config.Equalizer.Gains, g => Assert.InRange(g, -12, 12));
    }

    [Fact]
    public void SetBandGain_MarksTheCurveCustom()
    {
        var service = Service(out var config);

        service.ApplyPreset("Flat");
        var state = service.SetBandGain(3, 6);

        Assert.Equal("Custom", state.Preset);
        Assert.Equal(6, config.Equalizer.Gains[3]);
        Assert.Equal(6, state.Bands[3].GainDb);
    }

    [Fact]
    public void SaveAndDeletePreset_PersistInConfiguration()
    {
        var service = Service(out var config);
        service.ApplyPreset("Rock");

        service.SavePreset("My curve");
        Assert.Contains(config.Equalizer.CustomPresets, p => p.Name == "My curve");
        Assert.Contains(service.GetPresets(), p => p.Name == "My curve");

        service.DeletePreset("My curve");
        Assert.DoesNotContain(config.Equalizer.CustomPresets, p => p.Name == "My curve");
    }

    [Fact]
    public void Processor_BypassLeavesSignalUntouched()
    {
        var service = Service(out _);
        var source = new SineSource(1000, 0.25, 44100, 1);
        var processor = new EqualizerProcessor(source, service);

        var original = Read(processor, 8192);
        var bypass = Read(processor, 8192);

        // The first block is also the only block; bypass must be sample-accurate
        // except that the second read sees the same generator continuing.
        Assert.Equal(0.25 / Math.Sqrt(2), Rms(original), 2);
        Assert.Equal(Rms(original), Rms(bypass), 3);
    }

    [Fact]
    public void Processor_BoostedBandIncreasesEnergyAtThatFrequency()
    {
        var service = Service(out _);
        service.SetEnabled(true);
        service.SetBandGain(5, 12); // 1 kHz

        var basSource = new SineSource(1000, 0.10, 44100, 1);
        var baseline = new EqualizerProcessor(basSource, new EqualizerService
        {
            ConfigAccessor = () => new Config(),
            ConfigUpdater = _ => { }
        });
        double baseRms = Rms(Read(baseline, 24000));

        var boosted = new EqualizerProcessor(new SineSource(1000, 0.10, 44100, 1), service);
        double boostedRms = Rms(Read(boosted, 24000));

        Assert.True(boostedRms > baseRms * 1.7,
            $"a +12 dB peak should raise the 1 kHz band (base {baseRms:F4}, boosted {boostedRms:F4})");
        Assert.All(Read(boosted, 2048), s => Assert.InRange(s, -1f, 1f));
    }

    [Fact]
    public void Processor_PreampAppliesWithoutBands()
    {
        var service = Service(out _);
        service.Apply(true, -6, "Flat", null);

        var processor = new EqualizerProcessor(new SineSource(1000, 0.30, 44100, 1), service);
        var samples = Read(processor, 24000);

        Assert.Equal(0.30 / Math.Sqrt(2) * 0.501, Rms(samples), 1);
    }

    [Fact]
    public void State_ExposesBandFrequenciesAndRevision()
    {
        var service = Service(out _);
        long before = service.Revision;

        var state = service.SetEnabled(true);

        Assert.True(state.Enabled);
        Assert.Equal(EqualizerService.BandCount, state.Bands.Count);
        Assert.Equal(31, state.Bands[0].FrequencyHz);
        Assert.Equal(16000, state.Bands[^1].FrequencyHz);
        Assert.True(service.Revision > before);
    }

    private static float[] Read(ISampleProvider provider, int count)
    {
        var buffer = new float[count];
        provider.Read(buffer, 0, count);
        return buffer;
    }

    private static double Rms(float[] samples)
    {
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Endless sine generator in the mixer's interleaved float format.</summary>
    private sealed class SineSource : ISampleProvider
    {
        private readonly double _frequency;
        private readonly double _amplitude;
        private long _sample;

        public SineSource(double frequency, double amplitude, int sampleRate, int channels)
        {
            _frequency = frequency;
            _amplitude = amplitude;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                double t = (double)_sample / WaveFormat.SampleRate;
                buffer[offset + i] = (float)(Math.Sin(2 * Math.PI * _frequency * t) * _amplitude);
                _sample++;
            }
            return count;
        }
    }
}
