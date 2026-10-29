using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// A named equalizer curve. Built-in presets are shipped with the app; user
/// presets are persisted in <see cref="Config.Equalizer"/>.
/// </summary>
public sealed class EqualizerPreset
{
    public string Name { get; set; } = "Flat";

    /// <summary>True for the immutable factory presets.</summary>
    public bool BuiltIn { get; set; }

    /// <summary>Gain in dB for every band (see <see cref="EqualizerService.FrequenciesHz"/>).</summary>
    public double[] Gains { get; set; } = new double[EqualizerService.BandCount];
}

/// <summary>
/// Persisted equalizer state. The values live in config.json so the curve is the
/// same on desktop and mobile (and survives restarts on both).
/// </summary>
public sealed class EqualizerConfiguration
{
    public bool Enabled { get; set; }

    /// <summary>Pre-amplifier gain in dB, applied before the band filters.</summary>
    public double PreampDb { get; set; }

    /// <summary>Name of the selected preset, or "Custom" after editing bands.</summary>
    public string Preset { get; set; } = "Flat";

    public double[] Gains { get; set; } = new double[EqualizerService.BandCount];

    public List<EqualizerPreset> CustomPresets { get; set; } = new();

    /// <summary>Returns a valid configuration even when config.json is old or partial.</summary>
    public EqualizerConfiguration Normalized()
    {
        Gains ??= new double[EqualizerService.BandCount];
        if (Gains.Length != EqualizerService.BandCount)
        {
            var copy = new double[EqualizerService.BandCount];
            Array.Copy(Gains, copy, Math.Min(Gains.Length, copy.Length));
            Gains = copy;
        }

        for (int i = 0; i < Gains.Length; i++)
            Gains[i] = Math.Clamp(double.IsFinite(Gains[i]) ? Gains[i] : 0, -12, 12);

        PreampDb = Math.Clamp(double.IsFinite(PreampDb) ? PreampDb : 0, -12, 12);
        Preset = string.IsNullOrWhiteSpace(Preset) ? "Flat" : Preset.Trim();
        CustomPresets ??= new List<EqualizerPreset>();

        foreach (var preset in CustomPresets)
        {
            preset.Gains ??= Array.Empty<double>();
            if (preset.Gains.Length != EqualizerService.BandCount)
            {
                var copy = new double[EqualizerService.BandCount];
                Array.Copy(preset.Gains, copy, Math.Min(preset.Gains.Length, copy.Length));
                preset.Gains = copy;
            }
        }

        return this;
    }
}

/// <summary>One band as exposed to the UI and the local API.</summary>
public sealed record EqualizerBand(int Index, double FrequencyHz, string Label, double GainDb);

/// <summary>Serializable snapshot of the equalizer for UI/API consumers.</summary>
public sealed class EqualizerState
{
    public bool Enabled { get; init; }
    public double PreampDb { get; init; }
    public string Preset { get; init; } = "Flat";
    public IReadOnlyList<EqualizerBand> Bands { get; init; } = Array.Empty<EqualizerBand>();
    public IReadOnlyList<EqualizerPreset> Presets { get; init; } = Array.Empty<EqualizerPreset>();
    public long Revision { get; init; }
}

/// <summary>
/// Owns the persisted equalizer curve, the factory presets and the change
/// notification used to refresh the UI (and API clients).
///
/// The actual filtering happens in <see cref="EqualizerProcessor"/>, which reads
/// a snapshot and rebuilds its biquad coefficients whenever <see cref="Revision"/>
/// changes — so editing a band applies live without restarting playback.
/// </summary>
public sealed class EqualizerService
{
    public const int BandCount = 10;

    /// <summary>ISO-octave centres used by the graphical editor.</summary>
    public static readonly double[] FrequenciesHz =
    {
        31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000
    };

    public static readonly string[] Labels =
    {
        "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"
    };

    public static EqualizerService Instance { get; } = new();

    private readonly object _sync = new();
    private long _revision;

    public Func<Config?> ConfigAccessor { get; set; } = () => ConfigService.Instance.Current;
    public Action<Action<Config>> ConfigUpdater { get; set; } = updater => ConfigService.Instance.Update(updater);

