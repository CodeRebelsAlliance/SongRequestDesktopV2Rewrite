// Shared karaoke renderer: word-by-word lyric timing and animated long-pause
// bars. The host sends inferred word timings in the lyrics payload; this module
// only renders/animates them so desktop, mobile and presentation stay identical.
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

  /** Replace a lyric line's text with karaoke word spans (when data is present). */
  function renderLine(element, line) {
    if (!element || !line || !line.words || !line.words.length) return;
    element.innerHTML = '';
    for (var i = 0; i < line.words.length; i++) {
      var word = line.words[i];
      var span = doc().createElement('span');
      span.className = 'karaoke-word';
      span.dataset.start = word.start;
      span.dataset.end = word.end;
      span.textContent = word.text || '';
      element.appendChild(span);
      if (i < line.words.length - 1) element.appendChild(doc().createTextNode(' '));
    }
  }

  /** Insert pause elements into the lyrics flow. They stay hidden until reached. */
  function attachPauses(container, pauses) {
    if (!container) return;
    var old = container.querySelectorAll('.karaoke-pause');
    for (var i = 0; i < old.length; i++) old[i].remove();
    if (!pauses || !pauses.length) return;

    var lines = container.querySelectorAll('.lyrics-line[data-time], .mm-line[data-time]');
    for (var p = 0; p < pauses.length; p++) {
      var pause = pauses[p];
      var element = doc().createElement('div');
      element.className = 'karaoke-pause';
      element.hidden = true;
      element.dataset.start = pause.start;
      element.dataset.end = pause.end;
      element.innerHTML =
        '<div class="karaoke-pause-copy">' +
          '<span class="karaoke-pause-label"><i class="fas fa-hourglass-half"></i> Instrumental pause</span>' +
          '<span class="karaoke-pause-time">0:00</span>' +
        '</div>' +
        '<div class="karaoke-pause-track"><div class="karaoke-pause-fill"></div></div>';

      var target = null;
      for (var l = 0; l < lines.length; l++) {
        var time = Number(lines[l].dataset.time);
        if (isFinite(time) && time >= pause.end - 0.05) { target = lines[l]; break; }
      }

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

    var words = container.querySelectorAll('.karaoke-word');
    for (var i = 0; i < words.length; i++) {
      var word = words[i];
      var start = Number(word.dataset.start);
      var end = Number(word.dataset.end);
      var progress = 0;
      if (enabled && isFinite(start) && isFinite(end) && end > start) {
        progress = Math.max(0, Math.min(1, (lastTime - start) / (end - start)));
      }
      word.classList.toggle('is-sung', enabled && isFinite(end) && lastTime >= end);
      word.classList.toggle('is-active', enabled && isFinite(start) && isFinite(end) &&
        lastTime >= start && lastTime < end);
      word.style.setProperty('--karaoke-progress', Math.round(progress * 100) + '%');
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
    update: update,
    updateAll: updateAll
  };
})(typeof window !== 'undefined' ? window : globalThis);
