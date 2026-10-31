// Packaging/contract checks for karaoke mode. The runtime is also exercised in
// the browser harness, but these catch a page forgetting the shared module.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..', '..');
const ui = path.join(root, 'SongRequestDesktopV2Rewrite', 'newui');
const read = (name) => fs.readFileSync(path.join(ui, name), 'utf8');

test('karaoke mode is available on desktop, mobile and presentation pages', () => {
  for (const page of ['musicplayer.html', 'mobilemusic.html', 'presentation.html']) {
    const html = read(page);
    assert.match(html, /karaoke\.js/, `${page} must load the karaoke module`);
  }

  const js = read('karaoke.js');
  assert.match(js, /renderLine/);
  assert.match(js, /karaoke-word/);
  assert.match(js, /attachPauses/);
  assert.match(js, /karaoke-pause-fill/);
  assert.match(js, /is-sung/);
  assert.match(js, /is-active/);
  assert.match(js, /chooseLineForTime/);
  assert.match(js, /nextLineForTime/);
  assert.match(js, /gap \* 0\.5/);
});

test('karaoke toggle is exposed in settings and persisted', () => {
  const config = fs.readFileSync(path.join(root, 'SongRequest.Core', 'ConfigService.cs'), 'utf8');
  const desktop = read('index.html');
  const desktopJs = read('app.js');
  const mobile = read('mobilemusic.js');
  const interop = fs.readFileSync(path.join(root, 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');

  assert.match(config, /KaraokeMode/);
  assert.match(desktop, /id="s-karaoke-mode"/);
  assert.match(desktopJs, /karaokeMode/);
  assert.match(mobile, /id="mm-menu-karaoke"/);
  assert.match(mobile, /id="mm-set-karaoke"/);
  assert.match(mobile, /karaokeMode/);
  assert.match(interop, /karaokeModeChanged/);
});

test('karaoke version generation is linked, versioned and shown as a loading pill', () => {
  const config = fs.readFileSync(path.join(root, 'SongRequest.Core', 'ConfigService.cs'), 'utf8');
  const service = fs.readFileSync(path.join(root, 'SongRequest.Core', 'Audio', 'KaraokeVersionService.cs'), 'utf8');
  const remover = fs.readFileSync(path.join(root, 'SongRequest.Core', 'Audio', 'VocalRemover.cs'), 'utf8');
  const engine = fs.readFileSync(path.join(root, 'SongRequest.Core', 'MusicEngine.cs'), 'utf8');
  const interop = fs.readFileSync(path.join(root, 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');
  const js = read('advancedaudio.js');
  const desktop = read('index.html');
  const mobile = read('mobilemusic.js');

  assert.match(config, /PlayKaraokeVersions/);
  assert.match(service, /CurrentVersion/);
  assert.match(service, /EnsureAsync/);
  assert.match(service, /Delete/);
  assert.match(remover, /AlgorithmVersion/);
  assert.match(engine, /PreparePlaybackPathAsync/);
  assert.match(interop, /PrepareKaraokePlaybackAsync/);
  assert.match(interop, /karaokeGenerationProgress/);
  assert.match(interop, /generateKaraokeVersion/);
  assert.match(js, /handleKaraokeGeneration/);
  assert.match(js, /ad-karaoke-pill/);
  assert.match(js, /Karaoke failed/);
  assert.match(desktop, /id="s-play-karaoke"/);
  assert.match(mobile, /mm-menu-play-karaoke/);
});

test('karaoke timings are generated in the core from analysis and timed lyrics', () => {
  const core = fs.readFileSync(path.join(root, 'SongRequest.Core', 'Audio', 'KaraokeLyrics.cs'), 'utf8');
  const analyzer = fs.readFileSync(path.join(root, 'SongRequest.Core', 'Audio', 'SongAnalyzer.cs'), 'utf8');
  const interop = fs.readFileSync(path.join(root, 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');

  assert.match(core, /KaraokeLyricsData/);
  assert.match(core, /NoveltyPeaks/);
  assert.match(core, /EstimateSyllables/);
  assert.match(core, /MinPauseSeconds/);
  assert.match(analyzer, /Parts/);
  assert.match(interop, /BuildKaraokePayload/);
  assert.match(interop, /karaokeMode/);
});

test('lyrics renderers attach word spans and pause bars', () => {
  const desktop = read('musicplayer.js');
  const mobile = read('mobilemusic.js');
  const presentation = read('presentation.js');

  for (const js of [desktop, mobile, presentation]) {
    assert.match(js, /SongRequestKaraoke/);
    assert.match(js, /renderLine/);
    assert.match(js, /attachPauses/);
    assert.match(js, /\.update\(/);
  }
});
