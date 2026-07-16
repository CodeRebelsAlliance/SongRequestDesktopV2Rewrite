# Library Data Models

> **Scope:** New UI only (Photino window).  
> **Source file:** `LibraryModels.cs`  
> **Persistence:** JSON at `data/library/library.json`

---

## Overview

The library system adds local file management, playlists, recommendations, and
YouTube-to-library downloads to the SongRequest music player. It replaces the
ephemeral `Song` queue item with a persistent `LibrarySong` entity that
survives restarts.

```
┌──────────────┐     ┌──────────────┐     ┌──────────────────┐
│ LibrarySong  │────▶│  Playlist    │────▶│ PlaylistEntry    │
│  (track)     │     │ (collection) │     │ (ordered link)   │
└──────┬───────┘     └──────┬───────┘     └──────────────────┘
       │                    │
       │  SmartPlaylistRules│
       ▼                    ▼
┌──────────────┐     ┌──────────────────┐
│PlayHistoryEntry   │  │ Recommendation  │
│ (event log)  │────▶│  (scored list)   │
└──────────────┘     └──────────────────┘
```

---

## Enums

| Enum | Values | Purpose |
|------|--------|---------|
| `SongSource` | `Local`, `Downloaded`, `External` | Where the audio file came from |
| `FileStatus` | `Present`, `Missing`, `Unreadable` | Current state of the file on disk |
| `SmartSortField` | `DateAdded`, `LastPlayed`, `PlayCount`, `Title`, `Artist`, `Album`, `Duration`, `Random` | Smart playlist ordering |
| `DownloadState` | `Pending`, `Downloading`, `Converting`, `Importing`, `Completed`, `Failed` | YouTube download lifecycle |

---

## Core Entities

### `LibrarySong`

The canonical, persisted record for a track. **Not** the same as `Song` (which
is a runtime queue item with WPF `Image` references).

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Stable GUID. Never reused. |
| `YouTubeId` | `string?` | YouTube video ID (only for `Downloaded` source). |
| `Title` | `string` | Track title. |
| `Artist` | `string` | Artist name. Default `"Unknown"`. |
| `Album` | `string` | Album name. |
| `Genre` | `string` | Genre tag. |
| `Duration` | `TimeSpan` | Track length. |
| `Tags` | `List<string>` | User-defined tags (mood, activity, etc.). |
| `FilePath` | `string` | Absolute path to the audio file. |
| `ThumbnailPath` | `string?` | Path to cached thumbnail on disk. |
| `FileSizeBytes` | `long` | File size in bytes. |
| `BitrateKbps` | `int` | Audio bitrate. |
| `SampleRateHz` | `int` | Sample rate. |
| `Source` | `SongSource` | Origin of the file. |
| `SourceUrl` | `string?` | YouTube URL (null for Local). |
| `FileStatus` | `FileStatus` | Current file health. |
| `LastVerifiedUtc` | `DateTime` | Last time a scan confirmed the file exists. |
| `PlayCount` | `int` | Total times played. |
| `DateAddedUtc` | `DateTime` | When the song entered the library. |
| `LastModifiedUtc` | `DateTime` | When metadata was last changed. |
| `LastPlayedUtc` | `DateTime?` | When the song was last played. |
| `PerceivedLoudness` | `double` | LUFS value. `-1` = not measured. |

**Computed properties (JsonIgnore):**

| Property | Returns |
|----------|---------|
| `IsMissing` | `true` if `FileStatus != Present` |
| `DurationDisplay` | `"mm:ss"` formatted string |

**Methods:**

| Method | Description |
|--------|-------------|
| `FileExists()` | Checks `FileStatus == Present && File.Exists(FilePath)` |
| `RefreshFileStatus()` | Re-checks disk, updates `FileStatus` and `FileSizeBytes`. Returns `true` if status changed. |

---

### `Playlist`

A named, ordered collection of songs.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Stable GUID. |
| `Name` | `string` | Display name. |
| `Description` | `string?` | Optional description. |
| `Emoji` | `string?` | Optional icon emoji. |
| `CreatedUtc` | `DateTime` | Creation timestamp. |
| `LastModifiedUtc` | `DateTime` | Last modification timestamp. |
| `Entries` | `List<PlaylistEntry>` | Manual entries (ordered). |
| `SmartRules` | `SmartPlaylistRules?` | Non-null = smart playlist. |
| `ResolvedSongIds` | `List<string>` | Last-resolved smart playlist snapshot. |

**Computed:**

| Property | Returns |
|----------|---------|
| `IsSmart` | `true` if `SmartRules != null` |
| `SongCount` | `Entries.Count` |

---

### `PlaylistEntry`

