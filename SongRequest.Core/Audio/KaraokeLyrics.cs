using System;
using System.Collections.Generic;
using System.Linq;

namespace SongRequestDesktopV2Rewrite;

/// <summary>One detected syllable inside a timed lyric line.</summary>
public sealed class KaraokeSyllable
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";

    /// <summary>Index of the word this syllable belongs to (used for spacing).</summary>
    public int WordIndex { get; set; }
}

/// <summary>One word inside a timed lyric line, with its inferred syllables.</summary>
public sealed class KaraokeWord
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public List<KaraokeSyllable> Syllables { get; set; } = new();
}

/// <summary>A timed lyric line with syllable-level timing for karaoke display.</summary>
public sealed class KaraokeLine
{
    public double Time { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public List<KaraokeWord> Words { get; set; } = new();
    public List<KaraokeSyllable> Syllables { get; set; } = new();
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
/// Infers syllable-level timing from line-level timed lyrics and the song's
/// decoded analysis.
///
/// Line starts are gently snapped onto nearby vocal attacks, line ends are
/// trimmed to the last vocal activity before the next cue, and every word is
/// split into syllables whose boundaries are snapped to spectral-novelty /
/// energy-rise peaks. This gives the UI a much finer animation unit than whole
/// words while keeping every syllable inside its word and line.
/// </summary>
public static class KaraokeLyrics
{
    private const double MinPauseSeconds = 3.5;
    private const double MinWordSeconds = 0.08;
    private const double MinSyllableSeconds = 0.045;
    private const double MaxLineStartSnapSeconds = 0.24;

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

        // Snap every text line to a nearby vocal attack first, so the previous
        // line's end and pause can use the next line's actual (snapped) cue.
        var starts = SnapLineStarts(clean, duration, analysis);

        for (int i = 0; i < clean.Count; i++)
        {
            double start = Math.Min(starts[i], Math.Max(0, duration - 0.1));
            if (clean[i].Time.TotalSeconds >= duration) break;

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
                ? Math.Min(duration, starts[nextText])
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

            // Do not keep the wipe running through a long silent tail before the
            // next lyric line: end on the last vocal frame plus a short tail.
            end = RefineLineEnd(start, end, nextStart, analysis);
            end = Math.Max(start + 0.2, Math.Min(duration, end));

            var line = new KaraokeLine
            {
                Time = Round(start),
                End = Round(end),
                Text = text
            };
            line.Words = BuildWords(tokens, start, end, analysis);
            foreach (var word in line.Words)
                line.Syllables.AddRange(word.Syllables);
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

    private static double[] SnapLineStarts(
        IReadOnlyList<(TimeSpan Time, string Text)> clean,
        double duration,
        SongAnalysis? analysis)
    {
        var starts = new double[clean.Count];
        for (int i = 0; i < clean.Count; i++)
            starts[i] = Math.Max(0, Math.Min(duration, clean[i].Time.TotalSeconds));

        for (int i = 0; i < clean.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(clean[i].Text)) continue;

            int nextText = -1;
            for (int j = i + 1; j < clean.Count; j++)
            {
                if (!string.IsNullOrWhiteSpace(clean[j].Text)) { nextText = j; break; }
            }

            double nextOriginal = nextText >= 0
                ? Math.Min(duration, clean[nextText].Time.TotalSeconds)
                : duration;
            double immediateOriginal = i + 1 < clean.Count
                ? Math.Min(duration, clean[i + 1].Time.TotalSeconds)
                : duration;

            starts[i] = SnapLineStart(
                starts[i],
                Math.Min(nextOriginal, immediateOriginal),
                analysis);
        }

        return starts;
    }

    private static double SnapLineStart(double start, double nextStart, SongAnalysis? analysis)
    {
        double from = Math.Max(0, start - 0.10);
        double to = Math.Min(nextStart, start + MaxLineStartSnapSeconds + 0.06);
        if (to <= from) return start;

        var peaks = NoveltyPeaks(analysis, from, to);
        if (peaks.Count == 0) return start;

        double best = start;
        double bestDistance = MaxLineStartSnapSeconds;
        foreach (double peak in peaks)
        {
            double distance = Math.Abs(peak - start);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = peak;
            }
        }

        // Prefer moving later (the common LRC error), and never move far enough
        // to swallow the line's first syllable.
        if (best > start + 0.03 && best <= start + MaxLineStartSnapSeconds)
            return best;
        return start;
    }

    private static double RefineLineEnd(
        double start,
        double end,
        double nextStart,
        SongAnalysis? analysis)
    {
        var timeline = analysis?.Timeline;
        if (timeline == null || timeline.Count < 4) return end;

        double peakDb = double.MinValue;
        for (int i = 0; i < timeline.Count; i++)
        {
            double time = timeline[i].Time;
            if (time < start || time > nextStart) continue;
            peakDb = Math.Max(peakDb, timeline[i].RmsDb);
        }
        if (peakDb == double.MinValue || peakDb < -70) return end;

        // Keep frames within 18 dB of the loudest point in this line window. The
        // floor avoids treating a very quiet attack as the start of the tail.
        double threshold = Math.Max(peakDb - 18, -48);
        double lastVocal = start;
        for (int i = 0; i < timeline.Count; i++)
        {
            double time = timeline[i].Time;
            if (time < start || time > nextStart) continue;
            if (timeline[i].RmsDb >= threshold) lastVocal = time;
        }

        if (lastVocal <= start + 0.30) return end;

        double refined = lastVocal + 0.10;
        return refined < end - 0.12
            ? Math.Min(end, Math.Max(start + 0.30, refined))
            : end;
    }

    private static List<KaraokeWord> BuildWords(
        List<string> tokens,
        double start,
        double end,
        SongAnalysis? analysis)
    {
        var words = new List<KaraokeWord>();
        double total = Math.Max(0.1, end - start);
        var split = tokens.Select(Syllabify).ToList();
        var weights = split.Select(WordWeight).ToArray();
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
            double bestDistance = Math.Max(0.12, total / Math.Max(4, tokens.Count) * 0.30);
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
            var word = new KaraokeWord
            {
                Start = Round(boundaries[i]),
                End = Round(Math.Max(boundaries[i] + MinWordSeconds, boundaries[i + 1])),
                Text = tokens[i]
            };
            word.Syllables = BuildSyllables(
                tokens[i], split[i], word.Start, word.End, analysis, i);
            words.Add(word);
        }
        return words;
    }

