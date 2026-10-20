// Packaging checks for the purpose-built mobile pages. They are plain files in
// newui/ and are linked into the APK by the mobile csproj, so a missing stylesheet
// or script is easy to miss until a device run.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ui = path.join(__dirname, '..', '..', 'SongRequestDesktopV2Rewrite', 'newui');
const read = (name) => fs.readFileSync(path.join(ui, name), 'utf8');

test('mobile requests page loads its own layout and keeps the full desktop feature DOM', () => {
  const html = read('mobilerequests.html');
  assert.match(html, /mobilerequests\.css/);
  assert.match(html, /app\.js/);
  assert.match(html, /mobilerequests\.js/);

  // Settings, submit, word filter and dashboard are part of "all features", so
  // the mobile variant must still contain their ids.
  for (const id of ['settings-modal', 'modal-overlay', 'panel-wordfilter', 'panel-dashboard', 'startup-overlay']) {
    assert.ok(html.includes(`id="${id}"`), `missing #${id}`);
  }
});

test('mobile soundboard page is a page-based Material UI', () => {
  const html = read('mobilesoundboard.html');
  assert.match(html, /mobilesoundboard\.css/);
  assert.match(html, /mobilesoundboard\.js/);

  for (const id of ['msb-grid', 'msb-view-edit', 'msb-view-pages', 'msb-view-grid', 'msb-view-midi', 'msb-view-keys', 'msb-view-assign']) {
    assert.ok(html.includes(`id="${id}"`), `missing #${id}`);
  }
  // Editing is a page, not a modal dialog.
  assert.ok(!html.includes('sb-edit-modal'), 'old soundboard modal markup must be gone');
});

test('mobile player applies the new artwork after resetting the previous song', () => {
  const js = read('mobilemusic.js');
  const start = js.indexOf('function onNowPlaying');
  assert.notEqual(start, -1);
  const body = js.slice(start, js.indexOf('function bindTapList', start));
  const reset = body.indexOf('if (newSong) {');
  const artwork = body.indexOf('setArt(data.thumbnail)');
  assert.ok(reset >= 0 && artwork >= 0, 'expected reset + setArt in onNowPlaying');
  assert.ok(reset < artwork, 'newSong reset must run before the new thumbnail is assigned');
});

