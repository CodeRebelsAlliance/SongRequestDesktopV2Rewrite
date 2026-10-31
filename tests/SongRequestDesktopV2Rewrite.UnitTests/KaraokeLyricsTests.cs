using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class KaraokeLyricsTests
{
    [Fact]
    public void Build_CreatesMonotonicWordTimingsAndPauses()
    {
        var lines = new List<(TimeSpan Time, string Text)>
        {
            (TimeSpan.FromSeconds(1), "hello world"),
            (TimeSpan.FromSeconds(8), "this is a longer line of words"),
            (TimeSpan.FromSeconds(16), "ending line")
        };

        var analysis = new SongAnalysis
        {
            SongPath = "/music/karaoke.mp3",
            DurationSeconds = 24,
            Timeline = new List<SongAnalysisFrame>()
        };
        for (int i = 0; i < 48; i++)
        {
            analysis.Timeline.Add(new SongAnalysisFrame
            {
                Time = i * 0.5,
                RmsDb = -20,
                Novelty = i == 8 ? 0.9 : (i % 4 == 0 ? 0.2 : 0.05)
            });
        }

        var result = KaraokeLyrics.Build(lines, analysis, analysis.DurationSeconds);

        Assert.Equal(3, result.Lines.Count);
        Assert.Equal(2, result.Lines[0].Words.Count);
        Assert.Equal(7, result.Lines[1].Words.Count);
        Assert.NotEmpty(result.Pauses);

        foreach (var line in result.Lines)
        {
            Assert.True(line.End > line.Time);
            double previous = line.Time;
            foreach (var word in line.Words)
            {
                Assert.True(word.End > word.Start);
                Assert.True(word.Start >= previous - 0.001);
                Assert.True(word.End <= line.End + 0.05);
                previous = word.Start;
            }
        }

        Assert.All(result.Pauses, pause =>
        {
            Assert.True(pause.Duration >= 3.5);
            Assert.Equal(pause.End - pause.Start, pause.Duration, 2);
        });
    }

    [Fact]
    public void Build_StillProducesWordTimingsWithoutAnalysis()
    {
        var lines = new List<(TimeSpan Time, string Text)>
        {
            (TimeSpan.Zero, "one two three four")
        };

        var result = KaraokeLyrics.Build(lines, null, 10);

        Assert.Single(result.Lines);
        Assert.Equal(4, result.Lines[0].Words.Count);
        Assert.Equal("one", result.Lines[0].Words[0].Text);
        Assert.True(result.Lines[0].Words[^1].End > result.Lines[0].Words[^1].Start);
    }

    [Fact]
    public void KaraokeModeSetting_IsExposedInTheConfig()
    {
        var config = new Config { KaraokeMode = true };
        Assert.True(config.KaraokeMode);
        config.KaraokeMode = false;
        Assert.False(config.KaraokeMode);
    }
}