    private static List<KaraokeSyllable> BuildSyllables(
        string text,
        List<string> parts,
        double start,
        double end,
        SongAnalysis? analysis,
        int wordIndex)
    {
        if (parts.Count <= 1)
        {
            return new List<KaraokeSyllable>
            {
                new()
                {
                    Start = Round(start),
                    End = Round(end),
                    Text = text,
                    WordIndex = wordIndex
                }
            };
        }

        double total = Math.Max(0.02, end - start);
        double min = Math.Min(MinSyllableSeconds, total / parts.Count * 0.45);
        var weights = parts.Select(SyllableWeight).ToArray();
        double weightSum = Math.Max(0.001, weights.Sum());
        var peaks = NoveltyPeaks(analysis, start, end);

        var boundaries = new List<double> { start };
        double cumulative = 0;
        for (int i = 0; i < parts.Count - 1; i++)
        {
            cumulative += weights[i];
            double ideal = start + total * (cumulative / weightSum);

            double snapped = ideal;
            double bestDistance = Math.Max(0.035, total / Math.Max(3, parts.Count) * 0.34);
            foreach (double peak in peaks)
            {
                double distance = Math.Abs(peak - ideal);
                if (distance < bestDistance && peak > boundaries[^1] + min && peak < end - min)
                {
                    bestDistance = distance;
                    snapped = peak;
                }
            }

            double minimum = boundaries[^1] + min;
            double maximum = end - (parts.Count - i - 1) * min;
            boundaries.Add(Math.Clamp(snapped, minimum, Math.Max(minimum, maximum)));
        }
        boundaries.Add(end);

        var syllables = new List<KaraokeSyllable>(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            double syllableEnd = i == parts.Count - 1
                ? end
                : Math.Max(boundaries[i] + min, boundaries[i + 1]);
            syllables.Add(new KaraokeSyllable
            {
                Start = Round(boundaries[i]),
                End = Round(syllableEnd),
                Text = parts[i],
                WordIndex = wordIndex
            });
        }
        return syllables;
    }

    private static List<string> SplitWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    /// <summary>
    /// Heuristic English syllabifier. It keeps the original characters (so the
    /// rendered line is identical to the lyric text) and splits at vowel groups:
    /// single intervocalic consonants go with the next syllable, consonant
    /// clusters are split after the first consonant, and common silent endings
    /// (-e, -es, -ed) are folded into the preceding syllable.
    /// </summary>
    private static List<string> Syllabify(string word)
    {
        if (string.IsNullOrEmpty(word)) return new List<string> { word };

        int first = 0;
        while (first < word.Length && !char.IsLetter(word[first])) first++;
        int last = word.Length - 1;
        while (last >= first && !char.IsLetter(word[last])) last--;
        if (first > last) return new List<string> { word };

        string prefix = word[..first];
        string core = word[first..(last + 1)];
        string suffix = word[(last + 1)..];

        // Hyphenated compounds keep their hyphens but are syllabified per part
        // (rock-n-roll → rock- / n- / roll).
        if (core.Contains('-'))
        {
            var parts = core.Split('-');
            var combined = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                var sub = Syllabify(parts[i]);
                if (sub.Count == 0) sub.Add(parts[i]);
                if (i < parts.Length - 1) sub[^1] += "-";
                combined.AddRange(sub);
            }

            if (combined.Count == 0) return new List<string> { word };
            combined[0] = prefix + combined[0];
            combined[^1] += suffix;
            return combined;
        }

