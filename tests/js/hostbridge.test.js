// Tests for the shared Photino host bridge helper (newui/hostbridge.js).
// Run with: node --test tests/js
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const bridgePath = path.join(__dirname, '..', '..', 'SongRequestDesktopV2Rewrite', 'newui', 'hostbridge.js');

function load() {
  // Fresh module + a fresh fake `window` per test.
  delete require.cache[require.resolve(bridgePath)];
  return require(bridgePath);
}

// Mirrors the shim Photino.Native injects on Linux and Windows.
function photinoShim() {
  const callbacks = [];
  const external = {
    sent: [],
    sendMessage(message) { this.sent.push(message); },
    receiveMessage(callback) { callbacks.push(callback); },
  };
  return { external, dispatch: (message) => callbacks.forEach((cb) => cb(message)), callbackCount: () => callbacks.length };
}

test('uses window.external.receiveMessage by CALLING it (never overwriting the shim)', () => {
  const shim = photinoShim();
  global.window = global;
  global.external = shim.external;
  delete global.chrome;

  try {
    const { registerHostMessageHandler } = load();
    const received = [];
    const result = registerHostMessageHandler((m) => received.push(m));

    assert.equal(result, 'external');
    // The regression this guards: the shim must still be a function afterwards.
    assert.equal(typeof global.external.receiveMessage, 'function');
    assert.equal(shim.callbackCount(), 1);

    // And host messages must actually reach the handler.
    shim.dispatch('{"type":"response","id":1}');
    assert.deepEqual(received, ['{"type":"response","id":1}']);
  } finally {
    delete global.external;
    delete global.window;
  }
});

test('falls back to chrome.webview when the Photino shim is missing', () => {
  global.window = global;
  delete global.external;
  const listeners = [];
  global.chrome = { webview: { addEventListener: (name, cb) => listeners.push({ name, cb }) } };

  try {
    const { registerHostMessageHandler } = load();
    const received = [];
    const result = registerHostMessageHandler((m) => received.push(m));

    assert.equal(result, 'webview2');
    assert.equal(listeners.length, 1);
    assert.equal(listeners[0].name, 'message');

    listeners[0].cb({ data: '{"type":"event"}' });
    assert.deepEqual(received, ['{"type":"event"}']);
  } finally {
    delete global.chrome;
    delete global.window;
  }
});

test('reports when no host channel is available', () => {
  global.window = global;
  delete global.external;
  delete global.chrome;

  try {
    const { registerHostMessageHandler } = load();
    assert.equal(registerHostMessageHandler(() => {}), 'none');
  } finally {
    delete global.window;
  }
});

test('exposes the helper on window for the page scripts', () => {
  global.window = global;
  delete global.external;
  delete global.chrome;

  try {
    load();
    assert.equal(typeof global.SongRequestHost.registerHostMessageHandler, 'function');
  } finally {
    delete global.SongRequestHost;
    delete global.window;
  }
});
