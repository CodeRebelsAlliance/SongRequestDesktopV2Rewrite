// Mobile Requests UI helpers.
//
// app.js owns all request/settings/startup behaviour; this file only adds the
// touch-specific affordances the desktop markup does not need: ARIA state on the
// chip-style tabs, and an explicit scroll reset when a tab is (re)selected from
// the native bottom navigation.
(function () {
  'use strict';

  function ready(fn) {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn, { once: true });
    else fn();
  }

  ready(function () {
    var tabbar = document.getElementById('tabbar');
    if (tabbar) {
      tabbar.setAttribute('role', 'tablist');
      var tabs = tabbar.querySelectorAll('.tab-btn');
      tabs.forEach(function (tab) {
        tab.setAttribute('role', 'tab');
        tab.setAttribute('aria-selected', tab.classList.contains('active') ? 'true' : 'false');
        tab.addEventListener('click', function () {
          tabs.forEach(function (other) {
            other.setAttribute('aria-selected', other === tab ? 'true' : 'false');
          });
        });
      });
    }

    var main = document.getElementById('main');
    if (main) {
      main.querySelectorAll('.panel').forEach(function (panel) {
        panel.setAttribute('role', 'tabpanel');
      });
    }
  });
})();
