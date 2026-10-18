using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// "Import music" (files or a folder) for hosts whose library cannot be scanned in
/// place: the host picks with its native dialog, may copy the music into its own
/// storage, and the destination folder is registered and scanned.
/// </summary>
public class LibraryImportTests
{
    /// <summary>Points the app's writable folders at a fresh temp root for one test.</summary>
    private sealed class Sandbox : IDisposable
    {
        private readonly string _root = Path.Combine(TestSupport.NewTempDir(), "sandbox");
        private readonly string _dataRoot = AppPaths.DataRoot;
        private readonly string _download = AppPaths.DownloadPath;
        private readonly string _library = AppPaths.LibraryDataDir;
        private readonly string _appData = AppPaths.AppDataDir;
        private readonly string _localAppData = AppPaths.LocalAppDataDir;
        private readonly List<string> _savedScanFolders;

        public string Root => _root;
        public string Music => Path.Combine(_root, "music");

        public Sandbox()
        {
            // LibraryService re-reads the scan folders from the app config, which is
            // a process-wide singleton bound to the real config file: clear it here
            // and put it back in Dispose so the test stays hermetic.
            _savedScanFolders = new List<string>(ConfigService.Instance.Current?.LibraryScanFolders ?? new List<string>());
            ConfigService.Instance.Update(cfg => cfg.LibraryScanFolders = new List<string>());

            Directory.CreateDirectory(Music);
            AppPaths.DataRoot = _root;
            AppPaths.DownloadPath = Path.Combine(_root, "downloadedvideos");
            AppPaths.LibraryDataDir = Path.Combine(_root, "library");
            AppPaths.AppDataDir = _root;
            AppPaths.LocalAppDataDir = _root;
            Directory.CreateDirectory(AppPaths.LibraryDataDir);
        }

        public void Dispose()
        {
            ConfigService.Instance.Update(cfg => cfg.LibraryScanFolders = new List<string>(_savedScanFolders));
            AppPaths.DataRoot = _dataRoot;
            AppPaths.DownloadPath = _download;
            AppPaths.LibraryDataDir = _library;
            AppPaths.AppDataDir = _appData;
            AppPaths.LocalAppDataDir = _localAppData;
        }
    }