test('soundboard uses a dedicated single audio file picker', () => {
  const root = path.join(__dirname, '..', '..');
  const interop = fs.readFileSync(path.join(root, 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');
  const pickers = fs.readFileSync(path.join(root, 'SongRequest.Mobile', 'Platforms', 'Android', 'AndroidFilePickers.cs'), 'utf8');

  assert.match(interop, /PickSoundFile/);
  assert.match(interop, /BrowseSoundboardFileAsync/);
  assert.match(pickers, /PickSoundFileAsync/);
  assert.match(pickers, /audio\/\*/);
});

test('Android CI keeps one fallback signing key between workflow runs', () => {
  const root = path.join(__dirname, '..', '..');
  const workflow = fs.readFileSync(path.join(root, '.github', 'workflows', 'release.yml'), 'utf8');

  assert.match(workflow, /\.android-signing/);
  assert.match(workflow, /android-fallback-keystore-v2/);
  assert.match(workflow, /keytool -genkeypair/);
  assert.doesNotMatch(workflow, /path:\s*~\/\.android\/debug\.keystore/,
    'the old cache path is not where .NET Android stores its debug key');
});

test('startup is not replayed when a page is recreated', () => {
  const root = path.join(__dirname, '..', '..');
  const app = fs.readFileSync(path.join(ui, 'app.js'), 'utf8');
  const page = fs.readFileSync(path.join(root, 'SongRequest.Mobile', 'MainPage.cs'), 'utf8');
  const shell = fs.readFileSync(path.join(root, 'SongRequest.Mobile', 'MobileShell.cs'), 'utf8');

  assert.match(app, /data\.setupComplete/);
  assert.match(page, /SetNavigationVisible\(false\)/);
  assert.match(shell, /SetNavigationVisible\(true\)/);
});

test('mobile player offers a YouTube search mode next to library search', () => {
  const html = read('mobilemusic.html');
  const js = read('mobilemusic.js');
  assert.match(html, /id="mm-home-youtube"/);
  assert.match(js, /youtubeSearchMode/);
  assert.match(js, /searchYoutubeFor\(query\)/);
});

test('music settings sheet shows library import progress and current item', () => {
  const js = read('mobilemusic.js');
  assert.match(js, /mm-import-progress/);
  assert.match(js, /mm-import-label/);
  assert.match(js, /libraryScanProgress/);
});

test('blacklist rows are enriched with metadata, not just artwork', () => {
  const root = path.join(__dirname, '..', '..');
  const interop = fs.readFileSync(path.join(root, 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');
  const cache = fs.readFileSync(path.join(root, 'SongRequest.Core', 'VideoCache.cs'), 'utf8');

  assert.match(interop, /EnrichBlacklistMetadataAsync/);
  assert.match(interop, /GetVideoMetadataAsync/);
  assert.match(cache, /MetadataFetched/);
});

test('automatic cookie import is available on desktop and mobile', () => {
  const root = path.join(__dirname, '..', '..');
  const config = fs.readFileSync(path.join(root, 'SongRequest.Core', 'ConfigService.cs'), 'utf8');
  const signIn = fs.readFileSync(path.join(root, 'SongRequest.Mobile', 'Platforms', 'Android', 'AndroidSignInPage.cs'), 'utf8');
  const desktop = read('index.html');
  const mobile = read('mobilerequests.html');

  assert.match(config, /AutoGrabCookies/);
  assert.match(signIn, /HasYouTubeSession/);
  assert.match(desktop, /id="s-yt-auto"/);
  assert.match(mobile, /id="s-yt-auto"/);
});

test('mobile startup and request navigation use the Material layout', () => {
  const css = read('mobilerequests.css');
  const html = read('mobilerequests.html');
  assert.match(css, /#startup-overlay/);
  assert.match(css, /#startup-launch-grid/);
  assert.match(css, /#tabbar \{/);
  assert.match(html, /data-tab="all-songs"[^>]*><i /);
});

test('requests offer Play Now and playlists start immediately', () => {
  const app = read('app.js');
  const interop = fs.readFileSync(path.join(__dirname, '..', '..', 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');
  assert.match(app, /data-action="playNow"/);
  assert.match(app, /action === 'playNow'/);
  assert.match(interop, /actionToTake == "play"/);
  assert.match(interop, /AdvanceFromPlaylistQueue\(\)/);
});

test('mobile deletes use confirmation sheets and recent rows show timestamps', () => {
  const music = read('mobilemusic.js');
  const board = read('mobilesoundboard.js');
  assert.match(music, /openConfirmSheet/);
  assert.match(board, /openConfirmSheet/);
  assert.match(music, /Played ' \+ esc\(played\)/);
});

test('visualization has a mobile Material layout', () => {
  const css = read('visualization.css');
  assert.match(css, /:root\[data-shell="mobile"\] #viz-toolbar/);
  assert.match(css, /:root\[data-shell="mobile"\] #viz-modes/);
});

test('mobile queue is a player chip, not a navigation tab', () => {
  const html = read('mobilemusic.html');
  const page = fs.readFileSync(path.join(__dirname, '..', '..', 'SongRequest.Mobile', 'MainPage.cs'), 'utf8');
  assert.match(html, /id="mm-queue-toggle"/);
  assert.match(html, /id="mm-more"[\s\S]*?<span>Options<\/span>/);
  assert.doesNotMatch(page, /new TabDef\("queue"/);
});

test('playlists support queueing and dynamic mixes', () => {
  const interop = fs.readFileSync(path.join(__dirname, '..', '..', 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');
  assert.match(interop, /queuePlaylist/);
  assert.match(interop, /DynamicRandomMixId/);
  assert.match(interop, /dynamic:random-mix/);
  assert.match(interop, /DynamicMostPlayedId/);
});

test('automatic sign-in opens the in-app YouTube page', () => {
  const app = read('app.js');
  assert.match(app, /signInSupported && data\.autoGrabCookies/);
  assert.match(app, /Opening YouTube sign-in/);
});

test('mobile music page exposes repeat state and playlist browsing', () => {
  const html = read('mobilemusic.html');
  assert.match(html, /id="mm-repeat"[^>]*aria-pressed="false"/);
  assert.match(html, /id="mm-view-playlist"/);
  assert.match(html, /id="mm-playlist-list"/);
  assert.match(html, /id="mm-playlist-menu"/);
});

test('mobile stylesheets define the active repeat state and playlist rows', () => {
  const css = read('mobilemusic.css');
  assert.match(css, /\.mm-icon-btn\.is-active/);
  assert.match(css, /\.mm-playlist-song/);
  assert.match(css, /\.mm-entry-controls/);
});
