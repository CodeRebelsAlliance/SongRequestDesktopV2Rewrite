// Host <-> page bridge helpers, shared by every UI page.
//
// Three transports exist, tried in this order:
//
//  1. HybridWebView (mobile, .NET MAUI): the framework script
//     `_framework/hybridwebview.js` defines `window.HybridWebView`, which sends a
//     raw string to C# (`SendRawMessage`) and delivers host messages as the
//     `HybridWebViewMessageReceived` window event (detail.message).
//  2. Photino (desktop, all platforms): `window.external.sendMessage(message)`
//     and `window.external.receiveMessage(callback)`.
//  3. A raw WebView2 channel (`window.chrome.webview.postMessage` /
//     `addEventListener('message')`) as a fallback.
//
// NOTE: `receiveMessage` is a function that *takes* the callback. Assigning to it
// (`window.external.receiveMessage = handler`) replaces Photino's shim, after
// which the host can never deliver a message again — the UI then looks like it
// hangs during startup, because every request silently times out. Always call it.
//
// More than one page module can register a handler (the player plus the
// advanced-audio panel). On Photino/WebView2 the transport exposes only one
// callback, so the first registration installs a small dispatcher and every
// later handler is appended to it. This keeps independent modules from silently
// replacing each other's message channel.
(function (global) {
  'use strict';

  var HYBRID_EVENT = 'HybridWebViewMessageReceived';
  var handlers = [];
  var transport = 'none';
  var installed = false;

  function hybridWebView() {
    var hwv = global.HybridWebView;
    return (hwv && typeof hwv.SendRawMessage === 'function') ? hwv : null;
  }

  // The mobile host serves the pages from HybridWebView; mark the document so the
  // responsive stylesheet (mobile.css) can target it and desktop CSS stays as is.
  function markShell() {
    try {
      if (hybridWebView() && global.document && global.document.documentElement) {
        global.document.documentElement.setAttribute('data-shell', 'mobile');
      }
    } catch (e) { /* non-DOM host (tests) */ }
  }

  function dispatch(message) {
    for (var i = 0; i < handlers.length; i++) {
      try { handlers[i](message); }
      catch (e) {
        try { if (global.console) global.console.error('Host message handler failed', e); } catch (_) { }
      }
    }
  }

  function installTransport() {
    if (installed) return;
    installed = true;

    // Mobile: HybridWebView dispatches a window event carrying the raw message.
    if (hybridWebView() && global.addEventListener) {
      global.addEventListener(HYBRID_EVENT, function (e) {
        var message = e && e.detail ? e.detail.message : null;
        if (message !== null && message !== undefined) dispatch(String(message));
      });
      transport = 'hybrid';
      return;
    }

    if (global.external && typeof global.external.receiveMessage === 'function') {
      global.external.receiveMessage(function (message) {
        if (message !== null && message !== undefined) dispatch(String(message));
      });
      transport = 'external';
      return;
    }

    // Fallback for a raw WebView2 channel (Windows) when the shim is not present.
    var chrome = global.chrome;
    if (chrome && chrome.webview && typeof chrome.webview.addEventListener === 'function') {
      chrome.webview.addEventListener('message', function (e) {
        dispatch(typeof e.data === 'string' ? e.data : JSON.stringify(e.data));
      });
      transport = 'webview2';
      return;
    }

    transport = 'none';
  }

  function registerHostMessageHandler(handler) {
    if (typeof handler !== 'function') return 'none';
    handlers.push(handler);
    installTransport();
    return transport;
  }

  /// Sends one JSON string to the host. Returns false when no channel exists, so
  /// callers can log/reject instead of silently dropping the message.
  function sendToHost(message) {
    var hwv = hybridWebView();
    if (hwv) {
      hwv.SendRawMessage(message);
      return true;
    }

    if (global.external && typeof global.external.sendMessage === 'function') {
      global.external.sendMessage(message);
      return true;
    }

    var chrome = global.chrome;
    if (chrome && chrome.webview && typeof chrome.webview.postMessage === 'function') {
      chrome.webview.postMessage(message);
      return true;
    }

    return false;
  }

  /// Transport name, for diagnostics/logging.
  function transportName() {
    if (hybridWebView()) return 'hybrid';
    if (global.external && typeof global.external.sendMessage === 'function') return 'external';
    var chrome = global.chrome;
    if (chrome && chrome.webview && typeof chrome.webview.postMessage === 'function') return 'webview2';
    return 'none';
  }

  var api = {
    registerHostMessageHandler: registerHostMessageHandler,
    sendToHost: sendToHost,
    transportName: transportName,
  };

  markShell();
  global.SongRequestHost = api;

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = api;
  }
})(typeof window !== 'undefined' ? window : globalThis);