One slot inside a `Playlist`. The same song can appear multiple times.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Stable GUID. |
| `SongId` | `string` | FK → `LibrarySong.Id`. |
| `SortOrder` | `int` | Display ordering. |
| `AddedUtc` | `DateTime` | When this entry was added. |
| `Note` | `string?` | User note (e.g. "intro song"). |

---

### `SmartPlaylistRules`

Declarative filter that auto-populates a playlist from the library. Every
list field is an **OR-set** (match any item). A `null` field means "don't
filter on this".

**Include/Exclude filters:**

| Field | Type | Behaviour |
|-------|------|-----------|
| `IncludeGenres` | `List<string>?` | Only songs with ≥1 of these genres. |
| `ExcludeGenres` | `List<string>?` | Skip songs with any of these genres. |
| `IncludeArtists` | `List<string>?` | Only songs by these artists. |
| `ExcludeArtists` | `List<string>?` | Skip songs by these artists. |
| `IncludeTags` | `List<string>?` | Only songs with ≥1 of these tags. |
| `ExcludeTags` | `List<string>?` | Skip songs with any of these tags. |

**Range filters:**

| Field | Type | Description |
|-------|------|-------------|
| `MinDuration` | `TimeSpan?` | Minimum track length. |
| `MaxDuration` | `TimeSpan?` | Maximum track length. |
| `MinPlayCount` | `int?` | Minimum play count. |
| `MaxPlayCount` | `int?` | Maximum play count. |
| `AfterDateAdded` | `DateTime?` | Only songs added after this date. |
| `BeforeDateAdded` | `DateTime?` | Only songs added before this date. |
| `AfterLastPlayed` | `DateTime?` | Only songs played after this date. |
| `IncludeMissing` | `bool` | Include missing files (default `false`). |
| `SourceFilter` | `SongSource?` | Only songs from a specific source. |

**Sort/Limit:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `SortBy` | `SmartSortField` | `DateAdded` | Sort field. |
| `SortDescending` | `bool` | `true` | Sort direction. |
| `Limit` | `int?` | `null` | Max songs (`null`/`0` = unlimited). |

---

## History & Recommendations

### `PlayHistoryEntry`

A single playback event used for statistics and recommendations.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Stable GUID. |
| `SongId` | `string` | FK → `LibrarySong.Id`. |
| `PlayedUtc` | `DateTime` | When playback started. |
| `DurationPlayedSeconds` | `double` | Seconds actually listened to. |
| `Completed` | `bool` | Reached end of track. |
| `Trigger` | `string` | Why it played: `"queue"`, `"playlist"`, `"recommendation"`, `"autopilot"`. |

---

### `ArtistStat`

Aggregated per-artist statistics derived from `PlayHistoryEntry` records.

| Field | Type | Description |
|-------|------|-------------|
| `Artist` | `string` | Artist name. |
| `PlayCount` | `int` | Total plays. |
| `CompletedCount` | `int` | Plays that reached the end. |
| `LastPlayedUtc` | `DateTime` | Most recent play. |
| `Affinity` | `double` | 0–1 score of current listener interest. |

### `GenreStat`

Same shape as `ArtistStat` but for genres.

---

### `Recommendation`

A scored suggestion produced by the recommendation engine.

| Field | Type | Description |
|-------|------|-------------|
| `Song` | `LibrarySong` | The suggested track. |
| `Score` | `double` | 0–1 composite relevance score. |
| `Reason` | `string` | Human-readable explanation. |
| `Signal` | `string` | Dominant signal: `"artist_affinity"`, `"genre_affinity"`, `"collaborative"`, `"fresh_discovery"`, `"recently_enjoyed"`. |

---

## Downloads

### `DownloadTask`