        var nuclei = FindVowelGroups(core);
        if (nuclei.Count <= 1) return new List<string> { word };

        if (ShouldDropFinalE(core, nuclei[^1]))
            nuclei.RemoveAt(nuclei.Count - 1);
        if (nuclei.Count <= 1) return new List<string> { word };

        var boundaries = new List<int> { 0 };
        for (int i = 0; i < nuclei.Count - 1; i++)
        {
            int clusterStart = nuclei[i].End;
            int clusterEnd = nuclei[i + 1].Start;
            int length = clusterEnd - clusterStart;

            int boundary;
            if (length <= 0) boundary = nuclei[i + 1].Start;
            else if (length == 1) boundary = clusterStart;
            else if (length == 2 && IsValidOnset(core, clusterStart))
                boundary = clusterStart; // keep a valid two-consonant onset together
            else boundary = clusterStart + 1;

            boundary = Math.Clamp(
                boundary,
                boundaries[^1] + 1,
                Math.Max(boundaries[^1] + 1, core.Length - 1));
            boundaries.Add(boundary);
        }
        boundaries.Add(core.Length);

        var result = new List<string>(boundaries.Count - 1);
        for (int i = 0; i < boundaries.Count - 1; i++)
        {
            int from = boundaries[i];
            int to = boundaries[i + 1];
            if (to <= from) continue;

            string part = core[from..to];
            if (i == 0) part = prefix + part;
            if (i == boundaries.Count - 2) part += suffix;
            result.Add(part);
        }

        if (result.Count == 0) return new List<string> { word };

        // Never leave a punctuation-only fragment as its own animated syllable.
        for (int i = result.Count - 1; i >= 0; i--)
        {
            if (result[i].Any(char.IsLetterOrDigit)) continue;
            if (i > 0)
            {
                result[i - 1] += result[i];
                result.RemoveAt(i);
            }
            else if (result.Count > 1)
            {
                result[1] = result[0] + result[1];
                result.RemoveAt(0);
            }
        }

