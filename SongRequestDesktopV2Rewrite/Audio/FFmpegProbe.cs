using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Reads audio stream metadata (sample rate, channel count, duration) via
/// FFprobe, with a TagLib fallback for the duration.
/// </summary>
internal static class FFmpegProbe
{
    public readonly record struct Info(int SampleRate, int Channels, TimeSpan Duration);

    public static Info? Probe(string? ffprobePath, string path)
    {
        if (string.IsNullOrEmpty(ffprobePath)) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-select_streams", "a:0",
                "-show_entries", "stream=sample_rate,channels:format=duration",
                "-of", "default=noprint_wrappers=1",
                path
            })
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p == null) return null;

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            int rate = 0, channels = 0;
            double duration = 0;

            foreach (var line in output.Split('\n'))
            {
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line.Substring(0, idx).Trim();
                var value = line.Substring(idx + 1).Trim();

                if (key == "sample_rate") int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rate);
                else if (key == "channels") int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out channels);
                else if (key == "duration") double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            }

            if (rate <= 0 || channels <= 0) return null;

            var d = duration > 0 ? TimeSpan.FromSeconds(duration) : TimeSpan.Zero;
            return new Info(rate, channels, d);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best-effort duration from tags (may be inaccurate for WebM/Opus).</summary>
    public static TimeSpan TryDurationFromTagLib(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return file.Properties.Duration;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }
}
