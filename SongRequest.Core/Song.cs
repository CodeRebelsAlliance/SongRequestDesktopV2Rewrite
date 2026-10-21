using System;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// A queued/playable song. The model is intentionally WPF-free; the
    /// <see cref="thumbnail"/> is treated as an opaque platform object (a WPF
    /// <c>Image</c> while the UI is WPF-hosted) so the model can move to a
    /// cross-platform target unchanged.
    /// </summary>
    public class Song
    {
        public string Title { get; set; }
        public string Artist { get; set; }

        /// <summary>Opaque thumbnail (WPF <c>Image</c> for now; null when unavailable).</summary>
        public object? thumbnail { get; set; }

        public string length { get; set; }
        public TimeSpan Duration { get; set; }
        public string songPath { get; set; }

        // Estimated start time relative to now (for queue display)
        public TimeSpan EstimatedStart { get; set; }
        public string EstimatedStartDisplay { get; set; }

        // Loudness in LUFS for volume normalization (-1 means not calculated)
        public double PerceivedLoudness { get; set; } = -1;

        public Song(string title, string artist, object? thumbnail, string length, string songPath)
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

        public Song(string title, string artist, object? thumbnail, TimeSpan duration, string songPath)
        {
            Title = title;
            Artist = artist;
            this.thumbnail = thumbnail;
            Duration = duration;
            this.length = TimeDisplay.Clock(duration);
            this.songPath = songPath;
            EstimatedStart = TimeSpan.Zero;
            EstimatedStartDisplay = "00:00";
        }
    }
}
