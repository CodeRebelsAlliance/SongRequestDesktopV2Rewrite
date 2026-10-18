// Pure math for the audio visualization (newui/visualization.js).
//
// Kept dependency-free and DOM-free so `node --test tests/js` can cover it: the three
// bugs that made the bars look wrong were all in here (log bands built from *floored*
// edges, no frequency tilt, and a waveform mapping that stopped halfway across).
(function (global) {
  'use strict';

  // ── FFT (iterative radix-2, in place) ─────────────────────────────────────
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

  // Magnitude of the (already transformed) spectrum, normalised by the window size.
  function magnitudes(re, im, binCount) {
    const half = binCount == null ? re.length / 2 : binCount;
    const mag = new Float64Array(half);
    for (let k = 0; k < half; k++) mag[k] = Math.sqrt(re[k] * re[k] + im[k] * im[k]) / re.length;
    return mag;
  }

  /**
   * Log-spaced *fractional* band edges over the usable bins.
   *
   * The edges are deliberately not floored: with integer edges (and 64 bars over 256
   * bins) `floor(binCount^(b/64))` stays at 1 for the first eight bars, so bars 1-8
   * all showed bin 1, the next four showed bin 2, and so on — the bars came out in
   * groups of identical values. Fractional edges give every bar its own slice, with
   * sub-bin bands interpolated.
   */
  function spectrumEdges(barCount, binCount, minBin, maxBin) {
    const lo = Math.max(0.5, minBin == null ? 1 : minBin);
    const hi = Math.max(lo + 1, maxBin == null ? binCount - 1 : maxBin);
    const edges = new Float64Array(barCount + 1);
    for (let b = 0; b <= barCount; b++) {
      edges[b] = lo * Math.pow(hi / lo, b / barCount);
    }
    edges[barCount] = hi + 1;   // the last bar always reaches the top bin
    return edges;
  }

  /** Magnitude at a fractional bin index (linear interpolation between bins). */
  function magnitudeAt(mag, index) {
    if (mag.length === 0) return 0;
    if (index <= 0) return mag[0];
    if (index >= mag.length - 1) return mag[mag.length - 1];
    const i0 = Math.floor(index);
    const f = index - i0;
    return mag[i0] + (mag[i0 + 1] - mag[i0]) * f;
  }

  /**
   * Average magnitude per band, sampled across the (interpolated) spectrum.
   *
   * The lowest log bands are narrower than one FFT bin; averaging the integer bins
   * they overlap would return the *same* bin for several bars in a row (that is the
   * "first 8 bars are identical, then 4, then 3…" bug). Sampling the interpolated
   * spectrum instead gives every bar its own value.
   */
  function bandsFromMagnitudes(mag, edges) {
    const barCount = edges.length - 1;
    const bands = new Float64Array(barCount);
    for (let b = 0; b < barCount; b++) {
      const lo = edges[b], hi = edges[b + 1];
      const span = hi - lo;
      const samples = Math.max(3, Math.min(96, Math.ceil(span * 4)));
      let sum = 0;
      for (let s = 0; s < samples; s++) {
        const t = (s + 0.5) / samples;
        sum += magnitudeAt(mag, lo + span * t);
      }
      bands[b] = sum / samples;
    }
    return bands;
  }

  function bandCenterHz(edges, index, sampleRate, fftSize) {
    const hzPerBin = (sampleRate || 44100) / (fftSize || 512);
    return ((edges[index] + edges[index + 1]) / 2) * hzPerBin;
  }

  /**
   * Frequency tilt.
   *
   * Music carries most of its energy at the bottom (roughly 1/f in amplitude), so raw
   * bands leave the bass bars pinned at full scale while everything else stays near
   * zero — the bass looked "almost always max". Lifting each band by
   * (f / ref)^exponent counters that: 0.75 is a three-quarter slope, which makes a
   * typical music spectrum come out close to flat, so the mids and highs get real
   * range instead of being crushed into the bottom few pixels. The auto-gain then
   * normalises to whatever is loudest.
   */
  function applyTilt(bands, edges, sampleRate, fftSize, exponent, refHz) {
    const exp = exponent == null ? 0.75 : exponent;
    const ref = refHz == null ? 200 : refHz;
    const out = new Float64Array(bands.length);
    for (let b = 0; b < bands.length; b++) {
      const hz = Math.max(bandCenterHz(edges, b, sampleRate, fftSize), 20);
      out[b] = bands[b] * Math.pow(hz / ref, exp);
    }
    return out;
  }

  /**
   * Fractional sample position for a canvas column, spanning the *whole* buffer across
   * the whole width.
   *
   * The old waveform walk used `step = max(1, floor(len / width))` and stopped at the
   * end of the buffer, so with a 512-sample frame on a ~1000px canvas it drew 512px and
   * then broke out of the loop — the trace covered only half the view.
   */
  function sampleIndexForColumn(column, width, sampleCount) {
    if (sampleCount <= 0) return 0;
    if (width <= 1 || sampleCount === 1) return 0;
    const x = Math.min(Math.max(column, 0), width - 1) / (width - 1);
    return x * (sampleCount - 1);
  }

  /** Linear interpolation between samples. */
  function sampleAt(samples, index) {
    if (samples.length === 0) return 0;
    const i0 = Math.max(0, Math.min(samples.length - 1, Math.floor(index)));
    const i1 = Math.min(samples.length - 1, i0 + 1);
    const f = Math.max(0, Math.min(1, index - i0));
    return samples[i0] + (samples[i1] - samples[i0]) * f;
  }

  const api = {
    fft: fft,
    magnitudes: magnitudes,
    spectrumEdges: spectrumEdges,
    bandsFromMagnitudes: bandsFromMagnitudes,
    magnitudeAt: magnitudeAt,
    bandCenterHz: bandCenterHz,
    applyTilt: applyTilt,
    sampleIndexForColumn: sampleIndexForColumn,
    sampleAt: sampleAt,
  };

  global.SongRequestViz = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
