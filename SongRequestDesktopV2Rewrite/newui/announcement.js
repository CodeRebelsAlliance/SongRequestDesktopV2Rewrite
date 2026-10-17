(function () {
  'use strict';

  let requestId = 0;
  const pending = {};
  let _state = { state: 'idle', pushToTalk: true, playSound: true, dimDb: 20 };
  let _pressing = false;

  // ── IPC ────────────────────────────────────────────────────────
  function postMessageToHost(msg) {
    try { window.external.sendMessage(msg); }
    catch (e1) {
      try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(msg); }
      catch (e2) { console.error('Failed to send message:', e2); }
    }
  }

  function send(method, params) {
    return new Promise((resolve, reject) => {
      const id = ++requestId;
      pending[id] = {
        resolve, reject,
        timeout: setTimeout(() => { delete pending[id]; reject(new Error('Request timeout: ' + method)); }, 30000)
      };
      postMessageToHost(JSON.stringify(Object.assign({ type: 'request', id, method }, params || {})));
    });
  }

  function handleIncoming(json) {
    try {
      const msg = JSON.parse(json);
      if (msg.type === 'response') {
        const p = pending[msg.id];
        if (!p) return;
        clearTimeout(p.timeout);
        delete pending[msg.id];
        if (msg.result && msg.result.error) p.reject(new Error(msg.result.error));
        else p.resolve(msg.result);
      } else if (msg.type === 'event') {
        if (msg.eventName === 'announcementState') applyState(msg.data);
      }
    } catch (e) { console.error('Parse error:', e); }
  }

  if (!window.external) window.external = {};
  window.external.receiveMessage = handleIncoming;
  if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
    window.chrome.webview.addEventListener('message', function (e) { handleIncoming(e.data); });
  }

  // ── UI ─────────────────────────────────────────────────────────
  const micBtn = document.getElementById('ann-mic');
  const stateText = document.getElementById('ann-state-text');
  const dimSlider = document.getElementById('ann-dim');
  const dimValue = document.getElementById('ann-dim-value');
  const playSoundCb = document.getElementById('ann-play-sound');
  const pttCb = document.getElementById('ann-push-to-talk');
  const hintText = document.getElementById('ann-hint-text');

  function applyState(s) {
    if (!s) return;
    _state = Object.assign({}, _state, s);
    micBtn.dataset.state = s.state || 'idle';
    stateText.textContent = s.text || '';
    dimSlider.value = s.dimDb != null ? s.dimDb : 20;
    dimValue.textContent = Math.round(dimSlider.value) + ' dB';
    playSoundCb.checked = !!s.playSound;
    pttCb.checked = !!s.pushToTalk;
    updateHint();
  }

  function updateHint() {
    hintText.textContent = pttCb.checked
      ? 'Hold the microphone button while you speak.'
      : 'Click the microphone button to start and click again to stop.';
  }

  // Mic button interactions
  micBtn.addEventListener('mousedown', (e) => {
    e.preventDefault();
    if (pttCb.checked) {
      _pressing = true;
      send('announcementStart').catch(() => { });
    }
  });
  micBtn.addEventListener('mouseup', () => {
    if (pttCb.checked && _pressing) {
      _pressing = false;
      send('announcementStop').catch(() => { });
    }
  });
  micBtn.addEventListener('mouseleave', () => {
    if (pttCb.checked && _pressing) {
      _pressing = false;
      send('announcementStop').catch(() => { });
    }
  });
  micBtn.addEventListener('click', () => {
    if (pttCb.checked) return; // handled by hold
    if (_state.isActive) send('announcementStop').catch(() => { });
    else send('announcementStart').catch(() => { });
  });
  // Touch support for push-to-talk
  micBtn.addEventListener('touchstart', (e) => {
    if (!pttCb.checked) return;
    e.preventDefault();
    _pressing = true;
    send('announcementStart').catch(() => { });
  }, { passive: false });
  micBtn.addEventListener('touchend', (e) => {
    if (!pttCb.checked || !_pressing) return;
    e.preventDefault();
    _pressing = false;
    send('announcementStop').catch(() => { });
  });

  dimSlider.addEventListener('input', function () {
    dimValue.textContent = Math.round(this.value) + ' dB';
  });
  dimSlider.addEventListener('change', function () {
    send('announcementSetDimDb', { value: parseFloat(this.value) }).catch(() => { });
  });

  playSoundCb.addEventListener('change', function () {
    send('announcementSetPlaySound', { enabled: this.checked }).catch(() => { });
  });
  pttCb.addEventListener('change', function () {
    updateHint();
    send('announcementSetPushToTalk', { enabled: this.checked }).catch(() => { });
  });

  document.getElementById('ann-close').addEventListener('click', () => {
    send('announcementClose').catch(() => { });
  });

  // ── Init ───────────────────────────────────────────────────────
  send('announcementGetState').then(applyState).catch(() => { });
})();
