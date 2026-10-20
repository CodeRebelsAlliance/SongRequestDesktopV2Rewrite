using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Metadata for a fetched YouTube video (title, creator, duration, cached
/// thumbnail/subtitles and approval state).
/// </summary>
public class VideoData
{
    public string YtId { get; set; } = string.Empty;
    public bool IsApproved { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public long DurationTicks { get; set; }
    public string ThumbnailPath { get; set; } = string.Empty;

    /// <summary>
    /// Metadata was already requested (successfully or not). Blacklisted ids have
    /// no panel/download to populate the cache, so without this every fetch poll
    /// would ask YouTube again for the same entry.
    /// </summary>
    public bool MetadataFetched { get; set; }
    public string SubtitleText { get; set; } = string.Empty;
    public string SubtitleTimedText { get; set; } = string.Empty;

    /// <summary>
    /// Whether captions were already looked up. Without this, a video with no
    /// captions looked like "not cached yet" and every re-process asked YouTube
    /// again.
    /// </summary>
    public bool SubtitlesFetched { get; set; }

    /// <summary>Lyrics looked up for this video (any provider), cached verbatim.</summary>
    public string LyricsText { get; set; } = string.Empty;

    /// <summary>
    /// Whether lyrics were already looked up. Cached separately from the text so a
    /// "nothing found" result is not retried on every panel open.
    /// </summary>
    public bool LyricsFetched { get; set; }
}

/// <summary>
/// Video metadata cache for the fetch/download engine. Host-agnostic and
/// WPF-free.
/// </summary>
public sealed class VideoCache
{
    private readonly object _sync = new();
    private readonly string _metadataCachePath;
    private Dictionary<string, VideoData> _data = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a successful save.</summary>
    public event Action? Changed;

    public VideoCache()
    {
        _metadataCachePath = AppPaths.VideoMetadataCachePath;
        EnsureFolders();
        Load();
    }

    public string ThumbnailCachePath => AppPaths.ThumbnailCachePath;

    public void EnsureFolders()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DownloadPath);
            Directory.CreateDirectory(AppPaths.ThumbnailCachePath);
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_metadataCachePath)) return;
            var json = File.ReadAllText(_metadataCachePath);
            var loaded = JsonConvert.DeserializeObject<Dictionary<string, VideoData>>(json);
            if (loaded == null) return;
            lock (_sync)
            {
                _data = loaded
                    .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            EnsureFolders();
            var tempPath = _metadataCachePath + ".tmp";
            string json;
            lock (_sync)
            {
                json = JsonConvert.SerializeObject(_data, Formatting.Indented);
            }
            File.WriteAllText(tempPath, json);
            File.Copy(tempPath, _metadataCachePath, true);
            File.Delete(tempPath);
            Changed?.Invoke();
        }
        catch { }
    }

    public string? GetCachedTitle(string videoId)
    {
        lock (_sync) { return _data.TryGetValue(videoId, out var d) ? d.Title : null; }
    }

    public string? GetCachedCreator(string videoId)
    {
        lock (_sync) { return _data.TryGetValue(videoId, out var d) ? d.Creator : null; }
    }

    public long GetCachedDurationTicks(string videoId)
    {
        lock (_sync) { return _data.TryGetValue(videoId, out var d) ? d.DurationTicks : 0; }
    }

    public VideoData GetOrCreate(string videoId)
    {
        lock (_sync)
        {
            if (!_data.TryGetValue(videoId, out var data))
            {
                data = new VideoData { YtId = videoId };
                _data[videoId] = data;
            }
            return data;
        }
    }

    public bool TryGet(string videoId, out VideoData? data)
    {
        lock (_sync) { return _data.TryGetValue(videoId, out data); }
    }

    public bool Contains(string videoId)
    {
        lock (_sync) { return _data.ContainsKey(videoId); }
    }

    public void SetApproved(string videoId, bool approved)
    {
        lock (_sync)
        {
            if (_data.TryGetValue(videoId, out var d)) d.IsApproved = approved;
        }
    }

    public void Remove(string videoId)
    {
        lock (_sync) { _data.Remove(videoId); }
    }
}
