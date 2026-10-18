using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Decodes short soundboard clips fully into memory so button presses play with
/// no decoder start-up latency, and reuses the decoded PCM across repeated
/// presses and duplicate buttons. Long files are not cached (they stream
/// instead). The cache is bounded and evicts least-recently-used clips.
/// </summary>
internal sealed class AudioClipCache
{
    private const long MaxCacheBytes = 256L * 1024 * 1024;
    private const double MaxClipSeconds = 60.0;

    private readonly object _sync = new();
    private readonly Dictionary<string, AudioClip> _clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;

    /// <summary>
    /// Returns a playback source for the file: an in-memory source when the clip
    /// is short (and cached), otherwise a streaming source.
    /// </summary>
    public IAudioSource CreateSource(string path)
    {
        lock (_sync)
        {
            if (_clips.TryGetValue(path, out var cached))
            {
                Touch(path);
                return new InMemoryAudioSource(cached);
            }
        }

        var source = AudioSourceFactory.Open(path);
        if (source.TotalTime > TimeSpan.FromSeconds(MaxClipSeconds))
            return source; // too long to cache — stream it

        AudioClip clip;
        try { clip = Decode(source); }
        finally { source.Dispose(); }

        lock (_sync)
        {
            if (_clips.TryGetValue(path, out var existing))
            {
                Touch(path);
                clip = existing;
            }
            else
            {
                _clips[path] = clip;
                _bytes += clip.ByteSize;
                _lru.AddLast(path);
                Evict();
            }
        }

        return new InMemoryAudioSource(clip);
    }

    /// <summary>Warms the cache for a clip in the background (used at load / on assignment).</summary>
    public void Preload(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        lock (_sync)
        {
            if (_clips.ContainsKey(path) || !_loading.Add(path)) return;
        }

        _ = Task.Run(() =>
        {
            try { CreateSource(path).Dispose(); }
            catch { /* ignore preload failures; playback will surface them */ }
            finally { lock (_sync) _loading.Remove(path); }
        });
    }

    private static AudioClip Decode(IAudioSource source)
    {
        int channels = source.WaveFormat.Channels <= 0 ? 2 : source.WaveFormat.Channels;
        int rate = source.WaveFormat.SampleRate <= 0 ? 44100 : source.WaveFormat.SampleRate;

        long estimated = (long)(source.TotalTime.TotalSeconds * rate * channels);
        int initial = (int)Math.Clamp(estimated, 4096, 16 * 1024 * 1024);

        var data = new float[initial];
        int count = 0;

        var buffer = new float[Math.Max(rate * channels, 8192)];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (count + read > data.Length)
                Array.Resize(ref data, Math.Max(data.Length * 2, count + read));
            Array.Copy(buffer, 0, data, count, read);
            count += read;
        }

        if (count != data.Length) Array.Resize(ref data, count);
        return new AudioClip(data, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
    }

    private void Touch(string path)
    {
        _lru.Remove(path);
        _lru.AddLast(path);
    }

    private void Evict()
    {
        while (_bytes > MaxCacheBytes && _lru.First is { } first)
        {
            var key = first.Value;
            _lru.RemoveFirst();
            if (_clips.Remove(key, out var clip)) _bytes -= clip.ByteSize;
        }
    }
}
