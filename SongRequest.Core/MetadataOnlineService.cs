using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SongRequestDesktopV2Rewrite;

/// <summary>
/// Free online catalogue lookup used by the metadata editor and the "identify"
/// action. Providers are deliberately independent and optional: MusicBrainz +
/// Cover Art Archive, the iTunes Search API and Deezer's public API. No API key
/// or account is required, and no audio is uploaded.
/// </summary>
public sealed class MetadataOnlineService : IAlbumMetadataProvider, IArtistMetadataProvider
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    private readonly HttpClient _http;
    private readonly MetadataProviderSettings _providers;

    public MetadataOnlineService(HttpClient? httpClient = null, MetadataProviderSettings? providers = null)
    {
        _http = httpClient ?? SharedHttpClient;
        _providers = providers ?? MetadataProviderSettings.FromConfig(ConfigService.Instance.Current);
    }

    public async Task<MetadataSearchResult> IdentifyAsync(
        string title,
        string artist,
        string album,
        TimeSpan duration,
        string? filePath,
        CancellationToken ct = default)
    {
        var parsedTitle = title?.Trim() ?? string.Empty;
        var parsedArtist = artist?.Trim() ?? string.Empty;
        var titleMissing = string.IsNullOrWhiteSpace(parsedTitle) ||
                           string.Equals(parsedTitle, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(parsedTitle, "Untitled", StringComparison.OrdinalIgnoreCase);
        var artistMissing = string.IsNullOrWhiteSpace(parsedArtist) ||
                            string.Equals(parsedArtist, "Unknown", StringComparison.OrdinalIgnoreCase);
        if (titleMissing || artistMissing)
        {
            var (fileArtist, fileTitle) = ParseFileName(filePath);
            if (titleMissing || LooksLikeFileName(parsedTitle))
                parsedTitle = fileTitle;
            if (artistMissing)
                parsedArtist = fileArtist;
        }

        var queries = new List<string>();
        if (!string.IsNullOrWhiteSpace(parsedArtist) && !string.IsNullOrWhiteSpace(parsedTitle))
            queries.Add($"{parsedArtist} {parsedTitle}");
        if (!string.IsNullOrWhiteSpace(parsedTitle))
            queries.Add(parsedTitle);
        if (queries.Count == 0) queries.Add(System.IO.Path.GetFileNameWithoutExtension(filePath) ?? string.Empty);

        var candidates = new List<MetadataCandidate>();
        var errors = new List<string>();
        var seenQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_providers.AcoustId &&
            !string.IsNullOrWhiteSpace(_providers.AcoustIdApiKey) &&
            !string.IsNullOrWhiteSpace(filePath) &&
            System.IO.File.Exists(filePath))
        {
            var fingerprint = await AcoustIdAsync(filePath!, _providers.AcoustIdApiKey, ct).ConfigureAwait(false);
            candidates.AddRange(fingerprint);
            if (fingerprint.Count == 0) errors.Add("AcoustID fingerprint lookup returned no match.");
        }

        foreach (var query in queries.Take(2))
        {
            if (!seenQueries.Add(query)) continue;
            var raw = await SearchRawAsync(
                query,
                ct,
                candidate => Score(candidate, parsedTitle, parsedArtist, duration, album),
                stopAt: 0.78).ConfigureAwait(false);
            candidates.AddRange(raw.Candidates);
            if (!string.IsNullOrWhiteSpace(raw.Error)) errors.Add(raw.Error);
            if (raw.Candidates.Any(c => c.Score >= 0.78)) break;
        }

        foreach (var candidate in candidates)
        {
            candidate.Score = Score(candidate, parsedTitle, parsedArtist, duration, album);
            if (string.Equals(candidate.Provider, "AcoustID", StringComparison.Ordinal))
                candidate.Score = Math.Min(1.0, candidate.Score + 0.15);
        }

        var best = candidates
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Provider == "MusicBrainz")
            .FirstOrDefault();

        // Keep the list manageable but include a few alternatives for the UI.
        var ordered = candidates
            .OrderByDescending(c => c.Score)
            .GroupBy(c => (Normalize(c.Title), Normalize(c.Artist)))
            .Select(g => g.First())
            .Take(20)
            .ToList();

        return new MetadataSearchResult
        {
            Query = string.Join(" · ", seenQueries),
            Candidates = ordered,
            Best = best,
            Error = ordered.Count == 0 && errors.Count > 0 ? string.Join("; ", errors) : null,
        };
    }

    public async Task<MetadataSearchResult> SearchAsync(string query, TimeSpan duration, CancellationToken ct = default)
    {
        var result = await SearchRawAsync(
            query ?? string.Empty,
            ct,
            candidate => Score(candidate, query ?? string.Empty, string.Empty, duration, string.Empty),
            stopAt: 0.78).ConfigureAwait(false);
        foreach (var candidate in result.Candidates)
            candidate.Score = Score(candidate, query ?? string.Empty, string.Empty, duration, string.Empty);
        result.Candidates = result.Candidates
            .OrderByDescending(c => c.Score)
            .Take(30)
            .ToList();
        result.Best = result.Candidates.FirstOrDefault();
        return result;
    }

    public async Task<ArtworkResult> FetchArtworkAsync(string url, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return new ArtworkResult { Error = "Invalid artwork URL." };

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
            var provider = uri.Host.EndsWith("coverartarchive.org", StringComparison.OrdinalIgnoreCase)
                ? "CoverArtArchive"
                : "Artwork";
            using var response = await SendAsync(provider, request, false, ct).ConfigureAwait(false);
            if (response == null)
                return new ArtworkResult { Error = "Artwork source is cooling down; try again in a moment." };
            if (!response.IsSuccessStatusCode)
                return new ArtworkResult { Error = $"Artwork download failed ({(int)response.StatusCode})." };

            var contentLength = response.Content.Headers.ContentLength ?? 0;
            if (contentLength > SongMetadataService.MaxArtworkBytes)
                return new ArtworkResult { Error = "The artwork is larger than 20 MB." };

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0) return new ArtworkResult { Error = "The artwork file was empty." };
            if (bytes.Length > SongMetadataService.MaxArtworkBytes)
                return new ArtworkResult { Error = "The artwork is larger than 20 MB." };

            var detected = DetectImageMimeOrNull(bytes);
            if (detected == null) return new ArtworkResult { Error = "The downloaded file is not an image." };
            var mime = NormalizeImageMime(response.Content.Headers.ContentType?.MediaType) ?? detected;
            return new ArtworkResult { Bytes = bytes, MimeType = mime };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ArtworkResult { Error = ex.Message };
        }
    }

    private async Task<MetadataSearchResult> SearchRawAsync(
        string query,
        CancellationToken ct,
        Func<MetadataCandidate, double>? scorer = null,
        double stopAt = double.MaxValue)
    {
        var result = new MetadataSearchResult { Query = query };
        if (string.IsNullOrWhiteSpace(query)) return result;

        var errors = new List<string>();
        foreach (var provider in MetadataProviderSettings.NormalizePriority(_providers.Priority))
        {
            List<MetadataCandidate> candidates;
            try
            {
                candidates = provider switch
                {
                    "MusicBrainz" when _providers.MusicBrainz => await MusicBrainzAsync(query, ct).ConfigureAwait(false),
                    "iTunes" when _providers.ITunes => await ITunesAsync(query, ct).ConfigureAwait(false),
                    "Deezer" when _providers.Deezer => await DeezerAsync(query, ct).ConfigureAwait(false),
                    "Discogs" when _providers.Discogs => await DiscogsAsync(query, ct).ConfigureAwait(false),
                    _ => new List<MetadataCandidate>(),
                };
            }
            catch (Exception ex)
            {
                errors.Add(provider + ": " + ex.Message);
                continue;
            }

            if (candidates.Count == 0) continue;
            result.Candidates.AddRange(candidates);
            if (scorer != null && candidates.Max(scorer) >= stopAt)
                break;
        }

        result.Error = result.Candidates.Count == 0 && errors.Count > 0
            ? string.Join("; ", errors)
            : null;

        if (result.Candidates.Count == 0 && result.Error == null)
        {
            var cooling = MetadataProviderGovernor.GetStatuses()
                .Where(s => s.CooldownUntilUtc.HasValue)
                .Select(s => s.Name)
                .ToList();
            if (cooling.Count > 0)
                result.Error = "Provider cooling down: " + string.Join(", ", cooling);
        }

        return result;
    }

    // ── AcoustID fingerprinting (optional) ────────────────────────────────

    /// <summary>Path to <c>fpcalc</c> (Chromaprint) when the user has it installed.</summary>
    internal static string? FindFpcalc()
    {
        var name = OperatingSystem.IsWindows() ? "fpcalc.exe" : "fpcalc";
        foreach (var directory in new[] { AppPaths.ToolsDirectory, AppPaths.UserToolsDirectory })
        {
            try
            {
                var candidate = System.IO.Path.Combine(directory, name);
                if (System.IO.File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        try
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = System.IO.Path.Combine(directory, name);
                if (System.IO.File.Exists(candidate)) return candidate;
            }
        }
        catch { }

        return null;
    }

    private async Task<List<MetadataCandidate>> AcoustIdAsync(string filePath, string apiKey, CancellationToken ct)
    {
        var fpcalc = FindFpcalc();
        if (fpcalc == null) return new List<MetadataCandidate>();

        string fingerprint;
        double duration;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = fpcalc,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-json");
            start.ArgumentList.Add("-length");
            start.ArgumentList.Add("120");
            start.ArgumentList.Add(filePath);

            using var process = Process.Start(start);
            if (process == null) return new List<MetadataCandidate>();
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return new List<MetadataCandidate>();

            var json = JObject.Parse(output);
            fingerprint = json["fingerprint"]?.ToString() ?? string.Empty;
            duration = json["duration"]?.ToObject<double?>() ?? 0;
        }
        catch
        {
            return new List<MetadataCandidate>();
        }

        if (string.IsNullOrWhiteSpace(fingerprint)) return new List<MetadataCandidate>();

        var url = "https://api.acoustid.org/v2/lookup?client=" + Uri.EscapeDataString(apiKey) +
                  "&meta=recordings+releasegroups+compress&duration=" +
                  Math.Round(duration, 2).ToString(CultureInfo.InvariantCulture) +
                  "&fingerprint=" + Uri.EscapeDataString(fingerprint);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendAsync("AcoustID", request, false, ct).ConfigureAwait(false);
            if (response == null || !response.IsSuccessStatusCode) return new List<MetadataCandidate>();
            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var parsed = ParseAcoustId(json);
            if (duration > 0)
            {
                foreach (var candidate in parsed)
                    candidate.Duration = TimeSpan.FromSeconds(duration);
            }
            return parsed;
        }
        catch
        {
            return new List<MetadataCandidate>();
        }
    }

    internal static List<MetadataCandidate> ParseAcoustId(JObject json)
    {
        var list = new List<MetadataCandidate>();
        foreach (var result in json["results"] as JArray ?? new JArray())
        {
            foreach (var recording in result["recordings"] as JArray ?? new JArray())
            {
                var artist = string.Join(", ",
                    (recording["artists"] as JArray ?? new JArray())
                        .Select(a => a["name"]?.ToString())
                        .Where(n => !string.IsNullOrWhiteSpace(n)));
                var releaseGroup = (recording["releasegroups"] as JArray)?.FirstOrDefault();
                var groupId = releaseGroup?["id"]?.ToString();
                var score = result["score"]?.ToObject<double?>() ?? 0;

                list.Add(new MetadataCandidate
                {
                    Provider = "AcoustID",
                    ProviderId = recording["id"]?.ToString() ?? string.Empty,
                    Title = recording["title"]?.ToString() ?? string.Empty,
                    Artist = artist,
                    AlbumArtist = artist,
                    Album = releaseGroup?["title"]?.ToString() ?? string.Empty,
                    ArtworkUrl = string.IsNullOrWhiteSpace(groupId)
                        ? null
                        : $"https://coverartarchive.org/release-group/{groupId}/front-500",
                    SourceUrl = recording["id"] == null
                        ? null
                        : "https://musicbrainz.org/recording/" + recording["id"],
                    Score = score,
                });
            }
        }

        return list;
    }

    // ── MusicBrainz + Cover Art Archive ───────────────────────────────────

    private static readonly SemaphoreSlim MusicBrainzGate = new(1, 1);
    private static DateTime _lastMusicBrainzUtc = DateTime.MinValue;

    private async Task<List<MetadataCandidate>> MusicBrainzAsync(string query, CancellationToken ct)
    {
        // MusicBrainz requires at most one request per second per IP and a
        // meaningful User-Agent. Serialize calls and leave a small margin.
        await MusicBrainzGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastMusicBrainzUtc);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);

            var url = "https://musicbrainz.org/ws/2/recording?query=" +
                      Uri.EscapeDataString(query) + "&fmt=json&limit=10";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await SendAsync("MusicBrainz", request, false, ct).ConfigureAwait(false);
            _lastMusicBrainzUtc = DateTime.UtcNow;
            if (response == null || !response.IsSuccessStatusCode) return new List<MetadataCandidate>();

            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var list = new List<MetadataCandidate>();
            foreach (var rec in json["recordings"] as JArray ?? new JArray())
            {
                var release = (rec["releases"] as JArray)?.FirstOrDefault();
                var artist = string.Join(", ",
                    (rec["artist-credit"] as JArray ?? new JArray())
                        .Select(a => a["name"]?.ToString())
                        .Where(n => !string.IsNullOrWhiteSpace(n)));
                var releaseTitle = release?["title"]?.ToString() ?? string.Empty;
                var releaseId = release?["id"]?.ToString();
                var date = release?["date"]?.ToString()
                           ?? release?["release-group"]?["first-release-date"]?.ToString()
                           ?? string.Empty;
                var media = (release?["media"] as JArray)?.FirstOrDefault();
                var track = TryUInt(media?["track-number"]?.ToString());
                var length = rec["length"]?.ToObject<long?>() ?? 0;

                var candidate = new MetadataCandidate
                {
                    Provider = "MusicBrainz",
                    ProviderId = rec["id"]?.ToString() ?? string.Empty,
                    Title = rec["title"]?.ToString() ?? string.Empty,
                    Artist = artist,
                    AlbumArtist = artist,
                    Album = releaseTitle,
                    Year = ParseYear(date),
                    Track = track,
                    Duration = length > 0 ? TimeSpan.FromMilliseconds(length) : TimeSpan.Zero,
                    SourceUrl = rec["id"] == null ? null : "https://musicbrainz.org/recording/" + rec["id"],
                };

                if (_providers.CoverArtArchive && !string.IsNullOrWhiteSpace(releaseId))
                {
                    candidate.ArtworkUrl = $"https://coverartarchive.org/release/{releaseId}/front-500";
                }

                if (!string.IsNullOrWhiteSpace(candidate.Title))
                    list.Add(candidate);
            }

            return list;
        }
        finally
        {
            MusicBrainzGate.Release();
        }
    }

    // ── iTunes ────────────────────────────────────────────────────────────

    private async Task<List<MetadataCandidate>> ITunesAsync(string query, CancellationToken ct)
    {
        var url = "https://itunes.apple.com/search?term=" + Uri.EscapeDataString(query) +
                  "&media=music&entity=song&limit=10";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync("iTunes", request, false, ct).ConfigureAwait(false);
        if (response == null || !response.IsSuccessStatusCode) return new List<MetadataCandidate>();

        var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var list = new List<MetadataCandidate>();
        foreach (var item in json["results"] as JArray ?? new JArray())
        {
            var millis = item["trackTimeMillis"]?.ToObject<long?>() ?? 0;
            var artwork = item["artworkUrl100"]?.ToString();
            list.Add(new MetadataCandidate
            {
                Provider = "iTunes",
                ProviderId = item["trackId"]?.ToString() ?? string.Empty,
                Title = item["trackName"]?.ToString() ?? string.Empty,
                Artist = item["artistName"]?.ToString() ?? string.Empty,
                AlbumArtist = item["collectionArtistName"]?.ToString() ?? item["artistName"]?.ToString() ?? string.Empty,
                Album = item["collectionName"]?.ToString() ?? string.Empty,
                Genre = item["primaryGenreName"]?.ToString() ?? string.Empty,
                Year = ParseYear(item["releaseDate"]?.ToString()),
                Track = TryUInt(item["trackNumber"]?.ToString()),
                Disc = TryUInt(item["discNumber"]?.ToString()),
                Duration = millis > 0 ? TimeSpan.FromMilliseconds(millis) : TimeSpan.Zero,
                ArtworkUrl = UpscaleITunesArtwork(artwork),
                SourceUrl = item["trackViewUrl"]?.ToString(),
            });
        }

        return list.Where(c => !string.IsNullOrWhiteSpace(c.Title)).ToList();
    }

    // ── Deezer ────────────────────────────────────────────────────────────

    private async Task<List<MetadataCandidate>> DeezerAsync(string query, CancellationToken ct)
    {
        var url = "https://api.deezer.com/search?q=" + Uri.EscapeDataString(query) + "&limit=10";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync("Deezer", request, false, ct).ConfigureAwait(false);
        if (response == null || !response.IsSuccessStatusCode) return new List<MetadataCandidate>();

        var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var list = new List<MetadataCandidate>();
        foreach (var item in json["data"] as JArray ?? new JArray())
        {
            var seconds = item["duration"]?.ToObject<int?>() ?? 0;
            var album = item["album"];
            list.Add(new MetadataCandidate
            {
                Provider = "Deezer",
                ProviderId = item["id"]?.ToString() ?? string.Empty,
                Title = item["title"]?.ToString() ?? string.Empty,
                Artist = item["artist"]?["name"]?.ToString() ?? string.Empty,
                AlbumArtist = item["artist"]?["name"]?.ToString() ?? string.Empty,
                Album = album?["title"]?.ToString() ?? string.Empty,
                Year = ParseYear(album?["release_date"]?.ToString()),
                Track = TryUInt(item["track_position"]?.ToString()),
                Disc = TryUInt(item["disk_number"]?.ToString()),
                Duration = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero,
                ArtworkUrl = album?["cover_xl"]?.ToString() ?? album?["cover_big"]?.ToString(),
                SourceUrl = item["link"]?.ToString(),
            });
        }

        return list.Where(c => !string.IsNullOrWhiteSpace(c.Title)).ToList();
    }

    // ── Discogs ───────────────────────────────────────────────────────────

    private async Task<List<MetadataCandidate>> DiscogsAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = "https://api.discogs.com/database/search?q=" + Uri.EscapeDataString(query) +
                      "&type=release&per_page=10";
            if (!string.IsNullOrWhiteSpace(_providers.DiscogsToken))
                url += "&token=" + Uri.EscapeDataString(_providers.DiscogsToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await SendAsync("Discogs", request, !string.IsNullOrWhiteSpace(_providers.DiscogsToken), ct).ConfigureAwait(false);
            if (response == null || !response.IsSuccessStatusCode) return new List<MetadataCandidate>();

            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var list = new List<MetadataCandidate>();
            foreach (var item in json["results"] as JArray ?? new JArray())
            {
                var rawTitle = item["title"]?.ToString() ?? string.Empty;
                var (artist, title) = SplitDiscogsTitle(rawTitle);
                var genres = (item["genre"] as JArray ?? new JArray())
                    .Concat(item["style"] as JArray ?? new JArray())
                    .Select(g => g.ToString())
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                var year = ParseYear(item["year"]?.ToString());

                list.Add(new MetadataCandidate
                {
                    Provider = "Discogs",
                    ProviderId = item["id"]?.ToString() ?? item["master_id"]?.ToString() ?? string.Empty,
                    Title = title,
                    Artist = artist,
                    AlbumArtist = artist,
                    Album = title,
                    Genre = string.Join(", ", genres),
                    Year = year,
                    ArtworkUrl = item["cover_image"]?.ToString() ?? item["thumb"]?.ToString(),
                    SourceUrl = item["resource_url"]?.ToString() ?? item["uri"]?.ToString(),
                });
            }

            return list;
        }
        catch
        {
            return new List<MetadataCandidate>();
        }
    }

    internal static (string Artist, string Title) SplitDiscogsTitle(string rawTitle)
    {
        var text = rawTitle?.Trim() ?? string.Empty;
        var dash = text.IndexOf(" - ", StringComparison.Ordinal);
        if (dash <= 0) return (string.Empty, text);
        return (text[..dash].Trim(), text[(dash + 3)..].Trim());
    }

    private async Task<HttpResponseMessage?> SendAsync(
        string provider,
        HttpRequestMessage request,
        bool authenticated,
        CancellationToken ct)
    {
        if (!await MetadataProviderGovernor.AcquireAsync(provider, authenticated, ct).ConfigureAwait(false))
            return null;

        try
        {
            var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            MetadataProviderGovernor.ReportResponse(provider, (int)response.StatusCode, RetryAfter(response));
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MetadataProviderGovernor.ReportException(provider, ex);
            throw;
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        try
        {
            if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
            if (response.Headers.RetryAfter?.Date is { } date)
            {
                var wait = date - DateTimeOffset.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }
        catch { }
        return null;
    }

    // ── Album lookup (tracks + metadata) ──────────────────────────────────

    /// <summary>
    /// Finds an album and its ordered track list using the enabled providers in the
    /// user's priority order. The first provider returning a track list wins; the
    /// album service caches this result.
    /// </summary>
    public async Task<AlbumProviderResult> FindAlbumAsync(
        string album,
        string artist,
        uint year,
        CancellationToken ct = default)
    {
        AlbumProviderResult? fallback = null;
        foreach (var provider in MetadataProviderSettings.NormalizePriority(_providers.Priority))
        {
            AlbumProviderResult? result = provider switch
            {
                "MusicBrainz" when _providers.MusicBrainz => await MusicBrainzAlbumAsync(album, artist, ct).ConfigureAwait(false),
                "iTunes" when _providers.ITunes => await ITunesAlbumAsync(album, artist, ct).ConfigureAwait(false),
                "Deezer" when _providers.Deezer => await DeezerAlbumAsync(album, artist, ct).ConfigureAwait(false),
                "Discogs" when _providers.Discogs => await DiscogsAlbumAsync(album, artist, ct).ConfigureAwait(false),
                _ => null,
            };

            if (result == null) continue;
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                fallback ??= result;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result.Title) || result.Tracks.Count > 0)
            {
                if (result.Tracks.Count > 0) return result;
                fallback ??= result;
            }
        }

        return fallback ?? new AlbumProviderResult { Error = "No album match found." };
    }

    private async Task<AlbumProviderResult?> MusicBrainzAlbumAsync(string album, string artist, CancellationToken ct)
    {
        await MusicBrainzGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var query = $"release:\"{album}\"";
            if (!string.IsNullOrWhiteSpace(artist)) query += $" AND artist:\"{artist}\"";
            var searchUrl = "https://musicbrainz.org/ws/2/release?query=" +
                            Uri.EscapeDataString(query) + "&fmt=json&limit=3";
            using var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchRequest.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
            searchRequest.Headers.Accept.ParseAdd("application/json");
            using var searchResponse = await SendAsync("MusicBrainz", searchRequest, false, ct).ConfigureAwait(false);
            _lastMusicBrainzUtc = DateTime.UtcNow;
            if (searchResponse == null || !searchResponse.IsSuccessStatusCode) return null;

            var searchJson = JObject.Parse(await searchResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var release = (searchJson["releases"] as JArray)?.FirstOrDefault();
            var releaseId = release?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(releaseId)) return null;

            var detailUrl = "https://musicbrainz.org/ws/2/release/" + releaseId +
                            "?inc=recordings+artist-credits+release-groups&fmt=json";
            using var detailRequest = new HttpRequestMessage(HttpMethod.Get, detailUrl);
            detailRequest.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
            detailRequest.Headers.Accept.ParseAdd("application/json");
            using var detailResponse = await SendAsync("MusicBrainz", detailRequest, false, ct).ConfigureAwait(false);
            _lastMusicBrainzUtc = DateTime.UtcNow;
            if (detailResponse == null || !detailResponse.IsSuccessStatusCode) return null;

            var json = JObject.Parse(await detailResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var result = new AlbumProviderResult
            {
                Provider = "MusicBrainz",
                Title = json["title"]?.ToString() ?? album,
                Artist = string.Join(", ", (json["artist-credit"] as JArray ?? new JArray())
                    .Select(a => a["name"]?.ToString()).Where(n => !string.IsNullOrWhiteSpace(n))),
                Year = ParseYear(json["date"]?.ToString()),
                Genre = (json["tags"] as JArray ?? new JArray()).FirstOrDefault()?["name"]?.ToString() ?? string.Empty,
            };

            int disc = 0;
            foreach (var medium in json["media"] as JArray ?? new JArray())
            {
                disc++;
                foreach (var track in medium["tracks"] as JArray ?? new JArray())
                {
                    var length = track["length"]?.ToObject<long?>() ?? 0;
                    var trackPosition = TryUInt(track["number"]?.ToString());
                    var mediumPosition = TryUInt(medium["position"]?.ToString());
                    result.Tracks.Add(new AlbumTrackInfo
                    {
                        Title = track["title"]?.ToString() ?? string.Empty,
                        Artist = result.Artist,
                        Track = trackPosition,
                        Disc = mediumPosition > 0 ? mediumPosition : (uint)disc,
                        Duration = length > 0 ? TimeSpan.FromMilliseconds(length) : TimeSpan.Zero,
                    });
                }
            }

            if (_providers.CoverArtArchive && !string.IsNullOrWhiteSpace(releaseId))
                result.ArtworkUrl = $"https://coverartarchive.org/release/{releaseId}/front-500";
            return result;
        }
        catch (Exception ex)
        {
            return new AlbumProviderResult { Provider = "MusicBrainz", Error = ex.Message };
        }
        finally
        {
            MusicBrainzGate.Release();
        }
    }

    private async Task<AlbumProviderResult?> ITunesAlbumAsync(string album, string artist, CancellationToken ct)
    {
        var term = string.Join(" ", new[] { artist, album }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var searchUrl = "https://itunes.apple.com/search?term=" + Uri.EscapeDataString(term) +
                        "&media=music&entity=album&limit=3";
        using var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        using var searchResponse = await SendAsync("iTunes", searchRequest, false, ct).ConfigureAwait(false);
        if (searchResponse == null || !searchResponse.IsSuccessStatusCode) return null;

        var searchJson = JObject.Parse(await searchResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var collection = (searchJson["results"] as JArray)?.FirstOrDefault();
        var collectionId = collection?["collectionId"]?.ToString();
        if (string.IsNullOrWhiteSpace(collectionId)) return null;

        var lookupUrl = "https://itunes.apple.com/lookup?id=" + collectionId + "&entity=song&limit=200";
        using var lookupRequest = new HttpRequestMessage(HttpMethod.Get, lookupUrl);
        using var lookupResponse = await SendAsync("iTunes", lookupRequest, false, ct).ConfigureAwait(false);
        if (lookupResponse == null || !lookupResponse.IsSuccessStatusCode) return null;

        var json = JObject.Parse(await lookupResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var items = json["results"] as JArray ?? new JArray();
        var result = new AlbumProviderResult
        {
            Provider = "iTunes",
            Title = collection?["collectionName"]?.ToString() ?? album,
            Artist = collection?["artistName"]?.ToString() ?? artist,
            Year = ParseYear(collection?["releaseDate"]?.ToString()),
            Genre = collection?["primaryGenreName"]?.ToString() ?? string.Empty,
            ArtworkUrl = UpscaleITunesArtwork(collection?["artworkUrl100"]?.ToString()),
        };

        foreach (var item in items)
        {
            if (!string.Equals(item["wrapperType"]?.ToString(), "track", StringComparison.OrdinalIgnoreCase)) continue;
            var millis = item["trackTimeMillis"]?.ToObject<long?>() ?? 0;
            result.Tracks.Add(new AlbumTrackInfo
            {
                Title = item["trackName"]?.ToString() ?? string.Empty,
                Artist = item["artistName"]?.ToString() ?? result.Artist,
                Track = TryUInt(item["trackNumber"]?.ToString()),
                Disc = TryUInt(item["discNumber"]?.ToString()),
                Duration = millis > 0 ? TimeSpan.FromMilliseconds(millis) : TimeSpan.Zero,
            });
        }

        return result;
    }

    private async Task<AlbumProviderResult?> DeezerAlbumAsync(string album, string artist, CancellationToken ct)
    {
        var term = string.Join(" ", new[] { artist, album }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var searchUrl = "https://api.deezer.com/search/album?q=" + Uri.EscapeDataString(term) + "&limit=3";
        using var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        using var searchResponse = await SendAsync("Deezer", searchRequest, false, ct).ConfigureAwait(false);
        if (searchResponse == null || !searchResponse.IsSuccessStatusCode) return null;

        var searchJson = JObject.Parse(await searchResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var albumEntry = (searchJson["data"] as JArray)?.FirstOrDefault();
        var albumId = albumEntry?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(albumId)) return null;

        var detailUrl = "https://api.deezer.com/album/" + albumId;
        using var detailRequest = new HttpRequestMessage(HttpMethod.Get, detailUrl);
        using var detailResponse = await SendAsync("Deezer", detailRequest, false, ct).ConfigureAwait(false);
        if (detailResponse == null || !detailResponse.IsSuccessStatusCode) return null;

        var json = JObject.Parse(await detailResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var result = new AlbumProviderResult
        {
            Provider = "Deezer",
            Title = json["title"]?.ToString() ?? album,
            Artist = json["artist"]?["name"]?.ToString() ?? artist,
            Year = ParseYear(json["release_date"]?.ToString()),
            Genre = (json["genres"]?["data"] as JArray)?.FirstOrDefault()?["name"]?.ToString() ?? string.Empty,
            ArtworkUrl = json["cover_xl"]?.ToString() ?? json["cover_big"]?.ToString(),
        };

        foreach (var track in json["tracks"]?["data"] as JArray ?? new JArray())
        {
            var seconds = track["duration"]?.ToObject<int?>() ?? 0;
            result.Tracks.Add(new AlbumTrackInfo
            {
                Title = track["title"]?.ToString() ?? string.Empty,
                Artist = track["artist"]?["name"]?.ToString() ?? result.Artist,
                Track = TryUInt(track["track_position"]?.ToString()),
                Disc = TryUInt(track["disk_number"]?.ToString()),
                Duration = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero,
            });
        }

        return result;
    }

    private async Task<AlbumProviderResult?> DiscogsAlbumAsync(string album, string artist, CancellationToken ct)
    {
        var term = string.Join(" ", new[] { artist, album }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var url = "https://api.discogs.com/database/search?q=" + Uri.EscapeDataString(term) +
                  "&type=release&per_page=3";
        if (!string.IsNullOrWhiteSpace(_providers.DiscogsToken))
            url += "&token=" + Uri.EscapeDataString(_providers.DiscogsToken);

        using var searchRequest = new HttpRequestMessage(HttpMethod.Get, url);
        searchRequest.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
        searchRequest.Headers.Accept.ParseAdd("application/json");
        using var searchResponse = await SendAsync("Discogs", searchRequest, !string.IsNullOrWhiteSpace(_providers.DiscogsToken), ct).ConfigureAwait(false);
        if (searchResponse == null || !searchResponse.IsSuccessStatusCode) return null;

        var searchJson = JObject.Parse(await searchResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var entry = (searchJson["results"] as JArray)?.FirstOrDefault();
        var id = entry?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(id)) return null;

        var detailUrl = "https://api.discogs.com/releases/" + id;
        if (!string.IsNullOrWhiteSpace(_providers.DiscogsToken))
            detailUrl += "?token=" + Uri.EscapeDataString(_providers.DiscogsToken);
        using var detailRequest = new HttpRequestMessage(HttpMethod.Get, detailUrl);
        detailRequest.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
        detailRequest.Headers.Accept.ParseAdd("application/json");
        using var detailResponse = await SendAsync("Discogs", detailRequest, !string.IsNullOrWhiteSpace(_providers.DiscogsToken), ct).ConfigureAwait(false);
        if (detailResponse == null || !detailResponse.IsSuccessStatusCode) return null;

        var json = JObject.Parse(await detailResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var result = new AlbumProviderResult
        {
            Provider = "Discogs",
            Title = json["title"]?.ToString() ?? album,
            Artist = string.Join(", ", (json["artists"] as JArray ?? new JArray())
                .Select(a => a["name"]?.ToString()).Where(n => !string.IsNullOrWhiteSpace(n))),
            Year = ParseYear(json["year"]?.ToString()),
            Genre = string.Join(", ", (json["genres"] as JArray ?? new JArray()).Select(g => g.ToString())),
            ArtworkUrl = (json["images"] as JArray)?.FirstOrDefault()?["uri"]?.ToString(),
        };

        // Discogs uses "heading" rows to separate sides/discs. A release that
        // starts with a heading still has its first group on disc 1.
        uint disc = 0;
        foreach (var track in json["tracklist"] as JArray ?? new JArray())
        {
            if (string.Equals(track["type_"]?.ToString(), "heading", StringComparison.OrdinalIgnoreCase))
            {
                disc++;
                continue;
            }

            if (disc == 0) disc = 1;
            var duration = TryParseDuration(track["duration"]?.ToString());
            result.Tracks.Add(new AlbumTrackInfo
            {
                Title = track["title"]?.ToString() ?? string.Empty,
                Artist = result.Artist,
                Track = ParseDiscogsTrack(track["position"]?.ToString()),
                Disc = disc,
                Duration = duration,
            });
        }

        return result;
    }

    private static uint ParseDiscogsTrack(string? position)
    {
        if (string.IsNullOrWhiteSpace(position)) return 0;
        var digits = new string(position.Where(char.IsDigit).ToArray());
        return uint.TryParse(digits, out var value) ? value : 0;
    }

    private static TimeSpan TryParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
        var parts = text.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var m) && double.TryParse(parts[1], out var s))
            return TimeSpan.FromSeconds(m * 60 + s);
        if (parts.Length == 3 &&
            int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var mm) && double.TryParse(parts[2], out var ss))
            return new TimeSpan(0, h, mm, 0) + TimeSpan.FromSeconds(ss);
        return TimeSpan.Zero;
    }

    // ── Artist lookup (metadata + artwork) ────────────────────────────────

    /// <summary>
    /// Finds artist metadata/artwork using the enabled providers in priority
    /// order. Results are merged so a later provider can supply artwork when the
    /// first provider only knows the name/genre. The artist service caches this.
    /// </summary>
    public async Task<ArtistProviderResult> FindArtistAsync(
        string artist,
        CancellationToken ct = default)
    {
        ArtistProviderResult? best = null;
        foreach (var provider in MetadataProviderSettings.NormalizePriority(_providers.Priority))
        {
            ArtistProviderResult? result = provider switch
            {
                "MusicBrainz" when _providers.MusicBrainz => await MusicBrainzArtistAsync(artist, ct).ConfigureAwait(false),
                "iTunes" when _providers.ITunes => await ITunesArtistAsync(artist, ct).ConfigureAwait(false),
                "Deezer" when _providers.Deezer => await DeezerArtistAsync(artist, ct).ConfigureAwait(false),
                "Discogs" when _providers.Discogs => await DiscogsArtistAsync(artist, ct).ConfigureAwait(false),
                _ => null,
            };

            if (result == null || !string.IsNullOrWhiteSpace(result.Error)) continue;
            if (string.IsNullOrWhiteSpace(result.Name) && string.IsNullOrWhiteSpace(result.ArtworkUrl)) continue;

            if (best == null)
            {
                best = result;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(best.Name)) best.Name = result.Name;
                if (string.IsNullOrWhiteSpace(best.SortName)) best.SortName = result.SortName;
                if (string.IsNullOrWhiteSpace(best.Genre)) best.Genre = result.Genre;
                if (string.IsNullOrWhiteSpace(best.Description)) best.Description = result.Description;
                if (string.IsNullOrWhiteSpace(best.Country)) best.Country = result.Country;
                if (string.IsNullOrWhiteSpace(best.ArtworkUrl)) best.ArtworkUrl = result.ArtworkUrl;
                if (best.Albums.Count == 0 && result.Albums.Count > 0) best.Albums = result.Albums;
            }

            // Artwork is the expensive gap to fill (MusicBrainz/iTunes do not
            // provide artist photos). Stop as soon as a provider supplies it;
            // otherwise keep the best metadata collected so far.
            if (!string.IsNullOrWhiteSpace(best.ArtworkUrl))
                break;
        }

        return best ?? new ArtistProviderResult { Error = "No artist match found." };
    }

    private async Task<ArtistProviderResult?> MusicBrainzArtistAsync(string artist, CancellationToken ct)
    {
        await MusicBrainzGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var searchUrl = "https://musicbrainz.org/ws/2/artist?query=" +
                            Uri.EscapeDataString($"artist:\"{artist}\"") + "&fmt=json&limit=3";
            using var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchRequest.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
            searchRequest.Headers.Accept.ParseAdd("application/json");
            using var searchResponse = await SendAsync("MusicBrainz", searchRequest, false, ct).ConfigureAwait(false);
            _lastMusicBrainzUtc = DateTime.UtcNow;
            if (searchResponse == null || !searchResponse.IsSuccessStatusCode) return null;

            var searchJson = JObject.Parse(await searchResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var hit = (searchJson["artists"] as JArray)?.FirstOrDefault();
            var artistId = hit?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(artistId)) return null;

            var detailUrl = "https://musicbrainz.org/ws/2/artist/" + artistId +
                            "?inc=genres+tags+release-groups&fmt=json";
            using var detailRequest = new HttpRequestMessage(HttpMethod.Get, detailUrl);
            detailRequest.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
            detailRequest.Headers.Accept.ParseAdd("application/json");
            using var detailResponse = await SendAsync("MusicBrainz", detailRequest, false, ct).ConfigureAwait(false);
            _lastMusicBrainzUtc = DateTime.UtcNow;
            if (detailResponse == null || !detailResponse.IsSuccessStatusCode) return null;

            var json = JObject.Parse(await detailResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var result = new ArtistProviderResult
            {
                Provider = "MusicBrainz",
                Name = json["name"]?.ToString() ?? hit?["name"]?.ToString() ?? artist,
                SortName = json["sort-name"]?.ToString() ?? string.Empty,
                Country = json["country"]?.ToString() ?? string.Empty,
                Description = json["disambiguation"]?.ToString() ?? string.Empty,
                Genre = (json["genres"] as JArray)?.FirstOrDefault()?["name"]?.ToString()
                        ?? (json["tags"] as JArray)?.FirstOrDefault()?["name"]?.ToString()
                        ?? string.Empty,
            };

            foreach (var group in json["release-groups"] as JArray ?? new JArray())
            {
                var type = group["primary-type"]?.ToString();
                if (!string.IsNullOrWhiteSpace(type) &&
                    !string.Equals(type, "Album", StringComparison.OrdinalIgnoreCase)) continue;

                var title = group["title"]?.ToString();
                if (string.IsNullOrWhiteSpace(title)) continue;

                var releaseGroupId = group["id"]?.ToString();
                result.Albums.Add(new ArtistProviderAlbum
                {
                    Title = title!,
                    Year = ParseYear(group["first-release-date"]?.ToString()),
                    Provider = "MusicBrainz",
                    ArtworkUrl = _providers.CoverArtArchive && !string.IsNullOrWhiteSpace(releaseGroupId)
                        ? $"https://coverartarchive.org/release-group/{releaseGroupId}/front-250"
                        : null,
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            return new ArtistProviderResult { Provider = "MusicBrainz", Error = ex.Message };
        }
        finally
        {
            MusicBrainzGate.Release();
        }
    }

    private async Task<ArtistProviderResult?> ITunesArtistAsync(string artist, CancellationToken ct)
    {
        var searchUrl = "https://itunes.apple.com/search?term=" + Uri.EscapeDataString(artist) +
                        "&media=music&entity=musicArtist&limit=3";
        using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        using var response = await SendAsync("iTunes", request, false, ct).ConfigureAwait(false);
        if (response == null || !response.IsSuccessStatusCode) return null;

        var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var hit = (json["results"] as JArray)?.FirstOrDefault();
        if (hit == null) return null;

        var name = hit["artistName"]?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return null;

        var result = new ArtistProviderResult
        {
            Provider = "iTunes",
            Name = name!,
            Genre = hit["primaryGenreName"]?.ToString() ?? string.Empty,
        };

        var artistId = hit["artistId"]?.ToString();
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            var albumsUrl = "https://itunes.apple.com/lookup?id=" + artistId + "&entity=album&limit=200";
            using var albumsRequest = new HttpRequestMessage(HttpMethod.Get, albumsUrl);
            using var albumsResponse = await SendAsync("iTunes", albumsRequest, false, ct).ConfigureAwait(false);
            if (albumsResponse != null && albumsResponse.IsSuccessStatusCode)
            {
                var albumsJson = JObject.Parse(await albumsResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                foreach (var albumHit in albumsJson["results"] as JArray ?? new JArray())
                {
                    if (!string.Equals(albumHit["wrapperType"]?.ToString(), "collection", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.Equals(albumHit["collectionType"]?.ToString(), "Album", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var title = albumHit["collectionName"]?.ToString();
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    result.Albums.Add(new ArtistProviderAlbum
                    {
                        Title = title!,
                        Year = ParseYear(albumHit["releaseDate"]?.ToString()),
                        ArtworkUrl = UpscaleITunesArtwork(albumHit["artworkUrl100"]?.ToString()),
                        Provider = "iTunes",
                    });
                }
            }
        }

        return result;
    }

    private async Task<ArtistProviderResult?> DeezerArtistAsync(string artist, CancellationToken ct)
    {
        var searchUrl = "https://api.deezer.com/search/artist?q=" +
                        Uri.EscapeDataString(artist) + "&limit=3";
        using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        using var response = await SendAsync("Deezer", request, false, ct).ConfigureAwait(false);
        if (response == null || !response.IsSuccessStatusCode) return null;

        var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var hit = (json["data"] as JArray)?.FirstOrDefault();
        if (hit == null) return null;

        var name = hit["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return null;

        var result = new ArtistProviderResult
        {
            Provider = "Deezer",
            Name = name!,
            ArtworkUrl = hit["picture_xl"]?.ToString() ?? hit["picture_big"]?.ToString(),
        };

        var artistId = hit["id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            var albumsUrl = "https://api.deezer.com/artist/" + artistId + "/albums?limit=100";
            using var albumsRequest = new HttpRequestMessage(HttpMethod.Get, albumsUrl);
            using var albumsResponse = await SendAsync("Deezer", albumsRequest, false, ct).ConfigureAwait(false);
            if (albumsResponse != null && albumsResponse.IsSuccessStatusCode)
            {
                var albumsJson = JObject.Parse(await albumsResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                foreach (var albumHit in albumsJson["data"] as JArray ?? new JArray())
                {
                    var recordType = albumHit["record_type"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(recordType) &&
                        !string.Equals(recordType, "album", StringComparison.OrdinalIgnoreCase)) continue;

                    var title = albumHit["title"]?.ToString();
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    result.Albums.Add(new ArtistProviderAlbum
                    {
                        Title = title!,
                        Year = ParseYear(albumHit["release_date"]?.ToString()),
                        ArtworkUrl = albumHit["cover_big"]?.ToString() ?? albumHit["cover_medium"]?.ToString(),
                        Provider = "Deezer",
                    });
                }
            }
        }

        return result;
    }

    private async Task<ArtistProviderResult?> DiscogsArtistAsync(string artist, CancellationToken ct)
    {
        var url = "https://api.discogs.com/database/search?q=" + Uri.EscapeDataString(artist) +
                  "&type=artist&per_page=3";
        if (!string.IsNullOrWhiteSpace(_providers.DiscogsToken))
            url += "&token=" + Uri.EscapeDataString(_providers.DiscogsToken);

        using var searchRequest = new HttpRequestMessage(HttpMethod.Get, url);
        searchRequest.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
        searchRequest.Headers.Accept.ParseAdd("application/json");
        using var searchResponse = await SendAsync("Discogs", searchRequest, !string.IsNullOrWhiteSpace(_providers.DiscogsToken), ct).ConfigureAwait(false);
        if (searchResponse == null || !searchResponse.IsSuccessStatusCode) return null;

        var searchJson = JObject.Parse(await searchResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var hit = (searchJson["results"] as JArray)?.FirstOrDefault();
        var id = hit?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(id)) return null;

        var detailUrl = "https://api.discogs.com/artists/" + id;
        if (!string.IsNullOrWhiteSpace(_providers.DiscogsToken))
            detailUrl += "?token=" + Uri.EscapeDataString(_providers.DiscogsToken);
        using var detailRequest = new HttpRequestMessage(HttpMethod.Get, detailUrl);
        detailRequest.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
        detailRequest.Headers.Accept.ParseAdd("application/json");
        using var detailResponse = await SendAsync("Discogs", detailRequest, !string.IsNullOrWhiteSpace(_providers.DiscogsToken), ct).ConfigureAwait(false);
        if (detailResponse == null || !detailResponse.IsSuccessStatusCode) return null;

        var json = JObject.Parse(await detailResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var result = new ArtistProviderResult
        {
            Provider = "Discogs",
            Name = json["name"]?.ToString() ?? hit?["title"]?.ToString() ?? artist,
            Description = json["profile"]?.ToString() ?? string.Empty,
            ArtworkUrl = (json["images"] as JArray)?.FirstOrDefault()?["uri"]?.ToString(),
        };

        var releasesUrl = "https://api.discogs.com/artists/" + id + "/releases?per_page=100&sort=year&sort_order=desc";
        if (!string.IsNullOrWhiteSpace(_providers.DiscogsToken))
            releasesUrl += "&token=" + Uri.EscapeDataString(_providers.DiscogsToken);
        using var releasesRequest = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
        releasesRequest.Headers.UserAgent.ParseAdd(SongRequestDesktopV2Rewrite.BackendHttpClient.UserAgent);
        releasesRequest.Headers.Accept.ParseAdd("application/json");
        using var releasesResponse = await SendAsync("Discogs", releasesRequest, !string.IsNullOrWhiteSpace(_providers.DiscogsToken), ct).ConfigureAwait(false);
        if (releasesResponse != null && releasesResponse.IsSuccessStatusCode)
        {
            var releasesJson = JObject.Parse(await releasesResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            foreach (var release in releasesJson["releases"] as JArray ?? new JArray())
            {
                if (!string.Equals(release["role"]?.ToString(), "Main", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(release["type"]?.ToString(), "master", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(release["type"]?.ToString(), "release", StringComparison.OrdinalIgnoreCase)) continue;

                var title = release["title"]?.ToString();
                if (string.IsNullOrWhiteSpace(title)) continue;
                result.Albums.Add(new ArtistProviderAlbum
                {
                    Title = title!,
                    Year = ParseYear(release["year"]?.ToString()),
                    ArtworkUrl = release["thumb"]?.ToString(),
                    Provider = "Discogs",
                });
            }
        }

        return result;
    }

    // ── Matching / helpers ────────────────────────────────────────────────

    internal static double Score(
        MetadataCandidate candidate,
        string wantedTitle,
        string wantedArtist,
        TimeSpan wantedDuration,
        string wantedAlbum)
    {
        var titleScore = Similarity(candidate.Title, wantedTitle);
        var artistScore = string.IsNullOrWhiteSpace(wantedArtist) ||
                          string.Equals(wantedArtist, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? 0.5
            : Similarity(candidate.Artist, wantedArtist);
        var albumScore = string.IsNullOrWhiteSpace(wantedAlbum) ? 0.5 : Similarity(candidate.Album, wantedAlbum);

        double durationScore = 0.5;
        if (wantedDuration > TimeSpan.Zero && candidate.Duration > TimeSpan.Zero)
        {
            var diff = Math.Abs((candidate.Duration - wantedDuration).TotalSeconds);
            durationScore = diff <= 2 ? 1.0
                : diff <= 5 ? 0.8
                : diff <= 10 ? 0.5
                : diff <= 20 ? 0.25
                : 0.05;
        }

        return Math.Clamp(
            titleScore * 0.48 +
            artistScore * 0.28 +
            albumScore * 0.08 +
            durationScore * 0.16,
            0, 1);
    }

    internal static double Similarity(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 1;
        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
            return 0.84;

        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (leftTokens.Count == 0 || rightTokens.Count == 0) return 0;

        var intersection = leftTokens.Intersect(rightTokens).Count();
        var union = leftTokens.Union(rightTokens).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    internal static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).ToLowerInvariant();
        text = Regex.Replace(text, @"\s*[\(\[\{].*?[\)\]\}]\s*", " ");
        text = Regex.Replace(text, @"\b(official|video|audio|lyrics?|hd|hq|remaster(ed)?|feat\.?|ft\.?)\b", " ");
        text = Regex.Replace(text, @"[^\p{L}\p{N}\s]", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    internal static (string Artist, string Title) ParseFileName(string? filePath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(filePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return (string.Empty, string.Empty);

        name = Regex.Replace(name, @"\s*[\[\(](?:official|lyrics?|audio|video|hd|hq|4k).*?[\]\)]\s*", " ", RegexOptions.IgnoreCase);
        name = name.Replace('_', ' ').Trim();

        var parts = name.Split(" - ", 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2) return (parts[0], parts[1]);
        return (string.Empty, name);
    }

    private static bool LooksLikeFileName(string title)
        => title.IndexOfAny(new[] { '_', '\\', '/' }) >= 0 || Regex.IsMatch(title, @"\.[a-z0-9]{2,5}$", RegexOptions.IgnoreCase);

    private static uint ParseYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var match = Regex.Match(text, @"\b(19|20)\d{2}\b");
        return match.Success && uint.TryParse(match.Value, out var year) ? year : 0;
    }

    private static uint TryUInt(string? text)
        => uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string? UpscaleITunesArtwork(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return url.Replace("100x100bb", "600x600bb").Replace("100x100", "600x600");
    }

    private static string? NormalizeImageMime(string? mime)
    {
        mime = (mime ?? string.Empty).ToLowerInvariant();
        return mime switch
        {
            "image/jpeg" or "image/jpg" => "image/jpeg",
            "image/png" => "image/png",
            "image/gif" => "image/gif",
            "image/bmp" => "image/bmp",
            "image/webp" => "image/webp",
            _ => null,
        };
    }

    internal static string DetectImageMime(byte[] bytes)
        => DetectImageMimeOrNull(bytes) ?? "image/jpeg";

    internal static string? DetectImageMimeOrNull(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
            return "image/gif";
        if (bytes.Length >= 2 && bytes[0] == 'B' && bytes[1] == 'M')
            return "image/bmp";
        if (bytes.Length >= 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return "image/webp";
        return null;
    }
}
