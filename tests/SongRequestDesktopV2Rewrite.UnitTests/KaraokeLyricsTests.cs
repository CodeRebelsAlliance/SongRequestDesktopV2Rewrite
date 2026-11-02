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
        Assert.True(result.Lines[0].Syllables.Count >= result.Lines[0].Words.Count);
        Assert.True(result.Lines[1].Syllables.Count >= result.Lines[1].Words.Count);

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

                double previousSyllable = word.Start - 0.001;
                Assert.NotEmpty(word.Syllables);
                foreach (var syllable in word.Syllables)
                {
                    Assert.True(syllable.End > syllable.Start);
                    Assert.True(syllable.Start >= previousSyllable - 0.001);
                    Assert.True(syllable.End <= word.End + 0.05);
                    previousSyllable = syllable.Start;
                }
            }

            double previousLineSyllable = line.Time - 0.001;
            foreach (var syllable in line.Syllables)
            {
                Assert.True(syllable.Start >= previousLineSyllable - 0.001);
                Assert.True(syllable.End <= line.End + 0.05);
                previousLineSyllable = syllable.Start;
            }
        }

        Assert.All(result.Pauses, pause =>
        {
            Assert.True(pause.Duration >= 3.5);
            Assert.Equal(pause.End - pause.Start, pause.Duration, 2);
        });
    }

    [Fact]
    public void Build_PreservesEmptyTimedLinesSoUiIndexesStayAligned()
    {
        var lines = new List<(TimeSpan Time, string Text)>
        {
            (TimeSpan.Zero, ""),
            (TimeSpan.FromSeconds(5), "hello world"),
            (TimeSpan.FromSeconds(12), ""),
            (TimeSpan.FromSeconds(14), "last line")
        };

        var result = KaraokeLyrics.Build(lines, null, 20);

        Assert.Equal(4, result.Lines.Count);
        Assert.Empty(result.Lines[0].Words);
        Assert.Equal("hello", result.Lines[1].Words[0].Text);
        Assert.Empty(result.Lines[2].Words);
        Assert.Equal("last", result.Lines[3].Words[0].Text);
    }

    [Fact]
    public void Build_SplitsWordsIntoSyllablesWithoutChangingTheText()
    {
        var lines = new List<(TimeSpan Time, string Text)>
        {
            (TimeSpan.Zero, "beautiful table make")
        };

        var result = KaraokeLyrics.Build(lines, null, 10);
        var line = Assert.Single(result.Lines);
        Assert.Equal(3, line.Words.Count);

        Assert.Equal(new[] { "beau", "ti", "ful" },
            line.Words[0].Syllables.Select(s => s.Text).ToArray());
        Assert.Equal(new[] { "ta", "ble" },
            line.Words[1].Syllables.Select(s => s.Text).ToArray());
        Assert.Single(line.Words[2].Syllables);

        foreach (var word in line.Words)
        {
            Assert.Equal(word.Text, string.Concat(word.Syllables.Select(s => s.Text)));
        }

        Assert.Equal(line.Words.Sum(w => w.Syllables.Count), line.Syllables.Count);
        Assert.Equal("beau", line.Syllables[0].Text);
        Assert.Equal(0, line.Syllables[0].WordIndex);
        Assert.Equal(2, line.Syllables[^1].WordIndex);
    }

    [Theory]
    [InlineData("hello", "hel|lo")]
    [InlineData("hello,", "hel|lo,")]
    [InlineData("world!", "world!")]
    [InlineData("(hello)", "(hel|lo)")]
    [InlineData("rock-n-roll", "rock-|n-|roll")]
    [InlineData("café", "ca|fé")]
    [InlineData("world", "world")]
    [InlineData("singing", "sin|ging")]
    [InlineData("makes", "makes")]
    [InlineData("roses", "ro|ses")]
    [InlineData("walked", "walked")]
    [InlineData("wanted", "wan|ted")]
    [InlineData("beautiful", "beau|ti|ful")]
    [InlineData("table", "ta|ble")]
    [InlineData("little", "lit|tle")]
    [InlineData("simple", "sim|ple")]
    [InlineData("monster", "mon|ster")]
    [InlineData("winter", "win|ter")]
    [InlineData("people", "peo|ple")]
    [InlineData("fire", "fire")]
    [InlineData("care", "care")]
    [InlineData("while", "while")]
    [InlineData("smile", "smile")]
    public void Build_SyllabifiesCommonLyricWords(string word, string expected)
    {
        var result = KaraokeLyrics.Build(
            new[] { (TimeSpan.Zero, word) }, null, 5);

        var line = Assert.Single(result.Lines);
        var wordResult = Assert.Single(line.Words);
        Assert.Equal(expected, string.Join("|", wordResult.Syllables.Select(s => s.Text)));
    }

    [Fact]
    public void Build_SnapsLineStartsAndTrimsLineEndsToVocalActivity()
    {
        var lines = new List<(TimeSpan Time, string Text)>
        {
            (TimeSpan.FromSeconds(1), "hello world this is a long line"),
            (TimeSpan.FromSeconds(6), "next line")
        };

        var analysis = new SongAnalysis
        {
            SongPath = "/music/sync.mp3",
            DurationSeconds = 10,
            Timeline = new List<SongAnalysisFrame>()
        };

        for (int i = 0; i <= 70; i++)
        {
            double time = i * 0.1;
            bool active = time >= 1.15 && time <= 3.0;
            analysis.Timeline.Add(new SongAnalysisFrame
            {
                Time = time,
                RmsDb = active ? -20 : -44,
                Novelty = Math.Abs(time - 1.15) < 0.011 ? 0.95 : (active ? 0.05 : 0.01),
                MidEnergy = active ? 0.65 : 0.10,
                HighEnergy = active ? 0.25 : 0.05
            });
        }

        var result = KaraokeLyrics.Build(lines, analysis, analysis.DurationSeconds);
        var line = result.Lines[0];

        // The 1.00 s LRC cue is moved onto the first vocal frame at 1.20 s.
        Assert.InRange(line.Time, 1.19, 1.21);

        // The wipe ends with the voice (3.0 s + tail), not at the next cue.
        Assert.InRange(line.End, 2.95, 3.20);
        Assert.True(line.Words[^1].End <= line.End + 0.05);
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