    private static void WriteWav(string path)
    {
        // Smallest valid PCM WAV TagLib can read (mono, 8 kHz, 0.05 s of silence).
        const int sampleRate = 8000;
        const int samples = 400;
        const int dataBytes = samples * 2;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);              // PCM
        writer.Write((short)1);              // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);        // byte rate
        writer.Write((short)2);              // block align
        writer.Write((short)16);             // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
    }

    private static (HeadlessHost Host, YoutubeFormInterop Interop, ConcurrentQueue<string> Responses) Create()
    {
        var host = new HeadlessHost(new List<Cookie>());
        var interop = new YoutubeFormInterop(host, host.YoutubeService);
        var responses = new ConcurrentQueue<string>();
        interop.SendMessage = responses.Enqueue;
        return (host, interop, responses);
    }

    private static async Task<JObject> Request(
        YoutubeFormInterop interop,
        ConcurrentQueue<string> responses,
        string method,
        string id = "i1",
        Action<JObject>? customize = null)
    {
        var msg = new JObject { ["type"] = "request", ["id"] = id, ["method"] = method };
        customize?.Invoke(msg);
        interop.HandleMessage(msg.ToString(Formatting.None));

        for (int i = 0; i < 600; i++)
        {
            while (responses.TryDequeue(out var json))
            {
                var obj = JObject.Parse(json);
                if (obj["type"]?.ToString() == "response" && obj["id"]?.ToString() == id)
                    return obj;
            }
            await Task.Delay(10);
        }

        throw new TimeoutException($"No response for '{method}'");
    }

    [Fact]
    public async Task ImportFolder_RegistersTheFolderAndScansIt()
    {
        using var sandbox = new Sandbox();
        WriteWav(Path.Combine(sandbox.Music, "tone.wav"));

        var (_, interop, responses) = Create();

        // Desktop behaviour: the folder is scanned where it already is.
        interop.PickFolderAsync = _ => Task.FromResult<string?>(sandbox.Music);

        var response = await Request(interop, responses, "importLibrary", customize: m => m["mode"] = "folder");
        var result = response["result"];

        Assert.NotNull(result);
        Assert.True(result!["success"]!.ToObject<bool>());
        Assert.Equal(sandbox.Music, result["folder"]!.ToString());
        Assert.Equal(1, result["filesFound"]!.ToObject<int>());
        Assert.Equal(1, result["songsAdded"]!.ToObject<int>());

        // And it is registered as a scan folder for later scans.
        Assert.Contains(sandbox.Music, interop.LibraryService.Data.Config.ScanFolders);
    }

    [Fact]
    public async Task ImportFiles_UsesTheFolderTheHostPrepares()
    {
        using var sandbox = new Sandbox();
        var picked = Path.Combine(sandbox.Root, "picked.wav");
        WriteWav(picked);

        var destination = Path.Combine(sandbox.Music, "imported");
        Directory.CreateDirectory(destination);
        WriteWav(Path.Combine(destination, "picked.wav"));

        var (_, interop, responses) = Create();
        interop.PickFilesAsync = _ => Task.FromResult<string[]?>(new[] { picked });

        // A mobile host copies the files and returns the copy destination.
        string? prepared = null;
        interop.PrepareImportFiles = files =>
        {
            prepared = destination;
            return Task.FromResult<string?>(destination);
        };

        var response = await Request(interop, responses, "importLibrary", customize: m => m["mode"] = "files");
        var result = response["result"];

        Assert.Equal(destination, prepared);
        Assert.True(result!["success"]!.ToObject<bool>());
        Assert.Equal(destination, result["folder"]!.ToString());
        Assert.Equal(1, result["songsAdded"]!.ToObject<int>());
    }

    [Fact]
    public async Task Import_CancelledPicking_ReportsCancelledAndScansNothing()
    {
        using var sandbox = new Sandbox();
        var (_, interop, responses) = Create();
        interop.PickFolderAsync = _ => Task.FromResult<string?>(null);

        var response = await Request(interop, responses, "importLibrary", customize: m => m["mode"] = "folder");
        var result = response["result"];

        Assert.True(result!["cancelled"]!.ToObject<bool>());
        Assert.Null(result["songsAdded"]);
    }

    [Fact]
    public void LibraryJson_StoresImportedSongsPortably_AndResolvesThemOnLoad()
    {
        using var sandbox = new Sandbox();
        var songPath = Path.Combine(sandbox.Music, "tone.wav");
        WriteWav(songPath);

        using (var library = new LibraryService())
        {
            library.Data.Config.ScanFolders.Add(sandbox.Music);
            library.ScanAsync().GetAwaiter().GetResult();
            Assert.Single(library.Data.Songs);
        }

        // On disk the imported path is portable, so a moved sandbox still works.
        var json = File.ReadAllText(Path.Combine(AppPaths.LibraryDataDir, "library.json"));
        var stored = JObject.Parse(json)["Songs"]!.First!["FilePath"]!.ToString();
        Assert.Equal("~/music/tone.wav", stored);

        // Loading resolves it against the current root.
        using var reloaded = new LibraryService();
        Assert.Equal(songPath, reloaded.Data.Songs.Single().FilePath);
    }

    [Fact]
    public async Task GetSettings_ReportsMobileCapabilities()
    {
        var (_, interop, responses) = Create();
        var response = await Request(interop, responses, "getSettings");
        var result = response["result"];

        Assert.NotNull(result);
        // The desktop is not mobile and can update itself in place.
        Assert.False(result!["isMobile"]!.ToObject<bool>());
        Assert.True(result["selfUpdateSupported"]!.ToObject<bool>());
    }
}
