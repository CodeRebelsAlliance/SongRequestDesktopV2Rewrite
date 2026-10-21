using System.Collections.Generic;
using System.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class MusicSharePresentationTests
{
    [Fact]
    public void BuildLyrics_PlainText_IsSentAsLines()
    {
        var payload = MusicSharePresentation.BuildLyrics(new MusicShareMetadata
        {
            Lyrics = "First line\nSecond line",
            HasSyncedLyrics = false,
        });

        var lines = Assert.IsAssignableFrom<IEnumerable<string>>(payload["plainLyrics"]).ToList();
        Assert.Equal(new[] { "First line", "Second line" }, lines);
        Assert.False((bool)payload["hasSynced"]);
    }

    [Fact]
    public void BuildLyrics_SyncedText_IsParsedIntoTimeAndText()
    {
        var payload = MusicSharePresentation.BuildLyrics(new MusicShareMetadata
        {
            Lyrics = "[00:12.50]First\n[00:15.80]Second",
            HasSyncedLyrics = true,
        });

        var lines = Assert.IsAssignableFrom<IEnumerable<object>>(payload["syncedLyrics"]).ToList();
        Assert.Equal(2, lines.Count);
        Assert.True((bool)payload["hasSynced"]);
    }

    [Fact]
    public void ThumbnailDataUrl_DetectsPngAndJpegSignatures()
    {
        Assert.StartsWith("data:image/png;base64,", MusicSharePresentation.ThumbnailDataUrl("iVBORw0KGgo"));
        Assert.StartsWith("data:image/jpeg;base64,", MusicSharePresentation.ThumbnailDataUrl("/9j/4AAQ"));
        Assert.Null(MusicSharePresentation.ThumbnailDataUrl(null));
    }
}
