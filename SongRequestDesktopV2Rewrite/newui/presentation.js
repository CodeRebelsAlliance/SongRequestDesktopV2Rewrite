(function() {
  'use strict';

  // DOM refs
  var presRoot = document.getElementById('presentation-root');
  var presPanel = document.getElementById('pres-player-panel');
  var presArtwork = document.getElementById('presentation-artwork');
  var presTitle = document.getElementById('presentation-title');
  var presArtist = document.getElementById('presentation-artist');
  var presLyrics = document.getElementById('presentation-lyrics');
  var lyricsProvider = document.getElementById('lyrics-provider');
  var presProgressFill = document.getElementById('pres-progress-fill');
  var presProgressCurrent = document.getElementById('pres-progress-current');
  var presProgressTotal = document.getElementById('pres-progress-total');
  var presSide = document.getElementById('pres-side');
  var presQueuePanel = document.getElementById('pres-queue-panel');
  var presQueueList = document.getElementById('pres-queue-list');
  var presQrPanel = document.getElementById('pres-qr-panel');
  var presQrCode = document.getElementById('pres-qr-code');
  var presQrLogo = document.getElementById('pres-qr-logo');

  // State
  var syncedLyrics = [];
  var plainLyricLines = [];
  var hasSyncedLyrics = false;
  var totalTimeSecs = 0;
  var lastHighlightIndex = -1;
  var lastCurrentTime = 0;
  var currentTitle = '';
  var currentArtist = '';
  var lyricsFontScale = 1.0;
  var lyricsHidden = false;
  var currentSource = 'player';
  var qrInstance = null;

  // IPC
  function hostSend(method, data) {
    var msg = JSON.stringify({ type: 'request', method: method, data: data || {} });
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(msg);
    } else if (window.external && window.external.sendMessage) {
      window.external.sendMessage(msg);
    }
  }

  function escHtml(s) {
    var d = document.createElement('div');
    d.textContent = s;
    return d.innerHTML;
  }

  function formatTime(seconds) {
    if (!seconds || seconds < 0) return '0:00';
    var m = Math.floor(seconds / 60);
    var s = Math.floor(seconds % 60);
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  function parseDurationString(str) {
    if (!str) return 0;
    var parts = str.split(':');
    if (parts.length === 2) return parseInt(parts[0], 10) * 60 + parseInt(parts[1], 10);
    if (parts.length === 3) return parseInt(parts[0], 10) * 3600 + parseInt(parts[1], 10) * 60 + parseInt(parts[2], 10);
    return 0;
  }

  // ── Source visibility ──
  function setSourceVisibility(source) {
    currentSource = source;
    if (source === 'player') {
      presSide.classList.remove('hidden');
      presPanel.classList.remove('fullwidth');
    } else {
      presSide.classList.add('hidden');
      presPanel.classList.add('fullwidth');
    }
  }

  // ── QR code ──
  function setQrCode(url) {
    if (!url || !presQrCode) return;
    presQrCode.innerHTML = '';
    try {
      if (typeof QRCode !== 'undefined') {
        qrInstance = new QRCode(presQrCode, {
          text: url,
          width: 164,
          height: 164,
          colorDark: '#000000',
          colorLight: '#ffffff',
          correctLevel: QRCode.CorrectLevel.M
        });
      }
    } catch(e) { }
  }

  // ── Background color extraction ──
  function extractAccentColors(src) {
    var img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = function() {
      try {
        var canvas = document.createElement('canvas');
        var ctx = canvas.getContext('2d');
        var S = 40;
        canvas.width = S; canvas.height = S;
        ctx.drawImage(img, 0, 0, S, S);
        var data = ctx.getImageData(0, 0, S, S).data;

        var buckets = {};
        for (var i = 0; i < data.length; i += 4) {
          var r = data[i], g = data[i+1], b = data[i+2], a = data[i+3];
          if (a < 128) continue;
          var lum = 0.299 * r + 0.587 * g + 0.114 * b;
          if (lum < 20 || lum > 240) continue;
          var sat = Math.max(r, g, b) - Math.min(r, g, b);
          if (sat < 20) continue;
          var br = Math.round(r / 48) * 48;
          var bg = Math.round(g / 48) * 48;
          var bb = Math.round(b / 48) * 48;
          var key = br + ',' + bg + ',' + bb;
          if (!buckets[key]) buckets[key] = { r: 0, g: 0, b: 0, n: 0, lum: 0 };
          buckets[key].r += r;
          buckets[key].g += g;
          buckets[key].b += b;
          buckets[key].lum += lum;
          buckets[key].n++;
        }

        var sorted = Object.values(buckets)
          .filter(function(b) { return b.n >= 3; })
          .sort(function(a, b) { return b.n - a.n; });

        if (sorted.length < 1) { resetAccentColors(); return; }

        var c1 = sorted[0];
        var c1r = Math.round(c1.r / c1.n), c1g = Math.round(c1.g / c1.n), c1b = Math.round(c1.b / c1.n);

        var c2 = null;
        for (var j = 1; j < sorted.length; j++) {
          var d = sorted[j];
          var dr = Math.round(d.r / d.n), dg = Math.round(d.g / d.n), db = Math.round(d.b / d.n);
          var dist = Math.abs(dr - c1r) + Math.abs(dg - c1g) + Math.abs(db - c1b);
          if (dist > 60) { c2 = { r: dr, g: dg, b: db }; break; }
        }
        if (!c2) {
          c2 = { r: Math.max(0, c1r - 40), g: Math.max(0, c1g - 40), b: Math.max(0, c1b - 40) };
        }

        var dark = 0.35;
        var a1 = 'rgb(' + Math.round(c1r * dark) + ',' + Math.round(c1g * dark) + ',' + Math.round(c1b * dark) + ')';
        var a2 = 'rgb(' + Math.round(c2.r * dark) + ',' + Math.round(c2.g * dark) + ',' + Math.round(c2.b * dark) + ')';
        var a3 = 'rgb(' + Math.round((c1r + c2.r) / 2 * dark * 0.7) + ',' + Math.round((c1g + c2.g) / 2 * dark * 0.7) + ',' + Math.round((c1b + c2.b) / 2 * dark * 0.7) + ')';

        presPanel.style.setProperty('--accent-1', a1);
        presPanel.style.setProperty('--accent-2', a2);
        presPanel.style.setProperty('--accent-3', a3);
      } catch(e) {
        resetAccentColors();
      }
    };
    img.onerror = function() { resetAccentColors(); };
    img.src = src;
  }

  function resetAccentColors() {
    presPanel.style.setProperty('--accent-1', '#1e1e1e');
    presPanel.style.setProperty('--accent-2', '#1e1e1e');
    presPanel.style.setProperty('--accent-3', '#1a1a1a');
  }

  // ── Queue rendering ──
  function renderQueue(data) {
    if (!presQueueList) return;

    var queue = data.queue || [];

    // C# already excludes now-playing; take up to 3
    var upcoming = queue.slice(0, 3);

    // Toggle panel visibility
    if (upcoming.length === 0) {
      presQueueList.innerHTML = '';
      presQueuePanel.classList.add('empty');
      return;
    }
    presQueuePanel.classList.remove('empty');

    // Build new items with staggered enter animation
    var fragment = document.createDocumentFragment();
    upcoming.forEach(function(item, idx) {
      var el = document.createElement('div');
      el.className = 'pres-queue-item entering';

      var thumbHtml = '';
      if (item.thumbnail) {
        thumbHtml = '<img src="' + escHtml(item.thumbnail) + '" alt="">';
      } else {
        thumbHtml = '<i class="fas fa-music"></i>';
      }

      var waitText = item.estimatedStart || '';

      el.innerHTML =
        '<div class="pres-q-thumb">' + thumbHtml + '</div>' +
        '<div class="pres-q-meta">' +
          '<div class="pres-q-title">' + escHtml(item.title || '') + '</div>' +
          '<div class="pres-q-artist">' + escHtml(item.artist || '') + '</div>' +
          '<div class="pres-q-wait">' + escHtml(waitText) + '</div>' +
        '</div>';

      fragment.appendChild(el);

      // Stagger the enter animation
      var delay = idx * 80;
      setTimeout(function() {
        el.classList.remove('entering');
        el.classList.add('entering-active');
      }, 30 + delay);
    });

    presQueueList.innerHTML = '';
    presQueueList.appendChild(fragment);

    // Detect thumbnail aspect ratio (1:1 vs 16:9) and apply class
    var thumbs = presQueueList.querySelectorAll('.pres-q-thumb');
    thumbs.forEach(function(thumb) {
      var img = thumb.querySelector('img');
      if (!img) {
        thumb.classList.add('square');
        return;
      }
      if (img.complete) {
        checkThumbRatio(thumb, img);
      } else {
        img.onload = function() { checkThumbRatio(thumb, img); };
        // Fallback if load event doesn't fire
        thumb.classList.add('square');
      }
    });
  }

  function checkThumbRatio(thumbEl, img) {
    if (img.naturalWidth > 0 && img.naturalHeight > 0) {
      var ratio = img.naturalWidth / img.naturalHeight;
      // If close to square (0.8–1.2), use square; otherwise 16:9
      if (ratio >= 0.8 && ratio <= 1.2) {
        thumbEl.classList.add('square');
      }
    }
  }

  // ── Lyrics rendering ──
  function renderLyricsLoading() {
    presLyrics.innerHTML = '<div class="lyrics-loading"><i class="fas fa-circle-notch fa-spin"></i><div>Loading lyrics...</div></div>';
    if (lyricsProvider) lyricsProvider.textContent = '';
  }

  function renderNoLyrics() {
    presLyrics.innerHTML = '<div class="lyrics-no-lyrics"><i class="fas fa-music"></i><div>No lyrics available</div></div>';
  }

  function renderLyrics(data) {
    presLyrics.innerHTML = '';
    if (lyricsProvider) lyricsProvider.textContent = '';

    var fragment = document.createDocumentFragment();
    fragment.appendChild(createSpacer());

    var lines = [];

    if (data.syncedLyrics && data.syncedLyrics.length > 0) {
      syncedLyrics = data.syncedLyrics;
      hasSyncedLyrics = true;
      var prevTime = -999;
      data.syncedLyrics.forEach(function(item, i) {
        if (item.time - prevTime > 3.5) {
          var gap = document.createElement('div');
          gap.className = 'lyrics-gap';
          fragment.appendChild(gap);
        }
        prevTime = item.time;
        var div = document.createElement('div');
        div.className = 'lyrics-line';
        div.textContent = item.text || '';
        div.dataset.index = i;
        fragment.appendChild(div);
        lines.push(div);
      });
    } else if (data.plainLyrics && data.plainLyrics.length > 0) {
      plainLyricLines = data.plainLyrics;
      hasSyncedLyrics = false;
      data.plainLyrics.forEach(function(text, i) {
        var div = document.createElement('div');
        div.className = 'lyrics-line';
        div.textContent = text || '';
        div.dataset.index = i;
        fragment.appendChild(div);
        lines.push(div);
      });
    }

    fragment.appendChild(createSpacer());
    presLyrics.appendChild(fragment);

    if (data.provider && lyricsProvider) {
      lyricsProvider.textContent = 'Source: ' + data.provider;
    }

    updateLyricFontSizes();
  }

  function createSpacer() {
    var sp = document.createElement('div');
    sp.className = 'lyrics-spacer';
    return sp;
  }

  function updateLyricFontSizes() {
    var rect = presLyrics.getBoundingClientRect();
    var h = rect.height;
    if (h <= 0) return;
    var active = Math.round(h * 0.055 * lyricsFontScale);
    active = Math.max(28, Math.min(active, 60));
    var base = Math.round(active * 0.7);
    base = Math.max(20, Math.min(base, 44));
    var prev = Math.round(active * 0.78);
    prev = Math.max(22, Math.min(prev, 50));
    presLyrics.style.setProperty('--lyric-active', active + 'px');
    presLyrics.style.setProperty('--lyric-base', base + 'px');
    presLyrics.style.setProperty('--lyric-prev', prev + 'px');
  }

  function applyLyricsSettings(data) {
    if (data.lyricsFontScale != null) {
      lyricsFontScale = data.lyricsFontScale;
      updateLyricFontSizes();
    }
    if (data.lyricsHidden != null) {
      lyricsHidden = data.lyricsHidden;
      if (presLyrics) presLyrics.style.display = lyricsHidden ? 'none' : '';
      if (lyricsProvider) lyricsProvider.style.display = lyricsHidden ? 'none' : '';
    }
    if (data.requestUrl != null) {
      setQrCode(data.requestUrl);
    }
    if (data.presentationSource != null) {
      setSourceVisibility(data.presentationSource);
    }
  }

  // ── Lyric highlighting ──
  function updateLyricHighlighting(currentTime) {
    lastCurrentTime = currentTime;
    var lines = presLyrics.querySelectorAll('.lyrics-line');
    if (!lines.length) return;

    var adjustedTime = currentTime;

    if (hasSyncedLyrics && syncedLyrics.length > 0) {
      var domIndex = -1;
      for (var i = syncedLyrics.length - 1; i >= 0; i--) {
        if (syncedLyrics[i].time <= adjustedTime + 0.1) {
          domIndex = i;
          break;
        }
      }

      if (domIndex !== lastHighlightIndex) {
        lastHighlightIndex = domIndex;
        lines.forEach(function(line, i) {
          if (i === domIndex) {
            line.className = 'lyrics-line active';
            line.scrollIntoView({ behavior: 'smooth', block: 'center' });
          } else if (i === domIndex - 1) {
            line.className = 'lyrics-line prev';
          } else {
            line.className = 'lyrics-line';
          }
        });
      }
    } else if (plainLyricLines.length > 0 && totalTimeSecs > 0) {
      var pct = Math.min(currentTime / totalTimeSecs, 1);
      var idx = Math.min(Math.floor(pct * plainLyricLines.length), plainLyricLines.length - 1);
      if (idx !== lastHighlightIndex) {
        lastHighlightIndex = idx;
        lines.forEach(function(line, i) {
          if (i === idx) {
            line.className = 'lyrics-line active';
            line.scrollIntoView({ behavior: 'smooth', block: 'center' });
          } else if (i === idx - 1) {
            line.className = 'lyrics-line prev';
          } else {
            line.className = 'lyrics-line';
          }
        });
      }
    }
  }

  // ── Event handlers ──
  function onNowPlayingUpdate(data) {
    if (data.title !== undefined) {
      currentTitle = data.title;
      currentArtist = data.artist || '—';
      presTitle.textContent = currentTitle || 'No track playing';
      presArtist.textContent = currentArtist;
    }

    if (data.thumbnail) {
      presArtwork.innerHTML = '<img src="' + data.thumbnail + '" alt="artwork">';
      extractAccentColors(data.thumbnail);
    }

    if (data.duration) {
      presProgressTotal.textContent = data.duration;
      totalTimeSecs = parseDurationString(data.duration);
    } else if (data.totalTime != null) {
      presProgressTotal.textContent = formatTime(data.totalTime);
      totalTimeSecs = data.totalTime;
    }

    if (data.currentTime != null) {
      var effectiveTotal = totalTimeSecs;
      var pct = effectiveTotal > 0 ? Math.min(data.currentTime / effectiveTotal * 100, 100) : 0;
      presProgressFill.style.width = pct + '%';
      presProgressCurrent.textContent = formatTime(data.currentTime);
      updateLyricHighlighting(data.currentTime);
    }

    if (data.isPlaying !== undefined) {
      presPanel.classList.toggle('idle', !data.isPlaying);
    }
  }

  function onLyricsUpdate(data) {
    renderLyrics(data);
  }

  // ── IPC ──
  function handleIncoming(json) {
    try {
      var msg = JSON.parse(json);
      if (msg.type === 'response') {
        if (msg.result) {
          if (msg.result.title !== undefined) {
            onNowPlayingUpdate(msg.result);
          }
          if (msg.result.lyrics) {
            onLyricsUpdate(msg.result.lyrics);
          }
        }
      } else if (msg.type === 'event') {
        var name = msg.eventName;
        var data = msg.data || {};
        if (name === 'nowPlayingUpdate' || name === 'presentationUpdate') {
          onNowPlayingUpdate(data);
        } else if (name === 'lyricsUpdate' || name === 'presentationLyricsUpdate') {
          onLyricsUpdate(data);
        } else if (name === 'presentationSettings') {
          applyLyricsSettings(data);
        } else if (name === 'presentationQueueUpdate') {
          renderQueue(data);
        }
      }
    } catch (_) {}
  }

  window.external.receiveMessage = handleIncoming;
  if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
    window.chrome.webview.addEventListener('message', function(ev) {
      if (typeof ev.data === 'string') handleIncoming(ev.data);
    });
  }

  // ── Fullscreen toggle ──
  var btnFullscreen = document.getElementById('btn-fullscreen');

  function updateFullscreenIcon() {
    var isFs = !!(document.fullscreenElement || document.webkitFullscreenElement);
    if (btnFullscreen) {
      btnFullscreen.innerHTML = isFs ? '<i class="fas fa-compress"></i>' : '<i class="fas fa-expand"></i>';
      btnFullscreen.title = isFs ? 'Exit fullscreen' : 'Fullscreen';
    }
  }

  if (btnFullscreen) {
    btnFullscreen.addEventListener('click', function() {
      if (document.fullscreenElement || document.webkitFullscreenElement) {
        (document.exitFullscreen || document.webkitExitFullscreen || function(){}).call(document);
      } else {
        var el = document.documentElement;
        (el.requestFullscreen || el.webkitRequestFullscreen || function(){}).call(el);
      }
    });
  }

  document.addEventListener('fullscreenchange', updateFullscreenIcon);
  document.addEventListener('webkitfullscreenchange', updateFullscreenIcon);
  updateFullscreenIcon();

  // Ready
  hostSend('presentationReady');
})();
