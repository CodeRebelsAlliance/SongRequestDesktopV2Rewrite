// Advanced audio UI: equalizer, sleep timer and song analysis.
//
// Shared by the desktop player (musicplayer.html) and the touch player
// (mobilemusic.html). It talks to the host through the same JSON bridge as the
// rest of the UI, so desktop (Photino) and mobile (HybridWebView) run exactly the
// same features. The panel is a centered modal on desktop and a Material bottom
// sheet when the document is marked data-shell="mobile".
(function (global) {
  'use strict';

  var requestId = 0;
  var pending = {};
  var state = {
    equalizer: null,
    sleep: null,
    analysis: null,
    analysisTarget: null,
    analysisProgress: null,
    analysisLoading: false,
    view: null,
    sleepTimerHandle: null,
    currentSongPath: '',
    progressAnalysis: null,
    analyses: {},
    autoAnalyzeSongs: true,
    karaokeMode: false,
    analysisPending: false,
    analysisPendingProgress: 0,
    analysisPendingSince: 0
  };
  var dom = null;

  function isMobile() {
    try {
      return global.document && global.document.documentElement &&
        global.document.documentElement.getAttribute('data-shell') === 'mobile';
    } catch (e) { return false; }
  }

  function host() {
    return global.SongRequestHost;
  }

  function send(message) {
    var bridge = host();
    if (!bridge) return false;
    return bridge.sendToHost(JSON.stringify(message));
  }

  function request(method, params, timeoutMs) {
    return new Promise(function (resolve, reject) {
      var id = 'ad-' + (++requestId);
      var timeout = timeoutMs || 20000;
      var timer = setTimeout(function () {
        delete pending[id];
        reject(new Error('Timed out: ' + method));
      }, timeout);

      pending[id] = {
        resolve: function (value) { clearTimeout(timer); resolve(value); },
        reject: function (error) { clearTimeout(timer); reject(error); }
      };

      var message = { type: 'request', id: id, method: method };
      if (params) for (var key in params) message[key] = params[key];
      if (!send(message)) {
        clearTimeout(timer);
        delete pending[id];
        reject(new Error('No host bridge'));
      }
    });
  }

  function registerHandler() {
    var bridge = host();
    if (!bridge) return;
    bridge.registerHostMessageHandler(function (raw) {
      var message;
      try { message = JSON.parse(raw); } catch (e) { return; }

      if (message.type === 'response') {
        var entry = pending[message.id];
        if (!entry) return;
        delete pending[message.id];
        if (message.result && message.result.error && message.result.success === false) {
          entry.reject(new Error(message.result.error));
        } else {
          entry.resolve(message.result);
        }
        return;
      }

      if (message.type !== 'event') return;
      var data = message.data || {};
      switch (message.eventName) {
        case 'equalizerUpdate':
          state.equalizer = data;
          if (state.view === 'equalizer') render();
          break;
        case 'sleepTimerUpdate':
          state.sleep = data;
          updateSleepPill();
          if (state.view === 'sleep') render();
          break;
        case 'songAnalysisProgress':
          state.analysisProgress = data;
          if (samePath(data.songPath, state.currentSongPath)) {
            state.analysisPending = true;
            state.analysisPendingProgress = Number(data.progress) || 0;
            state.analysisPendingSince = state.analysisPendingSince || Date.now();
            updateAnalysisPendingUi();
          }
          if (state.view === 'analysis') renderProgress();
          break;
        case 'songAnalysisUpdate':
          cacheAnalysis(data);
          if (state.view === 'analysis' && panelAnalysisMatches(data)) {
            state.analysis = data;
            state.analysisLoading = false;
            state.analysisProgress = null;
            render();
          }
          break;
        case 'nowPlayingUpdate':
          handleNowPlaying(data);
          break;
        case 'songAnalysisUnavailable':
          if (!data.songPath || samePath(data.songPath, state.currentSongPath)) {
            state.analysisPending = false;
            state.analysisPendingProgress = 0;
            state.analysisPendingSince = 0;
            updateAnalysisPendingUi();
          }
          break;
        case 'karaokeModeChanged':
          applyKaraokeMode(data.enabled === true);
          break;
        case 'refresh':
          refreshStates();
          break;
      }
    });
  }

  function pathKey(value) {
    return String(value || '').replace(/\\/g, '/').toLowerCase();
  }

  function samePath(a, b) {
    var ka = pathKey(a);
    var kb = pathKey(b);
    if (!ka || !kb) return false;
    if (ka === kb) return true;

    // Mobile stores library paths with the "~/" data-root prefix, while the
    // analysis resolves them to an absolute sandbox path. Compare the relative
    // suffix so chapters still attach to the playing track.
    if (ka.indexOf('~/') === 0 || kb.indexOf('~/') === 0) {
      var a2 = ka.replace(/^~\//, '');
      var b2 = kb.replace(/^~\//, '');
      return a2 === b2 || a2.endsWith('/' + b2) || b2.endsWith('/' + a2);
    }
    return false;
  }

  function panelAnalysisMatches(data) {
    if (!data) return false;
    var target = state.analysisTarget;
    if (!target) return true;
    if (target.songId) return true; // the panel requested this song explicitly
    return !target.songPath || samePath(target.songPath, data.songPath);
  }

  function cacheAnalysis(data) {
    if (!data || !data.songPath) return;
    state.analyses[pathKey(data.songPath)] = data;
    if (samePath(state.currentSongPath, data.songPath)) {
      state.progressAnalysis = data;
      state.analysisPending = false;
      state.analysisPendingProgress = 1;
      renderTrackMarkers();
      updateBpmBadge();
      updateAnalysisPendingUi();
      decorateLyrics();
    }
  }

  function handleNowPlaying(data) {
    var path = data.currentSongPath || data.songPath || '';
    var changed = pathKey(path) !== pathKey(state.currentSongPath);
    if (changed) {
      state.currentSongPath = path;
      state.progressAnalysis = path ? (state.analyses[pathKey(path)] || null) : null;
      if (state.progressAnalysis) {
        state.analysisPending = false;
      } else if (path && state.autoAnalyzeSongs !== false) {
        startAnalysisPending();
      } else {
        clearAnalysisPending();
      }
      renderTrackMarkers();
      updateBpmBadge();
      if (path) {
        // Fetch a cached analysis (or the one auto-analysis just produced) for
        // this track so the chapters appear without opening the panel.
        request('getSongAnalysis', { songPath: path }).then(function (result) {
          if (result && result.songPath) cacheAnalysis(result);
        }).catch(function () { });
      }
    }
    updateBpmBadge();
    updateAnalysisPendingUi();
    decorateLyrics();
  }

  function startAnalysisPending() {
    state.analysisPending = true;
    state.analysisPendingProgress = 0;
    state.analysisPendingSince = Date.now();
    updateAnalysisPendingUi();
  }

  function clearAnalysisPending() {
    state.analysisPending = false;
    state.analysisPendingProgress = 0;
    state.analysisPendingSince = 0;
    updateAnalysisPendingUi();
  }

  function applyKaraokeMode(enabled) {
    var changed = state.karaokeMode !== !!enabled;
    state.karaokeMode = !!enabled;
    if (global.SongRequestKaraoke) global.SongRequestKaraoke.setEnabled(!!enabled);
    if (changed && state.karaokeMode) {
      // Re-request lyrics so the host can attach word timings derived from the
      // now-current analysis (the initial lyrics may have arrived before it).
      request('requestLyrics').catch(function () { });
    }
  }

  function refreshStates() {
    request('getSettings').then(function (cfg) {
      state.autoAnalyzeSongs = !cfg || cfg.autoAnalyzeSongs !== false;
      applyKaraokeMode(cfg && cfg.karaokeMode === true);
      if (state.autoAnalyzeSongs && state.currentSongPath && !state.progressAnalysis) {
        startAnalysisPending();
      } else if (!state.autoAnalyzeSongs) {
        clearAnalysisPending();
      }
    }).catch(function () { });

    request('getEqualizer').then(function (eq) {
      state.equalizer = eq;
      if (state.view === 'equalizer') render();
    }).catch(function () { });

    request('getSleepTimer').then(function (sleep) {
      state.sleep = sleep;
      updateSleepPill();
      if (state.view === 'sleep') render();
    }).catch(function () { });

    if (state.analysisTarget && state.view === 'analysis') {
      request('getSongAnalysis', state.analysisTarget).then(function (result) {
        if (result && result.songPath) {
          state.analysis = result;
          state.analysisLoading = false;
          cacheAnalysis(result);
          render();
        }
      }).catch(function () { });
    }

    if (state.currentSongPath) {
      request('getSongAnalysis', { songPath: state.currentSongPath }).then(function (result) {
        if (result && result.songPath) cacheAnalysis(result);
      }).catch(function () { });
    } else {
      request('getSongAnalysis', {}).then(function (result) {
        if (result && result.songPath) {
          state.currentSongPath = result.songPath;
          state.progressAnalysis = result;
          state.analyses[pathKey(result.songPath)] = result;
          renderTrackMarkers();
          updateBpmBadge();
        }
      }).catch(function () { });
    }
  }

  // ── Formatting helpers (exported for tests) ────────────────────────────
  function pad(n) { return (n < 10 ? '0' : '') + n; }

  function formatRemaining(seconds) {
    seconds = Math.max(0, Math.round(Number(seconds) || 0));
    var h = Math.floor(seconds / 3600);
    var m = Math.floor((seconds % 3600) / 60);
    var s = seconds % 60;
    return h > 0 ? h + ':' + pad(m) + ':' + pad(s) : m + ':' + pad(s);
  }

  function formatMinutes(seconds) {
    seconds = Math.max(0, Math.round(Number(seconds) || 0));
    if (seconds >= 3600) {
      var h = Math.floor(seconds / 3600);
      var m = Math.round((seconds % 3600) / 60);
      return h + ' h' + (m > 0 ? ' ' + m + ' min' : '');
    }
    return Math.max(1, Math.round(seconds / 60)) + ' min';
  }

  function esc(value) {
    return String(value == null ? '' : value)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  // ── DOM scaffolding ────────────────────────────────────────────────────
  function ensureDom() {
    if (dom) return dom;
    var doc = global.document;
    if (!doc) return null;

    var overlay = doc.getElementById('ad-overlay');
    if (!overlay) {
      overlay = doc.createElement('div');
      overlay.id = 'ad-overlay';
      overlay.className = 'ad-overlay';
      overlay.hidden = true;
      overlay.innerHTML =
        '<div id="ad-panel" class="ad-panel" role="dialog" aria-modal="true">' +
          '<div class="ad-handle"></div>' +
          '<header class="ad-head">' +
            '<div class="ad-head-title"><i id="ad-head-icon" class="fas fa-sliders"></i><span id="ad-head-text">Audio</span></div>' +
            '<button id="ad-close" class="ad-close" aria-label="Close"><i class="fas fa-times"></i></button>' +
          '</header>' +
          '<div id="ad-body" class="ad-body"></div>' +
        '</div>';
      doc.body.appendChild(overlay);
      overlay.addEventListener('click', function (event) {
        if (event.target === overlay) close();
      });
      overlay.querySelector('#ad-close').addEventListener('click', close);
    }

    var toast = doc.getElementById('ad-toast');
    if (!toast) {
      toast = doc.createElement('div');
      toast.id = 'ad-toast';
      toast.className = 'ad-toast';
      toast.hidden = true;
      doc.body.appendChild(toast);
    }

    dom = {
      overlay: overlay,
      panel: overlay.querySelector('#ad-panel'),
      icon: overlay.querySelector('#ad-head-icon'),
      title: overlay.querySelector('#ad-head-text'),
      body: overlay.querySelector('#ad-body'),
      toast: toast
    };
    return dom;
  }

  function open(view) {
    if (!ensureDom()) return;
    state.view = view;
    dom.overlay.hidden = false;
    global.requestAnimationFrame(function () { dom.overlay.classList.add('is-open'); });
    render();
    refreshStates();
  }

  function close() {
    if (!dom) return;
    dom.overlay.classList.remove('is-open');
    setTimeout(function () {
      if (!dom.overlay.classList.contains('is-open')) dom.overlay.hidden = true;
    }, 220);
    state.view = null;
  }

  function toast(message, isError) {
    if (!ensureDom()) return;
    dom.toast.textContent = message;
    dom.toast.className = 'ad-toast' + (isError ? ' is-error' : '');
    dom.toast.hidden = false;
    clearTimeout(toast._timer);
    toast._timer = setTimeout(function () { dom.toast.hidden = true; }, 2600);
  }

  function setTitle(icon, text) {
    if (!dom) return;
    dom.icon.className = 'fas ' + icon;
    dom.title.textContent = text;
  }

  // ── Render dispatch ────────────────────────────────────────────────────
  function render() {
    if (!dom) return;
    if (state.view === 'equalizer') renderEqualizer();
    else if (state.view === 'sleep') renderSleep();
    else if (state.view === 'analysis') renderAnalysis();
  }

  // ── Equalizer ──────────────────────────────────────────────────────────
  function renderEqualizer() {
    setTitle('fa-sliders', 'Equalizer');
    var eq = state.equalizer;
    if (!eq) {
      dom.body.innerHTML = '<div class="ad-empty"><i class="fas fa-circle-notch fa-spin"></i> Loading equalizer…</div>';
      return;
    }

    var presets = eq.presets || [];
    var selected = eq.preset || 'Flat';
    var options = presets.map(function (preset) {
      return '<option value="' + esc(preset.name) + '"' + (preset.name === selected ? ' selected' : '') + '>' +
        esc(preset.name) + (preset.builtIn ? '' : ' (user)') + '</option>';
    }).join('');

    var bands = (eq.bands || []).map(function (band) {
      return '<div class="ad-band" data-index="' + band.index + '">' +
        '<div class="ad-band-value" id="ad-band-value-' + band.index + '">' + formatGain(band.gainDb) + '</div>' +
        '<input class="ad-band-slider" data-index="' + band.index + '" type="range" min="-12" max="12" step="1" value="' + band.gainDb + '" aria-label="' + esc(band.label) + ' Hz">' +
        '<div class="ad-band-label">' + esc(band.label) + '</div>' +
      '</div>';
    }).join('');

    dom.body.innerHTML =
      '<div class="ad-eq-top">' +
        '<label class="ad-switch"><input id="ad-eq-enabled" type="checkbox"' + (eq.enabled ? ' checked' : '') + '><span class="ad-switch-track"></span><span class="ad-switch-label">' + (eq.enabled ? 'On' : 'Off') + '</span></label>' +
        '<div class="ad-field ad-preset-field"><label for="ad-eq-preset">Preset</label>' +
          '<select id="ad-eq-preset">' + options + '</select></div>' +
        '<button id="ad-eq-reset" class="ad-btn ad-quiet" title="Reset equalizer"><i class="fas fa-rotate-left"></i></button>' +
      '</div>' +
      '<div class="ad-eq-graph-wrap"><canvas id="ad-eq-graph" class="ad-graph ad-eq-graph"></canvas>' +
        '<div class="ad-eq-hint">Drag a band to shape the curve. Changes apply live.</div></div>' +
      '<div class="ad-bands">' + bands + '</div>' +
      '<div class="ad-row">' +
        '<div class="ad-field ad-preamp"><label for="ad-eq-preamp">Pre-amp <span id="ad-eq-preamp-value">' + formatGain(eq.preampDb) + '</span></label>' +
          '<input id="ad-eq-preamp" type="range" min="-12" max="12" step="1" value="' + eq.preampDb + '"></div>' +
      '</div>' +
      '<div class="ad-save-row">' +
        '<input id="ad-eq-preset-name" class="ad-input" maxlength="48" placeholder="Save current curve as…">' +
        '<button id="ad-eq-save" class="ad-btn"><i class="fas fa-floppy-disk"></i> Save</button>' +
        (isUserPreset(presets, selected)
          ? '<button id="ad-eq-delete" class="ad-btn ad-danger" title="Delete preset"><i class="fas fa-trash"></i></button>'
          : '') +
      '</div>';

    wireEqualizer(eq);
    drawEqualizerGraph(eq);
  }

  function isUserPreset(presets, name) {
    for (var i = 0; i < presets.length; i++) {
      if (presets[i].name === name && !presets[i].builtIn) return true;
    }
    return false;
  }

  function formatGain(value) {
    var n = Number(value) || 0;
    return (n > 0 ? '+' : '') + n.toFixed(0) + ' dB';
  }

  function wireEqualizer(eq) {
    var enabled = global.document.getElementById('ad-eq-enabled');
    if (enabled) enabled.addEventListener('change', function () {
      request('setEqualizerEnabled', { enabled: this.checked }).then(function (next) {
        state.equalizer = next; render();
      }).catch(function (e) { toast(e.message, true); });
    });

    var preset = global.document.getElementById('ad-eq-preset');
    if (preset) preset.addEventListener('change', function () {
      request('setEqualizerPreset', { preset: this.value }).then(function (next) {
        state.equalizer = next; render();
      }).catch(function (e) { toast(e.message, true); });
    });

    var reset = global.document.getElementById('ad-eq-reset');
    if (reset) reset.addEventListener('click', function () {
      request('resetEqualizer', {}).then(function (next) {
        state.equalizer = next; render(); toast('Equalizer reset');
      }).catch(function (e) { toast(e.message, true); });
    });

    var sliders = dom.body.querySelectorAll('.ad-band-slider');
    for (var i = 0; i < sliders.length; i++) {
      (function (slider) {
        slider.addEventListener('input', function () {
          var index = parseInt(slider.dataset.index, 10);
          var value = parseFloat(slider.value);
          var label = global.document.getElementById('ad-band-value-' + index);
          if (label) label.textContent = formatGain(value);
          var band = (state.equalizer.bands || []).filter(function (b) { return b.index === index; })[0];
          if (band) band.gainDb = value;
          drawEqualizerGraph(state.equalizer);
        });
        slider.addEventListener('change', function () {
          request('setEqualizerBand', {
            index: parseInt(slider.dataset.index, 10),
            gainDb: parseFloat(slider.value)
          }).then(function (next) {
            state.equalizer = next;
            var sel = global.document.getElementById('ad-eq-preset');
            if (sel) sel.value = next.preset;
          }).catch(function (e) { toast(e.message, true); });
        });
      })(sliders[i]);
    }

    var preamp = global.document.getElementById('ad-eq-preamp');
    if (preamp) {
      preamp.addEventListener('input', function () {
        var label = global.document.getElementById('ad-eq-preamp-value');
        if (label) label.textContent = formatGain(parseFloat(preamp.value));
      });
      preamp.addEventListener('change', function () {
        request('setEqualizer', { preampDb: parseFloat(preamp.value) }).then(function (next) {
          state.equalizer = next;
        }).catch(function (e) { toast(e.message, true); });
      });
    }

    var save = global.document.getElementById('ad-eq-save');
    if (save) save.addEventListener('click', function () {
      var input = global.document.getElementById('ad-eq-preset-name');
      var name = input ? input.value.trim() : '';
      if (!name) { toast('Enter a preset name', true); return; }
      request('saveEqualizerPreset', { name: name }).then(function (next) {
        state.equalizer = next; render(); toast('Preset saved');
      }).catch(function (e) { toast(e.message, true); });
    });

    var del = global.document.getElementById('ad-eq-delete');
    if (del) del.addEventListener('click', function () {
      var sel = global.document.getElementById('ad-eq-preset');
      var name = sel ? sel.value : '';
      if (!name) return;
      request('deleteEqualizerPreset', { name: name }).then(function (next) {
        state.equalizer = next; render(); toast('Preset deleted');
      }).catch(function (e) { toast(e.message, true); });
    });
  }

  function drawEqualizerGraph(eq) {
    var canvas = global.document.getElementById('ad-eq-graph');
    if (!canvas) return;

    var dpr = global.devicePixelRatio || 1;
    var rect = canvas.getBoundingClientRect();
    var width = Math.max(240, Math.round((rect.width || 520) * dpr));
    var height = Math.max(120, Math.round((rect.height || 150) * dpr));
    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;

    var ctx = canvas.getContext('2d');
    var w = width / dpr, h = height / dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);

    var styles = getComputedStyle(global.document.documentElement);
    var accent = styles.getPropertyValue('--ad-accent').trim() || '#d0bcff';
    var grid = styles.getPropertyValue('--ad-grid').trim() || 'rgba(255,255,255,0.10)';
    var text = styles.getPropertyValue('--ad-muted').trim() || 'rgba(255,255,255,0.55)';

    // dB grid.
    ctx.strokeStyle = grid;
    ctx.lineWidth = 1;
    for (var db = -12; db <= 12; db += 6) {
      var y = h / 2 - (db / 12) * (h / 2 - 12);
      ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke();
      ctx.fillStyle = text;
      ctx.font = '10px system-ui, sans-serif';
      ctx.textAlign = 'left';
      ctx.fillText((db > 0 ? '+' : '') + db, 4, y - 3);
    }

    var gains = (eq.bands || []).map(function (b) { return Number(b.gainDb) || 0; });
    if (gains.length === 0) return;

    // Approximate the response of the biquad cascade as a smooth curve over the
    // log frequency axis. This is a UI sketch, not the DSP coefficient engine.
    var points = [];
    var count = Math.max(80, Math.round(w / 3));
    for (var i = 0; i <= count; i++) {
      var t = i / count;
      var hz = 20 * Math.pow(1000, t); // 20 Hz .. 20 kHz
      var sum = 0;
      for (var b = 0; b < gains.length; b++) {
        var centre = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000][b] || 1000;
        var octaves = Math.log(hz / centre) / Math.LN2;
        sum += gains[b] * Math.exp(-(octaves * octaves) / (2 * 0.75 * 0.75));
      }
      var py = h / 2 - (sum / 12) * (h / 2 - 12);
      points.push([t * w, Math.max(3, Math.min(h - 3, py))]);
    }

    ctx.beginPath();
    ctx.moveTo(points[0][0], h / 2);
    for (var p = 0; p < points.length; p++) ctx.lineTo(points[p][0], points[p][1]);
    ctx.lineTo(points[points.length - 1][0], h / 2);
    ctx.closePath();
    var gradient = ctx.createLinearGradient(0, 0, 0, h);
    gradient.addColorStop(0, hexToRgba(accent, 0.36));
    gradient.addColorStop(1, hexToRgba(accent, 0.03));
    ctx.fillStyle = gradient;
    ctx.fill();

    ctx.beginPath();
    for (var q = 0; q < points.length; q++) {
      if (q === 0) ctx.moveTo(points[q][0], points[q][1]);
      else ctx.lineTo(points[q][0], points[q][1]);
    }
    ctx.strokeStyle = accent;
    ctx.lineWidth = 2.5;
    ctx.lineJoin = 'round';
    ctx.stroke();

    // Band handles on the curve.
    for (var k = 0; k < gains.length; k++) {
      var centreHz = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000][k];
      var x = Math.log(centreHz / 20) / Math.log(1000) * w;
      var y = h / 2 - (gains[k] / 12) * (h / 2 - 12);
      ctx.beginPath();
      ctx.arc(x, Math.max(3, Math.min(h - 3, y)), 3.5, 0, Math.PI * 2);
      ctx.fillStyle = accent;
      ctx.fill();
    }
  }

  function hexToRgba(hex, alpha) {
    var value = String(hex || '').trim();
    var m = /^#?([0-9a-f]{6})$/i.exec(value);
    if (!m) return 'rgba(208,188,255,' + alpha + ')';
    var n = parseInt(m[1], 16);
    return 'rgba(' + ((n >> 16) & 255) + ',' + ((n >> 8) & 255) + ',' + (n & 255) + ',' + alpha + ')';
  }

  // ── Sleep timer ────────────────────────────────────────────────────────
  function renderSleep() {
    setTitle('fa-moon', 'Sleep timer');
    var sleep = state.sleep;

    if (!sleep) {
      dom.body.innerHTML = '<div class="ad-empty"><i class="fas fa-circle-notch fa-spin"></i> Loading timer…</div>';
      return;
    }

    var active = !!sleep.isActive;
    var remaining = sleep.remainingSeconds || 0;
    var phaseText = sleep.phase === 'fading' ? 'Fading out'
      : sleep.phase === 'finishing-track' ? 'Finishing the last song'
      : 'Music stops in';

    var durationButtons = [15, 30, 45, 60, 90, 120].map(function (minutes) {
      return '<button class="ad-chip" data-sleep-minutes="' + minutes + '">' + minutes + ' min</button>';
    }).join('');

    var activeHtml = active
      ? '<div class="ad-sleep-active">' +
          '<div class="ad-sleep-phase">' + esc(phaseText) + '</div>' +
          '<div class="ad-sleep-time" id="ad-sleep-time">' + formatRemaining(remaining) + '</div>' +
          '<div class="ad-sleep-progress"><span id="ad-sleep-progress-fill" style="width:' + sleepProgress(sleep) + '%"></span></div>' +
          '<div class="ad-row ad-sleep-actions">' +
            '<button class="ad-chip" data-sleep-extend="5">+5 min</button>' +
            '<button class="ad-chip" data-sleep-extend="15">+15 min</button>' +
            '<button class="ad-btn ad-danger" data-sleep-cancel><i class="fas fa-stop"></i> Cancel</button>' +
          '</div>' +
        '</div>'
      : '<div class="ad-empty ad-sleep-idle"><i class="fas fa-moon"></i><div>No sleep timer running</div>' +
        '<small>Choose how long music should keep playing.</small></div>';

    var fadeOptions = [0, 5, 10, 15, 30, 60].map(function (seconds) {
      return '<option value="' + seconds + '"' + (sleep.fadeOutSeconds === seconds ? ' selected' : '') + '>' +
        (seconds === 0 ? 'No fade' : seconds + ' seconds') + '</option>';
    }).join('');

    dom.body.innerHTML =
      activeHtml +
      '<div class="ad-section-title">' + (active ? 'Start over' : 'Start timer') + '</div>' +
      '<div class="ad-chip-grid">' + durationButtons + '</div>' +
      '<div class="ad-row ad-custom-timer">' +
        '<input id="ad-sleep-custom" class="ad-input" type="number" min="1" max="600" step="1" placeholder="Custom minutes">' +
        '<button id="ad-sleep-start-custom" class="ad-btn"><i class="fas fa-play"></i> Start</button>' +
      '</div>' +
      '<div class="ad-section-title">Options</div>' +
      '<div class="ad-row ad-field"><label for="ad-sleep-fade">Fade out</label><select id="ad-sleep-fade">' + fadeOptions + '</select></div>' +
      '<label class="ad-check"><input id="ad-sleep-last-song" type="checkbox"' + (sleep.playLastSongToEnd ? ' checked' : '') + '>' +
        '<span>Play the last song to the end<small>Do not cut the current track off; fade over its final seconds.</small></span></label>';

    wireSleep(sleep);
    startSleepTicker();
  }

  function sleepProgress(sleep) {
    var total = Number(sleep.durationSeconds) || 0;
    var remaining = Number(sleep.remainingSeconds) || 0;
    if (total <= 0) return 0;
    return Math.max(0, Math.min(100, remaining / total * 100));
  }

  function wireSleep(sleep) {
    var chips = dom.body.querySelectorAll('[data-sleep-minutes]');
    for (var i = 0; i < chips.length; i++) {
      (function (chip) {
        chip.addEventListener('click', function () {
          startSleep(parseInt(chip.dataset.sleepMinutes, 10));
        });
      })(chips[i]);
    }

    var extendButtons = dom.body.querySelectorAll('[data-sleep-extend]');
    for (var e = 0; e < extendButtons.length; e++) {
      (function (button) {
        button.addEventListener('click', function () {
          request('extendSleepTimer', { minutes: parseInt(button.dataset.sleepExtend, 10) })
            .then(function (next) { state.sleep = next; render(); })
            .catch(function (err) { toast(err.message, true); });
        });
      })(extendButtons[e]);
    }

    var cancel = dom.body.querySelector('[data-sleep-cancel]');
    if (cancel) cancel.addEventListener('click', function () {
      request('cancelSleepTimer', {}).then(function (next) {
        state.sleep = next; render(); updateSleepPill(); toast('Sleep timer cancelled');
      }).catch(function (err) { toast(err.message, true); });
    });

    var startCustom = global.document.getElementById('ad-sleep-start-custom');
    if (startCustom) startCustom.addEventListener('click', function () {
      var input = global.document.getElementById('ad-sleep-custom');
      var minutes = input ? parseFloat(input.value) : 0;
      if (!isFinite(minutes) || minutes <= 0) { toast('Enter minutes', true); return; }
      startSleep(minutes);
    });

    var fade = global.document.getElementById('ad-sleep-fade');
    if (fade) fade.addEventListener('change', function () {
      request('setSleepTimerOptions', { fadeOutSeconds: parseFloat(this.value) })
        .then(function (next) { state.sleep = next; })
        .catch(function (err) { toast(err.message, true); });
    });

    var lastSong = global.document.getElementById('ad-sleep-last-song');
    if (lastSong) lastSong.addEventListener('change', function () {
      request('setSleepTimerOptions', { playLastSongToEnd: this.checked })
        .then(function (next) { state.sleep = next; })
        .catch(function (err) { toast(err.message, true); });
    });
  }

  function startSleep(minutes) {
    var fade = global.document.getElementById('ad-sleep-fade');
    var lastSong = global.document.getElementById('ad-sleep-last-song');
    request('startSleepTimer', {
      minutes: minutes,
      fadeOutSeconds: fade ? parseFloat(fade.value) : (state.sleep ? state.sleep.fadeOutSeconds : 10),
      playLastSongToEnd: lastSong ? lastSong.checked : (state.sleep ? state.sleep.playLastSongToEnd : true)
    }).then(function (next) {
      state.sleep = next;
      render();
      updateSleepPill();
      toast('Sleep timer set for ' + formatMinutes(minutes * 60));
    }).catch(function (err) { toast(err.message, true); });
  }

  function startSleepTicker() {
    if (state.sleepTimerHandle) clearInterval(state.sleepTimerHandle);
    state.sleepTimerHandle = setInterval(function () {
      if (!state.sleep || !state.sleep.isActive || !state.sleep.endsAtUtc) return;
      var remaining = (new Date(state.sleep.endsAtUtc).getTime() - Date.now()) / 1000;
      state.sleep.remainingSeconds = Math.max(0, remaining);
      updateSleepPill();
      if (state.view === 'sleep') {
        var time = global.document.getElementById('ad-sleep-time');
        if (time) time.textContent = formatRemaining(remaining);
        var fill = global.document.getElementById('ad-sleep-progress-fill');
        if (fill) fill.style.width = sleepProgress(state.sleep) + '%';
      }
    }, 1000);
  }

  function ensureSleepPill() {
    var doc = global.document;
    if (!doc || doc.getElementById('ad-sleep-pill')) return doc ? doc.getElementById('ad-sleep-pill') : null;

    var pill = doc.createElement('button');
    pill.id = 'ad-sleep-pill';
    pill.hidden = true;
    pill.innerHTML = '<i class="fas fa-moon"></i><span id="ad-sleep-pill-text">0:00</span>';
    pill.addEventListener('click', function () { open('sleep'); });

    if (isMobile()) {
      pill.className = 'mm-pill-btn ad-sleep-pill';
      var top = doc.querySelector('.mm-player-top');
      if (top) top.appendChild(pill);
      else doc.body.appendChild(pill);
    } else {
      pill.className = 'top-btn ad-sleep-pill';
      pill.title = 'Sleep timer';
      // The desktop top bar stays visible in collapsed and expanded playback.
      var actions = doc.getElementById('top-actions');
      var settings = doc.getElementById('btn-settings');
      if (actions) {
        if (settings) actions.insertBefore(pill, settings);
        else actions.appendChild(pill);
      } else {
        var right = doc.getElementById('player-bar-right');
        if (right) right.insertBefore(pill, right.firstChild);
        else doc.body.appendChild(pill);
      }
    }
    return pill;
  }

  function updateSleepPill() {
    var pill = ensureSleepPill();
    var doc = global.document;
    var toolbar = doc ? doc.getElementById('btn-sleep-timer') : null;
    if (!pill) return;
    var sleep = state.sleep;
    if (!sleep || !sleep.isActive) {
      pill.hidden = true;
      if (toolbar) {
        toolbar.classList.remove('is-active');
        toolbar.classList.remove('ad-timer-running');
        toolbar.title = 'Sleep timer';
      }
      return;
    }

    var remaining = sleep.remainingSeconds;
    if (sleep.endsAtUtc) {
      remaining = (new Date(sleep.endsAtUtc).getTime() - Date.now()) / 1000;
      if (remaining < 0) remaining = 0;
    }
    var text = pill.querySelector('#ad-sleep-pill-text');
    if (text) text.textContent = formatRemaining(remaining);
    pill.hidden = false;
    pill.classList.toggle('is-fading', sleep.phase === 'fading');
    pill.classList.toggle('is-finishing', sleep.phase === 'finishing-track');
    pill.title = sleep.phase === 'finishing-track'
      ? 'Sleep timer: finishing the last song'
      : 'Sleep timer: ' + formatRemaining(remaining) + ' left (click to edit)';
    if (toolbar) {
      toolbar.classList.add('is-active', 'ad-timer-running');
      toolbar.title = pill.title;
    }
  }

  // ── Song analysis ──────────────────────────────────────────────────────
  function renderAnalysis() {
    setTitle('fa-chart-line', 'Song analysis');
    if (state.analysisLoading) {
      dom.body.innerHTML =
        '<div class="ad-analysis-loading">' +
          '<i class="fas fa-circle-notch fa-spin"></i>' +
          '<div id="ad-analysis-stage">Analysing audio…</div>' +
          '<div class="ad-progress"><span id="ad-analysis-progress-fill" style="width:' + progressPercent() + '%"></span></div>' +
        '</div>';
      return;
    }

    var analysis = state.analysis;
    if (!analysis) {
      dom.body.innerHTML =
        '<div class="ad-empty ad-analysis-intro"><i class="fas fa-wave-square"></i>' +
          '<div>Analyse the current track</div>' +
          '<small>BPM, integrated loudness, dynamic range, quality and smart drop detection — computed from the decoded audio.</small>' +
          '<button id="ad-analysis-start" class="ad-btn ad-primary"><i class="fas fa-play"></i> Analyse current track</button>' +
        '</div>';
      var start = global.document.getElementById('ad-analysis-start');
      if (start) start.addEventListener('click', function () { analyzeCurrent(false); });
      return;
    }

    var quality = analysis.quality || {};
    var drops = analysis.drops || [];
    var parts = analysis.parts || [];
    var bpmConfidence = Math.round((Number(analysis.bpmConfidence) || 0) * 100);
    var dropList = drops.length
      ? drops.map(function (drop) {
          return '<button class="ad-drop-row" data-drop-time="' + drop.time + '">' +
            '<span class="ad-drop-dot ' + esc(drop.type) + '"></span>' +
            '<span class="ad-drop-label">' + esc(drop.label || drop.type) + '</span>' +
            '<span class="ad-drop-time-label">' + formatTimestamp(drop.time) + '</span>' +
            '<span class="ad-drop-confidence">' + Math.round((drop.confidence || 0) * 100) + '%</span>' +
          '</button>';
        }).join('')
      : '<div class="ad-empty-inline">No confident drops detected.</div>';

    var partList = parts.length
      ? parts.map(function (part) {
          return '<button class="ad-drop-row ad-part-row" data-part-time="' + part.startSeconds + '">' +
            '<span class="ad-part-dot ' + partClass(part.type) + '"></span>' +
            '<span class="ad-drop-label">' + esc(part.label || part.type) + '</span>' +
            '<span class="ad-drop-time-label">' + formatTimestamp(part.startSeconds) + ' – ' + formatTimestamp(part.endSeconds) + '</span>' +
            '<span class="ad-drop-confidence">' + Math.round((part.confidence || 0) * 100) + '%</span>' +
          '</button>';
        }).join('')
      : '<div class="ad-empty-inline">No song parts detected.</div>';

    dom.body.innerHTML =
      '<div class="ad-analysis-head">' +
        '<div><div class="ad-analysis-title">' + esc(analysis.title || 'Current track') + '</div>' +
        '<div class="ad-analysis-artist">' + esc(analysis.artist || '') + '</div></div>' +
        '<div class="ad-analysis-actions">' +
          '<button id="ad-analysis-current" class="ad-btn ad-quiet" title="Analyse the song that is playing now"><i class="fas fa-music"></i> Current track</button>' +
          '<button id="ad-analysis-refresh" class="ad-btn ad-quiet" title="Analyse this song again"><i class="fas fa-rotate"></i></button>' +
        '</div>' +
      '</div>' +
      '<div class="ad-stats">' +
        statCard('BPM', analysis.bpm > 0 ? Math.round(analysis.bpm) : '—', bpmConfidence + '% confidence') +
        statCard('Loudness', (analysis.loudnessLufs || 0).toFixed(1), 'LUFS integrated') +
        statCard('Peak', (analysis.peakDb || 0).toFixed(1), 'dBFS true peak') +
        statCard('Dynamic range', (analysis.dynamicRangeDb || 0).toFixed(1), 'dB') +
        statCard('Quality', Math.round(quality.score || 0), quality.summary || '') +
        statCard('Drops', drops.length, drops.length ? 'smart detection' : 'none found') +
      '</div>' +
      '<div class="ad-section-title">Audio path</div>' +
      '<div class="ad-graph-box"><canvas id="ad-analysis-timeline" class="ad-graph ad-analysis-graph"></canvas>' +
        '<div class="ad-graph-legend"><span class="ad-legend-low">Low</span><span class="ad-legend-mid">Mid</span>' +
        '<span class="ad-legend-high">High</span><span class="ad-legend-rms">RMS</span><span class="ad-legend-drop">Drop</span></div>' +
      '</div>' +
      '<div class="ad-section-title">Quality</div>' +
      '<div class="ad-graph-box ad-quality-box"><canvas id="ad-analysis-quality" class="ad-graph ad-quality-graph"></canvas>' +
        '<div class="ad-quality-facts">' +
          '<div><span>Codec</span><strong>' + esc(quality.codec || '—') + '</strong></div>' +
          '<div><span>Sample rate</span><strong>' + (quality.sampleRate ? (quality.sampleRate / 1000).toFixed(1) + ' kHz' : '—') + '</strong></div>' +
          '<div><span>Channels</span><strong>' + (quality.channels || '—') + '</strong></div>' +
          '<div><span>Bitrate</span><strong>' + (quality.lossless ? 'Lossless' : (quality.bitrateKbps || '—') + ' kbps') + '</strong></div>' +
          '<div><span>Clipping</span><strong class="' + (quality.clipping ? 'is-bad' : 'is-good') + '">' + (quality.clipping ? (quality.clippingPercent || 0) + '%' : 'None') + '</strong></div>' +
          '<div><span>Analysis path</span><strong class="ad-path">' + esc(analysis.analysisPath || '') + '</strong></div>' +
        '</div>' +
      '</div>' +
      '<div class="ad-section-title">Song parts / chapters</div>' +
      '<div class="ad-drop-list">' + partList + '</div>' +
      '<div class="ad-section-title">Detected drops</div>' +
      '<div class="ad-drop-list">' + dropList + '</div>';

    var refresh = global.document.getElementById('ad-analysis-refresh');
    if (refresh) refresh.addEventListener('click', function () { analyzeCurrent(true); });

    var currentTrack = global.document.getElementById('ad-analysis-current');
    if (currentTrack) currentTrack.addEventListener('click', function () {
      // Start a fresh analysis of whatever is playing now, even when this
      // result belongs to an older track or a library song chosen earlier.
      state.analysisTarget = null;
      state.analysis = null;
      analyzeCurrent(false);
    });

    var dropRows = dom.body.querySelectorAll('[data-drop-time]');
    for (var d = 0; d < dropRows.length; d++) {
      (function (row) {
        row.addEventListener('click', function () {
          var time = parseFloat(row.dataset.dropTime);
          var duration = Number(analysis.durationSeconds) || 0;
          if (!isFinite(time) || duration <= 0) return;
          request('seek', { position: Math.max(0, Math.min(1, time / duration)) })
            .then(function () { toast('Jumped to ' + formatTimestamp(time)); })
            .catch(function (error) { toast(error.message, true); });
        });
      })(dropRows[d]);
    }

    var partRows = dom.body.querySelectorAll('[data-part-time]');
    for (var p = 0; p < partRows.length; p++) {
      (function (row) {
        row.addEventListener('click', function () {
          var time = parseFloat(row.dataset.partTime);
          var duration = Number(analysis.durationSeconds) || 0;
          if (!isFinite(time) || duration <= 0) return;
          request('seek', { position: Math.max(0, Math.min(1, time / duration)) })
            .then(function () { toast('Jumped to ' + row.querySelector('.ad-drop-label').textContent); })
            .catch(function (error) { toast(error.message, true); });
        });
      })(partRows[p]);
    }

    drawAnalysisTimeline(analysis);
    drawQualityRadar(analysis);
  }

  function statCard(label, value, sub) {
    return '<div class="ad-stat"><div class="ad-stat-label">' + esc(label) + '</div>' +
      '<div class="ad-stat-value">' + esc(value) + '</div>' +
      '<div class="ad-stat-sub">' + esc(sub || '') + '</div></div>';
  }

  function formatTimestamp(seconds) {
    seconds = Math.max(0, Number(seconds) || 0);
    var m = Math.floor(seconds / 60);
    var s = Math.floor(seconds % 60);
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  function progressPercent() {
    var progress = state.analysisProgress;
    return progress ? Math.max(0, Math.min(100, Math.round((progress.progress || 0) * 100))) : 0;
  }

  function renderProgress() {
    if (!dom || state.view !== 'analysis') return;
    if (!state.analysisLoading) return;
    var fill = global.document.getElementById('ad-analysis-progress-fill');
    if (fill) fill.style.width = progressPercent() + '%';
    var stage = global.document.getElementById('ad-analysis-stage');
    if (stage && state.analysisProgress) stage.textContent = state.analysisProgress.stage || 'Analysing audio…';
  }

  function analyzeCurrent(force) {
    state.analysisLoading = true;
    state.analysisProgress = null;
    render();

    var target = state.analysisTarget;
    var params = force ? { force: true } : {};
    if (target && target.songId) params.songId = target.songId;
    else if (target && target.songPath) params.songPath = target.songPath;

    request('analyzeSong', params, 10 * 60 * 1000).then(function (result) {
      state.analysisLoading = false;
      state.analysisProgress = null;
      if (!result || result.success === false) {
        toast((result && result.error) || 'Analysis failed', true);
        state.analysis = null;
        render();
        return;
      }
      state.analysis = result;
      if (result.songPath) {
        if (!state.analysisTarget) state.analysisTarget = {};
        state.analysisTarget.songPath = result.songPath;
        if (result.title) state.analysisTarget.title = result.title;
        cacheAnalysis(result);
      }
      render();
    }).catch(function (error) {
      state.analysisLoading = false;
      toast(error.message, true);
      render();
    });
  }

  function canvasSize(canvas, fallbackW, fallbackH) {
    var dpr = global.devicePixelRatio || 1;
    var rect = canvas.getBoundingClientRect();
    var width = Math.max(fallbackW, Math.round((rect.width || fallbackW) * dpr));
    var height = Math.max(fallbackH, Math.round((rect.height || fallbackH) * dpr));
    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;
    var ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return { ctx: ctx, w: width / dpr, h: height / dpr };
  }

  function drawAnalysisTimeline(analysis) {
    var canvas = global.document.getElementById('ad-analysis-timeline');
    if (!canvas) return;
    var size = canvasSize(canvas, 520, 190);
    var ctx = size.ctx, w = size.w, h = size.h;
    var styles = getComputedStyle(global.document.documentElement);
    var accent = styles.getPropertyValue('--ad-accent').trim() || '#d0bcff';
    var grid = styles.getPropertyValue('--ad-grid').trim() || 'rgba(255,255,255,0.10)';
    var muted = styles.getPropertyValue('--ad-muted').trim() || 'rgba(255,255,255,0.55)';
    ctx.clearRect(0, 0, w, h);

    var timeline = analysis.timeline || [];
    if (!timeline.length) return;
    var duration = analysis.durationSeconds || timeline[timeline.length - 1].time || 1;
    var padL = 30, padB = 18, padT = 10;
    var plotW = Math.max(10, w - padL - 8);
    var plotH = Math.max(10, h - padB - padT);

    // dB grid.
    ctx.strokeStyle = grid;
    ctx.fillStyle = muted;
    ctx.font = '10px system-ui, sans-serif';
    ctx.textAlign = 'right';
    [-60, -40, -20, 0].forEach(function (db) {
      var y = padT + (1 - (db + 60) / 60) * plotH;
      ctx.beginPath(); ctx.moveTo(padL, y); ctx.lineTo(padL + plotW, y); ctx.stroke();
      ctx.fillText(db + '', padL - 4, y + 3);
    });

    ctx.textAlign = 'center';
    for (var t = 0; t <= duration; t += Math.max(30, Math.round(duration / 6))) {
      var x = padL + (t / duration) * plotW;
      ctx.fillStyle = muted;
      ctx.fillText(formatTimestamp(t), x, h - 4);
    }

    function xFor(time) { return padL + Math.max(0, Math.min(1, time / duration)) * plotW; }
    function yFor(db) { return padT + (1 - Math.max(0, Math.min(1, (db + 60) / 60))) * plotH; }

    // Band energy ribbons (stacked translucent). Convert the 0..1 fraction into a
    // visual band height so the low/mid/high balance is readable.
    var bands = [
      { key: 'lowEnergy', color: 'rgba(91,141,239,0.28)' },
      { key: 'midEnergy', color: 'rgba(208,188,255,0.28)' },
      { key: 'highEnergy', color: 'rgba(255,193,7,0.25)' }
    ];
    bands.forEach(function (band) {
      ctx.beginPath();
      ctx.moveTo(xFor(timeline[0].time), padT + plotH);
      for (var i = 0; i < timeline.length; i++) {
        var frame = timeline[i];
        var value = Math.max(0, Math.min(1, Number(frame[band.key]) || 0));
        ctx.lineTo(xFor(frame.time), padT + plotH - value * plotH * 0.9);
      }
      ctx.lineTo(xFor(timeline[timeline.length - 1].time), padT + plotH);
      ctx.closePath();
      ctx.fillStyle = band.color;
      ctx.fill();
    });

    // RMS curve.
    ctx.beginPath();
    for (var r = 0; r < timeline.length; r++) {
      var frame = timeline[r];
      var x = xFor(frame.time);
      var y = yFor(Number(frame.rmsDb) || -96);
      if (r === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.strokeStyle = accent;
    ctx.lineWidth = 2;
    ctx.lineJoin = 'round';
    ctx.stroke();

    // Beat grid.
    var bpm = Number(analysis.bpm) || 0;
    if (bpm > 0) {
      var beat = 60 / bpm;
      var offset = Number(analysis.beatOffsetSeconds) || 0;
      ctx.strokeStyle = 'rgba(255,255,255,0.08)';
      ctx.lineWidth = 1;
      for (var time = offset; time < duration; time += beat) {
        var bx = xFor(time);
        ctx.beginPath(); ctx.moveTo(bx, padT); ctx.lineTo(bx, padT + plotH); ctx.stroke();
      }
    }

    // Drop markers.
    (analysis.drops || []).forEach(function (drop) {
      var dx = xFor(drop.time);
      ctx.strokeStyle = '#ff6b6b';
      ctx.lineWidth = 1.5;
      ctx.setLineDash([3, 3]);
      ctx.beginPath(); ctx.moveTo(dx, padT); ctx.lineTo(dx, padT + plotH); ctx.stroke();
      ctx.setLineDash([]);
      ctx.fillStyle = '#ff6b6b';
      ctx.beginPath(); ctx.arc(dx, padT + 4, 3, 0, Math.PI * 2); ctx.fill();
    });
  }

  function drawQualityRadar(analysis) {
    var canvas = global.document.getElementById('ad-analysis-quality');
    if (!canvas) return;
    var size = canvasSize(canvas, 220, 190);
    var ctx = size.ctx, w = size.w, h = size.h;
    var styles = getComputedStyle(global.document.documentElement);
    var accent = styles.getPropertyValue('--ad-accent').trim() || '#d0bcff';
    var grid = styles.getPropertyValue('--ad-grid').trim() || 'rgba(255,255,255,0.10)';
    var muted = styles.getPropertyValue('--ad-muted').trim() || 'rgba(255,255,255,0.55)';
    ctx.clearRect(0, 0, w, h);

    var q = analysis.quality || {};
    var bitrateScore = q.lossless ? 1 : Math.max(0, Math.min(1, (Number(q.bitrateKbps) || 0) / 320));
    var resolutionScore = Math.max(0, Math.min(1, (Number(q.sampleRate) || 0) / 96000));
    var dynamicsScore = Math.max(0, Math.min(1, (Number(q.dynamicRangeDb) || 0) / 20));
    var clipScore = q.clipping ? 0.2 : 1;
    var overall = Math.max(0, Math.min(1, (Number(q.score) || 0) / 100));
    var values = [bitrateScore, resolutionScore, dynamicsScore, clipScore, overall];
    var labels = ['Bitrate', 'Resolution', 'Dynamics', 'Clean', 'Score'];

    var cx = w / 2, cy = h / 2 + 4;
    var radius = Math.max(24, Math.min(w, h) * 0.34);

    for (var ring = 1; ring <= 4; ring++) {
      ctx.beginPath();
      for (var i = 0; i <= values.length; i++) {
        var angle = -Math.PI / 2 + (i % values.length) * (Math.PI * 2 / values.length);
        var rr = radius * ring / 4;
        var x = cx + Math.cos(angle) * rr;
        var y = cy + Math.sin(angle) * rr;
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.strokeStyle = grid;
      ctx.lineWidth = 1;
      ctx.stroke();
    }

    ctx.beginPath();
    for (var v = 0; v < values.length; v++) {
      var va = -Math.PI / 2 + v * (Math.PI * 2 / values.length);
      var vr = radius * values[v];
      var vx = cx + Math.cos(va) * vr;
      var vy = cy + Math.sin(va) * vr;
      if (v === 0) ctx.moveTo(vx, vy); else ctx.lineTo(vx, vy);
    }
    ctx.closePath();
    ctx.fillStyle = hexToRgba(accent, 0.25);
    ctx.fill();
    ctx.strokeStyle = accent;
    ctx.lineWidth = 2;
    ctx.stroke();

    ctx.fillStyle = muted;
    ctx.font = '10px system-ui, sans-serif';
    ctx.textAlign = 'center';
    for (var l = 0; l < labels.length; l++) {
      var la = -Math.PI / 2 + l * (Math.PI * 2 / labels.length);
      ctx.fillText(labels[l], cx + Math.cos(la) * (radius + 14), cy + Math.sin(la) * (radius + 14) + 3);
    }
  }

  // ── Progress-bar chapters, drops and BPM badge ──────────────────────────
  function partClass(type) {
    switch (String(type || '').toLowerCase()) {
      case 'intro': return 'is-intro';
      case 'verse': return 'is-verse';
      case 'pre-chorus': return 'is-prechorus';
      case 'chorus': return 'is-chorus';
      case 'bridge': return 'is-bridge';
      case 'outro': return 'is-outro';
      default: return 'is-full';
    }
  }

  function clampPercent(value) {
    return Math.max(0, Math.min(100, Number(value) || 0));
  }

  function seekTo(seconds, duration) {
    if (!isFinite(seconds) || !isFinite(duration) || duration <= 0) return;
    request('seek', { position: clampPercent(seconds / duration * 100) / 100 })
      .catch(function () { });
  }

  function renderTrackMarkers() {
    var analysis = state.progressAnalysis;
    if (analysis && state.currentSongPath && !samePath(analysis.songPath, state.currentSongPath))
      analysis = null;

    renderMarkerLayer('progress-track', 'ad-progress-markers', analysis);
    renderMarkerLayer('exp-progress-track', 'ad-exp-progress-markers', analysis);
    renderMobileMarkers(analysis);
  }

  function renderMarkerLayer(trackId, layerId, analysis) {
    var doc = global.document;
    if (!doc) return;
    var track = doc.getElementById(trackId);
    if (!track) return;

    var layer = doc.getElementById(layerId);
    if (!layer) {
      layer = doc.createElement('div');
      layer.id = layerId;
      layer.className = 'ad-progress-markers';
      track.appendChild(layer);
    }
    populateMarkerLayer(layer, analysis);
  }

  function renderMobileMarkers(analysis) {
    var doc = global.document;
    if (!doc) return;
    var seek = doc.getElementById('mm-seek');
    if (!seek) return;

    var wrap = doc.getElementById('ad-mm-seek-wrap');
    if (!wrap) {
      wrap = doc.createElement('div');
      wrap.id = 'ad-mm-seek-wrap';
      wrap.className = 'ad-mm-seek-wrap';
      seek.parentNode.insertBefore(wrap, seek);
      wrap.appendChild(seek);
      var layer = doc.createElement('div');
      layer.id = 'ad-mm-markers';
      layer.className = 'ad-progress-markers ad-mobile-markers';
      wrap.appendChild(layer);
    }
    populateMarkerLayer(doc.getElementById('ad-mm-markers'), analysis);
  }

  function populateMarkerLayer(layer, analysis) {
    if (!layer) return;
    layer.innerHTML = '';

    var duration = analysis ? Number(analysis.durationSeconds) || 0 : 0;
    if (!analysis || duration <= 0) {
      layer.hidden = true;
      return;
    }
    layer.hidden = false;

    var parts = analysis.parts || [];
    for (var i = 0; i < parts.length; i++) {
      var part = parts[i];
      var start = clampPercent((Number(part.startSeconds) || 0) / duration * 100);
      var end = clampPercent((Number(part.endSeconds) || 0) / duration * 100);
      var cls = partClass(part.type);

      var band = global.document.createElement('div');
      band.className = 'ad-chapter-band ' + cls;
      band.style.left = start + '%';
      band.style.width = Math.max(0.3, end - start) + '%';
      band.title = (part.label || part.type) + ' · ' + formatTimestamp(part.startSeconds) + '–' + formatTimestamp(part.endSeconds);
      layer.appendChild(band);

      var chapter = global.document.createElement('button');
      chapter.type = 'button';
      chapter.className = 'ad-chapter ' + cls;
      chapter.style.left = start + '%';
      chapter.dataset.time = part.startSeconds;
      chapter.title = band.title;
      chapter.innerHTML = '<span class="ad-marker-label' + labelEdgeClass(start) + '">' +
        esc(part.label || part.type) + ' · ' + formatTimestamp(part.startSeconds) + '</span>';
      bindMarkerHover(chapter, layer);
      chapter.addEventListener('mousedown', function (event) { event.stopPropagation(); });
      chapter.addEventListener('touchstart', function (event) { event.stopPropagation(); }, { passive: true });
      chapter.addEventListener('click', function (event) {
        event.stopPropagation();
        seekTo(parseFloat(this.dataset.time), duration);
      });
      layer.appendChild(chapter);
    }

    var drops = analysis.drops || [];
    for (var d = 0; d < drops.length; d++) {
      var drop = drops[d];
      var at = clampPercent((Number(drop.time) || 0) / duration * 100);
      var pin = global.document.createElement('button');
      pin.type = 'button';
      pin.className = 'ad-drop-pin';
      pin.style.left = at + '%';
      pin.dataset.time = drop.time;
      pin.title = (drop.label || 'Drop') + ' · ' + formatTimestamp(drop.time);
      pin.innerHTML = '<span class="ad-marker-label' + labelEdgeClass(at) + '">' +
        esc(drop.label || 'Drop') + ' · ' + formatTimestamp(drop.time) + '</span>';
      bindMarkerHover(pin, layer);
      pin.addEventListener('mousedown', function (event) { event.stopPropagation(); });
      pin.addEventListener('touchstart', function (event) { event.stopPropagation(); }, { passive: true });
      pin.addEventListener('click', function (event) {
        event.stopPropagation();
        seekTo(parseFloat(this.dataset.time), duration);
      });
      layer.appendChild(pin);
    }
  }

  // Keep tooltips inside the bar at the edges (otherwise the first chapter's
  // label sat over the current-time text on the left).
  function labelEdgeClass(percent) {
    if (percent < 12) return ' is-start';
    if (percent > 88) return ' is-end';
    return '';
  }

  function bindMarkerHover(marker, layer) {
    var track = layer.parentElement;
    function enter() { if (track) track.classList.add('ad-marker-hover'); }
    function leave() { if (track) track.classList.remove('ad-marker-hover'); }
    marker.addEventListener('mouseenter', enter);
    marker.addEventListener('mouseleave', leave);
    marker.addEventListener('focus', enter);
    marker.addEventListener('blur', leave);
  }

  function updateBpmBadge() {
    var bpm = state.progressAnalysis ? Math.round(Number(state.progressAnalysis.bpm) || 0) : 0;
    if (bpm <= 0) bpm = 0;

    upsertBpmPill('player-bar-quality', 'ad-bpm-player', bpm);
    upsertBpmPill('expanded-quality', 'ad-bpm-expanded', bpm);
    updateMobileBpmBadge(bpm);
  }

  function updateAnalysisPendingUi() {
    if (state.analysisPending && state.analysisPendingSince &&
        Date.now() - state.analysisPendingSince > 180000) {
      state.analysisPending = false;
      state.analysisPendingProgress = 0;
      state.analysisPendingSince = 0;
    }

    var pending = !!state.analysisPending && !!state.currentSongPath;
    var progress = Math.max(0, Math.min(100, Math.round((state.analysisPendingProgress || 0) * 100)));
    var label = progress > 0 ? 'Analyzing ' + progress + '%' : 'Analyzing';
    var html = '<i class="fas fa-circle-notch fa-spin"></i> ' + label;

    upsertAnalysisPendingPill('player-bar-quality', 'ad-analysis-pending-player', pending, html);
    upsertAnalysisPendingPill('expanded-quality', 'ad-analysis-pending-expanded', pending, html);
    updateMobileAnalysisPending(pending, html);
  }

  function upsertAnalysisPendingPill(anchorId, id, pending, html) {
    var doc = global.document;
    if (!doc) return;
    var anchor = doc.getElementById(anchorId);
    if (!anchor) return;
    var pill = doc.getElementById(id);

    if (!pending) {
      if (pill) pill.remove();
      return;
    }

    if (!pill) {
      pill = doc.createElement('span');
      pill.id = id;
      pill.className = 'pill ad-analysis-pending';
      if (anchor.parentNode) anchor.parentNode.insertBefore(pill, anchor.nextSibling);
    }
    if (pill.innerHTML !== html) pill.innerHTML = html;
    pill.title = 'Automatic song analysis is running';
  }

  function updateMobileAnalysisPending(pending, html) {
    var doc = global.document;
    if (!doc) return;

    var top = doc.querySelector('.mm-player-top');
    var pill = doc.getElementById('ad-analysis-pill');
    if (top) {
      if (!pill) {
        pill = doc.createElement('span');
        pill.id = 'ad-analysis-pill';
        pill.className = 'mm-pill-btn ad-analysis-pending ad-analysis-pill';
        top.appendChild(pill);
      }
      pill.hidden = !pending;
      if (pending && pill.innerHTML !== html) pill.innerHTML = html;
    }

    var meta = doc.getElementById('mm-options-meta');
    if (meta) {
      var badge = doc.getElementById('ad-analysis-pending-mobile');
      if (!pending) {
        if (badge) badge.remove();
      } else {
        if (!badge) {
          badge = doc.createElement('span');
          badge.id = 'ad-analysis-pending-mobile';
          badge.className = 'mm-badge ad-analysis-pending';
          meta.appendChild(badge);
        }
        if (badge.innerHTML !== html) badge.innerHTML = html;
      }
    }
  }

  function upsertBpmPill(anchorId, id, bpm) {
    var doc = global.document;
    if (!doc) return;
    var anchor = doc.getElementById(anchorId);
    if (!anchor) return;
    var pill = doc.getElementById(id);

    if (bpm <= 0) {
      if (pill) pill.remove();
      return;
    }

    if (!pill) {
      pill = doc.createElement('span');
      pill.id = id;
      pill.className = 'pill ad-bpm-pill';
      if (anchor.parentNode) anchor.parentNode.insertBefore(pill, anchor.nextSibling);
    }
    var text = '<i class="fas fa-wave-square"></i> ' + bpm + ' BPM';
    if (pill.innerHTML !== text) pill.innerHTML = text;
    pill.title = bpm + ' BPM (detected from the audio)';
  }

  function updateMobileBpmBadge(bpm) {
    var doc = global.document;
    if (!doc) return;
    var hostEl = doc.getElementById('mm-options-meta');
    if (!hostEl) return;
    var badge = doc.getElementById('ad-bpm-mobile');

    if (bpm <= 0) {
      if (badge) badge.remove();
      return;
    }

    if (!badge) {
      badge = doc.createElement('span');
      badge.id = 'ad-bpm-mobile';
      badge.className = 'mm-badge ad-bpm-badge';
      hostEl.appendChild(badge);
    }
    var text = '<i class="fas fa-wave-square"></i><span>' + bpm + ' BPM</span>';
    if (badge.innerHTML !== text) badge.innerHTML = text;
  }

  // ── Chapter markers in the lyrics views ────────────────────────────────
  function decorateLyrics() {
    var doc = global.document;
    if (!doc) return;
    var parts = state.progressAnalysis && state.progressAnalysis.parts
      ? state.progressAnalysis.parts
      : [];

    decorateLyricsContainer(doc.getElementById('expanded-lyrics'), '.lyrics-line', parts);
    decorateLyricsContainer(doc.getElementById('mm-lyrics'), '.mm-line', parts);
  }

  function decorateLyricsContainer(container, lineSelector, parts) {
    if (!container) return;

    var oldMarkers = container.querySelectorAll('.lyrics-chapter-marker');
    for (var m = 0; m < oldMarkers.length; m++) oldMarkers[m].remove();

    var lines = container.querySelectorAll(lineSelector + '[data-time]');
    for (var i = 0; i < lines.length; i++) lines[i].removeAttribute('data-part');

    if (!parts.length || lines.length === 0) return;

    var sorted = parts.slice().sort(function (a, b) { return a.startSeconds - b.startSeconds; });
    var lastLine = null;

    for (var p = 0; p < sorted.length; p++) {
      var part = sorted[p];
      var target = null;
      for (var l = 0; l < lines.length; l++) {
        var time = Number(lines[l].dataset.time);
        if (isFinite(time) && time >= part.startSeconds - 0.35) { target = lines[l]; break; }
      }
      if (!target) continue;
      if (target === lastLine) continue;
      lastLine = target;

      var marker = global.document.createElement('div');
      marker.className = 'lyrics-chapter-marker ' + partClass(part.type);
      marker.innerHTML = '<span class="lyrics-chapter-label">' + esc(part.label || part.type) + '</span>' +
        '<small>' + formatTimestamp(part.startSeconds) + '</small>';
      target.parentNode.insertBefore(marker, target);
    }

    // Tint every synced line by its chapter (persistent data attribute survives
    // the players rewriting className for the active-line highlight).
    for (var n = 0; n < lines.length; n++) {
      var lineTime = Number(lines[n].dataset.time);
      if (!isFinite(lineTime)) continue;
      var current = null;
      for (var c = 0; c < sorted.length; c++) {
        if (sorted[c].startSeconds <= lineTime + 0.01) current = sorted[c];
        else break;
      }
      if (current) lines[n].dataset.part = current.type;
    }
  }

  function setAnalysisTarget(target) {    state.analysisTarget = target || null;
    state.analysis = null;
    state.analysisLoading = false;
  }

  // ── Bootstrap ──────────────────────────────────────────────────────────
  registerHandler();

  var api = {
    openEqualizer: function () { open('equalizer'); },
    openSleepTimer: function () { open('sleep'); },
    openAnalysis: function (target) { setAnalysisTarget(target); open('analysis'); },
    close: close,
    formatRemaining: formatRemaining,
    refresh: refreshStates,
    getChapters: function () {
      return state.progressAnalysis && state.progressAnalysis.parts
        ? state.progressAnalysis.parts
        : [];
    },
    decorateLyrics: decorateLyrics,
    _state: state
  };
  global.SongRequestAdvancedAudio = api;

  function bindToolbarButtons() {
    var doc = global.document;
    if (!doc) return;
    var bindings = [
      ['btn-equalizer', function () { open('equalizer'); }],
      ['btn-sleep-timer', function () { open('sleep'); }],
      ['btn-analysis', function () { api.openAnalysis(); }]
    ];
    bindings.forEach(function (entry) {
      var button = doc.getElementById(entry[0]);
      if (button && !button.dataset.adBound) {
        button.dataset.adBound = '1';
        button.addEventListener('click', entry[1]);
      }
    });
  }
  bindToolbarButtons();

  // Pick up an already-running timer when the page loads/reloads. Deferring lets
  // the page register its own music bridge first.
  setTimeout(function () { refreshStates(); }, 400);
  setInterval(function () { updateSleepPill(); updateAnalysisPendingUi(); }, 1000);

})(typeof window !== 'undefined' ? window : globalThis);
