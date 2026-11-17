# Song metadata editing

Right-click a song in the desktop library (or long-press it on mobile) and choose
**Edit metadata**. Desktop opens a dedicated window; mobile opens a hidden
full-screen page, so editing never becomes another permanent navigation tab.

## What can be edited

- Title / creator, album, album artist, genre, year, track, disc, BPM, comment.
- Composer, lyricist, conductor, publisher, copyright, ISRC and grouping.
- Embedded artwork (choose an image, use provider artwork, or remove it).
- Lyrics in a graphical timed editor or as plain text. Timed lyrics are written as
  LRC, embedded in the file and mirrored to a `.lrc` sidecar; plain lyrics are
  embedded as unsynchronised text.
- Online identification/search from MusicBrainz, the iTunes Search API, Deezer and
  Cover Art Archive. Each source can be disabled in the editor (or in the player
  settings tab).

## File-first, library fallback

Saving always tries to write the file tags with TagLib:

| Situation | Result |
|---|---|
| File writable and format supported | Tags/artwork/lyrics are written into the file; the library record is updated. |
| File read-only, unsupported or missing | The edit is stored in `library.json` (`MetadataStoredInLibrary`, `LyricsStoredInLibrary`, `ArtworkStoredInLibrary`) and remains the authoritative values. |

A later save retries the file and clears the fallback flags when it succeeds.
Library scans never overwrite an entry whose metadata is library-authoritative;
they only refresh technical fields (duration, bitrate, hash, file size/status).
Older `library.json` files have no new fields and deserialize with safe defaults.

## Online providers

| Provider | Cost / key | Rate limits | Provides |
|---|---|---|---|
| MusicBrainz | Free, no key. A meaningful User-Agent is required. | **1 request/second per IP** | Recording/track metadata, release/artist/date, links to Cover Art Archive |
| Cover Art Archive | Free, no key. | No published limit; be polite | Front/other artwork for MusicBrainz releases and release groups |
| iTunes Search API | Free, no key. | About **20 requests/minute** per IP | Track/artist/album/genre/year/artwork |
| Deezer public API | Free, no key. | **50 requests / 5 seconds** per IP/app | Track/artist/album/release date/cover art |
| AcoustID | Free for non-commercial/open-source use; **API key required**. Commercial use has paid plans. | **3 requests/second** | Audio fingerprint → MusicBrainz recording match |
| Discogs | Free account; token optional. | **25 req/minute unauthenticated**, **60 req/minute authenticated** | Release title/artist/year/genre/style/cover |

The **lookup priority** editor (Player Settings → Online metadata sources, and the
metadata editor) decides which provider is queried first. Providers are queried in
that order for each search; once a provider returns a confident match the remaining
lower-priority providers are skipped for that query. Disabled providers are skipped
entirely.

All requests go through a shared governor with a sliding window above each
provider's published limit and a minimum interval between calls (for example
MusicBrainz is paced at 1.1 s). A 429, 401/403, 5xx or network failure puts that
provider into a visible cooldown with exponential backoff; lookups skip a cooling
provider instead of blocking the UI, and the priority list shows `ready`,
`wait 42s`, `token error` or `error` next to it.

## Identification

**Identify** matches the current title/artist/album and duration against the
enabled providers, and falls back to parsing the file name when the tags are empty
(`Artist - Title`). Results include a confidence score; choosing a result fills the
editor, and provider artwork can be fetched separately. No audio is uploaded.

For files with no useful tags or filename, enable **AcoustID fingerprint** and add
a free AcoustID application key in the editor. When `fpcalc` (Chromaprint) is
installed — on `PATH` or in the app's `tools` folder — the app fingerprints the
audio locally, sends only the fingerprint to AcoustID, and turns matches into the
same selectable candidates. No audio is uploaded in either path.

## Player settings tab

Player-only preferences now live in the music player's **Settings** tab on desktop
and mobile: playback, crossfade, lyrics, output device, karaoke/AI, library
analysis, online metadata sources and player tools. The old main-window cards keep
their DOM ids for compatibility but are hidden; the visible entry point switches
to the player tab.
