using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class MetadataInteropTests
{
    private static (YoutubeFormInterop Interop, ConcurrentQueue<string> Responses) Create()
    {
        var host = new HeadlessHost(new List<Cookie>());
        var interop = new YoutubeFormInterop(host, host.YoutubeService);
        var responses = new ConcurrentQueue<string>();
        interop.SendMessage = responses.Enqueue;
        return (interop, responses);
    }

    private static async Task<JObject> Request(
        YoutubeFormInterop interop,
        ConcurrentQueue<string> responses,
        string method,
        Action<JObject>? customize = null)
    {
        var msg = new JObject { ["type"] = "request", ["id"] = "m1", ["method"] = method };
        customize?.Invoke(msg);
        interop.HandleMessage(msg.ToString(Formatting.None));

        for (int i = 0; i < 300; i++)
        {
            while (responses.TryDequeue(out var json))
            {
                var obj = JObject.Parse(json);
                if (obj["type"]?.ToString() == "response" && obj["id"]?.ToString() == "m1")
                    return obj;
            }
            await Task.Delay(10);
        }

        throw new TimeoutException("No response for " + method);
    }

    private static string ImportSample(YoutubeFormInterop interop)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "announcement.mp3");
        var folder = TestSupport.NewTempDir("metadata-interop");
        var path = Path.Combine(folder, "song.mp3");
        File.Copy(source, path);
        // The library rejects hash duplicates across tests; make each copy unique
        // with a small trailing marker that audio decoders ignore.
        File.AppendAllText(path, "\nSR-" + Guid.NewGuid().ToString("N"));
        var song = interop.LibraryService.ImportDownloadedSong(path);
        Assert.NotNull(song);
        return song!.Id;
    }

    [Fact]
    public async Task GetSongMetadata_ReturnsAllEditableFields()
    {
        var (interop, responses) = Create();
        var songId = ImportSample(interop);

        var response = await Request(interop, responses, "getSongMetadata", m => m["songId"] = songId);
        var result = response["result"]!;

        Assert.Equal(songId, result["songId"]?.ToString());
        Assert.True(result["fileExists"]?.ToObject<bool>());
        Assert.True(result["canWriteToFile"]?.ToObject<bool>());
        Assert.NotNull(result["providers"]);
        Assert.True(result["providers"]!["musicBrainz"]?.ToObject<bool>());
    }

    [Fact]
    public async Task OpenEditor_RemembersTheSelectedSong()
    {
        var (interop, responses) = Create();
        var songId = ImportSample(interop);

        await Request(interop, responses, "openMetadataEditor", m => m["songId"] = songId);
        var response = await Request(interop, responses, "getSongMetadata");
        var result = response["result"]!;

        Assert.Equal(songId, result["songId"]?.ToString());
    }

    [Fact]
    public async Task SaveSongMetadata_WritesFieldsAndLyrics()
    {
        var (interop, responses) = Create();
        var songId = ImportSample(interop);

        var response = await Request(interop, responses, "saveSongMetadata", m =>
        {
            m["songId"] = songId;
            m["title"] = "Edited title";
            m["artist"] = "Edited artist";
            m["albumArtist"] = "Album artist";
            m["album"] = "Edited album";
            m["genre"] = "Rock";
            m["year"] = 2025;
            m["track"] = 2;
            m["disc"] = 1;
            m["bpm"] = 120;
            m["comment"] = "comment";
            m["composer"] = "composer";
            m["lyricist"] = "lyricist";
            m["conductor"] = "conductor";
            m["publisher"] = "publisher";
            m["copyright"] = "copyright";
            m["isrc"] = "DEABC2500001";
            m["grouping"] = "group";
            m["lyrics"] = "[00:01.00]Line one\n[00:02.00]Line two";
        });

        var result = response["result"]!;
        Assert.True(result["success"]?.ToObject<bool>());
        Assert.Equal("file", result["target"]?.ToString());
        Assert.False(result["storedInLibrary"]?.ToObject<bool>());
        Assert.Equal("Edited title", result["metadata"]!["title"]?.ToString());
        Assert.Equal("lyricist", result["metadata"]!["lyricist"]?.ToString());
        Assert.True(result["metadata"]!["lyricsAreSynced"]?.ToObject<bool>());

        var song = interop.LibraryService.Data.FindSong(songId)!;
        Assert.Equal("Edited title", song.Title);
        Assert.Equal("Edited artist", song.Artist);
        Assert.Equal("Edited album", song.Album);
        Assert.False(song.MetadataStoredInLibrary);
    }

    [Fact]
    public async Task CloseEditor_IsHarmlessWithoutAHostWindow()
    {
        var (interop, responses) = Create();
        var response = await Request(interop, responses, "closeMetadataEditor");
        Assert.True(response["result"]!["success"]?.ToObject<bool>());
    }
}
