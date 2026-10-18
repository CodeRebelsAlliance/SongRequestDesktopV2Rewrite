// Tests for the visualization math (newui/vizmath.js).
//
// These cover the three bugs that made the bars/waveform look wrong:
//   * log bands built from *floored* edges gave groups of identical bars
//     (8 the same, then 4, then 3 …),
//   * no frequency tilt, so the bass bars were pinned at full scale,
//   * the waveform mapping stopped at the buffer length, covering half the width.
//
// Run with: node --test tests/js
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const vizPath = path.join(__dirname, '..', '..', 'SongRequestDesktopV2Rewrite', 'newui', 'vizmath.js');
const viz = require(vizPath);

const BAR_COUNT = 64;
const FFT_SIZE = 512;
const BIN_COUNT = FFT_SIZE / 2;

function flatSpectrum(bins, value) {
  const mag = new Float64Array(bins);
  mag.fill(value);
  return mag;
}

/** A 1/f (pink-ish) spectrum: how real music looks in raw FFT magnitudes. */
function pinkSpectrum(bins) {
  const mag = new Float64Array(bins);
  for (let k = 1; k < bins; k++) mag[k] = 1 / k;
  return mag;
}

test('band edges increase strictly: no two bars can share a bin', () => {
  const edges = viz.spectrumEdges(BAR_COUNT, BIN_COUNT, 1, BIN_COUNT - 1);

  assert.strictEqual(edges.length, BAR_COUNT + 1);
  for (let b = 0; b < edges.length - 1; b++) {
    assert.ok(edges[b + 1] > edges[b], `edge ${b + 1} (${edges[b + 1]}) must be > edge ${b} (${edges[b]})`);
  }
  assert.ok(edges[BAR_COUNT] > BIN_COUNT - 1, 'the last bar must reach the top bin');
});

test('a varying spectrum produces 64 distinct bar values (the old mapping gave 8+4+3+3…)', () => {
  const edges = viz.spectrumEdges(BAR_COUNT, BIN_COUNT, 1, BIN_COUNT - 1);
  const bands = viz.bandsFromMagnitudes(pinkSpectrum(BIN_COUNT), edges);

  assert.strictEqual(bands.length, BAR_COUNT);
  for (let b = 1; b < bands.length; b++) {
    assert.ok(Math.abs(bands[b] - bands[b - 1]) > 1e-12,
      `bar ${b} (${bands[b]}) must differ from bar ${b - 1} (${bands[b - 1]})`);
  }
});

test('neighbouring bars are never a group of identical values for pink noise', () => {
  const edges = viz.spectrumEdges(BAR_COUNT, BIN_COUNT, 1, BIN_COUNT - 1);
  const bands = viz.bandsFromMagnitudes(pinkSpectrum(BIN_COUNT), edges);

  // longest run of (near) identical values — it used to be 8 identical bass bars
  let run = 1, worst = 1;
  for (let b = 1; b < bands.length; b++) {
    run = Math.abs(bands[b] - bands[b - 1]) < 1e-12 ? run + 1 : 1;
    if (run > worst) worst = run;
  }
  assert.strictEqual(worst, 1, `longest run of identical bars was ${worst}`);
});

test('the tilt stops the bass from towering over the rest of the spectrum', () => {
  const edges = viz.spectrumEdges(BAR_COUNT, BIN_COUNT, 1, BIN_COUNT - 1);
  const raw = viz.bandsFromMagnitudes(pinkSpectrum(BIN_COUNT), edges);
  const tilted = viz.applyTilt(raw, edges, 44100, FFT_SIZE);

  const mid = Math.floor(BAR_COUNT / 2);
  const rawRatio = raw[0] / raw[mid];
  const tiltedRatio = tilted[0] / tilted[mid];

  assert.ok(rawRatio > 8, `sanity: the raw 1/f spectrum has the bass ${rawRatio.toFixed(1)}x the mids`);
  assert.ok(tiltedRatio < 3,
    `after the tilt the bass must be no longer dominant, but it is ${tiltedRatio.toFixed(1)}x the mids`);
  assert.ok(tiltedRatio < rawRatio / 4,
    `the tilt must flatten the spectrum (${rawRatio.toFixed(1)}x -> ${tiltedRatio.toFixed(1)}x)`);
});

