using System;
using System.Windows.Controls;

namespace SongRequestDesktopV2Rewrite
{
    public class Song
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public Image thumbnail { get; set; }
        public string length { get; set; }
        public TimeSpan Duration { get; set; }
        public string songPath { get; set; }

        // Estimated start time relative to now (for queue display)
        public TimeSpan EstimatedStart { get; set; }
        public string EstimatedStartDisplay { get; set; }

        // Perceived loudness in LUFS for normalization
        public double? PerceivedLoudness { get; set; }

        public Song(string title, string artist, Image thumbnail, string length, string songPath)
        {
            Title = title;
            Artist = artist;
            this.thumbnail = thumbnail;
            this.length = length;
            this.songPath = songPath;
            Duration = TimeSpan.Zero;
            EstimatedStart = TimeSpan.Zero;
            EstimatedStartDisplay = "00:00";
        }

        public Song(string title, string artist, Image thumbnail, TimeSpan duration, string songPath)
        {
            Title = title;
            Artist = artist;
            this.thumbnail = thumbnail;
            Duration = duration;
            this.length = duration.ToString(@"mm\:ss");
            this.songPath = songPath;
            EstimatedStart = TimeSpan.Zero;
            EstimatedStartDisplay = "00:00";
        }
    }
}