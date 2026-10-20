using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.ClosedCaptions;
using YoutubeExplode.Videos.Streams;
using System.Linq;
using Newtonsoft.Json;
using System.IO;
using System.Threading.Tasks;

namespace SongRequestDesktopV2Rewrite
{
    public class YoutubeService
    {
        private YoutubeClient _youtubeClient = null!;
        private readonly HttpClient _httpClient;
        private IReadOnlyList<System.Net.Cookie> _cookies = Array.Empty<System.Net.Cookie>();
        public YoutubeService(IReadOnlyList<System.Net.Cookie> cookies)
        {
            SetCookies(cookies);
            _httpClient = new HttpClient();
            _httpClient.BaseAddress = new Uri("https://api.lyrics.ovh/v1");
        }

        /// <summary>Replaces the auth cookies and rebuilds the underlying YouTube client.</summary>
        public void SetCookies(IReadOnlyList<System.Net.Cookie>? cookies)
        {
            var cookieContainer = new System.Net.CookieContainer();
            if (cookies != null)
            {
                foreach (var cookie in cookies)
                {
                    try { cookieContainer.Add(cookie); }
                    catch { }
                }
            }
            var handler = new HttpClientHandler { CookieContainer = cookieContainer, UseCookies = true };
            _youtubeClient = new YoutubeClient(new HttpClient(handler));
            _cookies = cookies ?? Array.Empty<System.Net.Cookie>();
        }

        public IReadOnlyList<System.Net.Cookie> Cookies => _cookies;

