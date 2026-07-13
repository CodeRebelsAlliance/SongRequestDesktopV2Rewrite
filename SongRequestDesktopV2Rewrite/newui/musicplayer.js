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

  // Lyrics
  const lyricsContainer = document.getElementById('expanded-lyrics');
  const lyricsProvider = document.getElementById('lyrics-provider');
  let syncedLyrics = [];
  let plainLyricLines = [];
  let hasSyncedLyrics = false;
  let totalTimeSecs = 0;
  let lastHighlightIndex = -1;
  let lastCurrentTime = 0;

  // --- Expand / collapse ---
  function setExpanded(expanded) {
    playerBar.classList.toggle('expanded', expanded);
    expandBtn.innerHTML = expanded ? '<i class="fas fa-chevron-down"></i>' : '<i class="fas fa-chevron-up"></i>';
    if (expanded) {
      leftSidebar.classList.add('collapsed');
      rightSidebar.classList.add('collapsed');
      topbar.style.display = 'none';
      bodyEl.style.display = 'none';
      if (lastCurrentTime >= 0) updateLyricHighlighting(lastCurrentTime);
    } else {
      leftSidebar.classList.remove('collapsed');
      rightSidebar.classList.remove('collapsed');
      topbar.style.display = '';
      bodyEl.style.display = '';
    }
  }

  expandBtn.addEventListener('click', () => setExpanded(true));
  collapseBtn.addEventListener('click', () => setExpanded(false));

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

      // Reset lyrics for new song
      syncedLyrics = [];
      plainLyricLines = [];
      hasSyncedLyrics = false;
      lastHighlightIndex = -1;
      lastCurrentTime = 0;
      renderLyricsLoading();
      hostSend('requestLyrics');
    }

    // Thumbnail
    if (data.thumbnail) {
      const imgHtml = `<img src="${data.thumbnail}" alt="artwork" style="width:100%;height:100%;object-fit:cover">`;
      // Collapsed
      colThumb.innerHTML = imgHtml;
      // Expanded
      expArtwork.innerHTML = imgHtml;
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
      const pct = effectiveTotal > 0 ? (data.currentTime / effectiveTotal * 100) : 0;
      colProgressFill.style.width = pct + '%';
      expProgressFill.style.width = pct + '%';
      const fmt = formatTime(data.currentTime);
      colProgressCurrent.textContent = fmt;
      expProgressCurrent.textContent = fmt;

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

  function updateQueue(data) {
    lastQueueData = data;
    lastPlaybackActive = !!data.isPlaying;
    renderQueue(data.queue || [], lastPlaybackActive);
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

  // Receive events from C# (matching app.js pattern)
  function handleIncoming(json) {
    try {
      const msg = JSON.parse(json);
      if (msg.type === 'event') {
        onEvent(msg.eventName, msg.data || {});
      } else if (msg.type === 'response' && msg.volume != null) {
        const v = Math.round(msg.volume * 100);
        const c = Math.round((msg.crossfade || 4) * 10);
        volSliders.forEach(sl => { if (sl) sl.value = v; });
        cfSliders.forEach(sl => { if (sl) sl.value = c; });
        const iconName = v == 0 ? 'fa-volume-mute' : 'fa-volume-up';
        volIcons.forEach(ic => { if (ic) ic.className = `fas ${iconName}`; });
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

    if (data.syncedLyrics && data.syncedLyrics.length > 0) {
      syncedLyrics = data.syncedLyrics;
      hasSyncedLyrics = true;
      data.syncedLyrics.forEach(function(line) {
        var div = document.createElement('div');
        div.className = 'lyrics-line';
        div.textContent = line.text;
        lyricsContainer.appendChild(div);
      });
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

    if (lyricsProvider) lyricsProvider.textContent = data.provider ? 'Lyrics: ' + data.provider : '';
    lastHighlightIndex = -1;
    if (lastCurrentTime >= 0) updateLyricHighlighting(lastCurrentTime);
  }

  function updateLyricHighlighting(currentTime) {
    var lines = lyricsContainer.querySelectorAll('.lyrics-line');
    if (lines.length === 0) return;

    var index = -1;
    if (hasSyncedLyrics && syncedLyrics.length > 0) {
      for (var i = 0; i < syncedLyrics.length; i++) {
        if (syncedLyrics[i].time <= currentTime) index = i;
        else break;
      }
      if (index < 0) index = 0;
    } else if (plainLyricLines.length > 0) {
      var total = totalTimeSecs || 1;
      var progress = Math.min(currentTime / total, 1);
      index = Math.round(progress * Math.max(0, plainLyricLines.length - 1));
    }

    if (index < 0 || index >= lines.length) return;
    if (index === lastHighlightIndex) return;

    lastHighlightIndex = index;

    lines.forEach(function(line, i) {
      if (i === index) {
        line.className = 'lyrics-line active';
        if (playerBar.classList.contains('expanded')) {
          line.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
      } else if (i === index - 1) {
        line.className = 'lyrics-line prev';
      } else {
        line.className = 'lyrics-line';
      }
    });
  }

})();
