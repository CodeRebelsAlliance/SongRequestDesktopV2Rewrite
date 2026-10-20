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

test('mobile soundboard page loads its own layout and the shared soundboard logic', () => {
  const html = read('mobilesoundboard.html');
  assert.match(html, /mobilesoundboard\.css/);
  assert.match(html, /soundboard\.js/);
  assert.match(html, /mobilesoundboard\.js/);

  for (const id of ['sb-grid', 'sb-edit-modal', 'sb-pages-modal', 'sb-midi-modal', 'sb-keys-modal']) {
    assert.ok(html.includes(`id="${id}"`), `missing #${id}`);
  }
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
