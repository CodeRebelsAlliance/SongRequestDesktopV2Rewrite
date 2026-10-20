// Runtime Material palette from the current artwork, shared by the mobile pages.
// The host sends { primary, secondary } after extracting colours from the track.
(function (global) {
  'use strict';

  function parse(hex) {
    var value = String(hex || '').trim();
    var m = /^#?([0-9a-f]{6})$/i.exec(value);
    if (!m) return null;
    var n = parseInt(m[1], 16);
    return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
  }

  function clamp(v) { return Math.max(0, Math.min(255, Math.round(v))); }
  function rgb(c) { return 'rgb(' + clamp(c.r) + ',' + clamp(c.g) + ',' + clamp(c.b) + ')'; }
  function rgba(c, a) { return 'rgba(' + clamp(c.r) + ',' + clamp(c.g) + ',' + clamp(c.b) + ',' + a + ')'; }

  function readable(c) {
    var lum = 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
    if (lum >= 115) return c;
    // Lighten dark artwork colours so they remain usable as an accent on the
    // dark Material surfaces.
    var lift = (115 - lum) / 255 + 0.45;
    return {
      r: c.r + (255 - c.r) * lift,
      g: c.g + (255 - c.g) * lift,
      b: c.b + (255 - c.b) * lift
    };
  }

  function mix(baseHex, accent, amount) {
    var base = parse(baseHex);
    if (!base) return baseHex;
    return rgb({
      r: base.r + (accent.r - base.r) * amount,
      g: base.g + (accent.g - base.g) * amount,
      b: base.b + (accent.b - base.b) * amount
    });
  }

  function apply(data, prefix, baseSurface, baseSurface2) {
    var root = document.documentElement;
    if (!data || !data.primary) {
      ['-primary', '-primary-soft', '-accent-soft', '-surface', '-surface-2'].forEach(function (suffix) {
        root.style.removeProperty('--' + prefix + suffix);
      });
      return false;
    }
    var primary = parse(data.primary);
    if (!primary) return false;
    var secondary = parse(data.secondary) || primary;
    var accent = readable(primary);

    root.style.setProperty('--' + prefix + '-primary', rgb(accent));
    root.style.setProperty('--' + prefix + '-primary-soft', rgba(primary, 0.20));
    root.style.setProperty('--' + prefix + '-accent-soft', rgba(primary, 0.18));
    root.style.setProperty('--' + prefix + '-surface', mix(baseSurface, secondary, 0.10));
    root.style.setProperty('--' + prefix + '-surface-2', mix(baseSurface2, secondary, 0.13));
    return true;
  }

  global.SongRequestTheme = { apply: apply, parse: parse, rgba: rgba };
})(typeof window !== 'undefined' ? window : globalThis);
