using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.Content;
using Android.Provider;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using SongRequestDesktopV2Rewrite;

namespace SongRequest.Mobile;

/// <summary>
/// Mobile library import: the platform pickers plus the copy into the app's own
/// storage.
///
/// Android cannot scan a Storage Access Framework tree in place — the scanner
/// walks real directories — so the picked music is copied into
/// <c>&lt;data root&gt;/music</c> and that folder becomes the scan folder. It is
/// also the only location the app is guaranteed to be able to read on every OS
/// version, and it lives under <see cref="AppPaths.DataRoot"/>, so the library
/// keeps working when the sandbox moves (see <see cref="AppPaths.ToPortablePath"/>).
/// </summary>
internal static class AndroidFilePickers
{
    private static readonly string[] AudioExtensions =
        { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".wma" };

    /// <summary>
    /// Explicit audio filter. The parameterless picker defaults to every file on
    /// some Android providers, and some DocumentsUI implementations then hide
    /// audio or hand back an unusable pick. `audio/*` is understood everywhere.
    /// </summary>
    private static readonly FilePickerFileType AudioFileTypes = new(
        new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android] = new[] { "audio/*" },
            [DevicePlatform.iOS] = new[] { "public.audio", "public.mp3", "com.microsoft.waveform-audio" },
            [DevicePlatform.MacCatalyst] = new[] { "public.audio", "public.mp3", "com.microsoft.waveform-audio" },
            [DevicePlatform.WinUI] = AudioExtensions,
        });

    /// <summary>Where imported music lands (and the folder that gets scanned).</summary>
    public static string ImportDirectory => Path.Combine(AppPaths.DataRoot, "music");

    // ── File picker (multi-select) ──────────────────────────────────────────

    public static async Task<string[]?> PickFilesAsync(string title)
    {
        try
        {
            var results = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = title,
                FileTypes = AudioFileTypes,
            }).ConfigureAwait(true);

            var paths = results?
                .Select(r => r.FullPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            return paths is { Length: > 0 } ? paths : null;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"file picker failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Picks exactly one audio file for the soundboard. Unlike the multi-file
    /// library picker, it materialises a provider result with no usable
    /// <c>FullPath</c> straight into the cache, so the caller always gets a
    /// readable path.
    /// </summary>
    public static async Task<string?> PickSoundFileAsync(string title)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = title,
                FileTypes = AudioFileTypes,
            }).ConfigureAwait(true);

            if (result == null) return null;

            if (!string.IsNullOrWhiteSpace(result.FullPath) && File.Exists(result.FullPath))
                return result.FullPath;

            if (!string.IsNullOrWhiteSpace(result.FullPath) &&
                result.FullPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
                return result.FullPath;

            // Some providers return an empty FullPath but a readable stream.
            var name = Sanitize(result.FileName ?? "sound.mp3");
            var extension = Path.GetExtension(name);
            if (string.IsNullOrEmpty(extension)) extension = ".mp3";

            var directory = Path.Combine(
                Android.App.Application.Context.CacheDir?.AbsolutePath ?? AppPaths.DataRoot,
                "soundboard-picks");
            Directory.CreateDirectory(directory);

            var destination = Path.Combine(
                directory,
                Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..6] + extension);

            using var input = await result.OpenReadAsync().ConfigureAwait(false);
            using var output = File.Create(destination);
            await input.CopyToAsync(output).ConfigureAwait(false);
            return destination;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"sound picker failed: {ex.Message}");
            return null;
        }
    }

    // ── Folder picker (SAF) ─────────────────────────────────────────────────

    /// <summary>Shows Android's folder picker; returns a tree URI or null.</summary>
    public static async Task<string?> PickFolderAsync(string title)
    {
        try
        {
            return await MainActivity.PickFolderAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"folder picker failed: {ex.Message}");
            return null;
        }
    }

    // ── Copy into the sandbox ───────────────────────────────────────────────

    /// <summary>Copies individually picked files into the import folder.</summary>
    public static Task<string?> PrepareFilesAsync(string[] files)
        => Task.Run(() =>
        {
            try
            {
                var destination = ImportDirectory;
                Directory.CreateDirectory(destination);

                foreach (var file in files)
                    CopyFile(file, destination);

                Android.Util.Log.Debug("SongRequest", $"imported {files.Length} file(s) into {destination}");
                return (string?)destination;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Debug("SongRequest", $"import failed: {ex.Message}");
                return null;
            }
        });

    /// <summary>
    /// Materialises a single picked file (a <c>content://</c> URI from
    /// <see cref="PickFilesAsync"/>) into a readable local file with its real name,
    /// for consumers like the soundboard editor that need a path.
    /// </summary>
    public static Task<string?> PrepareSoundFileAsync(string picked)
        => Task.Run(() =>
        {
            try
            {
                if (File.Exists(picked)) return (string?)picked;
                if (string.IsNullOrWhiteSpace(picked) ||
                    !picked.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
                    return null;

                var resolver = Android.App.Application.Context.ContentResolver;
                var uri = Android.Net.Uri.Parse(picked);
                if (resolver == null || uri == null) return null;

                var name = Sanitize(QueryDocumentDisplayName(resolver, uri) ??
                                     uri.LastPathSegment ?? "sound");
                var extension = Path.GetExtension(name);
                if (string.IsNullOrEmpty(extension)) extension = ".mp3";

                var directory = Path.Combine(
                    Android.App.Application.Context.CacheDir?.AbsolutePath ?? AppPaths.DataRoot,
                    "soundboard-picks");
                Directory.CreateDirectory(directory);

                var destination = Path.Combine(
                    directory,
                    Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..6] + extension);

                using (var input = resolver.OpenInputStream(uri))
                {
                    if (input == null) return null;
                    using var output = File.Create(destination);
                    input.CopyTo(output);
                }

                return (string?)destination;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Debug("SongRequest", $"sound file import failed: {ex.Message}");
                return null;
            }
        });

    /// <summary>Display name of a single SAF document (not a tree).</summary>
    private static string? QueryDocumentDisplayName(ContentResolver resolver, Android.Net.Uri uri)
    {
        try
        {
            using var cursor = resolver.Query(uri, new[] { IOpenableColumns.DisplayName }, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                var index = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (index >= 0) return cursor.GetString(index);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Copies a SAF tree (recursively) into the import folder.</summary>
    public static Task<string?> PrepareFolderAsync(string treeUri)
        => Task.Run(() =>
        {
            try
            {
                var resolver = Android.App.Application.Context.ContentResolver;
                var tree = Android.Net.Uri.Parse(treeUri);
                Android.Util.Log.Debug("SongRequest", $"import folder: uri={treeUri} resolver={resolver != null} tree={tree != null}");
                if (resolver == null || tree == null) return null;

                var folderName = Sanitize(QueryDisplayName(resolver, tree) ?? "Imported");
                var destination = Path.Combine(ImportDirectory, folderName);
                Directory.CreateDirectory(destination);

                var rootId = DocumentsContract.GetTreeDocumentId(tree);
                if (string.IsNullOrEmpty(rootId)) return null;

                int copied = CopyTree(resolver, tree, rootId, destination, 0);
                Android.Util.Log.Debug("SongRequest", $"imported {copied} file(s) from '{folderName}' into {destination}");

                // Nothing usable in there: do not register an empty folder.
                return copied > 0 ? destination : null;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Debug("SongRequest", $"folder import failed: {ex.Message}");
                return null;
            }
        });

    /// <summary>
    /// Walks a SAF tree. The *tree* URI stays the same for the whole walk — only
    /// the document id changes per folder; passing a document URI as the tree
    /// silently re-reads the root folder forever.
    /// </summary>
    private static int CopyTree(ContentResolver resolver, Android.Net.Uri tree, string documentId, string destination, int depth)
    {
        if (depth > 12) return 0;

        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, documentId);
        Android.Util.Log.Debug("SongRequest", $"import folder: docId={documentId} children={children}");
        if (children == null) return 0;

        int copied = 0;
        using var cursor = resolver.Query(children,
            new[]
            {
                DocumentsContract.Document.ColumnDocumentId,
                DocumentsContract.Document.ColumnDisplayName,
                DocumentsContract.Document.ColumnMimeType,
            }, null, null, null);

        if (cursor == null)
        {
            Android.Util.Log.Debug("SongRequest", "import folder: query returned no cursor");
            return 0;
        }

        Android.Util.Log.Debug("SongRequest", $"import folder: {cursor.Count} entries");

        while (cursor.MoveToNext())
        {
            var childId = cursor.GetString(0);
            var name = cursor.GetString(1) ?? "unknown";
            var mime = cursor.GetString(2) ?? string.Empty;

            if (string.IsNullOrEmpty(childId)) continue;

            var childUri = DocumentsContract.BuildDocumentUriUsingTree(tree, childId);
            if (childUri == null) continue;

            if (mime == DocumentsContract.Document.MimeTypeDir)
            {
                var sub = Path.Combine(destination, Sanitize(name));
                Directory.CreateDirectory(sub);
                copied += CopyTree(resolver, tree, childId, sub, depth + 1);
                continue;
            }

            if (!IsAudio(name))
            {
                Android.Util.Log.Debug("SongRequest", $"import folder: skipping '{name}' ({mime})");
                continue;
            }

            try
            {
                using var input = resolver.OpenInputStream(childUri);
                if (input == null) continue;

                using var output = File.Create(Path.Combine(destination, Sanitize(name)));
                input.CopyTo(output);
                copied++;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Debug("SongRequest", $"skipped '{name}': {ex.Message}");
            }
        }

        return copied;
    }

    private static void CopyFile(string source, string destination)
    {
        try
        {
            var name = Sanitize(Path.GetFileName(source));

            if (source.StartsWith("content://", StringComparison.OrdinalIgnoreCase))
            {
                var resolver = Android.App.Application.Context.ContentResolver;
                var uri = Android.Net.Uri.Parse(source);
                if (resolver == null || uri == null) return;

                using var input = resolver.OpenInputStream(uri);
                if (input == null) return;

                using var output = File.Create(Path.Combine(destination, name));
                input.CopyTo(output);
                return;
            }

            if (File.Exists(source))
                File.Copy(source, Path.Combine(destination, name), overwrite: true);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Debug("SongRequest", $"copy of '{source}' failed: {ex.Message}");
        }
    }

    private static string? QueryDisplayName(ContentResolver resolver, Android.Net.Uri tree)
    {
        try
        {
            var documentId = DocumentsContract.GetTreeDocumentId(tree);
            var documentUri = DocumentsContract.BuildDocumentUriUsingTree(tree, documentId);
            if (documentUri == null) return null;

            using var cursor = resolver.Query(documentUri,
                new[] { DocumentsContract.Document.ColumnDisplayName }, null, null, null);
            return cursor != null && cursor.MoveToFirst() ? cursor.GetString(0) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAudio(string name)
        => AudioExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "imported" : cleaned;
    }
}
