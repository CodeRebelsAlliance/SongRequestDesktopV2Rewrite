(function () {
  'use strict';

  let requestId = 0;
  const pending = {};
  let state = null;
  let _playback = { pageId: null, items: {} };

  const PREDEFINED_COLORS = ['#2A2A2A', '#FF5733', '#33FF57', '#3357FF', '#FF33F5', '#F5FF33', '#33FFF5', '#FF8C33', '#8C33FF', '#FF3383'];

  // ── IPC ────────────────────────────────────────────────────────
  function postMessageToHost(msg) {
    try { window.external.sendMessage(msg); }
    catch (e1) {
      try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(msg); }
      catch (e2) { console.error('Failed to send message:', e2); }
    }
  }

  function send(method, params) {
    return new Promise((resolve, reject) => {
      const id = ++requestId;
      pending[id] = {
        resolve, reject,
        timeout: setTimeout(() => { delete pending[id]; reject(new Error('Request timeout: ' + method)); }, 60000)
      };
      postMessageToHost(JSON.stringify(Object.assign({ type: 'request', id, method }, params || {})));
    });
  }

  function handleIncoming(json) {
    try {
      const msg = JSON.parse(json);
      if (msg.type === 'response') {
        const p = pending[msg.id];
        if (!p) return;
        clearTimeout(p.timeout);
        delete pending[msg.id];
        if (msg.result && msg.result.error) p.reject(new Error(msg.result.error));
        else p.resolve(msg.result);
      } else if (msg.type === 'event') {
        handleEvent(msg.eventName, msg.data);
      }
    } catch (e) { console.error('Parse error:', e); }
  }

  window.external.receiveMessage = handleIncoming;
  if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
    window.chrome.webview.addEventListener('message', function (e) { handleIncoming(e.data); });
  }

  function handleEvent(name, data) {
    if (name === 'soundboardPlayback') onPlayback(data);
    else if (name === 'soundboardMidiCaptured') onMidiCaptured(data);
    else if (name === 'soundboardMidiError') toast(data && data.message ? data.message : 'MIDI error', 'error');
    else if (name === 'soundboardImportProgress') onImportProgress(data);
  }

  // ── Utilities ──────────────────────────────────────────────────
  function escapeHtml(str) {
    if (str === null || str === undefined) return '';
    return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  function toast(msg, type) {
    const container = document.getElementById('toast-container');
    const el = document.createElement('div');
    el.className = 'toast ' + (type || 'info');
    const text = document.createElement('span');
    text.textContent = msg;
    const closeBtn = document.createElement('button');
    closeBtn.className = 'toast-close';
    closeBtn.innerHTML = '<i class="fas fa-times"></i>';
    closeBtn.addEventListener('click', () => el.remove());
    const bar = document.createElement('div');
    bar.className = 'toast-bar';
    el.appendChild(text); el.appendChild(closeBtn); el.appendChild(bar);
    container.appendChild(el);
    bar.style.transition = 'width 4s linear';
    requestAnimationFrame(() => { bar.style.width = '0%'; });
    setTimeout(() => { if (el.parentNode) el.remove(); }, 4000);
  }

  function fmt(sec) {
    sec = Math.max(0, Math.floor(sec || 0));
    const m = Math.floor(sec / 60), s = sec % 60;
    return m + ':' + String(s).padStart(2, '0');
  }

  function showModal(id) { document.getElementById(id).style.display = 'flex'; }
  function hideModal(id) { document.getElementById(id).style.display = 'none'; }
  function anyModalOpen() {
    return Array.prototype.some.call(document.querySelectorAll('.modal-overlay'), el => el.style.display === 'flex');
  }

  // ── State / rendering ──────────────────────────────────────────
  async function refreshState() {
    try {
      state = await send('soundboardGetState');
      renderToolbar();
      renderGrid();
      renderBottomBar();
    } catch (e) {
      toast('Failed to load soundboard: ' + e.message, 'error');
    }
  }

  function renderToolbar() {
    const page = state.currentPage;
    document.getElementById('sb-grid-info').textContent = 'Grid: ' + page.columns + ' × ' + page.rows;
    document.getElementById('sb-page-info').textContent =
      'Page ' + (state.currentPageIndex + 1) + ' of ' + state.pages.length + ': ' + page.name;
    document.getElementById('sb-midi-enabled').checked = !!state.midiEnabled;
    const assignBtn = document.getElementById('sb-assign-mode');
    assignBtn.classList.toggle('active-assign', !!state.isAssignMode);
  }

  function renderGrid() {
    const grid = document.getElementById('sb-grid');
    const page = state.currentPage;
    grid.style.gridTemplateColumns = 'repeat(' + page.columns + ', minmax(0, 1fr))';
    grid.style.gridTemplateRows = 'repeat(' + page.rows + ', minmax(88px, 1fr))';
    grid.innerHTML = '';
    for (const b of state.buttons) {
      const el = document.createElement('div');
      el.className = 'sb-btn' + (b.isEmpty ? ' empty' : '') + (state.isAssignMode ? ' assign-mode' : '');
      el.dataset.row = b.row;
      el.dataset.col = b.col;
      el.dataset.empty = b.isEmpty ? '1' : '0';
      el.style.setProperty('--btn-color', b.color || '#2a2a2a');
      const nameText = b.isEmpty ? ('Empty ' + (b.index + 1)) : b.name;
      el.innerHTML =
        '<div class="sb-name">' + escapeHtml(nameText) + '</div>' +
        '<div class="sb-icon">' + escapeHtml(b.icon || '🔊') + '</div>' +
        '<div class="sb-status">' + (b.isEmpty ? '' : 'Ready') + '</div>' +
        '<div class="sb-progress"></div>' +
        '<div class="sb-overlay"></div>';
      el.addEventListener('click', () => onButtonClick(b));
      el.addEventListener('contextmenu', (e) => { e.preventDefault(); openContextMenu(e, b); });
      grid.appendChild(el);
    }
    applyPlaybackToGrid();
  }

  function renderBottomBar() {
    const vol = state.masterVolume || 0;
    const slider = document.getElementById('sb-master');
    slider.value = Math.round(vol * 300);
    document.getElementById('sb-master-label').textContent = Math.round(slider.value) + '%';
    document.getElementById('sb-prev').disabled = state.currentPageIndex <= 0;
    document.getElementById('sb-next').disabled = state.currentPageIndex >= state.pages.length - 1;

    const device = document.getElementById('sb-device');
    device.innerHTML = '';
    for (const d of state.audioDevices) {
      const opt = document.createElement('option');
      opt.value = d.number;
      opt.textContent = escapeHtml(d.name);
      device.appendChild(opt);
    }
    device.value = String(state.outputDeviceNumber);
  }

  // ── Playback events ────────────────────────────────────────────
  function onPlayback(data) {
    if (!data) return;
    const items = {};
    for (const it of (data.items || [])) items[it.row + '_' + it.col] = it;
    _playback = { pageId: data.pageId, items };
    applyPlaybackToGrid();
  }

  function applyPlaybackToGrid() {
    const pageId = state && state.currentPage ? state.currentPage.id : null;
    const active = _playback.pageId === pageId ? _playback.items : {};
    document.querySelectorAll('.sb-btn').forEach(el => {
      const key = el.dataset.row + '_' + el.dataset.col;
      const item = active[key];
      const prog = el.querySelector('.sb-progress');
      const status = el.querySelector('.sb-status');
      if (item) {
        el.classList.add('playing');
        prog.style.width = (Math.min(item.progress, 1) * 100) + '%';
        if (status) status.textContent = fmt(item.elapsed) + ' / ' + fmt(item.total);
      } else {
        el.classList.remove('playing', 'pressed');
        prog.style.width = '0%';
        if (status && el.dataset.empty !== '1') status.textContent = 'Ready';
      }
    });
  }

  // ── Grid interactions ──────────────────────────────────────────
  async function onButtonClick(b) {
    if (state.isAssignMode) {
      openMidiAssign(b.row, b.col);
      return;
    }
    if (b.isEmpty) {
      openEditModal(b);
      return;
    }
    const el = document.querySelector('.sb-btn[data-row="' + b.row + '"][data-col="' + b.col + '"]');
    if (el) { el.classList.add('pressed'); setTimeout(() => el.classList.remove('pressed'), 150); }
    try { await send('soundboardPlay', { row: b.row, col: b.col }); }
    catch (e) { toast(e.message, 'error'); }
  }

  // ── Context menu ───────────────────────────────────────────────
  let _cmButton = null;
  const ctxMenu = document.getElementById('sb-context-menu');

  function openContextMenu(e, b) {
    _cmButton = b;
    const clearItem = document.getElementById('sb-cm-clear-midi');
    clearItem.style.display = (b.midi && b.midi.channel !== undefined) ? 'flex' : 'none';
    ctxMenu.style.display = 'block';
    const w = ctxMenu.offsetWidth, h = ctxMenu.offsetHeight;
    ctxMenu.style.left = Math.min(e.clientX, window.innerWidth - w - 6) + 'px';
    ctxMenu.style.top = Math.min(e.clientY, window.innerHeight - h - 6) + 'px';
  }
  function hideContextMenu() { ctxMenu.style.display = 'none'; _cmButton = null; }

  ctxMenu.addEventListener('click', async (e) => {
    const item = e.target.closest('.context-menu-item');
    if (!item || !_cmButton) return;
    const b = _cmButton;
    const action = item.dataset.cm;
    hideContextMenu();
    if (action === 'edit') openEditModal(b);
    else if (action === 'clear-midi') {
      if (!confirm('Clear MIDI mapping for "' + (b.name || 'this button') + '"?')) return;
      try { await send('soundboardClearMidi', { row: b.row, col: b.col }); toast('MIDI mapping cleared', 'success'); refreshState(); }
      catch (err) { toast(err.message, 'error'); }
    } else if (action === 'delete') {
      if (!confirm('Delete "' + (b.name || 'this sound') + '"? The sound file is removed if unused.')) return;
      try { await send('soundboardDeleteButton', { row: b.row, col: b.col }); toast('Deleted', 'success'); refreshState(); }
      catch (err) { toast(err.message, 'error'); }
    }
  });
  document.addEventListener('click', (e) => { if (!ctxMenu.contains(e.target)) hideContextMenu(); });
  document.addEventListener('contextmenu', (e) => { if (!e.target.closest('.sb-btn')) hideContextMenu(); });
  window.addEventListener('blur', hideContextMenu);

  // ── Edit modal ─────────────────────────────────────────────────
  let _edit = null;

  function openEditModal(b) {
    _edit = {
      row: b.row,
      col: b.col,
      soundFile: b.soundFile || '',
      color: (b.color || '#2A2A2A').toUpperCase(),
      shortcut: { enabled: (b.shortcut && b.shortcut.enabled) || false, gesture: (b.shortcut && b.shortcut.gesture) || '' }
    };
    document.getElementById('sb-edit-name').value = b.isEmpty ? '' : (b.name || '');
    document.getElementById('sb-edit-icon').value = b.icon || '🔊';
    document.getElementById('sb-edit-soundfile').value = b.soundFile || '';
    document.getElementById('sb-edit-fadein').checked = !!b.fadeIn;
    document.getElementById('sb-edit-fadeout').checked = !!b.fadeOut;
    document.getElementById('sb-edit-repeat').value = b.repeatMode || 'none';
    const volSlider = document.getElementById('sb-edit-volume');
    volSlider.value = Math.round((b.volume != null ? b.volume : 1) * 300);
    document.getElementById('sb-edit-volume-label').textContent = Math.round(volSlider.value) + '%';
    document.getElementById('sb-edit-yturl').value = '';
    document.getElementById('sb-edit-import-status').textContent = '';
    renderColorPalette();
    renderEditMidiStatus(b);
    renderEditDevices(b.customOutputDeviceNumber);
    renderEditShortcut();
    showModal('sb-edit-modal');
  }

  function renderColorPalette() {
    const wrap = document.getElementById('sb-edit-colors');
    wrap.innerHTML = '';
    for (const c of PREDEFINED_COLORS) {
      const sw = document.createElement('div');
      sw.className = 'sb-color-swatch' + (c.toUpperCase() === _edit.color ? ' selected' : '');
      sw.style.background = c;
      sw.dataset.color = c.toUpperCase();
      sw.addEventListener('click', () => { _edit.color = c.toUpperCase(); renderColorPalette(); document.getElementById('sb-edit-custom-color').value = ''; updateColorPreview(); });
      wrap.appendChild(sw);
    }
    document.getElementById('sb-edit-custom-color').value = PREDEFINED_COLORS.includes(_edit.color) ? '' : _edit.color;
    updateColorPreview();
  }

  function updateColorPreview() {
    document.getElementById('sb-edit-color-preview').style.background = _edit.color || '#202020';
  }

  document.getElementById('sb-edit-custom-color').addEventListener('input', function () {
    const v = this.value.trim().toUpperCase();
    if (/^#[0-9A-F]{6}$/.test(v)) {
      _edit.color = v;
      document.querySelectorAll('#sb-edit-colors .sb-color-swatch').forEach(s => s.classList.remove('selected'));
      updateColorPreview();
    }
  });

  function renderEditMidiStatus(b) {
    const el = document.getElementById('sb-edit-midi');
    if (b.midi && b.midi.channel !== undefined) {
      el.className = 'sb-midi-status mapped';
      el.textContent = '✓ Mapped to Channel ' + b.midi.channel + ', ' + b.midi.messageType + ' ' + b.midi.note +
        '\nPressed: ' + b.midi.velocityPressed + ', Unpressed: ' + b.midi.velocityUnpressed;
    } else {
      el.className = 'sb-midi-status';
      el.textContent = 'Not mapped — use "Assign" mode in the toolbar to map a MIDI control.';
    }
  }

  function renderEditDevices(selected) {
    const sel = document.getElementById('sb-edit-device');
    sel.innerHTML = '<option value="">Use Global Device</option>';
    for (const d of state.audioDevices) {
      if (d.number < 0) continue;
      const opt = document.createElement('option');
      opt.value = d.number;
      opt.textContent = escapeHtml(d.name);
      sel.appendChild(opt);
    }
    sel.value = (selected === null || selected === undefined) ? '' : String(selected);
  }

  function renderEditShortcut() {
    const s = _edit.shortcut;
    document.getElementById('sb-edit-shortcut-enabled').checked = !!s.enabled;
    document.getElementById('sb-edit-shortcut').textContent = s.gesture || '(not set)';
  }

  document.getElementById('sb-edit-volume').addEventListener('input', function () {
    document.getElementById('sb-edit-volume-label').textContent = Math.round(this.value) + '%';
  });

  document.getElementById('sb-edit-browse').addEventListener('click', async () => {
    try {
      const r = await send('soundboardBrowseFile', { row: _edit.row, col: _edit.col });
      if (r && r.cancelled) return;
      if (r && r.error) { toast(r.error, 'error'); return; }
      _edit.soundFile = r.fileName;
      document.getElementById('sb-edit-soundfile').value = r.fileName;
      if (!document.getElementById('sb-edit-name').value) {
        document.getElementById('sb-edit-name').value = (r.name || r.fileName.replace(/\.[^.]+$/, ''));
      }
    } catch (e) { toast(e.message, 'error'); }
  });

  document.getElementById('sb-edit-ytimport').addEventListener('click', async () => {
    const url = document.getElementById('sb-edit-yturl').value.trim();
    if (!url) { toast('Enter a YouTube URL', 'error'); return; }
    const status = document.getElementById('sb-edit-import-status');
    status.textContent = 'Downloading...';
    try {
      const r = await send('soundboardImportYoutube', { row: _edit.row, col: _edit.col, url });
      if (r && r.error) { status.textContent = 'Error: ' + r.error; toast(r.error, 'error'); return; }
      _edit.soundFile = r.fileName;
      document.getElementById('sb-edit-soundfile').value = r.fileName;
      if (!document.getElementById('sb-edit-name').value && r.name) {
        document.getElementById('sb-edit-name').value = r.name;
      }
      status.textContent = 'Imported: ' + r.fileName;
    } catch (e) { status.textContent = 'Error: ' + e.message; }
  });

  document.getElementById('sb-edit-shortcut-set').addEventListener('click', () => {
    openKeybindCapture((gesture) => { _edit.shortcut.gesture = gesture; _edit.shortcut.enabled = true; renderEditShortcut(); });
  });
  document.getElementById('sb-edit-shortcut-clear').addEventListener('click', () => {
    _edit.shortcut.gesture = ''; _edit.shortcut.enabled = false; renderEditShortcut();
  });

  document.getElementById('sb-edit-save').addEventListener('click', async () => {
    const name = document.getElementById('sb-edit-name').value.trim();
    const soundFile = document.getElementById('sb-edit-soundfile').value.trim();
    if (!name) { toast('Please enter a button name.', 'error'); return; }
    if (!soundFile) { toast('Please select a sound file.', 'error'); return; }
    const deviceVal = document.getElementById('sb-edit-device').value;
    const payload = {
      row: _edit.row, col: _edit.col,
      name,
      icon: document.getElementById('sb-edit-icon').value,
      color: _edit.color,
      soundFile,
      fadeIn: document.getElementById('sb-edit-fadein').checked,
      fadeOut: document.getElementById('sb-edit-fadeout').checked,
      repeatMode: document.getElementById('sb-edit-repeat').value,
      volume: parseFloat(document.getElementById('sb-edit-volume').value) / 300,
      customOutputDeviceNumber: deviceVal === '' ? null : parseInt(deviceVal, 10),
      shortcutEnabled: document.getElementById('sb-edit-shortcut-enabled').checked,
      shortcutGesture: _edit.shortcut.gesture
    };
    try {
      await send('soundboardUpdateButton', payload);
      hideModal('sb-edit-modal');
      toast('Button saved', 'success');
      refreshState();
    } catch (e) { toast(e.message, 'error'); }
  });

  document.getElementById('sb-edit-cancel').addEventListener('click', () => hideModal('sb-edit-modal'));
  document.getElementById('sb-edit-close').addEventListener('click', () => hideModal('sb-edit-modal'));
  document.getElementById('sb-edit-modal').addEventListener('click', (e) => { if (e.target.id === 'sb-edit-modal') hideModal('sb-edit-modal'); });

  // ── Add Sound ──────────────────────────────────────────────────
  document.getElementById('sb-add-sound').addEventListener('click', () => {
    const empty = state.buttons.find(b => b.isEmpty);
    if (!empty) { toast('No empty buttons on this page. Resize the grid or delete a sound.', 'info'); return; }
    openEditModal(empty);
  });

  // ── Pages modal ────────────────────────────────────────────────
  let _pagesSelected = 0;

  function openPagesModal() {
    _pagesSelected = state.currentPageIndex;
    renderPagesList();
    document.getElementById('sb-pages-addform').style.display = 'none';
    showModal('sb-pages-modal');
  }

  function renderPagesList() {
    const list = document.getElementById('sb-pages-list');
    list.innerHTML = '';
    state.pages.forEach((p, i) => {
      const el = document.createElement('div');
      el.className = 'sb-page-item' + (i === _pagesSelected ? ' selected' : '');
      el.textContent = (i + 1) + '. ' + p.name + ' (' + p.columns + '×' + p.rows + ')';
      el.addEventListener('click', () => { _pagesSelected = i; renderPagesList(); });
      list.appendChild(el);
    });
  }

  async function pagesAction(fn) { try { await fn(); await refreshState(); renderPagesList(); if (_pagesSelected >= state.pages.length) _pagesSelected = state.pages.length - 1; } catch (e) { toast(e.message, 'error'); } }

  document.getElementById('sb-pages-add').addEventListener('click', () => {
    const form = document.getElementById('sb-pages-addform');
    form.style.display = form.style.display === 'none' ? 'flex' : 'none';
    if (form.style.display === 'flex') {
      document.getElementById('sb-pages-addname').value = 'Page ' + new Date().toTimeString().slice(0, 5).replace(':', '');
      fillRange('sb-pages-addcols', 1, 12, 4);
      fillRange('sb-pages-addrows', 1, 12, 3);
      document.getElementById('sb-pages-addname').focus();
    }
  });
  document.getElementById('sb-pages-addconfirm').addEventListener('click', () => {
    const name = document.getElementById('sb-pages-addname').value.trim();
    const cols = parseInt(document.getElementById('sb-pages-addcols').value, 10);
    const rows = parseInt(document.getElementById('sb-pages-addrows').value, 10);
    pagesAction(async () => {
      await send('soundboardAddPage', { name, columns: cols, rows });
      document.getElementById('sb-pages-addform').style.display = 'none';
      _pagesSelected = state.pages.length;
      toast('Page added', 'success');
    });
  });
  document.getElementById('sb-pages-addcancel').addEventListener('click', () => { document.getElementById('sb-pages-addform').style.display = 'none'; });

  document.getElementById('sb-pages-delete').addEventListener('click', () => {
    const p = state.pages[_pagesSelected];
    if (!p) return;
    if (state.pages.length <= 1) { toast('Cannot delete the last page', 'error'); return; }
    if (!confirm('Delete page "' + p.name + '"?')) return;
    pagesAction(async () => { await send('soundboardDeletePage', { index: _pagesSelected }); toast('Page deleted', 'success'); });
  });

  document.getElementById('sb-pages-rename').addEventListener('click', () => {
    const p = state.pages[_pagesSelected];
    if (!p) return;
    openInputModal('Rename Page', 'Enter new name:', p.name, (value) => {
      if (!value) return;
      pagesAction(async () => { await send('soundboardRenamePage', { index: _pagesSelected, name: value }); toast('Page renamed', 'success'); });
    });
  });

  document.getElementById('sb-pages-up').addEventListener('click', () => {
    if (_pagesSelected <= 0) return;
    const from = _pagesSelected;
    pagesAction(async () => { await send('soundboardMovePage', { index: from, delta: -1 }); _pagesSelected = from - 1; });
  });
  document.getElementById('sb-pages-down').addEventListener('click', () => {
    if (_pagesSelected >= state.pages.length - 1) return;
    const from = _pagesSelected;
    pagesAction(async () => { await send('soundboardMovePage', { index: from, delta: 1 }); _pagesSelected = from + 1; });
  });
  document.getElementById('sb-pages-jump').addEventListener('click', () => {
    const idx = _pagesSelected;
    pagesAction(async () => { await send('soundboardSetPage', { index: idx }); toast('Jumped to page ' + (idx + 1), 'success'); });
  });
  document.getElementById('sb-pages-done').addEventListener('click', () => hideModal('sb-pages-modal'));
  document.getElementById('sb-pages-close').addEventListener('click', () => hideModal('sb-pages-modal'));
  document.getElementById('sb-pages-modal').addEventListener('click', (e) => { if (e.target.id === 'sb-pages-modal') hideModal('sb-pages-modal'); });

  // ── Grid size modal ────────────────────────────────────────────
  document.getElementById('sb-grid-size').addEventListener('click', () => {
    fillRange('sb-gridsize-cols', 1, 12, state.currentPage.columns);
    fillRange('sb-gridsize-rows', 1, 12, state.currentPage.rows);
    showModal('sb-gridsize-modal');
  });
  document.getElementById('sb-gridsize-apply').addEventListener('click', async () => {
    try {
      await send('soundboardSetGridSize', {
        columns: parseInt(document.getElementById('sb-gridsize-cols').value, 10),
        rows: parseInt(document.getElementById('sb-gridsize-rows').value, 10)
      });
      hideModal('sb-gridsize-modal');
      refreshState();
    } catch (e) { toast(e.message, 'error'); }
  });
  document.getElementById('sb-gridsize-cancel').addEventListener('click', () => hideModal('sb-gridsize-modal'));
  document.getElementById('sb-gridsize-close').addEventListener('click', () => hideModal('sb-gridsize-modal'));

  // ── Keyboard shortcuts modal ───────────────────────────────────
  const KEY_LABELS = [
    { id: 'stopAll', label: 'Stop All Sounds' },
    { id: 'volumeUp', label: 'Master Volume Up' },
    { id: 'volumeDown', label: 'Master Volume Down' },
    { id: 'nextPage', label: 'Next Page' },
    { id: 'previousPage', label: 'Previous Page' }
  ];
  let _keysEdit = null;

  function openKeysModal() {
    _keysEdit = {};
    for (const k of KEY_LABELS) {
      const s = state.keyboardShortcuts[k.id] || {};
      _keysEdit[k.id] = { enabled: !!s.enabled, gesture: s.gesture || '' };
    }
    renderKeysList();
    showModal('sb-keys-modal');
  }

  function renderKeysList() {
    const list = document.getElementById('sb-keys-list');
    list.innerHTML = '';
    for (const k of KEY_LABELS) {
      const row = document.createElement('div');
      row.className = 'sb-key-row';
      row.innerHTML =
        '<span class="sb-key-label">' + escapeHtml(k.label) + '</span>' +
        '<input type="checkbox" data-keys-enabled="' + k.id + '"' + (_keysEdit[k.id].enabled ? ' checked' : '') + '/>' +
        '<div class="sb-shortcut-display" data-keys-gesture="' + k.id + '">' + escapeHtml(_keysEdit[k.id].gesture || '(not set)') + '</div>' +
        '<button class="top-btn" data-keys-set="' + k.id + '">Set</button>' +
        '<button class="top-btn danger" data-keys-clear="' + k.id + '">Clear</button>';
      list.appendChild(row);
    }
  }

  document.getElementById('sb-keys-list').addEventListener('click', (e) => {
    const el = e.target.closest('button');
    if (!el) return;
    const set = el.dataset.keysSet;
    const clear = el.dataset.keysClear;
    if (set) openKeybindCapture((gesture) => { _keysEdit[set].gesture = gesture; _keysEdit[set].enabled = true; renderKeysList(); });
    else if (clear) { _keysEdit[clear].gesture = ''; _keysEdit[clear].enabled = false; renderKeysList(); }
  });
  document.getElementById('sb-keys-list').addEventListener('change', (e) => {
    const id = e.target.dataset.keysEnabled;
    if (id) { _keysEdit[id].enabled = e.target.checked; renderKeysList(); }
  });
  document.getElementById('sb-keys-apply').addEventListener('click', async () => {
    try {
      await send('soundboardSaveKeyboardShortcuts', { shortcuts: _keysEdit });
      hideModal('sb-keys-modal');
      toast('Shortcuts saved', 'success');
      refreshState();
    } catch (e) { toast(e.message, 'error'); }
  });
  document.getElementById('sb-keys-cancel').addEventListener('click', () => hideModal('sb-keys-modal'));
  document.getElementById('sb-keys-close').addEventListener('click', () => hideModal('sb-keys-modal'));

  // ── MIDI settings modal ────────────────────────────────────────
  document.getElementById('sb-midi-settings').addEventListener('click', () => {
    document.getElementById('sb-midi-modal-enabled').checked = !!state.midiEnabled;
    fillMidiSelect('sb-midi-modal-input', state.midiInputs, state.midiInputDevice);
    fillMidiSelect('sb-midi-modal-output', state.midiOutputs, state.midiOutputDevice);
    const vel = state.emptyFeedbackVelocity != null ? state.emptyFeedbackVelocity : 10;
    document.getElementById('sb-midi-modal-vel').value = vel;
    document.getElementById('sb-midi-modal-vel-label').textContent = vel;
    updateMidiStatus();
    showModal('sb-midi-modal');
  });

  function fillMidiSelect(id, devices, selected) {
    const sel = document.getElementById(id);
    sel.innerHTML = '<option value="-1">None</option>';
    for (const d of (devices || [])) {
      const opt = document.createElement('option');
      opt.value = d.number;
      opt.textContent = escapeHtml(d.name || ('Device ' + d.number));
      sel.appendChild(opt);
    }
    sel.value = String(selected == null ? -1 : selected);
  }

  function updateMidiStatus() {
    const enabled = document.getElementById('sb-midi-modal-enabled').checked;
    const input = parseInt(document.getElementById('sb-midi-modal-input').value, 10);
    const output = parseInt(document.getElementById('sb-midi-modal-output').value, 10);
    let text;
    if (!enabled) text = 'MIDI is disabled.';
    else if (input >= 0 && output >= 0) text = 'MIDI input and output connected.';
    else if (input >= 0) text = 'MIDI input connected (no output feedback device selected).';
    else if (output >= 0) text = 'MIDI output connected (no input device selected).';
    else text = 'No MIDI devices selected.';
    document.getElementById('sb-midi-modal-status').textContent = text;
  }

  ['sb-midi-modal-input', 'sb-midi-modal-output'].forEach(id => {
    document.getElementById(id).addEventListener('change', async () => {
      updateMidiStatus();
      try {
        await send('soundboardSetMidiDevices', {
          inputDevice: parseInt(document.getElementById('sb-midi-modal-input').value, 10),
          outputDevice: parseInt(document.getElementById('sb-midi-modal-output').value, 10)
        });
        refreshState();
      } catch (e) { toast(e.message, 'error'); }
    });
  });
  document.getElementById('sb-midi-modal-enabled').addEventListener('change', async function () {
    updateMidiStatus();
    try { await send('soundboardSetMidiEnabled', { enabled: this.checked }); refreshState(); }
    catch (e) { toast(e.message, 'error'); }
  });
  document.getElementById('sb-midi-modal-vel').addEventListener('input', function () {
    document.getElementById('sb-midi-modal-vel-label').textContent = this.value;
  });
  document.getElementById('sb-midi-modal-vel').addEventListener('change', async function () {
    try { await send('soundboardSetEmptyFeedback', { velocity: parseInt(this.value, 10) }); } catch (e) { }
  });
  document.getElementById('sb-midi-test').addEventListener('click', async () => {
    try {
      const r = await send('soundboardTestMidiFeedback');
      if (r && r.error) toast(r.error, 'error'); else toast('Test pattern sent', 'info');
    } catch (e) { toast(e.message, 'error'); }
  });
  document.getElementById('sb-midi-done').addEventListener('click', () => hideModal('sb-midi-modal'));
  document.getElementById('sb-midi-close').addEventListener('click', () => hideModal('sb-midi-modal'));

  // ── MIDI assign capture ────────────────────────────────────────
  let _assignTarget = null;
  let _assignDetected = null;
  let _assignTimeout = null;

  function openMidiAssign(row, col) {
    if (!state.midiEnabled) { toast('Enable MIDI first (toolbar toggle).', 'error'); return; }
    if (state.midiInputDevice == null || state.midiInputDevice < 0) { toast('Select a MIDI input device first (MIDI settings).', 'error'); return; }
    _assignTarget = { row, col };
    _assignDetected = null;
    const status = document.getElementById('sb-midiassign-status');
    status.style.color = '#ccc';
    status.textContent = 'Waiting for MIDI input...\n\nPress a button on your MIDI controller.';
    document.getElementById('sb-midiassign-ok').disabled = true;
    document.getElementById('sb-midiassign-vp').value = 127;
    document.getElementById('sb-midiassign-vu').value = 0;
    document.getElementById('sb-midiassign-vp-label').textContent = '127';
    document.getElementById('sb-midiassign-vu-label').textContent = '0';
    showModal('sb-midiassign-modal');
    send('soundboardStartMidiAssign').catch(() => { });
    _assignTimeout = setTimeout(() => {
      if (!_assignDetected) { status.textContent = 'Timeout - No MIDI input detected'; status.style.color = '#e53935'; }
    }, 15000);
  }

  function onMidiCaptured(data) {
    if (!_assignTarget || document.getElementById('sb-midiassign-modal').style.display === 'none') return;
    _assignDetected = data;
    if (_assignTimeout) { clearTimeout(_assignTimeout); _assignTimeout = null; }
    const unit = data.messageType === 'ControlChange' ? 'CC' : 'Value1';
    const status = document.getElementById('sb-midiassign-status');
    status.style.color = '#4caf50';
    status.textContent = '✓ Detected MIDI ' + data.messageType + '\n\nChannel: ' + data.channel +
      '\n' + unit + ': ' + data.value1 + '\nValue2: ' + data.value2 +
      '\n\nAdjust feedback values below and click OK to confirm.';
    document.getElementById('sb-midiassign-ok').disabled = false;
  }

  document.getElementById('sb-midiassign-ok').addEventListener('click', async () => {
    if (!_assignTarget || !_assignDetected) return;
    const mapping = {
      channel: _assignDetected.channel,
      note: _assignDetected.value1,
      messageType: _assignDetected.messageType,
      velocityPressed: parseInt(document.getElementById('sb-midiassign-vp').value, 10),
      velocityUnpressed: parseInt(document.getElementById('sb-midiassign-vu').value, 10)
    };
    const target = _assignTarget;
    try {
      await send('soundboardSetButtonMidi', { row: target.row, col: target.col, mapping });
      toast('MIDI mapping assigned', 'success');
    } catch (e) { toast(e.message, 'error'); }
    closeMidiAssign();
    refreshState();
  });
  async function closeMidiAssign() {
    hideModal('sb-midiassign-modal');
    if (_assignTimeout) { clearTimeout(_assignTimeout); _assignTimeout = null; }
    _assignTarget = null;
    _assignDetected = null;
    try { await send('soundboardSetAssignMode', { enabled: false }); } catch (e) { }
    if (state) state.isAssignMode = false;
    renderToolbar();
  }
  document.getElementById('sb-midiassign-cancel').addEventListener('click', closeMidiAssign);
  document.getElementById('sb-midiassign-close').addEventListener('click', closeMidiAssign);
  document.getElementById('sb-midiassign-vp').addEventListener('input', function () { document.getElementById('sb-midiassign-vp-label').textContent = this.value; });
  document.getElementById('sb-midiassign-vu').addEventListener('input', function () { document.getElementById('sb-midiassign-vu-label').textContent = this.value; });

  // ── Keybind capture ────────────────────────────────────────────
  let _keybindCallback = null;
  let _keybindGesture = '';

  function openKeybindCapture(callback) {
    _keybindCallback = callback;
    _keybindGesture = '';
    document.getElementById('sb-keybind-detected').textContent = 'No shortcut detected';
    showModal('sb-keybind-modal');
    document.addEventListener('keydown', onKeybindKeyDown, true);
  }
  function closeKeybindCapture() {
    hideModal('sb-keybind-modal');
    document.removeEventListener('keydown', onKeybindKeyDown, true);
    _keybindCallback = null;
  }
  function onKeybindKeyDown(e) {
    const gesture = window.SRKeys ? window.SRKeys.gestureFromEvent(e) : null;
    if (!gesture) return;
    e.preventDefault(); e.stopPropagation();
    _keybindGesture = gesture;
    document.getElementById('sb-keybind-detected').textContent = gesture;
  }
  document.getElementById('sb-keybind-apply').addEventListener('click', () => {
    if (!_keybindGesture) { toast('Press a key combination first.', 'error'); return; }
    const cb = _keybindCallback;
    closeKeybindCapture();
    if (cb) cb(_keybindGesture);
  });
  document.getElementById('sb-keybind-cancel').addEventListener('click', closeKeybindCapture);
  document.getElementById('sb-keybind-close').addEventListener('click', closeKeybindCapture);

  // ── Generic input modal ────────────────────────────────────────
  let _inputCallback = null;
  function openInputModal(title, prompt, value, callback) {
    _inputCallback = callback;
    document.getElementById('sb-input-title').textContent = title;
    document.getElementById('sb-input-prompt').textContent = prompt;
    document.getElementById('sb-input-value').value = value || '';
    showModal('sb-input-modal');
    setTimeout(() => document.getElementById('sb-input-value').focus(), 30);
  }
  function closeInputModal() { hideModal('sb-input-modal'); _inputCallback = null; }
  document.getElementById('sb-input-ok').addEventListener('click', () => {
    const v = document.getElementById('sb-input-value').value;
    const cb = _inputCallback;
    closeInputModal();
    if (cb) cb(v);
  });
  document.getElementById('sb-input-cancel').addEventListener('click', closeInputModal);
  document.getElementById('sb-input-close').addEventListener('click', closeInputModal);
  document.getElementById('sb-input-value').addEventListener('keydown', (e) => { if (e.key === 'Enter') document.getElementById('sb-input-ok').click(); });

  // ── Toolbar / bottom bar actions ───────────────────────────────
  document.getElementById('sb-prev').addEventListener('click', () => { send('soundboardStepPage', { delta: -1 }).then(refreshState).catch(e => toast(e.message, 'error')); });
  document.getElementById('sb-next').addEventListener('click', () => { send('soundboardStepPage', { delta: 1 }).then(refreshState).catch(e => toast(e.message, 'error')); });
  document.getElementById('sb-pages').addEventListener('click', openPagesModal);
  document.getElementById('sb-keys').addEventListener('click', openKeysModal);
  document.getElementById('sb-close').addEventListener('click', () => { send('soundboardClose').catch(() => { }); });

  document.getElementById('sb-stop-all').addEventListener('click', () => { send('soundboardStopAll').catch(() => { }); });

  document.getElementById('sb-master').addEventListener('input', function () {
    document.getElementById('sb-master-label').textContent = Math.round(this.value) + '%';
  });
  document.getElementById('sb-master').addEventListener('change', function () {
    send('soundboardSetMasterVolume', { value: parseFloat(this.value) / 300 }).catch(e => toast(e.message, 'error'));
  });
  document.getElementById('sb-device').addEventListener('change', async function () {
    try { await send('soundboardSetOutputDevice', { deviceNumber: parseInt(this.value, 10) }); refreshState(); }
    catch (e) { toast(e.message, 'error'); }
  });

  document.getElementById('sb-midi-enabled').addEventListener('change', async function () {
    try { await send('soundboardSetMidiEnabled', { enabled: this.checked }); refreshState(); }
    catch (e) { toast(e.message, 'error'); refreshState(); }
  });

  document.getElementById('sb-assign-mode').addEventListener('click', async () => {
    const target = !state.isAssignMode;
    if (target && !state.midiEnabled) { toast('Enable MIDI first.', 'error'); return; }
    try {
      await send('soundboardSetAssignMode', { enabled: target });
      state.isAssignMode = target;
      renderToolbar();
      renderGrid();
      if (target) toast('Assign mode active — click a button to map a MIDI control.', 'info');
    } catch (e) { toast(e.message, 'error'); }
  });

  // ── Keyboard shortcuts (client-side) ───────────────────────────
  document.addEventListener('keydown', (e) => {
    if (!state || anyModalOpen()) return;
    const tag = (e.target && e.target.tagName) || '';
    if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;

    const gesture = window.SRKeys ? window.SRKeys.gestureFromEvent(e) : null;
    if (!gesture) return;
    const sc = state.keyboardShortcuts || {};

    const match = (s) => s && s.enabled && s.gesture && s.gesture.toLowerCase() === gesture.toLowerCase();

    if (match(sc.stopAll)) { e.preventDefault(); send('soundboardStopAll').catch(() => { }); return; }
    if (match(sc.volumeUp)) { e.preventDefault(); adjustMaster(5); return; }
    if (match(sc.volumeDown)) { e.preventDefault(); adjustMaster(-5); return; }
    if (match(sc.nextPage)) { e.preventDefault(); send('soundboardStepPage', { delta: 1 }).then(refreshState).catch(() => { }); return; }
    if (match(sc.previousPage)) { e.preventDefault(); send('soundboardStepPage', { delta: -1 }).then(refreshState).catch(() => { }); return; }

    for (const b of state.buttons) {
      if (b.isEmpty || !b.shortcut || !b.shortcut.enabled || !b.shortcut.gesture) continue;
      if (b.shortcut.gesture.toLowerCase() === gesture.toLowerCase()) {
        e.preventDefault();
        send('soundboardPlay', { row: b.row, col: b.col }).catch(() => { });
        const el = document.querySelector('.sb-btn[data-row="' + b.row + '"][data-col="' + b.col + '"]');
        if (el) { el.classList.add('pressed'); setTimeout(() => el.classList.remove('pressed'), 150); }
        return;
      }
    }
  });

  function adjustMaster(delta) {
    const slider = document.getElementById('sb-master');
    slider.value = Math.max(0, Math.min(300, parseFloat(slider.value) + delta));
    document.getElementById('sb-master-label').textContent = Math.round(slider.value) + '%';
    send('soundboardSetMasterVolume', { value: parseFloat(slider.value) / 300 }).catch(() => { });
  }

  // ── Misc ───────────────────────────────────────────────────────
  function fillRange(id, min, max, selected) {
    const sel = document.getElementById(id);
    sel.innerHTML = '';
    for (let i = min; i <= max; i++) {
      const opt = document.createElement('option');
      opt.value = i;
      opt.textContent = i;
      sel.appendChild(opt);
    }
    sel.value = selected;
  }

  function onImportProgress(data) {
    if (!data || !_edit) return;
    const status = document.getElementById('sb-edit-import-status');
    if (!status) return;
    const stages = { downloading: 'Downloading audio...', importing: 'Importing...', done: 'Imported', error: 'Error' };
    if (data.row === _edit.row && data.col === _edit.col) {
      status.textContent = stages[data.stage] || data.stage;
    }
  }

  refreshState();
})();
