// Tests for the HTTP bridge the mobile stage server injects into presentation.html.
//
// The bridge is a C# raw string in StageServer.cs. It used to compare the *whole*
// /state document, so the ticking currentTime made the queue and lyrics look new on
// every 500 ms poll: the TV rebuilt Up Next and reset the lyrics scroll. These
// tests pin the per-section diff.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const serverPath = path.join(__dirname, '..', '..', 'SongRequest.Mobile', 'Platforms', 'Android', 'Stage', 'StageServer.cs');

function bridgeScript() {
  const source = fs.readFileSync(serverPath, 'utf8');
  const marker = 'private const string BridgeScript = """';
  const start = source.indexOf(marker);
  assert.notEqual(start, -1, 'BridgeScript raw string not found');
  const bodyStart = start + marker.length;
  const end = source.indexOf('""";', bodyStart);
  assert.notEqual(end, -1, 'end of BridgeScript raw string not found');
  return source.slice(bodyStart, end);
}

function makeHarness(states) {
  const emitted = [];
  let index = 0;
  let interval = null;

  const window = {};
  const context = vm.createContext({
    window,
    JSON,
    Promise,
    console,
    fetch: () => Promise.resolve({
      text: () => Promise.resolve(JSON.stringify(states[Math.min(index++, states.length - 1)])),
    }),
    setInterval: (fn) => { interval = fn; return 1; },
  });

  vm.runInContext(bridgeScript(), context, { filename: 'bridge-http.js' });
  const host = window.SongRequestHost;
  assert.ok(host, 'bridge must install window.SongRequestHost');

  const handler = (raw) => emitted.push(JSON.parse(raw));
  const transport = host.registerHostMessageHandler(handler);
  assert.equal(transport, 'http');

  return {
    host,
    emitted,
    next: () => interval(),
  };
}

const flush = () => new Promise((resolve) => setImmediate(resolve));

test('stage bridge emits each section only when that section changes', async () => {
  const queue = [{ title: 'Next', thumbnail: 'data:image/png;base64,AA==' }];
  const lyrics = { syncedLyrics: [{ time: 1, text: 'line' }], plainLyrics: [], provider: 'LRCLIB' };
  const settings = {
    lyricsFontScale: 1,
    lyricsHidden: false,
    presentationZoom: 0.5,
    requestUrl: 'https://example.test/request',
    requestQr: 'data:image/png;base64,BB==',
  };

  const harness = makeHarness([
    { nowPlaying: { title: 'Song', currentTime: 1, totalTime: 60 }, queue, lyrics, settings },
    { nowPlaying: { title: 'Song', currentTime: 2, totalTime: 60 }, queue, lyrics, settings },
    { nowPlaying: { title: 'Song', currentTime: 2, totalTime: 60 }, queue: [], lyrics, settings },
    { nowPlaying: { title: 'Next', currentTime: 0, totalTime: 90 }, queue: [], lyrics: null, settings },
  ]);

  await flush();
  assert.deepEqual(harness.emitted.map((e) => e.eventName), [
    'nowPlayingUpdate', 'presentationQueueUpdate', 'lyricsUpdate', 'presentationSettings',
  ]);

  harness.emitted.length = 0;
  await harness.next();
  await flush();
  // Only the ticking clock changed: no queue/lyrics/settings rebuild.
  assert.deepEqual(harness.emitted.map((e) => e.eventName), ['nowPlayingUpdate']);

  harness.emitted.length = 0;
  await harness.next();
  await flush();
  assert.deepEqual(harness.emitted.map((e) => e.eventName), ['presentationQueueUpdate']);

  harness.emitted.length = 0;
  await harness.next();
  await flush();
  // New song: nowPlaying plus an empty lyrics object (clears the old lines).
  assert.deepEqual(harness.emitted.map((e) => e.eventName), ['nowPlayingUpdate', 'lyricsUpdate']);
  assert.deepEqual(harness.emitted[1].data.syncedLyrics, []);
  assert.deepEqual(harness.emitted[1].data.plainLyrics, []);
});

test('stage bridge reads the presentation settings field names', async () => {
  const harness = makeHarness([{
    nowPlaying: null,
    queue: [],
    lyrics: null,
    settings: {
      lyricsFontScale: 1.25,
      lyricsHidden: true,
      presentationZoom: 1.5,
      requestUrl: 'https://example.test/request',
      requestQr: 'data:image/png;base64,BB==',
    },
  }]);

  await flush();
  const settings = harness.emitted.find((e) => e.eventName === 'presentationSettings');
  assert.ok(settings);
  assert.equal(settings.data.lyricsFontScale, 1.25);
  assert.equal(settings.data.lyricsHidden, true);
  assert.equal(settings.data.requestQr, 'data:image/png;base64,BB==');
});
