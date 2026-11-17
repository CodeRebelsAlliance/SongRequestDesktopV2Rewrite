# Player session and previous-track history

## Session restore

The music player writes `player-session.json` next to the app data
(`AppPaths.AppDataDir`). It is debounced/periodic (5 s), atomic (temp file +
rename), versioned, and capped at 8 MB. A missing, corrupt, oversized or
older/newer-version file is ignored; it can never prevent startup.

Saved state:
- main queue and playlist queue (portable paths + title/artist/duration),
- current track, position and whether it was playing,
- volume, crossfade, output device and repeat mode,
- the last three played tracks.

Restore prunes entries whose files no longer exist. The current track is opened
**paused** at the saved position, so opening the app never unexpectedly starts
audio; pressing play continues from that position. If the audio device or file
fails, the queue is still restored and the player remains usable.

Desktop stores/flushes it from `Program`; mobile from `AppHost`. Both hosts use the
same core service.

## Previous button and queue history

The engine keeps up to **three** previous tracks (oldest first). Starting a new
track, advancing, or completing a crossfade records the outgoing track. `Previous`
behaves like a normal player:

1. more than 3 seconds into the track → seek to 0,
2. near the start → pop the newest history entry, put it at the front of the queue
   and play it,
3. no history → seek to 0.

The queue payload includes `history`. Both queue UIs render those rows greyed out
above **Now Playing**; the queue initially scrolls to the current track, so history
is seen only when the user scrolls up. Mobile history rows are non-interactive and
excluded from multi-select/actions.
