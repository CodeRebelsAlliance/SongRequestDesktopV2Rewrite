(function() {
  'use strict';

  let requestId = 0;
  const pending = {};
  let database = [];
  let blacklist = [];
  let badWords = [];
  let _fetchingLock = false;
  let _fetchStartTime = 0;
  const thumbCache = {};
  let _authOverlayVisible = false;
  let _authBrowsers = [];

  function postMessageToHost(msg) {
    try {
      window.external.sendMessage(msg);
    } catch (e1) {
      try {
        if (window.chrome && window.chrome.webview) {
          window.chrome.webview.postMessage(msg);
        }
      } catch (e2) {
        console.error('Failed to send message:', e2);
      }
    }
  }

  function send(method, params) {
    return new Promise((resolve, reject) => {
      const id = ++requestId;
      pending[id] = { resolve, reject, timeout: setTimeout(() => {
        delete pending[id];
        if (!_authOverlayVisible) showAuthOverlay('server');
        reject(new Error('Request timeout: ' + method));
      }, 60000) };
      const msg = JSON.stringify({ type: 'request', id, method, ...params });
      postMessageToHost(msg);
    });
  }

  function classifyError(err) {
    if (typeof err !== 'string') return null;
    if (err.includes('401') || err.includes('Unauthorized')) return 'auth';
    if (err.includes('404') || err.includes('timeout') || err.includes('timed out') ||
        err.includes('connect') || err.includes('refused') || err.includes('ECONNREFUSED') ||
        err.includes('No such host') || err.includes('DNS') || err.includes('ENOTFOUND') ||
        err.includes('network')) return 'server';
    return null;
  }

  function showAuthOverlay(type) {
    if (_authOverlayVisible) return;
    _authOverlayVisible = true;
    const icon = document.getElementById('auth-required-icon');
    const title = document.getElementById('auth-required-title');
    const desc = document.getElementById('auth-required-desc');
    if (type === 'auth') {
      icon.className = 'fas fa-lock';
      icon.style.color = '#f44';
      title.textContent = 'Authentication Required';
      desc.textContent = 'Your credentials are invalid or expired. Update your bearer token in Settings to continue.';
    } else {
      icon.className = 'fas fa-exclamation-triangle';
      icon.style.color = '#fa0';
      title.textContent = 'Server Unreachable';
      desc.textContent = 'Server configuration invalid or can\'t connect to server. Check your server address in Settings.';
    }
    document.getElementById('tabbar').style.display = 'none';
    document.getElementById('main').style.display = 'none';
    document.getElementById('auth-required').style.display = 'flex';
  }

  function hideAuthOverlay() {
    _authOverlayVisible = false;
    document.getElementById('tabbar').style.display = '';
    document.getElementById('main').style.display = '';
    document.getElementById('auth-required').style.display = 'none';
  }

  function handleIncoming(json) {
    try {
      const msg = JSON.parse(json);
      if (msg.type === 'response') {
        const p = pending[msg.id];
        if (p) {
          clearTimeout(p.timeout);
          delete pending[msg.id];
          if (msg.result && msg.result.error) {
            if (!_authOverlayVisible) {
              const type = classifyError(msg.result.error);
              if (type) showAuthOverlay(type);
            }
            p.reject(new Error(msg.result.error));
          } else {
            p.resolve(msg.result);
          }
        }
      } else if (msg.type === 'event') {
        handleEvent(msg.eventName, msg.data);
      }
    } catch (e) {
      console.error('Parse error:', e);
    }
  }

  // Register the host -> page handler. Photino exposes
  // window.external.receiveMessage(callback); it must be *called* (assigning to
  // it replaces Photino's shim, after which no host message is ever delivered).
  SongRequestHost.registerHostMessageHandler(handleIncoming);

  function handleEvent(eventName, data) {
    if (eventName === 'refresh') loadData();
    else if (eventName === 'libraryScanProgress') onLibraryScanProgress(data);
    else if (eventName === 'libraryScanCompleted') onLibraryScanCompleted(data);
    else if (eventName === 'downloadProgress') onDownloadProgress(data);
    else if (eventName === 'updateProgress') onUpdateProgress(data);
    else if (eventName === 'midiInput') onMidiInput(data);
    else if (eventName === 'midiActivity') onMidiActivity(data);
    else if (eventName === 'toolProgress') onToolProgress(data);
    else if (eventName === 'toolStatus') onToolStatus(data);
    else if (eventName === 'cookieBrowserChanged') selectAuthBrowser(data && data.browser);
  }

  function onUpdateProgress(data) {
    if (!data) return;
    // About modal
    const aboutModal = document.getElementById('about-modal');
    if (aboutModal && aboutModal.style.display !== 'none') {
      document.getElementById('about-update-progress').style.display = 'block';
      document.getElementById('about-update-progress-fill').style.width = Math.min(data.progress || 0, 100) + '%';
      document.getElementById('about-update-progress-text').textContent = data.message || data.stage;
    }
    // Startup panel
    const sPanel = document.getElementById('startup-update-panel');
    if (sPanel && sPanel.style.display !== 'none') {
      document.getElementById('startup-update-buttons').style.display = 'none';
      document.getElementById('startup-update-progress').style.display = 'block';
      document.getElementById('startup-update-progress-fill').style.width = Math.min(data.progress || 0, 100) + '%';
      document.getElementById('startup-update-progress-text').textContent = data.message || data.stage;
    }
  }

  // ── Download progress tracking ──────────────────────────────────
  const downloadStates = {};

  function onDownloadProgress(data) {
    if (!data || !data.videoId) return;
    const vid = data.videoId;
    if (!downloadStates[vid]) downloadStates[vid] = {};
    const st = downloadStates[vid];
    st.stage = data.stage;
    st.progress = data.progress;
    st.message = data.message || '';
    st.time = Date.now();

    const card = document.querySelector('.song-card[data-ytid="' + vid + '"]');
    if (!card) return;
    const info = card.querySelector('.info');
    if (!info) return;

    // Show progress container inside .info so it doesn't break flex row layout
    let progWrap = info.querySelector('.dl-progress');
    if (!progWrap) {
      progWrap = document.createElement('div');
      progWrap.className = 'dl-progress';
      const bar = document.createElement('div');
      bar.className = 'dl-bar';
      const fill = document.createElement('div');
      fill.className = 'dl-fill';
      bar.appendChild(fill);
      const label = document.createElement('div');
      label.className = 'dl-label';
      progWrap.appendChild(bar);
      progWrap.appendChild(label);
      info.appendChild(progWrap);
    }
    const fill = progWrap.querySelector('.dl-fill');
    const label = progWrap.querySelector('.dl-label');

    if (data.stage === 'complete') {
      progWrap.classList.add('dl-done');
      progWrap.classList.remove('dl-error');
      fill.style.width = '100%';
      fill.className = 'dl-fill dl-fill-done';
      label.innerHTML = '<i class="fas fa-check-circle"></i> ' + escapeHtml(data.message || 'Complete');
      card.classList.remove('is-loading');
      // Remove progress after a delay
      setTimeout(() => { if (progWrap.parentNode) progWrap.remove(); }, 5000);
      return;
    }

    if (data.stage === 'error') {
      progWrap.classList.add('dl-error');
      progWrap.classList.remove('dl-done');
      fill.style.width = '100%';
      fill.className = 'dl-fill dl-fill-error';
      label.innerHTML = '<i class="fas fa-exclamation-triangle"></i> ' + escapeHtml(data.message || 'Error');
      return;
    }

    // Stage-specific styling
    const stageIcons = {
      metadata: 'fa-tag',
      downloading: 'fa-download',
      thumbnail: 'fa-image',
      subtitles: 'fa-closed-captioning'
    };
    const stageColors = {
      metadata: '#5fa8e0',
      downloading: '#4caf50',
      thumbnail: '#e0a85f',
      subtitles: '#a85fe0'
    };
    const icon = stageIcons[data.stage] || 'fa-spinner fa-spin';
    const color = stageColors[data.stage] || '#888';
    fill.style.width = Math.min(data.progress, 100) + '%';
    fill.style.background = color;
    fill.className = 'dl-fill';
    label.innerHTML = '<i class="fas ' + icon + '" style="color:' + color + '"></i> ' + escapeHtml(data.message || data.stage);
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
    el.appendChild(text);
    el.appendChild(closeBtn);
    el.appendChild(bar);
    container.appendChild(el);
    bar.style.transition = 'width 4s linear';
    requestAnimationFrame(() => { bar.style.width = '0%'; });
    setTimeout(() => { if (el.parentNode) el.remove(); }, 4000);
  }

  function setStatus(msg) {
    document.getElementById('status-text').textContent = msg;
  }

  function log(msg, cls) {
    const out = document.getElementById('console-output');
    const el = document.createElement('div');
    el.className = cls || '';
    el.textContent = '[' + new Date().toLocaleTimeString() + '] ' + msg;
    out.appendChild(el);
    out.scrollTop = out.scrollHeight;
  }

  function formatDuration(ticks) {
    if (!ticks || ticks <= 0) return '';
    const totalSec = Math.floor(ticks / 10000000);
    const h = Math.floor(totalSec / 3600);
    const m = Math.floor((totalSec % 3600) / 60);
    const s = totalSec % 60;
    if (h > 0) return h + ':' + String(m).padStart(2, '0') + ':' + String(s).padStart(2, '0');
    return m + ':' + String(s).padStart(2, '0');
  }

  function formatTimestamp(ts) {
    if (!ts) return '';
    const d = new Date(ts * 1000);
    return d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  function getStatusLabel(video) {
    if (blacklist.some(b => b.ytid === video.ytid)) return 'blacklisted';
    if (video.isApproved) return 'approved';
    return 'pending';
  }

  function getStatusIcon(label) {
    if (label === 'pending') return '<i class="fas fa-clock"></i>';
    if (label === 'approved') return '<i class="fas fa-check-circle"></i>';
    if (label === 'blacklisted') return '<i class="fas fa-ban"></i>';
    return '';
  }

  // Clock (moved to bottom bar center)
  function updateClock() {
    document.getElementById('clock').textContent = new Date().toLocaleTimeString();
  }
  setInterval(updateClock, 1000);
  updateClock();

  // Connection status
  function setConnectionStatus(state, msg) {
    const el = document.getElementById('connection-status');
    el.className = 'status-' + state;
    el.innerHTML = '<i class="fas fa-circle"></i> ' + msg;
  }

  // Startup sequence
  runStartupSequence();

  // About modal
  const aboutModal = document.getElementById('about-modal');
  function showAbout() {
    aboutModal.style.display = 'flex';
    send('getVersion').then(r => {
      document.getElementById('about-version').textContent = 'Version ' + (r.version || '?');
    }).catch(() => {
      document.getElementById('about-version').textContent = 'Version ?';
    });
  }
  document.getElementById('top-left').addEventListener('click', showAbout);
  document.getElementById('btn-about-close').addEventListener('click', () => { aboutModal.style.display = 'none'; });
  aboutModal.addEventListener('click', e => { if (e.target === aboutModal) aboutModal.style.display = 'none'; });
  let _availableUpdate = null;
  document.getElementById('btn-check-updates').addEventListener('click', async function() {
    const statusEl = document.getElementById('about-update-status');
    const installBtn = document.getElementById('btn-install-update');
    statusEl.textContent = 'Checking...';
    installBtn.style.display = 'none';
    _availableUpdate = null;
    try {
      const result = await send('checkForUpdates');
      const notesWrap = document.getElementById('about-update-notes-wrap');
      const notesEl = document.getElementById('about-update-notes');
      if (notesWrap) notesWrap.style.display = 'none';
      if (result.error) {
        statusEl.textContent = 'Check failed: ' + result.error;
      } else if (result.updateAvailable) {
        statusEl.textContent = 'Update available: ' + result.latestVersion + ' (current: ' + result.currentVersion + ')';
        toast('Update available: ' + result.latestVersion, 'info');
        _availableUpdate = result;
        installBtn.style.display = '';
        if (notesWrap && notesEl && result.releaseNotes) {
          notesEl.innerHTML = renderMarkdown(result.releaseNotes);
          notesWrap.style.display = '';
        }
      } else {
        statusEl.textContent = 'You\'re up to date! (Version ' + result.currentVersion + ')';
      }
    } catch (e) {
      statusEl.textContent = 'Update check failed';
    }
  });
  document.getElementById('btn-install-update').addEventListener('click', async function() {
    if (!_availableUpdate || !_availableUpdate.downloadUrl) return;
    const btn = this;
    btn.disabled = true;
    document.getElementById('about-update-progress').style.display = 'block';
    document.getElementById('about-update-progress-text').textContent = 'Starting download...';
    try {
      const r = await send('installUpdate', { downloadUrl: _availableUpdate.downloadUrl });
      if (r && r.error) {
        document.getElementById('about-update-progress-text').textContent = 'Error: ' + r.error;
        btn.disabled = false;
      }
    } catch (e) {
      document.getElementById('about-update-progress-text').textContent = 'Error: ' + e.message;
      btn.disabled = false;
    }
  });

  // ── Changelog / release notes rendering ────────────────────────
  // Release bodies come from GitHub as Markdown; render them instead of dumping the
  // raw text (headings and bullets used to show up literally).
  function renderMarkdown(markdown) {
    var text = markdown || '';
    if (!text) return '';
    if (window.SongRequestMarkdown && typeof window.SongRequestMarkdown.render === 'function') {
      try { return window.SongRequestMarkdown.render(text); } catch (e) { /* fall through */ }
    }
    var div = document.createElement('div');
    div.textContent = text;      // last resort: plain, escaped text
    return '<p>' + div.innerHTML.replace(/\n/g, '<br>') + '</p>';
  }

  // Settings modal
  const settingsModal = document.getElementById('settings-modal');
  function showSettings() {
    settingsModal.style.display = 'flex';
    loadSettings();
  }
  function hideSettings() {
    settingsModal.style.display = 'none';
  }
  settingsModal.addEventListener('click', e => { if (e.target === settingsModal) hideSettings(); });

  async function loadSettings() {
    const statusEl = document.getElementById('settings-status');
    statusEl.textContent = 'Loading...';
    try {
      const cfg = await send('getSettings');
      if (cfg.error) { statusEl.textContent = 'Error: ' + cfg.error; return; }
      document.getElementById('s-fetching-timer').value = cfg.fetchingTimer ?? 10;
      document.getElementById('s-threads').value = cfg.threads ?? 3;
      document.getElementById('s-default-sorting').value = cfg.defaultSorting ?? '';
      document.getElementById('s-fullscreen').checked = cfg.presentationFullscreen ?? false;
      document.getElementById('s-normalize-volume').checked = cfg.normalizeVolume ?? false;
      document.getElementById('s-caption-fallback').checked = cfg.useCaptionLyricsFallback ?? false;
      document.getElementById('s-enable-announcements').checked = cfg.enableAnnouncements ?? false;
      document.getElementById('s-autopilot').checked = cfg.autoEnqueue ?? false;
      document.getElementById('s-address').value = cfg.address ?? '';
      document.getElementById('s-request-url').value = cfg.requestUrl ?? '';
      document.getElementById('s-token').value = cfg.bearerToken ?? '';

      const dsm = cfg.defaultSubmitMethod ?? 'search';
      document.getElementById('s-default-submit-method').value = dsm;
      _defaultSubmitMethod = dsm;

      const si = await send('getSendinStatus');
      document.getElementById('s-sendin-allowed').checked = si.sendinAllowed ?? false;

      const rc = await send('getRemoteConfig');
      if (rc && !rc.error) loadRemoteConfig(rc);
      document.getElementById('volume-pill').style.display = cfg.normalizeVolume ? 'inline-flex' : 'none';

      // Library settings
      _libraryScanFolders = cfg.libraryScanFolders || [];
      renderLibraryFolders();
      document.getElementById('s-library-extensions').value = (cfg.libraryAllowedExtensions || ['mp3','m4a','wav','flac','ogg','aac','wma','opus']).join(', ');
      document.getElementById('s-library-auto-scan').checked = cfg.libraryAutoScanOnStartup ?? false;
      document.getElementById('s-library-auto-add-downloads').checked = cfg.libraryAutoAddDownloads ?? true;
      document.getElementById('s-library-remove-missing').checked = cfg.libraryRemoveMissingOnScan ?? false;
      document.getElementById('s-library-recommendations').checked = cfg.libraryRecommendationsEnabled ?? true;
      document.getElementById('s-log-playback').checked = cfg.logPlayback ?? false;
      document.getElementById('s-autostart').checked = cfg.autoStart ?? false;
      document.getElementById('s-start-minimized').checked = cfg.startMinimized ?? false;
      document.getElementById('s-minimize-tray').checked = cfg.minimizeToTray ?? false;
      document.getElementById('s-close-tray').checked = cfg.closeToTray ?? false;
      const trayOk = cfg.traySupported !== false;
      const autoOk = cfg.autoStartSupported !== false;
      document.getElementById('s-tray-hint').style.display = trayOk ? 'none' : '';
      document.getElementById('s-minimize-tray').disabled = !trayOk;
      document.getElementById('s-close-tray').disabled = !trayOk;
      document.getElementById('s-autostart-hint').style.display = autoOk ? 'none' : '';
      document.getElementById('s-autostart').disabled = !autoOk;
      const hotkeyOk = cfg.globalShortcutsSupported !== false;
      document.getElementById('s-global-hotkeys').checked = cfg.globalKeyboardShortcuts ?? false;
      document.getElementById('s-global-hotkeys-hint').style.display = hotkeyOk ? 'none' : '';
      document.getElementById('s-global-hotkeys').disabled = !hotkeyOk;
      const mediaOk = cfg.systemMediaSupported === true;
      document.getElementById('s-system-media').checked = cfg.systemMediaIntegration ?? false;
      document.getElementById('s-system-media').disabled = !mediaOk;
      document.getElementById('s-system-media-hint').style.display = mediaOk ? 'none' : '';
      document.getElementById('s-system-media-status').textContent =
        mediaOk ? ('Backend: ' + (cfg.systemMediaBackend || 'unknown') + (cfg.systemMediaActive ? ' (active)' : '')) : '';

      // Pre-select the browser remembered from a previous cookie import.
      await ensureAuthBrowsers();
      selectAuthBrowser(cfg.cookieBrowser || '');

      statusEl.textContent = 'Loaded.'; loadData();
      loadLibraryStats();
    } catch (e) { statusEl.textContent = 'Error: ' + e.message; }
  }

  // Reads a MIDI device select; returns -1 ("no device") when the list is empty,
  // because parseInt('') is NaN and JSON.stringify(NaN) is null, which the host
  // could not read as a number.
  function midiDeviceValue(elementId) {
    const el = document.getElementById(elementId);
    const value = el ? parseInt(el.value, 10) : NaN;
    return Number.isFinite(value) ? value : -1;
  }

  async function saveSettings() {    const statusEl = document.getElementById('settings-status');
    statusEl.textContent = 'Saving...';
    try {
      const settings = {
        fetchingTimer: parseInt(document.getElementById('s-fetching-timer').value) || 10,
        threads: parseInt(document.getElementById('s-threads').value) || 3,
        defaultSorting: document.getElementById('s-default-sorting').value,
        presentationFullscreen: document.getElementById('s-fullscreen').checked,
        normalizeVolume: document.getElementById('s-normalize-volume').checked,
        useCaptionLyricsFallback: document.getElementById('s-caption-fallback').checked,
        enableAnnouncements: document.getElementById('s-enable-announcements').checked,
        autoEnqueue: document.getElementById('s-autopilot').checked,
        address: document.getElementById('s-address').value,
        requestUrl: document.getElementById('s-request-url').value,
        bearerToken: document.getElementById('s-token').value,
        defaultSubmitMethod: document.getElementById('s-default-submit-method').value || 'search',
        logPlayback: document.getElementById('s-log-playback').checked,
        autoStart: document.getElementById('s-autostart').checked,
        startMinimized: document.getElementById('s-start-minimized').checked,
        minimizeToTray: document.getElementById('s-minimize-tray').checked,
        closeToTray: document.getElementById('s-close-tray').checked,
        globalKeyboardShortcuts: document.getElementById('s-global-hotkeys').checked,
        systemMediaIntegration: document.getElementById('s-system-media').checked,
        libraryScanFolders: _libraryScanFolders,
        libraryAllowedExtensions: document.getElementById('s-library-extensions').value.split(',').map(function(e){return e.trim()}).filter(function(e){return e}),
        libraryAutoScanOnStartup: document.getElementById('s-library-auto-scan').checked,
        libraryAutoAddDownloads: document.getElementById('s-library-auto-add-downloads').checked,
        libraryRemoveMissingOnScan: document.getElementById('s-library-remove-missing').checked,
        libraryRecommendationsEnabled: document.getElementById('s-library-recommendations').checked
      };
      const r = await send('saveSettings', { settings });
      if (!r.success) { statusEl.textContent = 'Save failed: ' + (r.error || 'unknown'); return; }

      // Remote control config (keybinds, MIDI mappings, MIDI devices)
      const rc = {
        midiEnabled: document.getElementById('r-midi-enabled').checked,
        // parseInt('') is NaN and JSON.stringify(NaN) is null, which the host could not
        // read as a number — send -1 ("no device") instead.
        midiInputDevice: midiDeviceValue('r-midi-input'),
        midiOutputDevice: midiDeviceValue('r-midi-output'),
        keybinds: _remoteKeybinds,
        midis: _remoteMidis
      };
      const rr = await send('saveRemoteConfig', rc);
      if (!rr.success) { statusEl.textContent = 'Remote config save failed: ' + (rr.error || 'unknown'); return; }

      statusEl.textContent = 'Saved successfully.';
      toast('Settings saved', 'success');
      loadData();
      hideSettings();
    } catch (e) { statusEl.textContent = 'Error: ' + e.message; }
  }

  function setSettingsStatus(msg, isError) {
    const el = document.getElementById('settings-status');
    el.textContent = msg;
    if (isError) toast(msg, 'error');
  }

  // ── Remote control settings (keybinds + MIDI) ─────────────────
  const REMOTE_MUSIC_ACTIONS = [
    { id: 'PlayPause', label: 'Play / Pause', midi: 'PlayPause' },
    { id: 'SkipNext', label: 'Skip Next', midi: 'SkipNext' },
    { id: 'Previous', label: 'Previous', midi: 'Previous' },
    { id: 'Stop', label: 'Stop', midi: 'Stop' },
    { id: 'VolumeUp', label: 'Volume Up', midi: 'Volume' },
    { id: 'VolumeDown', label: 'Volume Down', midi: null, midiNote: '(shares Volume MIDI row above)' },
    { id: 'CrossfadeUp', label: 'Crossfade Up', midi: 'Crossfade' },
    { id: 'CrossfadeDown', label: 'Crossfade Down', midi: null, midiNote: '(shares Crossfade MIDI row above)' }
  ];
  const REMOTE_ANNOUNCE_ACTIONS = [
    { id: 'Announcement', label: 'Announcement (Start/Toggle)', midi: 'Announcement' },
    { id: 'AnnouncementPlaySoundToggle', label: 'Toggle pre-announcement sound', midi: 'AnnouncementPlaySoundToggle' },
    { id: 'AnnouncementPushToTalkToggle', label: 'Toggle push-to-talk mode', midi: 'AnnouncementPushToTalkToggle' },
    { id: 'AnnouncementDimDbUp', label: 'Dim level (dB) up', midi: 'AnnouncementDimDb' },
    { id: 'AnnouncementDimDbDown', label: 'Dim level (dB) down', midi: null, midiNote: '(shares Dim MIDI row above)' }
  ];

  let _remoteKeybinds = {};
  let _remoteMidis = {};
  let _midiInputs = [];
  let _midiOutputs = [];

  function loadRemoteConfig(rc) {
    _remoteKeybinds = rc.keybinds || {};
    _remoteMidis = rc.midis || {};
    _midiInputs = rc.inputs || [];
    _midiOutputs = rc.outputs || [];

    document.getElementById('r-midi-enabled').checked = rc.midiEnabled ?? false;
    populateMidiSelect('r-midi-input', _midiInputs, rc.midiInputDevice);
    populateMidiSelect('r-midi-output', _midiOutputs, rc.midiOutputDevice);

    renderRemoteMappings('r-music-mappings', REMOTE_MUSIC_ACTIONS);
    renderRemoteMappings('r-announce-mappings', REMOTE_ANNOUNCE_ACTIONS);
    refreshMidiStatus();
  }

  function populateMidiSelect(id, devices, selected) {
    const sel = document.getElementById(id);
    let html = '<option value="-1">None</option>';
    for (const d of devices) {
      html += '<option value="' + d.number + '">' + escapeHtml(d.name || ('Device ' + d.number)) + '</option>';
    }
    sel.innerHTML = html;
    sel.value = String(selected ?? -1);
  }

  function midiMappingLabel(m) {
    if (!m) return '—';
    const unit = (m.messageType || '').toLowerCase() === 'controlchange' ? 'CC' : 'Note';
    return escapeHtml(m.messageType) + ' | Ch ' + m.channel + ' | ' + unit + ' ' + m.note;
  }

  function renderRemoteMappings(containerId, actions) {
    const container = document.getElementById(containerId);
    let html = '<div class="rm-header">' +
      '<span class="rm-col-action">Action</span>' +
      '<span class="rm-col-key">Key Bind</span>' +
      '<span class="rm-col-btn"></span><span class="rm-col-btn"></span>' +
      '<span class="rm-col-midi">MIDI Mapping</span>' +
      '<span class="rm-col-btn"></span><span class="rm-col-btn"></span>' +
      '</div>';
    for (const a of actions) {
      const kb = _remoteKeybinds[a.id] || {};
      const hasKb = !!(kb.enabled && kb.gesture);
      let midiHtml = '';
      let midiBtnsHtml = '';
      if (a.midi) {
        const m = _remoteMidis[a.midi];
        midiHtml = m ? midiMappingLabel(m) : '—';
        midiBtnsHtml = '<button class="rm-assign" data-midi-assign="' + a.midi + '">Assign</button>' +
          '<button class="rm-clear" data-midi-clear="' + a.midi + '">Clear</button>';
      } else {
        midiHtml = '<span style="color:#888">' + escapeHtml(a.midiNote || '') + '</span>';
        midiBtnsHtml = '<span class="rm-col-btn"></span><span class="rm-col-btn"></span>';
      }
      html += '<div class="rm-row">' +
        '<span class="rm-col-action">' + escapeHtml(a.label) + '</span>' +
        '<span class="rm-col-key' + (hasKb ? '' : ' rm-unset') + '">' + escapeHtml(hasKb ? kb.gesture : '—') + '</span>' +
        '<button class="rm-assign" data-kb-assign="' + a.id + '">Assign</button>' +
        '<button class="rm-clear" data-kb-clear="' + a.id + '">Clear</button>' +
        '<span class="rm-col-midi' + ((a.midi && _remoteMidis[a.midi]) ? '' : ' rm-unset') + '">' + midiHtml + '</span>' +
        midiBtnsHtml +
        '</div>';
    }
    container.innerHTML = html;
  }

  function rerenderRemoteMappings() {
    renderRemoteMappings('r-music-mappings', REMOTE_MUSIC_ACTIONS);
    renderRemoteMappings('r-announce-mappings', REMOTE_ANNOUNCE_ACTIONS);
  }

  function currentMidiSettings() {
    return {
      enabled: document.getElementById('r-midi-enabled').checked,
      inputDevice: parseInt(document.getElementById('r-midi-input').value, 10),
      outputDevice: parseInt(document.getElementById('r-midi-output').value, 10)
    };
  }

  function applyMidiPreview() {
    send('applyMidiDevices', currentMidiSettings()).catch(() => { });
  }

  function refreshMidiStatus() {
    const enabled = document.getElementById('r-midi-enabled').checked;
    const input = parseInt(document.getElementById('r-midi-input').value, 10);
    const output = parseInt(document.getElementById('r-midi-output').value, 10);
    let text;
    if (!enabled) text = 'MIDI remote control is disabled.';
    else if (input >= 0 && output >= 0) text = 'MIDI input and output connected.';
    else if (input >= 0) text = 'MIDI input connected (no output feedback device selected).';
    else if (output >= 0) text = 'MIDI output connected (no input device selected).';
    else text = 'No MIDI devices selected.';
    document.getElementById('r-midi-status').textContent = text;
  }

  document.querySelectorAll('.settings-remote-grid').forEach(grid => {
    grid.addEventListener('click', e => {
      const btn = e.target.closest('button');
      if (!btn) return;
      if (btn.dataset.kbAssign) openKeybindCapture(btn.dataset.kbAssign);
      else if (btn.dataset.kbClear) {
        _remoteKeybinds[btn.dataset.kbClear] = { enabled: false, gesture: '' };
        rerenderRemoteMappings();
      } else if (btn.dataset.midiAssign) openMidiAssign(btn.dataset.midiAssign);
      else if (btn.dataset.midiClear) {
        _remoteMidis[btn.dataset.midiClear] = null;
        rerenderRemoteMappings();
      }
    });
  });

  ['r-midi-enabled', 'r-midi-input', 'r-midi-output'].forEach(id => {
    document.getElementById(id).addEventListener('change', () => {
      applyMidiPreview();
      refreshMidiStatus();
    });
  });

  document.getElementById('r-test-midi').addEventListener('click', async () => {
    const r = await send('testMidiFeedback').catch(e => ({ error: e.message }));
    if (r && r.success) toast('Test pattern sent - LEDs should flash briefly', 'info');
    else toast(r && r.error ? r.error : 'MIDI test failed', 'error');
  });

  // ── Keybind capture ────────────────────────────────────────────
  function jsKeyToGesture(e) {
    return window.SRKeys ? window.SRKeys.gestureFromEvent(e) : null;
  }

  let _captureTarget = null;
  let _captureGesture = '';

  function openKeybindCapture(action) {
    _captureTarget = action;
    const existing = _remoteKeybinds[action] || {};
    _captureGesture = (existing.enabled && existing.gesture) ? existing.gesture : '';
    document.getElementById('keybind-detected').textContent = _captureGesture || 'No shortcut detected';
    document.getElementById('keybind-capture-modal').style.display = 'flex';
    document.addEventListener('keydown', onCaptureKeyDown, true);
  }

  function closeKeybindCapture() {
    document.getElementById('keybind-capture-modal').style.display = 'none';
    document.removeEventListener('keydown', onCaptureKeyDown, true);
    _captureTarget = null;
  }

  function onCaptureKeyDown(e) {
    const gesture = jsKeyToGesture(e);
    if (!gesture) return;
    e.preventDefault();
    e.stopPropagation();
    _captureGesture = gesture;
    document.getElementById('keybind-detected').textContent = gesture;
  }

  document.getElementById('btn-keybind-apply').addEventListener('click', () => {
    if (!_captureTarget || !_captureGesture) { toast('Press a key combination first.', 'error'); return; }
    _remoteKeybinds[_captureTarget] = { enabled: true, gesture: _captureGesture };
    rerenderRemoteMappings();
    closeKeybindCapture();
  });
  document.getElementById('btn-keybind-cancel').addEventListener('click', closeKeybindCapture);
  document.getElementById('btn-keybind-cancel-x').addEventListener('click', closeKeybindCapture);

  // ── MIDI assignment capture ────────────────────────────────────
  let _midiAssignTarget = null;
  let _midiDetected = null;
  let _midiAssignTimeout = null;

  async function openMidiAssign(midiKey) {
    const input = parseInt(document.getElementById('r-midi-input').value, 10);
    if (input < 0) { toast('Select a MIDI input device first.', 'error'); return; }

    _midiAssignTarget = midiKey;
    _midiDetected = null;
    const existing = _remoteMidis[midiKey];
    document.getElementById('midi-vel-pressed').value = existing ? existing.velocityPressed : 127;
    document.getElementById('midi-vel-unpressed').value = existing ? existing.velocityUnpressed : 0;
    document.getElementById('midi-vel-pressed-label').textContent = document.getElementById('midi-vel-pressed').value;
    document.getElementById('midi-vel-unpressed-label').textContent = document.getElementById('midi-vel-unpressed').value;

    const okBtn = document.getElementById('btn-midi-assign-ok');
    okBtn.disabled = true;
    const status = document.getElementById('midi-assign-status');
    status.style.color = '#ccc';
    status.textContent = existing
      ? ('Current: Channel ' + existing.channel + ', ' + existing.messageType + ' ' + existing.note + '\n\nPress a button on your MIDI controller...')
      : 'Waiting for MIDI input...\n\nPress a button on your MIDI controller.';
    document.getElementById('midi-assign-modal').style.display = 'flex';

    // Temporarily enable + connect the selected input so capture works.
    try {
      await send('applyMidiDevices', {
        enabled: true,
        inputDevice: input,
        outputDevice: parseInt(document.getElementById('r-midi-output').value, 10)
      });
    } catch (e) { }
    try { await send('startMidiCapture'); } catch (e) { }

    _midiAssignTimeout = setTimeout(() => {
      if (!_midiDetected) {
        status.textContent = 'Timeout - No MIDI input detected';
        status.style.color = '#e53935';
      }
    }, 15000);
  }

  async function closeMidiAssign() {
    document.getElementById('midi-assign-modal').style.display = 'none';
    if (_midiAssignTimeout) { clearTimeout(_midiAssignTimeout); _midiAssignTimeout = null; }
    try { await send('stopMidiCapture'); } catch (e) { }
    try { await send('applyMidiDevices', currentMidiSettings()); } catch (e) { }
    _midiAssignTarget = null;
    _midiDetected = null;
  }

  function onMidiInput(data) {
    if (!data || !_midiAssignTarget) return;
    if (document.getElementById('midi-assign-modal').style.display === 'none') return;
    _midiDetected = data;
    if (_midiAssignTimeout) { clearTimeout(_midiAssignTimeout); _midiAssignTimeout = null; }
    const unit = (data.messageType === 'ControlChange') ? 'CC' : 'Value1';
    const status = document.getElementById('midi-assign-status');
    status.style.color = '#4caf50';
    status.textContent = '✓ Detected MIDI ' + data.messageType +
      '\n\nChannel: ' + data.channel +
      '\n' + unit + ': ' + data.value1 +
      '\nValue2: ' + data.value2 +
      '\n\nAdjust feedback values below and click OK to confirm.';
    document.getElementById('btn-midi-assign-ok').disabled = false;
  }

  function onMidiActivity(data) {
    const el = document.getElementById('r-midi-last');
    if (el && data && data.message) el.textContent = data.message;
  }

  document.getElementById('btn-midi-assign-ok').addEventListener('click', () => {
    if (!_midiAssignTarget || !_midiDetected) return;
    _remoteMidis[_midiAssignTarget] = {
      channel: _midiDetected.channel,
      note: _midiDetected.value1,
      messageType: _midiDetected.messageType,
      velocityPressed: parseInt(document.getElementById('midi-vel-pressed').value, 10),
      velocityUnpressed: parseInt(document.getElementById('midi-vel-unpressed').value, 10)
    };
    rerenderRemoteMappings();
    closeMidiAssign();
  });
  document.getElementById('btn-midi-assign-cancel').addEventListener('click', closeMidiAssign);
  document.getElementById('btn-midi-assign-cancel-x').addEventListener('click', closeMidiAssign);
  document.getElementById('midi-assign-modal').addEventListener('click', e => {
    if (e.target === document.getElementById('midi-assign-modal')) closeMidiAssign();
  });
  document.getElementById('midi-vel-pressed').addEventListener('input', function () {
    document.getElementById('midi-vel-pressed-label').textContent = this.value;
  });
  document.getElementById('midi-vel-unpressed').addEventListener('input', function () {
    document.getElementById('midi-vel-unpressed-label').textContent = this.value;
  });

  document.getElementById('btn-settings-close').onclick = hideSettings;
  document.getElementById('btn-settings-save').onclick = saveSettings;
  document.getElementById('btn-settings-reload').onclick = async () => {
    try { await send('reloadConfig'); } catch (e) { }
    loadSettings();
  };

  document.querySelectorAll('.settings-tab').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.settings-tab, .settings-panel').forEach(el => el.classList.remove('active'));
      btn.classList.add('active');
      document.getElementById('settings-' + btn.dataset.tab).classList.add('active');
      if (btn.dataset.tab === 'logging') loadPlayedSongs();
    });
  });

  // Played songs handlers
  document.getElementById('s-played-songs-refresh').onclick = loadPlayedSongs;
  document.getElementById('s-played-songs-clear').onclick = async () => {
    if (!confirm('Clear all played songs history?')) return;
    const btn = document.getElementById('s-played-songs-clear');
    btn.disabled = true; btn.textContent = '...';
    try {
      const r = await send('clearPlayedSongs');
      if (r.success) { loadPlayedSongs(); toast('Played songs cleared'); }
      else setSettingsStatus(r.error || 'Failed', true);
    } catch (e) { setSettingsStatus(e.message, true); }
    btn.disabled = false; btn.innerHTML = '<i class="fas fa-trash"></i> Clear All';
  };

  async function loadPlayedSongs() {
    const listEl = document.getElementById('s-played-songs-list');
    listEl.innerHTML = '<div style="color:#888;font-style:italic">Loading...</div>';
    try {
      const r = await send('getPlayedSongs');
      if (r.success && r.songs && r.songs.length) {
        listEl.innerHTML = r.songs.map(s => {
          const title = s[0] || '', artist = s[1] || '', ts = s[2], platform = s[3] || '';
          const timeStr = ts ? new Date(ts * 1000).toLocaleString() : '';
          return '<div style="padding:4px 0;border-bottom:1px solid #222"><span style="color:#5fa8e0">' + escapeHtml(title) + '</span> <span style="color:#888">&mdash;</span> <span style="color:#aaa">' + escapeHtml(artist) + '</span> <span style="color:#666">[' + escapeHtml(platform) + ']</span>' + (timeStr ? ' <span style="float:right;color:#666;font-size:11px">' + timeStr + '</span>' : '') + '</div>';
        }).join('');
      } else {
        listEl.innerHTML = '<div style="color:#888;font-style:italic">No played songs recorded.</div>';
      }
} catch (e) {
      listEl.innerHTML = '<div style="color:#f55">Error: ' + escapeHtml(e.message) + '</div>';
    }
  }

  document.getElementById('s-toggle-token').onclick = () => {
    const inp = document.getElementById('s-token');
    inp.type = inp.type === 'password' ? 'text' : 'password';
  };

  document.getElementById('s-toggle-sendin').onclick = async () => {
    const btn = document.getElementById('s-toggle-sendin');
    btn.disabled = true;
    btn.textContent = '...';
    try {
      const r = await send('toggleSendin');
      if (r.success) await loadSettings();
      else setSettingsStatus((r.error || 'failed'), true);
    } catch (e) { setSettingsStatus(e.message, true); }
    btn.disabled = false;
    btn.textContent = 'Toggle';
  };

  document.getElementById('s-auth-btn').onclick = () => { send('openAuthUrl'); };

  // YouTube authentication (Security tab)
  const ytStatusEl = document.getElementById('s-yt-status');

  async function ensureAuthBrowsers() {
    if (_authBrowsers.length) return _authBrowsers;
    try {
      const r = await send('getAuthBrowsers');
      _authBrowsers = r.browsers || [];
    } catch { _authBrowsers = []; }

    const select = document.getElementById('s-yt-browser');
    if (select) {
      select.innerHTML = _authBrowsers
        .map(b => '<option value="' + b + '">' + b.charAt(0).toUpperCase() + b.slice(1) + '</option>')
        .join('');
    }
    return _authBrowsers;
  }

  // Pre-select the browser the user last imported cookies from (if still available).
  function selectAuthBrowser(browser) {
    if (!browser || _authBrowsers.indexOf(browser) === -1) return;
    const select = document.getElementById('s-yt-browser');
    if (select) select.value = browser;
  }

  ensureAuthBrowsers();
  document.getElementById('s-yt-browser').addEventListener('change', function () {
    send('saveCookieBrowser', { browser: this.value }).catch(() => { });
  });
  document.getElementById('s-yt-grab').onclick = async () => {
    const browser = document.getElementById('s-yt-browser').value;
    if (!browser) { ytStatusEl.textContent = 'No browser available.'; return; }
    ytStatusEl.innerHTML = '<span class="loading"><i class="fas fa-spinner fa-pulse"></i> Reading cookies from ' + browser + '...</span>';
    try {
      const r = await send('grabCookies', { browser });
      ytStatusEl.textContent = r.success
        ? 'Saved ' + r.cookieCount + ' cookies from ' + browser + '.'
        : 'Failed: ' + (r.error || 'no cookies found');
      if (r.success) await send('saveCookieBrowser', { browser }).catch(() => { });
    } catch (e) { ytStatusEl.textContent = 'Error: ' + e.message; }
  };
  document.getElementById('s-yt-save').onclick = async () => {
    const text = document.getElementById('s-yt-cookies').value;
    if (!text.trim()) { ytStatusEl.textContent = 'Paste some cookies first.'; return; }
    try {
      const r = await send('submitCookies', { text });
      ytStatusEl.textContent = r.success
        ? 'Saved ' + r.cookieCount + ' cookies.'
        : 'Failed: ' + (r.error || 'invalid cookies');
      if (r.success) document.getElementById('s-yt-cookies').value = '';
    } catch (e) { ytStatusEl.textContent = 'Error: ' + e.message; }
  };
  document.getElementById('s-yt-clear').onclick = async () => {
    try {
      const r = await send('clearAuthCookies');
      ytStatusEl.textContent = r.success ? 'Saved cookies cleared.' : 'Failed: ' + (r.error || 'unknown error');
    } catch (e) { ytStatusEl.textContent = 'Error: ' + e.message; }
  };

  // ── External tools (FFmpeg / yt-dlp) ────────────────────────────────
  let _toolProgressEl = null;
  let _toolPromptSelection = null;

  function applyToolStatus(status) {
    const set = (id, entry) => {
      const el = document.getElementById(id);
      if (!el) return;
      const found = entry && entry.found;
      el.textContent = found ? (entry.path || 'Found') : 'Not found';
      el.style.color = found ? '#7ad07a' : '#e08080';
      el.title = (entry && entry.path) || '';
    };
    set('tool-ffmpeg-status', status.ffmpeg);
    set('tool-ffprobe-status', status.ffprobe);
    set('tool-ytdlp-status', status.ytDlp);
    const dir = document.getElementById('tool-tools-dir');
    if (dir) dir.textContent = status.toolsDirectory ? ('Download folder: ' + status.toolsDirectory) : '';
    const dlF = document.getElementById('tool-dl-ffmpeg');
    const dlY = document.getElementById('tool-dl-ytdlp');
    if (dlF && !dlF.dataset.touched) dlF.checked = !(status.audioReady);
    if (dlY && !dlY.dataset.touched) dlY.checked = !(status.ytDlp && status.ytDlp.found);
  }

  async function refreshToolStatus() {
    try { applyToolStatus(await send('getToolStatus')); } catch (e) { /* ignore */ }
  }

  async function downloadTools(ffmpeg, ytDlp, progressEl) {
    _toolProgressEl = progressEl || null;
    if (progressEl) progressEl.textContent = 'Starting…';
    try {
      const r = await send('downloadTools', { ffmpeg: ffmpeg, ytDlp: ytDlp });
      return !!r.success;
    } catch (e) {
      if (progressEl) progressEl.textContent = 'Error: ' + e.message;
      return false;
    }
  }

  function onToolProgress(data) {
    if (!data) return;
    const text = (data.message || data.stage || '') + (data.percent ? ' ' + Math.round(data.percent) + '%' : '');
    const target = _toolProgressEl || document.getElementById('tool-progress');
    if (target) target.textContent = text;
    const prompt = document.getElementById('tool-prompt-progress');
    if (prompt) prompt.textContent = text;
    if (data.stage === 'done' || data.stage === 'error') refreshToolStatus();
  }

  function onToolStatus(data) {
    if (!data || !data.ffmpeg) return;
    applyToolStatus(data);

    const sel = _toolPromptSelection;
    const prompt = document.getElementById('tool-prompt-modal');
    if (sel && prompt && prompt.style.display !== 'none') {
      const okFf = !sel.ff || data.audioReady;
      const okYt = !sel.yt || (data.ytDlp && data.ytDlp.found);
      if (okFf && okYt) { prompt.style.display = 'none'; _toolPromptSelection = null; }
    }
  }

  document.getElementById('tool-refresh').onclick = refreshToolStatus;
  document.getElementById('tool-download').onclick = async () => {
    const f = document.getElementById('tool-dl-ffmpeg').checked;
    const y = document.getElementById('tool-dl-ytdlp').checked;
    const progress = document.getElementById('tool-progress');
    if (!f && !y) { progress.textContent = 'Select at least one tool.'; return; }
    progress.textContent = 'Starting…';
    await downloadTools(f, y, progress);
  };
  ['tool-dl-ffmpeg', 'tool-dl-ytdlp'].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.addEventListener('change', () => { el.dataset.touched = '1'; });
  });
  refreshToolStatus();

  // Startup prompt (only when something is missing and it was not dismissed).
  async function maybePromptTools() {
    try {
      const cfg = await send('getSettings');
      if (cfg.toolPromptDismissed) return;

      const status = await send('getToolStatus');
      const needFf = !status.audioReady;
      const needYt = !(status.ytDlp && status.ytDlp.found);
      if (!needFf && !needYt) return;

      applyToolStatus(status);
      const ff = document.getElementById('tool-prompt-ffmpeg');
      const yt = document.getElementById('tool-prompt-ytdlp');
      ff.checked = needFf; ff.disabled = !needFf;
      yt.checked = needYt; yt.disabled = !needYt;
      document.getElementById('tool-prompt-modal').style.display = 'flex';
    } catch (e) { /* ignore */ }
  }

  document.getElementById('tool-prompt-skip').onclick = async () => {
    document.getElementById('tool-prompt-modal').style.display = 'none';
    _toolPromptSelection = null;
    try { await send('dismissToolPrompt'); } catch (e) { /* ignore */ }
  };

  document.getElementById('tool-prompt-download').onclick = async () => {
    const ff = document.getElementById('tool-prompt-ffmpeg').checked;
    const yt = document.getElementById('tool-prompt-ytdlp').checked;
    if (!ff && !yt) return;
    _toolPromptSelection = { ff: ff, yt: yt };
    const btn = document.getElementById('tool-prompt-download');
    btn.disabled = true;
    await downloadTools(ff, yt, document.getElementById('tool-prompt-progress'));
  };

  setTimeout(maybePromptTools, 1500);

  // Console tools (Console tab)
  function consoleColorClass() {
    return document.getElementById('c-color').value;
  }
  document.getElementById('c-append').addEventListener('click', () => {
    const text = document.getElementById('c-append-text').value;
    if (!text) return;
    log(text, consoleColorClass());
    setSettingsStatus('Appended to console.');
  });
  document.getElementById('c-clear').addEventListener('click', () => {
    document.getElementById('console-output').innerHTML = '';
    setSettingsStatus('Console cleared.');
  });
  document.getElementById('c-apply-color').addEventListener('click', () => {
    const cls = consoleColorClass();
    document.getElementById('console-output').querySelectorAll('div').forEach(d => { d.className = cls; });
    setSettingsStatus('Console color applied.');
  });

  // Auth / server error overlay buttons
  document.getElementById('btn-auth-settings').addEventListener('click', () => {
    showSettings();
    hideAuthOverlay();
  });
  document.getElementById('btn-auth-retry').addEventListener('click', () => {
    hideAuthOverlay();
    loadData();
  });

  document.querySelectorAll('#about-badges img').forEach(img => {
    img.addEventListener('click', function() {
      const url = this.dataset.url;
      if (url) window.open(url, '_blank');
    });
  });

  // Tab switching with directional slide
  const tabOrder = ['all-songs', 'pending', 'blacklist', 'wordfilter', 'dashboard'];
  document.querySelectorAll('#tabbar .tab-btn').forEach(btn => {
    btn.addEventListener('click', function() {
      const prevTab = document.querySelector('#tabbar .tab-btn.active');
      if (prevTab === this) return;
      const prevIdx = prevTab ? tabOrder.indexOf(prevTab.dataset.tab) : -1;
      const newIdx = tabOrder.indexOf(this.dataset.tab);
      const dir = newIdx > prevIdx ? 'left' : 'right';

      document.querySelectorAll('#tabbar .tab-btn').forEach(b => b.classList.remove('active'));
      this.classList.add('active');

      const oldPanel = document.querySelector('.panel.active');
      const newPanel = document.getElementById('panel-' + this.dataset.tab);
      if (!newPanel) return;

      if (oldPanel) {
        oldPanel.style.animation = 'slideOut' + (dir === 'left' ? 'Left' : 'Right') + ' 0.2s ease forwards';
        setTimeout(() => { oldPanel.classList.remove('active'); oldPanel.style.animation = ''; }, 200);
      }

      newPanel.style.animation = 'none';
      void newPanel.offsetHeight;
      newPanel.classList.add('active');
      newPanel.style.animation = 'slideIn' + (dir === 'left' ? 'Right' : 'Left') + ' 0.2s ease';

      if (this.dataset.tab === 'wordfilter') loadWordFilter();
    });
  });

  // Search / sort for all song lists
  document.querySelectorAll('.search-input').forEach(el => {
    el.addEventListener('input', () => renderSongList(el.dataset.list));
  });
  document.querySelectorAll('.sort-select').forEach(el => {
    el.addEventListener('change', () => renderSongList(el.dataset.list));
  });

  // Library scan folders
  let _libraryScanFolders = [];

  function renderLibraryFolders() {
    var container = document.getElementById('s-library-folders-list');
    if (!_libraryScanFolders.length) {
      container.innerHTML = '<div class="settings-folder-empty">No scan folders configured.</div>';
      return;
    }
    var html = '';
    for (var i = 0; i < _libraryScanFolders.length; i++) {
      html += '<div class="settings-folder-item"><span class="folder-path">' + escapeHtml(_libraryScanFolders[i]) + '</span><button class="folder-remove" data-index="' + i + '" title="Remove folder"><i class="fas fa-times"></i></button></div>';
    }
    container.innerHTML = html;
    container.querySelectorAll('.folder-remove').forEach(function(btn) {
      btn.addEventListener('click', function() {
        _libraryScanFolders.splice(parseInt(this.dataset.index), 1);
        renderLibraryFolders();
      });
    });
  }

  document.getElementById('s-library-folder-add').addEventListener('click', function() {
    var input = document.getElementById('s-library-folder-input');
    var val = input.value.trim();
    if (!val) return;
    if (_libraryScanFolders.indexOf(val) !== -1) return;
    _libraryScanFolders.push(val);
    input.value = '';
    renderLibraryFolders();
  });

  document.getElementById('s-library-folder-input').addEventListener('keydown', function(e) {
    if (e.key === 'Enter') document.getElementById('s-library-folder-add').click();
  });

  // Library scan controls
  function onLibraryScanProgress(data) {
    var pct = Math.round((data.progress || 0) * 100);
    var wrap = document.getElementById('s-library-scan-progress');
    var bar = document.getElementById('s-library-scan-bar');
    var pctEl = document.getElementById('s-library-scan-pct');
    var statusEl = document.getElementById('s-library-scan-status');
    wrap.style.display = '';
    bar.style.width = pct + '%';
    pctEl.textContent = pct + '%';
    statusEl.textContent = data.message || 'Scanning...';
    document.getElementById('s-library-scan-btn').disabled = true;
    document.getElementById('s-library-cancel-scan-btn').style.display = '';
  }

  function onLibraryScanCompleted(data) {
    var wrap = document.getElementById('s-library-scan-progress');
    var bar = document.getElementById('s-library-scan-bar');
    var statusEl = document.getElementById('s-library-scan-status');
    var pctEl = document.getElementById('s-library-scan-pct');
    var resultEl = document.getElementById('s-library-scan-result');

    bar.style.width = '100%';
    pctEl.textContent = '100%';
    statusEl.textContent = 'Scan complete';

    var errors = data.errorCount || 0;
    var cls = errors > 0 ? 'color:#fa0;background:#2a2200' : 'color:#4caf50;background:#1a2a1a';
    var text = 'Found ' + (data.filesFound || 0) + ' files \u2022 +' + (data.songsAdded || 0) + ' added \u2022 ' + (data.songsUpdated || 0) + ' updated \u2022 ' + (data.songsMissing || 0) + ' missing';
    if (errors > 0) text += ' \u2022 ' + errors + ' error(s)';

    resultEl.style.display = '';
    resultEl.style.cssText = 'display:block;font-size:12px;padding:8px 10px;border-radius:6px;margin-bottom:8px;' + cls;
    resultEl.textContent = text;

    log('Library scan complete: ' + text, errors > 0 ? 'warn' : 'success');

    // Log errors to console
    if (data.errors && data.errors.length > 0) {
      data.errors.forEach(function(err) { log('Library scan error: ' + err, 'error'); });
    }

    document.getElementById('s-library-scan-btn').disabled = false;
    document.getElementById('s-library-cancel-scan-btn').style.display = 'none';

    setTimeout(function() { wrap.style.display = 'none'; }, 5000);
    loadLibraryStats();
  }

  document.getElementById('s-library-scan-btn').addEventListener('click', async function() {
    var btn = this;
    var cancelBtn = document.getElementById('s-library-cancel-scan-btn');
    btn.disabled = true;
    btn.innerHTML = '<i class="fas fa-spinner fa-pulse"></i> Scanning...';
    document.getElementById('s-library-scan-result').style.display = 'none';
    log('Library scan started', 'info');
    setStatus('Scanning library...');
    try {
      var result = await send('scanLibrary');
      if (result.error) {
        log('Library scan failed: ' + result.error, 'error');
        toast('Scan failed: ' + result.error, 'error');
      }
    } catch (e) {
      log('Library scan failed: ' + e.message, 'error');
      toast('Scan failed: ' + e.message, 'error');
    }
    btn.disabled = false;
    btn.innerHTML = '<i class="fas fa-sync-alt"></i> Scan Library';
    cancelBtn.style.display = 'none';
    document.getElementById('s-library-scan-progress').style.display = 'none';
    setStatus('Ready');
    loadLibraryStats();
  });

  document.getElementById('s-library-cancel-scan-btn').addEventListener('click', function() {
    send('cancelLibraryScan');
    log('Library scan cancelled', 'warn');
    toast('Scan cancelled', 'info');
    document.getElementById('s-library-scan-btn').disabled = false;
    document.getElementById('s-library-scan-btn').innerHTML = '<i class="fas fa-sync-alt"></i> Scan Library';
    this.style.display = 'none';
    document.getElementById('s-library-scan-progress').style.display = 'none';
  });

  async function loadLibraryStats() {
    try {
      var stats = await send('getLibraryStats');
      var el = document.getElementById('s-library-stats');
      if (!stats || stats.error) {
        el.textContent = 'No library loaded yet.';
        return;
      }
      var parts = [];
      parts.push(stats.presentSongs + ' song' + (stats.presentSongs !== 1 ? 's' : ''));
      if (stats.missingSongs > 0) parts.push(stats.missingSongs + ' missing');
      if (stats.hashDuplicates > 0) parts.push(stats.hashDuplicates + ' hash dup');
      if (stats.metadataDuplicates > 0) parts.push(stats.metadataDuplicates + ' meta dup');
      parts.push('Last scan: ' + (stats.lastScanUtc ? new Date(stats.lastScanUtc).toLocaleString() : 'never'));
      el.textContent = parts.join(' \u2022 ');
    } catch (e) { /* ignore */ }
  }

  function escapeHtml(str) {
    var d = document.createElement('div');
    d.appendChild(document.createTextNode(str));
    return d.innerHTML;
  }

  // Submit Song modal
  const submitOverlay = document.getElementById('modal-overlay');
  let _defaultSubmitMethod = 'search';
  document.getElementById('btn-submit').addEventListener('click', () => {
    submitOverlay.style.display = 'flex';
    switchSubmitMode(_defaultSubmitMethod);
    if (_defaultSubmitMethod === 'url') document.getElementById('submit-url').focus();
    else document.getElementById('submit-search-input').focus();
  });
  submitOverlay.querySelector('.modal-close').addEventListener('click', () => {
    submitOverlay.style.display = 'none';
  });
  submitOverlay.addEventListener('click', (e) => {
    if (e.target === submitOverlay) submitOverlay.style.display = 'none';
  });
  document.getElementById('btn-submit-send').addEventListener('click', doSubmitSong);
  document.getElementById('submit-url').addEventListener('keydown', e => {
    if (e.key === 'Enter') doSubmitSong();
  });
  document.getElementById('submit-message').addEventListener('keydown', e => {
    if (e.key === 'Enter') doSubmitSong();
  });

  // Submit modal mode switching
  document.querySelectorAll('.modal-tab-btn').forEach(btn => {
    btn.addEventListener('click', function() {
      switchSubmitMode(this.dataset.mode);
    });
  });
  document.getElementById('btn-submit-search').addEventListener('click', doYoutubeSearch);
  document.getElementById('btn-submit-search-send').addEventListener('click', doSubmitSong);
  document.getElementById('submit-search-input').addEventListener('keydown', e => {
    if (e.key === 'Enter') doYoutubeSearch();
  });

  let _selectedSearchResult = null;

  function switchSubmitMode(mode) {
    document.querySelectorAll('.modal-tab-btn').forEach(b => b.classList.remove('active'));
    document.querySelector('.modal-tab-btn[data-mode="' + mode + '"]').classList.add('active');
    document.getElementById('submit-url-mode').style.display = mode === 'url' ? 'block' : 'none';
    document.getElementById('submit-search-mode').style.display = mode === 'search' ? 'block' : 'none';
    _selectedSearchResult = null;
    if (mode === 'url') {
      document.getElementById('submit-url').focus();
    } else {
      document.getElementById('submit-search-input').focus();
    }
  }

  async function doYoutubeSearch() {
    const query = document.getElementById('submit-search-input').value.trim();
    if (!query) return;
    const status = document.getElementById('submit-search-status');
    const resultsContainer = document.getElementById('submit-search-results');
    status.textContent = 'Searching...';
    resultsContainer.innerHTML = '';
    _selectedSearchResult = null;
    try {
      const result = await send('searchYoutube', { query });
      if (result.error) {
        status.textContent = 'Error: ' + result.error;
        return;
      }
      const items = result.results || [];
      if (items.length === 0) {
        status.textContent = 'No results found';
        return;
      }
      status.textContent = 'Found ' + items.length + ' results. Click one to select, then Send.';
      for (const item of items) {
        const el = document.createElement('div');
        el.className = 'search-result-item';
        el.dataset.videoId = item.videoId;
        el.innerHTML =
          '<div class="search-result-info">' +
          '<div class="search-result-title">' + escapeHtml(item.title) + '</div>' +
          '<div class="search-result-author">' + escapeHtml(item.author) + ' &middot; ' + escapeHtml(item.duration) + '</div>' +
          '</div>';
        el.addEventListener('click', function() {
          document.querySelectorAll('.search-result-item').forEach(i => i.classList.remove('selected'));
          this.classList.add('selected');
          _selectedSearchResult = this.dataset.videoId;
        });
        resultsContainer.appendChild(el);
      }
    } catch (e) {
      status.textContent = 'Search failed: ' + e.message;
    }
  }

  // Auto-refresh (fallback)
  setInterval(() => { loadData(); loadWordFilter(); }, 30000);

  // Top actions
  document.getElementById('btn-music-player').addEventListener('click', () => send('showMusicPlayer'));
  document.getElementById('btn-soundboard').addEventListener('click', () => send('showSoundboard'));
  document.getElementById('btn-settings').addEventListener('click', showSettings);

  // Word filter
  document.getElementById('btn-wordfilter-add').addEventListener('click', addBadWord);
  document.getElementById('wordfilter-input').addEventListener('keydown', e => {
    if (e.key === 'Enter') addBadWord();
  });

  // Refresh
  document.getElementById('btn-refresh').addEventListener('click', loadData);

  // Quit (main page and the loading/launch/auth steps). Closing the window is not
  // always an exit — close-to-tray hides it — so there is an explicit way out.
  function quitApplication() {
    try { send('quit'); } catch { /* the host closes the process anyway */ }
  }
  document.getElementById('btn-quit').addEventListener('click', quitApplication);
  document.getElementById('startup-quit').addEventListener('click', quitApplication);

  // Main data loading
  async function loadData() {
    if (_fetchingLock) {
      // Force-reset if stale (>35 seconds)
      if (_fetchStartTime && Date.now() - _fetchStartTime > 35000) {
        _fetchingLock = false;
        log('Stale fetch lock reset', 'warn');
      } else {
        log('Fetch already in progress, skipping...', 'info');
        return;
      }
    }
    _fetchingLock = true;
    _fetchStartTime = Date.now();
    _fetchingLock = true;
    setStatus('Fetching data...');
    log('Fetching data from server...', 'info');
    try {
      const result = await send('fetchData');
      database = result.database || [];
      blacklist = result.blacklist || [];
      updateStats();
      requestAnimationFrame(() => {
        renderSongList('all-songs');
        renderSongList('pending');
        renderSongList('blacklist');
      });
      setStatus('Ready - ' + database.length + ' videos loaded');
      log('Loaded ' + database.length + ' videos, ' + blacklist.length + ' blacklisted', 'success');
      setConnectionStatus('connected', 'Connected');
    } catch (e) {
      setStatus('Error: ' + e.message);
      log('Failed to fetch data: ' + e.message, 'error');
      toast('Failed to fetch data: ' + e.message, 'error');
      setConnectionStatus('disconnected', 'Disconnected');
    } finally {
      _fetchingLock = false;
    }
    // Config fetch is optional; don't let it block subsequent loads
    try {
      const cfgResult = await send('config');
      if (cfgResult) {
        log('Config loaded', 'success');
        const badge = document.getElementById('autopilot-badge');
        if (badge) badge.style.display = cfgResult.autoEnqueue ? 'inline-flex' : 'none';
        const vpill = document.getElementById('volume-pill');
        if (vpill) vpill.style.display = cfgResult.normalizeVolume ? 'inline-flex' : 'none';
      }
    } catch (e) {
      log('Config fetch failed: ' + e.message, 'info');
    }
  }

  function updateStats() {
    const total = database.length;
    const approved = database.filter(v => v.isApproved).length;
    const p = total - approved;
    document.getElementById('stat-total').textContent = total;
    document.getElementById('stat-approved').textContent = approved;
    document.getElementById('stat-pending').textContent = p;
    document.getElementById('stat-blacklisted').textContent = blacklist.length;
    document.getElementById('stat-badwords').textContent = badWords.length;
    // Library stats
    send('getLibraryStats').then(function(stats) {
      if (stats && !stats.error) {
        document.getElementById('stat-library').textContent = stats.presentSongs || 0;
      }
    }).catch(function() {});
  }

  function renderSongList(listName) {
    const container = document.getElementById('list-' + listName);
    const searchInput = document.querySelector('.search-input[data-list="' + listName + '"]');
    const sortSelect = document.querySelector('.sort-select[data-list="' + listName + '"]');
    if (!container || !searchInput || !sortSelect) return;

    const search = searchInput.value.toLowerCase();
    const sort = sortSelect.value;

    let videos;
    if (listName === 'pending') {
      videos = database.filter(v => !v.isApproved && !blacklist.some(b => b.ytid === v.ytid));
    } else if (listName === 'blacklist') {
      videos = blacklist.slice();
    } else {
      videos = database;
    }

    if (search) {
      videos = videos.filter(v =>
        (v.title || '').toLowerCase().includes(search) ||
        (v.creator || '').toLowerCase().includes(search) ||
        (v.ytid || '').toLowerCase().includes(search)
      );
    }

    videos.sort((a, b) => {
      const cmp = sort.includes('asc') ? 1 : -1;
      if (sort.startsWith('title')) return (a.title || '').localeCompare(b.title || '') * cmp;
      if (sort.startsWith('creator')) return (a.creator || '').localeCompare(b.creator || '') * cmp;
      return ((a.sentInTimestamp || 0) - (b.sentInTimestamp || 0)) * cmp;
    });

    const fragment = document.createDocumentFragment();
    for (const v of videos) {
      const card = document.createElement('div');
      const hasTitle = v.title !== v.ytid && v.title;
      card.className = 'song-card' + (hasTitle ? '' : ' is-loading');
      card.dataset.ytid = v.ytid;

      const thumbWrap = document.createElement('div');
      thumbWrap.className = 'thumb-wrap' + (thumbCache[v.ytid] ? '' : ' is-loading');
      const placeholder = document.createElement('span');
      placeholder.className = 'thumb-placeholder';
      placeholder.innerHTML = '<i class="fas fa-music"></i>';
      thumbWrap.appendChild(placeholder);
      const img = document.createElement('img');
      img.alt = '';
      if (thumbCache[v.ytid]) {
        img.src = thumbCache[v.ytid];
        thumbWrap.classList.add('is-loaded');
      } else {
        img.style.opacity = '0';
        img.onload = () => { img.style.opacity = '1'; thumbWrap.classList.remove('is-loading'); thumbWrap.classList.add('is-loaded'); };
        loadThumbnail(v.ytid, img);
      }
      thumbWrap.appendChild(img);

      const info = document.createElement('div');
      info.className = 'info';
      if (!hasTitle) {
        info.innerHTML = '<div class="skeleton-line"></div><div class="skeleton-line"></div>';
      } else {
        let infoHtml = '<div class="stitle">' + escapeHtml(v.title || 'Unknown') + '</div>' +
          (v.creator ? '<div class="screator">' + escapeHtml(v.creator) + '</div>' : '<div class="skeleton-line"></div>');
        const dur = formatDuration(v.durationTicks);
        const ts = formatTimestamp(v.sentInTimestamp);
        if (dur || ts) {
          infoHtml += '<div class="smeta">' +
            (dur ? '<span class="sduration"><i class="fas fa-clock"></i> ' + dur + '</span>' : '') +
            (ts ? '<span class="stimestamp"><i class="fas fa-calendar-alt"></i> ' + ts + '</span>' : '') +
            '</div>';
        }
        if (v.message) {
          infoHtml += '<div class="smessage"><i class="fas fa-comment"></i> ' + escapeHtml(v.message) + '</div>';
        }
        info.innerHTML = infoHtml;
      }

      const label = getStatusLabel(v);
      const status = document.createElement('span');
      status.className = 'sstatus status-' + label;
      status.innerHTML = getStatusIcon(label) + ' ' + label;

      const actions = document.createElement('div');
      actions.className = 'sactions';
      actions.innerHTML =
        '<button class="btn-queue" data-action="queueSong" data-ytid="' + v.ytid + '"><i class="fas fa-list"></i> Queue</button>' +
        '<button class="btn-playnext" data-action="playNext" data-ytid="' + v.ytid + '"><i class="fas fa-forward"></i> Play Next</button>' +
        (label === 'approved'
          ? '<button class="btn-unapprove" data-action="unapprove" data-ytid="' + v.ytid + '"><i class="fas fa-times-circle"></i> Unapprove</button>'
          : '<button class="btn-approve" data-action="approve" data-ytid="' + v.ytid + '"><i class="fas fa-check-circle"></i> Approve</button>') +
        '<button class="btn-delete" data-action="delete" data-ytid="' + v.ytid + '"><i class="fas fa-trash-alt"></i> Delete</button>' +
        (label === 'blacklisted'
          ? '<button class="btn-unblacklist" data-action="unblacklist" data-ytid="' + v.ytid + '"><i class="fas fa-undo"></i> Unblacklist</button>'
          : '<button class="btn-blacklist" data-action="blacklist" data-ytid="' + v.ytid + '"><i class="fas fa-ban"></i> Blacklist</button>');
      actions.addEventListener('click', handleSongAction);

      card.appendChild(thumbWrap);
      card.appendChild(info);
      card.appendChild(status);
      card.appendChild(actions);
      fragment.appendChild(card);
    }
    container.replaceChildren(fragment);

    // Re-apply active download progress states to newly rendered cards
    for (const [vid, st] of Object.entries(downloadStates)) {
      if (Date.now() - st.time > 30000) continue;
      const card = container.querySelector('.song-card[data-ytid="' + vid + '"]');
      if (!card) continue;
      const info = card.querySelector('.info');
      if (!info) continue;

      const progWrap = document.createElement('div');
      progWrap.className = 'dl-progress';
      const bar = document.createElement('div');
      bar.className = 'dl-bar';
      const fill = document.createElement('div');
      fill.className = 'dl-fill';
      fill.style.width = Math.min(st.progress || 0, 100) + '%';
      const colorMap = { metadata: '#5fa8e0', downloading: '#4caf50', thumbnail: '#e0a85f', subtitles: '#a85fe0' };
      fill.style.background = colorMap[st.stage] || '#888';
      bar.appendChild(fill);
      const label = document.createElement('div');
      label.className = 'dl-label';
      const iconMap = { metadata: 'fa-tag', downloading: 'fa-download', thumbnail: 'fa-image', subtitles: 'fa-closed-captioning' };
      const icon = iconMap[st.stage] || 'fa-spinner fa-spin';
      const color = colorMap[st.stage] || '#888';
      label.innerHTML = '<i class="fas ' + icon + '" style="color:' + color + '"></i> ' + escapeHtml(st.message || st.stage);
      progWrap.appendChild(bar);
      progWrap.appendChild(label);
      info.appendChild(progWrap);
    }
  }

  function loadThumbnail(videoId, imgEl) {
    if (thumbCache[videoId]) {
      imgEl.src = thumbCache[videoId];
      imgEl.closest('.thumb-wrap')?.classList.add('is-loaded');
      return;
    }
    send('getThumbnail', { videoId }).then(result => {
      if (result && result.data) {
        const url = 'data:' + (result.mime || 'image/png') + ';base64,' + result.data;
        thumbCache[videoId] = url;
        imgEl.src = url;
      } else {
        imgEl.style.opacity = '1';
        imgEl.closest('.thumb-wrap')?.classList.remove('is-loading');
        imgEl.closest('.thumb-wrap')?.classList.add('is-loaded');
      }
    }).catch(() => {
      imgEl.style.opacity = '1';
      imgEl.closest('.thumb-wrap')?.classList.remove('is-loading');
      imgEl.closest('.thumb-wrap')?.classList.add('is-loaded');
    });
  }

  function handleSongAction(e) {
    const btn = e.target.closest('button');
    if (!btn) return;
    const action = btn.dataset.action;
    const ytid = btn.dataset.ytid;
    if (!action || !ytid) return;
    doAction(action, ytid);
  }

  function doAction(action, ytid) {
    if (action === 'queueSong' || action === 'playNext') {
      setStatus('Checking ' + ytid + '...');
      send('isFileDownloaded', { ytid }).then(res => {
        if (res && res.downloaded) {
          // File exists — send direct queue/playNext
          const method = action === 'queueSong' ? 'queueSong' : 'playNext';
          setStatus((action === 'queueSong' ? 'Queuing' : 'Playing next') + ' ' + ytid + '...');
          return send(method, { ytid });
        } else {
          // Not downloaded — trigger full download pipeline
          const act = action === 'playNext' ? 'playNext' : 'queue';
          setStatus('Downloading ' + ytid + '...');
          return send('downloadAndProcess', { ytid, action: act });
        }
      }).then(() => {
        const msg = action === 'queueSong' ? 'Queued' : 'Playing Next';
        toast(msg + ' ' + ytid, 'success');
        log(msg + ' ' + ytid, 'success');
        loadData();
      }).catch(e => {
        toast('Failed to ' + action + ': ' + e.message, 'error');
        log('Failed to ' + action + ': ' + e.message, 'error');
      });
      return;
    }

    const actionMap = {
      'approve': { method: 'approve', msg: 'Approved' },
      'unapprove': { method: 'unapprove', msg: 'Unapproved' },
      'delete': { method: 'delete', msg: 'Deleted' },
      'blacklist': { method: 'blacklist', msg: 'Blacklisted' },
      'unblacklist': { method: 'unblacklist', msg: 'Unblacklisted' }
    };
    const entry = actionMap[action];
    if (!entry) return;
    setStatus(entry.msg + ' ' + ytid + '...');
    send(entry.method, { ytid }).then(() => {
      toast(entry.msg + ' ' + ytid, 'success');
      log(entry.msg + ' ' + ytid, 'success');
      loadData();
    }).catch(e => {
      toast('Failed to ' + action + ': ' + e.message, 'error');
      log('Failed to ' + action + ': ' + e.message, 'error');
    });
  }

  async function loadWordFilter() {
    try {
      const result = await send('getBadWords');
      badWords = result.words || [];
      renderWordFilter();
    } catch (e) {
      toast('Failed to load bad words: ' + e.message, 'error');
    }
  }

  function renderWordFilter() {
    const list = document.getElementById('wordfilter-list');
    list.innerHTML = '';
    for (const w of badWords) {
      const li = document.createElement('li');
      li.innerHTML = '<span>' + escapeHtml(w) + '</span><button data-word="' + escapeHtml(w) + '"><i class="fas fa-trash-alt"></i> Delete</button>';
      list.appendChild(li);
    }
    list.addEventListener('click', e => {
      const btn = e.target.closest('button');
      if (!btn) return;
      const word = btn.dataset.word;
      if (!word) return;
      send('deleteBadWord', { word }).then(() => {
        toast('Deleted bad word', 'success');
        loadWordFilter();
        loadData();
      }).catch(e => toast('Failed: ' + e.message, 'error'));
    });
  }

  async function addBadWord() {
    const input = document.getElementById('wordfilter-input');
    const word = input.value.trim().toLowerCase();
    if (!word) return;
    input.value = '';
    try {
      await send('addBadWord', { word });
      toast('Added bad word: ' + word, 'success');
      loadWordFilter();
      loadData();
    } catch (e) {
      toast('Failed to add: ' + e.message, 'error');
    }
  }

  function extractVideoId(input) {
    const trimmed = input.trim();
    if (!trimmed) return '';
    const m = trimmed.match(/^(?:https?:\/\/)?(?:www\.)?(?:youtube\.com\/(?:watch\?v=|embed\/|v\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})/);
    if (m) return m[1];
    if (/^[a-zA-Z0-9_-]{11}$/.test(trimmed)) return trimmed;
    return trimmed;
  }

  async function doSubmitSong() {
    const urlMode = document.getElementById('submit-url-mode').style.display !== 'none';
    const urlStatus = document.getElementById('submit-status');
    const searchStatus = document.getElementById('submit-search-status');
    let videoId, message;
    if (urlMode) {
      const urlInput = document.getElementById('submit-url');
      const messageInput = document.getElementById('submit-message');
      videoId = extractVideoId(urlInput.value);
      message = messageInput.value.trim();
      if (!videoId) { urlStatus.textContent = 'Enter a YouTube URL'; return; }
    } else {
      if (!_selectedSearchResult) {
        searchStatus.textContent = 'Select a search result first';
        return;
      }
      videoId = _selectedSearchResult;
      message = '[Admin] Added via search';
    }
    const status = urlMode ? urlStatus : searchStatus;
    status.textContent = 'Submitting...';
    try {
      const result = await send('submitSong', { videoId, message });
      if (result.error) {
        status.textContent = 'Error: ' + result.error;
        toast('Submit failed: ' + result.error, 'error');
      } else {
        status.textContent = 'Song submitted!';
        toast('Song submitted successfully!', 'success');
        log('Submitted song: ' + videoId, 'success');
        document.getElementById('submit-url').value = '';
        document.getElementById('submit-message').value = '';
        document.getElementById('submit-search-input').value = '';
        document.getElementById('submit-search-results').innerHTML = '';
        document.getElementById('submit-search-status').textContent = '';
        _selectedSearchResult = null;
        submitOverlay.style.display = 'none';
        loadData();
      }
    } catch (e) {
      status.textContent = 'Error: ' + e.message;
      toast('Submit failed: ' + e.message, 'error');
    }
  }

  function escapeHtml(str) {
    if (!str) return '';
    return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  // Startup animations
  function runStartupAnim() {
    const els = document.querySelectorAll('.startup-hide');
    els.forEach((el, i) => {
      setTimeout(() => el.classList.add('startup-show'), 80 * i);
    });
  }

  // Populate version badge
  send('getVersion').then(r => {
    const el = document.getElementById('version-badge');
    if (el) el.textContent = 'v' + (r.version || '?');
  }).catch(() => {});

  // Initial load
  loadData();
  loadWordFilter();
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', runStartupAnim);
  } else {
    runStartupAnim();
  }

  async function runStartupSequence() {
    const overlay = document.getElementById('startup-overlay');
    const splash = document.getElementById('startup-splash');
    const updatePanel = document.getElementById('startup-update-panel');
    const launchPanel = document.getElementById('startup-launch-panel');
    const authPanel = document.getElementById('startup-auth-panel');
    const statusEl = document.getElementById('startup-status');
    const progressFill = document.getElementById('startup-progress-fill');
    const versionEl = document.getElementById('startup-version');
    const shimmer = document.getElementById('startup-shimmer');

    // Shimmer animation
    let shimmerPos = -80;
    setInterval(() => {
      shimmerPos += 3;
      if (shimmerPos > overlay.offsetWidth + 80) shimmerPos = -80;
      shimmer.style.transform = 'translateX(' + shimmerPos + 'px)';
    }, 16);

    function setProgress(pct, status) {
      progressFill.style.width = pct + '%';
      if (status) statusEl.textContent = status;
    }

    function hideAll() {
      splash.style.display = 'none';
      updatePanel.style.display = 'none';
      launchPanel.style.display = 'none';
      authPanel.style.display = 'none';
    }

    function showPanel(panel) {
      hideAll();
      panel.style.display = 'flex';
    }

    // Get startup data
    let data;
    try { data = await send('getStartupData'); } catch (e) { data = { version: '?' }; }
    versionEl.textContent = 'Version ' + (data.version || '?');

    // Loading phase: progress to 35%, then check for updates
    setProgress(5, 'Initializing...');
    await delay(400);
    setProgress(15, 'Loading configuration...');
    await delay(400);
    setProgress(30, 'Checking for updates...');
    await delay(300);

    // Update check at ~35%
    let updateInfo = null;
    try {
      updateInfo = await send('checkForUpdates');
    } catch (e) { /* silently fail */ }

    if (updateInfo && updateInfo.updateAvailable) {
      // Pause loading, show update prompt
      showPanel(updatePanel);
      document.getElementById('startup-update-info').textContent =
        'Version ' + updateInfo.latestVersion + ' available (current: ' + updateInfo.currentVersion + ')';
      var startupNotes = document.getElementById('startup-update-notes');
      if (startupNotes) {
        var notesHtml = renderMarkdown(updateInfo.releaseNotes || '');
        startupNotes.innerHTML = notesHtml;
        startupNotes.style.display = notesHtml ? '' : 'none';
      }

      await new Promise(resolve => {
        document.getElementById('startup-update-later').onclick = resolve;
        document.getElementById('startup-update-now').onclick = async () => {
          document.getElementById('startup-update-buttons').style.display = 'none';
          document.getElementById('startup-update-progress').style.display = 'block';
          document.getElementById('startup-update-progress-text').textContent = 'Starting download...';
          try {
            const r = await send('installUpdate', { downloadUrl: updateInfo.downloadUrl });
            if (r && r.error) {
              document.getElementById('startup-update-progress-text').textContent = 'Error: ' + r.error;
              document.getElementById('startup-update-buttons').style.display = 'flex';
            }
            // On success the update runs in the background; the app restarts itself,
            // so we intentionally stay on this panel and keep the sequence paused.
          } catch (e) {
            document.getElementById('startup-update-progress-text').textContent = 'Error: ' + e.message;
            document.getElementById('startup-update-buttons').style.display = 'flex';
          }
        };
      });
    }

    // Resume loading
    showPanel(splash);
    setProgress(50, 'Preparing launch options...');
    await delay(500);
    setProgress(70, 'Almost ready...');
    await delay(500);
    setProgress(85, 'Starting...');
    await delay(300);
    setProgress(100, 'Ready!');

    // Launch options phase
    const launchState = data.launchOptions || { rememberSelection: false, lastSelectedMode: 'SongRequests' };
    let selectedMode = launchState.lastSelectedMode;

    if (launchState.rememberSelection) {
      // Auto-select with 3-second countdown
      showPanel(launchPanel);
      const modeLabel = { SongRequests:'Song Requests', MusicPlayer:'Music Player', MusicShare:'Music Share', Soundboard:'Soundboard' }[selectedMode] || 'Song Requests';
      document.getElementById('startup-launch-auto-mode').textContent = modeLabel;
      document.getElementById('startup-launch-remember-cb').checked = true;

      // Pre-select the remembered button
      document.querySelectorAll('.startup-launch-btn').forEach(b => b.classList.remove('active'));
      const rememberedBtn = document.querySelector('.startup-launch-btn[data-mode="' + selectedMode + '"]');
      if (rememberedBtn) rememberedBtn.classList.add('active');

      const barFill = document.getElementById('startup-launch-bar-fill');
      const secEl = document.getElementById('startup-launch-auto-sec');

      // Phase 1: countdown — interruptible by any click
      let timerExpired = false;
      await new Promise(resolve => {
        let secs = 3;
        const timer = setInterval(() => {
          secs -= 0.1;
          barFill.style.width = (secs / 3 * 100) + '%';
          secEl.textContent = Math.ceil(secs);
          if (secs <= 0) {
            clearInterval(timer);
            timerExpired = true;
            resolve();
          }
        }, 100);
        launchPanel.addEventListener('click', function stopTimer() {
          clearInterval(timer);
          resolve();
        }, { once: true });
      });

      if (timerExpired) {
        // Auto-proceed with pre-selected mode
        barFill.style.width = '0%';
      } else {
        // Timer interrupted — wait for explicit button click
        barFill.style.width = '100%';
        secEl.textContent = '0';
        document.getElementById('startup-launch-auto').textContent = 'Choose how you want to start';
        await new Promise(resolve => {
          document.querySelectorAll('.startup-launch-btn').forEach(btn => {
            btn.addEventListener('click', () => {
              document.querySelectorAll('.startup-launch-btn').forEach(b => b.classList.remove('active'));
              btn.classList.add('active');
              selectedMode = btn.dataset.mode;
              resolve();
            }, { once: true });
          });
        });
      }

      const remember = document.getElementById('startup-launch-remember-cb').checked;
      await send('saveLaunchOptions', { rememberSelection: remember, lastSelectedMode: selectedMode });
    } else {
      showPanel(launchPanel);
      document.getElementById('startup-launch-auto-mode').textContent = 'Song Requests';
      document.getElementById('startup-launch-remember-cb').checked = false;
      document.getElementById('startup-launch-bar-fill').style.width = '100%';
      document.getElementById('startup-launch-auto').textContent = 'Choose how you want to start';

      // Pre-select last mode
      const lastBtn = document.querySelector('.startup-launch-btn[data-mode="' + selectedMode + '"]');
      if (lastBtn) {
        document.querySelectorAll('.startup-launch-btn').forEach(b => b.classList.remove('active'));
        lastBtn.classList.add('active');
      }

      // Wait for click
      await new Promise(resolve => {
        document.querySelectorAll('.startup-launch-btn').forEach(btn => {
          btn.addEventListener('click', () => {
            document.querySelectorAll('.startup-launch-btn').forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            selectedMode = btn.dataset.mode;
            resolve();
          }, { once: true });
        });
      });

      const remember = document.getElementById('startup-launch-remember-cb').checked;
      await send('saveLaunchOptions', { rememberSelection: remember, lastSelectedMode: selectedMode });
    }

    // Auth phase — import YouTube cookies from a browser (optional for most modes)
    showPanel(authPanel);
    const authStatusEl = document.getElementById('startup-auth-status');

    let authBrowsers = ['chrome', 'edge', 'firefox', 'brave', 'chromium', 'opera', 'vivaldi', 'safari'];
    try {
      const b = await send('getAuthBrowsers');
      if (b && b.browsers && b.browsers.length) authBrowsers = b.browsers;
    } catch { /* use defaults */ }

    const browserOptions = authBrowsers
      .map(b => '<option value="' + b + '"' + (b === ((data && data.cookieBrowser) || '') ? ' selected' : '') + '>' + b.charAt(0).toUpperCase() + b.slice(1) + '</option>')
      .join('');

    authStatusEl.innerHTML =
      'Sign in to YouTube in one of these browsers, then import its cookies.<br><br>' +
      '<select id="startup-auth-browser" class="settings-input" style="max-width:220px;margin:0 auto 10px;display:block">' + browserOptions + '</select>' +
      '<button id="startup-auth-yes" class="action-btn" style="width:auto;padding:8px 20px;font-size:13px"><i class="fas fa-cookie-bite"></i> Import cookies</button> ' +
      '<button id="startup-auth-no" class="top-btn" style="font-size:13px;padding:8px 20px">Skip</button>';

    await new Promise(resolve => {
      document.getElementById('startup-auth-no').onclick = () => resolve();
      document.getElementById('startup-auth-yes').onclick = async () => {
        const browser = document.getElementById('startup-auth-browser').value;
        authStatusEl.innerHTML = '<span class="loading"><i class="fas fa-spinner fa-pulse"></i> Reading cookies from ' + browser + '...</span>';
        try {
          const r = await send('grabCookies', { browser });
          if (r.success) {
            authStatusEl.textContent = 'Authentication successful (' + r.cookieCount + ' cookies)';
            await send('saveCookieBrowser', { browser }).catch(() => { });
            resolve();
          } else {
            authStatusEl.innerHTML = 'Import failed: ' + (r.error || 'no cookies found') +
              '<br><br><button id="startup-auth-retry" class="action-btn" style="width:auto;padding:8px 20px;font-size:13px">Retry</button> ' +
              '<button id="startup-auth-skip" class="top-btn" style="font-size:13px;padding:8px 20px">Skip</button>';
            document.getElementById('startup-auth-retry').onclick = () => document.getElementById('startup-auth-yes').click();
            document.getElementById('startup-auth-skip').onclick = () => resolve();
          }
        } catch (e) {
          authStatusEl.textContent = 'Authentication error: ' + e.message;
        }
      };
    });
    await delay(600);

    // Complete setup
    await send('completeSetup', { startupMode: selectedMode });

    // Dismiss overlay
    overlay.style.opacity = '0';
    setTimeout(() => { overlay.style.display = 'none'; window.focus(); }, 400);
  }

  function delay(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
  }
})();
