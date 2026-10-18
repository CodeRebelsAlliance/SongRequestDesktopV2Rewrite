using System;
using System.Collections.Generic;
using System.Linq;
using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

/// <summary>
/// The recommendation engine described in <c>docs/library-data-models.md</c>: five
/// weighted signals, a reason/signal per suggestion, and no suggestions for songs that
/// are not playable library entries.
/// </summary>
public class LibraryRecommendationTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static LibrarySong Song(
        string id,
        string title,
        string artist = "Artist",
        string genre = "Pop",
        string album = "",
        int playCount = 0,
        TimeSpan? duration = null,
        FileStatus status = FileStatus.Present,
        DateTime? addedUtc = null,
        double daysSincePlayed = double.NaN)
    {
        return new LibrarySong
        {
            Id = id,
            Title = title,
            Artist = artist,
            Genre = genre,
            Album = album,
            Duration = duration ?? TimeSpan.FromMinutes(3),
            FilePath = "/music/" + id + ".mp3",
            FileStatus = status,
            PlayCount = playCount,
            DateAddedUtc = addedUtc ?? Now.AddDays(-30),
            LastPlayedUtc = double.IsNaN(daysSincePlayed) ? null : Now.AddDays(-daysSincePlayed)
        };
    }

    private static LibraryData Library(IEnumerable<LibrarySong> songs, params PlayHistoryEntry[] history) => new()
    {
        Songs = songs.ToList(),
        PlayHistory = history.ToList()
    };

    private static PlayHistoryEntry Play(string songId, double daysAgo, bool completed = true, string trigger = "queue") => new()
    {
        SongId = songId,
        PlayedUtc = Now.AddDays(-daysAgo),
        Completed = completed,
        Trigger = trigger
    };

    [Fact]
    public void NothingToSuggest_WhenLibraryIsEmpty()
    {
        Assert.Empty(RecommendationEngine.Recommend(Library(Array.Empty<LibrarySong>()), 10, Now));
    }

    [Fact]
    public void MissingFiles_AreNeverSuggested()
    {
        var data = Library(new[]
        {
            Song("present", "Playable"),
            Song("missing", "Gone", status: FileStatus.Missing),
            Song("unreadable", "Broken", status: FileStatus.Unreadable)
        });

        var recommended = RecommendationEngine.Recommend(data, 10, Now);

        Assert.Single(recommended);
        Assert.Equal("present", recommended[0].Song.Id);
    }

    [Fact]
    public void ScoresAreOrderedDescending_AndWithinZeroToOne()
    {
        var data = Library(new[]
        {
            Song("a", "Alpha", "Loved", "Pop"),
            Song("b", "Beta", "Unknown artist"),
            Song("c", "Gamma", "Loved", "Techno")
        }, Play("a", 1), Play("a", 2), Play("a", 3));

        var recommended = RecommendationEngine.Recommend(data, 10, Now);

        Assert.NotEmpty(recommended);
        for (int i = 0; i < recommended.Count; i++)
        {
            Assert.InRange(recommended[i].Score, 0.0, 1.0);
            if (i > 0) Assert.True(recommended[i - 1].Score >= recommended[i].Score, "scores must descend");
        }
    }

    [Fact]
    public void ArtistAffinity_RanksFollowUpSongsAboveStrangers()
    {
        var data = Library(new[]
        {
            Song("loved1", "Loved One", "Loved Artist", "Pop"),
            Song("loved2", "Loved Two", "Loved Artist", "Pop"),
            Song("other", "Something Else", "Other Artist", "Jazz")
        }, Play("loved1", 1), Play("loved1", 3), Play("loved1", 6));

        var recommended = RecommendationEngine.Recommend(data, 10, Now);
        var loved = recommended.First(r => r.Song.Id == "loved2");
        var other = recommended.First(r => r.Song.Id == "other");

        Assert.True(loved.Score > other.Score, $"played artist {loved.Score} should beat {other.Score}");
        Assert.Equal("artist_affinity", loved.Signal);
        Assert.Contains("Loved Artist", loved.Reason);
    }

    [Fact]
    public void GenreAffinity_Helps_WhenArtistAffinityIsUnknown()
    {
        var data = Library(new[]
        {
            Song("seed", "Seed", "Someone", "Techno"),
            Song("sameGenre", "Same Genre", "Different Artist", "Techno"),
            Song("different", "Different", "Another", "Country")
        }, Play("seed", 1), Play("seed", 2));

        var recommended = RecommendationEngine.Recommend(data, 10, Now);
        var same = recommended.First(r => r.Song.Id == "sameGenre");
        var different = recommended.First(r => r.Song.Id == "different");

        Assert.True(same.Score > different.Score,
            $"same genre {same.Score} should beat unrelated {different.Score}");
    }

    [Fact]
    public void FreshDiscovery_OnlyAppliesToUnplayedSongs_AndFavoursCompleteMetadata()
    {
        var data = Library(new[]
        {
            Song("complete", "Complete", "Artist", "Pop", "Album"),
            Song("bare", "Bare", "Unknown", "", "", duration: TimeSpan.Zero),
            Song("played", "Played", "Artist", "Pop", "Album", playCount: 4)
        });

        var recommended = RecommendationEngine.Recommend(data, 10, Now);
        var complete = recommended.First(r => r.Song.Id == "complete");
        var bare = recommended.First(r => r.Song.Id == "bare");
        var played = recommended.FirstOrDefault(r => r.Song.Id == "played");

        Assert.True(complete.Score > bare.Score, "complete metadata should rank higher");
        Assert.Equal("fresh_discovery", complete.Signal);
        Assert.Equal("New in your library", complete.Reason);
        Assert.True(played == null || played.Score < complete.Score, "already played is not fresh");
    }

    [Fact]
    public void RecentlyEnjoyed_SimilarityRaisesTheScore()
    {
        // The twin shares a tag with the seed but no artist/genre/album, so the only
        // extra signal a recent play of the seed can add is the similarity.
        var seed = Song("seed", "Seed Song", "Seed Artist", "Rock", "Seed Album");
        seed.Tags = new List<string> { "workout" };
        var twin = Song("twin", "Twin Song", "Other Artist", "Metal", "Other Album");
        twin.Tags = new List<string> { "workout" };

        var withHistory = RecommendationEngine.Recommend(
            Library(new[] { seed, twin }, Play("seed", 0.2)), 10, Now).First(r => r.Song.Id == "twin");
        var withoutHistory = RecommendationEngine.Recommend(
            Library(new[] { seed, twin }), 10, Now).First(r => r.Song.Id == "twin");

        Assert.True(withHistory.Score > withoutHistory.Score,
            $"the recent play must raise the score ({withHistory.Score} vs {withoutHistory.Score})");
    }

    [Fact]
    public void RecentlyEnjoyed_BecomesTheSignal_WhenItIsTheStrongestOne()
    {
        // Already played (so it is not "fresh") and unrelated apart from the tag, so the
        // similarity to the recent play is what the suggestion is based on.
        var seed = Song("seed", "Seed Song", "Seed Artist", "Rock", "Seed Album");
        seed.Tags = new List<string> { "workout" };
        var twin = Song("twin", "Twin Song", "Other Artist", "Metal", "Other Album", playCount: 2);
        twin.Tags = new List<string> { "workout" };

        var scored = RecommendationEngine.Recommend(
            Library(new[] { seed, twin }, Play("seed", 0.2)), 10, Now).First(r => r.Song.Id == "twin");

        Assert.Equal("recently_enjoyed", scored.Signal);
        Assert.Contains("Seed Song", scored.Reason);
    }

    [Fact]
    public void AffinityDecays_SoRecentPlaysMatterMoreThanOldOnes()
    {
        var data = Library(new[]
        {
            Song("recentArtist", "Recent", "Recent Artist", "Pop"),
            Song("recentArtist2", "Recent Follow", "Recent Artist", "Pop"),
            Song("oldArtist", "Old", "Old Artist", "Pop"),
            Song("oldArtist2", "Old Follow", "Old Artist", "Pop")
        },
        Play("recentArtist", 1), Play("recentArtist", 2),
        Play("oldArtist", 200), Play("oldArtist", 210));

        var recommended = RecommendationEngine.Recommend(data, 10, Now);
        var recent = recommended.First(r => r.Song.Id == "recentArtist2");
        var old = recommended.First(r => r.Song.Id == "oldArtist2");

        Assert.True(recent.Score > old.Score,
            $"recent affinity {recent.Score} should beat stale affinity {old.Score}");
    }

    [Fact]
    public void SongsTheEngineSuggested_AndWereSkipped_AreDiscounted()
    {
        var songs = new[]
        {
            Song("skipped", "Skipped Pick", "Same Artist", "Pop"),
            Song("fresh", "Fresh Pick", "Same Artist", "Pop")
        };
        var seed = Play("anchor", 1);

        var withoutEcho = Library(songs.Concat(new[] { Song("anchor", "Anchor", "Same Artist", "Pop") }), seed);
        var withEcho = Library(
            songs.Concat(new[] { Song("anchor", "Anchor", "Same Artist", "Pop") }),
            seed,
            Play("skipped", 0.5, completed: false, trigger: "recommendation"));

        var scoreWithout = RecommendationEngine.Recommend(withoutEcho, 10, Now).First(r => r.Song.Id == "skipped").Score;
        var scoreWith = RecommendationEngine.Recommend(withEcho, 10, Now).First(r => r.Song.Id == "skipped").Score;

        Assert.True(scoreWith < scoreWithout,
            $"a skipped recommendation ({scoreWith}) must score below the same song untouched ({scoreWithout})");
    }

    [Fact]
    public void TheMostRecentlyPlayedSongs_AreNotSuggestedAgain()
    {
        var data = Library(new[]
        {
            Song("justPlayed", "Just Played", "Artist", "Pop", playCount: 1, daysSincePlayed: 0),
            Song("older", "Older", "Artist", "Pop", playCount: 1, daysSincePlayed: 5),
            Song("never", "Never", "Artist", "Pop")
        });

        var recommended = RecommendationEngine.Recommend(data, 10, Now);

        Assert.DoesNotContain(recommended, r => r.Song.Id == "justPlayed");
        Assert.Contains(recommended, r => r.Song.Id == "never");
    }

    [Fact]
    public void Count_IsRespected_AndTheNewestWinsTies()
    {
        var songs = Enumerable.Range(0, 30)
            .Select(i => Song("s" + i, "Song " + i, addedUtc: Now.AddDays(-i)))
            .ToArray();

        var recommended = RecommendationEngine.Recommend(Library(songs), 5, Now);

        Assert.Equal(5, recommended.Count);
        // all scores tie (no history, identical metadata) -> newest additions first
        Assert.Equal(new[] { "s0", "s1", "s2", "s3", "s4" }, recommended.Select(r => r.Song.Id));
    }

    [Fact]
    public void EverySuggestion_CarriesADocumentedSignalAndReason()
    {
        var data = Library(new[]
        {
            Song("a", "Alpha", "Loved", "Pop"),
            Song("b", "Beta", "Loved", "Pop"),
            Song("c", "Gamma", "Other", "Jazz")
        }, Play("a", 1));

        var documented = new[] { "artist_affinity", "genre_affinity", "collaborative", "fresh_discovery", "recently_enjoyed" };

        foreach (var scored in RecommendationEngine.Recommend(data, 10, Now))
        {
            Assert.Contains(scored.Signal, documented);
            Assert.False(string.IsNullOrWhiteSpace(scored.Reason));
        }
    }
}
