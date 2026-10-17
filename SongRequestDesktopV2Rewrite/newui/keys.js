// Shared keyboard-gesture helpers.
// Converts a browser KeyboardEvent into the same gesture string format the
// remote-control / soundboard back-end expects (e.g. "Ctrl+Alt+P", "PageUp").
(function () {
  'use strict';

  const WPF_KEY_MAP = {
    ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
    Space: 'Space', Enter: 'Return', NumpadEnter: 'Return', Escape: 'Escape', Tab: 'Tab',
    Backspace: 'Back', Delete: 'Delete', Insert: 'Insert', Home: 'Home', End: 'End',
    PageUp: 'PageUp', PageDown: 'PageDown',
    Minus: 'Minus', Equal: 'Plus', BracketLeft: 'OemOpenBrackets', BracketRight: 'OemCloseBrackets',
    Semicolon: 'OemSemicolon', Quote: 'OemQuotes', Comma: 'OemComma', Period: 'OemPeriod',
    Slash: 'OemQuestion', Backslash: 'OemPipe', Backquote: 'OemTilde',
    NumpadAdd: 'Add', NumpadSubtract: 'Subtract', NumpadMultiply: 'Multiply',
    NumpadDivide: 'Divide', NumpadDecimal: 'Decimal',
    CapsLock: 'CapsLock', NumLock: 'NumLock', ScrollLock: 'Scroll',
    PrintScreen: 'Snapshot', Pause: 'Pause', ContextMenu: 'Apps'
  };

  function codeToWpfKey(code, key) {
    if (!code) return (key && key.length === 1) ? key.toUpperCase() : null;
    if (code.indexOf('Key') === 0) return code.slice(3);
    if (code.indexOf('Digit') === 0) return 'D' + code.slice(5);
    if (/^F\d{1,2}$/.test(code)) return code;
    if (/^Numpad\d$/.test(code)) return 'NumPad' + code.slice(6);
    if (WPF_KEY_MAP[code]) return WPF_KEY_MAP[code];
    if (key && key.length === 1) return key.toUpperCase();
    return null;
  }

  // Returns null when only modifier keys are involved (nothing assignable yet).
  function gestureFromEvent(e) {
    const key = codeToWpfKey(e.code, e.key);
    if (!key) return null;
    const mods = [];
    if (e.ctrlKey) mods.push('Ctrl');
    if (e.altKey) mods.push('Alt');
    if (e.shiftKey) mods.push('Shift');
    if (e.metaKey) mods.push('Win');
    return mods.concat([key]).join('+');
  }

  window.SRKeys = { codeToWpfKey: codeToWpfKey, gestureFromEvent: gestureFromEvent };
})();
