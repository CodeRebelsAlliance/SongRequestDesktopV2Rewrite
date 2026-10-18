using System;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Sample provider that captures audio samples while passing them through
    /// </summary>
    public class CapturingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private bool _hasLoggedFormat = false;
        private int _totalReads = 0;

        public event EventHandler<float[]>? SamplesCaptured;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public CapturingSampleProvider(ISampleProvider source)
        {
            _source = source;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);

            _totalReads++;

            if (!_hasLoggedFormat && samplesRead > 0)
            {
                System.Diagnostics.Debug.WriteLine($"🎙️ CapturingSampleProvider format: {WaveFormat}");
                System.Diagnostics.Debug.WriteLine($"   Encoding: {WaveFormat.Encoding}, {WaveFormat.BitsPerSample}bit, {WaveFormat.SampleRate}Hz, {WaveFormat.Channels}ch");
                _hasLoggedFormat = true;
            }

            // Log actual audio data (not just silence)
            if (samplesRead >= 4 && _totalReads % 1000 == 0) // Log every 1000 reads
            {
                System.Diagnostics.Debug.WriteLine($"🎙️ Read #{_totalReads}: First samples from buffer: [{buffer[offset].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {buffer[offset + 1].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {buffer[offset + 2].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {buffer[offset + 3].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}]");
            }

            if (samplesRead > 0 && SamplesCaptured != null)
            {
                // Make a defensive copy IMMEDIATELY before any async work
                var samples = new float[samplesRead];

                // Use Buffer.BlockCopy for performance and correctness
                Buffer.BlockCopy(buffer, offset * sizeof(float), samples, 0, samplesRead * sizeof(float));

                SamplesCaptured?.Invoke(this, samples);
            }

            return samplesRead;
        }
    }
}
