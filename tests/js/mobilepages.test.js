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

test('startup updater stays open while the package installs', () => {
  const app = read('app.js');
  const interop = fs.readFileSync(path.join(__dirname, '..', '..', 'SongRequest.Core', 'YoutubeFormInterop.cs'), 'utf8');
  assert.doesNotMatch(app, /setTimeout\(resolve, 2500\)/);
  assert.match(app, /Continue without updating/);
  assert.match(interop, /await packageInstaller\(downloadUrl, progress\)/);
  assert.match(interop, /case "installUpdate":\s*result = await StartUpdate/);
});

test('visualization mode chips are as compact as the request tabs', () => {
  const css = read('visualization.css');
  const viz = css.slice(css.indexOf(':root[data-shell="mobile"] .viz-mode'));
  assert.match(viz, /min-height: 40px/);
  assert.match(viz, /flex-direction: row/);
});

test('playlist icons use the shared Font Awesome picker on both platforms', () => {
  const shared = read('playlisticons.js');
  const desktop = read('musicplayer.html');
  const mobile = read('mobilemusic.html');
  assert.match(shared, /SongRequestPlaylistIcons/);
  assert.match(desktop, /playlisticons\.js/);
  assert.match(desktop, /create-playlist-icon-picker/);
  assert.match(mobile, /playlisticons\.js/);
  assert.match(read('mobilemusic.js'), /mm-playlist-icon-picker/);
});

test('artwork colours are broadcast to every mobile page', () => {
  const theme = read('mobiletheme.js');
  const music = read('mobilemusic.js');
  const requests = read('app.js');
  const page = fs.readFileSync(path.join(__dirname, '..', '..', 'SongRequest.Mobile', 'MainPage.cs'), 'utf8');
  assert.match(theme, /SongRequestTheme/);
  assert.match(music, /mobileThemeColors/);
  assert.match(requests, /mobileThemeColors/);
  assert.match(page, /BroadcastThemeColors/);
  assert.match(page, /mobileThemeColors/);
});

test('mobile player shows colour-coded quality and lyrics source in Options', () => {
  const js = read('mobilemusic.js');
  const css = read('mobilemusic.css');
  assert.match(js, /id="mm-options-meta"/);
  assert.match(js, /function setQuality/);
  assert.match(js, /function setLyricsSource/);
  assert.match(css, /\.mm-quality\.is-lossless/);
  assert.match(css, /\.mm-quality\.is-high/);
});

test('mobile seekbar sends a normalized position instead of absolute seconds', () => {
  const js = read('mobilemusic.js');
  assert.match(js, /seekEl\.value \/ 1000/);
  assert.match(js, /send\('seek', \{ position: position \}\)/);
});

test('mobile lyrics view has synchronization and size controls plus landscape layout', () => {
  const html = read('mobilemusic.html');
  const js = read('mobilemusic.js');
  const css = read('mobilemusic.css');

  assert.match(html, /id="mm-lyrics-settings-toggle"/);
  assert.match(js, /id="mm-sync-value"/);
  assert.match(js, /id="mm-font-value"/);
  assert.match(js, /function setLyricsSyncOffset/);
  assert.match(js, /function setLyricsFontScale/);
  assert.match(js, /function toggleLyricsSettings/);

  // The lyrics menu is a bottom sheet like the Options menu, and lyrics mode
  // keeps the chip labels visible.
  assert.match(js, /openSheet\(lyricsSettingsSheetHtml\(\), 'lyrics'\)/);
  assert.match(js, /class="mm-sheet-row"/);
  assert.doesNotMatch(css, /\.mm-player\.is-lyrics \.mm-player-top \.mm-pill-btn > span/);
  assert.match(css, /@media \(orientation: landscape\) and \(max-height: 640px\)/);
  assert.match(css, /grid-template-areas:\s*"top top"\s*"hero lyrics"\s*"bottom lyrics"/);
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

test('add-to-playlist menus exclude generated playlists and keep real icons', () => {
  const desktop = read('musicplayer.js');
  const mobile = read('mobilemusic.js');

  assert.match(desktop, /if \(pl\.isDynamic\) continue;/);
  assert.match(mobile, /filter\(function \(list\) \{ return !list\.isDynamic; \}\)/);
  assert.match(mobile, /plIconHtml\(list\.icon\)/);
});

test('mobile output device selection keeps device index zero', () => {
  const js = read('mobilemusic.js');

  assert.doesNotMatch(js, /parseInt\(this\.value, 10\) \|\| -1/);
  assert.match(js, /isNaN\(number\) \? -1 : number/);
});

test('player time displays keep hours for long tracks', () => {
  const player = read('musicplayer.js');
  const presentation = read('presentation.js');

  assert.match(player, /const h = Math\.floor\(total \/ 3600\)/);
  assert.match(player, /if \(h > 0\) return `\$\{h\}/);
  assert.match(presentation, /var h = Math\.floor\(total \/ 3600\)/);
  assert.match(presentation, /if \(h > 0\) return h \+/);
});

test('presentation zoom controls auto-hide on TV/HDMI and return on activity', () => {
  const js = read('presentation.js');
  const css = read('presentation.css');

  assert.match(js, /function wireZoomAutoHide/);
  assert.match(js, /classList\.add\('is-idle'\)/);
  assert.match(css, /#pres-zoom\.is-idle/);
});

test('mobile stage follows the presentation source and sends plain lyrics as lines', () => {
  const root = path.join(__dirname, '..', '..');
  const server = fs.readFileSync(path.join(root, 'SongRequest.Mobile', 'Platforms', 'Android', 'Stage', 'StageServer.cs'), 'utf8');

  assert.match(server, /MusicSharePresentation\.BuildNowPlaying/);
  assert.match(server, /MusicSharePresentation\.BuildLyrics/);
  assert.match(server, /plainLyrics = plain/);
});

test('visualization frames wait until the native window exists', () => {
  const root = path.join(__dirname, '..', '..');
  const window = fs.readFileSync(path.join(root, 'SongRequestDesktopV2Rewrite', 'NewUiWindow.cs'), 'utf8');

  assert.match(window, /visualizationReady/);
  assert.match(window, /visualizationReady \|\| vw\.WindowHandle == IntPtr\.Zero/);
  // The 30 fps stream starts from WindowCreated, not before the native window exists.
  assert.match(window, /StartVisualizationStreaming/);
});

test('desktop output device menu stays inside the window and scrolls', () => {
  const js = read('musicplayer.js');
  const css = read('musicplayer.css');

  // The menu is repositioned after the real device list replaces the loading
  // placeholder, and its list is capped to the available height.
  assert.match(js, /function positionDeviceMenu/);
  // The menu must leave #player-bar-right so the expanded player cannot hide it.
  assert.match(js, /document\.body\.appendChild\(deviceMenuEl\)/);
  assert.match(js, /renderDeviceMenu\(\(result && result\.devices\) \|\| \[\], deviceSelected\);[\s\S]*?positionDeviceMenu\(\);/);
  assert.match(js, /deviceMenuListEl\.style\.maxHeight/);
  assert.match(css, /#device-menu-list\s*\{[^}]*overflow-y:\s*auto/s);
  assert.match(css, /#device-menu-list\s*\{[^}]*min-height:\s*0/s);
});
