using System;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>Now-playing snapshot raised on playback ticks (for external displays / MusicShare).</summary>
    public class NowPlayingEventArgs : EventArgs
    {
        public Song? Current { get; set; }
        public TimeSpan CurrentTime { get; set; }
        public TimeSpan TotalTime { get; set; }
    }
}
