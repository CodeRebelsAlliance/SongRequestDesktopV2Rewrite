// Mobile Requests page helpers.
//
// app.js still owns the data flow and every feature; this file turns the desktop
// markup into a touch/Material experience: cards open a bottom action sheet, and
// modal overlays become full-screen app-bar pages with a back button.
(function () {
  'use strict';

  function ready(fn) {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn, { once: true });
    else fn();
  }

  function esc(value) {
    return String(value == null ? '' : value)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  ready(function () {
    setupTabs();
    setupModalPages();
    setupActionSheet();
    enhanceCards();

    // app.js replaces list children on every refresh: enhance the new cards.
    document.querySelectorAll('.song-list').forEach(function (list) {
      new MutationObserver(enhanceCards).observe(list, { childList: true });
    });
  });

  // ── Chip tabs ────────────────────────────────────────────
  function setupTabs() {
    var tabbar = document.getElementById('tabbar');
    if (!tabbar) return;

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

    var main = document.getElementById('main');
    if (main) {
      main.querySelectorAll('.panel').forEach(function (panel) {
        panel.setAttribute('role', 'tabpanel');
      });
    }
  }

  // ── Modal overlays → full-screen pages ───────────────────
  function setupModalPages() {
    document.querySelectorAll('.modal-overlay').forEach(function (overlay) {
      var modal = overlay.querySelector('.modal');
      var header = overlay.querySelector('.modal-header');
      if (!modal || !header) return;

      var close = header.querySelector('.modal-close');
      if (!close) return;

      overlay.classList.add('mr-page-overlay');
      overlay.setAttribute('role', 'dialog');
      overlay.setAttribute('aria-modal', 'true');

      var back = document.createElement('button');
      back.className = 'mr-page-back';
      back.type = 'button';
      back.setAttribute('aria-label', 'Back');
      back.innerHTML = '<i class="fas fa-arrow-left"></i>';
      back.addEventListener('click', function () { close.click(); });
      header.insertBefore(back, header.firstChild);
    });
  }

  // ── Card action sheet ────────────────────────────────────
  var sheet, sheetScrim, sheetBody;
  var ICONS = {
    queueSong: 'fas fa-list',
    playNext: 'fas fa-forward-step',
    playNow: 'fas fa-play',
    approve: 'fas fa-check-circle',
    unapprove: 'fas fa-times-circle',
    delete: 'fas fa-trash-alt',
    blacklist: 'fas fa-ban',
    unblacklist: 'fas fa-rotate-left'
  };

  function setupActionSheet() {
    sheetScrim = document.createElement('div');
    sheetScrim.id = 'mr-sheet-scrim';
    sheetScrim.hidden = true;
    document.body.appendChild(sheetScrim);

    sheet = document.createElement('div');
    sheet.id = 'mr-sheet';
    sheet.hidden = true;
    sheet.innerHTML = '<div class="mr-sheet-handle"></div><div id="mr-sheet-body"></div>';
    document.body.appendChild(sheet);
    sheetBody = document.getElementById('mr-sheet-body');

    sheetScrim.addEventListener('click', closeSheet);
    sheetBody.addEventListener('click', function (event) {
      var action = event.target.closest('button[data-action]');
      if (!action) return;

      var ytid = sheetBody.dataset.ytid;
      var target = ytid
        ? document.querySelector('.song-card[data-ytid="' + cssEscape(ytid) + '"] button[data-action="' + action.dataset.action + '"]')
        : null;

      closeSheet();
      if (target) target.click();
    });

    // Tap a card (anywhere except its hidden action row) to open the sheet.
    document.addEventListener('click', function (event) {
      if (event.target.closest('#mr-sheet')) return;
      var card = event.target.closest('.song-card');
      if (!card) return;
      if (event.target.closest('button')) return;
      openCardSheet(card);
    });
  }

  function cssEscape(value) {
    if (window.CSS && typeof window.CSS.escape === 'function') return window.CSS.escape(value);
    return String(value).replace(/["\\]/g, '\\$&');
  }

  function openCardSheet(card) {
    var ytid = card.dataset.ytid || '';
    var title = card.querySelector('.stitle');
    var creator = card.querySelector('.screator');
    var status = card.querySelector('.sstatus');
    var actions = card.querySelectorAll('.sactions button');
    if (!actions.length) return;

    var html = '<div class="mr-sheet-title">' + esc(title ? title.textContent : 'Song') + '</div>' +
      (creator ? '<div class="mr-sheet-sub">' + esc(creator.textContent) + '</div>' : '');

    actions.forEach(function (button) {
      var action = button.dataset.action || '';
      var label = (button.textContent || '').replace(/\s+/g, ' ').trim();
      var icon = ICONS[action] || 'fas fa-circle';
      var danger = action === 'delete' || action === 'blacklist';
      html += '<button class="mr-sheet-action' + (danger ? ' is-danger' : '') + '" data-action="' + esc(action) + '">' +
        '<i class="' + icon + '"></i> ' + esc(label) + '</button>';
    });

    sheetBody.innerHTML = html;
    sheetBody.dataset.ytid = ytid;

    sheetScrim.hidden = false;
    sheet.hidden = false;
    requestAnimationFrame(function () {
      sheetScrim.classList.add('is-open');
      sheet.classList.add('is-open');
    });
  }

  function closeSheet() {
    if (!sheet || sheet.hidden) return;
    sheet.classList.remove('is-open');
    sheetScrim.classList.remove('is-open');
    setTimeout(function () {
      if (!sheet.classList.contains('is-open')) {
        sheet.hidden = true;
        sheetScrim.hidden = true;
      }
    }, 280);
  }

  // ── Card affordances ─────────────────────────────────────
  function enhanceCards() {
    document.querySelectorAll('.song-card').forEach(function (card) {
      if (card.dataset.mrEnhanced === '1') return;
      card.dataset.mrEnhanced = '1';
      card.setAttribute('role', 'button');
      card.setAttribute('tabindex', '0');

      var chevron = document.createElement('span');
      chevron.className = 'mr-card-chevron';
      chevron.innerHTML = '<i class="fas fa-chevron-right"></i>';
      card.appendChild(chevron);

      card.addEventListener('keydown', function (event) {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault();
          openCardSheet(card);
        }
      });
    });
  }
})();
