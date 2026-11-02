using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Backend contract for local-AI vocal separation. Implementations return a
/// stereo instrumental WAV that the existing karaoke pipeline can play.
/// </summary>
public interface IAiVocalSeparator
{
    bool IsSupported { get; }
    string BackendName { get; }

    void SeparateInstrumental(
        string inputPath,
        string outputPath,
        string modelPath,
        int threads,
        Action<double, string>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Selects the local AI vocal-separation backend. sherpa-onnx (UVR/MDX-Net) is
/// registered by default; hosts and tests can replace it through
/// <see cref="Register"/>.
/// </summary>
public static class AiVocalSeparationFactory
{
    private static IAiVocalSeparator? _backend = new SherpaAiVocalSeparator();

    public static void Register(IAiVocalSeparator? backend) => _backend = backend;

    public static bool IsSupported => _backend?.IsSupported == true;

    public static string BackendName => _backend?.BackendName ?? "none";

    public static IAiVocalSeparator? TryCreate()
        => _backend is { IsSupported: true } ? _backend : null;
}

/// <summary>Version metadata for AI-separated karaoke files.</summary>
public static class AiVocalRemover
{
    /// <summary>Bump when the AI preprocessing/postprocessing changes.</summary>
    public const string AlgorithmVersion = "3.0";

    /// <summary>The stored karaoke version for a specific model.</summary>
    public static string VersionFor(string modelId) =>
        AlgorithmVersion + "-ai:" + modelId;
}

/// <summary>
/// Runs UVR/MDX-Net vocal separation on-device through sherpa-onnx.
///
/// The source is decoded/resampled to 44.1 kHz stereo in managed code and passed
/// to the local ONNX runtime. Stem 1 is the non-vocal accompaniment, which is
/// written as a 16-bit WAV for the player. No audio leaves the device.
/// </summary>
public sealed class SherpaAiVocalSeparator : IAiVocalSeparator
{
    private static readonly Lazy<bool> Support = new(ProbeSupport,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsSupported => Support.Value;

    public string BackendName => "sherpa-onnx · UVR/MDX-Net";

    public void SeparateInstrumental(
        string inputPath,
        string outputPath,
        string modelPath,
        int threads,
        Action<double, string>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "The local AI vocal separation runtime is not available on this platform.");
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Song file not found.", inputPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("AI vocal model not found. Download it in Settings.", modelPath);

        cancellationToken.ThrowIfCancellationRequested();

        string tempPath = outputPath + ".ai.part";
        try
        {
            progress?.Invoke(0.02, "Decoding audio");
            var channels = ReadStereoPcm(inputPath, progress, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(0.18, "Loading AI model");
            var config = new OfflineSourceSeparationConfig();
            config.Model.Uvr.Model = modelPath;
            config.Model.NumThreads = Math.Clamp(threads, 1, 8);
            config.Model.Debug = 0;
            config.Model.Provider = "cpu";

            using var separator = new OfflineSourceSeparation(config);
            progress?.Invoke(0.22, "Separating vocals with the local AI model");
            using var output = separator.Process(channels, 44100);

            cancellationToken.ThrowIfCancellationRequested();
            if (output.NumStems < 2)
                throw new InvalidOperationException("The AI model did not return vocal and instrumental stems.");

            progress?.Invoke(0.90, "Writing instrumental stem");
            if (File.Exists(tempPath)) File.Delete(tempPath);
            // Stem 0 is vocals, stem 1 is the accompaniment/instrumental.
            if (!output.SaveStemToWaveFile(1, tempPath))
                throw new IOException("Could not write the AI instrumental stem.");

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tempPath, outputPath);
            progress?.Invoke(1.0, "AI karaoke version ready");
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static float[][] ReadStereoPcm(
        string inputPath,
        Action<double, string>? progress,
        CancellationToken cancellationToken)
    {
        using var source = AudioSourceFactory.Open(inputPath);
        ISampleProvider provider = source.ToSampleProvider();

        // The UVR models are trained at 44.1 kHz. Keep the original channel
        // layout until after resampling so the source length stays correct.
        if (provider.WaveFormat.SampleRate != 44100)
            provider = new WdlResamplingSampleProvider(provider, 44100);

        int channels = Math.Max(1, provider.WaveFormat.Channels);
        if (channels > 2) channels = 2;

        int capacity = 44100;
        try
        {
            double seconds = source.TotalTime.TotalSeconds;
            if (seconds > 0 && seconds < 60 * 60)
                capacity = Math.Max(capacity, (int)Math.Ceiling(seconds * 44100) + 44100);
        }
        catch { }

        var left = new float[capacity];
        var right = new float[capacity];
        int written = 0;
        var buffer = new float[44100 * Math.Max(1, provider.WaveFormat.Channels)];

        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frames = read / Math.Max(1, provider.WaveFormat.Channels);
            if (frames <= 0) break;

            if (written + frames > left.Length)
            {
                int newCapacity = Math.Max(left.Length * 2, written + frames);
                Array.Resize(ref left, newCapacity);
                Array.Resize(ref right, newCapacity);
            }

            for (int i = 0; i < frames; i++)
            {
                int index = i * Math.Max(1, provider.WaveFormat.Channels);
                float l = buffer[index];
                left[written + i] = l;
                right[written + i] = provider.WaveFormat.Channels > 1
                    ? buffer[index + 1]
                    : l;
            }
            written += frames;

            if (written % (44100 * 5) < frames)
            {
                double duration = Math.Max(1, source.TotalTime.TotalSeconds);
                progress?.Invoke(Math.Clamp(written / 44100.0 / duration, 0, 1) * 0.16, "Decoding audio");
            }
        }

        if (written == 0)
            throw new InvalidOperationException("The song contained no decodable audio.");

        Array.Resize(ref left, written);
        Array.Resize(ref right, written);
        return new[] { left, right };
    }

    private static bool ProbeSupport()
    {
        // iOS currently ships no sherpa-onnx native runtime in this package.
        if (OperatingSystem.IsIOS() || OperatingSystem.IsBrowser()) return false;

        // Android packages native libraries inside the APK's lib/<abi> folders;
        // they are resolved by the runtime rather than AppContext.BaseDirectory.
        if (OperatingSystem.IsAndroid()) return true;

        try
        {
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm => "arm",
                _ => "x64"
            };
            string rid = OperatingSystem.IsWindows() ? "win-" + arch
                : OperatingSystem.IsMacOS() ? "osx-" + arch
                : "linux-" + arch;
            string file = OperatingSystem.IsWindows() ? "sherpa-onnx-c-api.dll"
                : OperatingSystem.IsMacOS() ? "libsherpa-onnx-c-api.dylib"
                : "libsherpa-onnx-c-api.so";

            string baseDir = AppContext.BaseDirectory;
            return File.Exists(Path.Combine(baseDir, file)) ||
                   File.Exists(Path.Combine(baseDir, "runtimes", rid, "native", file));
        }
        catch
        {
            return false;
        }
    }
}
