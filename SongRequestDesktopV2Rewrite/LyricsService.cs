using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SongRequestDesktopV2Rewrite
{
    public sealed class LyricsResult
    {
        public bool Found { get; set; }
        public int StatusCode { get; set; }
        public string TrackName { get; set; } = string.Empty;
        public string ArtistName { get; set; } = string.Empty;
        public string AlbumName { get; set; } = string.Empty;
        public int DurationSeconds { get; set; }
        public bool Instrumental { get; set; }
        public string PlainLyrics { get; set; } = string.Empty;
        public string SyncedLyrics { get; set; } = string.Empty;

        /// <summary>
        /// Indicates whether synced (timed) lyrics are present.
        /// </summary>
        public bool HasSynced => !string.IsNullOrWhiteSpace(SyncedLyrics);

        /// <summary>
        /// Parses the synced lyrics into timestamped lines (TimeSpan, text).
        /// Returns empty list if no synced lyrics.
        /// </summary>
        public List<(TimeSpan Time, string Text)> ParseSyncedLines()
        {
            var result = new List<(TimeSpan, string)>();
            if (!HasSynced) return result;

            var lines = SyncedLyrics.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                // example: [03:25.72] some text
                var txt = line.Trim();
                int idxClose = txt.IndexOf(']');
                if (txt.StartsWith("[") && idxClose > 0)
                {
                    var tsPart = txt.Substring(1, idxClose - 1);
                    var textPart = txt.Substring(idxClose + 1).Trim();
                    if (TryParseTimestamp(tsPart, out var ts))
                    {
                        result.Add((ts, textPart));
                    }
                }
            }

            return result;
        }

        private static bool TryParseTimestamp(string tsPart, out TimeSpan ts)
        {
            // Accept formats mm:ss.xx or hh:mm:ss.xx
            ts = TimeSpan.Zero;
            try
            {
                // replace '.' with ':'? Not safe. Use manual parsing
                string s = tsPart;
                // Split on ':'
                var parts = s.Split(':');
                if (parts.Length == 2)
                {
                    // mm:ss.xx
                    int mm = int.Parse(parts[0]);
                    var secPart = parts[1];
                    double seconds = double.Parse(secPart, System.Globalization.CultureInfo.InvariantCulture);
                    ts = TimeSpan.FromSeconds(mm * 60 + seconds);
                    return true;
                }
                else if (parts.Length == 3)
                {
                    int hh = int.Parse(parts[0]);
                    int mm = int.Parse(parts[1]);
                    double seconds = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                    ts = new TimeSpan(0, hh, mm, 0).Add(TimeSpan.FromSeconds(seconds));
                    return true;
                }
            }
            catch { }
            return false;
        }
    }

    public class LyricsService
    {
        private readonly HttpClient _http;
        private const string BaseUrl = "https://lrclib.net";

        public LyricsService(HttpClient? httpClient = null)
        {
            _http = httpClient ?? new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Fetch lyrics (attempt live fetch). Returns LyricsResult. If not found, Found==false.
        /// </summary>
        public async Task<LyricsResult> GetLyricsAsync(string artistName, string trackName, TimeSpan duration, string? albumName = null, CancellationToken ct = default)
        {
            return await GetAsync("/api/get", artistName, trackName, duration, albumName, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Fetch cached lyrics (does not attempt external sources if not found).
        /// </summary>
        public async Task<LyricsResult> GetCachedLyricsAsync(string artistName, string trackName, TimeSpan duration, string? albumName = null, CancellationToken ct = default)
        {
            return await GetAsync("/api/get-cached", artistName, trackName, duration, albumName, ct).ConfigureAwait(false);
        }

        private async Task<LyricsResult> GetAsync(string path, string artistName, string trackName, TimeSpan duration, string? albumName, CancellationToken ct)
        {
            var result = new LyricsResult();
            try
            {
                var q = new List<string>
                {
                    "artist_name=" + Uri.EscapeDataString(artistName ?? string.Empty),
                    "track_name=" + Uri.EscapeDataString(trackName ?? string.Empty),
                    "duration=" + ((int)Math.Round(duration.TotalSeconds)).ToString()
                };
                if (!string.IsNullOrWhiteSpace(albumName)) q.Add("album_name=" + Uri.EscapeDataString(albumName));

                var url = BaseUrl.TrimEnd('/') + path + "?" + string.Join('&', q);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                result.StatusCode = (int)resp.StatusCode;

                var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    // try to parse error details
                    try
                    {
                        var j = JObject.Parse(content);
                        // If 404 not found, return not found
                        result.Found = false;
                        return result;
                    }
                    catch
                    {
                        result.Found = false;
                        return result;
                    }
                }

                // parse success JSON
                var obj = JObject.Parse(content);
                result.Found = true;
                result.TrackName = (string?)obj["trackName"] ?? (string?)obj["track_name"] ?? trackName;
                result.ArtistName = (string?)obj["artistName"] ?? (string?)obj["artist_name"] ?? artistName;
                result.AlbumName = (string?)obj["albumName"] ?? (string?)obj["album_name"] ?? albumName ?? string.Empty;
                result.DurationSeconds = (int?)obj["duration"] ?? (int)Math.Round(duration.TotalSeconds);
                result.Instrumental = (bool?)obj["instrumental"] ?? false;
                result.PlainLyrics = (string?)obj["plainLyrics"] ?? string.Empty;
                result.SyncedLyrics = (string?)obj["syncedLyrics"] ?? string.Empty;

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // on network/parse error return not found result
                result.Found = false;
                return result;
            }
        }
    }
}
