using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SongRequestDesktopV2Rewrite.IntegrationTests;

/// <summary>
/// Shared, lazily generated audio fixtures and environment setup for integration
/// tests. OpenAL runs headless (null driver) and FFmpeg fixtures are produced at
/// test time so nothing binary is committed.
/// </summary>
internal static class Fixtures
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Run OpenAL Soft with no real output device (belt-and-braces: the same
        // setting is also applied by tests/coverage.runsettings, which the test
        // host applies before this assembly is even loaded).
        Environment.SetEnvironmentVariable("ALSOFT_DRIVERS", "null");

        // Keep per-user config/cookies off the real machine during tests.
        AppPaths.AppDataDir = NewTempDir();
    }

    public static string FfmpegExe { get; } = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    public static bool FfmpegAvailable { get; } = DetectFfmpeg();

    private static readonly Lazy<bool> _openAlAvailable = new(DetectOpenAl);

    /// <summary>
    /// Whether OpenAL can actually open a device. With the null driver configured
    /// this is true even without a sound card, but on a runner where OpenAL cannot
    /// initialise at all the audio integration tests skip instead of failing.
    /// </summary>
    public static bool OpenAlAvailable => _openAlAvailable.Value;

    private static unsafe bool DetectOpenAl()
    {
        try
        {
            var alc = Silk.NET.OpenAL.ALContext.GetApi();
            var device = alc.OpenDevice(null);
            if (device == null) return false;

            try { return true; }
            finally { try { alc.CloseDevice(device); } catch { } }
        }
        catch
        {
            return false;
        }
    }

    private static readonly Lazy<AudioFixtures> _fixtures = new(Create);

    public static AudioFixtures Audio => _fixtures.Value;

    /// <summary>Deletes the persisted library so a fresh service starts empty.</summary>
    public static void ResetLibrary()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "library", "library.json");
        if (File.Exists(path)) File.Delete(path);
    }

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sr-integration-tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool DetectFfmpeg()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(FfmpegExe, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) return false;
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static AudioFixtures Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sr-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var set = new AudioFixtures
        {
            Dir = dir,
            Wav = Path.Combine(dir, "tone.wav"),
            Mp3 = Path.Combine(dir, "tone.mp3"),
            M4a = Path.Combine(dir, "tone.m4a"),
            Flac = Path.Combine(dir, "tone.flac"),
            OpusMp3 = Path.Combine(dir, "gap.mp3"),   // Opus-in-WebM, mislabeled as .mp3
            LongOpus = Path.Combine(dir, "long.opus"),
        };

        Run($"-y -f lavfi -i \"aevalsrc=0.9*sin(2*PI*440*t):s=44100:d=3\" -ac 2 \"{set.Wav}\"");
        Run($"-y -f lavfi -i \"aevalsrc=0.9*sin(2*PI*440*t):s=44100:d=3\" -ac 2 -c:a libmp3lame \"{set.Mp3}\"");
        Run($"-y -f lavfi -i \"aevalsrc=0.9*sin(2*PI*440*t):s=48000:d=3\" -ac 2 -c:a aac \"{set.M4a}\"");
        Run($"-y -f lavfi -i \"aevalsrc=0.9*sin(2*PI*440*t):s=44100:d=3\" -ac 2 -c:a flac \"{set.Flac}\"");
        Run($"-y -f lavfi -i \"aevalsrc=0.9*sin(2*PI*440*t):s=48000:d=2\" -af \"adelay=2000|2000\" -ac 2 -c:a libopus -f webm \"{set.OpusMp3}\"");
        Run($"-y -f lavfi -i \"anullsrc=r=48000:cl=stereo\" -t 61 -c:a libopus \"{set.LongOpus}\"");
        return set;
    }

    private static void Run(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(FfmpegExe, args)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed: {args}\n{error}");
    }
}

internal sealed class AudioFixtures
{
    public string Dir { get; set; } = "";
    public string Wav { get; set; } = "";
    public string Mp3 { get; set; } = "";
    public string M4a { get; set; } = "";
    public string Flac { get; set; } = "";
    public string OpusMp3 { get; set; } = "";
    public string LongOpus { get; set; } = "";
}
