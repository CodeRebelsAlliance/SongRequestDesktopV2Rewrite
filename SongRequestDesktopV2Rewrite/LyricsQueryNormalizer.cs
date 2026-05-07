using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SongRequestDesktopV2Rewrite
{
    internal static class LyricsQueryNormalizer
    {
        internal readonly struct LyricsQuery
        {
            public LyricsQuery(string artist, string title, bool isSentInSong)
            {
                Artist = artist;
                Title = title;
                IsSentInSong = isSentInSong;
            }

            public string Artist { get; }
            public string Title { get; }
            public bool IsSentInSong { get; }
        }

        private static readonly Regex BracketNoiseRegex = new Regex(
            @"[\(\[\{][^\)\]\}]{0,120}(official|offiziell|offizielles?|musikvideo|music\s*video|videoclip|video\s*clip|clip\s*official|lyric|lyrics|songtext|mit\s*text|text|subtitle|subtitles|audio|visualizer|karaoke|hq|hd|4k|8k|remaster(ed)?|radio\s*edit|extended|version)[^\)\]\}]{0,120}[\)\]\}]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingNoiseRegex = new Regex(
            @"\s[-:|]\s*(official.*|offiziell.*|lyrics?.*|songtext.*|lyric.*|audio.*|video.*|visualizer.*|karaoke.*|hd.*|4k.*|8k.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex WhitespaceRegex = new Regex(@"\s{2,}", RegexOptions.Compiled);
        private static readonly Regex SeparatorRegex = new Regex(@"\s[-–—]\s", RegexOptions.Compiled);

        public static LyricsQuery Build(Song song)
        {
            var rawArtist = (song?.Artist ?? string.Empty).Replace(" - Topic", string.Empty).Trim();
            var rawTitle = (song?.Title ?? string.Empty).Trim();
            var isSentIn = IsSentInSong(song?.songPath);

            if (!isSentIn)
            {
                return new LyricsQuery(rawArtist, rawTitle, false);
            }

            string derivedArtist = rawArtist;
            string derivedTitle = CleanTitle(rawTitle);

            var splitParts = SeparatorRegex.Split(rawTitle).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (splitParts.Length >= 2)
            {
                var candidateArtist = CleanArtist(splitParts[0]);
                var candidateTitle = CleanTitle(string.Join(" - ", splitParts.Skip(1)));

                if (!string.IsNullOrWhiteSpace(candidateArtist) && !string.IsNullOrWhiteSpace(candidateTitle))
                {
                    derivedArtist = candidateArtist;
                    derivedTitle = candidateTitle;
                }
            }

            derivedArtist = CleanArtist(derivedArtist);
            if (string.IsNullOrWhiteSpace(derivedArtist))
            {
                derivedArtist = rawArtist;
            }

            if (string.IsNullOrWhiteSpace(derivedTitle))
            {
                derivedTitle = rawTitle;
            }

            return new LyricsQuery(derivedArtist, derivedTitle, true);
        }

        private static string CleanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            var cleaned = BracketNoiseRegex.Replace(title, " ");
            cleaned = TrailingNoiseRegex.Replace(cleaned, string.Empty);
            cleaned = cleaned.Replace("“", "\"").Replace("”", "\"").Trim();
            cleaned = WhitespaceRegex.Replace(cleaned, " ");
            return cleaned.Trim('-', '–', '—', '|', ':', ' ');
        }

        private static string CleanArtist(string artist)
        {
            if (string.IsNullOrWhiteSpace(artist)) return string.Empty;
            var cleaned = BracketNoiseRegex.Replace(artist, " ");
            cleaned = TrailingNoiseRegex.Replace(cleaned, string.Empty);
            cleaned = WhitespaceRegex.Replace(cleaned, " ");
            return cleaned.Trim('-', '–', '—', '|', ':', ' ');
        }

        private static bool IsSentInSong(string? songPath)
        {
            if (string.IsNullOrWhiteSpace(songPath)) return false;

            try
            {
                var fullSongPath = Path.GetFullPath(songPath);
                var fullDownloadPath = Path.GetFullPath(YoutubeForm.downloadPath);
                return fullSongPath.StartsWith(fullDownloadPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
