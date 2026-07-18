(function() {
  'use strict';

  // --- Elements ---
  const playerBar = document.getElementById('player-bar');
  const expandBtn = document.getElementById('btn-expand');
  const collapseBtn = document.getElementById('btn-exp-collapse');
  const leftSidebar = document.getElementById('left-sidebar');
  const rightSidebar = document.getElementById('right-sidebar');
  const topbar = document.getElementById('topbar');
  const bodyEl = document.getElementById('body');

  // Collapsed mode elements
  const colTitle = document.getElementById('player-bar-title');
  const colArtist = document.getElementById('player-bar-artist');
  const colThumb = document.getElementById('player-bar-thumb');
  const colPlayBtn = document.getElementById('btn-play');
  const colProgressFill = document.getElementById('progress-fill');
  const colProgressCurrent = document.getElementById('progress-current');
  const colProgressTotal = document.getElementById('progress-total');

  // Expanded mode elements
  const expTitle = document.getElementById('expanded-title');
  const expArtist = document.getElementById('expanded-artist');
  const expArtwork = document.getElementById('expanded-artwork');
  const expPlayBtn = document.getElementById('btn-exp-play');
  const expProgressFill = document.getElementById('exp-progress-fill');
  const expProgressCurrent = document.getElementById('exp-progress-current');
  const expProgressTotal = document.getElementById('exp-progress-total');

  // Quality pill elements
  const colQuality = document.getElementById('player-bar-quality');
  const expQuality = document.getElementById('expanded-quality');

  // Lyrics
  const lyricsContainer = document.getElementById('expanded-lyrics');
  const lyricsProvider = document.getElementById('lyrics-provider');
  let syncedLyrics = [];
  let plainLyricLines = [];
  let hasSyncedLyrics = false;
  let totalTimeSecs = 0;
  let lastHighlightIndex = -1;
  let lastCurrentTime = 0;

  // Lyrics sync offset
  let lyricsSyncOffset = 0;
  let syncAnimFrame = null;
  const syncValueEl = document.getElementById('sync-value');
  const syncResetEl = document.getElementById('sync-reset');
  const syncBackBtn = document.getElementById('sync-back');
  const syncForwardBtn = document.getElementById('sync-forward');

  // Lyrics font size (per-song)
  let lyricsFontScale = 1.0;
  let fontAnimFrame = null;
  const fontValueEl = document.getElementById('font-size-value');
  const fontResetEl = document.getElementById('font-reset');
  const fontDecreaseBtn = document.getElementById('font-decrease');
  const fontIncreaseBtn = document.getElementById('font-increase');

  // Slider value labels
  const volumeValueEls = [document.getElementById('volume-value'), document.getElementById('exp-volume-value')];
  const crossfadeValueEls = [document.getElementById('crossfade-value'), document.getElementById('exp-crossfade-value')];

  // --- Expand / collapse ---
  var _animating = false;
  var _savedCollapsedRect = null;

  function clearPlayerBarInlineStyles() {
    var a1 = playerBar.style.getPropertyValue('--accent-1');
    var a2 = playerBar.style.getPropertyValue('--accent-2');
    var a3 = playerBar.style.getPropertyValue('--accent-3');
    playerBar.style.cssText = '';
    if (a1) playerBar.style.setProperty('--accent-1', a1);
    if (a2) playerBar.style.setProperty('--accent-2', a2);
    if (a3) playerBar.style.setProperty('--accent-3', a3);
  }

  function setExpanded(expanded) {
    if (_animating) return;

    if (expanded) {
      var rect = playerBar.getBoundingClientRect();
      _savedCollapsedRect = rect;
      _animating = true;

      // Hide sidebars/topbar immediately
      leftSidebar.classList.add('collapsed');
      rightSidebar.classList.add('collapsed');
      topbar.style.display = 'none';
      bodyEl.style.display = 'none';

      // Start fade-out of collapsed sections + pin to current position
      playerBar.classList.add('animating');
      playerBar.style.position = 'fixed';
      playerBar.style.top = rect.top + 'px';
      playerBar.style.left = rect.left + 'px';
      playerBar.style.right = (window.innerWidth - rect.right) + 'px';
      playerBar.style.bottom = (window.innerHeight - rect.bottom) + 'px';
      playerBar.style.margin = '0';
      playerBar.style.zIndex = '100';
      expandBtn.innerHTML = '<i class="fas fa-chevron-down"></i>';

      // Force reflow for start position
      playerBar.offsetHeight;

      // After collapsed sections fade out, add .expanded to remove from layout
      setTimeout(function() {
        playerBar.classList.add('expanded');
        playerBar.style.top = '0';
        playerBar.style.left = '0';
        playerBar.style.right = '0';
        playerBar.style.bottom = '0';
        playerBar.style.borderRadius = '0';
        playerBar.style.padding = '0';

        playerBar.addEventListener('transitionend', function onEnd(e) {
          if (e.propertyName !== 'top') return;
          playerBar.removeEventListener('transitionend', onEnd);
          playerBar.classList.remove('animating');
          clearPlayerBarInlineStyles();
          _animating = false;
          requestAnimationFrame(function() {
            updateLyricFontSizes();
            if (lastCurrentTime >= 0) updateLyricHighlighting(lastCurrentTime);
          });
        });
      }, 200);

    } else {
      var target = _savedCollapsedRect || { top: window.innerHeight - 68, left: 5, right: window.innerWidth - 5, bottom: window.innerHeight - 5 };

      _animating = true;

      // Remove .expanded — collapsed sections fade in (opacity:1), expanded content fades out
      playerBar.classList.remove('expanded');

      // Re-pin to fullscreen so top transitions from 0 to target (auto→pixel doesn't animate)
      playerBar.classList.add('collapsing');
      playerBar.style.position = 'fixed';
      playerBar.style.top = '0';
      playerBar.style.left = '0';
      playerBar.style.right = '0';
      playerBar.style.bottom = '0';
      playerBar.offsetHeight;

      // Animate to collapsed position
      playerBar.style.top = target.top + 'px';
      playerBar.style.left = target.left + 'px';
      playerBar.style.right = (window.innerWidth - target.right) + 'px';
      playerBar.style.bottom = (window.innerHeight - target.bottom) + 'px';
      playerBar.style.borderRadius = '0 0 15px 15px';
      playerBar.style.margin = '0 5px 5px';
      playerBar.style.padding = '8px 15px';
      expandBtn.innerHTML = '<i class="fas fa-chevron-up"></i>';

      playerBar.addEventListener('transitionend', function onEnd(e) {
        if (e.propertyName !== 'top') return;
        playerBar.removeEventListener('transitionend', onEnd);
        playerBar.classList.remove('collapsing');
        clearPlayerBarInlineStyles();
        leftSidebar.classList.remove('collapsed');
        rightSidebar.classList.remove('collapsed');
        topbar.style.display = '';
        bodyEl.style.display = '';
        _animating = false;
      });
    }
  }

  var _lyricResizeTimer = null;
  window.addEventListener('resize', function() {
    if (_lyricResizeTimer) clearTimeout(_lyricResizeTimer);
    _lyricResizeTimer = setTimeout(updateLyricFontSizes, 150);
  });

  expandBtn.addEventListener('click', () => setExpanded(true));
  collapseBtn.addEventListener('click', () => setExpanded(false));

  // --- Sidebar tab switching ---
  document.querySelectorAll('.sidebar-tab').forEach(function(tab) {
    tab.addEventListener('click', function() {
      var view = this.getAttribute('data-view');
      document.querySelectorAll('.sidebar-tab').forEach(function(t) { t.classList.remove('active'); });
      this.classList.add('active');
      document.querySelectorAll('#center .view').forEach(function(v) { v.classList.remove('active'); });
      var target = document.getElementById('view-' + view);
      if (target) target.classList.add('active');
    });
  });

  // --- Home action buttons ---
  document.getElementById('home-btn-queue').addEventListener('click', function() {
    var right = document.getElementById('right-sidebar');
    if (right) right.classList.toggle('collapsed');
  });
  document.getElementById('home-btn-playlists').addEventListener('click', function() {
    document.querySelectorAll('.sidebar-tab').forEach(function(t) { t.classList.remove('active'); });
    document.querySelector('[data-view="playlists"]').classList.add('active');
    document.querySelectorAll('#center .view').forEach(function(v) { v.classList.remove('active'); });
    document.getElementById('view-playlists').classList.add('active');
  });
  document.getElementById('home-btn-settings').addEventListener('click', function() {
    document.getElementById('btn-settings').click();
  });

  // --- Play button sync ---
  function syncPlayButtons(playing) {
    const btnPairs = [
      [colPlayBtn, 'btn-play'],
      [expPlayBtn, 'btn-exp-play']
    ];
    btnPairs.forEach(([btn]) => {
      if (!btn) return;
      btn.classList.toggle('playing', playing);
      btn.innerHTML = playing ? '<i class="fas fa-pause"></i>' : '<i class="fas fa-play"></i>';
    });
  }

  function onPlayClick(btn) {
    const playing = btn.classList.toggle('playing');
    const icon = playing ? 'pause' : 'play';
    btn.innerHTML = `<i class="fas fa-${icon}"></i>`;
    // Sync the other play button
    [colPlayBtn, expPlayBtn].forEach(b => {
      if (b && b !== btn) {
        b.classList.toggle('playing', playing);
        b.innerHTML = `<i class="fas fa-${icon}"></i>`;
      }
    });
    hostSend('playPause');
  }

  [colPlayBtn, expPlayBtn].forEach(btn => {
    if (!btn) return;
    btn.addEventListener('click', function() { onPlayClick(this); });
  });

  // --- Control buttons ---
  function wireCtrlBtn(id, method) {
    const btn = document.getElementById(id);
    if (!btn) return;
    btn.addEventListener('click', () => hostSend(method));
  }
  wireCtrlBtn('btn-prev', 'skipPrevious');
  wireCtrlBtn('btn-exp-prev', 'skipPrevious');
  wireCtrlBtn('btn-next', 'skipNext');
  wireCtrlBtn('btn-exp-next', 'skipNext');
  wireCtrlBtn('btn-shuffle', 'shuffleQueue');
  wireCtrlBtn('btn-exp-shuffle', 'shuffleQueue');
  wireCtrlBtn('btn-exp-collapse', null); // handled by collapseBtn above

  // --- Repeat toggle ---
  let repeatEnabled = false;
  const repeatBtns = [document.getElementById('btn-repeat'), document.getElementById('btn-exp-repeat')];

  function setRepeatState(on) {
    repeatEnabled = on;
    repeatBtns.forEach(function(btn) {
      if (!btn) return;
      btn.classList.toggle('active', on);
      btn.innerHTML = on ? '<i class="fas fa-redo"></i><span class="repeat-dot"></span>' : '<i class="fas fa-redo"></i>';
    });
  }

  repeatBtns.forEach(function(btn) {
    if (!btn) return;
    btn.addEventListener('click', function() {
      setRepeatState(!repeatEnabled);
      hostSend('toggleRepeat');
    });
  });

  // --- Volume sync ---
  const volSliders = [
    document.getElementById('volume-slider'),
    document.getElementById('exp-volume-slider')
  ];
  const volIcons = [
    document.getElementById('btn-volume')?.querySelector('i'),
    document.getElementById('btn-exp-volume')?.querySelector('i')
  ];
  function sendVolume(val) {
    hostSend('setVolume', { value: val / 100 });
  }
  volSliders.forEach(slider => {
    if (!slider) return;
    slider.addEventListener('input', function() {
      const vol = this.value;
      const iconName = vol == 0 ? 'fa-volume-mute' : vol < 50 ? 'fa-volume-down' : 'fa-volume-up';
      volSliders.forEach(s => { if (s && s !== this) s.value = vol; });
      volIcons.forEach(icon => { if (icon) icon.className = `fas ${iconName}`; });
    });
    slider.addEventListener('change', function() {
      sendVolume(this.value);
    });
  });
  // --- Crossfade sync ---
  const cfSliders = [
    document.getElementById('crossfade-slider'),
    document.getElementById('exp-crossfade-slider')
  ];
  cfSliders.forEach(slider => {
    if (!slider) return;
    slider.addEventListener('input', function() {
      cfSliders.forEach(s => { if (s && s !== this) s.value = this.value; });
    });
    slider.addEventListener('change', function() {
      hostSend('setCrossfade', { value: this.value / 10 });
    });
  });

  // Volume icon clicks toggle mute
  volIcons.forEach(icon => {
    const btn = icon?.closest('button');
    if (btn) {
      btn.addEventListener('click', function() {
        const slider = this.closest('#player-bar-right, #expanded-controls');
        const s = slider ? slider.querySelector('input[type="range"]') : null;
        if (s) {
          const newVal = s.value > 0 ? 0 : 80;
          s.value = newVal;
          volSliders.forEach(sl => { if (sl) sl.value = newVal; });
          const iconName = newVal == 0 ? 'fa-volume-mute' : 'fa-volume-up';
          volIcons.forEach(ic => { if (ic) ic.className = `fas ${iconName}`; });
          sendVolume(newVal);
        }
      });
    }
  });

  // --- Time formatting ---
  function formatTime(seconds) {
    if (!seconds || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  // --- Seekbar interaction ---
  const seekTracks = [
    { track: document.getElementById('progress-track'), fill: document.getElementById('progress-fill'), hoverFill: document.getElementById('progress-hover-fill'), tooltip: document.getElementById('progress-tooltip') },
    { track: document.getElementById('exp-progress-track'), fill: document.getElementById('exp-progress-fill'), hoverFill: document.getElementById('exp-progress-hover-fill'), tooltip: document.getElementById('exp-progress-tooltip') }
  ];
  let isSeeking = false;

  function getSeekPosition(e, trackEl) {
    var rect = trackEl.getBoundingClientRect();
    return Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
  }

  seekTracks.forEach(function(s) {
    if (!s.track) return;

    // Hover preview
    s.track.addEventListener('mousemove', function(e) {
      if (isSeeking) return;
      var pos = getSeekPosition(e, s.track);
      s.hoverFill.style.width = (pos * 100) + '%';
      s.tooltip.textContent = formatTime(pos * totalTimeSecs);
      s.tooltip.style.left = (pos * 100) + '%';
      s.tooltip.classList.add('visible');
    });

    s.track.addEventListener('mouseleave', function() {
      if (isSeeking) return;
      s.hoverFill.style.width = '0%';
      s.tooltip.classList.remove('visible');
    });

    // Click to seek
    s.track.addEventListener('mousedown', function(e) {
      e.preventDefault();
      isSeeking = true;
      s.track.classList.add('seeking');
      var pos = getSeekPosition(e, s.track);
      s.hoverFill.style.width = (pos * 100) + '%';
      s.fill.style.transition = 'none';
      s.fill.style.width = (pos * 100) + '%';
      s.tooltip.textContent = formatTime(pos * totalTimeSecs);
      s.tooltip.style.left = (pos * 100) + '%';
      s.tooltip.classList.add('visible');

      function onMove(ev) {
        var p = getSeekPosition(ev, s.track);
        s.hoverFill.style.width = (p * 100) + '%';
        s.fill.style.width = (p * 100) + '%';
        s.tooltip.textContent = formatTime(p * totalTimeSecs);
        s.tooltip.style.left = (p * 100) + '%';
      }

      function onUp(ev) {
        document.removeEventListener('mousemove', onMove);
        document.removeEventListener('mouseup', onUp);
        var finalPos = getSeekPosition(ev, s.track);
        s.track.classList.remove('seeking');
        s.tooltip.classList.remove('visible');
        s.hoverFill.style.width = '0%';
        s.fill.style.transition = '';
        s.fill.style.width = (finalPos * 100) + '%';
        isSeeking = false;
        // Pulse animation
        s.track.classList.remove('seek-pulse');
        void s.track.offsetWidth;
        s.track.classList.add('seek-pulse');
        s.track.addEventListener('animationend', function() {
          s.track.classList.remove('seek-pulse');
        }, { once: true });
        hostSend('seek', { position: finalPos });
        lastHighlightIndex = -1;
        updateLyricHighlighting(finalPos * totalTimeSecs);
      }

      document.addEventListener('mousemove', onMove);
      document.addEventListener('mouseup', onUp);
    });
  });

  // --- Receive events from C# ---
  function onEvent(eventName, data) {
    switch (eventName) {
      case 'nowPlayingUpdate':
        updateNowPlaying(data);
        break;
      case 'queueUpdate':
        updateQueue(data);
        break;
      case 'lyricsUpdate':
        renderLyrics(data);
        break;
      case 'ytDownloadProgress':
        onYtDownloadProgress(data);
        break;
    }
  }

  function updateNowPlaying(data) {
    // Track metadata (only sent when song changes)
    if (data.title) {
      // Collapsed mode
      colTitle.textContent = data.title;
      colArtist.textContent = data.artist || '—';
      // Expanded mode
      expTitle.textContent = data.title;
      expArtist.textContent = data.artist || '—';

      // Quality pill (collapsed + expanded)
      if (data.fileType) {
        var ql = data.fileType;
        if (data.bitrateKbps > 0) ql += ' ' + data.bitrateKbps + 'kbps';
        var pillHtml = '<i class="fas fa-signal"></i> ' + escHtml(ql);
        colQuality.innerHTML = pillHtml;
        colQuality.style.display = '';
        expQuality.innerHTML = pillHtml;
        expQuality.style.display = '';
      } else {
        colQuality.style.display = 'none';
        expQuality.style.display = 'none';
      }

      // Reset lyrics for new song
      syncedLyrics = [];
      plainLyricLines = [];
      hasSyncedLyrics = false;
      lastHighlightIndex = -1;
      lastCurrentTime = 0;
      animateSyncToZero();
      animateFontToDefault();
      renderLyricsLoading();
    }

    // Update library highlight from every tick
    if (data.currentSongPath != null) {
      var newPath = data.currentSongPath || null;
      if (newPath !== currentPlayingPath) {
        currentPlayingPath = newPath;
        updateLibraryPlayingState();
      }
    } else if (currentPlayingPath !== null) {
      currentPlayingPath = null;
      updateLibraryPlayingState();
    }

    // Thumbnail
    if (data.thumbnail) {
      const imgHtml = `<img src="${data.thumbnail}" alt="artwork">`;
      // Collapsed
      colThumb.innerHTML = imgHtml;
      // Expanded
      expArtwork.innerHTML = imgHtml;
      extractAccentColors(data.thumbnail);
    }

    // Duration (total time)
    if (data.duration) {
      colProgressTotal.textContent = data.duration;
      expProgressTotal.textContent = data.duration;
      totalTimeSecs = parseDurationString(data.duration);
    } else if (data.totalTime != null) {
      const fmt = formatTime(data.totalTime);
      colProgressTotal.textContent = fmt;
      expProgressTotal.textContent = fmt;
      totalTimeSecs = data.totalTime;
    }

    // Current position
    if (data.currentTime != null) {
      lastCurrentTime = data.currentTime;
      const effectiveTotal = data.totalTime || totalTimeSecs;
      const pct = effectiveTotal > 0 ? Math.min(data.currentTime / effectiveTotal * 100, 100) : 0;
      if (!isSeeking) {
        colProgressFill.style.width = pct + '%';
        expProgressFill.style.width = pct + '%';
        const fmt = formatTime(data.currentTime);
        colProgressCurrent.textContent = fmt;
        expProgressCurrent.textContent = fmt;
      }

      updateLyricHighlighting(data.currentTime);
    }

    // Play state
    if (data.isPlaying != null) {
      syncPlayButtons(data.isPlaying);
    }
    // Sync queue now-playing header when playback active state changes
    if (data.isPlaybackActive != null) {
      updateQueueFromPlayback(!!data.isPlaybackActive);
    }
  }

  // --- Queue ---
  const queueList = document.getElementById('queue-list');
  const clearQueueBtn = document.getElementById('btn-clear-queue');
  let lastQueueData = null;
  let lastPlaybackActive = false;
  let currentPlayingPath = null;

  function updateQueue(data) {
    lastQueueData = data;
    lastPlaybackActive = !!data.isPlaying;
    var newPath = data.currentSongPath || null;
    if (newPath !== currentPlayingPath) {
      currentPlayingPath = newPath;
      updateLibraryPlayingState();
    }
    renderQueue(data.queue || [], lastPlaybackActive);
  }

  function updateLibraryPlayingState() {
    var songs = libListEl.querySelectorAll('.library-song');
    songs.forEach(function(el) {
      var fp = el.getAttribute('data-filepath');
      var isPlaying = currentPlayingPath && fp && fp === currentPlayingPath;
      el.classList.toggle('lib-now-playing', isPlaying);
    });
  }

  function renderQueue(items, isPlaying) {

    if (items.length === 0) {
      queueList.innerHTML = '<div class="queue-empty">Queue is empty</div>';
      return;
    }

    var html = '';

    // First item = now playing
    const first = items[0];
    if (isPlaying) {
      html += '<div class="queue-now-playing-header"><i class="fas fa-play-circle"></i> Now Playing</div>';
    }
    html += renderQueueItem(first, true);

    // Remaining items (after separator)
    if (items.length > 1) {
      html += '<div class="queue-separator"></div>';
      for (let i = 1; i < items.length; i++) {
        html += renderQueueItem(items[i], false);
      }
    }

    queueList.innerHTML = html;

    // Delegate remove clicks
    queueList.querySelectorAll('.queue-item-remove').forEach(btn => {
      btn.addEventListener('click', function(e) {
        e.stopPropagation();
        const idx = parseInt(this.dataset.index);
        if (!isNaN(idx)) hostSend('removeQueueItem', { index: idx });
      });
    });

    // Drag-and-drop reordering (only upcoming items)
    let dragSrcIdx = null;
    queueList.querySelectorAll('.queue-item[draggable]').forEach(el => {
      el.addEventListener('dragstart', function(e) {
        dragSrcIdx = parseInt(this.dataset.index);
        this.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', String(dragSrcIdx));
      });
      el.addEventListener('dragend', function(e) {
        this.classList.remove('dragging');
        queueList.querySelectorAll('.drag-indicator').forEach(d => d.remove());
      });
    });

    queueList.addEventListener('dragover', function(e) {
      e.preventDefault();
      const target = e.target.closest('.queue-item');
      if (!target || !target.hasAttribute('draggable')) return;
      if (parseInt(target.dataset.index) === dragSrcIdx) return;
      target.classList.add('drag-over');
    });

    queueList.addEventListener('dragleave', function(e) {
      const target = e.target.closest('.queue-item');
      if (target) target.classList.remove('drag-over');
    });

    queueList.addEventListener('drop', function(e) {
      e.preventDefault();
      queueList.querySelectorAll('.drag-over').forEach(el => el.classList.remove('drag-over'));
      const target = e.target.closest('.queue-item');
      if (!target) return;
      const toIdx = parseInt(target.dataset.index);
      if (isNaN(toIdx) || toIdx === dragSrcIdx || toIdx === 0) return;
      if (!isNaN(dragSrcIdx)) {
        hostSend('moveQueueItem', { fromIndex: dragSrcIdx, toIndex: toIdx });
        dragSrcIdx = null;
      }
    });
  }

  function renderQueueItem(item, isNowPlaying) {
    const thumbHtml = item.thumbnail
      ? `<div class="queue-item-thumb"><img src="${item.thumbnail}" alt=""></div>`
      : `<div class="queue-item-thumb"><i class="fas fa-music"></i></div>`;
    const draggable = !isNowPlaying ? ' draggable="true"' : '';

    var pills = '';
    if (item.isMissing) pills += '<span class="pill pill-missing"><i class="fas fa-exclamation-triangle"></i> Missing</span>';
    if (item.isHashDuplicate) pills += '<span class="pill pill-dup"><i class="fas fa-clone"></i> Hash Dup</span>';
    if (item.hasMetadataDuplicates) pills += '<span class="pill pill-dup"><i class="fas fa-clone"></i> Meta Dup' + (item.metadataDuplicateCount > 1 ? ' (' + item.metadataDuplicateCount + ')' : '') + '</span>';

    return `
      <div class="queue-item${isNowPlaying ? ' queue-now-playing' : ''}" data-index="${item.index}"${draggable}>
        ${thumbHtml}
        <div class="queue-item-info">
          <div class="queue-item-title">${escHtml(item.title)}</div>
          <div class="queue-item-meta">
            <span class="queue-item-artist">${escHtml(item.artist)}</span>
            <span class="queue-item-sep">·</span>
            <span class="queue-item-duration">${item.duration}</span>
            ${item.estimatedStart ? `<span class="queue-item-sep">·</span><span class="queue-item-start">~${item.estimatedStart}</span>` : ''}
          </div>
          ${pills ? '<div class="queue-item-pills">' + pills + '</div>' : ''}
        </div>
        <button class="queue-item-remove" data-index="${item.index}" title="Remove"><i class="fas fa-times"></i></button>
      </div>
    `;
  }

  function updateQueueFromPlayback(isPlaybackActive) {
    if (lastQueueData && isPlaybackActive !== lastPlaybackActive) {
      lastPlaybackActive = isPlaybackActive;
      renderQueue(lastQueueData.queue || [], isPlaybackActive);
    }
  }

  // Add files
  document.getElementById('btn-add-files').addEventListener('click', () => hostSend('addLocalFiles', { mode: 'add' }));
  document.getElementById('btn-add-next').addEventListener('click', () => hostSend('addLocalFiles', { mode: 'addNext' }));

  // Clear queue
  clearQueueBtn.addEventListener('click', () => hostSend('clearQueue'));

  function escHtml(str) {
    const div = document.createElement('div');
    div.appendChild(document.createTextNode(str));
    return div.innerHTML;
  }

  // --- Host communication ---
  var _reqId = 0;
  var _pendingReqs = {};

  function hostSend(method, params) {
    try {
      window.external.sendMessage(JSON.stringify({ type: 'request', id: 0, method, ...params }));
    } catch (e1) {
      try {
        if (window.chrome && window.chrome.webview) {
          window.chrome.webview.postMessage(JSON.stringify({ type: 'request', id: 0, method, ...params }));
        }
      } catch (e2) {
        console.error('hostSend failed', e2);
      }
    }
  }

  function hostRequest(method, params, timeoutMs) {
    return new Promise(function(resolve, reject) {
      var id = ++_reqId;
      _pendingReqs[id] = { resolve: resolve, reject: reject, timer: setTimeout(function() {
        delete _pendingReqs[id];
        reject(new Error('Request timeout: ' + method));
      }, timeoutMs || 30000) };
      try {
        window.external.sendMessage(JSON.stringify({ type: 'request', id: id, method: method, ...params }));
      } catch (e1) {
        try {
          if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'request', id: id, method: method, ...params }));
          }
        } catch (e2) {
          delete _pendingReqs[id];
          reject(e2);
        }
      }
    });
  }

  function postMessageToHost(msg) {
    try {
      window.external.sendMessage(msg);
    } catch (e1) {
      try {
        if (window.chrome && window.chrome.webview) {
          window.chrome.webview.postMessage(msg);
        }
      } catch (e2) {
        console.error('postMessageToHost failed', e2);
      }
    }
  }

  // Receive events from C# (matching app.js pattern)
  function handleIncoming(json) {
    try {
      const msg = JSON.parse(json);
      if (msg.type === 'response') {
        var id = msg.id;
        if (id && _pendingReqs[id]) {
          clearTimeout(_pendingReqs[id].timer);
          var result = msg.result !== undefined ? msg.result : msg;
          _pendingReqs[id].resolve(result);
          delete _pendingReqs[id];
          return;
        }
        // Fallback: volume/crossfade init response (from musicPlayerReady)
        if (msg.volume != null) {
          const v = Math.round(msg.volume * 100);
          const c = Math.round((msg.crossfade || 4) * 10);
          volSliders.forEach(sl => { if (sl) sl.value = v; });
          cfSliders.forEach(sl => { if (sl) sl.value = c; });
          const iconName = v == 0 ? 'fa-volume-mute' : 'fa-volume-up';
          volIcons.forEach(ic => { if (ic) ic.className = `fas ${iconName}`; });
        }
      } else if (msg.type === 'event') {
        onEvent(msg.eventName, msg.data || {});
      }
    } catch (_) {}
  }

  function sendReady() {
    // Request initial state from C# (page is now loaded)
    hostSend('musicPlayerReady');
  }

  window.external.receiveMessage = handleIncoming;
  if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
    window.chrome.webview.addEventListener('message', function(e) {
      handleIncoming(e.data);
    });
  }
  // Notify C# that we're ready to receive events
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', sendReady);
  } else {
    sendReady();
  }

  // --- Lyrics rendering & highlighting ---
  function parseDurationString(str) {
    if (!str) return 0;
    var parts = str.split(':').map(Number);
    if (parts.length === 2) return parts[0] * 60 + parts[1];
    if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2];
    return 0;
  }

  function updateLyricFontSizes() {
    if (!playerBar.classList.contains('expanded')) return;
    var rect = lyricsContainer.getBoundingClientRect();
    var h = rect.height;
    if (h <= 0) return;
    var active = Math.round(h * 0.055 * lyricsFontScale);
    active = Math.max(22, Math.min(active, 52));
    var base = Math.round(active * 0.7);
    base = Math.max(16, Math.min(base, 38));
    var prev = Math.round(active * 0.78);
    prev = Math.max(16, Math.min(prev, 42));
    lyricsContainer.style.setProperty('--lyric-active', active + 'px');
    lyricsContainer.style.setProperty('--lyric-base', base + 'px');
    lyricsContainer.style.setProperty('--lyric-prev', prev + 'px');
  }

  function renderLyricsLoading() {
    lyricsContainer.innerHTML = '<div class="lyrics-loading"><i class="fas fa-circle-notch fa-spin"></i><div>Loading lyrics...</div></div>';
    if (lyricsProvider) lyricsProvider.textContent = '';
  }

  function renderNoLyrics() {
    lyricsContainer.innerHTML = '<div class="lyrics-no-lyrics"><i class="fas fa-music"></i><div>No lyrics available</div></div>';
  }

  function renderLyrics(data) {
    lyricsContainer.innerHTML = '';
    syncedLyrics = [];
    plainLyricLines = [];
    hasSyncedLyrics = false;

    var topSpacer = document.createElement('div');
    topSpacer.className = 'lyrics-spacer';
    lyricsContainer.appendChild(topSpacer);

    var emptyLine = document.createElement('div');
    emptyLine.className = 'lyrics-line';
    lyricsContainer.appendChild(emptyLine);

    if (data.syncedLyrics && data.syncedLyrics.length > 0) {
      syncedLyrics = data.syncedLyrics;
      hasSyncedLyrics = true;
      for (var i = 0; i < data.syncedLyrics.length; i++) {
        var line = data.syncedLyrics[i];
        if (i > 0) {
          var gap = line.time - data.syncedLyrics[i - 1].time;
          if (gap > 3.5) {
            var gapDiv = document.createElement('div');
            gapDiv.className = 'lyrics-gap';
            lyricsContainer.appendChild(gapDiv);
          }
        }
        var div = document.createElement('div');
        div.className = 'lyrics-line';
        div.textContent = line.text;
        lyricsContainer.appendChild(div);
      }
    } else if (data.plainLyrics && data.plainLyrics.length > 0) {
      plainLyricLines = data.plainLyrics;
      data.plainLyrics.forEach(function(text) {
        var div = document.createElement('div');
        div.className = 'lyrics-line';
        div.textContent = text;
        lyricsContainer.appendChild(div);
      });
    } else {
      renderNoLyrics();
      if (lyricsProvider) lyricsProvider.textContent = '';
      return;
    }

    var bottomSpacer = document.createElement('div');
    bottomSpacer.className = 'lyrics-spacer';
    lyricsContainer.appendChild(bottomSpacer);

    if (lyricsProvider) lyricsProvider.textContent = data.provider ? 'Lyrics: ' + data.provider : '';
    lastHighlightIndex = -1;
    updateLyricFontSizes();
    if (lastCurrentTime >= 0) updateLyricHighlighting(lastCurrentTime);
  }

  function updateLyricHighlighting(currentTime) {
    var lines = lyricsContainer.querySelectorAll('.lyrics-line');
    if (lines.length === 0) return;

    var adjustedTime = currentTime + lyricsSyncOffset;
    var index = -1;
    if (hasSyncedLyrics && syncedLyrics.length > 0) {
      for (var i = 0; i < syncedLyrics.length; i++) {
        if (syncedLyrics[i].time <= adjustedTime) index = i;
        else break;
      }
    } else if (plainLyricLines.length > 0) {
      var total = totalTimeSecs || 1;
      var progress = Math.min(adjustedTime / total, 1);
      index = Math.round(progress * Math.max(0, plainLyricLines.length - 1));
    }

    var domIndex = index + 1;

    if (domIndex < 0 || domIndex >= lines.length) return;
    if (domIndex === lastHighlightIndex) return;

    lastHighlightIndex = domIndex;

    lines.forEach(function(line, i) {
      if (i === domIndex) {
        line.className = 'lyrics-line active';
        if (playerBar.classList.contains('expanded')) {
          line.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
      } else if (i === domIndex - 1) {
        line.className = 'lyrics-line prev';
      } else {
        line.className = 'lyrics-line';
      }
    });
  }

  // --- Lyrics sync offset controls ---
  function updateSyncDisplay() {
    var sign = lyricsSyncOffset >= 0 ? '+' : '';
    syncValueEl.textContent = sign + lyricsSyncOffset.toFixed(2) + 's';
    syncValueEl.classList.toggle('nonzero', lyricsSyncOffset !== 0);
    syncResetEl.classList.toggle('visible', lyricsSyncOffset !== 0);
  }

  function setSyncOffset(newOffset) {
    lyricsSyncOffset = Math.round(newOffset * 4) / 4;
    lyricsSyncOffset = Math.max(-5, Math.min(5, lyricsSyncOffset));
    updateSyncDisplay();
    lastHighlightIndex = -1;
    if (lastCurrentTime >= 0) updateLyricHighlighting(lastCurrentTime);
  }

  function animateSyncToZero() {
    if (syncAnimFrame) cancelAnimationFrame(syncAnimFrame);
    var startOffset = lyricsSyncOffset;
    if (startOffset === 0) return;
    var duration = 300;
    var startTime = performance.now();
    function tick(now) {
      var elapsed = now - startTime;
      var t = Math.min(elapsed / duration, 1);
      var eased = 1 - Math.pow(1 - t, 3);
      lyricsSyncOffset = Math.round((startOffset * (1 - eased)) * 4) / 4;
      if (Math.abs(lyricsSyncOffset) < 0.001) lyricsSyncOffset = 0;
      updateSyncDisplay();
      lastHighlightIndex = -1;
      if (lastCurrentTime >= 0) updateLyricHighlighting(lastCurrentTime);
      if (t < 1) syncAnimFrame = requestAnimationFrame(tick);
      else { lyricsSyncOffset = 0; updateSyncDisplay(); syncAnimFrame = null; }
    }
    syncAnimFrame = requestAnimationFrame(tick);
  }

  syncBackBtn.addEventListener('click', function() { setSyncOffset(lyricsSyncOffset - 0.25); });
  syncForwardBtn.addEventListener('click', function() { setSyncOffset(lyricsSyncOffset + 0.25); });
  syncResetEl.addEventListener('click', function() { animateSyncToZero(); });
  updateSyncDisplay();

  // --- Lyrics font size controls ---
  function updateFontDisplay() {
    var pct = Math.round(lyricsFontScale * 100);
    fontValueEl.textContent = pct + '%';
    fontValueEl.classList.toggle('nonzero', lyricsFontScale !== 1.0);
    fontResetEl.classList.toggle('visible', lyricsFontScale !== 1.0);
  }

  function setFontScale(newScale) {
    lyricsFontScale = Math.max(0.5, Math.min(2.0, Math.round(newScale * 40) / 40));
    updateFontDisplay();
    updateLyricFontSizes();
  }

  function animateFontToDefault() {
    if (fontAnimFrame) cancelAnimationFrame(fontAnimFrame);
    var startScale = lyricsFontScale;
    if (startScale === 1.0) return;
    var duration = 300;
    var startTime = performance.now();
    function tick(now) {
      var elapsed = now - startTime;
      var t = Math.min(elapsed / duration, 1);
      var eased = 1 - Math.pow(1 - t, 3);
      lyricsFontScale = startScale + (1.0 - startScale) * eased;
      if (Math.abs(lyricsFontScale - 1.0) < 0.001) lyricsFontScale = 1.0;
      updateFontDisplay();
      updateLyricFontSizes();
      if (t < 1) fontAnimFrame = requestAnimationFrame(tick);
      else { lyricsFontScale = 1.0; updateFontDisplay(); updateLyricFontSizes(); fontAnimFrame = null; }
    }
    fontAnimFrame = requestAnimationFrame(tick);
  }

  fontDecreaseBtn.addEventListener('click', function() { setFontScale(lyricsFontScale - 0.1); });
  fontIncreaseBtn.addEventListener('click', function() { setFontScale(lyricsFontScale + 0.1); });
  fontResetEl.addEventListener('click', function() { animateFontToDefault(); });
  updateFontDisplay();

  // =====================================================
  //  Library: lazy-loaded song list
  // =====================================================
  var LIB_PAGE_SIZE = 30;
  var libPage = 0;
  var libTotal = 0;
  var libLoading = false;
  var libAllLoaded = false;
  var libInitialized = false;
  var libInitialLoadDone = false;

  var libListEl = document.getElementById('library-list');
  var libLoadingEl = document.getElementById('library-loading');
  var libEmptyEl = document.getElementById('library-empty');
  var libEndEl = document.getElementById('library-end');
  var libCountEl = document.getElementById('library-count');
  var libSentinel = document.getElementById('library-load-more-sentinel');

  function renderLibrarySong(song) {
    var thumbHtml = song.thumbnail
      ? '<img src="' + song.thumbnail + '" alt="">'
      : '<i class="fas fa-music"></i>';
    var title = song.title || song.filePath.split(/[/\\]/).pop();
    var artist = song.artist || 'Unknown';

    var pills = '';
    if (song.isMissing) pills += '<span class="pill pill-missing"><i class="fas fa-exclamation-triangle"></i> Missing</span>';
    if (song.isHashDuplicate) pills += '<span class="pill pill-dup"><i class="fas fa-clone"></i> Hash Dup</span>';
    if (song.hasMetadataDuplicates) pills += '<span class="pill pill-dup"><i class="fas fa-clone"></i> Meta Dup' + (song.metadataDuplicateCount > 1 ? ' (' + song.metadataDuplicateCount + ')' : '') + '</span>';

    return '<div class="library-song" data-id="' + song.id + '" data-filepath="' + escHtml(song.filePath || '') + '">' +
      '<div class="library-song-thumb" data-song-id="' + song.id + '">' + thumbHtml +
        '<div class="thumb-overlay"><i class="fas fa-play"></i></div>' +
        '<div class="thumb-now-playing"><span class="eq-bar"></span><span class="eq-bar"></span><span class="eq-bar"></span></div>' +
      '</div>' +
      '<div class="library-song-info">' +
        '<div class="library-song-title">' + escHtml(title) + '</div>' +
        '<div class="library-song-artist">' + escHtml(artist) + (pills ? ' ' + pills : '') + '</div>' +
      '</div>' +
      '<div class="library-song-duration">' + escHtml(song.durationDisplay) + '</div>' +
    '</div>';
  }

  function loadLibraryPage() {
    if (libLoading || libAllLoaded) return Promise.resolve();
    libLoading = true;
    libLoadingEl.style.display = '';
    libEndEl.style.display = 'none';

    return hostRequest('getLibrarySongs', {
      page: libPage + 1,
      pageSize: LIB_PAGE_SIZE,
      sortBy: 'dateAdded',
      descending: true,
      filterMissing: true,
      filterDuplicates: 'none'
    }).then(function(result) {
      var songs = result.songs || [];
      var total = result.total || 0;
      libTotal = total;

      if (libPage === 0) {
        libListEl.innerHTML = '';
        if (total === 0) {
          libEmptyEl.style.display = '';
          libLoadingEl.style.display = 'none';
          libCountEl.textContent = '';
          libAllLoaded = true;
          libLoading = false;
          return;
        }
        libEmptyEl.style.display = 'none';
        libCountEl.textContent = total + ' song' + (total !== 1 ? 's' : '');
      }

      songs.forEach(function(song) {
        libListEl.insertAdjacentHTML('beforeend', renderLibrarySong(song));
      });
      updateLibraryPlayingState();

      libPage++;
      if (songs.length < LIB_PAGE_SIZE || libPage * LIB_PAGE_SIZE >= total) {
        libAllLoaded = true;
        libLoadingEl.style.display = 'none';
        if (libPage * LIB_PAGE_SIZE >= total && songs.length > 0) {
          libEndEl.style.display = '';
        }
      } else {
        libLoadingEl.style.display = 'none';
      }

      libLoading = false;
    }).catch(function(err) {
      console.error('Library load failed:', err);
      libLoadingEl.style.display = 'none';
      libLoading = false;
    });
  }

  // IntersectionObserver: load more when sentinel is visible
  var libObserver = null;
  function setupLibraryObserver() {
    if (libObserver) return;
    libObserver = new IntersectionObserver(function(entries) {
      if (entries.some(function(e) { return e.isIntersecting; })) {
        if (!libAllLoaded && !libLoading) {
          loadLibraryPage();
        }
      }
    }, { root: document.getElementById('view-library'), threshold: 0.1 });
    if (libSentinel) libObserver.observe(libSentinel);
  }

  // When the Library tab is clicked, load songs (once)
  document.querySelector('[data-view="library"]').addEventListener('click', function() {
    if (!libInitialized) {
      libInitialized = true;
      setupLibraryObserver();
      loadLibraryPage();
    }
  });

  // Also listen for library updates from C# (e.g. after scan)
  function onLibraryUpdated() {
    libPage = 0;
    libTotal = 0;
    libAllLoaded = false;
    libInitialized = true;
    libListEl.innerHTML = '';
    libEmptyEl.style.display = 'none';
    libEndEl.style.display = 'none';
    libCountEl.textContent = '';
    setupLibraryObserver();
    loadLibraryPage();
  }

  // Listen for 'libraryUpdated' events from C#
  var origOnEvent = onEvent;
  onEvent = function(eventName, data) {
    if (eventName === 'libraryUpdated') {
      onLibraryUpdated();
      return;
    }
    origOnEvent(eventName, data);
  };

  // =====================================================
  //  Search
  // =====================================================
  var searchInput = document.getElementById('search-input');
  var searchTab = document.getElementById('sidebar-tab-search');
  var searchView = document.getElementById('view-search');
  var searchListEl = document.getElementById('search-results-list');
  var searchCountEl = document.getElementById('search-results-count');
  var searchTitleEl = document.getElementById('search-results-title');
  var searchNoResults = document.getElementById('search-no-results');
  var searchDebounceTimer = null;
  var lastSearchQuery = '';

  function activateSearchView() {
    document.querySelectorAll('.sidebar-tab').forEach(function(t) { t.classList.remove('active'); });
    searchTab.classList.add('active');
    document.querySelectorAll('#center .view').forEach(function(v) { v.classList.remove('active'); });
    searchView.classList.add('active');
  }

  function renderSearchResults(query, data) {
    var results = data.results || [];
    searchListEl.innerHTML = '';
    searchTitleEl.textContent = 'Results for "' + escHtml(query) + '"';
    searchCountEl.textContent = results.length + ' found';

    if (results.length === 0) {
      searchNoResults.style.display = '';
      return;
    }
    searchNoResults.style.display = 'none';

    results.forEach(function(song) {
      searchListEl.insertAdjacentHTML('beforeend', renderLibrarySong(song));
    });
  }

  function performLocalSearch(query) {
    lastSearchQuery = query;
    searchTab.style.display = '';
    activateSearchView();
    searchListEl.innerHTML = '<div class="library-loading"><i class="fas fa-circle-notch fa-spin"></i><span>Searching...</span></div>';
    searchNoResults.style.display = 'none';

    hostRequest('searchLibrary', { query: query, maxResults: 100 }).then(function(data) {
      if (query !== lastSearchQuery) return;
      renderSearchResults(query, data);
    }).catch(function(err) {
      console.error('Search failed:', err);
      searchListEl.innerHTML = '';
      searchNoResults.style.display = '';
    });
  }

  searchInput.addEventListener('input', function() {
    var q = searchInput.value.trim();
    if (searchDebounceTimer) clearTimeout(searchDebounceTimer);
    if (q.length <= 3) return;
    searchDebounceTimer = setTimeout(function() {
      performLocalSearch(q);
    }, 300);
  });

  // Right-click on search results (library + YouTube)
  searchListEl.addEventListener('contextmenu', function(e) {
    var songEl = e.target.closest('.library-song');
    if (!songEl) return;
    e.preventDefault();
    var id = songEl.dataset.id || songEl.dataset.ytid || null;
    if (id) showContextMenu(e.clientX, e.clientY, id, songEl);
  });

  // Artwork click on search results: play immediately
  searchListEl.addEventListener('click', function(e) {
    var overlay = e.target.closest('.thumb-overlay');
    if (!overlay) return;
    var songEl = overlay.closest('.library-song');
    if (!songEl) return;
    e.stopPropagation();
    if (songEl.dataset.id) {
      hostSend('playLibrarySong', { songId: songEl.dataset.id });
    } else if (songEl.dataset.ytid) {
      startYouTubeDownload(songEl, 'play');
    }
  });

  // Long-press for touch users on search results
  searchListEl.addEventListener('touchstart', function(e) {
    var songEl = e.target.closest('.library-song');
    if (!songEl) return;
    longPressTriggered = false;
    var touch = e.touches[0];
    longPressTimer = setTimeout(function() {
      longPressTriggered = true;
      showContextMenu(touch.clientX, touch.clientY, songEl.dataset.id, songEl);
      if (navigator.vibrate) navigator.vibrate(30);
    }, 500);
  }, { passive: true });

  searchListEl.addEventListener('touchend', function(e) {
    if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; }
    if (longPressTriggered) {
      e.preventDefault();
      longPressTriggered = false;
    }
  });

  searchListEl.addEventListener('touchmove', function() {
    if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; }
  }, { passive: true });

  // External search button (YouTube via YoutubeExplode)
  document.getElementById('btn-external-search').addEventListener('click', function() {
    var q = lastSearchQuery || searchInput.value.trim();
    if (!q) return;
    var btn = this;
    btn.disabled = true;
    btn.innerHTML = '<i class="fas fa-circle-notch fa-spin"></i> Searching...';

    hostRequest('searchYoutube', { query: q, maxResults: 10 }).then(function(data) {
      btn.disabled = false;
      btn.innerHTML = '<i class="fab fa-youtube"></i> Search YouTube';
      var ytResults = data.results || [];
      renderYouTubeResults(ytResults);
    }).catch(function(err) {
      console.error('YouTube search failed:', err);
      btn.disabled = false;
      btn.innerHTML = '<i class="fab fa-youtube"></i> Search YouTube';
    });
  });

  function renderYouTubeResult(item) {
    var dur = item.duration || '';
    return '<div class="library-song yt-search-result" data-ytid="' + escHtml(item.videoId || '') + '" data-title="' + escHtml(item.title || '') + '" data-author="' + escHtml(item.author || '') + '">' +
      '<div class="library-song-thumb yt-search-thumb">' +
        '<i class="fab fa-youtube yt-search-icon"></i>' +
        '<div class="thumb-overlay"><i class="fas fa-play"></i></div>' +
        '<div class="yt-download-progress" style="display:none"><div class="yt-download-bar"></div><div class="yt-download-icon"><i class="fas fa-circle-notch fa-spin"></i></div></div>' +
      '</div>' +
      '<div class="library-song-info">' +
        '<div class="library-song-title">' + escHtml(item.title || '') + '</div>' +
        '<div class="library-song-artist">' + escHtml(item.author || 'Unknown') +
          ' <span class="pill pill-quality yt-pill"><i class="fab fa-youtube"></i> YouTube</span>' +
        '</div>' +
      '</div>' +
      '<div class="library-song-duration">' + escHtml(dur) + '</div>' +
    '</div>';
  }

  function renderYouTubeResults(results) {
    if (!results.length) return;
    var header = '<div class="search-section-header"><i class="fab fa-youtube"></i> YouTube Results (' + results.length + ')</div>';
    searchListEl.insertAdjacentHTML('beforeend', header);
    results.forEach(function(item) {
      searchListEl.insertAdjacentHTML('beforeend', renderYouTubeResult(item));
    });
    searchNoResults.style.display = 'none';
    // Update total count
    var localCount = searchListEl.querySelectorAll('.library-song:not(.yt-search-result)').length;
    var ytCount = results.length;
    searchCountEl.textContent = (localCount + ytCount) + ' found';
  }

  // =====================================================
  //  YouTube download + play
  // =====================================================
  function startYouTubeDownload(songEl, action) {
    var ytid = songEl.dataset.ytid;
    var title = songEl.dataset.title || '';
    var author = songEl.dataset.author || '';
    if (!ytid) return;

    var progWrap = songEl.querySelector('.yt-download-progress');
    var progBar = songEl.querySelector('.yt-download-bar');
    var progIcon = songEl.querySelector('.yt-download-icon i');
    if (!progWrap || !progIcon) return;

    songEl.classList.add('yt-downloading');
    progWrap.style.display = '';
    progBar.style.width = '0%';
    progBar.style.background = '';
    progIcon.className = 'fas fa-circle-notch fa-spin';
    var overlay = songEl.querySelector('.thumb-overlay');
    if (overlay) overlay.style.display = 'none';

    function showError() {
      progIcon.className = 'fas fa-exclamation';
      progBar.style.width = '100%';
      progBar.style.background = '#f44';
      setTimeout(function() {
        songEl.classList.remove('yt-downloading');
        progWrap.style.display = 'none';
        if (overlay) overlay.style.display = '';
      }, 2000);
    }

    hostRequest('downloadAndPlayYouTube', { videoId: ytid, action: action, title: title, author: author }, 120000).then(function(res) {
      if (res && res.error) showError();
    }).catch(showError);
  }

  function onYtDownloadProgress(data) {
    var ytid = data.videoId;
    var stage = data.stage;
    var progress = data.progress || 0;
    var el = searchListEl.querySelector('[data-ytid="' + ytid + '"]');
    if (!el) return;
    var progWrap = el.querySelector('.yt-download-progress');
    var progBar = el.querySelector('.yt-download-bar');
    var progIcon = el.querySelector('.yt-download-icon i');
    if (!progWrap || !progIcon) return;

    if (stage === 'downloading') {
      progWrap.style.display = '';
      el.classList.add('yt-downloading');
      progBar.style.width = Math.min(progress, 99) + '%';
      progBar.style.background = '';
      progIcon.className = 'fas fa-circle-notch fa-spin';
    } else if (stage === 'importing') {
      progBar.style.width = '100%';
      progBar.style.background = '';
      progIcon.className = 'fas fa-compact-disc fa-spin';
    } else if (stage === 'playing') {
      progIcon.className = 'fas fa-play';
    } else if (stage === 'done') {
      progIcon.className = 'fas fa-check';
      progBar.style.width = '100%';
      progBar.style.background = '#4f4';
      setTimeout(function() {
        el.classList.remove('yt-downloading');
        progWrap.style.display = 'none';
        progBar.style.width = '0%';
        var overlay = el.querySelector('.thumb-overlay');
        if (overlay) overlay.style.display = '';
      }, 1000);
    } else if (stage === 'error') {
      progIcon.className = 'fas fa-exclamation';
      progBar.style.width = '100%';
      progBar.style.background = '#f44';
      setTimeout(function() {
        el.classList.remove('yt-downloading');
        progWrap.style.display = 'none';
        var ov = el.querySelector('.thumb-overlay');
        if (ov) ov.style.display = '';
      }, 2000);
    }
  }

  // --- Slider value label updates ---
  function updateSliderLabels() {
    volSliders.forEach(function(sl, i) {
      if (sl && volumeValueEls[i]) volumeValueEls[i].textContent = sl.value;
    });
    cfSliders.forEach(function(sl, i) {
      if (sl && crossfadeValueEls[i]) crossfadeValueEls[i].textContent = (sl.value / 10).toFixed(1);
    });
  }
  volSliders.forEach(function(sl) {
    if (sl) sl.addEventListener('input', updateSliderLabels);
  });
  cfSliders.forEach(function(sl) {
    if (sl) sl.addEventListener('input', updateSliderLabels);
  });
  updateSliderLabels();

  // =====================================================
  //  Context menu for library songs
  // =====================================================
  var ctxMenu = document.getElementById('context-menu');
  var ctxSongId = null;
  var ctxSongYtEl = null;
  var longPressTimer = null;
  var longPressTriggered = false;

  function showContextMenu(x, y, songId, songEl) {
    ctxSongId = songId;
    ctxSongYtEl = songEl && songEl.dataset.ytid ? songEl : null;
    ctxMenu.style.display = 'block';
    // Clamp to viewport
    var mw = ctxMenu.offsetWidth, mh = ctxMenu.offsetHeight;
    if (x + mw > window.innerWidth) x = window.innerWidth - mw - 4;
    if (y + mh > window.innerHeight) y = window.innerHeight - mh - 4;
    ctxMenu.style.left = x + 'px';
    ctxMenu.style.top = y + 'px';
  }

  function hideContextMenu() {
    ctxMenu.style.display = 'none';
    ctxSongId = null;
    ctxSongYtEl = null;
  }

  // Right-click on library songs
  libListEl.addEventListener('contextmenu', function(e) {
    var songEl = e.target.closest('.library-song');
    if (!songEl) return;
    e.preventDefault();
    showContextMenu(e.clientX, e.clientY, songEl.dataset.id, songEl);
  });

  // Artwork click: play immediately
  libListEl.addEventListener('click', function(e) {
    var overlay = e.target.closest('.thumb-overlay');
    if (!overlay) return;
    var songEl = overlay.closest('.library-song');
    if (!songEl) return;
    e.stopPropagation();
    hostSend('playLibrarySong', { songId: songEl.dataset.id });
  });

  // Context menu actions
  ctxMenu.addEventListener('click', function(e) {
    var item = e.target.closest('.context-menu-item');
    if (!item || !ctxSongId) return;
    var action = item.dataset.action;
    var songId = ctxSongId;
    var ytEl = ctxSongYtEl;
    hideContextMenu();
    if (ytEl) {
      var ytAction = action === 'play' ? 'play' : action === 'playNext' ? 'playNext' : 'queue';
      startYouTubeDownload(ytEl, ytAction);
    } else if (action === 'play') {
      hostSend('playLibrarySong', { songId: songId });
    } else if (action === 'playNext') {
      hostSend('playLibrarySongNext', { songId: songId });
    } else if (action === 'addToQueue') {
      hostSend('queueLibrarySong', { songId: songId });
    }
  });

  // Hide context menu on click elsewhere / scroll / key
  document.addEventListener('click', function(e) {
    if (!ctxMenu.contains(e.target)) hideContextMenu();
  });
  document.addEventListener('scroll', hideContextMenu, true);
  document.addEventListener('keydown', function(e) { if (e.key === 'Escape') hideContextMenu(); });

  // Long-press for touch users on library songs
  libListEl.addEventListener('touchstart', function(e) {
    var songEl = e.target.closest('.library-song');
    if (!songEl) return;
    longPressTriggered = false;
    var touch = e.touches[0];
      longPressTimer = setTimeout(function() {
      longPressTriggered = true;
      showContextMenu(touch.clientX, touch.clientY, songEl.dataset.id, songEl);
      // Prevent ghost click
      if (navigator.vibrate) navigator.vibrate(30);
    }, 500);
  }, { passive: true });

  libListEl.addEventListener('touchend', function(e) {
    if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; }
    if (longPressTriggered) {
      e.preventDefault();
      longPressTriggered = false;
    }
  });

  libListEl.addEventListener('touchmove', function() {
    if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; }
  }, { passive: true });

  // --- Accent color extraction from thumbnail ---
  function resetAccentColors() {
    playerBar.style.removeProperty('--accent-1');
    playerBar.style.removeProperty('--accent-2');
    playerBar.style.removeProperty('--accent-3');
  }

  function extractAccentColors(src) {
    var img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = function() {
      try {
        var canvas = document.createElement('canvas');
        var ctx = canvas.getContext('2d');
        var S = 40;
        canvas.width = S; canvas.height = S;
        ctx.drawImage(img, 0, 0, S, S);
        var data = ctx.getImageData(0, 0, S, S).data;

        var buckets = {};
        for (var i = 0; i < data.length; i += 4) {
          var r = data[i], g = data[i+1], b = data[i+2], a = data[i+3];
          if (a < 128) continue;
          var lum = 0.299 * r + 0.587 * g + 0.114 * b;
          if (lum < 20 || lum > 240) continue;
          var sat = Math.max(r, g, b) - Math.min(r, g, b);
          if (sat < 20) continue;
          var br = Math.round(r / 48) * 48;
          var bg = Math.round(g / 48) * 48;
          var bb = Math.round(b / 48) * 48;
          var key = br + ',' + bg + ',' + bb;
          if (!buckets[key]) buckets[key] = { r: 0, g: 0, b: 0, n: 0, lum: 0 };
          buckets[key].r += r;
          buckets[key].g += g;
          buckets[key].b += b;
          buckets[key].lum += lum;
          buckets[key].n++;
        }

        var sorted = Object.values(buckets)
          .filter(function(b) { return b.n >= 3; })
          .sort(function(a, b) { return b.n - a.n; });

        if (sorted.length < 1) { resetAccentColors(); return; }

        var c1 = sorted[0];
        var c1r = Math.round(c1.r / c1.n), c1g = Math.round(c1.g / c1.n), c1b = Math.round(c1.b / c1.n);

        var c2 = null;
        for (var j = 1; j < sorted.length; j++) {
          var d = sorted[j];
          var dr = Math.round(d.r / d.n), dg = Math.round(d.g / d.n), db = Math.round(d.b / d.n);
          var dist = Math.abs(dr - c1r) + Math.abs(dg - c1g) + Math.abs(db - c1b);
          if (dist > 60) { c2 = { r: dr, g: dg, b: db }; break; }
        }
        if (!c2) {
          c2 = { r: Math.max(0, c1r - 40), g: Math.max(0, c1g - 40), b: Math.max(0, c1b - 40) };
        }

        var dark = 0.35;
        var a1 = 'rgb(' + Math.round(c1r * dark) + ',' + Math.round(c1g * dark) + ',' + Math.round(c1b * dark) + ')';
        var a2 = 'rgb(' + Math.round(c2.r * dark) + ',' + Math.round(c2.g * dark) + ',' + Math.round(c2.b * dark) + ')';
        var a3 = 'rgb(' + Math.round((c1r + c2.r) / 2 * dark * 0.7) + ',' + Math.round((c1g + c2.g) / 2 * dark * 0.7) + ',' + Math.round((c1b + c2.b) / 2 * dark * 0.7) + ')';

        playerBar.style.setProperty('--accent-1', a1);
        playerBar.style.setProperty('--accent-2', a2);
        playerBar.style.setProperty('--accent-3', a3);
      } catch(e) {
        resetAccentColors();
      }
    };
    img.onerror = function() { resetAccentColors(); };
    img.src = src;
  }

  // =====================================================
  //  Music Share modal
  // =====================================================
  var shareModal = document.getElementById('share-modal');
  var shareBtn = document.getElementById('btn-share');
  var shareModalClose = document.getElementById('share-modal-close');
  var shareStartBtn = document.getElementById('btn-share-start');
  var shareMetadataOnly = document.getElementById('share-metadata-only');
  var shareIdDisplay = document.getElementById('share-id-display');
  var shareIdText = document.getElementById('share-id-text');
  var shareStatusBox = document.getElementById('share-status-box');
  var shareStatusDot = document.getElementById('share-status-dot');
  var shareStatusText = document.getElementById('share-status-text');
  var shareStatsText = document.getElementById('share-stats-text');
  var shareCodeInput = document.getElementById('share-code-input');
  var receiveStartBtn = document.getElementById('btn-receive-start');
  var receiveStatusBox = document.getElementById('receive-status-box');
  var receiveStatusDot = document.getElementById('receive-status-dot');
  var receiveStatusText = document.getElementById('receive-status-text');
  var receiveStatsText = document.getElementById('receive-stats-text');
  var receiveMetadataBox = document.getElementById('receive-metadata-box');
  var receiveThumb = document.getElementById('receive-thumb');
  var receiveTitle = document.getElementById('receive-title');
  var receiveArtist = document.getElementById('receive-artist');
  var receiveElapsed = document.getElementById('receive-elapsed');
  var receiveTotal = document.getElementById('receive-total');
  var receiveProgressFill = document.getElementById('receive-progress-fill');
  var shareReceiveAudio = document.getElementById('share-receive-audio');
  var shareIsSharing = false;
  var shareIsReceiving = false;

  function shareOpenModal() { shareModal.style.display = 'flex'; }
  function shareCloseModal() { shareModal.style.display = 'none'; }

  shareBtn.addEventListener('click', shareOpenModal);
  shareModalClose.addEventListener('click', shareCloseModal);
  shareModal.addEventListener('click', function(e) { if (e.target === shareModal) shareCloseModal(); });

  function shareSetStatus(type, status, text, stats) {
    var dot = type === 'share' ? shareStatusDot : receiveStatusDot;
    var stxt = type === 'share' ? shareStatusText : receiveStatusText;
    var sbox = type === 'share' ? shareStatusBox : receiveStatusBox;
    var sstat = type === 'share' ? shareStatsText : receiveStatsText;
    sbox.style.display = '';
    dot.className = 'share-status-dot status-' + status;
    stxt.textContent = text;
    sstat.textContent = stats || '';
  }

  function shareClearStatus(type) {
    var sbox = type === 'share' ? shareStatusBox : receiveStatusBox;
    sbox.style.display = 'none';
  }

  // Start / stop sharing
  shareStartBtn.addEventListener('click', function() {
    if (shareIsSharing) {
      hostSend('musicShareStop');
      shareStartBtn.textContent = 'Stopping...';
      shareStartBtn.disabled = true;
    } else {
      var metadataOnly = shareMetadataOnly.checked;
      hostSend('musicShareStart', { metadataOnly: metadataOnly });
      shareStartBtn.textContent = 'Starting...';
      shareStartBtn.disabled = true;
    }
  });

  // Enable/disable receive button based on code input
  shareCodeInput.addEventListener('input', function() {
    shareCodeInput.value = shareCodeInput.value.replace(/[^0-9]/g, '');
    receiveStartBtn.disabled = shareCodeInput.value.length !== 6 || shareIsReceiving;
  });

  // Start / stop receiving
  receiveStartBtn.addEventListener('click', function() {
    if (shareIsReceiving) {
      hostSend('musicShareDisconnect');
      receiveStartBtn.textContent = 'Disconnecting...';
      receiveStartBtn.disabled = true;
    } else {
      var receiveAudio = shareReceiveAudio.checked;
      hostSend('musicShareConnect', { sessionId: shareCodeInput.value, receiveAudio: receiveAudio });
      receiveStartBtn.textContent = 'Connecting...';
      receiveStartBtn.disabled = true;
    }
  });

  function shareFormatStatus(status) {
    var map = { idle:'Idle', connecting:'Connecting...', connected:'Connected', streaming:'Streaming', buffering:'Buffering', error:'Error', disconnected:'Disconnected' };
    return map[status] || status;
  }

  // Handle musicShare events from C#
  var origOnEvent2 = onEvent;
  onEvent = function(eventName, data) {
    if (eventName === 'musicShareStatus') {
      var isSharing = data.sharing;
      var isReceiving = data.receiving;
      var status = data.status;

      if (isSharing) {
        shareIsSharing = true;
        shareStartBtn.textContent = 'Stop Sharing';
        shareStartBtn.className = 'share-btn share-btn-danger';
        shareStartBtn.disabled = false;
        shareMetadataOnly.disabled = true;
        shareCodeInput.disabled = true;
        receiveStartBtn.disabled = true;

        if (data.sessionId) {
          shareIdDisplay.style.display = '';
          shareIdText.textContent = data.sessionId;
        }
        shareSetStatus('share', status, shareFormatStatus(status),
          data.metadataOnly ? 'Metadata Only' : 'Metadata + Audio');
      } else {
        shareIsSharing = false;
        shareStartBtn.textContent = 'Start Sharing';
        shareStartBtn.className = 'share-btn share-btn-accent';
        shareStartBtn.disabled = false;
        shareMetadataOnly.disabled = false;
        shareCodeInput.disabled = false;
        shareIdDisplay.style.display = 'none';
        shareClearStatus('share');
        receiveStartBtn.disabled = shareCodeInput.value.length !== 6;
      }

      if (isReceiving) {
        shareIsReceiving = true;
        receiveStartBtn.textContent = 'Disconnect';
        receiveStartBtn.className = 'share-btn share-btn-danger';
        receiveStartBtn.disabled = false;
        shareCodeInput.disabled = true;
        shareStartBtn.disabled = true;
        shareMetadataOnly.disabled = true;
        shareSetStatus('receive', status, shareFormatStatus(status));
      } else {
        shareIsReceiving = false;
        receiveStartBtn.textContent = 'Connect';
        receiveStartBtn.className = 'share-btn share-btn-accent';
        receiveStartBtn.disabled = shareCodeInput.value.length !== 6;
        shareCodeInput.disabled = false;
        shareStartBtn.disabled = false;
        shareMetadataOnly.disabled = false;
        shareClearStatus('receive');
        receiveMetadataBox.style.display = 'none';
      }

      return;
    }

    if (eventName === 'musicShareError') {
      // Show error briefly in status
      if (shareIsSharing || shareStartBtn.textContent === 'Starting...') {
        shareSetStatus('share', 'error', 'Error: ' + data.error);
      } else {
        shareSetStatus('receive', 'error', 'Error: ' + data.error);
      }
      shareStartBtn.textContent = shareIsSharing ? 'Stop Sharing' : 'Start Sharing';
      shareStartBtn.className = shareIsSharing ? 'share-btn share-btn-danger' : 'share-btn share-btn-accent';
      shareStartBtn.disabled = false;
      receiveStartBtn.textContent = shareIsReceiving ? 'Disconnect' : 'Connect';
      receiveStartBtn.className = shareIsReceiving ? 'share-btn share-btn-danger' : 'share-btn share-btn-accent';
      receiveStartBtn.disabled = shareCodeInput.value.length !== 6 || shareIsReceiving;
      return;
    }

    if (eventName === 'musicShareMetadataReceived') {
      receiveMetadataBox.style.display = '';
      receiveTitle.textContent = data.title || 'Unknown';
      receiveArtist.textContent = data.artist || '—';
      receiveElapsed.textContent = formatTime(data.elapsedSeconds || 0);
      receiveTotal.textContent = formatTime(data.totalSeconds || 0);
      var pct = data.totalSeconds > 0 ? (data.elapsedSeconds / data.totalSeconds * 100) : 0;
      receiveProgressFill.style.width = pct + '%';
      if (data.thumbnailData) {
        receiveThumb.innerHTML = '<img src="data:image/jpeg;base64,' + data.thumbnailData + '" alt="">';
      } else {
        receiveThumb.innerHTML = '<i class="fas fa-music"></i>';
      }
      return;
    }

    origOnEvent2(eventName, data);
  };

  // Fetch initial status
  hostRequest('musicShareGetStatus', {}).then(function(data) {
    if (data.sharing) {
      shareIsSharing = true;
      shareStartBtn.textContent = 'Stop Sharing';
      shareStartBtn.className = 'share-btn share-btn-danger';
      shareMetadataOnly.disabled = true;
      if (data.sessionId) {
        shareIdDisplay.style.display = '';
        shareIdText.textContent = data.sessionId;
      }
      shareSetStatus('share', data.status, shareFormatStatus(data.status),
        data.metadataOnly ? 'Metadata Only' : 'Metadata + Audio');
    }
    if (data.receiving) {
      shareIsReceiving = true;
      receiveStartBtn.textContent = 'Disconnect';
      receiveStartBtn.className = 'share-btn share-btn-danger';
      shareCodeInput.disabled = true;
      shareSetStatus('receive', data.status, shareFormatStatus(data.status));
    }
  }).catch(function() {});

})();