Tracks a YouTube → library download through its lifecycle.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` | Stable GUID. |
| `YouTubeId` | `string` | Video ID. |
| `VideoTitle` | `string` | Original YouTube title. |
| `VideoUrl` | `string` | Full YouTube URL. |
| `DesiredTitle` | `string` | User-specified title (may differ). |
| `DesiredArtist` | `string` | User-specified artist. |
| `DesiredAlbum` | `string` | User-specified album. |
| `RequestedUtc` | `DateTime` | When the download was requested. |
| `State` | `DownloadState` | Current lifecycle state. |
| `Progress` | `double` | 0–1 progress. |
| `ErrorMessage` | `string?` | Error details if failed. |
| `ResultSongId` | `string?` | `LibrarySong.Id` after successful import. |

---

## Scanning & Configuration

### `LibraryConfiguration`

Top-level settings stored in `library.json`.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `ScanFolders` | `List<string>` | `[]` | Absolute paths to scan. |
| `AllowedExtensions` | `List<string>` | `mp3,m4a,wav,flac,ogg,aac,wma,opus` | File extensions to include. |
| `AutoScanOnStartup` | `bool` | `false` | Scan on app launch. |
| `AutoAddDownloadsToLibrary` | `bool` | `true` | YouTube downloads → library automatically. |
| `RemoveMissingOnScan` | `bool` | `false` | Delete missing entries vs. flag them. |

### `LibraryScanResult`

Summary returned after a folder scan.

| Field | Type | Description |
|-------|------|-------------|
| `FilesFound` | `int` | Audio files discovered. |
| `SongsAdded` | `int` | New library entries created. |
| `SongsUpdated` | `int` | Existing entries with metadata changes. |
| `SongsMissing` | `int` | Entries now flagged as missing. |
| `Errors` | `List<string>` | Per-file errors during scan. |
| `ScannedUtc` | `DateTime` | When the scan ran. |

---

## `LibraryData` (Root Document)

Serialised to `data/library/library.json`. Everything lives here.

| Field | Type | Description |
|-------|------|-------------|
| `Config` | `LibraryConfiguration` | User settings. |
| `Songs` | `List<LibrarySong>` | All library tracks. |
| `Playlists` | `List<Playlist>` | All playlists. |
| `PlayHistory` | `List<PlayHistoryEntry>` | Playback event log. |
| `ArtistStats` | `List<ArtistStat>` | Cached artist aggregates. |
| `GenreStats` | `List<GenreStat>` | Cached genre aggregates. |
| `LastScanUtc` | `DateTime` | Last scan timestamp. |
| `LastModifiedUtc` | `DateTime` | Last write timestamp. |

**In-memory helpers (not serialised):**

| Method | Complexity | Description |
|--------|-----------|-------------|
| `RebuildIndex()` | O(n) | Builds a `Dictionary<string, LibrarySong>` for O(1) ID lookups. |
| `FindSong(id)` | O(1) | Lookup by ID. Auto-builds index on first call. |
| `Search(query, max)` | O(n × t) | Multi-term, case-insensitive substring search across title/artist/album/genre/tags. |

---

## Design Decisions

### Why JSON and not SQLite?

The existing codebase (config, soundboard, metadata cache) already uses
Newtonsoft.Json files. Adding SQLite would introduce a new dependency and
two storage strategies. JSON is sufficient for libraries under ~50k songs.
If performance becomes an issue the `LibraryData` class can be swapped for
a SQLite-backed implementation later — the service layer interface stays
the same.

### Why a separate `LibrarySong` instead of reusing `Song`?

`Song` holds WPF `Image` objects and runtime queue fields (`EstimatedStart`,
`PerceivedLoudness`). Those are transient and platform-coupled.
`LibrarySong` is a clean, serialisable, WPF-free entity designed for
persistence and indexing.

### Missing file handling

`RefreshFileStatus()` is called during library scans. Two strategies are
supported:

1. **Flag only** (`RemoveMissingOnScan = false`): songs stay in the library
   with `FileStatus.Missing`. UI shows them greyed out with a warning icon.
   If the file reappears (e.g. external drive plugged in) it is restored on
   the next scan.

2. **Auto-remove** (`RemoveMissingOnScan = true`): missing songs are deleted
   from the library. Their `PlayHistoryEntry` records are kept for
   recommendation continuity.

### Smart playlists

Rules are evaluated against the in-memory index. The resolved IDs are cached
in `Playlist.ResolvedSongIds` so the playlist can be displayed without
re-evaluation on every UI render. Re-evaluation is triggered on:

- Library scan completion
- Play (the user opens the playlist tab)
- Manual refresh button

### Recommendation scoring

The engine produces a 0–1 `Score` per unplayed library song using these
signals:

| Signal | Weight | Description |
|--------|--------|-------------|
| `artist_affinity` | 30% | Correlates with `ArtistStat.Affinity` for the song's artist. |
| `genre_affinity` | 20% | Correlates with `GenreStat.Affinity` for the song's genre. |
| `fresh_discovery` | 25% | High metadata quality + `PlayCount == 0`. |
| `recently_enjoyed` | 15% | Cosine similarity with recently completed tracks. |
| `collaborative` | 10% | Reserved for future server-side similar-user data. |

`PlayHistoryEntry.Trigger` lets the engine discount its own suggestions
to avoid echo chambers.

### Fast searching

`LibraryData.Search()` does multi-term AND matching across all text fields.
For the new UI this is called from the JS search bar via a message handler.
Typical library sizes (< 10k songs) will return in < 5ms. If needed, an
inverted index can be added later without changing the public API.
