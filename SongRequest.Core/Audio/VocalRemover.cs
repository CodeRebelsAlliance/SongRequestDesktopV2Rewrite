using System;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Quality-first on-device vocal removal.
///
/// Uses a stereo STFT center-channel suppressor: vocals are normally panned to
/// the centre, so bins with high left/right coherence are attenuated while
/// hard-panned instruments and low bass are preserved. The result is a 16-bit
/// PCM WAV that every platform decoder can play.
/// </summary>
public static class VocalRemover
{
    public const string AlgorithmVersion = "2.1";

    public static void Process(
        string inputPath,
        string outputPath,
        Action<double, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var source = AudioSourceFactory.Open(inputPath);
        var format = source.WaveFormat;
        if (format.Channels < 2)
            throw new InvalidOperationException(
                "Karaoke generation needs a stereo source; this track is mono.");

        int sampleRate = Math.Max(8000, format.SampleRate);
        int windowSize = sampleRate >= 32000 ? 4096 : 2048;
        int hop = windowSize / 8;
        int channels = format.Channels;

        var window = new double[windowSize];
        for (int i = 0; i < windowSize; i++)
            window[i] = Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (windowSize - 1)));

        var leftWindow = new double[windowSize];
        var rightWindow = new double[windowSize];
        var outLeft = new double[windowSize];
        var outRight = new double[windowSize];
        var norm = new double[windowSize];
        var reL = new double[windowSize];
        var imL = new double[windowSize];
        var reR = new double[windowSize];
        var imR = new double[windowSize];
        var inputBuffer = new float[windowSize * channels];
        var mask = new double[windowSize];
        var previousMask = new double[windowSize];
        var magnitude = new double[windowSize];
        var previousMagnitude = new double[windowSize];
        var smoothMask = new double[windowSize];
        double fluxAverage = 0;

        double totalFrames = source.TotalTime > TimeSpan.Zero
            ? source.TotalTime.TotalSeconds * sampleRate
            : 0;
        long emittedFrames = 0;
        bool sourceEnded = false;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var tempPath = outputPath + ".part";
        using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        using var writer = new BinaryWriter(stream);
        WriteWavHeader(writer, sampleRate, 2, 0);

        // Prime the first window.
        ReadIntoWindow(source, leftWindow, rightWindow, 0, windowSize, inputBuffer, channels);

        while (!sourceEnded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessWindow(sampleRate, windowSize, hop, window, leftWindow, rightWindow,
                outLeft, outRight, norm, reL, imL, reR, imR, mask, previousMask,
                magnitude, previousMagnitude, smoothMask, ref fluxAverage);

            int framesToEmit = hop;
            if (totalFrames > 0)
            {
                long remaining = (long)totalFrames - emittedFrames;
                if (remaining <= 0) break;
                if (remaining < framesToEmit) framesToEmit = (int)remaining;
            }

            for (int i = 0; i < framesToEmit; i++)
            {
                double n = norm[i];
                double l = n > 1e-8 ? outLeft[i] / n : 0;
                double r = n > 1e-8 ? outRight[i] / n : 0;
                writer.Write((short)Math.Clamp((int)Math.Round(l * short.MaxValue), short.MinValue, short.MaxValue));
                writer.Write((short)Math.Clamp((int)Math.Round(r * short.MaxValue), short.MinValue, short.MaxValue));
            }
            emittedFrames += framesToEmit;

            // Slide the overlap-add and analysis windows by one hop.
            Array.Copy(outLeft, hop, outLeft, 0, windowSize - hop);
            Array.Copy(outRight, hop, outRight, 0, windowSize - hop);
            Array.Copy(norm, hop, norm, 0, windowSize - hop);
            Array.Clear(outLeft, windowSize - hop, hop);
            Array.Clear(outRight, windowSize - hop, hop);
            Array.Clear(norm, windowSize - hop, hop);

            Array.Copy(leftWindow, hop, leftWindow, 0, windowSize - hop);
            Array.Copy(rightWindow, hop, rightWindow, 0, windowSize - hop);
            Array.Clear(leftWindow, windowSize - hop, hop);
            Array.Clear(rightWindow, windowSize - hop, hop);

            int read = ReadIntoWindow(source, leftWindow, rightWindow, windowSize - hop, hop, inputBuffer, channels);
            if (read < hop) sourceEnded = true;

            if (totalFrames <= 0 && sourceEnded && emittedFrames >= windowSize)
                break;

            if (emittedFrames % (hop * 8) == 0)
            {
                double pct = totalFrames > 0 ? Math.Clamp(emittedFrames / totalFrames, 0, 1) : 0;
                progress?.Invoke(0.15 + pct * 0.8, "Separating vocals");
            }
        }

        // Flush the final overlap tail: process zero-padded windows until the
        // accumulated output has been emitted.
        int guard = 0;
        while (totalFrames > 0 && emittedFrames < totalFrames && guard++ < 8)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessWindow(sampleRate, windowSize, hop, window, leftWindow, rightWindow,
                outLeft, outRight, norm, reL, imL, reR, imR, mask, previousMask,
                magnitude, previousMagnitude, smoothMask, ref fluxAverage);
            int remaining = (int)Math.Min(hop, totalFrames - emittedFrames);
            for (int i = 0; i < remaining; i++)
            {
                double n = norm[i];
                double l = n > 1e-8 ? outLeft[i] / n : 0;
                double r = n > 1e-8 ? outRight[i] / n : 0;
                writer.Write((short)Math.Clamp((int)Math.Round(l * short.MaxValue), short.MinValue, short.MaxValue));
                writer.Write((short)Math.Clamp((int)Math.Round(r * short.MaxValue), short.MinValue, short.MaxValue));
            }
            emittedFrames += remaining;
            Array.Copy(outLeft, hop, outLeft, 0, windowSize - hop);
            Array.Copy(outRight, hop, outRight, 0, windowSize - hop);
            Array.Copy(norm, hop, norm, 0, windowSize - hop);
            Array.Clear(outLeft, windowSize - hop, hop);
            Array.Clear(outRight, windowSize - hop, hop);
            Array.Clear(norm, windowSize - hop, hop);
        }

        long dataBytes = stream.Position - 44;
        writer.Flush();
        stream.Position = 0;
        WriteWavHeader(writer, sampleRate, 2, dataBytes);
        writer.Flush();
        stream.Position = stream.Length;

        if (File.Exists(outputPath)) File.Delete(outputPath);
        File.Move(tempPath, outputPath);
        progress?.Invoke(1.0, "Karaoke version ready");
    }

    private static int ReadIntoWindow(
        IAudioSource source,
        double[] left,
        double[] right,
        int frameOffset,
        int frames,
        float[] inputBuffer,
        int channels)
    {
        int wanted = frames * channels;
        int read = source.Read(inputBuffer, 0, wanted);
        int frameCount = read / channels;
        for (int i = 0; i < frameCount; i++)
        {
            left[frameOffset + i] = inputBuffer[i * channels];
            right[frameOffset + i] = inputBuffer[i * channels + 1];
        }
        return frameCount;
    }

    private static void ProcessWindow(
        int sampleRate,
        int windowSize,
        int hop,
        double[] window,
        double[] leftWindow,
        double[] rightWindow,
        double[] outLeft,
        double[] outRight,
        double[] norm,
        double[] reL,
        double[] imL,
        double[] reR,
        double[] imR,
        double[] mask,
        double[] previousMask,
        double[] magnitude,
        double[] previousMagnitude,
        double[] smoothMask,
        ref double fluxAverage)
    {
        for (int i = 0; i < windowSize; i++)
        {
            reL[i] = leftWindow[i] * window[i];
            imL[i] = 0;
            reR[i] = rightWindow[i] * window[i];
            imR[i] = 0;
        }

        AnalysisFft.Forward(reL, imL);
        AnalysisFft.Forward(reR, imR);

        int bins = windowSize / 2 + 1;
        double flux = 0;
        for (int k = 0; k < bins; k++)
        {
            double mag = Math.Sqrt(reL[k] * reL[k] + imL[k] * imL[k] +
                                   reR[k] * reR[k] + imR[k] * imR[k]);
            magnitude[k] = mag;
            double diff = mag - previousMagnitude[k];
            if (diff > 0) flux += diff;
            previousMagnitude[k] = mag;
        }

        double transientStrength = 0;
        if (fluxAverage <= 0)
        {
            fluxAverage = flux;
        }
        else
        {
            double ratio = flux / (fluxAverage + 1e-9);
            transientStrength = Math.Clamp((ratio - 1.6) / 2.8, 0, 1);
            fluxAverage = fluxAverage * 0.90 + flux * 0.10;
        }

        for (int k = 0; k < bins; k++)
        {
            double lr = reL[k];
            double li = imL[k];
            double rr = reR[k];
            double ri = imR[k];

            double powerL = lr * lr + li * li;
            double powerR = rr * rr + ri * ri;
            double totalPower = powerL + powerR;
            if (totalPower < 1e-12)
            {
                mask[k] = 0;
                if (k > 0 && k < windowSize / 2) mask[windowSize - k] = 0;
                continue;
            }

            // Signed coherence: only in-phase center material is a vocal candidate.
            double cross = 2 * (lr * rr + li * ri);
            double coherence = Math.Clamp(cross / totalPower, 0, 1);

            double centerRe = (lr + rr) * 0.5;
            double centerIm = (li + ri) * 0.5;
            double sideRe = (lr - rr) * 0.5;
            double sideIm = (li - ri) * 0.5;
            double centerMag = Math.Sqrt(centerRe * centerRe + centerIm * centerIm);
            double sideMag = Math.Sqrt(sideRe * sideRe + sideIm * sideIm);
            double hz = k * (double)sampleRate / windowSize;

            double band = VocalRangeWeight(hz);
            double tonal = TonalWeight(magnitude, k, bins);
            double sideRatio = sideMag / (centerMag + sideMag + 1e-12);
            double dominance = centerMag / (centerMag + sideMag + 1e-12);

            // Strongly attenuate centered, in-phase vocal material while leaving
            // side-panned instruments and the bass range intact. Coherence is the
            // primary cue; center dominance keeps partly-panned vocals in the mask.
            double m = band *
                       Math.Pow(coherence, 0.75) *
                       Math.Clamp(0.35 + 0.65 * dominance, 0, 1) *
                       (0.82 + 0.18 * tonal) *
                       (1.0 - 0.28 * transientStrength);

            // Side-heavy content is instrumental even if it happens to be centered.
            m *= Math.Clamp(0.94 + sideRatio * 0.06, 0, 1);
            m = Math.Clamp(m * 1.04, 0, 0.995);
            mask[k] = m;
            if (k > 0 && k < windowSize / 2) mask[windowSize - k] = m;
        }

        // Frequency smoothing prevents "musical noise" from isolated bins; a
        // short temporal smoothing keeps the mask from chattering between frames.
        for (int k = 0; k < windowSize; k++)
        {
            double sum = 0;
            int count = 0;
            for (int j = Math.Max(0, k - 2); j <= Math.Min(windowSize - 1, k + 2); j++)
            {
                sum += mask[j];
                count++;
            }
            double smoothed = sum / count;
            smoothMask[k] = previousMask[k] * 0.28 + smoothed * 0.72;
            previousMask[k] = smoothMask[k];
        }

        for (int k = 0; k < windowSize; k++)
        {
            double gain = smoothMask[k] * 0.98;
            double centerRe = (reL[k] + reR[k]) * 0.5;
            double centerIm = (imL[k] + imR[k]) * 0.5;
            reL[k] -= gain * centerRe;
            imL[k] -= gain * centerIm;
            reR[k] -= gain * centerRe;
            imR[k] -= gain * centerIm;
        }

        InverseFft(reL, imL);
        InverseFft(reR, imR);

        for (int i = 0; i < windowSize; i++)
        {
            double w2 = window[i] * window[i];
            outLeft[i] += reL[i] * window[i];
            outRight[i] += reR[i] * window[i];
            norm[i] += w2;
        }
    }

    private static double TonalWeight(double[] magnitude, int bin, int bins)
    {
        double sum = 0;
        double logSum = 0;
        int count = 0;
        for (int j = Math.Max(1, bin - 4); j <= Math.Min(bins - 1, bin + 4); j++)
        {
            double m = magnitude[j] + 1e-12;
            sum += m;
            logSum += Math.Log(m);
            count++;
        }
        if (count == 0) return 0.5;
        double arithmetic = sum / count;
        double geometric = Math.Exp(logSum / count);
        double flatness = arithmetic > 0 ? geometric / arithmetic : 1;
        return Math.Clamp(1.0 - flatness * 3.2, 0, 1);
    }

    private static double VocalRangeWeight(double hz)
    {
        if (hz < 100) return 0.0;
        if (hz < 180) return (hz - 100) / 80.0 * 0.75;
        if (hz <= 7000) return 1.0;
        if (hz <= 12000) return 1.0 - (hz - 7000) / 5000.0 * 0.55;
        return 0.20;
    }

    private static void InverseFft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 0; i < n; i++) im[i] = -im[i];
        AnalysisFft.Forward(re, im);
        for (int i = 0; i < n; i++)
        {
            re[i] /= n;
            im[i] = -im[i] / n;
        }
    }

    private static void WriteWavHeader(BinaryWriter writer, int sampleRate, int channels, long dataBytes)
    {
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write((int)Math.Min(int.MaxValue, 36 + dataBytes));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write((int)Math.Min(int.MaxValue, dataBytes));
    }
}
