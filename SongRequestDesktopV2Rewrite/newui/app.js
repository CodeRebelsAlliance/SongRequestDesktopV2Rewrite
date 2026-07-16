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

  window.external.receiveMessage = handleIncoming;
  if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
    window.chrome.webview.addEventListener('message', function(e) {
      handleIncoming(e.data);
    });
  }

  function handleEvent(eventName, data) {
    if (eventName === 'refresh') loadData();
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
  document.getElementById('btn-check-updates').addEventListener('click', async function() {
    const statusEl = document.getElementById('about-update-status');
    statusEl.textContent = 'Checking...';
    try {
      const result = await send('checkForUpdates');
      if (result.error) {
        statusEl.textContent = 'Check failed: ' + result.error;
      } else if (result.updateAvailable) {
        statusEl.textContent = 'Update available: ' + result.latestVersion + ' (current: ' + result.currentVersion + ')';
        toast('Update available: ' + result.latestVersion, 'info');
      } else {
        statusEl.textContent = 'You\'re up to date! (Version ' + result.currentVersion + ')';
      }
    } catch (e) {
      statusEl.textContent = 'Update check failed';
    }
  });

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
      if (rc && rc.mappings) {
        renderRemoteMappings('r-music-mappings', rc.mappings, ['playPause','skipNext','previous','stop','volumeUp','volumeDown','crossfadeUp','crossfadeDown']);
        renderRemoteMappings('r-announce-mappings', rc.mappings, ['announcement','announcementSound','announcementPushToTalk','announcementDimUp','announcementDimDown']);
        document.getElementById('r-midi-enabled').checked = rc.midiEnabled ?? false;
        document.getElementById('r-midi-input').value = 'Device ' + (rc.midiInputDevice ?? 0);
        document.getElementById('r-midi-output').value = 'Device ' + (rc.midiOutputDevice ?? 0);
      }
      document.getElementById('volume-pill').style.display = cfg.normalizeVolume ? 'inline-flex' : 'none';

      // Library settings
      _libraryScanFolders = cfg.libraryScanFolders || [];
      renderLibraryFolders();
      document.getElementById('s-library-extensions').value = (cfg.libraryAllowedExtensions || ['mp3','m4a','wav','flac','ogg','aac','wma','opus']).join(', ');
      document.getElementById('s-library-auto-scan').checked = cfg.libraryAutoScanOnStartup ?? false;
      document.getElementById('s-library-auto-add-downloads').checked = cfg.libraryAutoAddDownloads ?? true;
      document.getElementById('s-library-remove-missing').checked = cfg.libraryRemoveMissingOnScan ?? false;
      document.getElementById('s-library-recommendations').checked = cfg.libraryRecommendationsEnabled ?? true;
      statusEl.textContent = 'Loaded.'; loadData();
    } catch (e) { statusEl.textContent = 'Error: ' + e.message; }
  }

  async function saveSettings() {
    const statusEl = document.getElementById('settings-status');
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
        libraryScanFolders: _libraryScanFolders,
        libraryAllowedExtensions: document.getElementById('s-library-extensions').value.split(',').map(function(e){return e.trim()}).filter(function(e){return e}),
        libraryAutoScanOnStartup: document.getElementById('s-library-auto-scan').checked,
        libraryAutoAddDownloads: document.getElementById('s-library-auto-add-downloads').checked,
        libraryRemoveMissingOnScan: document.getElementById('s-library-remove-missing').checked,
        libraryRecommendationsEnabled: document.getElementById('s-library-recommendations').checked
      };
      const r = await send('saveSettings', { settings });
      if (r.success) {       statusEl.textContent = 'Saved successfully.'; toast('Settings saved', 'success'); loadData(); hideSettings(); }
      else { statusEl.textContent = 'Save failed: ' + (r.error || 'unknown'); }
    } catch (e) { statusEl.textContent = 'Error: ' + e.message; }
  }

  function setSettingsStatus(msg, isError) {
    const el = document.getElementById('settings-status');
    el.textContent = msg;
    if (isError) toast(msg, 'error');
  }

  function renderRemoteMappings(containerId, mappings, keys) {
    const container = document.getElementById(containerId);
    const labels = { playPause:'Play / Pause', skipNext:'Skip Next', previous:'Previous', stop:'Stop', volumeUp:'Volume Up', volumeDown:'Volume Down', crossfadeUp:'Crossfade Up', crossfadeDown:'Crossfade Down', announcement:'Announcement (Start/Toggle)', announcementSound:'Toggle pre-announcement sound', announcementPushToTalk:'Toggle push-to-talk mode', announcementDimUp:'Dim level (dB) up', announcementDimDown:'Dim level (dB) down' };
    let html = '<div class="rm-header"><span class="rm-col-action">Action</span><span class="rm-col-key">Key Bind</span><span class="rm-col-midi">MIDI Mapping</span></div>';
    for (const key of keys) {
      const m = mappings[key] || {};
      html += '<div class="rm-row"><span class="rm-col-action">' + (labels[key] || key) + '</span><span class="rm-col-key' + (m.keybind ? '' : ' rm-unset') + '">' + (m.keybind || '—') + '</span><span class="rm-col-midi' + (m.midi ? '' : ' rm-unset') + '">' + (m.midi || '—') + '</span></div>';
    }
    container.innerHTML = html;
  }

  document.getElementById('btn-settings-close').onclick = hideSettings;
  document.getElementById('btn-settings-save').onclick = saveSettings;
  document.getElementById('btn-settings-reload').onclick = loadSettings;

  document.querySelectorAll('.settings-tab').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.settings-tab, .settings-panel').forEach(el => el.classList.remove('active'));
      btn.classList.add('active');
      document.getElementById('settings-' + btn.dataset.tab).classList.add('active');
    });
  });

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
  document.getElementById('r-open-legacy').onclick = () => { send('showSettings'); };

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

      const thumbWrap = document.createElement('div');
      thumbWrap.className = 'thumb-wrap' + (thumbCache[v.ytid] ? '' : ' is-loading');
      const img = document.createElement('img');
      img.alt = '';
      if (thumbCache[v.ytid]) {
        img.src = thumbCache[v.ytid];
      } else {
        img.style.opacity = '0';
        img.onload = () => { img.style.opacity = '1'; thumbWrap.classList.remove('is-loading'); };
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
  }

  function loadThumbnail(videoId, imgEl) {
    if (thumbCache[videoId]) {
      imgEl.src = thumbCache[videoId];
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
      }
    }).catch(() => {
      imgEl.style.opacity = '1';
      imgEl.closest('.thumb-wrap')?.classList.remove('is-loading');
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
    const actionMap = {
      'approve': { method: 'approve', msg: 'Approved' },
      'unapprove': { method: 'unapprove', msg: 'Unapproved' },
      'delete': { method: 'delete', msg: 'Deleted' },
      'blacklist': { method: 'blacklist', msg: 'Blacklisted' },
      'unblacklist': { method: 'unblacklist', msg: 'Unblacklisted' },
      'queueSong': { method: 'queueSong', msg: 'Queued' },
      'playNext': { method: 'playNext', msg: 'Playing Next' }
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
      document.getElementById('startup-update-notes').textContent = updateInfo.releaseNotes || '';

      await new Promise(resolve => {
        document.getElementById('startup-update-later').onclick = resolve;
        document.getElementById('startup-update-now').onclick = async () => {
          document.getElementById('startup-update-buttons').style.display = 'none';
          document.getElementById('startup-update-progress').style.display = 'block';
          try { await send('installUpdate', { downloadUrl: updateInfo.downloadUrl }); } catch (e) {}
          resolve();
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

    // Auth phase — mandatory if SongRequests (auto-opens), optional otherwise
    showPanel(authPanel);
    const authStatusEl = document.getElementById('startup-auth-status');

    if (selectedMode === 'SongRequests') {
      authStatusEl.innerHTML = '<span class="loading"><i class="fas fa-spinner fa-pulse"></i> Opening browser for Google sign-in...</span>';
      try {
        const r = await send('triggerAuth');
        if (r.success) {
          authStatusEl.textContent = 'Authentication successful (' + r.cookieCount + ' cookies)';
        } else {
          authStatusEl.textContent = 'Authentication was cancelled. Continuing without auth.';
        }
      } catch (e) {
        authStatusEl.textContent = 'Authentication error: ' + e.message;
      }
    } else {
      authStatusEl.innerHTML = 'Sign in with Google to enable song requests later.<br><br>' +
        '<button id="startup-auth-yes" class="action-btn" style="width:auto;padding:8px 20px;font-size:13px"><i class="fas fa-id-card"></i> Sign in now</button> ' +
        '<button id="startup-auth-no" class="top-btn" style="font-size:13px;padding:8px 20px">Skip</button>';

      await new Promise(resolve => {
        document.getElementById('startup-auth-yes').onclick = async () => {
          authStatusEl.innerHTML = '<span class="loading"><i class="fas fa-spinner fa-pulse"></i> Opening browser for Google sign-in...</span>';
          try {
            const r = await send('triggerAuth');
            authStatusEl.textContent = r.success ? 'Authentication successful (' + r.cookieCount + ' cookies)' : 'Authentication was cancelled.';
          } catch (e) {
            authStatusEl.textContent = 'Error: ' + e.message;
          }
          resolve();
        };
        document.getElementById('startup-auth-no').onclick = resolve;
      });
    }
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