test('the display uses the whole spectrum instead of a wall of bass bars', () => {
  const edges = viz.spectrumEdges(BAR_COUNT, BIN_COUNT, 1, BIN_COUNT - 1);
  const spectrum = pinkSpectrum(BIN_COUNT);

  // What renderSpectrum does: auto-gain puts the loudest band at 0.6, then the bar is
  // drawn with a 1.6x multiplier and clamped to the canvas.
  const heightsFor = (bands) => {
    const max = Math.max(...bands);
    return Array.from(bands, v => Math.min((v / max) * 0.6 * 1.6, 1));
  };

  const rawHeights = heightsFor(viz.bandsFromMagnitudes(spectrum, edges));
  const tiltedHeights = heightsFor(viz.applyTilt(
    viz.bandsFromMagnitudes(spectrum, edges), edges, 44100, FFT_SIZE));

  const visible = (hs) => hs.filter(h => h > 0.15).length;

  assert.ok(visible(rawHeights) < BAR_COUNT / 2,
    `sanity: without the tilt only ${visible(rawHeights)}/${BAR_COUNT} bars are visible`);
  assert.ok(visible(tiltedHeights) > BAR_COUNT * 0.75,
    `with the tilt most bars should be visible, but only ${visible(tiltedHeights)}/${BAR_COUNT} are`);

  // and nothing may sit pinned at the top of the canvas
  assert.ok(Math.max(...tiltedHeights) < 1, 'no bar may be clipped at full height');
});

test('tilt gain grows with frequency and is 1 at the reference', () => {
  const edges = viz.spectrumEdges(BAR_COUNT, BIN_COUNT, 1, BIN_COUNT - 1);
  const bands = flatSpectrum(BAR_COUNT, 1);
  const tilted = viz.applyTilt(bands, edges, 44100, FFT_SIZE);

  for (let b = 1; b < BAR_COUNT; b++) {
    assert.ok(tilted[b] > tilted[b - 1], `gain must grow with frequency (bar ${b})`);
  }
  // 200 Hz default reference -> the band containing it is ~1
  const hz = viz.bandCenterHz(edges, 3, 44100, FFT_SIZE);
  assert.ok(hz > 100 && hz < 400, `sanity: band 3 is ${hz} Hz`);
  const refBand = viz.applyTilt(flatSpectrum(BAR_COUNT, 1), edges, 44100, FFT_SIZE);
  const atRef = refBand.find((v, i) => {
    const hz = viz.bandCenterHz(edges, i, 44100, FFT_SIZE);
    return hz >= 200;
  });
  assert.ok(Math.abs(atRef - 1) < 0.2, `the band at the reference is ${atRef}, expected ~1`);
});

test('the waveform spans the whole width (last column = last sample)', () => {
  const width = 1000;
  const samples = 512;

  assert.strictEqual(viz.sampleIndexForColumn(0, width, samples), 0);
  assert.strictEqual(viz.sampleIndexForColumn(width - 1, width, samples), samples - 1,
    'the last column must map to the last sample, not stop halfway');

  let prev = -1;
  for (let x = 0; x < width; x++) {
    const idx = viz.sampleIndexForColumn(x, width, samples);
    assert.ok(idx > prev, `column ${x} must advance (${idx} > ${prev})`);
    prev = idx;
  }
});

test('the waveform covers the full width when there are more samples than columns', () => {
  const width = 400;
  const samples = 4096;
  assert.strictEqual(viz.sampleIndexForColumn(width - 1, width, samples), samples - 1);
  assert.strictEqual(viz.sampleIndexForColumn(0, width, samples), 0);
});

test('sampleAt interpolates linearly and clamps at the ends', () => {
  const data = new Float32Array([0, 1, 0, -1]);
  assert.strictEqual(viz.sampleAt(data, 0), 0);
  assert.strictEqual(viz.sampleAt(data, 1), 1);
  assert.strictEqual(viz.sampleAt(data, 1.5), 0.5);
  assert.strictEqual(viz.sampleAt(data, -5), 0);
  assert.strictEqual(viz.sampleAt(data, 99), -1);
});

test('fft finds the frequency of a pure tone', () => {
  const n = 512;
  const re = new Float32Array(n);
  const im = new Float32Array(n);
  const bin = 32;              // bin 32 of 512 @ 44.1kHz = 2756 Hz
  for (let i = 0; i < n; i++) re[i] = Math.sin(2 * Math.PI * bin * i / n);

  viz.fft(re, im);
  const mag = viz.magnitudes(re, im, n / 2);

  let peak = 0;
  for (let k = 1; k < mag.length; k++) if (mag[k] > mag[peak]) peak = k;
  assert.strictEqual(peak, bin);
});
