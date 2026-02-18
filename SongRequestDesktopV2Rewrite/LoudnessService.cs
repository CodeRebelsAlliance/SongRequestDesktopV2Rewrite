using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite
{
    public static class LoudnessService
    {
        /// <summary>
        /// Calculates the perceived loudness of an audio file in LUFS (approximated via RMS).
        /// Returns the loudness value in dB (LUFS-like), or null if calculation fails.
        /// </summary>
        public static double? CalculateLoudness(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                using var reader = new AudioFileReader(filePath);
                double sumSquares = 0;
                long sampleCount = 0;
                var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];

                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        sumSquares += (double)buffer[i] * buffer[i];
                    }
                    sampleCount += read;
                }

                if (sampleCount == 0) return null;

                double rms = Math.Sqrt(sumSquares / sampleCount);
                if (rms <= 0) return null;

                // Convert RMS to dB (LUFS-like approximation)
                double loudnessDb = 20.0 * Math.Log10(rms);
                return loudnessDb;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Calculates the perceived loudness asynchronously.
        /// </summary>
        public static Task<double?> CalculateLoudnessAsync(string filePath)
        {
            return Task.Run(() => CalculateLoudness(filePath));
        }

        /// <summary>
        /// Calculates the volume multiplier needed to adjust from the song's measured loudness
        /// to the target loudness level. Returns 1.0 if normalization is not applicable.
        /// </summary>
        public static float GetNormalizationMultiplier(double? songLoudness, double targetLoudness)
        {
            if (songLoudness == null) return 1.0f;

            double diff = targetLoudness - songLoudness.Value;
            // Convert dB difference to linear gain
            float multiplier = (float)Math.Pow(10.0, diff / 20.0);

            // Clamp to reasonable range to avoid extreme amplification or silence
            return Math.Clamp(multiplier, 0.1f, 3.0f);
        }
    }
}