    /// <summary>Raised after a change, with the new state (UI pushes and API events use it).</summary>
    public event Action<EqualizerState>? Changed;

    public long Revision => System.Threading.Interlocked.Read(ref _revision);

    private static readonly EqualizerPreset[] FactoryPresets =
    {
        Preset("Flat", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        Preset("Rock", 5, 4, 3, 1, -1, -1, 1, 3, 4, 5),
        Preset("Pop", -1, 0, 1, 2, 4, 4, 2, 1, -1, -1),
        Preset("Jazz", 4, 3, 2, 0, -1, -1, 0, 1, 2, 3),
        Preset("Classical", 4, 3, 2, 0, -1, -2, -1, 0, 2, 3),
        Preset("Electronic", 5, 4, 2, 0, -1, 1, 1, 2, 4, 5),
        Preset("Hip-Hop", 6, 5, 3, 1, -1, -2, -1, 1, 2, 3),
        Preset("Acoustic", 3, 2, 1, 1, 2, 2, 2, 1, 2, 3),
        Preset("Vocal", -2, -1, 1, 3, 5, 5, 3, 1, 0, -1),
        Preset("Bass Boost", 7, 6, 5, 3, 1, 0, 0, 0, 0, 0),
        Preset("Treble Boost", 0, 0, 0, 0, 0, 1, 3, 5, 6, 7),
        Preset("Loudness", 5, 4, 2, 0, -1, -1, 0, 2, 3, 4)
    };

    private static EqualizerPreset Preset(string name, params double[] gains)
        => new() { Name = name, BuiltIn = true, Gains = gains };

    public IReadOnlyList<EqualizerPreset> GetPresets()
    {
        var cfg = GetConfiguration().Normalized();
        var list = new List<EqualizerPreset>(FactoryPresets.Length + cfg.CustomPresets.Count);
        list.AddRange(FactoryPresets.Select(Clone));
        list.AddRange(cfg.CustomPresets.Select(Clone));
        return list;
    }

    public EqualizerState GetState()
    {
        var cfg = GetConfiguration().Normalized();
        lock (_sync)
        {
            return new EqualizerState
            {
                Enabled = cfg.Enabled,
                PreampDb = cfg.PreampDb,
                Preset = cfg.Preset,
                Bands = Enumerable.Range(0, BandCount)
                    .Select(i => new EqualizerBand(i, FrequenciesHz[i], Labels[i], cfg.Gains[i]))
                    .ToArray(),
                Presets = GetPresets(),
                Revision = _revision
            };
        }
    }

    /// <summary>Snapshot used by the real-time processor (allocation-light, immutable).</summary>
    public EqualizerSnapshot GetSnapshot()
    {
        var cfg = GetConfiguration().Normalized();
        return new EqualizerSnapshot(
            cfg.Enabled,
            (float)cfg.PreampDb,
            cfg.Gains.Select(g => (float)g).ToArray(),
            _revision);
    }

    public EqualizerState SetEnabled(bool enabled)
        => Mutate(cfg => cfg.Enabled = enabled);

    public EqualizerState SetPreamp(double db)
        => Mutate(cfg => cfg.PreampDb = Math.Clamp(db, -12, 12));

    public EqualizerState SetBandGain(int index, double gainDb)
        => Mutate(cfg =>
        {
            if (index < 0 || index >= BandCount) return;
            cfg.Gains[index] = Math.Clamp(gainDb, -12, 12);
            cfg.Preset = MatchPresetName(cfg) ?? "Custom";
        });

    public EqualizerState ApplyPreset(string name)
        => Mutate(cfg =>
        {
            var preset = GetPresets().FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset == null) return;

            cfg.Gains = preset.Gains.Select(g => Math.Clamp(g, -12, 12)).ToArray();
            cfg.Preset = preset.Name;
        });

    public EqualizerState Reset()
        => Mutate(cfg =>
        {
            cfg.Enabled = false;
            cfg.PreampDb = 0;
            cfg.Preset = "Flat";
            cfg.Gains = new double[BandCount];
        });

