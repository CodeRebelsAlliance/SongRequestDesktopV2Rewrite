// Shared playlist icon catalogue + picker for the desktop and mobile music UIs.
(function (global) {
  'use strict';

  var icons = [
    'fa-list', 'fa-list-ul', 'fa-music', 'fa-compact-disc', 'fa-headphones',
    'fa-guitar', 'fa-drum', 'fa-microphone', 'fa-star', 'fa-heart',
    'fa-fire', 'fa-bolt', 'fa-moon', 'fa-sun', 'fa-cloud',
    'fa-tree', 'fa-rocket', 'fa-wand-magic-sparkles', 'fa-cake-candles', 'fa-graduation-cap',
    'fa-gamepad', 'fa-car', 'fa-plane', 'fa-umbrella', 'fa-ghost',
    'fa-robot', 'fa-cat', 'fa-dog', 'fa-dragon', 'fa-pizza-slice',
    'fa-mug-hot', 'fa-champagne-glasses', 'fa-crown', 'fa-gem', 'fa-thumbs-up',
    'fa-circle-play', 'fa-clock', 'fa-calendar', 'fa-wand-magic', 'fa-dice'
  ];

  function normalize(name) {
    var value = String(name || '').trim();
    return /^fa-[a-z0-9-]+$/.test(value) ? value : 'fa-list';
  }

  function iconHtml(name) {
    return '<i class="fas ' + normalize(name) + '"></i>';
  }

  function renderPicker(container, selected, onSelect) {
    if (!container) return;
    container.innerHTML = '';
    var current = normalize(selected);
    icons.forEach(function (name) {
      var button = document.createElement('button');
      button.type = 'button';
      button.className = 'playlist-icon-choice' + (name === current ? ' is-selected' : '');
      button.setAttribute('data-icon', name);
      button.setAttribute('aria-label', name.replace('fa-', '').replace(/-/g, ' '));
      button.innerHTML = '<i class="fas ' + name + '"></i>';
      button.addEventListener('click', function () {
        container.querySelectorAll('.playlist-icon-choice').forEach(function (other) {
          other.classList.toggle('is-selected', other === button);
        });
        if (onSelect) onSelect(name);
      });
      container.appendChild(button);
    });
  }

  global.SongRequestPlaylistIcons = { icons: icons, normalize: normalize, iconHtml: iconHtml, renderPicker: renderPicker };
})(typeof window !== 'undefined' ? window : globalThis);
