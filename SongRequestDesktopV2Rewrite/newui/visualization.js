(function () {
  'use strict';

  // ── IPC ────────────────────────────────────────────────────────
  function handleIncoming(json) {
    try {
      const msg = JSON.parse(json);
      if (msg.type === 'event' && msg.eventName === 'audioSamples') {
        decodeFrame(msg.data);
      }
    } catch (e) { console.error('Parse error:', e); }
  }
  if (!window.external) window.external = {};
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
    canvas.width = Math.max(1, Math.round(rect.width * dpr));
    canvas.height = Math.max(1, Math.round(rect.height * dpr));
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }
  window.addEventListener('resize', resize);
  resize();

  // ── FFT (iterative radix-2) ────────────────────────────────────
  function fft(re, im) {
    const n = re.length;
    for (let i = 1, j = 0; i < n; i++) {
      let bit = n >> 1;
      for (; j & bit; bit >>= 1) j ^= bit;
      j ^= bit;
      if (i < j) {
        let t = re[i]; re[i] = re[j]; re[j] = t;
        t = im[i]; im[i] = im[j]; im[j] = t;
      }
    }
    for (let len = 2; len <= n; len <<= 1) {
      const ang = -2 * Math.PI / len;
      const wr = Math.cos(ang), wi = Math.sin(ang);
      for (let i = 0; i < n; i += len) {
        let cwr = 1, cwi = 0;
        for (let j = 0; j < len / 2; j++) {
          const ur = re[i + j], ui = im[i + j];
          const vr = re[i + j + len / 2] * cwr - im[i + j + len / 2] * cwi;
          const vi = re[i + j + len / 2] * cwi + im[i + j + len / 2] * cwr;
          re[i + j] = ur + vr; im[i + j] = ui + vi;
          re[i + j + len / 2] = ur - vr; im[i + j + len / 2] = ui - vi;
          const nwr = cwr * wr - cwi * wi;
          cwi = cwr * wi + cwi * wr;
          cwr = nwr;
        }
      }
    }
  }

  const BAR_COUNT = 64;
  const FFT_SIZE = 512;
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

    const half = N / 2;
    // Logarithmically spaced bands
    const bands = new Float32Array(BAR_COUNT);
    let maxVal = 0.0001;
    for (let b = 0; b < BAR_COUNT; b++) {
      const lo = Math.floor(Math.pow(half, b / BAR_COUNT));
      const hi = Math.max(lo + 1, Math.floor(Math.pow(half, (b + 1) / BAR_COUNT)));
      let sum = 0, count = 0;
      for (let k = lo; k < hi && k < half; k++) {
        const mag = Math.sqrt(re[k] * re[k] + im[k] * im[k]) / N;
        sum += mag; count++;
      }
      const val = count > 0 ? sum / count : 0;
      bands[b] = val;
      if (val > maxVal) maxVal = val;
    }

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
    ctx.strokeStyle = 'rgba(120,120,120,0.3)';
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(0, centerY); ctx.lineTo(w, centerY); ctx.stroke();

    ctx.strokeStyle = '#5b8def';
    ctx.lineWidth = 2;
    ctx.lineJoin = 'round';
    ctx.beginPath();
    const step = Math.max(1, Math.floor(mono.length / w));
    let started = false;
    for (let x = 0; x < w; x++) {
      const idx = x * step;
      if (idx >= mono.length) break;
      const y = centerY + mono[idx] * h * 0.4;
      if (!started) { ctx.moveTo(x, y); started = true; } else ctx.lineTo(x, y);
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
  document.getElementById('viz-fullscreen').addEventListener('click', () => {
    if (document.fullscreenElement) document.exitFullscreen();
    else document.documentElement.requestFullscreen();
  });
})();
