(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  function toast(message, type) {
    var host = $('ms-toasts');
    if (!host) {
      host = document.createElement('div');
      host.id = 'ms-toasts';
      document.body.appendChild(host);
    }

    var el = document.createElement('div');
    el.className = 'ms-toast' + (type ? ' is-' + type : '');
    el.textContent = message;
    host.appendChild(el);

    setTimeout(function () { el.classList.add('is-out'); }, 2200);
    setTimeout(function () { if (el.parentNode) el.remove(); }, 2700);
  }

  // ── Bridge ───────────────────────────────────────────────
  var pending = {};
  var requestId = 0;

  function request(method, params, timeoutMs) {
    return new Promise(function (resolve, reject) {
      var id = ++requestId;
      var timer = setTimeout(function () {
        delete pending[id];
        reject(new Error('timeout: ' + method));
      }, timeoutMs || 20000);

      pending[id] = { resolve: resolve, timer: timer };

      var message = { type: 'request', id: id, method: method };
      if (params) for (var key in params) message[key] = params[key];

      if (!SongRequestHost.sendToHost(JSON.stringify(message))) {
        clearTimeout(timer);
        delete pending[id];
        reject(new Error('no host channel'));
      }
    });
  }

  function send(method, params) {
    var message = { type: 'request', id: 0, method: method };
    if (params) for (var key in params) message[key] = params[key];
    SongRequestHost.sendToHost(JSON.stringify(message));
  }

  SongRequestHost.registerHostMessageHandler(function (raw) {
    try {
      var message = JSON.parse(raw);

      if (message.type === 'response') {
        var entry = pending[message.id];
        if (entry) {
          clearTimeout(entry.timer);
          delete pending[message.id];
          entry.resolve(message.result);
        }
        return;
      }

      if (message.type === 'event') {
        if (message.eventName === 'stageStatus' || message.eventName === 'stageServer') {
          applyStatus(message.data || {});
        } else if (message.eventName === 'mobileThemeColors') {
          if (window.SongRequestTheme) window.SongRequestTheme.apply(message.data || {}, 'ms', '#211f26', '#2a2632');
        } else if (message.eventName === 'refresh') {
          loadStatus();
        }
      }
    } catch (error) { /* ignore */ }
  });

  // ── State ────────────────────────────────────────────────
  var status = { running: false, url: null, qr: null, zoom: 0.5, operatingMode: 'SongRequests' };
  var source = 'player';

  var preview = $('ms-preview');
  var previewWrap = $('ms-preview-wrap');
  var previewEmpty = $('ms-preview-empty');

  function applyStatus(data) {
    if (!data) return;

    if (data.running != null) status.running = !!data.running;
    if (data.url !== undefined) status.url = data.url;
    if (data.qr !== undefined) status.qr = data.qr;
    if (data.zoom != null) status.zoom = data.zoom;
    if (data.presentationSource) source = data.presentationSource;
    if (data.operatingMode) status.operatingMode = data.operatingMode;

    render();
    requestAnimationFrame(fitPreview);
  }

  function render() {
    var badge = $('ms-status');
    badge.textContent = status.running ? 'Live' : 'Stopped';
    badge.classList.toggle('is-live', status.running);

    $('ms-url').textContent = status.running && status.url ? status.url : 'Not running';
    $('ms-copy').disabled = !(status.running && status.url);

    var qr = $('ms-qr');
    qr.hidden = !(status.running && status.qr);
    if (qr.hidden) qr.removeAttribute('src');
    else qr.src = status.qr;

    $('ms-start').innerHTML = status.running
      ? '<i class="fas fa-stop"></i> Stop stage'
      : '<i class="fas fa-play"></i> Start stage';

    $('ms-zoom-value').textContent = Math.round(status.zoom * 100) + '%';

    previewEmpty.hidden = !status.running ? false : previewLoaded;
    previewEmpty.innerHTML = status.running
      ? '<i class="fas fa-circle-notch fa-spin"></i><span>Loading preview…</span>'
      : '<i class="fas fa-tv"></i><span>Start the stage to preview and share it.</span>';

    if (!status.running) {
      preview.removeAttribute('src');
      previewLoaded = false;
    } else if (preview.getAttribute('src') !== status.url) {
      previewLoaded = false;
      previewEmpty.hidden = false;
      preview.src = status.url;
    }

    updateSourceButtons();
    updateModeButtons();
  }

  var previewLoaded = false;
  preview.addEventListener('load', function () {
    if (preview.getAttribute('src')) {
      previewLoaded = true;
      previewEmpty.hidden = true;
    }
  });

  function updateSourceButtons() {
    document.querySelectorAll('#ms-source button').forEach(function (button) {
      button.classList.toggle('is-active', button.dataset.source === source);
    });
  }

  function updateModeButtons() {
    document.querySelectorAll('#ms-mode button').forEach(function (button) {
      button.classList.toggle('is-active', button.dataset.mode === status.operatingMode);
    });
  }

  function fitPreview() {
    if (!previewWrap || !previewWrap.clientWidth) return;
    var scale = previewWrap.clientWidth / 1280;
    preview.style.transform = 'scale(' + scale + ')';
  }

  window.addEventListener('resize', fitPreview);

  // ── Actions ──────────────────────────────────────────────
  function loadStatus() {
    request('stageStatus').then(applyStatus).catch(function () { });
  }

  $('ms-start').addEventListener('click', function () {
    if (status.running) {
      request('stageStop').then(applyStatus).catch(function (error) { toast(error.message, 'error'); });
      return;
    }

    request('stageStart').then(applyStatus).catch(function (error) { toast(error.message, 'error'); });
  });

  $('ms-copy').addEventListener('click', function () {
    if (!status.url) return;

    try {
      navigator.clipboard.writeText(status.url).then(function () {
        toast('Link copied', 'success');
      }).catch(function () {
        toast(status.url);
      });
    } catch (error) {
      toast(status.url);
    }
  });

  $('ms-zoom-out').addEventListener('click', function () {
    setZoom(status.zoom - 0.1);
  });

  $('ms-zoom-in').addEventListener('click', function () {
    setZoom(status.zoom + 0.1);
  });

  function setZoom(value) {
    var zoom = Math.min(2, Math.max(0.5, Math.round(value * 100) / 100));
    status.zoom = zoom;
    $('ms-zoom-value').textContent = Math.round(zoom * 100) + '%';
    send('setPresentationZoom', { zoom: zoom });
  }

  $('ms-mode').addEventListener('click', function (event) {
    var button = event.target.closest('button[data-mode]');
    if (!button) return;

    status.operatingMode = button.dataset.mode;
    updateModeButtons();
    send('setOperatingMode', { mode: status.operatingMode });
  });

  $('ms-source').addEventListener('click', function (event) {
    var button = event.target.closest('button[data-source]');
    if (!button) return;

    source = button.dataset.source;
    updateSourceButtons();
    send('setPresentationSource', { source: source });
  });

  // ── Start ────────────────────────────────────────────────
  request('stageStatus').then(function (data) {
    applyStatus(data || {});
    fitPreview();
  }).catch(function () {
    render();
    fitPreview();
  });

  loadStatus();
})();
