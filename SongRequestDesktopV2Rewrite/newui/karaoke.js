// Shared karaoke renderer: syllable-by-syllable lyric timing and animated
// long-pause bars. The host sends inferred syllable timings in the lyrics
// payload; this module only renders/animates them so desktop, mobile and
// presentation stay identical.
//
// Performance notes: the host publishes the play position only ~4 times a
// second. Re-painting every syllable in the song on each update (the old
// implementation) meant thousands of style/class writes per tick and made long
// lyrics stutter, especially on the HDMI WebView. The painter now caches the
// line/span elements and only touches the line that is actually being sung.
// Between host updates it advances the clock with requestAnimationFrame, so the
// wipe moves smoothly instead of jumping in 250-500 ms steps.
(function (global) {
  'use strict';

  var enabled = false;
  var lastTime = 0;
  var maxLiftDefault = 6.5;

  // Interpolated playback clock. `base` is the last host time, `stamp` the
  // monotonic time at which it arrived.
  var clock = { playing: false, base: 0, stamp: 0 };
  var rafId = 0;

  // Containers currently on screen. Kept as an array (small, max 3) so the
  // animation loop can paint all of them with one rAF callback.
  var containers = [];
  var cache = typeof WeakMap === 'function' ? new WeakMap() : null;

  function doc() { return global.document; }

  function nowMs() {
    if (global.performance && typeof global.performance.now === 'function') {
      return global.performance.now();
    }
    return Date.now();
  }

  function num(value) {
    var n = Number(value);
    return isFinite(n) ? n : NaN;
  }

  function invalidate(container) {
    if (container && cache) cache.delete(container);
  }

  function setEnabled(on) {
    enabled = !!on;
    var root = doc() && doc().documentElement;
    if (root) root.setAttribute('data-karaoke', enabled ? 'on' : 'off');

    // Leave no half-painted wipe behind when karaoke mode is turned off.
    for (var i = 0; i < containers.length; i++) {
      var entry = entryFor(containers[i]);
      if (entry) resetEntry(entry);
    }
    if (enabled) paintAll(currentTime());
    else stopLoop();

    try {
      if (doc()) doc().dispatchEvent(new CustomEvent('karaokechange', { detail: { enabled: enabled } }));
    } catch (e) { }
  }

  function isEnabled() { return enabled; }

  function setPlaying(on) {
    var playing = !!on;
    if (clock.playing === playing) return;
    clock.base = currentTime();
    clock.stamp = nowMs();
    clock.playing = playing;
    if (playing) startLoop();
    else stopLoop();
  }

  function currentTime() {
    if (!clock.playing) return lastTime;
    return clock.base + (nowMs() - clock.stamp) / 1000;
  }

  /**
   * Replace a lyric line's text with karaoke syllable spans. Each unit carries
   * its own start/end, so the wipe always spans that syllable's sung duration.
   * Spaces are inserted between words only, never between syllables.
   */
  function renderLine(element, line) {
    if (!element || !line) return;

    var units = line.syllables;
    if (!units || !units.length) {
      units = [];
      var words = line.words || [];
      for (var w = 0; w < words.length; w++) {
        units.push({
          start: words[w].start,
          end: words[w].end,
          text: words[w].text || '',
          wordIndex: w
        });
      }
    }
    if (!units.length) return;

    var lineStart = Number(line.time);
    var lineEnd = Number(line.end);
    if (!isFinite(lineStart)) lineStart = Number(units[0].start);
    if (!isFinite(lineEnd)) lineEnd = Number(units[units.length - 1].end);
    if (isFinite(lineStart)) element.dataset.karaokeStart = lineStart;
    if (isFinite(lineEnd)) element.dataset.karaokeEnd = lineEnd;

    // Use the snapped line time for highlighting / pause placement too.
    if (isFinite(lineStart)) element.dataset.time = lineStart;

    element.innerHTML = '';
    for (var i = 0; i < units.length; i++) {
      var unit = units[i];
      var span = doc().createElement('span');
      span.className = 'karaoke-word karaoke-syllable';
      span.dataset.start = unit.start;
      span.dataset.end = unit.end;
      span.dataset.text = unit.text || '';
      span.dataset.word = unit.wordIndex == null ? i : unit.wordIndex;
      span.textContent = unit.text || '';

      // Smooth the wipe for this unit's length. With the interpolated clock
      // these transitions only need to bridge the few milliseconds between
      // animation frames, so a slightly longer fade stays fluent without the
      // syllable looking late.
      var duration = Math.max(0.01, Number(unit.end) - Number(unit.start));
      var smooth = Math.min(0.18, Math.max(0.06, duration * 0.55));
      span.style.setProperty('--karaoke-smooth', smooth.toFixed(3) + 's');
      var liftTime = Math.min(0.18, Math.max(0.07, duration * 0.50));
      span.style.setProperty('--karaoke-lift-time', liftTime.toFixed(3) + 's');
      var colorTime = Math.min(0.16, Math.max(0.06, duration * 0.45));
      span.style.setProperty('--karaoke-color-time', colorTime.toFixed(3) + 's');

      element.appendChild(span);

      var next = units[i + 1];
      if (!next) continue;
      var thisWord = unit.wordIndex == null ? i : unit.wordIndex;
      var nextWord = next.wordIndex == null ? i + 1 : next.wordIndex;
      if (nextWord !== thisWord) element.appendChild(doc().createTextNode(' '));
    }

    invalidate(element.parentNode);
  }

  function getVisibleLines(container) {
    if (!container) return [];
    var all = container.querySelectorAll('.lyrics-line[data-time], .mm-line[data-time]');
    var result = [];
    for (var i = 0; i < all.length; i++) {
      if (String(all[i].textContent || '').trim().length > 0) result.push(all[i]);
    }
    return result;
  }

  /**
   * Picks the lyric line a chapter marker belongs to. Analysis boundaries can be
   * slightly after the real line; prefer the previous line when it is much closer
   * than the next one instead of always placing the marker one line late.
   */
  function chooseLineForTime(container, time) {
    var lines = getVisibleLines(container);
    if (!lines.length) return null;
    var previous = null;
    var previousTime = -Infinity;
    for (var i = 0; i < lines.length; i++) {
      var t = Number(lines[i].dataset.time);
      if (!isFinite(t)) continue;
      if (t >= time - 0.01) {
        if (!previous) return lines[i];
        var gap = t - previousTime;
        return (time - previousTime) <= Math.max(0.8, gap * 0.5) ? previous : lines[i];
      }
      previous = lines[i];
      previousTime = t;
    }
    return previous;
  }

  /** Next visible lyric line at/after a gap end (used by pause bars). */
  function nextLineForTime(container, time) {
    var lines = getVisibleLines(container);
    for (var i = 0; i < lines.length; i++) {
      var t = Number(lines[i].dataset.time);
      if (isFinite(t) && t >= time - 0.05) return lines[i];
    }
    return null;
  }

  /** Insert pause elements into the lyrics flow. They stay hidden until reached. */
  function attachPauses(container, pauses) {
    if (!container) return;
    var old = container.querySelectorAll('.karaoke-pause');
    for (var i = 0; i < old.length; i++) old[i].remove();
    if (!pauses || !pauses.length) { invalidate(container); return; }

    for (var p = 0; p < pauses.length; p++) {
      var pause = pauses[p];
      var element = doc().createElement('div');
      element.className = 'karaoke-pause';
      element.hidden = true;
      element.dataset.start = pause.start;
      element.dataset.end = pause.end;
      element.innerHTML =
        '<div class="karaoke-pause-track"><div class="karaoke-pause-fill"></div></div>' +
        '<div class="karaoke-pause-caption">' +
          '<span class="karaoke-pause-label"><i class="fas fa-hourglass-half"></i> Instrumental</span>' +
          '<span class="karaoke-pause-time">0:00</span>' +
        '</div>';

      var target = nextLineForTime(container, pause.end);
      if (target && target.parentNode) target.parentNode.insertBefore(element, target);
      else {
        var spacer = container.querySelector('.lyrics-spacer, .mm-lyrics-spacer');
        if (spacer && spacer.parentNode) spacer.parentNode.insertBefore(element, spacer);
        else container.appendChild(element);
      }
    }

    invalidate(container);
  }

  function formatTime(seconds) {
    seconds = Math.max(0, Math.ceil(Number(seconds) || 0));
    var m = Math.floor(seconds / 60);
    var s = seconds % 60;
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  // ── Span cache ────────────────────────────────────────────
  // Each cache entry keeps only lines that have karaoke spans. Plain lyric
  // lines (and empty timed markers) are skipped, so the painter never walks
  // them at all.

  function entryFor(container) {
    if (!container || !cache) return null;
    var entry = cache.get(container);
    if (entry) return entry;

    var lineEls = container.querySelectorAll('.lyrics-line, .mm-line');
    var lines = [];
    var totalSpans = 0;

    for (var i = 0; i < lineEls.length; i++) {
      var lineEl = lineEls[i];
      var spanEls = lineEl.querySelectorAll('.karaoke-word');
      if (!spanEls.length) continue;

      var spans = [];
      var firstStart = num(spanEls[0].dataset.start);
      var lastEnd = num(spanEls[spanEls.length - 1].dataset.end);
      var lineStart = num(lineEl.dataset.karaokeStart);
      var lineEnd = num(lineEl.dataset.karaokeEnd);
      if (!isFinite(lineStart)) lineStart = firstStart;
      if (!isFinite(lineEnd)) lineEnd = lastEnd;

      for (var s = 0; s < spanEls.length; s++) {
        var spanEl = spanEls[s];
        var start = num(spanEl.dataset.start);
        var end = num(spanEl.dataset.end);
        spans.push({
          el: spanEl,
          start: start,
          end: end,
          progress: -1,
          lift: null,
          sung: null,
          active: null
        });

        // Initialise every span once, so lines that have not been reached yet
        // are explicitly at rest (the old painter wrote this every tick).
        setSpan(spans[spans.length - 1], 0, false, false, 0);
      }

      lines.push({
        el: lineEl,
        start: lineStart,
        end: lineEnd,
        spans: spans
      });
      totalSpans += spans.length;
    }

    entry = {
      lines: lines,
      spans: totalSpans,
      active: -1,
      sungThrough: -1,
      pauses: container.querySelectorAll('.karaoke-pause')
    };
    cache.set(container, entry);
    return entry;
  }

  function resetEntry(entry) {
    for (var i = 0; i < entry.lines.length; i++) {
      paintLine(entry.lines[i], 0, false, false, -1);
    }
    entry.active = -1;
    entry.sungThrough = -1;
    entry.pauses = entry.pauses || [];
  }

  // ── Painting ──────────────────────────────────────────────

  function setSpan(span, progress, sung, active, lift) {
    var pct = Math.round(progress * 100);
    if (span.progress !== pct) {
      span.progress = pct;
      span.el.style.setProperty('--karaoke-progress', pct + '%');
    }
    if (span.sung !== sung) {
      span.sung = sung;
      span.el.classList.toggle('is-sung', sung);
    }
    if (span.active !== active) {
      span.active = active;
      span.el.classList.toggle('is-active', active);
    }
    if (span.lift !== lift) {
      span.lift = lift;
      span.el.style.setProperty('--karaoke-lift', lift.toFixed(2) + 'px');
    }
  }

  function paintLine(line, time, lineActive, activeIndex, maxLift) {
    var spans = line.spans;
    var lift = maxLift == null ? 0 : maxLift;

    for (var i = 0; i < spans.length; i++) {
      var span = spans[i];
      var start = span.start;
      var end = span.end;

      var progress = 0;
      if (enabled && isFinite(start) && isFinite(end) && end > start) {
        // The wipe spans exactly the syllable's sung duration.
        progress = Math.max(0, Math.min(1, (time - start) / (end - start)));
      }

      var isSung = enabled && isFinite(end) && time >= end;
      var isActive = enabled && isFinite(start) && isFinite(end) &&
        time >= start && time < end;

      var distance = lineActive && activeIndex >= 0 ? Math.abs(i - activeIndex) : 99;
      var liftFactor = distance === 0 ? 1.0
        : distance === 1 ? 0.58
        : distance === 2 ? 0.24
        : 0;
      var liftPx = distance < 99 ? -lift * liftFactor : 0;

      setSpan(span, progress, isSung, isActive, liftPx);
    }
  }

  function paintLineFull(line, maxLift) {
    paintLine(line, Number.MAX_VALUE, false, -1, maxLift || 0);
  }

  function paintLineRest(line) {
    paintLine(line, -1, false, -1, 0);
  }

  /** Binary search: last line whose start is at/before `time`. */
  function lineIndexAt(lines, time) {
    var lo = 0;
    var hi = lines.length - 1;
    var found = -1;
    while (lo <= hi) {
      var mid = (lo + hi) >> 1;
      var start = lines[mid].start;
      if (isFinite(start) && start <= time) {
        found = mid;
        lo = mid + 1;
      } else {
        hi = mid - 1;
      }
    }
    return found;
  }

  function paint(container, time) {
    var entry = entryFor(container);
    if (!entry || !entry.lines.length) return;

    var lines = entry.lines;
    var index = lineIndexAt(lines, time);

    if (index !== entry.active) {
      if (index > entry.sungThrough + 1) {
        // Moved forward (or jumped): finish the lines we skipped.
        var from = Math.max(0, entry.sungThrough + 1);
        for (var f = from; f < index; f++) paintLineFull(lines[f], 0);
      } else if (index < entry.active) {
        // Seeked backwards: clear every line that is no longer sung, including
        // the line that was partially painted when the seek happened.
        var upper = Math.max(entry.active, entry.sungThrough);
        for (var b = index + 1; b <= upper; b++) paintLineRest(lines[b]);
      }
      entry.active = index;
      entry.sungThrough = index > 0 ? index - 1 : -1;
    }

    if (index >= 0) {
      var line = lines[index];
      var lineActive = enabled && isFinite(line.start) && isFinite(line.end) &&
        time >= line.start && time < line.end;
      var activeIndex = -1;

      if (lineActive) {
        var spans = line.spans;
        for (var a = 0; a < spans.length; a++) {
          var as = spans[a].start;
          var ae = spans[a].end;
          if (isFinite(as) && isFinite(ae) && time >= as && time < ae) {
            activeIndex = a;
            break;
          }
        }

        // Between syllables inside the same line: keep the wave on the last
        // syllable that has started instead of jumping to another line.
        if (activeIndex < 0) {
          for (var c = 0; c < spans.length; c++) {
            var started = spans[c].start;
            if (isFinite(started) && time >= started) activeIndex = c;
          }
        }
      }

      var maxLift = container.id === 'mm-lyrics' ? 5.0 : maxLiftDefault;
      paintLine(line, time, lineActive, activeIndex, maxLift);
    }

    paintPauses(entry, time);
  }

  function paintPauses(entry, time) {
    if (!entry.pauses || !entry.pauses.length) return;
    for (var p = 0; p < entry.pauses.length; p++) {
      var pause = entry.pauses[p];
      var pauseStart = num(pause.dataset.start);
      var pauseEnd = num(pause.dataset.end);
      var active = enabled && isFinite(pauseStart) && isFinite(pauseEnd) &&
        time >= pauseStart && time < pauseEnd;

      if (pause.hidden === active) pause.hidden = !active;
      pause.classList.toggle('is-active', active);
      if (!active) continue;

      var duration = Math.max(0.001, pauseEnd - pauseStart);
      var pct = Math.max(0, Math.min(100, (time - pauseStart) / duration * 100));
      var fill = pause.querySelector('.karaoke-pause-fill');
      var label = pause.querySelector('.karaoke-pause-time');
      if (fill) fill.style.width = pct + '%';
      if (label) label.textContent = formatTime(pauseEnd - time);
    }
  }

  function paintAll(time) {
    var painted = 0;
    for (var i = containers.length - 1; i >= 0; i--) {
      var container = containers[i];
      if (!container || !container.isConnected) {
        containers.splice(i, 1);
        if (cache) cache.delete(container);
        continue;
      }
      // No point animating a hidden lyrics panel (the mobile view keeps the
      // last DOM around while closed).
      if (container.hidden) continue;
      paint(container, time);
      painted++;
    }
    return painted;
  }

  function register(container) {
    if (!container) return;
    if (containers.indexOf(container) < 0) containers.push(container);
  }

  // ── Interpolated clock ────────────────────────────────────

  function startLoop() {
    if (rafId) return;
    if (!global.requestAnimationFrame) return;
    rafId = global.requestAnimationFrame(frame);
  }

  function stopLoop() {
    if (!rafId) return;
    try { global.cancelAnimationFrame(rafId); } catch (e) { }
    rafId = 0;
  }

  function frame() {
    rafId = 0;
    if (!clock.playing) return;
    // If every lyrics panel is hidden, stop burning animation frames. The next
    // update() restarts the loop when a panel becomes visible again.
    if (paintAll(currentTime()) > 0) startLoop();
  }

  /**
   * Paint the current time for a container. When `playing` is supplied, the
   * module also starts/stops its interpolated clock so the wipe keeps moving
   * between the host's ~4 Hz position updates.
   */
  function update(container, currentTime, playing) {
    if (!container) return;
    lastTime = Number(currentTime) || 0;

    if (playing != null) {
      clock.playing = !!playing;
    }
    clock.base = lastTime;
    clock.stamp = nowMs();

    register(container);
    paint(container, lastTime);

    if (clock.playing) startLoop();
    else stopLoop();
  }

  function updateAll() {
    if (!doc()) return;
    var time = currentTime();
    var ids = ['expanded-lyrics', 'mm-lyrics', 'presentation-lyrics'];
    for (var i = 0; i < ids.length; i++) {
      var container = doc().getElementById(ids[i]);
      if (container) {
        register(container);
        paint(container, time);
      }
    }
    if (clock.playing) startLoop();
  }

  global.SongRequestKaraoke = {
    setEnabled: setEnabled,
    isEnabled: isEnabled,
    setPlaying: setPlaying,
    renderLine: renderLine,
    attachPauses: attachPauses,
    chooseLineForTime: chooseLineForTime,
    nextLineForTime: nextLineForTime,
    update: update,
    updateAll: updateAll,
    invalidate: invalidate
  };
})(typeof window !== 'undefined' ? window : globalThis);
