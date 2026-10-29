(function () {
  'use strict';

  // ── IPC ────────────────────────────────────────────────────────
  var requestId = 0;
  function postToHost(method, params) {
    var msg = JSON.stringify(Object.assign({ type: 'request', id: ++requestId, method: method }, params || {}));
    if (!SongRequestHost.sendToHost(msg)) console.error('Failed to send message: no host channel');
  }

  function handleIncoming(json) {
    try {
      const msg = JSON.parse(json);
      if (msg.type === 'event' && msg.eventName === 'audioSamples') {
        decodeFrame(msg.data);
      } else if (msg.type === 'event' && msg.eventName === 'mobileThemeColors') {
        if (window.SongRequestTheme) window.SongRequestTheme.apply(msg.data || {}, 'vz', '#1d1b20', '#26232c');
        resize();
      } else if (msg.type === 'event' && msg.eventName === 'refresh') {
        // Android pauses JavaScript in a hidden web view. When this page becomes
        // visible again the canvas may still have the 1×1 size measured while it
        // was hidden, so re-measure and re-request the audio tap.
        resize();
        requestAnimationFrame(resize);
      }
    } catch (e) { console.error('Parse error:', e); }
  }
  // Register the host -> page handler. Photino exposes
  // window.external.receiveMessage(callback); it must be *called* (assigning to
  // it replaces Photino's shim, after which no host message is ever delivered).
  SongRequestHost.registerHostMessageHandler(handleIncoming);

  // ── State ──────────────────────────────────────────────────────
  let mode = 'Spectrum';
  let _samples = new Float32Array(0);   // interleaved, -1..1
  let _mono = new Float32Array(0);
  let _channels = 2;
  let _sampleRate = 44100;
  let _lastFrameTime = 0;

  function decodeFrame(evt) {
    if (!evt || !evt.data) return;
    try {
      const raw = atob(evt.data);
      const n = raw.length;
      const s = new Float32Array(n);
      for (let i = 0; i < n; i++) {
        let v = raw.charCodeAt(i);
        if (v > 127) v -= 256;
        s[i] = v / 127;
      }
      _samples = s;
      _channels = evt.channels > 0 ? evt.channels : 1;
      _sampleRate = evt.sampleRate || 44100;
      buildMono();
      _lastFrameTime = performance.now();
    } catch (e) { /* ignore malformed frame */ }
  }

  function buildMono() {
    const ch = _channels;
    if (ch <= 1) { _mono = _samples; return; }
    const frames = Math.floor(_samples.length / ch);
    const m = new Float32Array(frames);
    for (let i = 0; i < frames; i++) {
      let sum = 0;
      for (let c = 0; c < ch; c++) sum += _samples[i * ch + c];
      m[i] = sum / ch;
    }
    _mono = m;
  }

  // ── Canvas ─────────────────────────────────────────────────────
  const canvas = document.getElementById('viz-canvas');
  const ctx = canvas.getContext('2d');
  const idleEl = document.getElementById('viz-idle');

  function resize() {
    const dpr = window.devicePixelRatio || 1;
    const rect = canvas.parentElement.getBoundingClientRect();
    const width = Math.max(1, Math.round(rect.width * dpr));
    const height = Math.max(1, Math.round(rect.height * dpr));
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }
  window.addEventListener('resize', resize);
  window.addEventListener('orientationchange', () => setTimeout(resize, 80));
  if (window.visualViewport) window.visualViewport.addEventListener('resize', resize);
  // The stage page can be created while its tab is still hidden (notably in the
  // Android WebView). Without observing the element itself the canvas kept the
  // 1×1 size measured at load and stayed black; a window resize never fired.
  if (window.ResizeObserver) {
    try { new ResizeObserver(resize).observe(canvas.parentElement); } catch (e) { }
  }
  resize();

  // ── FFT / band helpers (pure, unit-tested in tests/js/vizmath.test.js) ──
  const viz = window.SongRequestViz;
  const fft = viz.fft;

  const BAR_COUNT = 64;
  const FFT_SIZE = 512;
  // 1..(binCount-1) of a 512-point FFT: ~86 Hz .. ~22 kHz, log-spaced.
  const bandEdges = viz.spectrumEdges(BAR_COUNT, FFT_SIZE / 2, 1, FFT_SIZE / 2 - 1);
  const smoothed = new Float32Array(BAR_COUNT);
  let autoGain = 1.0;

  function computeSpectrum() {
    const N = FFT_SIZE;
    const re = new Float32Array(N);
    const im = new Float32Array(N);
    const mono = _mono;
    const start = Math.max(0, mono.length - N);
    for (let i = 0; i < N; i++) {
      const s = (start + i < mono.length) ? mono[start + i] : 0;
      const w = 0.5 - 0.5 * Math.cos(2 * Math.PI * i / (N - 1)); // Hann
      re[i] = s * w;
    }
    fft(re, im);

    const mag = viz.magnitudes(re, im, N / 2);
    // Log bands with fractional edges (no two bars share a bin) + a frequency tilt,
    // so the bass is not pinned at full scale while everything else sits near zero.
    const bands = viz.applyTilt(
      viz.bandsFromMagnitudes(mag, bandEdges), bandEdges, _sampleRate, N);

    let maxVal = 0.0001;
    for (let b = 0; b < BAR_COUNT; b++) if (bands[b] > maxVal) maxVal = bands[b];

    // Auto gain (decay towards 1)
    const targetGain = 0.6 / maxVal;
    autoGain = autoGain * 0.95 + targetGain * 0.05;
    if (autoGain > 40) autoGain = 40;
    if (autoGain < 1) autoGain = 1;

    for (let b = 0; b < BAR_COUNT; b++) {
      let v = bands[b] * autoGain;
      if (v > 1) v = 1;
      smoothed[b] = smoothed[b] * 0.7 + v * 0.3;
    }
  }

  function spectrumColor(mag) {
    // blue -> cyan -> magenta -> red
    const m = Math.max(0, Math.min(1, mag));
    let r, g, b;
    if (m < 0.5) { const t = m / 0.5; r = 91 + (79 - 91) * t; g = 141 + (240 - 141) * t; b = 239 + (255 - 239) * t; }
    else { const t = (m - 0.5) / 0.5; r = 79 + (255 - 79) * t; g = 240 + (70 - 240) * t; b = 255 + (130 - 255) * t; }
    return `rgb(${r|0},${g|0},${b|0})`;
  }

  // ── Renderers ──────────────────────────────────────────────────
  function rms(samples, offset, step) {
    let sum = 0, count = 0;
    for (let i = offset; i < samples.length; i += step) { sum += samples[i] * samples[i]; count++; }
    return count > 0 ? Math.sqrt(sum / count) : 0;
  }

  function renderSpectrum(w, h) {
    computeSpectrum();
    const barW = w / BAR_COUNT;
    const spacing = Math.max(1, barW * 0.15);
    for (let i = 0; i < BAR_COUNT; i++) {
      const mag = smoothed[i];
      const barH = Math.min(mag * h * 1.6, h);
      ctx.fillStyle = spectrumColor(mag);
      ctx.fillRect(i * barW + spacing / 2, h - barH, barW - spacing, barH);
    }
  }

  function renderWaveform(w, h) {
    const mono = _mono;
    const centerY = h / 2;
    const amp = h * 0.45;
    ctx.strokeStyle = 'rgba(120,120,120,0.3)';
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(0, centerY); ctx.lineTo(w, centerY); ctx.stroke();

    if (mono.length === 0) return;

    ctx.strokeStyle = '#5b8def';
    ctx.lineWidth = 2;
    ctx.lineJoin = 'round';
    ctx.beginPath();

    if (mono.length >= w) {
      // More samples than columns: draw the min/max envelope of each column so the
      // shape stays readable instead of aliasing into noise.
      const per = mono.length / w;
      for (let x = 0; x < w; x++) {
        const from = Math.floor(x * per);
        const to = Math.min(mono.length, Math.max(from + 1, Math.floor((x + 1) * per)));
        let lo = 1, hi = -1;
        for (let i = from; i < to; i++) {
          const v = mono[i];
          if (v < lo) lo = v;
          if (v > hi) hi = v;
        }
        ctx.moveTo(x, centerY - hi * amp);
        ctx.lineTo(x, centerY - lo * amp);
      }
    } else {
      // Fewer samples than columns: stretch the buffer across the whole width
      // (the old code stopped at the buffer length and drew only half of it).
      for (let x = 0; x < w; x++) {
        const v = viz.sampleAt(mono, viz.sampleIndexForColumn(x, w, mono.length));
        const y = centerY - v * amp;
        if (x === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
    }

    ctx.stroke();
  }

  function renderCircular(w, h) {
    computeSpectrum();
    const cx = w / 2, cy = h / 2;
    const radius = Math.min(w, h) * 0.3;
    ctx.fillStyle = '#5b8def';
    ctx.beginPath();
    ctx.arc(cx, cy, radius * 0.3, 0, Math.PI * 2);
    ctx.fill();

    const angleStep = (Math.PI * 2) / BAR_COUNT;
    ctx.lineWidth = 3;
    ctx.lineCap = 'round';
    for (let i = 0; i < BAR_COUNT; i++) {
      const mag = smoothed[i];
      const angle = i * angleStep;
      const x1 = cx + Math.cos(angle) * radius;
      const y1 = cy + Math.sin(angle) * radius;
      const x2 = cx + Math.cos(angle) * (radius + mag * radius * 2);
      const y2 = cy + Math.sin(angle) * (radius + mag * radius * 2);
      ctx.strokeStyle = spectrumColor(mag);
      ctx.beginPath(); ctx.moveTo(x1, y1); ctx.lineTo(x2, y2); ctx.stroke();
    }
  }

  function renderVU(w, h) {
    const ch = _channels;
    const left = rms(_samples, 0, ch >= 2 ? 2 : 1);
    const right = ch >= 2 ? rms(_samples, 1, 2) : left;
    const ldb = 20 * Math.log10(Math.max(left, 0.00001));
    const rdb = 20 * Math.log10(Math.max(right, 0.00001));
    const lLevel = Math.max(0, (ldb + 60) / 60);
    const rLevel = Math.max(0, (rdb + 60) / 60);

    const meterW = w * 0.7, meterH = 60, spacing = 40;
    const startX = (w - meterW) / 2;
    drawMeter(startX, h / 2 - meterH - spacing / 2, meterW, meterH, lLevel, 'L');
    drawMeter(startX, h / 2 + spacing / 2, meterW, meterH, rLevel, 'R');
    drawPeak(startX + meterW + 20, h / 2 - meterH - spacing / 2 + 15, left);
    drawPeak(startX + meterW + 20, h / 2 + spacing / 2 + 15, right);
  }

  function drawMeter(x, y, w, h, level, label) {
    ctx.fillStyle = '#1e1e1e';
    roundRect(x, y, w, h, 8); ctx.fill();
    const bw = w * level;
    if (bw > 0) {
      const grad = ctx.createLinearGradient(x, 0, x + w, 0);
      grad.addColorStop(0, '#4caf50');
      grad.addColorStop(0.7, '#ffc107');
      grad.addColorStop(1, '#e74c3c');
      ctx.fillStyle = grad;
      roundRect(x, y, bw, h, 8); ctx.fill();
    }
    ctx.fillStyle = '#fff';
    ctx.font = 'bold 20px Segoe UI, sans-serif';
    ctx.textAlign = 'right';
    ctx.fillText(label, x - 12, y + h / 2 + 7);
    ctx.textAlign = 'center';
    ctx.font = 'bold 14px Segoe UI, sans-serif';
    ctx.fillText(Math.round(level * 100) + '%', x + w / 2, y + h / 2 + 5);
    ctx.textAlign = 'left';
  }

  function drawPeak(x, y, level) {
    ctx.fillStyle = level > 0.9 ? '#e74c3c' : level > 0.7 ? '#ffc107' : '#4caf50';
    ctx.globalAlpha = Math.min(1, level * 2);
    ctx.beginPath(); ctx.arc(x + 15, y, 15, 0, Math.PI * 2); ctx.fill();
    ctx.globalAlpha = 1;
  }

  const particles = [];
  function renderParticles(w, h) {
    const energy = rms(_mono, 0, 1);
    if (energy > 0.1 && particles.length < 200) {
      const spawn = Math.min(12, Math.floor(energy * 10) + 1);
      for (let i = 0; i < spawn; i++) {
        particles.push({
          x: w / 2 + (Math.random() - 0.5) * 100,
          y: h / 2 + (Math.random() - 0.5) * 100,
          vx: (Math.random() - 0.5) * energy * 50,
          vy: (Math.random() - 0.5) * energy * 50,
          size: Math.random() * 8 + 2,
          life: 1.0,
          hue: Math.random() * 360
        });
      }
    }
    for (let i = particles.length - 1; i >= 0; i--) {
      const p = particles[i];
      p.x += p.vx; p.y += p.vy;
      p.vx *= 0.98; p.vy *= 0.98;
      p.life -= 0.012;
      if (p.life <= 0 || p.x < -20 || p.x > w + 20 || p.y < -20 || p.y > h + 20) { particles.splice(i, 1); continue; }
      ctx.globalAlpha = Math.max(0, p.life);
      ctx.fillStyle = `hsl(${p.hue}, 80%, 60%)`;
      ctx.beginPath(); ctx.arc(p.x, p.y, p.size, 0, Math.PI * 2); ctx.fill();
    }
    ctx.globalAlpha = 1;
  }

  function roundRect(x, y, w, h, r) {
    r = Math.min(r, w / 2, h / 2);
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }

  // ── Main loop ──────────────────────────────────────────────────
  function render() {
    requestAnimationFrame(render);
    const w = canvas.clientWidth, h = canvas.clientHeight;
    const dpr = window.devicePixelRatio || 1;
    if (w > 0 && h > 0 &&
        (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr))) {
      resize();
    }
    ctx.clearRect(0, 0, w, h);

    const idle = performance.now() - _lastFrameTime > 400;
    idleEl.style.display = idle ? 'flex' : 'none';
    if (idle) return;

    switch (mode) {
      case 'Spectrum': renderSpectrum(w, h); break;
      case 'Waveform': renderWaveform(w, h); break;
      case 'Circular': renderCircular(w, h); break;
      case 'VUMeter': renderVU(w, h); break;
      case 'Particles': renderParticles(w, h); break;
    }
  }
  requestAnimationFrame(render);

  // ── UI ─────────────────────────────────────────────────────────
  document.querySelectorAll('.viz-mode').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.viz-mode').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      mode = btn.dataset.mode;
      particles.length = 0;
    });
  });

  // Fullscreen is applied to the *native* window by the host: the DOM Fullscreen API
  // needs the embedder to handle WebKit's enter-fullscreen request, which Photino does
  // not do — so `document.documentElement.requestFullscreen()` silently rejects here.
  let _fullscreen = false;
  const fullscreenBtn = document.getElementById('viz-fullscreen');

  function setFullscreenIcon(on) {
    if (!fullscreenBtn) return;
    fullscreenBtn.innerHTML = on ? '<i class="fas fa-compress"></i>' : '<i class="fas fa-expand"></i>';
    fullscreenBtn.title = on ? 'Exit fullscreen' : 'Fullscreen';
  }

  function applyFullscreen(on) {
    _fullscreen = !!on;
    postToHost('setFullscreen', { window: 'visualization', enabled: _fullscreen });
    setFullscreenIcon(_fullscreen);
  }

  if (fullscreenBtn) {
    fullscreenBtn.addEventListener('click', () => applyFullscreen(!_fullscreen));
  }

  // Esc leaves fullscreen (the native window has no title bar to do it for us).
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && _fullscreen) applyFullscreen(false);
  });

  setFullscreenIcon(false);

  // Handshake: the host starts the audio tap only after the page is really ready.
  // On mobile the page may be constructed while hidden, so the first frame can be
  // missed; announcing again on visibility changes re-arms the stream.
  function announceReady() {
    resize();
    requestAnimationFrame(resize);
    postToHost('visualizationReady', {});
  }
  window.addEventListener('load', announceReady);
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) announceReady();
  });
  setTimeout(announceReady, 0);
})();
