using System;

namespace SongRequestDesktopV2Rewrite
{
    /// <summary>
    /// Metadata packet sent from sharer to server and from server to receiver
    /// </summary>
    public class MusicShareMetadata
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Lyrics { get; set; } = string.Empty;
        public bool HasSyncedLyrics { get; set; }
        public double ElapsedSeconds { get; set; }
        public double TotalSeconds { get; set; }
        public string? ThumbnailData { get; set; }  // Base64-encoded JPEG
        public long Timestamp { get; set; } // Unix timestamp for synchronization
    }

    /// <summary>
    /// Audio chunk packet
    /// </summary>
    public class AudioChunk
    {
        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public string Data { get; set; } = string.Empty; // Base64-encoded audio data

        [System.Text.Json.Serialization.JsonPropertyName("sampleRate")]
        public int SampleRate { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("channels")]
        public int Channels { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("sequenceNumber")]
        public int SequenceNumber { get; set; }
    }

    /// <summary>
    /// Session info
    /// </summary>
    public class ShareSession
    {
        public string SessionId { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime StartTime { get; set; }
        public int ListenerCount { get; set; }
    }

    /// <summary>
    /// Connection status
    /// </summary>
    public enum ShareStatus
    {
        Idle,
        Connecting,
        Connected,
        Streaming,
        Buffering,
        Error,
        Disconnected
    }
}
