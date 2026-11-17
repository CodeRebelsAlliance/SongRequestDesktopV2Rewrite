# Artists, search and home integration

Artists follow the same collection pattern as albums. `ArtistCatalogService`
groups present songs by normalized artist name, links each artist to its local
albums through `AlbumCatalogService`, enriches the artist with provider
metadata/artwork, and caches that provider payload.

## Grouping and identity

- Group key: trimmed, lower-cased artist name, with runs of whitespace collapsed.
- Fallback: when a track has no artist tag, its album artist is used.
- Stable id: first 20 hex characters of a SHA-256 hash of the normalized name.
- Missing files and songs with no artist at all are not grouped.
- Albums on the artist page include albums where the artist is the album artist
  or appears on a track, so guest appearances/compilations are included.

## Provider enrichment and cache

- Home, search and artist recommendations use local summaries only; they never
  call a provider.
- Opening an artist (`getArtist`) looks up the artist through the user's
  provider priority order (MusicBrainz, iTunes, Deezer, Discogs).
- Provider results are merged: if the first provider knows the name/genre but
  not the image, a later provider can supply the artwork.
- Metadata, artwork and the provider discography are cached in
  `AppData/artist-cache.json` and `AppData/artist-artwork/`.
- A successful lookup is fetched once. Reopening the artist or restarting the
  app reads the cache; `refresh: true` forces a new lookup.
- Local tracks and albums are matched live, so newly added files appear
  immediately without another provider call.
- Provider albums already in the library are merged into the normal album cards.
  Provider albums that are not in the library are shown greyed out as
  “Not in your library” (the artist-level analog of missing album tracks).
- Editing a song's artist invalidates the cache entry for the artist it belonged
  to.

## Interop commands

| Command | Parameters | Purpose |
| --- | --- | --- |
| `searchLibrary` | `query`, `maxResults` | Returns `artists`, `albums`, `playlists`, `results` and totals. |
| `getHomeFeed` | `count` | Adds `artists`, `recommendedArtists` and `stats.totalArtists`. |
| `getArtist` | `artistId` or `songId`, `refresh` | Artist metadata, local tracks, local albums plus cached provider albums; fetches/caches provider data on first open. |
| `playArtist` | `artistId` | Play the artist's present local tracks in album/track order. |
| `shuffleArtist` | `artistId` | Shuffle the artist's present local tracks and play. |
| `queueArtist` | `artistId` | Append the artist's present local tracks to the queue. |

Album cards inside an artist page use the regular album ids, so they open the
same album detail page (and share the album provider cache).