    /// <summary>Persists the current curve under a user-supplied name.</summary>
    public EqualizerState SavePreset(string name)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) throw new ArgumentException("A preset name is required.", nameof(name));
        if (clean.Length > 48) clean = clean[..48];

        return Mutate(cfg =>
        {
            var existing = cfg.CustomPresets.FirstOrDefault(p =>
                string.Equals(p.Name, clean, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new EqualizerPreset { Name = clean, Gains = new double[BandCount] };
                cfg.CustomPresets.Add(existing);
            }
            existing.Gains = cfg.Gains.ToArray();
            cfg.Preset = clean;
        });
    }

    public EqualizerState DeletePreset(string name)
        => Mutate(cfg =>
        {
            var existing = cfg.CustomPresets.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing == null) return;
            cfg.CustomPresets.Remove(existing);
            if (string.Equals(cfg.Preset, existing.Name, StringComparison.OrdinalIgnoreCase))
                cfg.Preset = MatchPresetName(cfg) ?? "Custom";
        });

    /// <summary>Applies a complete state in one update (used by the API/UI).</summary>
    public EqualizerState Apply(bool? enabled, double? preampDb, string? preset, double[]? gains)
        => Mutate(cfg =>
        {
            if (enabled.HasValue) cfg.Enabled = enabled.Value;
            if (preampDb.HasValue) cfg.PreampDb = Math.Clamp(preampDb.Value, -12, 12);

            if (gains is { Length: > 0 })
            {
                var next = new double[BandCount];
                Array.Copy(gains, next, Math.Min(gains.Length, BandCount));
                cfg.Gains = next.Select(g => Math.Clamp(double.IsFinite(g) ? g : 0, -12, 12)).ToArray();
                cfg.Preset = MatchPresetName(cfg) ?? "Custom";
            }
            else if (!string.IsNullOrWhiteSpace(preset))
            {
                var match = GetPresets().FirstOrDefault(p =>
                    string.Equals(p.Name, preset, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    cfg.Gains = match.Gains.Select(g => Math.Clamp(g, -12, 12)).ToArray();
                    cfg.Preset = match.Name;
                }
            }
        });

    private EqualizerState Mutate(Action<EqualizerConfiguration> mutate)
    {
        var accessor = ConfigAccessor;
        EqualizerState state;
        lock (_sync)
        {
            ConfigUpdater(cfg =>
            {
                cfg.Equalizer ??= new EqualizerConfiguration();
                mutate(cfg.Equalizer);
                cfg.Equalizer.Normalized();
            });

            System.Threading.Interlocked.Increment(ref _revision);
            state = BuildState(GetConfiguration().Normalized());
        }

        try { Changed?.Invoke(state); } catch { }
        return state;
    }

    private EqualizerState BuildState(EqualizerConfiguration cfg) => new()
    {
        Enabled = cfg.Enabled,
        PreampDb = cfg.PreampDb,
        Preset = cfg.Preset,
        Bands = Enumerable.Range(0, BandCount)
            .Select(i => new EqualizerBand(i, FrequenciesHz[i], Labels[i], cfg.Gains[i]))
            .ToArray(),
        Presets = GetPresets(),
        Revision = _revision
    };

    private EqualizerConfiguration GetConfiguration()
    {
        var cfg = ConfigAccessor();
        if (cfg == null) return new EqualizerConfiguration();
        cfg.Equalizer ??= new EqualizerConfiguration();
        return cfg.Equalizer;
    }

    private string? MatchPresetName(EqualizerConfiguration cfg)
    {
        foreach (var preset in GetPresets())
        {
            if (preset.Gains.Length != cfg.Gains.Length) continue;
            bool equal = true;
            for (int i = 0; i < cfg.Gains.Length; i++)
            {
                if (Math.Abs(preset.Gains[i] - cfg.Gains[i]) > 0.001) { equal = false; break; }
            }
            if (equal) return preset.Name;
        }
        return null;
    }

    private static EqualizerPreset Clone(EqualizerPreset preset) => new()
    {
        Name = preset.Name,
        BuiltIn = preset.BuiltIn,
        Gains = preset.Gains.ToArray()
    };
}

