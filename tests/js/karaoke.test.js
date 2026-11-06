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
  assert.match(js, /karaoke-syllable/);
  assert.match(js, /line\.syllables/);
  assert.match(js, /attachPauses/);
  assert.match(js, /karaoke-pause-fill/);
  assert.match(js, /karaoke-pause-caption/);
  assert.match(js, /is-sung/);
  assert.match(js, /is-active/);
  assert.match(js, /dataset\.text/);
  assert.match(js, /dataset\.karaokeStart/);
  assert.match(js, /dataset\.karaokeEnd/);
  assert.match(js, /\(time - start\) \/ \(end - start\)/);
  // The painter interpolates between host ticks instead of jumping 2–4 times a
  // second, and keeps a per-span cache so inactive lines are not re-written.
  assert.match(js, /setPlaying/);
  assert.match(js, /requestAnimationFrame/);
  assert.match(js, /entryFor/);
  assert.match(js, /invalidate/);
  assert.match(js, /--karaoke-lift/);
  assert.match(js, /--karaoke-smooth/);
  assert.match(js, /--karaoke-lift-time/);
  assert.match(js, /--karaoke-color-time/);
  assert.match(js, /liftFactor/);
  assert.match(js, /lineActive/);
  assert.match(js, /distance === 1 \? 0\.58/);
  assert.match(js, /chooseLineForTime/);
  assert.match(js, /nextLineForTime/);
  assert.match(js, /gap \* 0\.5/);
  const css = read('advancedaudio.css');
  assert.match(css, /clip-path:\s*inset\(0 calc\(100% - var\(--karaoke-progress/);
  assert.match(css, /var\(--karaoke-smooth/);
  assert.match(css, /\.karaoke-word::after/);
  assert.match(css, /\.karaoke-pause-track\s*\{[^}]*height:\s*12px/s);
  assert.match(css, /\.karaoke-pause\s*\{[^}]*background:\s*none/s);
  assert.doesNotMatch(css, /\.karaoke-pause-copy/);
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
  assert.match(core, /KaraokeSyllable/);
  assert.match(core, /Syllabify/);
  assert.match(core, /NoveltyPeaks/);
  assert.match(core, /EstimateSyllables/);
  assert.match(core, /MinPauseSeconds/);
  assert.match(analyzer, /Parts/);
  assert.match(interop, /BuildKaraokePayload/);
  assert.match(interop, /syllables = line\.Syllables/);
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
  assert.match(desktop, /karaokeLineTimes/);
  assert.match(mobile, /karaokeTimes/);
  assert.match(presentation, /karaokeLineTimes/);
});

test('enhanced AI vocal removal is a settings-managed local model feature', () => {
  const root = path.join(__dirname, '..', '..');
  const core = fs.readFileSync(path.join(root, 'SongRequest.Core', 'Audio', 'AiVocalModels.cs'), 'utf8');
  const separator = fs.readFileSync(path.join(root, 'SongRequest.Core', 'Audio', 'AiVocalSeparation.cs'), 'utf8');
  const service = fs.readFileSync(path.join(root, 'SongRequest.Core', 'Audio', 'KaraokeVersionService.cs'), 'utf8');
  const config = fs.readFileSync(path.join(root, 'SongRequest.Core', 'ConfigService.cs'), 'utf8');
  const interop = fs.readFileSync(path.join(root, 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');
  const catalog = fs.readFileSync(path.join(root, 'SongRequest.Core', 'Integration', 'CommandCatalog.cs'), 'utf8');
  const desktop = read('index.html');
  const desktopJs = read('app.js');
  const mobile = read('mobilemusic.js');

  assert.match(core, /AiVocalModelCatalog/);
  assert.match(core, /AiVocalModelManager/);
  assert.match(core, /QualityRating/);
  assert.match(core, /PerformanceRating/);
  assert.match(core, /UVR_MDXNET_KARA_2/);
  assert.match(core, /DownloadUrl/);
  assert.match(separator, /SherpaAiVocalSeparator/);
  assert.match(separator, /OfflineSourceSeparation/);
  assert.match(separator, /SaveStemToWaveFile\(instrumentalStemIndex/);
  assert.match(separator, /instrumentalStemIndex/);
  assert.match(service, /AiVocalRemover\.VersionFor/);
  assert.match(service, /FallbackFromAi/);
  assert.match(service, /ModelId/);
  assert.match(config, /EnhancedVocalRemoval/);
  assert.match(config, /AiVocalModelId/);
  assert.match(interop, /getAiVocalModels/);
  assert.match(interop, /downloadAiVocalModel/);
  assert.match(interop, /aiModelProgress/);
  assert.match(catalog, /getAiVocalModels/);
  assert.match(desktop, /s-ai-vocal-removal/);
  assert.match(desktop, /s-ai-vocal-model/);
  assert.match(desktop, /s-ai-vocal-disclaimer/);
  assert.match(desktop, /ai-disclaimer-modal/);
  assert.match(desktop, /ai-settings-card/);
  assert.match(desktopJs, /enhancedVocalRemoval/);
  assert.match(desktopJs, /aiDisclaimerAccepted/);
  assert.match(desktopJs, /openAiDisclaimer/);
  assert.match(desktopJs, /getAiVocalModels/);
  assert.match(mobile, /mm-set-ai-vocal/);
  assert.match(mobile, /mm-set-ai-model/);
  assert.match(mobile, /mm-ai-disclaimer-accept/);
  assert.match(mobile, /mm-ai-settings/);
  assert.match(mobile, /aiDisclaimerAccepted/);
  assert.match(mobile, /getAiVocalModels/);
  assert.match(interop, /aiVocalModeChanged/);

  const playerHtml = read('musicplayer.html');
  const playerJs = read('musicplayer.js');
  const mobileHtml = read('mobilemusic.html');
  const aiCss = read('ai.css');
  assert.match(playerHtml, /expanded-ai-badge/);
  assert.match(playerHtml, /ai\.css/);
  assert.match(mobileHtml, /mm-ai-badge/);
  assert.match(mobileHtml, /ai\.css/);
  assert.match(aiCss, /\.ai-badge/);
  assert.match(aiCss, /ai-pan/);
  assert.match(aiCss, /\.ai-settings-card/);
  assert.match(aiCss, /\.mm-ai-settings/);
  assert.match(aiCss, /\.ai-disclaimer-modal/);
  assert.match(playerJs, /expanded-ai-badge/);
  assert.match(mobile, /mm-ai-badge/);
});
