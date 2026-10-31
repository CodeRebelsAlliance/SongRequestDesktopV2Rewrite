using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SongRequestDesktopV2Rewrite;

/// <summary>One word inside a timed lyric line, with its inferred karaoke timing.</summary>
public sealed class KaraokeWord
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>A timed lyric line with word-level timing for karaoke display.</summary>
public sealed class KaraokeLine
{
    public double Time { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public List<KaraokeWord> Words { get; set; } = new();
}

/// <summary>A long instrumental/vocal gap between lyric lines.</summary>
public sealed class KaraokePause
{
    public double Start { get; set; }
    public double End { get; set; }
    public double Duration { get; set; }
}

/// <summary>The complete karaoke timing document embedded in lyrics events.</summary>
public sealed class KaraokeLyricsData
{
    public List<KaraokeLine> Lines { get; set; } = new();
    public List<KaraokePause> Pauses { get; set; } = new();
}

/// <summary>
/// Infers word-level timing from line-level timed lyrics and the song's decoded
/// analysis. Word boundaries follow syllable weight and are snapped to nearby
/// spectral-novelty peaks from <see cref="SongAnalysis.Timeline"/> so words land
/// on the actual vocal/attack transients instead of being evenly spaced.
/// </summary>
public static class KaraokeLyrics
{
    private const double MinPauseSeconds = 3.5;
    private const double MinWordSeconds = 0.08;

    public static KaraokeLyricsData Build(
        IReadOnlyList<(TimeSpan Time, string Text)> lines,
        SongAnalysis? analysis,
        double durationSeconds)
    {
        var result = new KaraokeLyricsData();
        if (lines == null || lines.Count == 0) return result;

        var clean = lines
            .Where(l => l.Time >= TimeSpan.Zero)
            .OrderBy(l => l.Time)
            .ToList();
        if (clean.Count == 0) return result;

        double duration = durationSeconds > 0
            ? durationSeconds
            : clean[^1].Time.TotalSeconds + 5;

        for (int i = 0; i < clean.Count; i++)
        {
            double start = Math.Max(0, clean[i].Time.TotalSeconds);
            if (start >= duration) break;

            string text = (clean[i].Text ?? string.Empty).Trim();
            var tokens = SplitWords(text);

            // Find the next line that actually has words. Empty timed entries
            // (instrumental markers, blank LRC lines) still need a karaoke line
            // slot so UI indexes stay aligned with syncedLyrics.
            int nextText = -1;
            for (int j = i + 1; j < clean.Count; j++)
            {
                if (!string.IsNullOrWhiteSpace(clean[j].Text)) { nextText = j; break; }
            }
            double nextStart = nextText >= 0
                ? Math.Min(duration, clean[nextText].Time.TotalSeconds)
                : duration;

            if (tokens.Count == 0)
            {
                result.Lines.Add(new KaraokeLine
                {
                    Time = Round(start),
                    End = Round(start),
                    Text = text
                });
                continue;
            }

            double interval = Math.Max(0.25, nextStart - start);
            double estimated = EstimateLineDuration(tokens);
            double end = Math.Min(nextStart - 0.04, start + Math.Min(interval * 0.92, Math.Max(1.0, estimated)));
            if (end <= start + 0.2) end = Math.Min(nextStart, start + Math.Max(0.5, interval * 0.92));
            end = Math.Max(start + 0.2, Math.Min(duration, end));

            var line = new KaraokeLine
            {
                Time = Round(start),
                End = Round(end),
                Text = text
            };
            line.Words = BuildWords(tokens, start, end, analysis);
            result.Lines.Add(line);

            double gapStart = end;
            double gapEnd = nextStart;
            if (gapEnd - gapStart >= MinPauseSeconds)
            {
                result.Pauses.Add(new KaraokePause
                {
                    Start = Round(gapStart),
                    End = Round(gapEnd),
                    Duration = Round(gapEnd - gapStart)
                });
            }
        }

        return result;
    }

