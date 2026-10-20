// Mobile Soundboard — a Material 3, page-based UI over the shared soundboard
// engine. All audio, persistence, MIDI and YouTube import still happen in the
// host; this page only renders state and sends the same commands as the desktop.
(function () {
  'use strict';

  function $(id) { return document.getElementById(id); }

  function esc(value) {
    return String(value == null ? '' : value)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function toast(message, type) {
    var host = $('msb-toasts');
    var el = document.createElement('div');
    el.className = 'msb-toast' + (type ? ' is-' + type : '');
    el.textContent = message;
    host.appendChild(el);
    setTimeout(function () { el.classList.add('is-out'); }, 2300);
    setTimeout(function () { if (el.parentNode) el.remove(); }, 2800);
  }

  function formatTime(seconds) {
    seconds = Math.max(0, Math.floor(seconds || 0));
    var m = Math.floor(seconds / 60);
    var s = seconds % 60;
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  // ── Host bridge ──────────────────────────────────────────
  var pending = {};
  var requestId = 0;

  function request(method, params, timeoutMs) {
    return new Promise(function (resolve, reject) {
      var id = ++requestId;
      var timer = setTimeout(function () {
        delete pending[id];
        reject(new Error('timeout: ' + method));
      }, timeoutMs || 30000);
      pending[id] = { resolve: resolve, timer: timer };

      var message = { type: 'request', id: id, method: method };
      if (params) for (var key in params) message[key] = params[key];

      if (!SongRequestHost.sendToHost(JSON.stringify(message))) {
        clearTimeout(timer);
        delete pending[id];
        reject(new Error('no host channel'));
      }
    });
  }

  function send(method, params) {
    var message = { type: 'request', id: 0, method: method };
    if (params) for (var key in params) message[key] = params[key];
    SongRequestHost.sendToHost(JSON.stringify(message));
  }

  SongRequestHost.registerHostMessageHandler(function (raw) {
    try {
      var message = JSON.parse(raw);

      if (message.type === 'response') {
        var entry = pending[message.id];
        if (entry) {
          clearTimeout(entry.timer);
          delete pending[message.id];
          if (message.result && message.result.error) entry.resolve(message.result);
          else entry.resolve(message.result);
        }
        return;
      }

      if (message.type === 'event') onEvent(message.eventName, message.data || {});
    } catch (error) { /* ignore malformed host messages */ }
  });

  // ── State ────────────────────────────────────────────────
  var state = null;
  var playback = { pageId: null, items: {} };
  var view = 'board';
  var pagesSelected = 0;
  var editing = null;
  var assignTarget = null;
  var assignDetected = null;
  var assignTimer = null;
  var keysEdit = null;
  var keyCaptureCallback = null;
  var keyCaptureGesture = '';
  var keyCaptureReturn = 'board';
  var lastStateSignature = '';
  var suppressClick = false;
  var suppressPadClick = false;

  var COLORS = ['#2A2A2A', '#FF5733', '#33FF57', '#3357FF', '#FF33F5', '#F5FF33', '#33FFF5', '#FF8C33', '#8C33FF', '#FF3383'];

  var views = {
    board: $('msb-view-board'),
    edit: $('msb-view-edit'),
    pages: $('msb-view-pages'),
    grid: $('msb-view-grid'),
    midi: $('msb-view-midi'),
    keys: $('msb-view-keys'),
    assign: $('msb-view-assign'),
    keycapture: $('msb-view-keycapture'),
  };

  function showView(name) {
    if (!views[name]) name = 'board';
    view = name;
    for (var key in views) views[key].hidden = key !== name;
    if (name === 'board') updateBoardScroll();
  }

  // ── State / rendering ────────────────────────────────────
  function refreshState(force) {
    request('soundboardGetState').then(function (data) {
      if (!data || data.error) { toast((data && data.error) || 'Could not load the soundboard', 'error'); return; }

      var signature = JSON.stringify(data);
      if (!force && signature === lastStateSignature) return;
      lastStateSignature = signature;
      state = data;
      renderAll();
    }).catch(function (error) { toast(error.message, 'error'); });
  }

  function renderAll() {
    if (!state) return;
    renderBoard();
    renderBottomBar();
    renderEditPage();
    renderPagesPage();
    renderGridPage();
    renderMidiPage();
    renderKeysPage();
    updateAssignBanner();
  }

  function currentPage() { return state && state.currentPage ? state.currentPage : null; }

  function renderBoard() {
    var page = currentPage();
    if (!page) return;

    $('msb-page-name').textContent = (state.currentPageIndex + 1) + ' of ' + state.pages.length + ' · ' + page.name;

    var grid = $('msb-grid');
    grid.style.gridTemplateColumns = 'repeat(' + page.columns + ', minmax(0, 1fr))';
    grid.style.gridTemplateRows = 'repeat(' + page.rows + ', minmax(76px, 1fr))';
    grid.innerHTML = '';

    var hasButtons = (state.buttons || []).length > 0;
    $('msb-empty').hidden = hasButtons;

    (state.buttons || []).forEach(function (button) {
      var pad = document.createElement('button');
      pad.className = 'msb-pad' + (button.isEmpty ? ' empty' : '') + (state.isAssignMode ? ' assign' : '');
      pad.dataset.row = button.row;
      pad.dataset.col = button.col;
      pad.dataset.empty = button.isEmpty ? '1' : '0';
      pad.dataset.index = button.index;
      pad.style.setProperty('--btn-color', button.color || '#2a2a2a');

      var midi = button.midi && button.midi.channel !== undefined;
      var status = button.isEmpty ? '' : (midi ? 'MIDI ' + button.midi.note : 'Ready');
      pad.innerHTML =
        (midi ? '<span class="pad-midi"><i class="fas fa-sliders"></i></span>' : '') +
        '<span class="pad-name">' + esc(button.isEmpty ? 'Empty ' + (button.index + 1) : button.name) + '</span>' +
        '<span class="pad-icon">' + esc(button.icon || '🔊') + '</span>' +
        '<span class="pad-status">' + esc(status) + '</span>' +
        '<span class="pad-progress"></span>';

      pad.addEventListener('click', function () {
        if (suppressPadClick) { suppressPadClick = false; return; }
        onPadTap(button);
      });
      pad.addEventListener('contextmenu', function (event) {
        event.preventDefault();
        suppressPadClick = true;
        setTimeout(function () { suppressPadClick = false; }, 450);
        openPadSheet(button);
      });
      grid.appendChild(pad);
    });

    applyPlayback();
  }

  function renderBottomBar() {
    var slider = $('msb-master');
    slider.value = String(Math.round((state.masterVolume || 0) * 300));
    $('msb-master-label').textContent = Math.round(slider.value) + '%';

    $('msb-prev-page').disabled = state.currentPageIndex <= 0;
    $('msb-next-page').disabled = state.currentPageIndex >= state.pages.length - 1;

    var device = $('msb-device');
    device.innerHTML = '';
    (state.audioDevices || []).forEach(function (entry) {
      var option = document.createElement('option');
      option.value = entry.number;
      option.textContent = entry.name;
      device.appendChild(option);
    });
    device.value = String(state.outputDeviceNumber);
  }

  function updateBoardScroll() {
    // Fit the grid to the available height; the CSS rows use minmax, so a very
    // large page still scrolls instead of squeezing pads to nothing.
    var scroll = $('msb-grid-scroll');
    if (scroll) scroll.scrollTop = 0;
  }

  function updateAssignBanner() {
    $('msb-assign-banner').hidden = !(state && state.isAssignMode);
    var button = $('msb-midi-assign-mode');
    if (button && state) {
      button.innerHTML = state.isAssignMode
        ? '<i class="fas fa-times"></i> Stop assign mode'
        : '<i class="fas fa-bullseye"></i> Start assign mode';
    }
  }

  // ── Playback ─────────────────────────────────────────────
  function onPlayback(data) {
    if (!data) return;
    var items = {};
    (data.items || []).forEach(function (item) { items[item.row + '_' + item.col] = item; });
    playback = { pageId: data.pageId, items: items };
    applyPlayback();
  }

  function applyPlayback() {
    var page = currentPage();
    var active = page && playback.pageId === page.id ? playback.items : {};

    document.querySelectorAll('.msb-pad').forEach(function (pad) {
      var item = active[pad.dataset.row + '_' + pad.dataset.col];
      var progress = pad.querySelector('.pad-progress');
      var status = pad.querySelector('.pad-status');
      if (item) {
        pad.classList.add('playing');
        progress.style.width = Math.min(item.progress, 1) * 100 + '%';
        if (status) status.textContent = formatTime(item.elapsed) + ' / ' + formatTime(item.total);
      } else {
        pad.classList.remove('playing');
        progress.style.width = '0%';
        if (status && pad.dataset.empty !== '1') {
          var button = (state && state.buttons || []).find(function (b) {
            return String(b.row) === pad.dataset.row && String(b.col) === pad.dataset.col;
          });
          var midi = button && button.midi && button.midi.channel !== undefined;
          status.textContent = midi ? 'MIDI ' + button.midi.note : 'Ready';
        }
      }
    });
  }

  function onPadTap(button) {
    if (state && state.isAssignMode) {
      openAssign(button.row, button.col);
      return;
    }

    if (button.isEmpty) {
      openEdit(button);
      return;
    }

    var pad = document.querySelector('.msb-pad[data-row="' + button.row + '"][data-col="' + button.col + '"]');
    if (pad) {
      pad.classList.add('playing');
      setTimeout(function () { pad.classList.remove('playing'); }, 140);
    }
    request('soundboardPlay', { row: button.row, col: button.col })
      .then(function (result) { if (result && result.error) toast(result.error, 'error'); })
      .catch(function (error) { toast(error.message, 'error'); });
  }

  // ── Action sheet ─────────────────────────────────────────
  var sheet = $('msb-sheet');
  var sheetScrim = $('msb-sheet-scrim');
  var sheetBody = $('msb-sheet-body');
  var pendingConfirm = null;

  function openSheet(html) {
    sheetBody.innerHTML = html;
    sheetScrim.hidden = false;
    sheet.hidden = false;
    requestAnimationFrame(function () {
      sheet.classList.add('is-open');
      sheetScrim.classList.add('is-open');
    });
  }

  // Android's HybridWebView does not reliably return from window.confirm(), so
  // destructive actions get an in-app confirmation sheet.
  function openConfirmSheet(title, message, confirmLabel, callback) {
    pendingConfirm = callback;
    openSheet(
      '<div class="msb-sheet-title">' + esc(title) + '</div>' +
      '<div class="msb-confirm-text">' + esc(message) + '</div>' +
      '<div class="msb-confirm-actions">' +
        '<button id="msb-confirm-cancel" class="msb-btn">Cancel</button>' +
        '<button id="msb-confirm-ok" class="msb-btn msb-btn-danger">' + esc(confirmLabel) + '</button>' +
      '</div>'
    );
  }

  function closeSheet() {
    sheet.classList.remove('is-open');
    sheetScrim.classList.remove('is-open');
    setTimeout(function () {
      if (!sheet.classList.contains('is-open')) {
        sheet.hidden = true;
        sheetScrim.hidden = true;
      }
    }, 280);
  }

  sheetScrim.addEventListener('click', closeSheet);

  function openPadSheet(button) {
    var mapped = button.midi && button.midi.channel !== undefined;
    openSheet(
      '<div class="msb-sheet-title">' + esc(button.isEmpty ? 'Empty pad' : button.name) + '</div>' +
      '<button class="msb-sheet-action" data-pad-action="edit"><i class="fas fa-pen"></i> ' +
        (button.isEmpty ? 'Add sound' : 'Edit sound') + '</button>' +
      (mapped ? '<button class="msb-sheet-action" data-pad-action="clear-midi"><i class="fas fa-eraser"></i> Clear MIDI mapping</button>' : '') +
      (button.isEmpty ? '' : '<button class="msb-sheet-action is-danger" data-pad-action="delete"><i class="fas fa-trash-alt"></i> Delete sound</button>')
    );
    sheetBody.dataset.row = button.row;
    sheetBody.dataset.col = button.col;
  }

  sheetBody.addEventListener('click', function (event) {
    var confirmOk = event.target.closest('#msb-confirm-ok');
    if (confirmOk) {
      var callback = pendingConfirm;
      pendingConfirm = null;
      closeSheet();
      if (callback) callback();
      return;
    }
    if (event.target.closest('#msb-confirm-cancel')) {
      pendingConfirm = null;
      closeSheet();
      return;
    }

    var action = event.target.closest('button[data-pad-action]');
    if (!action) return;

    var row = parseInt(sheetBody.dataset.row, 10);
    var col = parseInt(sheetBody.dataset.col, 10);
    var button = findButton(row, col);
    closeSheet();
    if (!button) return;

    switch (action.dataset.padAction) {
      case 'edit':
        openEdit(button);
        break;
      case 'clear-midi':
        openConfirmSheet('Clear MIDI mapping',
          'Clear the MIDI mapping for “' + (button.name || 'this pad') + '”?', 'Clear',
          function () {
            request('soundboardClearMidi', { row: row, col: col }).then(function () {
              toast('MIDI mapping cleared', 'success');
              refreshState(true);
            }).catch(function (error) { toast(error.message, 'error'); });
          });
        break;
      case 'delete':
        openConfirmSheet('Delete sound',
          'Delete “' + (button.name || 'this sound') + '”?', 'Delete',
          function () {
            request('soundboardDeleteButton', { row: row, col: col }).then(function (result) {
              if (result && result.error) { toast(result.error, 'error'); return; }
              toast('Sound deleted', 'success');
              refreshState(true);
            }).catch(function (error) { toast(error.message, 'error'); });
          });
        break;
    }
  });

  function findButton(row, col) {
    return (state && state.buttons || []).find(function (button) {
      return button.row === row && button.col === col;
    }) || null;
  }

  // ── Edit sound page ──────────────────────────────────────
  function openEdit(button) {
    editing = {
      row: button.row,
      col: button.col,
      soundFile: button.soundFile || '',
      color: (button.color || '#2A2A2A').toUpperCase(),
      shortcut: {
        enabled: !!(button.shortcut && button.shortcut.enabled),
        gesture: (button.shortcut && button.shortcut.gesture) || ''
      }
    };

    $('msb-edit-title').textContent = button.isEmpty ? 'Add sound' : 'Edit sound';
    $('msb-edit-name').value = button.isEmpty ? '' : (button.name || '');
    $('msb-edit-icon').value = button.icon || '🔊';
    $('msb-edit-soundfile').value = button.soundFile || '';
    $('msb-edit-fadein').checked = !!button.fadeIn;
    $('msb-edit-fadeout').checked = !!button.fadeOut;
    $('msb-edit-repeat').value = button.repeatMode || 'none';

    var volume = $('msb-edit-volume');
    volume.value = String(Math.round((button.volume != null ? button.volume : 1) * 300));
    $('msb-edit-volume-label').textContent = Math.round(volume.value) + '%';
    $('msb-edit-yturl').value = '';
    $('msb-edit-import-status').textContent = '';
    $('msb-edit-delete').hidden = !!button.isEmpty;

    renderColorPalette();
    renderEditMidi(button);
    renderEditDevices(button.customOutputDeviceNumber);
    renderEditShortcut();
    showView('edit');
  }

  function renderColorPalette() {
    var wrap = $('msb-edit-colors');
    wrap.innerHTML = '';
    COLORS.forEach(function (color) {
      var swatch = document.createElement('button');
      swatch.className = 'msb-color-swatch' + (color.toUpperCase() === editing.color ? ' selected' : '');
      swatch.style.background = color;
      swatch.dataset.color = color.toUpperCase();
      swatch.setAttribute('aria-label', 'Colour ' + color);
      swatch.addEventListener('click', function () {
        editing.color = color.toUpperCase();
        renderColorPalette();
        $('msb-edit-custom-color').value = '';
        updateColorPreview();
      });
      wrap.appendChild(swatch);
    });

    $('msb-edit-custom-color').value = COLORS.indexOf(editing.color) >= 0 ? '' : editing.color;
    updateColorPreview();
  }

  function updateColorPreview() {
    $('msb-edit-color-preview').style.background = editing.color || '#202020';
  }

  $('msb-edit-custom-color').addEventListener('input', function () {
    var value = this.value.trim().toUpperCase();
    if (/^#[0-9A-F]{6}$/.test(value)) {
      editing.color = value;
      document.querySelectorAll('#msb-edit-colors .msb-color-swatch').forEach(function (swatch) {
        swatch.classList.remove('selected');
      });
      updateColorPreview();
    }
  });

  function renderEditMidi(button) {
    var element = $('msb-edit-midi');
    if (button.midi && button.midi.channel !== undefined) {
      element.className = 'msb-midi-status mapped';
      element.textContent = '✓ Channel ' + button.midi.channel + ', ' + button.midi.messageType + ' ' +
        button.midi.note + '\nPressed ' + button.midi.velocityPressed + ' · Unpressed ' + button.midi.velocityUnpressed;
    } else {
      element.className = 'msb-midi-status';
      element.textContent = 'Not mapped. Use Assign to map a MIDI control.';
    }
  }

  function renderEditDevices(selected) {
    var select = $('msb-edit-device');
    select.innerHTML = '<option value="">Use global device</option>';
    (state.audioDevices || []).forEach(function (device) {
      if (device.number < 0) return;
      var option = document.createElement('option');
      option.value = device.number;
      option.textContent = device.name;
      select.appendChild(option);
    });
    select.value = selected === null || selected === undefined ? '' : String(selected);
  }

  function renderEditShortcut() {
    $('msb-edit-shortcut-enabled').checked = !!(editing && editing.shortcut.enabled);
    $('msb-edit-shortcut').textContent = (editing && editing.shortcut.gesture) || '(not set)';
  }

  $('msb-edit-volume').addEventListener('input', function () {
    $('msb-edit-volume-label').textContent = Math.round(this.value) + '%';
  });

  $('msb-edit-browse').addEventListener('click', function () {
    if (!editing) return;
    var status = $('msb-edit-import-status');
    status.textContent = 'Opening the file picker…';
    request('soundboardBrowseFile', { row: editing.row, col: editing.col }, 120000)
      .then(function (result) {
        if (!result || result.cancelled) { status.textContent = ''; return; }
        if (result.error) { status.textContent = result.error; toast(result.error, 'error'); return; }

        editing.soundFile = result.fileName;
        $('msb-edit-soundfile').value = result.fileName;
        if (!$('msb-edit-name').value.trim()) {
          $('msb-edit-name').value = result.name || String(result.fileName).replace(/\.[^.]+$/, '');
        }
        status.textContent = 'Selected: ' + result.fileName;
        toast('Sound selected', 'success');
      }).catch(function (error) { status.textContent = error.message; toast(error.message, 'error'); });
  });

  $('msb-edit-ytimport').addEventListener('click', function () {
    if (!editing) return;
    var url = $('msb-edit-yturl').value.trim();
    if (!url) { toast('Enter a YouTube URL', 'error'); return; }

    var status = $('msb-edit-import-status');
    status.textContent = 'Downloading…';
    request('soundboardImportYoutube', { row: editing.row, col: editing.col, url: url }, 600000)
      .then(function (result) {
        if (!result || result.error) {
          status.textContent = (result && result.error) || 'Import failed';
          toast(status.textContent, 'error');
          return;
        }
        editing.soundFile = result.fileName;
        $('msb-edit-soundfile').value = result.fileName;
        if (!$('msb-edit-name').value.trim() && result.name) $('msb-edit-name').value = result.name;
        status.textContent = 'Imported: ' + result.fileName;
        toast('YouTube sound imported', 'success');
      }).catch(function (error) { status.textContent = error.message; toast(error.message, 'error'); });
  });

  $('msb-edit-midi-clear').addEventListener('click', function () {
    if (!editing) return;
    openConfirmSheet('Clear MIDI mapping', 'Clear the MIDI mapping for this pad?', 'Clear', function () {
      if (!editing) return;
      request('soundboardClearMidi', { row: editing.row, col: editing.col }).then(function () {
        toast('MIDI mapping cleared', 'success');
        var row = editing.row, col = editing.col;
        refreshState(true);
        renderEditMidi(findButton(row, col) || {});
      }).catch(function (error) { toast(error.message, 'error'); });
    });
  });

  $('msb-edit-midi-assign').addEventListener('click', function () {
    if (!editing) return;
    if (!state.midiEnabled) { toast('Enable MIDI first.', 'error'); return; }
    openAssign(editing.row, editing.col);
  });

  $('msb-edit-shortcut-set').addEventListener('click', function () {
    if (!editing) return;
    openKeyCapture(function (gesture) {
      editing.shortcut.gesture = gesture;
      editing.shortcut.enabled = true;
      renderEditShortcut();
    });
  });

  $('msb-edit-shortcut-clear').addEventListener('click', function () {
    if (!editing) return;
    editing.shortcut.gesture = '';
    editing.shortcut.enabled = false;
    renderEditShortcut();
  });

  $('msb-edit-save').addEventListener('click', function () {
    if (!editing) return;

    var name = $('msb-edit-name').value.trim();
    var soundFile = $('msb-edit-soundfile').value.trim();
    if (!name) { toast('Enter a button name.', 'error'); return; }
    if (!soundFile) { toast('Select a sound file.', 'error'); return; }

    var device = $('msb-edit-device').value;
    var payload = {
      row: editing.row,
      col: editing.col,
      name: name,
      icon: $('msb-edit-icon').value,
      color: editing.color,
      soundFile: soundFile,
      fadeIn: $('msb-edit-fadein').checked,
      fadeOut: $('msb-edit-fadeout').checked,
      repeatMode: $('msb-edit-repeat').value,
      volume: parseFloat($('msb-edit-volume').value) / 300,
      customOutputDeviceNumber: device === '' ? null : parseInt(device, 10),
      shortcutEnabled: $('msb-edit-shortcut-enabled').checked,
      shortcutGesture: editing.shortcut.gesture
    };

    request('soundboardUpdateButton', payload).then(function (result) {
      if (result && result.error) { toast(result.error, 'error'); return; }
      toast('Sound saved', 'success');
      editing = null;
      showView('board');
      refreshState(true);
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-edit-delete').addEventListener('click', function () {
    if (!editing) return;
    var target = { row: editing.row, col: editing.col };
    openConfirmSheet('Delete sound', 'Delete this sound?', 'Delete', function () {
      request('soundboardDeleteButton', target).then(function (result) {
        if (result && result.error) { toast(result.error, 'error'); return; }
        toast('Sound deleted', 'success');
        editing = null;
        showView('board');
        refreshState(true);
      }).catch(function (error) { toast(error.message, 'error'); });
    });
  });

  $('msb-edit-back').addEventListener('click', function () {
    editing = null;
    showView('board');
  });

  function renderEditPage() {
    if (!editing) return;
    var button = findButton(editing.row, editing.col);
    if (button) renderEditMidi(button);
  }

  // ── Pages ────────────────────────────────────────────────
  function openPages() {
    pagesSelected = state.currentPageIndex;
    renderPagesPage();
    $('msb-page-addform').hidden = true;
    showView('pages');
  }

  function renderPagesPage() {
    if (!state) return;
    var list = $('msb-pages-list');
    list.innerHTML = '';
    state.pages.forEach(function (page, index) {
      var row = document.createElement('button');
      row.className = 'msb-list-row' + (index === pagesSelected ? ' selected' : '');
      row.innerHTML =
        '<div class="row-main"><div class="row-title">' + esc(page.name) + '</div>' +
        '<div class="row-sub">' + page.columns + ' × ' + page.rows + (index === state.currentPageIndex ? ' · current' : '') + '</div></div>' +
        (index === pagesSelected ? '<i class="fas fa-check"></i>' : '');
      row.addEventListener('click', function () {
        pagesSelected = index;
        renderPagesPage();
      });
      list.appendChild(row);
    });

    $('msb-page-delete').disabled = state.pages.length <= 1;
  }

  $('msb-pages-btn').addEventListener('click', openPages);
  $('msb-pages-back').addEventListener('click', function () { showView('board'); });

  $('msb-pages-add').addEventListener('click', function () {
    var form = $('msb-page-addform');
    form.hidden = !form.hidden;
    if (!form.hidden) {
      $('msb-page-addname').value = 'Page ' + new Date().toTimeString().slice(0, 5).replace(':', '');
      fillRange('msb-page-addcols', 1, 12, 4);
      fillRange('msb-page-addrows', 1, 12, 3);
      $('msb-page-addname').focus();
    }
  });

  $('msb-page-addcancel').addEventListener('click', function () { $('msb-page-addform').hidden = true; });

  $('msb-page-addconfirm').addEventListener('click', function () {
    var name = $('msb-page-addname').value.trim() || 'Untitled page';
    var columns = parseInt($('msb-page-addcols').value, 10);
    var rows = parseInt($('msb-page-addrows').value, 10);
    request('soundboardAddPage', { name: name, columns: columns, rows: rows }).then(function () {
      toast('Page added', 'success');
      $('msb-page-addform').hidden = true;
      pagesSelected = state.pages.length;
      refreshState(true);
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-page-jump').addEventListener('click', function () {
    if (!state.pages[pagesSelected]) return;
    request('soundboardSetPage', { index: pagesSelected }).then(function () {
      toast('Jumped to page ' + (pagesSelected + 1), 'success');
      showView('board');
      refreshState(true);
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-page-rename').addEventListener('click', function () {
    var page = state.pages[pagesSelected];
    if (!page) return;
    openSheet(
      '<div class="msb-sheet-title">Rename page</div>' +
      '<input id="msb-page-rename-input" class="msb-field" style="width:100%;min-height:46px;padding:10px 12px;border-radius:12px;background:var(--msb-surface);border:1px solid var(--msb-outline);font-size:16px" value="' + esc(page.name) + '">' +
      '<button class="msb-btn msb-primary wide" id="msb-page-rename-save" style="margin-top:10px">Save name</button>'
    );
  });

  sheetBody.addEventListener('click', function (event) {
    if (!event.target.closest('#msb-page-rename-save')) return;
    var input = $('msb-page-rename-input');
    var name = input ? input.value.trim() : '';
    if (!name) return;
    request('soundboardRenamePage', { index: pagesSelected, name: name }).then(function () {
      toast('Page renamed', 'success');
      closeSheet();
      refreshState(true);
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-page-delete').addEventListener('click', function () {
    var page = state.pages[pagesSelected];
    if (!page || state.pages.length <= 1) return;
    var index = pagesSelected;
    openConfirmSheet('Delete page', 'Delete page “' + page.name + '”?', 'Delete', function () {
      request('soundboardDeletePage', { index: index }).then(function (result) {
        if (result && result.error) { toast(result.error, 'error'); return; }
        toast('Page deleted', 'success');
        pagesSelected = Math.max(0, index - 1);
        refreshState(true);
      }).catch(function (error) { toast(error.message, 'error'); });
    });
  });

  $('msb-page-up').addEventListener('click', function () {
    if (pagesSelected <= 0) return;
    var from = pagesSelected;
    request('soundboardMovePage', { index: from, delta: -1 }).then(function () {
      pagesSelected = from - 1;
      refreshState(true);
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-page-down').addEventListener('click', function () {
    if (!state || pagesSelected >= state.pages.length - 1) return;
    var from = pagesSelected;
    request('soundboardMovePage', { index: from, delta: 1 }).then(function () {
      pagesSelected = from + 1;
      refreshState(true);
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  // ── Grid size ────────────────────────────────────────────
  $('msb-grid-btn').addEventListener('click', function () {
    fillRange('msb-grid-cols', 1, 12, currentPage().columns);
    fillRange('msb-grid-rows', 1, 12, currentPage().rows);
    showView('grid');
  });

  $('msb-grid-back').addEventListener('click', function () { showView('board'); });

  $('msb-grid-save').addEventListener('click', function () {
    request('soundboardSetGridSize', {
      columns: parseInt($('msb-grid-cols').value, 10),
      rows: parseInt($('msb-grid-rows').value, 10)
    }).then(function () {
      toast('Grid updated', 'success');
      showView('board');
      refreshState(true);
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  function renderGridPage() {
    // Values are filled when the page opens (state may have changed meanwhile).
  }

  // ── MIDI ─────────────────────────────────────────────────
  $('msb-midi-btn').addEventListener('click', function () {
    renderMidiPage();
    showView('midi');
  });

  $('msb-midi-back').addEventListener('click', function () { showView('board'); });

  function renderMidiPage() {
    if (!state) return;
    $('msb-midi-enabled').checked = !!state.midiEnabled;
    fillMidiSelect('msb-midi-input', state.midiInputs, state.midiInputDevice);
    fillMidiSelect('msb-midi-output', state.midiOutputs, state.midiOutputDevice);
    var velocity = state.emptyFeedbackVelocity != null ? state.emptyFeedbackVelocity : 10;
    $('msb-midi-vel').value = velocity;
    $('msb-midi-vel-label').textContent = velocity;
    updateMidiStatus();
    updateAssignBanner();
  }

  function fillMidiSelect(id, devices, selected) {
    var select = $(id);
    select.innerHTML = '<option value="-1">None</option>';
    (devices || []).forEach(function (device) {
      var option = document.createElement('option');
      option.value = device.number;
      option.textContent = device.name || ('Device ' + device.number);
      select.appendChild(option);
    });
    select.value = String(selected == null ? -1 : selected);
  }

  function updateMidiStatus() {
    var enabled = $('msb-midi-enabled').checked;
    var input = parseInt($('msb-midi-input').value, 10);
    var output = parseInt($('msb-midi-output').value, 10);
    var text;
    if (!enabled) text = 'MIDI is disabled.';
    else if (input >= 0 && output >= 0) text = 'Input and output connected.';
    else if (input >= 0) text = 'Input connected (no feedback output).';
    else if (output >= 0) text = 'Output connected (no input).';
    else text = 'No MIDI devices selected.';
    $('msb-midi-status').textContent = text;
  }

  ['msb-midi-input', 'msb-midi-output'].forEach(function (id) {
    $(id).addEventListener('change', function () {
      updateMidiStatus();
      request('soundboardSetMidiDevices', {
        inputDevice: parseInt($('msb-midi-input').value, 10),
        outputDevice: parseInt($('msb-midi-output').value, 10)
      }).then(function () { refreshState(true); }).catch(function (error) { toast(error.message, 'error'); });
    });
  });

  $('msb-midi-enabled').addEventListener('change', function () {
    updateMidiStatus();
    request('soundboardSetMidiEnabled', { enabled: this.checked })
      .then(function () { refreshState(true); })
      .catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-midi-vel').addEventListener('input', function () {
    $('msb-midi-vel-label').textContent = this.value;
  });

  $('msb-midi-vel').addEventListener('change', function () {
    request('soundboardSetEmptyFeedback', { velocity: parseInt(this.value, 10) })
      .catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-midi-test').addEventListener('click', function () {
    request('soundboardTestMidiFeedback').then(function (result) {
      if (result && result.error) toast(result.error, 'error');
      else toast('Test feedback sent', 'info');
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-midi-assign-mode').addEventListener('click', function () {
    var enable = !(state && state.isAssignMode);
    if (enable && !state.midiEnabled) { toast('Enable MIDI first.', 'error'); return; }
    request('soundboardSetAssignMode', { enabled: enable }).then(function () {
      refreshState(true);
      showView('board');
      if (enable) toast('Tap a pad to assign a control', 'info');
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-assign-cancel-banner').addEventListener('click', function () {
    request('soundboardSetAssignMode', { enabled: false }).then(function () {
      refreshState(true);
    }).catch(function () { });
  });

  // ── MIDI assign page ─────────────────────────────────────
  function openAssign(row, col) {
    if (!state || !state.midiEnabled) { toast('Enable MIDI first.', 'error'); return; }
    if (state.midiInputDevice == null || state.midiInputDevice < 0) {
      toast('Select a MIDI input device first.', 'error');
      renderMidiPage();
      showView('midi');
      return;
    }

    assignTarget = { row: row, col: col };
    assignDetected = null;
    $('msb-assign-status').className = 'msb-assign-status';
    $('msb-assign-status').textContent = 'Waiting for MIDI input…\n\nPress a control on your MIDI device.';
    $('msb-assign-ok').disabled = true;
    $('msb-assign-vp').value = 127;
    $('msb-assign-vu').value = 0;
    $('msb-assign-vp-label').textContent = '127';
    $('msb-assign-vu-label').textContent = '0';
    showView('assign');

    request('soundboardStartMidiAssign').catch(function () { });
    if (assignTimer) clearTimeout(assignTimer);
    assignTimer = setTimeout(function () {
      if (!assignDetected) {
        $('msb-assign-status').className = 'msb-assign-status';
        $('msb-assign-status').textContent = 'No MIDI input detected.\n\nCheck the MIDI input device, then try again.';
      }
    }, 15000);
  }

  $('msb-assign-back').addEventListener('click', closeAssign);
  $('msb-assign-ok').addEventListener('click', function () {
    if (!assignTarget || !assignDetected) return;
    var mapping = {
      channel: assignDetected.channel,
      note: assignDetected.value1,
      messageType: assignDetected.messageType,
      velocityPressed: parseInt($('msb-assign-vp').value, 10),
      velocityUnpressed: parseInt($('msb-assign-vu').value, 10)
    };
    var target = assignTarget;
    request('soundboardSetButtonMidi', { row: target.row, col: target.col, mapping: mapping }).then(function () {
      toast('MIDI mapping assigned', 'success');
      closeAssign();
      refreshState(true);
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  function closeAssign() {
    if (assignTimer) { clearTimeout(assignTimer); assignTimer = null; }
    assignTarget = null;
    assignDetected = null;
    request('soundboardSetAssignMode', { enabled: false }).catch(function () { });
    if (state) state.isAssignMode = false;
    updateAssignBanner();
    showView('board');
  }

  $('msb-assign-vp').addEventListener('input', function () { $('msb-assign-vp-label').textContent = this.value; });
  $('msb-assign-vu').addEventListener('input', function () { $('msb-assign-vu-label').textContent = this.value; });

  // ── Keyboard shortcuts ───────────────────────────────────
  var KEY_LABELS = [
    { id: 'stopAll', label: 'Stop all sounds' },
    { id: 'volumeUp', label: 'Master volume up' },
    { id: 'volumeDown', label: 'Master volume down' },
    { id: 'nextPage', label: 'Next page' },
    { id: 'previousPage', label: 'Previous page' }
  ];

  $('msb-keys-btn').addEventListener('click', function () {
    keysEdit = {};
    KEY_LABELS.forEach(function (key) {
      var shortcut = (state.keyboardShortcuts || {})[key.id] || {};
      keysEdit[key.id] = { enabled: !!shortcut.enabled, gesture: shortcut.gesture || '' };
    });
    renderKeysPage();
    showView('keys');
  });

  $('msb-keys-back').addEventListener('click', function () { keysEdit = null; showView('board'); });

  function renderKeysPage() {
    if (!keysEdit) return;
    var list = $('msb-keys-list');
    list.innerHTML = '';
    KEY_LABELS.forEach(function (key) {
      var row = document.createElement('div');
      row.className = 'msb-list-row';
      row.innerHTML =
        '<div class="row-main"><div class="row-title">' + esc(key.label) + '</div>' +
        '<div class="row-sub">' + esc(keysEdit[key.id].gesture || '(not set)') + '</div></div>' +
        '<input type="checkbox" data-key-enabled="' + key.id + '"' + (keysEdit[key.id].enabled ? ' checked' : '') + '>' +
        '<button class="msb-icon-btn" data-key-set="' + key.id + '" aria-label="Set"><i class="fas fa-keyboard"></i></button>' +
        '<button class="msb-icon-btn" data-key-clear="' + key.id + '" aria-label="Clear"><i class="fas fa-eraser"></i></button>';
      list.appendChild(row);
    });
  }

  $('msb-keys-list').addEventListener('click', function (event) {
    var set = event.target.closest('button[data-key-set]');
    if (set) {
      var id = set.dataset.keySet;
      openKeyCapture(function (gesture) {
        keysEdit[id].gesture = gesture;
        keysEdit[id].enabled = true;
        renderKeysPage();
      });
      return;
    }

    var clear = event.target.closest('button[data-key-clear]');
    if (clear) {
      var clearId = clear.dataset.keyClear;
      keysEdit[clearId].gesture = '';
      keysEdit[clearId].enabled = false;
      renderKeysPage();
    }
  });

  $('msb-keys-list').addEventListener('change', function (event) {
    var id = event.target.dataset.keyEnabled;
    if (!id) return;
    keysEdit[id].enabled = event.target.checked;
  });

  $('msb-keys-save').addEventListener('click', function () {
    if (!keysEdit) return;
    request('soundboardSaveKeyboardShortcuts', { shortcuts: keysEdit }).then(function () {
      toast('Shortcuts saved', 'success');
      keysEdit = null;
      showView('board');
      refreshState(true);
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  // ── Key capture page ─────────────────────────────────────
  function openKeyCapture(callback) {
    keyCaptureCallback = callback;
    keyCaptureGesture = '';
    keyCaptureReturn = view === 'keys' ? 'keys' : 'edit';
    $('msb-keycapture-value').textContent = 'No shortcut detected';
    showView('keycapture');
    document.addEventListener('keydown', onCaptureKeyDown, true);
  }

  function closeKeyCapture() {
    document.removeEventListener('keydown', onCaptureKeyDown, true);
    keyCaptureCallback = null;
    keyCaptureGesture = '';
  }

  function onCaptureKeyDown(event) {
    var gesture = window.SRKeys ? window.SRKeys.gestureFromEvent(event) : null;
    if (!gesture) return;
    event.preventDefault();
    event.stopPropagation();
    keyCaptureGesture = gesture;
    $('msb-keycapture-value').textContent = gesture;
  }

  $('msb-keycapture-back').addEventListener('click', function () {
    closeKeyCapture();
    showView(keyCaptureReturn);
  });

  $('msb-keycapture-apply').addEventListener('click', function () {
    if (!keyCaptureGesture) { toast('Press a key combination first.', 'error'); return; }
    var callback = keyCaptureCallback;
    var returnView = keyCaptureReturn;
    closeKeyCapture();
    showView(returnView);
    if (callback) callback(keyCaptureGesture);
  });

  // ── Board / page actions ─────────────────────────────────
  $('msb-prev-page').addEventListener('click', function () {
    request('soundboardStepPage', { delta: -1 }).then(function () { refreshState(true); })
      .catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-next-page').addEventListener('click', function () {
    request('soundboardStepPage', { delta: 1 }).then(function () { refreshState(true); })
      .catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-page-name').addEventListener('click', openPages);

  $('msb-add-btn').addEventListener('click', addSound);
  $('msb-empty-add').addEventListener('click', addSound);

  function addSound() {
    if (!state || !state.buttons) return;
    var empty = state.buttons.find(function (button) { return button.isEmpty; });
    if (!empty) {
      toast('No empty pads on this page. Resize the grid or delete a sound.', 'info');
      return;
    }
    openEdit(empty);
  }

  $('msb-stop-all').addEventListener('click', function () {
    request('soundboardStopAll').catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-master').addEventListener('input', function () {
    $('msb-master-label').textContent = Math.round(this.value) + '%';
  });

  $('msb-master').addEventListener('change', function () {
    request('soundboardSetMasterVolume', { value: parseFloat(this.value) / 300 })
      .catch(function (error) { toast(error.message, 'error'); });
  });

  $('msb-device').addEventListener('change', function () {
    request('soundboardSetOutputDevice', { deviceNumber: parseInt(this.value, 10) })
      .then(function () { refreshState(true); })
      .catch(function (error) { toast(error.message, 'error'); });
  });

  // Swipe between pages; suppress the pad click that the browser may synthesise.
  var gridScroll = $('msb-grid-scroll');
  var touch = { x: 0, y: 0, active: false };

  gridScroll.addEventListener('touchstart', function (event) {
    if (event.touches.length !== 1) { touch.active = false; return; }
    touch.active = true;
    touch.x = event.touches[0].clientX;
    touch.y = event.touches[0].clientY;
  }, { passive: true });

  gridScroll.addEventListener('click', function (event) {
    if (!suppressClick) return;
    suppressClick = false;
    event.preventDefault();
    event.stopPropagation();
  }, true);

  gridScroll.addEventListener('touchend', function (event) {
    if (!touch.active) return;
    touch.active = false;
    var point = event.changedTouches && event.changedTouches[0];
    if (!point) return;

    var dx = point.clientX - touch.x;
    var dy = point.clientY - touch.y;
    if (Math.abs(dx) < 64 || Math.abs(dx) < Math.abs(dy) * 1.6) return;

    var target = dx < 0 ? $('msb-next-page') : $('msb-prev-page');
    if (target && !target.disabled) {
      suppressClick = true;
      target.click();
      setTimeout(function () { suppressClick = false; }, 350);
    }
  }, { passive: true });

  // ── Host events ──────────────────────────────────────────
  function onEvent(name, data) {
    switch (name) {
      case 'refresh':
        refreshState(false);
        break;
      case 'soundboardPlayback':
        onPlayback(data);
        break;
      case 'soundboardMidiCaptured':
        onMidiCaptured(data);
        break;
      case 'soundboardMidiError':
        toast(data && data.message ? data.message : 'MIDI error', 'error');
        break;
      case 'soundboardImportProgress':
        onImportProgress(data);
        break;
    }
  }

  function onMidiCaptured(data) {
    if (!assignTarget) return;
    assignDetected = data;
    if (assignTimer) { clearTimeout(assignTimer); assignTimer = null; }

    var unit = data.messageType === 'ControlChange' ? 'CC' : 'Value';
    var status = $('msb-assign-status');
    status.className = 'msb-assign-status detected';
    status.textContent = '✓ ' + data.messageType + '\nChannel ' + data.channel +
      ' · ' + unit + ' ' + data.value1 + ' · value ' + data.value2 +
      '\n\nAdjust the feedback values, then press OK.';
    $('msb-assign-ok').disabled = false;
  }

  function onImportProgress(data) {
    if (!data || !editing) return;
    if (data.row !== editing.row || data.col !== editing.col) return;
    var stages = { downloading: 'Downloading audio…', importing: 'Importing…', done: 'Imported', error: 'Error' };
    $('msb-edit-import-status').textContent = stages[data.stage] || data.stage;
  }

  // ── Local keyboard shortcuts (external keyboard) ─────────
  document.addEventListener('keydown', function (event) {
    if (!state || view !== 'board' || !sheet.hidden) return;
    var tag = (event.target && event.target.tagName) || '';
    if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;

    var gesture = window.SRKeys ? window.SRKeys.gestureFromEvent(event) : null;
    if (!gesture) return;
    var shortcuts = state.keyboardShortcuts || {};
    var matches = function (entry) {
      return entry && entry.enabled && entry.gesture && entry.gesture.toLowerCase() === gesture.toLowerCase();
    };

    if (matches(shortcuts.stopAll)) { event.preventDefault(); request('soundboardStopAll'); return; }
    if (matches(shortcuts.volumeUp)) { event.preventDefault(); adjustMaster(5); return; }
    if (matches(shortcuts.volumeDown)) { event.preventDefault(); adjustMaster(-5); return; }
    if (matches(shortcuts.nextPage)) { event.preventDefault(); request('soundboardStepPage', { delta: 1 }).then(function () { refreshState(true); }); return; }
    if (matches(shortcuts.previousPage)) { event.preventDefault(); request('soundboardStepPage', { delta: -1 }).then(function () { refreshState(true); }); return; }

    (state.buttons || []).forEach(function (button) {
      if (button.isEmpty || !button.shortcut || !button.shortcut.enabled || !button.shortcut.gesture) return;
      if (button.shortcut.gesture.toLowerCase() !== gesture.toLowerCase()) return;
      event.preventDefault();
      request('soundboardPlay', { row: button.row, col: button.col });
    });
  });

  function adjustMaster(delta) {
    var slider = $('msb-master');
    slider.value = Math.max(0, Math.min(300, parseFloat(slider.value) + delta));
    $('msb-master-label').textContent = Math.round(slider.value) + '%';
    request('soundboardSetMasterVolume', { value: parseFloat(slider.value) / 300 }).catch(function () { });
  }

  // ── Helpers ──────────────────────────────────────────────
  function fillRange(id, min, max, selected) {
    var select = $(id);
    select.innerHTML = '';
    for (var i = min; i <= max; i++) {
      var option = document.createElement('option');
      option.value = i;
      option.textContent = i;
      select.appendChild(option);
    }
    select.value = selected;
  }

  // ── Start ────────────────────────────────────────────────
  showView('board');
  refreshState(true);
})();