        public static string ExtractVideoId(string url)
        {
            // Define regex patterns for different types of YouTube URLs
            string[] patterns = new string[]
            {
            @"(?:https?://)?(?:www\.)?youtube\.com/watch\?v=([a-zA-Z0-9_-]{11})",  // Standard URL
            @"(?:https?://)?youtu\.be/([a-zA-Z0-9_-]{11})",                       // Shortened URL
            @"(?:https?://)?(?:www\.)?youtube\.com/embed/([a-zA-Z0-9_-]{11})",     // Embed URL
            @"(?:https?://)?(?:www\.)?youtube\.com/v/([a-zA-Z0-9_-]{11})",         // /v/ URL
            @"(?:https?://)?(?:www\.)?youtube\.com/e/([a-zA-Z0-9_-]{11})",         // /e/ URL
            @"(?:https?://)?(?:www\.)?youtube\.com/shorts/([a-zA-Z0-9_-]{11})",    // /shorts/ URL
            @"(?:https?://)?(?:www\.)?youtube\.com/live/([a-zA-Z0-9_-]{11})",      // /live/ URL
            @"(?:https?://)?(?:www\.)?music\.youtube\.com/watch\?v=([a-zA-Z0-9_-]{11})",  // Music URL
            @"(?:https?://)?m\.youtube\.com/watch\?app=desktop&v=([a-zA-Z0-9_-]{11})"     // Mobile URL
            };

            // Iterate over the patterns and search for a match
            foreach (string pattern in patterns)
            {
                var match = Regex.Match(url, pattern);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            // Return null if no match is found
            return "404";
        }

        public static string FormatSeconds(int totalSeconds)
        {
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;

            // Use PadLeft to ensure two-digit formatting for both minutes and seconds
            string formattedTime = minutes.ToString().PadLeft(2, '0') + ":" + seconds.ToString().PadLeft(2, '0');

            return formattedTime;
        }

        public async Task<(string Title, TimeSpan Length, string Creator)> GetVideoMetadataAsync(string videoUrl)
        {
            var video = await _youtubeClient.Videos.GetAsync(videoUrl);

            string title = video.Title;
            TimeSpan length = video.Duration ?? TimeSpan.Zero;
            string creator = video.Author.ChannelTitle;

            return (title, length, creator);
        }

        public string GetYouTubeVideoId(string url)
        {
            var uri = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            return query["v"] ?? string.Empty;
        }

        /// <summary>
        /// Picks the audio stream to download: the preferred container first (mobile
        /// wants AAC/m4a so the platform decoders can play it), then the highest
        /// bitrate. Language only breaks ties, so a lower-bitrate "original" track
        /// can never win over a higher-quality stream (the old order did that).
        /// </summary>
        private static IAudioStreamInfo? SelectAudioStream(IEnumerable<IAudioStreamInfo> audioStreams)
        {
            var streams = audioStreams.ToList();
            if (streams.Count == 0) return null;

            if (!string.IsNullOrEmpty(DownloadedAudio.PreferredContainer))
            {
                var preferred = streams
                    .Where(s => DownloadedAudio.MatchesPreferredContainer(s.Container.Name))
                    .OrderByDescending(s => s.Bitrate)
                    .ThenByDescending(IsOriginalLanguage)
                    .FirstOrDefault();

                if (preferred != null) return preferred;
            }

            return streams
                .OrderByDescending(s => s.Bitrate)
                .ThenByDescending(IsOriginalLanguage)
                .FirstOrDefault();
        }

        private static bool IsOriginalLanguage(IAudioStreamInfo stream)
            => stream.AudioLanguage?.ToString() is { } language &&
               language.IndexOf("Original", StringComparison.OrdinalIgnoreCase) >= 0;

        public async Task<string> DownloadVideoAsync(string videoUrl, string downloadPath)
        {
            string videoId = GetYouTubeVideoId(videoUrl);
            if (string.IsNullOrEmpty(videoId))
            {
                throw new Exception("Invalid YouTube URL. Unable to extract video ID.");
            }

            // Idempotent: an existing download (in any container we have used) is
            // returned as is instead of being fetched and overwritten again.
            var existing = DownloadedAudio.Existing(videoId, downloadPath);
            if (DownloadedAudio.IsComplete(existing)) return existing!;

            var streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
            var audioStreams = streamManifest.GetAudioOnlyStreams();
            if (audioStreams == null || !audioStreams.Any())
            {
                throw new Exception("No suitable video stream found. The input stream collection is empty.");
            }

            var selectedStream = SelectAudioStream(audioStreams)
                ?? throw new Exception("No suitable audio stream found.");

            string filePath = DownloadedAudio.PathFor(videoId, downloadPath);

            await _youtubeClient.Videos.Streams.DownloadAsync(selectedStream, filePath);

            return filePath;
        }

        public async Task<string> DownloadVideoWithProgressAsync(string videoUrl, string downloadPath, IProgress<double>? progress = null)
        {
            string videoId = GetYouTubeVideoId(videoUrl);
            if (string.IsNullOrEmpty(videoId))
            {
                throw new Exception("Invalid YouTube URL. Unable to extract video ID.");
            }

            try
            {
                return await DownloadDirectAsync(videoId, downloadPath, progress).ConfigureAwait(false);
            }
            catch (YoutubeExplode.Exceptions.YoutubeExplodeException) when (YtDlpDownloader.IsAvailable)
            {
                // YouTube refuses some videos to the plain web client ("Video is not
                // available"), regardless of cookies. yt-dlp solves the current player
                // challenges, so let it fetch the audio into the same path.
                var targetPath = DownloadedAudio.PathFor(videoId, downloadPath);
                var error = await YtDlpDownloader.DownloadAudioAsync(videoId, targetPath, progress).ConfigureAwait(false);
                if (error != null) throw new Exception(error);
                return targetPath;
            }
        }

        private async Task<string> DownloadDirectAsync(string videoId, string downloadPath, IProgress<double>? progress)
        {
            var existing = DownloadedAudio.Existing(videoId, downloadPath);
            if (DownloadedAudio.IsComplete(existing))
            {
                progress?.Report(1);
                return existing!;
            }

            var streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
            var audioStreams = streamManifest.GetAudioOnlyStreams();
            if (audioStreams == null || !audioStreams.Any())
            {
                throw new Exception("No suitable video stream found.");
            }

            var selectedStream = SelectAudioStream(audioStreams)
                ?? throw new Exception("No suitable audio stream found.");

            string filePath = DownloadedAudio.PathFor(videoId, downloadPath);

            await _youtubeClient.Videos.Streams.DownloadAsync(selectedStream, filePath, progress);

            return filePath;
        }

        public async Task<string> DownloadAndConvertVideoAsync(string videoUrl, string downloadPath)
        {
            try
            {
                string videoId = GetYouTubeVideoId(videoUrl);
                if (string.IsNullOrEmpty(videoId))
                {
                    throw new Exception("Invalid YouTube URL. Unable to extract video ID.");
                }

                string mp4FilePath = DownloadedAudio.PathFor(videoId, downloadPath);
                string mp3FilePath = mp4FilePath;

                await DownloadVideoAsync(videoUrl, downloadPath);
                //await ConvertMp4ToMp3WithFFmpeg(mp4FilePath, mp3FilePath);
                //if (File.Exists(mp3FilePath))
                //{
                //    File.Delete(mp3FilePath);
                //}

                return mp3FilePath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error downloading and converting video: {ex.Message}");
                throw;
            }
        }

        private async Task ConvertMp4ToMp3WithFFmpeg(string inputFilePath, string outputFilePath)
        {
            try
            {
                string ffmpegPath = @"ffmpeg\ffmpeg.exe";
                string arguments = $"-i \"{inputFilePath}\" -vn -acodec libmp3lame -q:a 2 \"{outputFilePath}\"";

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null) await process.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error converting MP4 to MP3: {ex.Message}");
                throw;
            }
        }

