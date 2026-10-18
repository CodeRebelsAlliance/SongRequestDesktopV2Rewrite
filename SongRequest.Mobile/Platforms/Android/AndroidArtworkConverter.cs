using System;
using System.IO;
using Android.Graphics;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Album-artwork downscaler using the platform's own image decoder.
///
/// The desktop shells out to FFmpeg for this; mobile has no FFmpeg, and without a
/// converter <see cref="ThumbnailStore"/> would skip any artwork larger than a few
/// kilobytes — i.e. most real album art. <c>BitmapFactory</c> does the same job in
/// process (and decodes with a subsample factor, so a 1500 px cover costs little).
/// </summary>
internal static class AndroidArtworkConverter
{
    public static bool Downscale(string source, string target)
    {
        try
        {
            if (!File.Exists(source)) return false;

            // Pass 1: read the dimensions only.
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(source, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return false;

            // Pass 2: decode at the smallest power-of-two subsample that still
            // leaves us at or above the target width.
            int sample = 1;
            while (bounds.OutWidth / (sample * 2) >= ThumbnailStore.MaxWidth) sample *= 2;

            var options = new BitmapFactory.Options { InSampleSize = sample };
            using var decoded = BitmapFactory.DecodeFile(source, options);
            if (decoded == null) return false;

            var width = Math.Min(ThumbnailStore.MaxWidth, decoded.Width);
            if (width <= 0) return false;

            var height = Math.Max(1, (int)Math.Round(decoded.Height * (width / (double)decoded.Width)));

            Bitmap? scaled = width < decoded.Width
                ? Bitmap.CreateScaledBitmap(decoded, width, height, filter: true)
                : null;

            var bitmap = scaled ?? decoded;

            using var output = File.Create(target);
            var ok = bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 82, output);
            output.Flush();

            scaled?.Recycle();
            return ok && new FileInfo(target).Length > 0;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"artwork conversion failed: {ex.Message}");
            return false;
        }
    }
}