/// <summary>Immutable settings snapshot read by the real-time processor.</summary>
public readonly record struct EqualizerSnapshot(bool Enabled, float PreampDb, float[] Gains, long Revision);

/// <summary>
/// Streams the mixer through a cascade of peaking biquads (one per band and
/// channel) plus the pre-amp. It sits between the mixer and the output/capture
/// tap, so the equalizer applies to every platform and the visualization shows
/// the processed signal.
/// </summary>
public sealed class EqualizerProcessor : ISampleProvider
{
    private const double Q = 1.1;

    private readonly ISampleProvider _source;
    private readonly EqualizerService _service;
    private readonly WaveFormat _format;
    private readonly double[] _b0 = new double[EqualizerService.BandCount];
    private readonly double[] _b1 = new double[EqualizerService.BandCount];
    private readonly double[] _b2 = new double[EqualizerService.BandCount];
    private readonly double[] _a1 = new double[EqualizerService.BandCount];
    private readonly double[] _a2 = new double[EqualizerService.BandCount];
    private readonly double[,] _z1;
    private readonly double[,] _z2;

    private long _revision = -1;
    private bool _enabled;
    private float _preamp = 1f;

    public EqualizerProcessor(ISampleProvider source, EqualizerService service)
    {
        _source = source;
        _service = service;
        _format = source.WaveFormat;
        _z1 = new double[EqualizerService.BandCount, Math.Max(1, _format.Channels)];
        _z2 = new double[EqualizerService.BandCount, Math.Max(1, _format.Channels)];
        Refresh(force: true);
    }

    public WaveFormat WaveFormat => _format;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        Refresh(force: false);

        // Bypass untouched signal when the EQ is off (but leave the pre-amp
        // available: pre-amp is 0 dB unless the user set it).
        if (!_enabled && Math.Abs(_preamp - 1f) < 0.00001f) return read;

        int channels = _format.Channels;
        for (int i = offset; i < offset + read; i++)
        {
            int channel = (i - offset) % channels;
            double sample = buffer[i];

            if (_enabled)
            {
                for (int band = 0; band < EqualizerService.BandCount; band++)
                {
                    double y = _b0[band] * sample + _z1[band, channel];
                    _z1[band, channel] = _b1[band] * sample - _a1[band] * y + _z2[band, channel];
                    _z2[band, channel] = _b2[band] * sample - _a2[band] * y;
                    sample = y;
                }
            }

            sample *= _preamp;
            if (sample > 1.0) sample = 1.0;
            else if (sample < -1.0) sample = -1.0;
            buffer[i] = (float)sample;
        }

        return read;
    }

    private void Refresh(bool force)
    {
        long revision = _service.Revision;
        if (!force && revision == _revision) return;

        _revision = revision;
        var snapshot = _service.GetSnapshot();
        _enabled = snapshot.Enabled;
        _preamp = (float)Math.Pow(10.0, snapshot.PreampDb / 20.0);

        int sampleRate = _format.SampleRate > 0 ? _format.SampleRate : 44100;
        for (int band = 0; band < EqualizerService.BandCount; band++)
        {
            double gainDb = band < snapshot.Gains.Length ? snapshot.Gains[band] : 0;
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * EqualizerService.FrequenciesHz[band] / sampleRate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2.0 * Q);

            double a0 = 1.0 + alpha / a;
            _b0[band] = (1.0 + alpha * a) / a0;
            _b1[band] = (-2.0 * cos) / a0;
            _b2[band] = (1.0 - alpha * a) / a0;
            _a1[band] = (-2.0 * cos) / a0;
            _a2[band] = (1.0 - alpha / a) / a0;
        }

        if (!_enabled)
        {
            // New coefficients next time it is switched on; clear the filter state
            // so an old tail cannot ring into the new curve.
            for (int band = 0; band < EqualizerService.BandCount; band++)
                for (int ch = 0; ch < _z1.GetLength(1); ch++)
                    _z1[band, ch] = _z2[band, ch] = 0;
        }
    }
}
