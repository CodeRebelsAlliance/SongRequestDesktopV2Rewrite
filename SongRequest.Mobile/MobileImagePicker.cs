using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace SongRequest.Mobile;

/// <summary>
/// Cross-platform image picker for the metadata editor. Android SAF can return a
/// content URI without a readable FullPath, so the stream is copied into the
/// app cache when needed. Used on Android and iOS alike.
/// </summary>
internal static class MobileImagePicker
{
    private static readonly FilePickerFileType ImageTypes = new(
        new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android] = new[] { "image/*" },
            [DevicePlatform.iOS] = new[] { "public.image" },
            [DevicePlatform.MacCatalyst] = new[] { "public.image" },
            [DevicePlatform.WinUI] = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" },
        });

    public static async Task<string?> PickAsync(string title)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = title,
                FileTypes = ImageTypes,
            }).ConfigureAwait(true);

            if (result == null) return null;
            if (!string.IsNullOrWhiteSpace(result.FullPath) && File.Exists(result.FullPath))
                return result.FullPath;

            var directory = Path.Combine(FileSystem.CacheDirectory, "artwork-picks");
            Directory.CreateDirectory(directory);
            var name = Path.GetFileName(result.FileName ?? "cover.jpg");
            if (string.IsNullOrWhiteSpace(name)) name = "cover.jpg";
            var extension = Path.GetExtension(name);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".jpg";

            var destination = Path.Combine(
                directory,
                Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..6] + extension);

            using var input = await result.OpenReadAsync().ConfigureAwait(false);
            using var output = File.Create(destination);
            await input.CopyToAsync(output).ConfigureAwait(false);
            return destination;
        }
        catch
        {
            return null;
        }
    }
}
