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
