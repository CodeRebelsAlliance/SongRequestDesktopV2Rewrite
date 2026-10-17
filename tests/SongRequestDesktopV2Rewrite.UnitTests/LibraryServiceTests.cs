using SongRequestDesktopV2Rewrite;
using Xunit;

namespace SongRequestDesktopV2Rewrite.UnitTests;

public class LibraryServiceTests
{
    [Fact]
    public void CreatePlaylist_AddsAndPersists()
    {
        TestSupport.ResetLibrary();
        using var lib = new LibraryService();

        var playlist = lib.CreatePlaylist("Roadtrip", "\U0001F697");

        Assert.Equal("Roadtrip", playlist.Name);
        Assert.Single(lib.GetPlaylistsSummary());
        Assert.NotNull(lib.GetPlaylist(playlist.Id));
    }

    [Fact]
    public void CreatePlaylist_BlankName_DefaultsToUntitled()
    {
        TestSupport.ResetLibrary();
        using var lib = new LibraryService();

        var playlist = lib.CreatePlaylist("   ");

        Assert.Equal("Untitled Playlist", playlist.Name);
    }

    [Fact]
    public void AddSongToPlaylist_IncrementsEntriesWithIncreasingSortOrder()
    {
        TestSupport.ResetLibrary();
        using var lib = new LibraryService();
        var playlist = lib.CreatePlaylist("P");

        lib.AddSongToPlaylist(playlist.Id, "songA");
        lib.AddSongToPlaylist(playlist.Id, "songB");

        var stored = lib.GetPlaylist(playlist.Id)!;
        Assert.Equal(2, stored.Entries.Count);
        Assert.Equal(new[] { 1, 2 }, stored.Entries.Select(e => e.SortOrder));
    }

    [Fact]
    public void AddSongToPlaylist_UnknownPlaylist_ReturnsNull()
    {
        TestSupport.ResetLibrary();
        using var lib = new LibraryService();
        Assert.Null(lib.AddSongToPlaylist("does-not-exist", "songA"));
    }

    [Fact]
    public void ReorderAndRemovePlaylistEntry()
    {
        TestSupport.ResetLibrary();
        using var lib = new LibraryService();
        var playlist = lib.CreatePlaylist("P");
        lib.AddSongToPlaylist(playlist.Id, "a");
        lib.AddSongToPlaylist(playlist.Id, "b");
        var entryId = lib.GetPlaylist(playlist.Id)!.Entries[0].Id;

        Assert.True(lib.ReorderPlaylistEntry(playlist.Id, entryId, 99));
        Assert.Equal(99, lib.GetPlaylist(playlist.Id)!.Entries.First(e => e.Id == entryId).SortOrder);

        Assert.True(lib.RemovePlaylistEntry(playlist.Id, entryId));
        Assert.Single(lib.GetPlaylist(playlist.Id)!.Entries);
        Assert.False(lib.RemovePlaylistEntry(playlist.Id, "missing"));
    }

    [Fact]
    public void UpdateAndDeletePlaylist()
    {
        TestSupport.ResetLibrary();
        using var lib = new LibraryService();
        var playlist = lib.CreatePlaylist("Old", "x");

        Assert.True(lib.UpdatePlaylist(playlist.Id, "New", "y"));
        Assert.Equal("New", lib.GetPlaylist(playlist.Id)!.Name);
        Assert.Equal("y", lib.GetPlaylist(playlist.Id)!.Emoji);

        Assert.True(lib.DeletePlaylist(playlist.Id));
        Assert.Null(lib.GetPlaylist(playlist.Id));
        Assert.False(lib.DeletePlaylist(playlist.Id));
    }

    [Fact]
    public void RecordPlay_AppearsInRecentlyPlayedWithMetadata()
    {
        TestSupport.ResetLibrary();
        using var lib = new LibraryService();
        var song = new LibrarySong { Title = "Song Title", Artist = "Artist", FilePath = "/music/x.mp3" };
        lib.Data.Songs.Add(song);
        lib.Data.AddToIndex(song);

        lib.RecordPlay(song.Id, durationPlayedSeconds: 30, completed: true, trigger: "queue");

        var recent = lib.GetRecentlyPlayed();
        Assert.Single(recent);
        Assert.Equal("Song Title", (string)recent[0]["title"]);
        Assert.True((bool)recent[0]["completed"]);
        Assert.Equal("queue", (string)recent[0]["trigger"]);
    }

    [Fact]
    public void RemovePlayHistoryEntry_RemovesIt()
    {
        TestSupport.ResetLibrary();
        using var lib = new LibraryService();
        var song = new LibrarySong { Title = "T" };
        lib.Data.Songs.Add(song);
        lib.Data.AddToIndex(song);
        lib.RecordPlay(song.Id);
        var entryId = (string)lib.GetRecentlyPlayed()[0]["entryId"];

        lib.RemovePlayHistoryEntry(entryId);

        Assert.Empty(lib.GetRecentlyPlayed());
    }

    [Fact]
    public void PlaylistsPersistAcrossInstances()
    {
        TestSupport.ResetLibrary();
        string id;
        using (var lib = new LibraryService())
        {
            id = lib.CreatePlaylist("Persisted", "Z").Id;
        }

        using var reloaded = new LibraryService();
        Assert.Equal("Persisted", reloaded.GetPlaylist(id)!.Name);
    }
}
