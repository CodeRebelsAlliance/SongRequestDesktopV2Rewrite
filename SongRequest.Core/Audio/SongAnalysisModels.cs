using System;
using System.Collections.Generic;

namespace SongRequestDesktopV2Rewrite;

/// <summary>One point of the analysis timeline (used by the audio path graph).</summary>
public sealed class SongAnalysisFrame
{
    public double Time { get; set; }
    public double RmsDb { get; set; }
    public double PeakDb { get; set; }
    public double LowEnergy { get; set; }
    public double MidEnergy { get; set; }
    public double HighEnergy { get; set; }
    public double Novelty { get; set; }
}

/// <summary>A detected drop/build-up/breakdown with a confidence (0..1).</summary>
public sealed class SongDrop
{
    public double Time { get; set; }
    public double Confidence { get; set; }

    /// <summary>drop, build-up or breakdown.</summary>
    public string Type { get; set; } = "drop";

    public string Label { get; set; } = "Drop";
}

/// <summary>A detected structural section (intro, verse, chorus, bridge, outro…).</summary>
public sealed class SongPart
{
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public double DurationSeconds { get; set; }

    /// <summary>Machine-readable kind: intro, verse, pre-chorus, chorus, bridge, outro.</summary>
    public string Type { get; set; } = "verse";

    /// <summary>Display label, e.g. "Chorus 1" or "Verse 2".</summary>
    public string Label { get; set; } = "Verse";

    /// <summary>How strongly the section was recognised (0..1).</summary>
    public double Confidence { get; set; }

    public double EnergyDb { get; set; }
}

/// <summary>Signal/quality facts gathered while decoding the track.</summary>
public sealed class SongQualityReport
{
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string Codec { get; set; } = "";
    public bool Lossless { get; set; }
    public int BitrateKbps { get; set; }
    public double DynamicRangeDb { get; set; }
    public double TruePeakDb { get; set; }
    public bool Clipping { get; set; }
    public double ClippingPercent { get; set; }

    /// <summary>0..100 composite score used by the quality graph/badge.</summary>
    public double Score { get; set; }

    public string Tier { get; set; } = "unknown";
    public string Summary { get; set; } = "";
}

/// <summary>Complete result of analysing one audio file.</summary>
public sealed class SongAnalysis
{
    public string SongPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public DateTime FileModifiedUtc { get; set; }
    public DateTime AnalyzedUtc { get; set; } = DateTime.UtcNow;
    public string AlgorithmVersion { get; set; } = "1.0";

    public double DurationSeconds { get; set; }
    public double Bpm { get; set; }
    public double BpmConfidence { get; set; }
    public double BeatOffsetSeconds { get; set; }
    public int BeatsDetected { get; set; }

    /// <summary>Integrated loudness in LUFS (BS.1770-style K-weighting approximation).</summary>
    public double IntegratedLoudnessLufs { get; set; }
    public double LoudnessRangeLu { get; set; }
    public double PeakDb { get; set; }
    public double AverageRmsDb { get; set; }
    public double DynamicRangeDb { get; set; }

    public bool IsLowConfidence { get; set; }
    public string AnalysisPath { get; set; } = "";

    public SongQualityReport Quality { get; set; } = new();
    public List<SongDrop> Drops { get; set; } = new();
    public List<SongPart> Parts { get; set; } = new();
    public List<SongAnalysisFrame> Timeline { get; set; } = new();

    public string DisplayBpm => Bpm > 0 ? Math.Round(Bpm).ToString("0") : "—";
    public string DisplayLoudness => IntegratedLoudnessLufs > -70
        ? IntegratedLoudnessLufs.ToString("0.0") + " LUFS"
        : "—";
}

/// <summary>Progress reported while a background analysis runs.</summary>
public readonly record struct SongAnalysisProgress(string SongPath, double Progress, string Stage);

/// <summary>Small radix-2 FFT shared by the analyzer (no external dependency).</summary>
internal static class AnalysisFft
{
    public static void Forward(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    int a = i + j;
                    int b = a + len / 2;
                    double vRe = re[b] * curRe - im[b] * curIm;
                    double vIm = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - vRe;
                    im[b] = im[a] - vIm;
                    re[a] += vRe;
                    im[a] += vIm;
                    double nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
