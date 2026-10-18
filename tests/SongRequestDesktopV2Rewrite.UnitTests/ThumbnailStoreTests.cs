using System;
using System.IO;
using System.Linq;
using System.Threading;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// Artwork preparation: the UI must never wait for dozens of image conversions, and the
/// same artwork must only be converted once (the raw library artwork is ~1 MB per file,
/// which used to be base64-inlined into every feed/queue update).
/// </summary>
public class ThumbnailStoreTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sr-thumbs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class Harness : IDisposable
    {
        public string Dir { get; }
        public string CacheDir { get; }
        public ThumbnailStore Store { get; }
        public int Conversions;

        public Harness(int sourceBytes = 200_000, bool converterAvailable = true, bool workers = false)
        {
            Dir = NewTempDir();
            CacheDir = Path.Combine(Dir, "cache");
            Store = new ThumbnailStore
            {
                CacheDirectory = CacheDir,
                BackgroundWorkersEnabled = workers,
                ConverterAvailable = () => converterAvailable,
                Downscale = (source, target) =>
                {
                    Interlocked.Increment(ref Conversions);
                    File.WriteAllBytes(target, new byte[512]);   // "small" result
                    return true;
                },
            };
        }

        public string Source(string name, int bytes = 200_000)
        {
            var path = Path.Combine(Dir, name);
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ConvertsOnce_ThenServesFromMemory()
    {
        using var h = new Harness();
        var art = h.Source("a.jpg");

        h.Store.BeginRequest();
        var first = h.Store.GetDataUrl(art);
        h.Store.BeginRequest();
        var second = h.Store.GetDataUrl(art);

        Assert.NotNull(first);
        Assert.StartsWith("data:image/jpeg;base64,", first);
        Assert.Equal(first, second);
        Assert.Equal(1, h.Conversions);                       // converted exactly once
        Assert.Single(Directory.GetFiles(h.CacheDir));        // cached on disk
    }

    [Fact]
    public void OneCall_OnlyConvertsUpToTheBudget_AndQueuesTheRest()
    {
        using var h = new Harness();   // workers off: observe the per-call budget alone
        var art1 = h.Source("1.jpg");
        var art2 = h.Source("2.jpg");
        var art3 = h.Source("3.jpg");

        h.Store.BeginRequest(1);
        var results = new[] { h.Store.GetDataUrl(art1), h.Store.GetDataUrl(art2), h.Store.GetDataUrl(art3) };

        Assert.Equal(1, results.Count(r => r != null));        // only one inline conversion
        Assert.Equal(1, h.Conversions);
        Assert.Equal(2, h.Store.PendingCount);                 // the rest waits for a worker
    }

    [Fact]
    public void FeedStyleCall_NeverConvertsInline()
    {
        using var h = new Harness();   // workers off: nothing may convert inside the call
        var art = h.Source("a.jpg");

        h.Store.BeginRequest(0);
        var inline = h.Store.GetDataUrl(art);

        Assert.Null(inline);                                   // "not ready yet" → placeholder
        Assert.Equal(0, h.Conversions);                        // nothing converted inside the call
        Assert.True(h.Store.PendingCount >= 1);                // but it was queued
    }

    [Fact]
    public void SmallArtwork_IsInlinedWithoutConversion()
    {
        using var h = new Harness(converterAvailable: false);
        var art = h.Source("small.jpg", 5_000);

        h.Store.BeginRequest();
        var url = h.Store.GetDataUrl(art);

        Assert.NotNull(url);
        Assert.Equal(0, h.Conversions);
    }

    [Fact]
    public void WithoutAConverter_BigArtwork_IsSkippedInsteadOfInlined()
    {
        using var h = new Harness(converterAvailable: false);
        var art = h.Source("huge.jpg", 500_000);

        h.Store.BeginRequest();
        Assert.Null(h.Store.GetDataUrl(art));
    }

    [Fact]
    public void MissingArtwork_AndEmptyPaths_ReturnNull()
    {
        using var h = new Harness();

        h.Store.BeginRequest();
        Assert.Null(h.Store.GetDataUrl(null));
        Assert.Null(h.Store.GetDataUrl(""));
        Assert.Null(h.Store.GetDataUrl(Path.Combine(h.Dir, "nope.jpg")));
        Assert.Null(h.Store.GetDataUrl(new byte[0], "song"));
    }

    [Fact]
    public void EmbeddedArtwork_IsConvertedFromBytes()
    {
        using var h = new Harness();
        var bytes = Enumerable.Repeat((byte)7, 300_000).ToArray();

        h.Store.BeginRequest();
        var url = h.Store.GetDataUrl(bytes, "/music/song.mp3");

        Assert.NotNull(url);
        Assert.Equal(1, h.Conversions);
    }

    [Fact]
    public void Prewarm_QueuesArtworkInTheBackground()
    {
        using var h = new Harness(workers: true);
        var a = h.Source("a.jpg");
        var b = h.Source("b.jpg");

        h.Store.Prewarm(new[] { a, b });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && h.Store.GetDataUrl(a) == null) Thread.Sleep(50);

        Assert.NotNull(h.Store.GetDataUrl(a));
        Assert.NotNull(h.Store.GetDataUrl(b));
    }

    [Fact]
    public void BrokenConverter_DoesNotThrow_AndReportsNotReady()
    {
        using var h = new Harness();
        h.Store.Downscale = (_, _) => false;
        var art = h.Source("bad.jpg");

        h.Store.BeginRequest();
        Assert.Null(h.Store.GetDataUrl(art));
    }
}
