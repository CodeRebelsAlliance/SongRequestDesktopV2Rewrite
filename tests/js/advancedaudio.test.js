// Packaging/behaviour checks for the advanced audio features (equalizer, sleep
// timer, song analysis) and the mobile visualization handshake.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..', '..');
const ui = path.join(root, 'SongRequestDesktopV2Rewrite', 'newui');
const read = (name) => fs.readFileSync(path.join(ui, name), 'utf8');

test('desktop and mobile player load the shared advanced-audio panel', () => {
  for (const page of ['musicplayer.html', 'mobilemusic.html']) {
    const html = read(page);
    assert.match(html, /advancedaudio\.css/, `${page} must load the panel styles`);
    assert.match(html, /advancedaudio\.js/, `${page} must load the panel logic`);
  }

  const html = read('musicplayer.html');
  for (const id of ['btn-equalizer', 'btn-sleep-timer', 'btn-analysis']) {
    assert.ok(html.includes(`id="${id}"`), `missing desktop toolbar button #${id}`);
  }
});

test('mobile player exposes equalizer, sleep timer and analysis in Options', () => {
  const js = read('mobilemusic.js');
  assert.match(js, /data-menu="equalizer"/);
  assert.match(js, /data-menu="sleep"/);
  assert.match(js, /data-menu="analysis"/);
  assert.match(js, /SongRequestAdvancedAudio\.openEqualizer\(\)/);
  assert.match(js, /SongRequestAdvancedAudio\.openSleepTimer\(\)/);
  assert.match(js, /SongRequestAdvancedAudio\.openAnalysis\(/);
});

test('sleep timer is a player pill whose remaining time can be edited by clicking', () => {
  const js = read('advancedaudio.js');
  assert.match(js, /id = 'ad-sleep-pill'/);
  assert.match(js, /ad-sleep-pill-text/);
  assert.match(js, /pill\.addEventListener\('click', function \(\) \{ open\('sleep'\); \}\)/);
  assert.match(js, /formatRemaining/);
  assert.match(js, /is-finishing/);
  assert.match(js, /playLastSongToEnd/);
});

test('equalizer panel has a graphical curve, ten bands and presets', () => {
  const js = read('advancedaudio.js');
  const css = read('advancedaudio.css');

  assert.match(js, /drawEqualizerGraph/);
  assert.match(js, /ad-eq-graph/);
  assert.match(js, /ad-band-slider/);
  assert.match(js, /data-sleep-minutes/);
  assert.match(js, /setEqualizerBand/);
  assert.match(js, /saveEqualizerPreset/);
  assert.match(css, /\.ad-bands/);
  assert.match(css, /grid-template-columns: repeat\(10/);
});

test('analysis panel draws the audio path and quality graphs', () => {
  const js = read('advancedaudio.js');

  assert.match(js, /drawAnalysisTimeline/);
  assert.match(js, /drawQualityRadar/);
  assert.match(js, /ad-analysis-timeline/);
  assert.match(js, /ad-analysis-quality/);
  assert.match(js, /songAnalysisProgress/);
  assert.match(js, /bpm/);
  assert.match(js, /loudnessLufs/);
  assert.match(js, /drops/);
});

test('host bridge dispatches to every registered handler', () => {
  const js = read('hostbridge.js');

  assert.match(js, /var handlers = \[\]/);
  assert.match(js, /handlers\.push\(handler\)/);
  assert.match(js, /function dispatch\(message\)/);
  assert.match(js, /for \(var i = 0; i < handlers\.length; i\+\+\)/);
});

test('mobile visualization announces readiness and re-measures its canvas', () => {
  const js = read('visualization.js');

  // Hidden Android web views measure a 1x1 parent; observe the real element and
  // re-check the size in the render loop instead of relying on window.resize.
  assert.match(js, /ResizeObserver/);
  assert.match(js, /canvas\.parentElement/);
  assert.match(js, /visualizationReady/);
  assert.match(js, /document\.addEventListener\('visibilitychange'/);
  assert.match(js, /if \(w > 0 && h > 0 &&/);
});

test('mobile shell starts the tap and the page handshake re-arms it', () => {
  const shell = fs.readFileSync(path.join(root, 'SongRequest.Mobile', 'MobileShell.cs'), 'utf8');
  const interop = fs.readFileSync(path.join(root, 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');

  assert.match(shell, /ShowFileInTab\("visualization\.html", "stage"\)[\s\S]*?StartVisualizationStreaming\(\)/);
  assert.match(interop, /case "visualizationReady":/);
  assert.match(interop, /EnsureVisualization\(\)/);
});

test('analysis is reachable per song on both platforms', () => {
  const desktop = read('musicplayer.html');
  const desktopJs = read('musicplayer.js');
  const mobile = read('mobilemusic.js');

  assert.match(desktop, /data-action="analyze"/);
  assert.match(desktopJs, /openAnalysis\(\{ songId: songId \}\)/);
  assert.match(mobile, /data-song-action="analyze"/);
  assert.match(mobile, /openAnalysis\(\{ songId: songId \}\)/);
});

test('opening song analysis always offers the current song, not only re-analysis', () => {
  const js = read('advancedaudio.js');

  // The desktop toolbar used to call open('analysis') directly, so the previous
  // track's result (with only a re-analyse button) stayed on screen even after
  // the song changed.
  assert.match(js, /\['btn-analysis', function \(\) \{ api\.openAnalysis\(\); \}\]/);
  assert.match(js, /function setAnalysisTarget\(target\) \{[\s\S]*?state\.analysis = null/);
  assert.match(js, /id="ad-analysis-current"/);
  assert.match(js, /state\.analysisTarget = null;\s*\n\s*state\.analysis = null;\s*\n\s*analyzeCurrent\(false\)/);
});

test('closing an advanced-audio modal cannot leave an invisible click blocker', () => {
  const css = read('advancedaudio.css');
  const js = read('advancedaudio.js');

  // display:flex on #ad-overlay overrides the browser's [hidden] rule unless the
  // hidden state is made authoritative. The stuck overlay used to swallow every
  // click in the desktop music player after closing the equalizer/sleep/analysis
  // panel.
  assert.match(css, /#ad-overlay\[hidden\]\s*\{[^}]*display:\s*none\s*!important/s);
  assert.match(css, /#ad-overlay\[hidden\]\s*\{[^}]*pointer-events:\s*none/s);
  assert.match(js, /overlay\.hidden = true/);
  assert.match(js, /overlay\.hidden = false/);
});

test('song parts and drops are interactive chapters on player progress bars', () => {
  const js = read('advancedaudio.js');
  const css = read('advancedaudio.css');

  assert.match(js, /renderTrackMarkers/);
  assert.match(js, /renderMarkerLayer\('exp-progress-track'/);
  assert.match(js, /renderMobileMarkers/);
  assert.match(js, /ad-chapter/);
  assert.match(js, /ad-drop-pin/);
  assert.match(js, /data-part-time/);
  assert.match(js, /parts = analysis\.parts/);
  assert.match(js, /samePath\(state\.currentSongPath, data\.songPath\)/);
  assert.match(js, /function labelEdgeClass/);
  assert.match(js, /bindMarkerHover/);
  assert.match(css, /\.ad-marker-label\.is-start/);
  assert.match(css, /#progress-track\.ad-marker-hover \.seek-tooltip/);
  assert.match(css, /#exp-progress-track\.ad-marker-hover \.seek-tooltip/);
  assert.match(css, /\.ad-chapter-band/);
  assert.match(css, /\.ad-drop-pin/);
  assert.match(css, /\.is-chorus/);
  assert.match(css, /\.ad-mm-seek-wrap/);
});

test('BPM appears next to the quality badge on desktop and in mobile Options', () => {
  const js = read('advancedaudio.js');
  const css = read('advancedaudio.css');

  assert.match(js, /function updateBpmBadge/);
  assert.match(js, /upsertBpmPill\('player-bar-quality'/);
  assert.match(js, /upsertBpmPill\('expanded-quality'/);
  assert.match(js, /mm-options-meta/);
  assert.match(js, /ad-bpm-mobile/);
  assert.match(css, /\.ad-bpm-pill/);
  assert.match(css, /\.ad-bpm-badge/);
});

test('auto-analysis has a setting on desktop and mobile', () => {
  const desktop = read('index.html');
  const desktopJs = read('app.js');
  const mobile = read('mobilemusic.js');
  const config = fs.readFileSync(path.join(root, 'SongRequest.Core', 'ConfigService.cs'), 'utf8');
  const interop = fs.readFileSync(path.join(root, 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');

  assert.match(config, /AutoAnalyzeSongs/);
  assert.match(interop, /SendOrStartSongAnalysis/);
  assert.match(interop, /AutoAnalyzeSongs/);
  assert.match(desktop, /id="s-auto-analyze"/);
  assert.match(desktopJs, /autoAnalyzeSongs/);
  assert.match(mobile, /mm-menu-auto-analyze/);
  assert.match(mobile, /mm-set-auto-analyze/);
  assert.match(mobile, /autoAnalyzeSongs/);
});

test('analysis result exposes song parts in the panel', () => {
  const js = read('advancedaudio.js');
  assert.match(js, /Song parts \/ chapters/);
  assert.match(js, /partClass\(part\.type\)/);
  assert.match(js, /analysis\.parts/);
});

test('automatic analysis shows a loading indicator in both players', () => {
  const js = read('advancedaudio.js');
  const css = read('advancedaudio.css');

  assert.match(js, /analysisPending/);
  assert.match(js, /startAnalysisPending/);
  assert.match(js, /songAnalysisProgress/);
  assert.match(js, /songAnalysisUnavailable/);
  assert.match(js, /ad-analysis-pending-player/);
  assert.match(js, /ad-analysis-pending-expanded/);
  assert.match(js, /ad-analysis-pill/);
  assert.match(js, /ad-analysis-pending-mobile/);
  assert.match(js, /Analyzing /);
  assert.match(css, /\.ad-analysis-pending/);
  assert.match(css, /fa-spin|is-spinning|ad-analysis-pending i/);
});

test('auto analysis is driven by the engine, not the player page', () => {
  const interop = fs.readFileSync(path.join(root, 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');
  assert.match(interop, /mp\.NowPlayingChanged = song/);
  assert.match(interop, /SendOrStartSongAnalysis\(song\)/);
});

test('advanced audio is exposed through the local API and command catalogue', () => {
  const api = fs.readFileSync(path.join(root, 'SongRequest.Core', 'Integration', 'AppIntegrationServer.Api.cs'), 'utf8');
  const catalog = fs.readFileSync(path.join(root, 'SongRequest.Core', 'Integration', 'CommandCatalog.cs'), 'utf8');
  const openapi = fs.readFileSync(path.join(root, 'SongRequest.Core', 'Integration', 'OpenApiDocument.cs'), 'utf8');

  for (const route of ['/api/v1/equalizer', '/api/v1/sleep-timer', '/api/v1/analysis']) {
    assert.ok(api.includes(route), `missing API route ${route}`);
    assert.ok(openapi.includes(route), `missing OpenAPI path ${route}`);
  }

  for (const command of ['getEqualizer', 'setEqualizerBand', 'getSleepTimer', 'startSleepTimer', 'analyzeSong', 'getSongAnalysis']) {
    assert.ok(catalog.includes(`Add("${command}"`), `missing command ${command}`);
  }
});
