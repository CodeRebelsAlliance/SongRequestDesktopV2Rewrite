using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
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
    class YoutubeService
    {
        private readonly YoutubeClient _youtubeClient;
        private readonly HttpClient _httpClient;
        public YoutubeService(IReadOnlyList<System.Net.Cookie> cookies)
        {
            _youtubeClient = new YoutubeClient();
            _httpClient = new HttpClient();
            _httpClient.BaseAddress = new Uri("https://api.lyrics.ovh/v1");
        }

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
            return query["v"];
        }

        public async Task<string> DownloadVideoAsync(string videoUrl, string downloadPath)
        {
            string videoId = GetYouTubeVideoId(videoUrl);
            if (string.IsNullOrEmpty(videoId))
            {
                throw new Exception("Invalid YouTube URL. Unable to extract video ID.");
            }

            var streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
            var audioStreams = streamManifest.GetAudioOnlyStreams();
            if (audioStreams == null || !audioStreams.Any())
            {
                throw new Exception("No suitable video stream found. The input stream collection is empty.");
            }

            var originalStreams = audioStreams
                .Where(a => a.AudioLanguage != null &&
                            a.AudioLanguage.ToString().IndexOf("Original", StringComparison.OrdinalIgnoreCase) >= 0);

            var selectedStream = audioStreams.GetWithHighestBitrate();

            if (originalStreams.Any())
            {
                selectedStream = originalStreams
                    .OrderByDescending(a => a.Bitrate)
                    .FirstOrDefault();
            }
            else
            {
                selectedStream = audioStreams.GetWithHighestBitrate();
            }


            if (selectedStream == null)
            {
                throw new Exception("No suitable audio stream found.");
            }

            string videoFileName = $"{videoId}.mp3";
            string filePath = Path.Combine(downloadPath, videoFileName);

            await _youtubeClient.Videos.Streams.DownloadAsync(selectedStream, filePath);

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

                string mp4FileName = $"{videoId}.mp3";
                string mp3FileName = $"{videoId}.mp3";
                string mp4FilePath = Path.Combine(downloadPath, mp4FileName);
                string mp3FilePath = Path.Combine(downloadPath, mp3FileName);

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
                MessageBox.Show($"Error downloading and converting video: {ex.Message}");
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

                using (Process process = Process.Start(startInfo))
                {
                    await process.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error converting MP4 to MP3: {ex.Message}");
                throw;
            }
        }

        public async Task<string> DownloadSubtitlesAndGetTextAsync(string videoUrl, string downloadPath)
        {
            try
            {
                string videoId = GetYouTubeVideoId(videoUrl);

                var captionsManifest = await _youtubeClient.Videos.ClosedCaptions.GetManifestAsync(videoId);
                string[] preferredLanguages = { "de", "en", "fr" };

                ClosedCaptionTrackInfo subtitleTrack = null; // Adjust language code as needed

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
                    throw new Exception("No subtitles available for this video.");
                }

                var captions = await _youtubeClient.Videos.ClosedCaptions.GetAsync(subtitleTrack);

                string subtitleText = string.Join("", captions.Captions);

                string language = subtitleTrack.Language.Name;
                string autoGenerated = subtitleTrack.IsAutoGenerated ? "Auto-generated" : "Human-generated";
                string lcid = "Lyrics: YouTube API\n";

                string fullText = $"{lcid}Language: {language}\nType: {autoGenerated}\n\n{subtitleText}";

                string subtitleFileName = $"{videoId}_subtitles.txt";
                string subtitleFilePath = Path.Combine(downloadPath, subtitleFileName);



                await File.WriteAllTextAsync(subtitleFilePath, fullText);

                // Read the text from the file
                string text = await File.ReadAllTextAsync(subtitleFilePath);

                // Delete the subtitles file after reading
                File.Delete(subtitleFilePath);

                return text;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error downloading subtitles and getting text: {ex.Message}");
                throw;
            }
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
                dynamic jsonResponse = Newtonsoft.Json.JsonConvert.DeserializeObject(responseBody);
                string lyrics = jsonResponse.lyrics;

                string completelyrics = "Lyrics: Lyrics.OVH API\n" + lyrics;

                return completelyrics;
            }
            catch (HttpRequestException ex)
            {
                try
                {
                    string desc = await GetVideoDescriptionAsync(id);

                    return "No Lyrics (YouTube Description)\n" + desc + "\n\nIf there aren't any lyrics in the description, please look them up by clicking the link below!";
                }
                catch (Exception exx)
                {
                    return "No Lyrics found, not even a description. Please look up your own lyrics using the button below!";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
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

        public async Task<string> GetThumbnailUrlAsync(string videoUrl)
        {
            var video = await _youtubeClient.Videos.GetAsync(videoUrl);
            return video.Thumbnails.GetWithHighestResolution().Url;
        }
    }
}
