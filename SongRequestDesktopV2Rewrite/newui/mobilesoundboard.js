// Mobile Soundboard helpers.
//
// soundboard.js owns the board, playback, MIDI and all dialogs. This file adds
// the touch affordances the desktop page does not need: accessibility labels for
// the icon-first header and horizontal swipes between pages.
(function () {
  'use strict';

  function ready(fn) {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn, { once: true });
    else fn();
  }

  ready(function () {
    // The phone header hides button text, so make sure every control still has a
    // spoken name (the page supplies `title` for most of them).
    document.querySelectorAll('#sb-top-actions .top-btn, #sb-midi-toggle').forEach(function (button) {
      var label = button.getAttribute('title') ||
        (button.textContent || '').replace(/\s+/g, ' ').trim();
      if (label) {
        button.setAttribute('aria-label', label);
        if (!button.getAttribute('title')) button.setAttribute('title', label);
      }
    });

    // Swipe left/right on the pad area to move between soundboard pages. Vertical
    // scrolling and taps are untouched: the gesture only fires for a clear
    // horizontal drag.
    var scroll = document.getElementById('sb-grid-scroll');
    if (scroll) {
      var startX = 0, startY = 0, tracking = false, suppressClick = false;

      scroll.addEventListener('touchstart', function (event) {
        if (event.touches.length !== 1) { tracking = false; return; }
        tracking = true;
        startX = event.touches[0].clientX;
        startY = event.touches[0].clientY;
      }, { passive: true });

      // A page swipe starts on a pad; stop the browser's synthesised click so the
      // gesture does not also fire that sound.
      scroll.addEventListener('click', function (event) {
        if (!suppressClick) return;
        suppressClick = false;
        event.preventDefault();
        event.stopPropagation();
      }, true);

      scroll.addEventListener('touchend', function (event) {
        if (!tracking) return;
        tracking = false;

        var touch = event.changedTouches && event.changedTouches[0];
        if (!touch) return;

        var dx = touch.clientX - startX;
        var dy = touch.clientY - startY;
        if (Math.abs(dx) < 64 || Math.abs(dx) < Math.abs(dy) * 1.6) return;

        var target = dx < 0 ? document.getElementById('sb-next') : document.getElementById('sb-prev');
        if (target && !target.disabled) {
          suppressClick = true;
          target.click();
          setTimeout(function () { suppressClick = false; }, 350);
        }
      }, { passive: true });
    }
  });
})();
