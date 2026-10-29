using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Decodes a track once and derives BPM, loudness, dynamic range, quality
/// metadata and smart drop candidates from the audio itself.
///
/// The analysis deliberately uses the same <see cref="IAudioSource"/> seam as
/// playback, so it works with FFmpeg on desktop and the platform decoders on
/// mobile without a second native dependency.
/// </summary>
public sealed class SongAnalyzer
{
    private const int FftSize = 1024;
    private const int HopSize = 512;
    private const int MaxTimelinePoints = 900;

    private readonly Func<string, IAudioSource> _open;
    private readonly Action<SongAnalysisProgress>? _progress;

    public SongAnalyzer(Func<string, IAudioSource> open, Action<SongAnalysisProgress>? progress = null)
    {
        _open = open;
        _progress = progress;
    }

    public SongAnalysis Analyze(string path, string title = "", string artist = "")
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A song path is required.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("The audio file was not found.", path);

        var info = new FileInfo(path);
        var result = new SongAnalysis
        {
            SongPath = path,
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title,
            Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown" : artist,
            FileSizeBytes = info.Length,
            FileModifiedUtc = info.LastWriteTimeUtc,
            Fingerprint = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}"
        };

        Report(path, 0.02, "Opening audio");
        using var source = _open(path);
        var format = source.WaveFormat;
        int channels = Math.Max(1, format.Channels);
        int sampleRate = Math.Max(8000, format.SampleRate);
        double duration = source.TotalTime.TotalSeconds;
        result.DurationSeconds = duration;

        var frames = new List<RawFrame>();
        var window = new float[FftSize];
        int windowFill = 0;
        var block = new float[HopSize * channels];
        var monoBlock = new float[HopSize];

        double sumSquares = 0;
        long totalSamples = 0;
        double peak = 0;
        long clipped = 0;
        int lastProgress = 0;

        var previousMagnitudes = new double[FftSize / 2];
        var re = new double[FftSize];
        var im = new double[FftSize];
        var hann = new double[FftSize];
        for (int i = 0; i < FftSize; i++)
            hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));

        int read;
        long frameIndex = 0;
        while ((read = source.Read(block, 0, block.Length)) > 0)
        {
            int framesRead = read / channels;
            for (int f = 0; f < framesRead; f++)
            {
                double sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    float value = block[f * channels + c];
                    sum += value;
                    double abs = Math.Abs(value);
                    if (abs > peak) peak = abs;
                    if (abs >= 0.999) clipped++;
                    sumSquares += value * value;
                    totalSamples++;
                }

                float mono = (float)(sum / channels);
                monoBlock[f] = mono;

                window[windowFill++] = mono;
                if (windowFill == FftSize)
                {
                    var frame = ComputeFrame(window, hann, re, im, previousMagnitudes, sampleRate, frameIndex);
                    frames.Add(frame);
                    Array.Copy(window, HopSize, window, 0, FftSize - HopSize);
                    windowFill = FftSize - HopSize;
                    frameIndex++;
                }
            }

            if (duration > 0)
            {
                int percent = (int)Math.Clamp(source.CurrentTime.TotalSeconds / duration * 100, 0, 100);
                if (percent >= lastProgress + 5)
                {
                    lastProgress = percent;
                    Report(path, 0.05 + percent / 100.0 * 0.85, "Decoding and measuring");
                }
            }
        }

        if (frames.Count == 0)
            throw new InvalidOperationException("The audio file did not contain enough decodable samples to analyse.");

        double effectiveDuration = duration > 0
            ? duration
            : (double)totalSamples / (sampleRate * channels);

        double rms = totalSamples > 0 ? Math.Sqrt(sumSquares / totalSamples) : 0;
        double peakDb = peak > 0 ? 20 * Math.Log10(peak) : -96;
        double averageRmsDb = rms > 0 ? 20 * Math.Log10(rms) : -96;

        result.DurationSeconds = effectiveDuration;
        result.PeakDb = Round(peakDb, 2);
        result.AverageRmsDb = Round(averageRmsDb, 2);
        result.IntegratedLoudnessLufs = Round(LoudnessFromRms(rms), 2);
        result.DynamicRangeDb = Round(Percentile(frames.Select(f => f.RmsDb).ToList(), 0.95) -
                                       Percentile(frames.Select(f => f.RmsDb).ToList(), 0.10), 2);
        result.LoudnessRangeLu = Round(Percentile(frames.Select(f => f.RmsDb).ToList(), 0.95) -
                                       Percentile(frames.Select(f => f.RmsDb).ToList(), 0.10), 2);

        Report(path, 0.93, "Detecting tempo and drops");
        var (bpm, confidence, beatOffset, beats) = DetectTempo(frames, HopSize / (double)sampleRate);
        result.Bpm = Round(bpm, 1);
        result.BpmConfidence = Round(confidence, 3);
        result.BeatOffsetSeconds = Round(beatOffset, 3);
        result.BeatsDetected = beats;
        result.IsLowConfidence = bpm <= 0 || confidence < 0.18;

        result.Drops = DetectDrops(frames, HopSize / (double)sampleRate);
        result.Timeline = Downsample(frames, HopSize / (double)sampleRate);
        result.Quality = BuildQuality(path, format, effectiveDuration, info.Length, result, clipped, totalSamples);
        result.AnalysisPath =
            $"{result.Quality.Codec} {sampleRate} Hz/{channels}ch → mono downmix → " +
            $"{FftSize}-point FFT (hop {HopSize}) → RMS/band/onset → tempo autocorrelation → drop novelty scan";
        result.AnalyzedUtc = DateTime.UtcNow;

        Report(path, 1.0, "Done");
        return result;
    }

    private static RawFrame ComputeFrame(
        float[] window,
        double[] hann,
        double[] re,
        double[] im,
        double[] previousMagnitudes,
        int sampleRate,
        long frameIndex)
    {
        double sumSquares = 0;
        double framePeak = 0;
        for (int i = 0; i < window.Length; i++)
        {
            double v = window[i];
            sumSquares += v * v;
            double abs = Math.Abs(v);
            if (abs > framePeak) framePeak = abs;
            re[i] = v * hann[i];
            im[i] = 0;
        }

        AnalysisFft.Forward(re, im);

        int bins = FftSize / 2;
        var magnitudes = new double[bins];
        double low = 0, mid = 0, high = 0, flux = 0;
        for (int k = 1; k < bins; k++)
        {
            double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / FftSize;
            magnitudes[k] = mag;
            double power = mag * mag;
            double hz = k * sampleRate / (double)FftSize;
            if (hz < 250) low += power;
            else if (hz < 4000) mid += power;
            else high += power;

            double diff = mag - previousMagnitudes[k];
            if (diff > 0) flux += diff;
        }

        Array.Copy(magnitudes, previousMagnitudes, bins);

        double totalBand = low + mid + high;
        if (totalBand <= 1e-12) totalBand = 1;

        double rms = Math.Sqrt(sumSquares / window.Length);
        return new RawFrame
        {
            Index = frameIndex,
            Rms = rms,
            RmsDb = rms > 0 ? 20 * Math.Log10(rms) : -96,
            PeakDb = framePeak > 0 ? 20 * Math.Log10(framePeak) : -96,
            Flux = flux,
            Low = low / totalBand,
            Mid = mid / totalBand,
            High = high / totalBand
        };
    }

    private static (double Bpm, double Confidence, double BeatOffset, int Beats) DetectTempo(
        List<RawFrame> frames,
        double hopSeconds)
    {
        int n = frames.Count;
        if (n < 16) return (0, 0, 0, 0);

        // Onset strength: spectral flux + positive energy change. A moving average
        // is subtracted so sustained loud sections do not dominate the tempo.
        var onset = new double[n];
        for (int i = 0; i < n; i++)
        {
            double previousRms = i > 0 ? frames[i - 1].Rms : 0;
            double energyRise = Math.Max(0, frames[i].Rms - previousRms);
            onset[i] = frames[i].Flux + energyRise * 8.0;
        }

        const int smoothWindow = 32;
        var baseline = new double[n];
        double running = 0;
        for (int i = 0; i < n; i++)
        {
            running += onset[i];
            if (i >= smoothWindow) running -= onset[i - smoothWindow];
            baseline[i] = running / Math.Min(i + 1, smoothWindow);
        }
        for (int i = 0; i < n; i++) onset[i] = Math.Max(0, onset[i] - baseline[i]);

        double minBpm = 55, maxBpm = 205;
        int minLag = Math.Max(2, (int)Math.Floor(60.0 / maxBpm / hopSeconds));
        int maxLag = Math.Min(n / 2 - 1, (int)Math.Ceiling(60.0 / minBpm / hopSeconds));
        if (maxLag <= minLag) return (0, 0, 0, 0);

        double bestScore = 0;
        int bestLag = 0;
        var scores = new double[maxLag + 1];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double score = 0;
            for (int i = lag; i < n; i++) score += onset[i] * onset[i - lag];
            score /= n - lag;
            scores[lag] = score;
            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }

        if (bestLag == 0 || bestScore <= 0) return (0, 0, 0, 0);

        // Octave correction: harmonic peaks at half/double the beat period can win
        // on heavily compressed music. Pick the candidate that stays in the
        // musically common 70-180 BPM window.
        double bpm = 60.0 / (bestLag * hopSeconds);
        for (int pass = 0; pass < 2; pass++)
        {
            if (bpm < 70)
            {
                int doubled = bestLag / 2;
                if (doubled >= minLag && scores[doubled] >= bestScore * 0.75) { bestLag = doubled; bpm *= 2; continue; }
            }
            if (bpm > 180)
            {
                int halved = bestLag * 2;
                if (halved <= maxLag && scores[halved] >= bestScore * 0.75) { bestLag = halved; bpm /= 2; continue; }
            }
            break;
        }

        double mean = scores.Skip(minLag).Take(maxLag - minLag + 1).Where(s => s > 0).DefaultIfEmpty(0).Average();
        double confidence = mean > 0 ? Math.Clamp((bestScore / mean - 1.0) / 6.0, 0, 1) : 0;

        // Beat phase: the lag offset with the strongest accumulated onset.
        double bestPhase = 0;
        double bestPhaseScore = -1;
        for (int phase = 0; phase < bestLag; phase++)
        {
            double sum = 0;
            for (int i = phase; i < n; i += bestLag) sum += onset[i];
            if (sum > bestPhaseScore) { bestPhaseScore = sum; bestPhase = phase; }
        }

        int beats = (int)Math.Floor(n * hopSeconds / (60.0 / Math.Max(1, bpm)));
        return (bpm, confidence, bestPhase * hopSeconds, beats);
    }

    private static List<SongDrop> DetectDrops(List<RawFrame> frames, double hopSeconds)
    {
        int n = frames.Count;
        if (n < 8) return new List<SongDrop>();

        int beforeWindow = Math.Max(2, (int)(1.5 / hopSeconds));
        int afterWindow = Math.Max(2, (int)(2.0 / hopSeconds));
        double noveltyMax = frames.Select(f => f.Flux).DefaultIfEmpty(0).Max();
        if (noveltyMax <= 0) noveltyMax = 1;

        var candidates = new List<SongDrop>();
        for (int i = beforeWindow; i < n - afterWindow; i++)
        {
            double before = TopAverage(frames, i - beforeWindow, beforeWindow);
            double after = TopAverage(frames, i, afterWindow);
            double increase = after - before;

            double localNovelty = frames[i].Flux / noveltyMax;
            bool loudEnough = after > -45;
            if (increase < 4.0 || !loudEnough) continue;

            // Local maximum: a gradual crescendo should not produce dozens of drops.
            bool isPeak = true;
            for (int j = Math.Max(0, i - 2); j <= Math.Min(n - 1, i + 2); j++)
            {
                if (frames[j].RmsDb > frames[i].RmsDb + 0.75 &&
                    Math.Abs(frames[j].RmsDb - frames[i].RmsDb) > 0.1)
                {
                    isPeak = false;
                    break;
                }
            }
            if (!isPeak && increase < 8) continue;

            double confidence = Math.Clamp(increase / 16.0 * 0.65 + localNovelty * 0.35, 0.05, 1);
            string type = increase >= 8 ? "drop" : "build-up";
            candidates.Add(new SongDrop
            {
                Time = Math.Round(i * hopSeconds, 2),
                Confidence = Math.Round(confidence, 3),
                Type = type,
                Label = type == "drop" ? "Drop" : "Build-up"
            });
        }

        // Keep the strongest candidate in each 10-second cluster.
        var merged = new List<SongDrop>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Confidence))
        {
            if (merged.Any(m => Math.Abs(m.Time - candidate.Time) < 10)) continue;
            merged.Add(candidate);
        }

        return merged.OrderBy(d => d.Time).Take(12).ToList();
    }

    /// <summary>
    /// Average of the loudest 10% of frames in a window. Using the mean would make
    /// sparse material (clicks, stabs, a cappella) look silent between hits and
    /// hide the energy jump a drop detector is looking for.
    /// </summary>
    private static double TopAverage(List<RawFrame> frames, int start, int count)
    {
        int from = Math.Max(0, start);
        int to = Math.Min(frames.Count, start + count);
        if (to <= from) return -96;

        var values = new List<double>(to - from);
        for (int i = from; i < to; i++) values.Add(frames[i].RmsDb);
        values.Sort();
        int take = Math.Max(1, (int)Math.Ceiling(values.Count * 0.10));
        double sum = 0;
        for (int i = values.Count - take; i < values.Count; i++) sum += values[i];
        return sum / take;
    }

    private static SongQualityReport BuildQuality(
        string path,
        WaveFormat format,
        double duration,
        long fileSize,
        SongAnalysis analysis,
        long clippedSamples,
        long totalSamples)
    {
        string extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        bool lossless = extension is "FLAC" or "WAV" or "ALAC" or "AIFF" or "AIFF" or "APE" or "DSF";
        string codec = extension switch
        {
            "M4A" or "MP4" or "AAC" => "AAC",
            "MP3" => "MP3",
            "FLAC" => "FLAC",
            "WAV" => "WAV",
            "OGG" or "OPUS" => "Opus",
            "WMA" => "WMA",
            _ => extension.Length > 0 ? extension : "Audio"
        };

        int estimatedBitrate = 0;
        if (duration > 0)
            estimatedBitrate = (int)Math.Round(fileSize * 8.0 / duration / 1000.0);

        double clippingPercent = totalSamples > 0 ? clippedSamples * 100.0 / totalSamples : 0;
        bool clipping = clippingPercent > 0.02;

        double score = 45;
        if (lossless) score += 25;
        else if (estimatedBitrate >= 256) score += 22;
        else if (estimatedBitrate >= 192) score += 15;
        else if (estimatedBitrate >= 128) score += 8;

        if (format.SampleRate >= 48000) score += 12;
        else if (format.SampleRate >= 44100) score += 9;
        else score += 2;

        if (format.Channels >= 2) score += 8;
        if (analysis.DynamicRangeDb >= 10) score += 8;
        else if (analysis.DynamicRangeDb >= 5) score += 4;
        if (clipping) score -= 18;
        score = Math.Clamp(score, 1, 100);

        string tier = score >= 88 ? "is-lossless"
            : score >= 72 ? "is-high"
            : score >= 55 ? "is-good"
            : "is-low";

        return new SongQualityReport
        {
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            Codec = codec,
            Lossless = lossless,
            BitrateKbps = lossless ? 0 : estimatedBitrate,
            DynamicRangeDb = analysis.DynamicRangeDb,
            TruePeakDb = analysis.PeakDb,
            Clipping = clipping,
            ClippingPercent = Math.Round(clippingPercent, 4),
            Score = Math.Round(score, 1),
            Tier = tier,
            Summary = lossless
                ? $"{codec} · {format.SampleRate / 1000.0:0.#} kHz · lossless"
                : $"{codec} · {estimatedBitrate} kbps · {format.SampleRate / 1000.0:0.#} kHz"
        };
    }

    private static List<SongAnalysisFrame> Downsample(List<RawFrame> frames, double hopSeconds)
    {
        int stride = Math.Max(1, (int)Math.Ceiling(frames.Count / (double)MaxTimelinePoints));
        var result = new List<SongAnalysisFrame>(Math.Min(frames.Count, MaxTimelinePoints));
        for (int i = 0; i < frames.Count; i += stride)
        {
            var slice = frames.Skip(i).Take(stride).ToList();
            result.Add(new SongAnalysisFrame
            {
                Time = Math.Round(i * hopSeconds, 2),
                RmsDb = Round(slice.Average(f => f.RmsDb), 2),
                PeakDb = Round(slice.Max(f => f.PeakDb), 2),
                LowEnergy = Round(slice.Average(f => f.Low), 4),
                MidEnergy = Round(slice.Average(f => f.Mid), 4),
                HighEnergy = Round(slice.Average(f => f.High), 4),
                Novelty = Round(slice.Average(f => f.Flux), 4)
            });
        }
        return result;
    }

    private static double LoudnessFromRms(double rms)
    {
        if (rms <= 0) return -96;
        // BS.1770-style offset. The analyzer works on the decoded program signal;
        // this approximates integrated LUFS closely enough for normalisation and
        // the quality graph.
        return 20 * Math.Log10(rms) - 0.691;
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        double position = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(sorted.Count - 1, lower + 1);
        double fraction = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }

    private static double Round(double value, int digits)
        => double.IsFinite(value) ? Math.Round(value, digits) : 0;

    private void Report(string path, double progress, string stage)
    {
        try { _progress?.Invoke(new SongAnalysisProgress(path, Math.Clamp(progress, 0, 1), stage)); } catch { }
    }

    private sealed class RawFrame
    {
        public long Index;
        public double Rms;
        public double RmsDb;
        public double PeakDb;
        public double Flux;
        public double Low;
        public double Mid;
        public double High;
    }
}
