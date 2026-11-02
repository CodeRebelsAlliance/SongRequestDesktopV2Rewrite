// Shared karaoke renderer: syllable-by-syllable lyric timing and animated
// long-pause bars. The host sends inferred syllable timings in the lyrics
// payload; this module only renders/animates them so desktop, mobile and
// presentation stay identical.
(function (global) {
  'use strict';

  var enabled = false;
  var lastTime = 0;

  function doc() { return global.document; }

  function setEnabled(on) {
    enabled = !!on;
    var root = doc() && doc().documentElement;
    if (root) root.setAttribute('data-karaoke', enabled ? 'on' : 'off');
    updateAll();
    try {
      if (doc()) doc().dispatchEvent(new CustomEvent('karaokechange', { detail: { enabled: enabled } }));
    } catch (e) { }
  }

  function isEnabled() { return enabled; }

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

      // Smooth the wipe for this unit's length. Short syllables get a short
      // transition so they finish on time; long ones get a little more easing.
      var duration = Math.max(0.01, Number(unit.end) - Number(unit.start));
      var smooth = Math.min(0.085, Math.max(0.02, duration * 0.35));
      span.style.setProperty('--karaoke-smooth', smooth.toFixed(3) + 's');
      var liftTime = Math.min(0.16, Math.max(0.04, duration * 0.45));
      span.style.setProperty('--karaoke-lift-time', liftTime.toFixed(3) + 's');
      var colorTime = Math.min(0.14, Math.max(0.03, duration * 0.40));
      span.style.setProperty('--karaoke-color-time', colorTime.toFixed(3) + 's');

      element.appendChild(span);

      var next = units[i + 1];
      if (!next) continue;
      var thisWord = unit.wordIndex == null ? i : unit.wordIndex;
      var nextWord = next.wordIndex == null ? i + 1 : next.wordIndex;
      if (nextWord !== thisWord) element.appendChild(doc().createTextNode(' '));
    }
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
    if (!pauses || !pauses.length) return;

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
  }

  function formatTime(seconds) {
    seconds = Math.max(0, Math.ceil(Number(seconds) || 0));
    var m = Math.floor(seconds / 60);
    var s = seconds % 60;
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  function update(container, currentTime) {
    if (!container) return;
    lastTime = Number(currentTime) || 0;

    var maxLift = container.id === 'mm-lyrics' ? 5.0 : 6.5;

    // Process line by line. The lift/wave must never cross a line boundary:
    // previously a global word index let the last word of one line lift the
    // first words of the next (and vice versa).
    var lines = container.querySelectorAll('.lyrics-line, .mm-line');
    for (var li = 0; li < lines.length; li++) {
      var line = lines[li];
      var spans = line.querySelectorAll('.karaoke-word');
      if (!spans.length) continue;

      var lineStart = Number(line.dataset.karaokeStart);
      var lineEnd = Number(line.dataset.karaokeEnd);
      if (!isFinite(lineStart)) lineStart = Number(spans[0].dataset.start);
      if (!isFinite(lineEnd)) lineEnd = Number(spans[spans.length - 1].dataset.end);

      var lineActive = enabled && isFinite(lineStart) && isFinite(lineEnd) &&
        lastTime >= lineStart && lastTime < lineEnd;

      var activeIndex = -1;
      if (lineActive) {
        for (var a = 0; a < spans.length; a++) {
          var as = Number(spans[a].dataset.start);
          var ae = Number(spans[a].dataset.end);
          if (isFinite(as) && isFinite(ae) && lastTime >= as && lastTime < ae) {
            activeIndex = a;
            break;
          }
        }

        // Between syllables inside the same line: keep the wave on the last
        // syllable that has started instead of jumping to another line.
        if (activeIndex < 0) {
          for (var b = 0; b < spans.length; b++) {
            var started = Number(spans[b].dataset.start);
            if (isFinite(started) && lastTime >= started) activeIndex = b;
          }
        }
      }

      for (var i = 0; i < spans.length; i++) {
        var word = spans[i];
        var start = Number(word.dataset.start);
        var end = Number(word.dataset.end);
        var progress = 0;
        if (enabled && isFinite(start) && isFinite(end) && end > start) {
          // The wipe spans exactly the syllable's sung duration.
          progress = Math.max(0, Math.min(1, (lastTime - start) / (end - start)));
        }
        word.classList.toggle('is-sung', enabled && isFinite(end) && lastTime >= end);
        word.classList.toggle('is-active', enabled && isFinite(start) && isFinite(end) &&
          lastTime >= start && lastTime < end);
        word.style.setProperty('--karaoke-progress', Math.round(progress * 100) + '%');

        var distance = lineActive && activeIndex >= 0 ? Math.abs(i - activeIndex) : 99;
        var liftFactor = distance === 0 ? 1.0
          : distance === 1 ? 0.58
          : distance === 2 ? 0.24
          : 0;
        var lift = distance < 99 ? -maxLift * liftFactor : 0;
        word.style.setProperty('--karaoke-lift', lift.toFixed(2) + 'px');
      }
    }

    var pauses = container.querySelectorAll('.karaoke-pause');
    for (var p = 0; p < pauses.length; p++) {
      var pause = pauses[p];
      var pauseStart = Number(pause.dataset.start);
      var pauseEnd = Number(pause.dataset.end);
      var active = enabled && isFinite(pauseStart) && isFinite(pauseEnd) &&
        lastTime >= pauseStart && lastTime < pauseEnd;

      pause.hidden = !active;
      pause.classList.toggle('is-active', active);
      if (!active) continue;

      var duration = Math.max(0.001, pauseEnd - pauseStart);
      var pct = Math.max(0, Math.min(100, (lastTime - pauseStart) / duration * 100));
      var fill = pause.querySelector('.karaoke-pause-fill');
      var label = pause.querySelector('.karaoke-pause-time');
      if (fill) fill.style.width = pct + '%';
      if (label) label.textContent = formatTime(pauseEnd - lastTime);
    }
  }

  function updateAll() {
    if (!doc()) return;
    update(doc().getElementById('expanded-lyrics'), lastTime);
    update(doc().getElementById('mm-lyrics'), lastTime);
    update(doc().getElementById('presentation-lyrics'), lastTime);
  }

  global.SongRequestKaraoke = {
    setEnabled: setEnabled,
    isEnabled: isEnabled,
    renderLine: renderLine,
    attachPauses: attachPauses,
    chooseLineForTime: chooseLineForTime,
    nextLineForTime: nextLineForTime,
    update: update,
    updateAll: updateAll
  };
})(typeof window !== 'undefined' ? window : globalThis);
