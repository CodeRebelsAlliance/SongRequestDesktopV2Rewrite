// Photino host <-> page bridge helpers, shared by every UI page.
//
// Photino injects (on all platforms):
//
//   window.external = {
//     sendMessage: function (message) { ... },
//     receiveMessage: function (callback) { ...register callback... }
//   };
//
// NOTE: receiveMessage is a function that *takes* the callback. Assigning to it
// (`window.external.receiveMessage = handler`) replaces Photino's shim, after
// which the host can never deliver a message again — the UI then looks like it
// hangs during startup, because every request silently times out. Always call it.
(function (global) {
  'use strict';

  function registerHostMessageHandler(handler) {
    if (global.external && typeof global.external.receiveMessage === 'function') {
      global.external.receiveMessage(handler);
      return 'external';
    }

    // Fallback for a raw WebView2 channel (Windows) when the shim is not present.
    var chrome = global.chrome;
    if (chrome && chrome.webview && typeof chrome.webview.addEventListener === 'function') {
      chrome.webview.addEventListener('message', function (e) {
        handler(typeof e.data === 'string' ? e.data : JSON.stringify(e.data));
      });
      return 'webview2';
    }

    return 'none';
  }

  var api = { registerHostMessageHandler: registerHostMessageHandler };
  global.SongRequestHost = api;

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = api;
  }
})(typeof window !== 'undefined' ? window : globalThis);