        return result;
    }

    private static List<(int Start, int End)> FindVowelGroups(string text)
    {
        var groups = new List<(int Start, int End)>();
        int i = 0;
        while (i < text.Length)
        {
            if (!IsVowel(text[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < text.Length && IsVowel(text[i])) i++;
            groups.Add((start, i));
        }
        return groups;
    }

    private static bool IsVowel(char value)
    {
        value = char.ToLowerInvariant(value);
        return value is 'a' or 'e' or 'i' or 'o' or 'u' or 'y'
            or 'á' or 'à' or 'â' or 'ä' or 'ã' or 'å'
            or 'é' or 'è' or 'ê' or 'ë'
            or 'í' or 'ì' or 'î' or 'ï'
            or 'ó' or 'ò' or 'ô' or 'ö' or 'õ' or 'ø'
            or 'ú' or 'ù' or 'û' or 'ü'
            or 'æ' or 'œ';
    }

    /// <summary>
    /// True when the final vowel group is a silent 'e' (or the 'e' of a silent
    /// plural/'-ed' ending) and should be folded into the previous syllable.
    /// </summary>
    private static bool ShouldDropFinalE(string core, (int Start, int End) final)
    {
        if (final.End - final.Start != 1) return false;
        if (char.ToLowerInvariant(core[final.Start]) != 'e') return false;

        bool atEnd = final.End == core.Length;
        bool beforeS = !atEnd &&
                       final.End == core.Length - 1 &&
                       (core[^1] == 's' || core[^1] == 'S');
        bool beforeD = !atEnd && !beforeS &&
                       final.End == core.Length - 1 &&
                       (core[^1] == 'd' || core[^1] == 'D');
        if (!atEnd && !beforeS && !beforeD) return false;

        if (final.Start == 0) return false;
        char before = char.ToLowerInvariant(core[final.Start - 1]);

        if (beforeS)
        {
            // "roses"/"wishes"/"boxes" pronounce the e; "makes"/"loves" do not.
            return !IsSibilant(core, final.Start);
        }

        if (beforeD)
        {
            // "wanted"/"needed" pronounce the e; "walked"/"played" do not.
            return before != 't' && before != 'd';
        }

        if (core.Length <= 2) return false;

        // "-le"/"-re" are syllabic only after a consonant: table / acre, but
        // while / care / smile keep a single syllable.
        if (before is 'l' or 'r')
        {
            char prior = final.Start >= 2 ? char.ToLowerInvariant(core[final.Start - 2]) : ' ';
            if (prior != ' ' && !IsVowel(prior)) return false;
        }

        return true;
    }

    private static bool IsSibilant(string core, int eIndex)
    {
        char before = char.ToLowerInvariant(core[eIndex - 1]);
        if (before is 's' or 'x' or 'z' or 'c') return true;
        if (before == 'h' && eIndex >= 2 && (core[eIndex - 2] is 's' or 'S' or 'c' or 'C'))
            return true;
        if (before == 'g' && eIndex >= 2 && (core[eIndex - 2] is 'n' or 'N'))
            return true;
        return false;
    }

    /// <summary>True for common two-consonant English syllable onsets.</summary>
    private static bool IsValidOnset(string core, int start)
    {
        if (start < 0 || start + 1 >= core.Length) return false;
        char a = char.ToLowerInvariant(core[start]);
        char b = char.ToLowerInvariant(core[start + 1]);

        // s + consonant (sp, st, sk, sm, sn, sl, sw)
        if (a == 's' && b is 'p' or 't' or 'k' or 'm' or 'n' or 'l' or 'w') return true;

        // stop/fricative + liquid (bl, br, cl, cr, dr, fl, fr, gl, gr, pl, pr, tr)
        if ((a is 'b' or 'c' or 'd' or 'f' or 'g' or 'k' or 'p' or 't') && (b is 'l' or 'r'))
            return true;

        // t/d + w (twelve, dwell)
        if ((a is 't' or 'd') && b == 'w') return true;

        return false;
    }

    private static double EstimateLineDuration(List<string> tokens)
    {
        int syllables = tokens.Sum(t => Syllabify(t).Count);
        int characters = tokens.Sum(t => t.Length);
        return 0.55 + syllables * 0.17 + characters * 0.012;
    }

    private static double WordWeight(List<string> syllables)
    {
        int letters = syllables.Sum(s => s.Count(char.IsLetterOrDigit));
        return 0.55 + syllables.Count * 0.55 + letters * 0.025;
    }

    private static double SyllableWeight(string syllable)
    {
        int vowels = Math.Max(1, FindVowelGroups(syllable).Count);
        int letters = Math.Max(1, syllable.Count(char.IsLetterOrDigit));
        return 0.35 + vowels * 0.45 + letters * 0.035;
    }

    private static int EstimateSyllables(string word)
    {
        return Math.Max(1, Syllabify(word).Count);
    }

    private static List<double> NoveltyPeaks(SongAnalysis? analysis, double start, double end)
    {
        var peaks = new List<double>();
        var timeline = analysis?.Timeline;
        if (timeline == null || timeline.Count < 3) return peaks;
        if (end <= start) return peaks;

        var strengths = new double[timeline.Count];
        double total = 0;
        int count = 0;
        for (int i = 1; i < timeline.Count; i++)
        {
            var frame = timeline[i];
            if (frame.Time <= start || frame.Time >= end) continue;

            // Vocal attacks show up both as spectral novelty and as a fast rise
            // in short-term energy. Combining them catches consonants/syllable
            // starts that a flux-only detector misses. Mid/high energy is
            // weighted up so a bass drum hit is less likely to win over a voice.
            double rise = Math.Max(0, frame.RmsDb - timeline[i - 1].RmsDb);
            double vocalBias = 0.55 + Math.Min(0.9,
                frame.MidEnergy * 2.2 + frame.HighEnergy * 1.4);
            strengths[i] = frame.Novelty * vocalBias + rise * 0.05;
            total += strengths[i];
            count++;
        }
        if (count == 0) return peaks;

        double mean = total / count;
        double max = 0;
        for (int i = 0; i < strengths.Length; i++)
            if (strengths[i] > max) max = strengths[i];
        if (max <= 0.0001) return peaks;

        double threshold = Math.Max(mean * 1.08, max * 0.12);
        double minSpacing = 0.075;
        for (int i = 1; i < timeline.Count - 1; i++)
        {
            var frame = timeline[i];
            if (frame.Time <= start || frame.Time >= end) continue;
            if (strengths[i] < threshold) continue;
            if (strengths[i] < strengths[i - 1] || strengths[i] < strengths[i + 1]) continue;
            if (peaks.Count > 0 && frame.Time - peaks[^1] < minSpacing) continue;
            peaks.Add(frame.Time);
        }
        return peaks;
    }

    private static double Round(double value) => Math.Round(value, 3);
}