        public sealed class SubtitleFetchResult
        {
            public string PlainText { get; set; } = string.Empty;
            public string TimedLrcText { get; set; } = string.Empty;
        }

        public async Task<SubtitleFetchResult?> TryGetSubtitlesAsync(string videoUrl)
        {
            try
            {
                string videoId = GetYouTubeVideoId(videoUrl);
                if (string.IsNullOrWhiteSpace(videoId))
                {
                    return null;
                }

                var captionsManifest = await _youtubeClient.Videos.ClosedCaptions.GetManifestAsync(videoId);
                string[] preferredLanguages = { "de", "en", "fr" };

                ClosedCaptionTrackInfo? subtitleTrack = null;

                foreach (var lang in preferredLanguages)
                {
                    subtitleTrack = captionsManifest.TryGetByLanguage(lang);
                    if (subtitleTrack != null)
                        break;
                }

                // If no preferred language tracks are found, use any available track
                if (subtitleTrack == null)
                {
                    subtitleTrack = captionsManifest.Tracks.FirstOrDefault();
                }

                if (subtitleTrack == null)
                {
                    return null;
                }

                var captions = await _youtubeClient.Videos.ClosedCaptions.GetAsync(subtitleTrack);

                var captionItems = captions.Captions
                    .Select(c => new { Offset = c.Offset, Text = c.Text?.Trim() })
                    .Where(c => !string.IsNullOrWhiteSpace(c.Text))
                    .ToList();

                string subtitleText = string.Join(
                    Environment.NewLine,
                    captionItems.Select(c => c.Text));

                if (string.IsNullOrWhiteSpace(subtitleText))
                {
                    return null;
                }

                string timedText = string.Join(
                    Environment.NewLine,
                    captionItems.Select(c => $"[{FormatLrcTimestamp(c.Offset)}]{c.Text}"));

                return new SubtitleFetchResult
                {
                    PlainText = subtitleText,
                    TimedLrcText = timedText
                };
            }
            catch
            {
                return null;
            }
        }

        public async Task<string?> TryGetSubtitlesTextAsync(string videoUrl)
        {
            var subtitleResult = await TryGetSubtitlesAsync(videoUrl);
            if (subtitleResult == null || string.IsNullOrWhiteSpace(subtitleResult.PlainText))
            {
                return null;
            }

            return subtitleResult.PlainText;
        }

        public async Task<string> DownloadSubtitlesAndGetTextAsync(string videoUrl, string downloadPath)
        {
            var subtitleText = await TryGetSubtitlesTextAsync(videoUrl);
            if (string.IsNullOrWhiteSpace(subtitleText))
            {
                throw new Exception("No subtitles available for this video.");
            }

            return subtitleText;
        }

