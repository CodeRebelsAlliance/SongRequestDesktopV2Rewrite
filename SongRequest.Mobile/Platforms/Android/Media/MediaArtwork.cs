using System;
using System.Collections.Generic;
using System.IO;
using Android.Graphics;

namespace SongRequest.Mobile;

/// <summary>
/// Decodes artwork files into small bitmaps for the media session and the
/// Android Auto media browser. The car UI cannot read <c>file://</c> artwork
/// URIs, so icons are embedded as bitmaps instead.
/// </summary>
internal static class MediaArtwork
{
    private const int MaxPixels = 320;
    private const int CacheLimit = 160;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();

    public static Bitmap? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return null;
            key = path + "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
        }
        catch { return null; }

        lock (Sync)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        Bitmap? bitmap = null;
        try
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(path, bounds);
            var options = new BitmapFactory.Options
            {
                InSampleSize = SampleSize(bounds.OutWidth, bounds.OutHeight),
            };
            bitmap = BitmapFactory.DecodeFile(path, options);
        }
        catch { }

        lock (Sync)
        {
            if (Cache.ContainsKey(key)) return Cache[key];
            Cache[key] = bitmap;
            Order.Enqueue(key);
            while (Order.Count > CacheLimit)
                Cache.Remove(Order.Dequeue());
        }

        return bitmap;
    }

    private static int SampleSize(int width, int height)
    {
        int sample = 1;
        while (width / sample > MaxPixels * 2 || height / sample > MaxPixels * 2)
            sample *= 2;
        return sample;
    }
}
