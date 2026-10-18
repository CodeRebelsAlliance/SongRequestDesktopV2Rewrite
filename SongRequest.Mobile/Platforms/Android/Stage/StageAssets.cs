using System;
using System.IO;
using Android.Content.Res;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Copies the bundled web UI out of the app package so the stage server can serve
/// it (assets inside an APK are not readable as files).
/// </summary>
internal static class StageAssets
{
    /// <summary>Where the served copy lives.</summary>
    public static string WebRoot => Path.Combine(AppPaths.DataRoot, "stage-web");

    /// <summary>Extracts the assets once per app version (cheap: a few hundred KB).</summary>
    public static bool EnsureExtracted()
    {
        try
        {
            var root = WebRoot;
            var marker = Path.Combine(root, "presentation.html");

            var assets = Android.App.Application.Context.Assets;
            if (assets == null) return false;

            // Re-extract when the marker is missing, or when the packaged page is newer.
            if (File.Exists(marker) && !IsStale(assets, marker)) return true;

            Directory.CreateDirectory(root);
            CopyDirectory(assets, "wwwroot", root);
            Android.Util.Log.Debug("SongRequest", $"stage assets extracted to {root}");
            return File.Exists(marker);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"stage asset extraction failed: {ex.Message}");
            return false;
        }
    }

    private static bool IsStale(AssetManager assets, string marker)
    {
        try
        {
            // The packaged asset's length is a decent "did the app update" signal.
            using var stream = assets.Open("wwwroot/presentation.html");
            return stream.Length != new FileInfo(marker).Length;
        }
        catch
        {
            return true;
        }
    }

    private static void CopyDirectory(AssetManager assets, string assetPath, string target)
    {
        var children = assets.List(assetPath) ?? Array.Empty<string>();
        if (children.Length == 0)
        {
            CopyFile(assets, assetPath, target);
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var child in children)
        {
            var childAsset = assetPath + "/" + child;
            var childTarget = Path.Combine(target, child);

            var grandChildren = assets.List(childAsset) ?? Array.Empty<string>();
            if (grandChildren.Length > 0) CopyDirectory(assets, childAsset, childTarget);
            else CopyFile(assets, childAsset, childTarget);
        }
    }

    private static void CopyFile(AssetManager assets, string assetPath, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = assets.Open(assetPath);
            using var destination = File.Create(target);
            source.CopyTo(destination);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"stage asset '{assetPath}' skipped: {ex.Message}");
        }
    }
}
