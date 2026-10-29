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

    public SongAnalysis Analyze(
        string path,
        string title = "",
        string artist = "",
        IReadOnlyList<TimedLyricLine>? timedLyrics = null)
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
        result.Parts = DetectParts(frames, HopSize / (double)sampleRate, effectiveDuration,
            result.Bpm, result.BeatOffsetSeconds, result.BpmConfidence);
        result.Timeline = Downsample(frames, HopSize / (double)sampleRate);
        if (timedLyrics is { Count: >= 4 })
            RefinePartsWithLyrics(result, timedLyrics);
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
        var chroma = new double[12];
        double low = 0, mid = 0, high = 0, flux = 0;
        double sub = 0, bass = 0, lowMid = 0, presence = 0, brilliance = 0, air = 0;
        double centroidWeighted = 0, centroidPower = 0;
        for (int k = 1; k < bins; k++)
        {
            double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / FftSize;
            double logMag = Math.Log(1.0 + mag * 120.0);
            double power = mag * mag;
            double hz = k * sampleRate / (double)FftSize;

            if (hz < 250) low += power;
            else if (hz < 4000) mid += power;
            else high += power;

            if (hz < 120) sub += power;
            else if (hz < 250) bass += power;

            if (hz >= 250 && hz < 2000) lowMid += power;
            else if (hz >= 2000 && hz < 5000) presence += power;
            else if (hz >= 5000 && hz < 12000) brilliance += power;
            else if (hz >= 12000) air += power;

            if (hz >= 30 && hz <= 8000)
            {
                double midi = 69 + 12 * Math.Log(hz / 440.0, 2);
                int pitchClass = ((int)Math.Round(midi) % 12 + 12) % 12;
                chroma[pitchClass] += power;
            }

            centroidWeighted += hz * power;
            centroidPower += power;

            double diff = logMag - previousMagnitudes[k];
            if (diff > 0) flux += diff;
            previousMagnitudes[k] = logMag;
        }

        double totalBand = low + mid + high;
        if (totalBand <= 1e-12) totalBand = 1;
        double totalC = sub + bass + lowMid + presence + brilliance + air;
        if (totalC <= 1e-12) totalC = 1;
        double totalChroma = chroma.Sum();
        if (totalChroma > 1e-12)
        {
            for (int i = 0; i < chroma.Length; i++) chroma[i] /= totalChroma;
        }

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
            High = high / totalBand,
            Sub = sub / totalC,
            Bass = bass / totalC,
            LowMid = lowMid / totalC,
            Presence = presence / totalC,
            Brilliance = brilliance / totalC,
            Air = air / totalC,
            Chroma = chroma,
            Centroid = centroidPower > 0 ? centroidWeighted / centroidPower : 0
        };
    }

    private static (double Bpm, double Confidence, double BeatOffset, int Beats) DetectTempo(
        List<RawFrame> frames,
        double hopSeconds)
    {
        int n = frames.Count;
        if (n < 16) return (0, 0, 0, 0);

        // ── 1. Onset strength ────────────────────────────────────────────────
        // Log-magnitude spectral flux (already computed per frame) plus a
        // positive log-energy derivative. Adaptive whitening removes the local
        // mean so a loud sustained section cannot dominate the tempo.
        var onset = new double[n];
        for (int i = 0; i < n; i++)
        {
            double previousDb = i > 0 ? frames[i - 1].RmsDb : -96;
            double rise = Math.Max(0, frames[i].RmsDb - previousDb);
            onset[i] = frames[i].Flux + rise * 0.18;
        }

        int smoothWindow = Math.Max(16, (int)Math.Round(0.75 / hopSeconds));
        var baseline = new double[n];
        double running = 0;
        for (int i = 0; i < n; i++)
        {
            running += onset[i];
            if (i >= smoothWindow) running -= onset[i - smoothWindow];
            baseline[i] = running / Math.Min(i + 1, smoothWindow);
        }
        double globalMean = 0;
        for (int i = 0; i < n; i++)
        {
            onset[i] = Math.Max(0, onset[i] - baseline[i]);
            globalMean += onset[i];
        }
        globalMean = globalMean / n + 1e-9;

        // ── 2. Autocorrelation over the useful tempo range ───────────────────
        double minBpm = 52, maxBpm = 210;
        int minLag = Math.Max(2, (int)Math.Floor(60.0 / maxBpm / hopSeconds));
        int maxLag = Math.Min(n / 2 - 1, (int)Math.Ceiling(60.0 / minBpm / hopSeconds));
        if (maxLag <= minLag) return (0, 0, 0, 0);

        double meanOnset = onset.Average();
        double variance = onset.Sum(v => (v - meanOnset) * (v - meanOnset)) / n;
        if (variance <= 1e-12) return (0, 0, 0, 0);

        var scores = new double[maxLag + 1];
        var acfValues = new double[maxLag + 1];
        var phaseValues = new double[maxLag + 1];

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double acf = 0;
            for (int i = lag; i < n; i++)
                acf += (onset[i] - meanOnset) * (onset[i - lag] - meanOnset);
            acf /= (n - lag) * variance;

            // Harmonic summation makes the beat period win over a slightly
            // stronger bar-length lag.
            double harmonic = acf;
            if (lag * 2 <= maxLag) harmonic += 0.35 * Math.Max(0, acfAt(lag * 2));
            if (lag * 3 <= maxLag) harmonic += 0.18 * Math.Max(0, acfAt(lag * 3));
            acfValues[lag] = Math.Max(0, harmonic);

            // Phase coherence: how much stronger the onsets are on a grid with
            // this period than on average. This rejects lags that only fit noise.
            double bestPhaseAverage = 0;
            for (int phase = 0; phase < lag; phase++)
            {
                double sum = 0;
                int count = 0;
                for (int i = phase; i < n; i += lag) { sum += onset[i]; count++; }
                if (count > 0) bestPhaseAverage = Math.Max(bestPhaseAverage, sum / count);
            }
            phaseValues[lag] = bestPhaseAverage / globalMean;

            double bpm = 60.0 / (lag * hopSeconds);
            double phaseScore = Math.Clamp((phaseValues[lag] - 1.0) / 3.0, 0, 1);

            scores[lag] = (0.58 * acfValues[lag] + 0.42 * phaseScore) * TempoPrior(bpm);

            double acfAt(int otherLag)
            {
                double other = 0;
                for (int i = otherLag; i < n; i++)
                    other += (onset[i] - meanOnset) * (onset[i - otherLag] - meanOnset);
                other /= (n - otherLag) * variance;
                return other;
            }
        }

        // ── 3. Pick the strongest local peak, then check its octave family ───
        var peaks = new List<int>();
        for (int lag = minLag + 1; lag < maxLag; lag++)
        {
            if (scores[lag] >= scores[lag - 1] && scores[lag] >= scores[lag + 1] && scores[lag] > 0)
                peaks.Add(lag);
        }
        if (peaks.Count == 0) return (0, 0, 0, 0);

        peaks.Sort((a, b) => scores[b].CompareTo(scores[a]));
        int bestLag = peaks[0];
        double bestBpm = 60.0 / (bestLag * hopSeconds);
        double bestScore = scores[bestLag] * TempoPrior(bestBpm);

        var alternatives = new List<int>();
        foreach (int baseLag in new[] { bestLag / 3, bestLag / 2, bestLag, bestLag * 3 / 2, bestLag * 2, bestLag * 3 })
        {
            for (int delta = -2; delta <= 2; delta++)
                alternatives.Add(baseLag + delta);
        }

        foreach (int candidate in alternatives.Distinct())
        {
            if (candidate < minLag || candidate > maxLag) continue;
            double bpm = 60.0 / (candidate * hopSeconds);
            if (bpm < 65 || bpm > 190) continue;
            double adjusted = scores[candidate] * TempoPrior(bpm);
            if (adjusted > bestScore * 1.02)
            {
                bestLag = candidate;
                bestScore = adjusted;
                bestBpm = bpm;
            }
        }

        // ── 4. Confidence from peak contrast ────────────────────────────────
        bestScore = scores[bestLag];
        double second = peaks
            .Where(l => Math.Abs(l - bestLag) > Math.Max(2, bestLag * 0.06))
            .Select(l => scores[l])
            .DefaultIfEmpty(0)
            .Max();
        double meanScore = scores.Skip(minLag).Take(maxLag - minLag + 1).DefaultIfEmpty(0).Average();
        double stdScore = Math.Sqrt(scores.Skip(minLag).Take(maxLag - minLag + 1)
            .Select(s => (s - meanScore) * (s - meanScore)).DefaultIfEmpty(0).Average());
        double contrast = bestScore > 0 ? Math.Clamp((bestScore - second) / bestScore, 0, 1) : 0;
        double prominence = stdScore > 0 ? Math.Clamp((bestScore - meanScore) / stdScore / 2.5, 0, 1) : 0;
        double confidence = Math.Clamp(contrast * 0.45 + prominence * 0.55, 0, 1);

        // ── 5. Beat phase ───────────────────────────────────────────────────
        double bestPhase = 0;
        double bestPhaseScore = -1;
        for (int phase = 0; phase < bestLag; phase++)
        {
            double sum = 0;
            int count = 0;
            for (int i = phase; i < n; i += bestLag) { sum += onset[i]; count++; }
            double average = count > 0 ? sum / count : 0;
            if (average > bestPhaseScore) { bestPhaseScore = average; bestPhase = phase; }
        }

        double beatSeconds = 60.0 / Math.Max(1, bestBpm);
        int beats = (int)Math.Floor(n * hopSeconds / beatSeconds);
        return (bestBpm, confidence, bestPhase * hopSeconds, beats);
    }

    private static double TempoPrior(double bpm)
    {
        if (bpm <= 0) return 0.55;
        double octaves = Math.Log(bpm / 120.0, 2);
        double prior = Math.Exp(-0.5 * (octaves / 0.80) * (octaves / 0.80));
        return 0.55 + 0.45 * prior;
    }

    private static List<SongDrop> DetectDrops(List<RawFrame> frames, double hopSeconds)
    {
        int n = frames.Count;
        if (n < 8) return new List<SongDrop>();

        int beforeWindow = Math.Max(2, (int)(1.5 / hopSeconds));
        int afterWindow = Math.Max(2, (int)(2.0 / hopSeconds));
        int slopeWindow = Math.Max(2, (int)(3.0 / hopSeconds));

        double meanFlux = frames.Average(f => f.Flux);
        double fluxStd = Math.Sqrt(frames.Select(f => (f.Flux - meanFlux) * (f.Flux - meanFlux)).DefaultIfEmpty(0).Average());
        if (fluxStd <= 1e-9) fluxStd = 1;

        var candidates = new List<SongDrop>();
        for (int i = beforeWindow; i < n - afterWindow; i++)
        {
            double before = TopAverage(frames, i - beforeWindow, beforeWindow, f => f.RmsDb);
            double after = TopAverage(frames, i, afterWindow, f => f.RmsDb);
            double increase = after - before;
            if (after < -55 || increase < 2.0) continue;

            double subBefore = TopAverage(frames, i - beforeWindow, beforeWindow, f => f.Sub);
            double subAfter = TopAverage(frames, i, afterWindow, f => f.Sub);
            double presenceBefore = TopAverage(frames, i - beforeWindow, beforeWindow, f => f.Presence + f.Brilliance);
            double presenceAfter = TopAverage(frames, i, afterWindow, f => f.Presence + f.Brilliance);

            double older = TopAverage(frames, i - slopeWindow - beforeWindow, beforeWindow, f => f.RmsDb);
            double crescendo = before - older;
            double noveltyZ = (frames[i].Flux - meanFlux) / fluxStd;

            bool audibleJump = increase >= 3.0;
            bool timbralJump = noveltyZ >= 2.6 &&
                               (subAfter - subBefore > 0.035 || presenceAfter - presenceBefore > 0.035);
            if (!audibleJump && !timbralJump) continue;

            // Refine the exact transition to the largest positive frame-to-frame
            // energy jump nearby instead of the first frame that saw the window.
            int transition = i;
            double bestJump = double.MinValue;
            int search = Math.Max(1, (int)Math.Round(0.8 / hopSeconds));
            for (int j = Math.Max(1, i - search); j <= Math.Min(n - 1, i + search); j++)
            {
                double jump = frames[j].RmsDb - frames[j - 1].RmsDb;
                if (jump > bestJump) { bestJump = jump; transition = j; }
            }

            double energyScore = Math.Clamp((increase - 1.5) / 14.0, 0, 1);
            double noveltyScore = Math.Clamp((noveltyZ + 0.5) / 3.5, 0, 1);
            double subScore = Math.Clamp((subAfter - subBefore) / 0.15, 0, 1);
            double highScore = Math.Clamp((presenceAfter - presenceBefore) / 0.12, 0, 1);
            double silenceScore = before < -40 ? 1 : 0.35;
            double confidence = Math.Clamp(
                energyScore * 0.42 + noveltyScore * 0.26 + subScore * 0.14 +
                highScore * 0.10 + silenceScore * 0.08, 0.05, 1);

            if (confidence < 0.33) continue;

            bool buildUp = increase < 6 && crescendo > 2.5;
            string type = buildUp ? "build-up" : "drop";
            candidates.Add(new SongDrop
            {
                Time = Math.Round(transition * hopSeconds, 2),
                Confidence = Math.Round(confidence, 3),
                Type = type,
                Label = buildUp ? "Build-up" : "Drop"
            });
        }

        // Non-maximum suppression: one meaningful event per ~12-second region.
        var merged = new List<SongDrop>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Confidence))
        {
            if (merged.Any(m => Math.Abs(m.Time - candidate.Time) < 12)) continue;
            merged.Add(candidate);
        }

        return merged.OrderBy(d => d.Time).Take(12).ToList();
    }

    /// <summary>
    /// Average of the loudest 10% of frames in a window. Using the mean would make
    /// sparse material (clicks, stabs, a cappella) look silent between hits and
    /// hide the energy jump a drop detector is looking for.
    /// </summary>
    private static double TopAverage(List<RawFrame> frames, int start, int count, Func<RawFrame, double> selector)
    {
        int from = Math.Max(0, start);
        int to = Math.Min(frames.Count, start + count);
        if (to <= from) return -96;

        var values = new List<double>(to - from);
        for (int i = from; i < to; i++) values.Add(selector(frames[i]));
        values.Sort();
        int take = Math.Max(1, (int)Math.Ceiling(values.Count * 0.10));
        double sum = 0;
        for (int i = values.Count - take; i < values.Count; i++) sum += values[i];
        return sum / take;
    }

    private static double TopAverage(List<RawFrame> frames, int start, int count)
        => TopAverage(frames, start, count, f => f.RmsDb);

    /// <summary>
    /// Groups the decoded track into structural chapters. Boundaries come from
    /// energy/spectrum novelty, then repeated sections are clustered: the loud
    /// recurring cluster is a chorus, quieter recurring sections are verses, and
    /// a unique contrasting middle section becomes the bridge.
    /// </summary>
    private static List<SongPart> DetectParts(
        List<RawFrame> frames,
        double hopSeconds,
        double duration,
        double bpm,
        double beatOffset,
        double bpmConfidence)
    {
        if (frames.Count < 8 || duration <= 0)
            return new List<SongPart>();

        // Work at a musical block size when the tempo is trustworthy. Two bars
        // gives stable chroma/energy statistics, while a 2 s fallback keeps
        // speech-like or rubato material analysable.
        double barSeconds = bpm >= 55 && bpm <= 210 ? 4.0 * 60.0 / bpm : 0;
        double blockSeconds = barSeconds > 0 && bpmConfidence >= 0.15
            ? Math.Clamp(barSeconds, 1.5, 2.5)
            : 2.0;
        int blockFrames = Math.Max(1, (int)Math.Round(blockSeconds / hopSeconds));
        var blocks = new List<PartBlock>();

        for (int i = 0; i < frames.Count; i += blockFrames)
        {
            var slice = frames.Skip(i).Take(blockFrames).ToList();
            if (slice.Count == 0) continue;

            var chroma = new double[12];
            foreach (var frame in slice)
                for (int pc = 0; pc < 12; pc++) chroma[pc] += frame.Chroma[pc];
            double chromaSum = chroma.Sum();
            if (chromaSum > 1e-9)
                for (int pc = 0; pc < 12; pc++) chroma[pc] /= chromaSum;
            else
                chroma = new double[12];

            var block = new PartBlock
            {
                Start = i * hopSeconds,
                End = Math.Min(duration, (i + slice.Count) * hopSeconds),
                Low = slice.Average(f => f.Low),
                Mid = slice.Average(f => f.Mid),
                High = slice.Average(f => f.High),
                Sub = slice.Average(f => f.Sub),
                Bass = slice.Average(f => f.Bass),
                LowMid = slice.Average(f => f.LowMid),
                Presence = slice.Average(f => f.Presence),
                Brilliance = slice.Average(f => f.Brilliance),
                Air = slice.Average(f => f.Air),
                Centroid = slice.Average(f => f.Centroid),
                Flux = slice.Average(f => f.Flux),
                Chroma = chroma
            };

            double linear = slice.Average(f => Math.Pow(10, f.RmsDb / 20.0));
            block.RmsDb = linear > 0 ? 20 * Math.Log10(linear) : -96;
            blocks.Add(block);
        }

        if (blocks.Count < 3)
            return new List<SongPart> { FullSong(duration, blocks) };

        // Novelty between adjacent blocks: a change of harmony (chroma), timbre
        // (band balance), level or attack density. This is much more selective
        // than energy alone.
        var novelty = new double[blocks.Count];
        for (int i = 1; i < blocks.Count; i++)
        {
            var a = blocks[i - 1];
            var b = blocks[i];
            double chromaDistance = 1.0 - Cosine(a.Chroma, b.Chroma);
            double bandDistance =
                Math.Abs(b.Sub - a.Sub) + Math.Abs(b.Bass - a.Bass) +
                Math.Abs(b.LowMid - a.LowMid) + Math.Abs(b.Presence - a.Presence) +
                Math.Abs(b.Brilliance - a.Brilliance) + Math.Abs(b.Air - a.Air);
            novelty[i] =
                0.56 * chromaDistance +
                0.24 * bandDistance +
                0.13 * Math.Abs(b.RmsDb - a.RmsDb) / 8.0 +
                0.07 * Math.Abs(b.Flux - a.Flux);
        }

        // Change-point pass at two window sizes. This catches a verse→chorus
        // level lift even when the harmony stays the same (very common in pop).
        for (int i = 1; i < blocks.Count - 1; i++)
        {
            foreach (int scale in new[] { 2, 4 })
            {
                if (i - scale < 0 || i + scale > blocks.Count) continue;
                double before = blocks.Skip(i - scale).Take(scale).Average(b => b.RmsDb);
                double after = blocks.Skip(i).Take(scale).Average(b => b.RmsDb);
                double change = Math.Abs(after - before);
                if (change >= 1.5)
                    novelty[i] = Math.Max(novelty[i], Math.Min(0.55, change / 8.0));
            }
        }

        double mean = novelty.Skip(1).DefaultIfEmpty(0).Average();
        double variance = novelty.Skip(1).Select(v => (v - mean) * (v - mean)).DefaultIfEmpty(0).Average();
        double threshold = Math.Max(0.12, mean + Math.Sqrt(variance) * 0.85);
        double minPartSeconds = Math.Max(6.0, duration / 20.0);

        // Pick the strongest boundaries first, keeping every part at least
        // minPartSeconds long and capped so the progress bar stays readable.
        var boundaries = new List<double> { 0, duration };
        var candidates = Enumerable.Range(1, blocks.Count - 1)
            .OrderByDescending(i => novelty[i])
            .ToList();

        // Explicit edge pass: quiet intros/outros are often too gradual for a
        // peak-based novelty threshold, but their first/last active block is a
        // reliable chapter boundary.
        double medianBlockDb = Percentile(blocks.Select(b => b.RmsDb).ToList(), 0.5);
        int firstActive = blocks.FindIndex(b => b.RmsDb >= medianBlockDb - 4.0);
        if (firstActive > 0 && blocks[firstActive].Start < duration * 0.25)
            boundaries.Add(blocks[firstActive].Start);
        int lastActive = blocks.FindLastIndex(b => b.RmsDb >= medianBlockDb - 4.0);
        if (lastActive >= 0 && lastActive < blocks.Count - 1 &&
            blocks[lastActive].End > duration * 0.70)
            boundaries.Add(blocks[lastActive].End);

        // Independent change-point candidates for clear level shifts. Keeping
        // these separate from the novelty threshold prevents a noisy novelty
        // distribution from hiding a strong verse→chorus lift.
        var levelChanges = new List<(double At, double Magnitude)>();
        for (int i = 1; i < blocks.Count - 1; i++)
        {
            foreach (int scale in new[] { 2, 4 })
            {
                if (i - scale < 0 || i + scale > blocks.Count) continue;
                double before = blocks.Skip(i - scale).Take(scale).Average(b => b.RmsDb);
                double after = blocks.Skip(i).Take(scale).Average(b => b.RmsDb);
                double change = Math.Abs(after - before);
                if (change >= 2.5)
                    levelChanges.Add((blocks[i].Start, change));
            }
        }
        foreach (var change in levelChanges.OrderByDescending(c => c.Magnitude))
        {
            if (boundaries.Any(b => Math.Abs(b - change.At) < minPartSeconds)) continue;
            boundaries.Add(change.At);
            if (boundaries.Count >= 20) break;
        }

        foreach (int index in candidates)
        {
            if (novelty[index] < threshold) continue;
            double at = blocks[index].Start;

            // Snap confident boundaries to the nearest bar line so chapters
            // start where a listener hears the new section, not a fraction later.
            if (barSeconds >= 0.8 && bpmConfidence >= 0.2)
            {
                double snapped = beatOffset + Math.Round((at - beatOffset) / barSeconds) * barSeconds;
                if (snapped > 0.5 && snapped < duration - 0.5 &&
                    Math.Abs(snapped - at) <= barSeconds * 0.45)
                    at = snapped;
            }

            if (boundaries.Any(b => Math.Abs(b - at) < minPartSeconds)) continue;
            boundaries.Add(at);
            if (boundaries.Count >= 14) break;
        }

        boundaries.Sort();
        var segments = new List<PartSegment>();
        for (int i = 0; i < boundaries.Count - 1; i++)
        {
            double start = boundaries[i];
            double end = boundaries[i + 1];
            if (end - start < 0.5) continue;

            var inside = blocks.Where(b => b.Start >= start - 0.01 && b.Start < end - 0.01).ToList();
            if (inside.Count == 0) continue;

            var chroma = new double[12];
            foreach (var block in inside)
                for (int pc = 0; pc < 12; pc++) chroma[pc] += block.Chroma[pc];
            double chromaSum = chroma.Sum();
            if (chromaSum > 1e-9)
                for (int pc = 0; pc < 12; pc++) chroma[pc] /= chromaSum;

            var segment = new PartSegment
            {
                Start = start,
                End = end,
                RmsDb = inside.Average(b => b.RmsDb),
                Low = inside.Average(b => b.Low),
                Mid = inside.Average(b => b.Mid),
                High = inside.Average(b => b.High),
                Sub = inside.Average(b => b.Sub),
                Bass = inside.Average(b => b.Bass),
                LowMid = inside.Average(b => b.LowMid),
                Presence = inside.Average(b => b.Presence),
                Brilliance = inside.Average(b => b.Brilliance),
                Air = inside.Average(b => b.Air),
                Centroid = inside.Average(b => b.Centroid),
                Flux = inside.Average(b => b.Flux),
                Chroma = chroma
            };

            // Rising energy in the second half is the classic pre-chorus shape.
            int half = Math.Max(1, inside.Count / 2);
            segment.TrendDb = inside.Skip(half).DefaultIfEmpty(inside[^1]).Average(b => b.RmsDb) -
                              inside.Take(half).DefaultIfEmpty(inside[0]).Average(b => b.RmsDb);
            segments.Add(segment);
        }

        if (segments.Count == 0)
            return new List<SongPart> { FullSong(duration, blocks) };

        if (segments.Count == 1 && duration > 30)
        {
            // No usable boundary was found. Split long, uniform analyses into
            // equal chapters rather than pretending a whole song is one verse.
            var only = segments[0];
            double third = (only.End - only.Start) / 3.0;
            segments = new List<PartSegment>
            {
                Slice(only, only.Start, only.Start + third),
                Slice(only, only.Start + third, only.Start + third * 2),
                Slice(only, only.Start + third * 2, only.End)
            };
        }

        AssignPartTypes(segments);
        MergeAdjacent(segments);
        NumberParts(segments);
        return segments.Select(s => new SongPart
        {
            StartSeconds = Round(s.Start, 2),
            EndSeconds = Round(s.End, 2),
            DurationSeconds = Round(s.End - s.Start, 2),
            Type = s.Type,
            Label = s.Label,
            Confidence = Round(s.Confidence, 3),
            EnergyDb = Round(s.RmsDb, 2)
        }).ToList();
    }

    private static void AssignPartTypes(List<PartSegment> segments)
    {
        int count = segments.Count;
        var medianDb = Percentile(segments.Select(s => s.RmsDb).ToList(), 0.5);

        // Union repeated segments by feature similarity.
        var parent = Enumerable.Range(0, count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }

        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                if (Similarity(segments[i], segments[j]) >= 0.90)
                    Union(i, j);
            }
        }

        var clusters = Enumerable.Range(0, count).GroupBy(Find).Select(g => g.ToList()).ToList();
        var repeated = clusters.Where(c => c.Count >= 2).ToList();

        double EnergyRank(double db)
        {
            int below = segments.Count(s => s.RmsDb <= db);
            return segments.Count > 1 ? (double)below / (segments.Count - 1) : 0.5;
        }
        double BrightnessRank(double value)
        {
            int below = segments.Count(s => s.Presence + s.Brilliance <= value);
            return segments.Count > 1 ? (double)below / (segments.Count - 1) : 0.5;
        }
        double ChorusScore(List<int> cluster) =>
            cluster.Average(i => EnergyRank(segments[i].RmsDb)) * 0.60 +
            cluster.Average(i => BrightnessRank(segments[i].Presence + segments[i].Brilliance)) * 0.20 +
            Math.Min(1.0, cluster.Count / 3.0) * 0.20;

        var chorusCluster = repeated
            .OrderByDescending(ChorusScore)
            .FirstOrDefault();

        // A repeated loud cluster is the chorus. If there is no repeated high
        // energy cluster, the loudest section is still the most chorus-like.
        var chorusIndexes = new HashSet<int>(chorusCluster ?? new List<int>());
        if (chorusIndexes.Count == 0)
        {
            int loudest = Enumerable.Range(0, count)
                .OrderByDescending(i => segments[i].RmsDb)
                .First();
            if (segments[loudest].RmsDb > medianDb + 1.0) chorusIndexes.Add(loudest);
        }

        foreach (var cluster in repeated)
        {
            if (cluster == chorusCluster) continue;
            cluster.ForEach(i => segments[i].Type = "verse");
        }
        foreach (int i in chorusIndexes) segments[i].Type = "chorus";

        // Intro/outro are low-energy edges. Do not overwrite a repeated chorus
        // that happens to start the song.
        if (count > 2 && segments[0].RmsDb < medianDb - 1.0 &&
            segments[0].Start < Math.Min(15, segments[^1].End * 0.2))
            segments[0].Type = "intro";

        if (count > 2 && segments[^1].RmsDb < medianDb - 1.0 &&
            segments[^1].End - segments[^1].Start <= Math.Max(20, segments[^1].End * 0.2))
            segments[^1].Type = "outro";

        // Bridge: the unique middle section that contrasts most strongly with the
        // repeated verse/chorus material (different harmony, arrangement or energy).
        int bridge = -1;
        double bestContrast = 0;
        var repeatedCentres = repeated.SelectMany(c => c).ToList();
        for (int i = 1; i < count - 1; i++)
        {
            if (!string.IsNullOrEmpty(segments[i].Type)) continue;
            if (segments[i].Start < segments[^1].End * 0.28) continue;

            double maxSimilarity = repeatedCentres.Count > 0
                ? repeatedCentres.Max(c => Similarity(segments[i], segments[c]))
                : 0;
            double contrast = 1 - maxSimilarity;
            bool energyContrast = Math.Abs(segments[i].RmsDb - medianDb) >= 1.5;
            if ((contrast > 0.08 || energyContrast) && contrast > bestContrast)
            {
                bestContrast = contrast;
                bridge = i;
            }
        }
        if (bridge >= 0) segments[bridge].Type = "bridge";

        // Pre-chorus: a short rising section immediately before a chorus. A
        // recurring verse stays a verse unless its rise is very pronounced.
        var repeatedIndexes = new HashSet<int>(repeated.SelectMany(c => c));
        for (int i = 1; i < count; i++)
        {
            if (segments[i].Type != "chorus") continue;
            var before = segments[i - 1];
            bool repeatedBefore = repeatedIndexes.Contains(i - 1);
            if ((string.IsNullOrEmpty(before.Type) || before.Type == "verse") &&
                before.TrendDb > (repeatedBefore ? 4.0 : 1.5) &&
                before.End - before.Start <= 16)
            {
                before.Type = "pre-chorus";
                before.Confidence = Math.Max(before.Confidence, 0.6);
            }
        }

        // Anything still unclassified becomes a verse, unless it is as loud as
        // the chorus (then it is another chorus-like section).
        double chorusDb = chorusIndexes.Count > 0
            ? chorusIndexes.Average(i => segments[i].RmsDb)
            : medianDb + 3;
        foreach (var segment in segments)
        {
            if (!string.IsNullOrEmpty(segment.Type)) continue;
            segment.Type = segment.RmsDb >= chorusDb - 1.2 ? "chorus" : "verse";
            segment.Confidence = 0.45;
        }

        foreach (int i in chorusIndexes) segments[i].Confidence = 0.8;
        foreach (var cluster in repeated)
        {
            if (cluster == chorusCluster) continue;
            foreach (int i in cluster) segments[i].Confidence = 0.7;
        }
        for (int i = 0; i < count; i++)
        {
            if (segments[i].Confidence <= 0)
                segments[i].Confidence = segments[i].Type is "intro" or "outro" ? 0.75 : 0.55;
        }
    }

    private static void MergeAdjacent(List<PartSegment> segments)
    {
        for (int i = segments.Count - 1; i > 0; i--)
        {
            if (!string.Equals(segments[i - 1].Type, segments[i].Type, StringComparison.Ordinal))
                continue;
            if (Math.Abs(segments[i - 1].End - segments[i].Start) > 0.5) continue;

            double total = segments[i - 1].End - segments[i - 1].Start + segments[i].End - segments[i].Start;
            if (total <= 0) total = 1;
            double w1 = (segments[i - 1].End - segments[i - 1].Start) / total;
            double w2 = (segments[i].End - segments[i].Start) / total;
            segments[i - 1].End = segments[i].End;
            segments[i - 1].RmsDb = segments[i - 1].RmsDb * w1 + segments[i].RmsDb * w2;
            segments[i - 1].Confidence = Math.Max(segments[i - 1].Confidence, segments[i].Confidence);
            segments.RemoveAt(i);
        }
    }

    private static void NumberParts(List<PartSegment> segments)
    {
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment.Type)) segment.Type = "verse";
            string name = segment.Type switch
            {
                "pre-chorus" => "Pre-chorus",
                "chorus" => "Chorus",
                "verse" => "Verse",
                "intro" => "Intro",
                "outro" => "Outro",
                "bridge" => "Bridge",
                _ => "Section"
            };

            counters.TryGetValue(segment.Type, out int number);
            counters[segment.Type] = ++number;
            bool numbered = segment.Type is "verse" or "chorus" or "pre-chorus";
            segment.Label = numbered ? $"{name} {number}" : name;
        }
    }

    private static double Similarity(PartSegment a, PartSegment b)
    {
        // Harmony is the strongest structural cue, followed by timbre and level.
        // A repeated chorus keeps its chord progression even when the lyrics
        // change; a bridge normally changes both harmony and arrangement.
        double chroma = Cosine(a.Chroma, b.Chroma);
        var av = new[] { a.Sub, a.Bass, a.LowMid, a.Presence, a.Brilliance, a.Air };
        var bv = new[] { b.Sub, b.Bass, b.LowMid, b.Presence, b.Brilliance, b.Air };
        double bands = Cosine(av, bv);
        double energy = Math.Clamp(1.0 - Math.Abs(a.RmsDb - b.RmsDb) / 18.0, 0, 1);

        double similarity = 0.52 * chroma + 0.28 * bands + 0.20 * energy;
        if (Math.Abs(a.RmsDb - b.RmsDb) > 5.0)
            similarity *= 0.72; // verse vs. a much louder chorus
        return similarity;
    }

    private static double Cosine(double[] a, double[] b)
    {
        if (a == null || b == null || a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, am = 0, bm = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            am += a[i] * a[i];
            bm += b[i] * b[i];
        }
        if (am <= 1e-12 || bm <= 1e-12) return 0;
        return dot / (Math.Sqrt(am) * Math.Sqrt(bm));
    }

    private static PartSegment Slice(PartSegment source, double start, double end) => new()
    {
        Start = start,
        End = end,
        RmsDb = source.RmsDb,
        Low = source.Low,
        Mid = source.Mid,
        High = source.High,
        Sub = source.Sub,
        Bass = source.Bass,
        LowMid = source.LowMid,
        Presence = source.Presence,
        Brilliance = source.Brilliance,
        Air = source.Air,
        Centroid = source.Centroid,
        Flux = source.Flux,
        Chroma = source.Chroma.ToArray(),
        TrendDb = source.TrendDb,
        Confidence = 0.4
    };

    private static SongPart FullSong(double duration, List<PartBlock> blocks)
    {
        double db = blocks.Count > 0 ? blocks.Average(b => b.RmsDb) : -96;
        return new SongPart
        {
            StartSeconds = 0,
            EndSeconds = Round(duration, 2),
            DurationSeconds = Round(duration, 2),
            Type = "full",
            Label = "Full song",
            Confidence = 0.25,
            EnergyDb = Round(db, 2)
        };
    }

    /// <summary>
    /// Uses timed lyrics (<b>not</b> YouTube captions) to refine/split chapters.
    /// Repeated lyric phrases identify choruses; large gaps and unique phrases
    /// identify verse/bridge boundaries. This fixes the common failure where the
    /// whole middle of a song was merged into one very long "chorus".
    /// </summary>
    public static void RefinePartsWithLyrics(SongAnalysis analysis, IReadOnlyList<TimedLyricLine> lyrics)
    {
        if (analysis == null || analysis.Parts.Count == 0 || lyrics == null) return;

        var lines = lyrics
            .Where(l => !string.IsNullOrWhiteSpace(l.Text) && l.TimeSeconds >= 0 &&
                        l.TimeSeconds < analysis.DurationSeconds + 1)
            .OrderBy(l => l.TimeSeconds)
            .ToList();
        if (lines.Count < 4) return;

        int n = lines.Count;
        var times = lines.Select(l => l.TimeSeconds).ToArray();
        var norm = lines.Select(l => NormalizeLyric(l.Text)).ToArray();
        var endTimes = new double[n];
        for (int i = 0; i < n; i++)
        {
            double next = i + 1 < n ? times[i + 1] : analysis.DurationSeconds;
            endTimes[i] = Math.Min(next, times[i] + 8.0);
            if (endTimes[i] <= times[i]) endTimes[i] = times[i] + 1.0;
        }

        // Mark lines that occur in a repeated sequence of at least two lines.
        var repeated = new bool[n];
        var rawSpans = new List<(double Start, double End)>();
        for (int i = 0; i < n; i++)
        {
            if (norm[i].Length < 2) continue;
            for (int j = i + 1; j < n; j++)
            {
                if (!LyricsSimilar(norm[i], norm[j])) continue;
                int length = 0;
                while (i + length < n && j + length < n &&
                       LyricsSimilar(norm[i + length], norm[j + length]))
                    length++;

                if (length < 2) continue;

                for (int k = 0; k < length; k++)
                {
                    repeated[i + k] = true;
                    repeated[j + k] = true;
                }
                rawSpans.Add((times[i], endTimes[i + length - 1]));
                rawSpans.Add((times[j], endTimes[j + length - 1]));
            }
        }

        var repeatedSpans = MergeIntervals(rawSpans);
        var lyricBoundaries = new SortedSet<double>();

        // Instrumental gaps are reliable section boundaries.
        for (int i = 1; i < n; i++)
        {
            if (times[i] - endTimes[i - 1] > 4.0)
                lyricBoundaries.Add(times[i]);
        }

        // Repeated phrase starts/ends delimit choruses (and repeated verses).
        foreach (var span in repeatedSpans)
        {
            if (span.Start > 1) lyricBoundaries.Add(span.Start);
            if (span.End < analysis.DurationSeconds - 1) lyricBoundaries.Add(span.End);
        }

        var refined = new List<PartSegment>();
        foreach (var part in analysis.Parts)
        {
            double partDuration = part.EndSeconds - part.StartSeconds;
            if (partDuration <= 0.1) continue;

            double minEdge = Math.Max(3.0, partDuration * 0.14);
            var inside = lyricBoundaries
                .Where(b => b > part.StartSeconds + minEdge && b < part.EndSeconds - minEdge)
                .ToList();

            var points = new List<double> { part.StartSeconds };
            points.AddRange(inside);
            points.Add(part.EndSeconds);

            if (points.Count == 2)
            {
                var segment = PartToSegment(part);
                double coverage = RepeatedCoverage(part.StartSeconds, part.EndSeconds, repeatedSpans);
                if (IsFlexiblePart(part.Type))
                {
                    if (coverage >= 0.45)
                    {
                        segment.Type = "chorus";
                        segment.Confidence = Math.Max(segment.Confidence, 0.72);
                    }
                    else if (part.Type == "chorus" && coverage < 0.10 && partDuration > 30)
                    {
                        // A huge audio-only "chorus" with no repeated lyric content
                        // is much more likely the whole verse section.
                        segment.Type = "verse";
                        segment.Confidence = Math.Max(segment.Confidence, 0.55);
                    }
                }
                refined.Add(segment);
                continue;
            }

            for (int i = 0; i < points.Count - 1; i++)
            {
                double start = points[i];
                double end = points[i + 1];
                if (end - start < 1.0) continue;

                var segment = new PartSegment
                {
                    Start = start,
                    End = end,
                    RmsDb = part.EnergyDb,
                    Type = part.Type,
                    Confidence = part.Confidence
                };

                double coverage = RepeatedCoverage(start, end, repeatedSpans);
                if (IsFlexiblePart(part.Type))
                {
                    segment.Type = coverage >= 0.30 ? "chorus" : "verse";
                    segment.Confidence = coverage >= 0.30 ? 0.75 : 0.55;
                }

                refined.Add(segment);
            }
        }

        if (refined.Count == 0) return;

        MergeAdjacent(refined);
        NumberParts(refined);
        analysis.Parts = refined.Select(s => new SongPart
        {
            StartSeconds = Round(s.Start, 2),
            EndSeconds = Round(s.End, 2),
            DurationSeconds = Round(s.End - s.Start, 2),
            Type = s.Type,
            Label = s.Label,
            Confidence = Round(s.Confidence, 3),
            EnergyDb = Round(s.RmsDb, 2)
        }).ToList();
        analysis.LyricsUsedForParts = true;
    }

    private static bool IsFlexiblePart(string type)
        => type is "verse" or "chorus" or "full" or "pre-chorus";

    private static PartSegment PartToSegment(SongPart part) => new()
    {
        Start = part.StartSeconds,
        End = part.EndSeconds,
        RmsDb = part.EnergyDb,
        Type = part.Type,
        Confidence = part.Confidence
    };

    private static string NormalizeLyric(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var builder = new System.Text.StringBuilder(text.Length);
        bool space = false;
        foreach (char ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (space && builder.Length > 0) builder.Append(' ');
                builder.Append(ch);
                space = false;
            }
            else
            {
                space = true;
            }
        }
        return builder.ToString().Trim();
    }

    private static bool LyricsSimilar(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        if (a.Length < 4 || b.Length < 4) return false;
        if (Math.Abs(a.Length - b.Length) > Math.Max(a.Length, b.Length) * 0.35) return false;

        // Levenshtein ratio: repeated chorus lines often differ in one word
        // ("again", a backing vocal, punctuation), so exact equality is brittle.
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0) return false;
        if (n * m > 40000) return false;

        var previous = new int[m + 1];
        var current = new int[m + 1];
        for (int j = 0; j <= m; j++) previous[j] = j;

        for (int i = 1; i <= n; i++)
        {
            current[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        double similarity = 1.0 - (double)previous[m] / Math.Max(n, m);
        return similarity >= 0.86;
    }

    private static List<(double Start, double End)> MergeIntervals(List<(double Start, double End)> intervals)
    {
        var merged = new List<(double Start, double End)>();
        foreach (var interval in intervals.OrderBy(i => i.Start))
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End + 0.2)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.End, interval.End));
            }
            else
            {
                merged.Add(interval);
            }
        }
        return merged;
    }

    private static double RepeatedCoverage(
        double start, double end, List<(double Start, double End)> repeatedSpans)
    {
        if (end <= start) return 0;
        double covered = 0;
        foreach (var span in repeatedSpans)
        {
            double overlap = Math.Min(end, span.End) - Math.Max(start, span.Start);
            if (overlap > 0) covered += overlap;
        }
        return Math.Clamp(covered / (end - start), 0, 1);
    }

    private sealed class PartBlock
    {
        public double Start;
        public double End;
        public double RmsDb;
        public double Low;
        public double Mid;
        public double High;
        public double Sub;
        public double Bass;
        public double LowMid;
        public double Presence;
        public double Brilliance;
        public double Air;
        public double Centroid;
        public double Flux;
        public double[] Chroma = new double[12];
    }

    private sealed class PartSegment
    {
        public double Start;
        public double End;
        public double RmsDb;
        public double Low;
        public double Mid;
        public double High;
        public double Sub;
        public double Bass;
        public double LowMid;
        public double Presence;
        public double Brilliance;
        public double Air;
        public double Centroid;
        public double Flux;
        public double[] Chroma = new double[12];
        public double TrendDb;
        public string Type = "";
        public string Label = "";
        public double Confidence;
    }

    private static SongQualityReport BuildQuality(        string path,
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
        public double Sub;
        public double Bass;
        public double LowMid;
        public double Presence;
        public double Brilliance;
        public double Air;
        public double Centroid;
        public double[] Chroma = new double[12];
    }
}
