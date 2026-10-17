// Tests for the shared keyboard-gesture helper (newui/keys.js).
// Run with: node --test tests/js
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

// keys.js is a browser IIFE that assigns window.SRKeys.
globalThis.window = globalThis;
require(path.join(__dirname, '..', '..', 'SongRequestDesktopV2Rewrite', 'newui', 'keys.js'));

const { codeToWpfKey, gestureFromEvent } = globalThis.window.SRKeys;

test('codeToWpfKey maps letters, digits, function and numpad keys', () => {
  assert.equal(codeToWpfKey('KeyA', 'a'), 'A');
  assert.equal(codeToWpfKey('KeyZ', 'z'), 'Z');
  assert.equal(codeToWpfKey('Digit1', '1'), 'D1');
  assert.equal(codeToWpfKey('F5', 'F5'), 'F5');
  assert.equal(codeToWpfKey('Numpad3', '3'), 'NumPad3');
  assert.equal(codeToWpfKey('NumpadAdd', '+'), 'Add');
});

test('codeToWpfKey maps OEM and navigation keys to WPF names', () => {
  assert.equal(codeToWpfKey('ArrowUp', 'ArrowUp'), 'Up');
  assert.equal(codeToWpfKey('PageUp', 'PageUp'), 'PageUp');
  assert.equal(codeToWpfKey('BracketLeft', '['), 'OemOpenBrackets');
  assert.equal(codeToWpfKey('Backquote', '`'), 'OemTilde');
  assert.equal(codeToWpfKey('Semicolon', ';'), 'OemSemicolon');
  assert.equal(codeToWpfKey('Space', ' '), 'Space');
  assert.equal(codeToWpfKey('Enter', 'Enter'), 'Return');
});

test('codeToWpfKey returns null for pure modifier keys', () => {
  assert.equal(codeToWpfKey('ControlLeft', 'Control'), null);
  assert.equal(codeToWpfKey('ShiftRight', 'Shift'), null);
  assert.equal(codeToWpfKey('AltLeft', 'Alt'), null);
  assert.equal(codeToWpfKey('MetaLeft', 'Meta'), null);
});

test('codeToWpfKey falls back to the key value when code is missing', () => {
  assert.equal(codeToWpfKey(undefined, 'a'), 'A');
  assert.equal(codeToWpfKey('', '7'), '7');
  assert.equal(codeToWpfKey(undefined, 'Shift'), null);
});

test('gestureFromEvent builds modifier chains in Ctrl/Alt/Shift/Win order', () => {
  assert.equal(gestureFromEvent({ code: 'KeyP', key: 'p' }), 'P');
  assert.equal(gestureFromEvent({ code: 'KeyP', key: 'p', ctrlKey: true }), 'Ctrl+P');
  assert.equal(gestureFromEvent({ code: 'KeyP', key: 'p', ctrlKey: true, altKey: true }), 'Ctrl+Alt+P');
  assert.equal(
    gestureFromEvent({ code: 'KeyP', key: 'p', ctrlKey: true, altKey: true, shiftKey: true, metaKey: true }),
    'Ctrl+Alt+Shift+Win+P'
  );
  assert.equal(gestureFromEvent({ code: 'PageUp', key: 'PageUp', shiftKey: true }), 'Shift+PageUp');
});

test('gestureFromEvent returns null for modifier-only presses', () => {
  assert.equal(gestureFromEvent({ code: 'ControlLeft', key: 'Control', ctrlKey: true }), null);
});