        private static string FormatLrcTimestamp(TimeSpan offset)
        {
            int minutes = (int)offset.TotalMinutes;
            int seconds = offset.Seconds;
            int centiseconds = offset.Milliseconds / 10;
            return $"{minutes:D2}:{seconds:D2}.{centiseconds:D2}";
        }



        public async Task<string> GetLyricsAsync(string artist, string title, string id)
        {
            try
            {
                string preparedTitle = Uri.EscapeDataString(title);
                string preparedArtist = Uri.EscapeDataString(artist);
                string requestUri = $"/{preparedArtist}/{preparedTitle}";

                // Log the constructed URL to debu

                HttpResponseMessage response = await _httpClient.GetAsync(_httpClient.BaseAddress + requestUri);
                response.EnsureSuccessStatusCode();

                string responseBody = await response.Content.ReadAsStringAsync();

                // Parse JSON response to get lyrics
                dynamic? jsonResponse = Newtonsoft.Json.JsonConvert.DeserializeObject(responseBody);
                string lyrics = (string?)jsonResponse?.lyrics ?? string.Empty;

                string completelyrics = "Lyrics: Lyrics.OVH API\n" + lyrics;

                return completelyrics;
            }
            catch (HttpRequestException)
            {
                try
                {
                    string desc = await GetVideoDescriptionAsync(id);

                    return "No Lyrics (YouTube Description)\n" + desc + "\n\nIf there aren't any lyrics in the description, please look them up by clicking the link below!";
                }
                catch (Exception)
                {
                    return "No Lyrics found, not even a description. Please look up your own lyrics using the button below!";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }

        public void SearchLyricsOnDuckDuckGo(string artist, string title)
        {
            string searchQuery = $"{artist} {title} lyrics";
            //LyricsLookup searchForm = new LyricsLookup(searchQuery);
            //searchForm.Show();
        }

        public async Task<string> GetVideoDescriptionAsync(string videoUrl)
        {
            var video = await _youtubeClient.Videos.GetAsync(videoUrl);
            return video.Description;
        }

        /// <summary>
        /// The well-known thumbnail URL for a video id. Thumbnails live on a static
        /// image host, so this needs no YouTube metadata request — the old version
        /// called <c>Videos.GetAsync</c> for *every* thumbnail, turning a grid of
        /// cards into a burst of full metadata requests.
        /// </summary>
        public static string GetThumbnailUrl(string videoId, bool highQuality = true)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return string.Empty;
            return highQuality
                ? $"https://i.ytimg.com/vi/{videoId}/maxresdefault.jpg"
                : $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
        }

        /// <summary>
        /// Fetches a thumbnail's bytes, preferring the high-resolution image and
        /// falling back to the always-present 480x360 version. Null when neither works.
        /// </summary>
        public async Task<byte[]?> DownloadThumbnailBytesAsync(string videoId, System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(videoId)) return null;

            foreach (var url in new[] { GetThumbnailUrl(videoId, true), GetThumbnailUrl(videoId, false) })
            {
                try
                {
                    var bytes = await _httpClient.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                    if (bytes.Length > 0) return bytes;
                }
                catch
                {
                    // 404 for videos without a max-resolution image: try the next one.
                }
            }

            return null;
        }

        public Task<string> GetThumbnailUrlAsync(string videoUrl)
        {
            var videoId = GetYouTubeVideoId(videoUrl);
            return Task.FromResult(string.IsNullOrWhiteSpace(videoId) ? string.Empty : GetThumbnailUrl(videoId));
        }

        public async Task<List<SearchResult>> SearchAsync(string query, int maxResults = 10)
        {
            try
            {
                var searchResults = await _youtubeClient.Search.GetVideosAsync(query).CollectAsync(maxResults);
                return searchResults.Select(video => new SearchResult
                {
                    VideoId = video.Id,
                    Title = video.Title,
                    Author = video.Author.ChannelTitle,
                    Duration = video.Duration
                }).ToList();
            }
            catch (Exception ex)
            {
                throw new Exception($"Search failed: {ex.Message}");
            }
        }
    }

    public class SearchResult
    {
        public string VideoId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public TimeSpan? Duration { get; set; }
    }
}
