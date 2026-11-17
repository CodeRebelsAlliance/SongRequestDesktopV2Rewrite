# Albums, search and home integration

Albums are a library-level collection, not a separate database. A new
`AlbumCatalogService` (`SongRequest.Core/AlbumCatalogService.cs`) groups present
songs by normalized album title + album artist, enriches the result through the
existing metadata providers, and caches that provider payload.

The service is deliberately collection-oriented so artist support can reuse the
same search/home/detail pattern later.

## Grouping and identity

- Group key: trimmed, lower-cased album title + album artist (falling back to
  the track artist when no album artist is tagged).
- Stable id: first 20 hex characters of a SHA-256 hash of that key.
- Missing files and songs without an album are not grouped.
- Track order uses disc then track number, treating an untagged disc (`0`) as
  disc 1 so local files sort alongside provider tracks.

## Provider enrichment and cache

- Home and search only use local album summaries; they never call a provider.
- Opening an album (`getAlbum`) calls `MetadataOnlineService.FindAlbumAsync`
  with the user's provider priority. The first provider that returns a track
  list wins.
- Provider metadata, the ordered track list and downloaded artwork are stored
  in `AppData/album-cache.json` and `AppData/album-artwork/`.
- A successful lookup is fetched once. Reopening the album (or restarting the
  app) reads the cache. `refresh: true` forces a new lookup.
- Local matching is always recomputed from the current library, so adding a
  missing local file immediately fills it in without another provider call.
- Editing a song's metadata invalidates the cache entry for the album it
  belonged to.

## Smart track matching

Provider tracks are matched to local songs by normalized title and track
number. Local-only tracks (for example bonus tracks the provider does not list)
remain visible. Provider tracks with no local match are returned as
`isMissing: true` placeholders and are shown greyed out in the album view.

## Interop commands

| Command | Parameters | Purpose |
| --- | --- | --- |
| `searchLibrary` | `query`, `maxResults` | Returns `results` (songs), `albums`, `playlists` and totals. |
| `getHomeFeed` | `count` | Adds `albums`, `recommendedAlbums` and `stats.totalAlbums`. |
| `getAlbum` | `albumId` or `songId`, `refresh` | Album metadata and ordered tracks; fetches/caches provider data on first open. |
| `playAlbum` | `albumId` | Play the album's present local tracks in order. |
| `shuffleAlbum` | `albumId` | Shuffle the album's present local tracks and play. |
| `queueAlbum` | `albumId` | Append the album's present local tracks to the queue. |
