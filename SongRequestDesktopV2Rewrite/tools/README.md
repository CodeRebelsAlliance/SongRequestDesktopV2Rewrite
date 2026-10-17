# tools/

Optional helper binaries.

> **The app can download these for you.** When FFmpeg or yt-dlp are missing, the
> app offers to download them (on startup, and in **Settings → Security & Server
> → External Tools**) with progress. Files land in this `tools` folder, or in the
> per-user `tools` folder (`<AppData>/SongRequestDesktopV2Rewrite/tools`) when
> this one is not writable. The manual instructions below are the fallback.

## yt-dlp

The **Settings → Security & Server → YouTube Authentication** card (and the
startup auth step) can import YouTube cookies from an installed browser. That
works by shelling out to `yt-dlp`:

```
yt-dlp --cookies-from-browser <browser> --cookies <tmp.txt> --skip-download https://www.youtube.com
```

To enable it, place a `yt-dlp` binary here (`yt-dlp.exe` on Windows):

```
tools/yt-dlp        (Linux / macOS)
tools/yt-dlp.exe    (Windows)
```

If the binary is not present here, the app falls back to looking for `yt-dlp`
on the `PATH`.

If `yt-dlp` is unavailable, cookies can still be imported by pasting them
(a Netscape `cookies.txt` or a raw `Cookie:` header) into the same settings
card.

## ffmpeg / ffprobe

The cross-platform audio decoder decodes playback audio with **FFmpeg** by
streaming raw 32-bit float PCM:

```
ffmpeg -hide_banner -loglevel error -i <file> -vn -sn -dn -map 0:a:0 \
       -f f32le -acodec pcm_f32le -ar <native rate> -ac <native channels> pipe:1
```

`ffprobe` is used to read the sample rate, channel count and duration.

Place the binaries here (`ffmpeg.exe` / `ffprobe.exe` on Windows):

```
tools/ffmpeg        tools/ffprobe        (Linux / macOS)
tools/ffmpeg.exe    tools/ffprobe.exe    (Windows)
```

If they are not present here, the app looks for them on the `PATH`. If FFmpeg
is not available at all, the app falls back to the Windows Media Foundation
decoder (Windows only), which cannot decode Opus/WebM files.

FFmpeg is required to correctly decode the files YouTube downloads are saved
as (audio-only streams are commonly Opus-in-WebM, saved with a `.mp3`
extension) and imported Ogg/Opus/FLAC files on Linux and macOS.