    private static List<KaraokeWord> BuildWords(
        List<string> tokens,
        double start,
        double end,
        SongAnalysis? analysis)
    {
        var words = new List<KaraokeWord>();
        double total = Math.Max(0.1, end - start);
        var weights = tokens.Select(WordWeight).ToArray();
        double weightSum = Math.Max(0.001, weights.Sum());

        var peaks = NoveltyPeaks(analysis, start, end);
        var boundaries = new List<double> { start };
        double cumulative = 0;
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            cumulative += weights[i];
            double ideal = start + total * (cumulative / weightSum);

            // Snap to a real attack transient if one is close enough.
            double snapped = ideal;
            double bestDistance = Math.Max(0.14, total / Math.Max(4, tokens.Count) * 0.32);
            foreach (double peak in peaks)
            {
                double distance = Math.Abs(peak - ideal);
                if (distance < bestDistance && peak > boundaries[^1] + MinWordSeconds && peak < end - MinWordSeconds)
                {
                    bestDistance = distance;
                    snapped = peak;
                }
            }

            double minimum = boundaries[^1] + MinWordSeconds;
            double maximum = end - (tokens.Count - i - 1) * MinWordSeconds;
            boundaries.Add(Math.Clamp(snapped, minimum, Math.Max(minimum, maximum)));
        }
        boundaries.Add(end);

        for (int i = 0; i < tokens.Count; i++)
        {
            words.Add(new KaraokeWord
            {
                Start = Round(boundaries[i]),
                End = Round(Math.Max(boundaries[i] + MinWordSeconds, boundaries[i + 1])),
                Text = tokens[i]
            });
        }
        return words;
    }

    private static List<string> SplitWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static double EstimateLineDuration(List<string> tokens)
    {
        int syllables = tokens.Sum(w => EstimateSyllables(w));
        int characters = tokens.Sum(w => w.Length);
        return 0.65 + syllables * 0.19 + characters * 0.018;
    }

    private static double WordWeight(string word)
    {
        int syllables = EstimateSyllables(word);
        int letters = word.Count(char.IsLetterOrDigit);
        return 0.55 + syllables * 0.55 + letters * 0.025;
    }

    private static int EstimateSyllables(string word)
    {
        int count = 0;
        bool inVowel = false;
        foreach (char raw in word.ToLowerInvariant())
        {
            bool vowel = raw is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';
            if (vowel && !inVowel) count++;
            inVowel = vowel;
        }
        return Math.Max(1, count);
    }

    private static List<double> NoveltyPeaks(SongAnalysis? analysis, double start, double end)
    {
        var peaks = new List<double>();
        var timeline = analysis?.Timeline;
        if (timeline == null || timeline.Count < 3) return peaks;

        var strengths = new double[timeline.Count];
        double total = 0;
        int count = 0;
        for (int i = 1; i < timeline.Count; i++)
        {
            var frame = timeline[i];
            if (frame.Time <= start || frame.Time >= end) continue;

            // Vocal attacks show up both as spectral novelty and as a fast rise
            // in short-term energy. Combining them catches consonants/syllable
            // starts that a flux-only detector misses.
            double rise = Math.Max(0, frame.RmsDb - timeline[i - 1].RmsDb);
            strengths[i] = frame.Novelty + rise * 0.05;
            total += strengths[i];
            count++;
        }
        if (count == 0) return peaks;

        double threshold = total / count * 1.08;
        for (int i = 1; i < timeline.Count - 1; i++)
        {
            var frame = timeline[i];
            if (frame.Time <= start || frame.Time >= end) continue;
            if (strengths[i] < threshold) continue;
            if (strengths[i] >= strengths[i - 1] && strengths[i] >= strengths[i + 1])
                peaks.Add(frame.Time);
        }
        return peaks;
    }

    private static double Round(double value) => Math.Round(value, 3);
}
