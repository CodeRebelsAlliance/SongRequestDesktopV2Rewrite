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
  const lyricsWrapper = document.getElementById('lyrics-wrapper');
  const lyricsHiddenPlaceholder = document.getElementById('lyrics-hidden-placeholder');
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

  // Lyrics font size (persisted)
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
      recentlyPlayedActive = (view === 'recently-played');
      if (view === 'home') loadHomeFeed(false);
    });
  });

  // --- Home tab: playlists / recommendations / recently listened / new ---
  var homeLoadingEl = document.getElementById('home-loading');
  var homeEmptyEl = document.getElementById('home-empty');
  var homeStatsEl = document.getElementById('home-stats');
  var homeFeedEl = document.getElementById('home-feed');
  var homeRows = {
    playlists: { section: document.getElementById('home-row-playlists'), scroller: document.getElementById('home-playlists') },
    recommended: { section: document.getElementById('home-row-recommended'), scroller: document.getElementById('home-recommended') },
    recent: { section: document.getElementById('home-row-recent'), scroller: document.getElementById('home-recent') },
    fresh: { section: document.getElementById('home-row-new'), scroller: document.getElementById('home-new') }
  };
  var HOME_CARD_COUNT = 12;
  var homeFeedData = null;
  var homeFeedLoading = false;

  function homeRelativeTime(value) {
    if (!value) return '';
    var then = new Date(value);
    if (isNaN(then.getTime())) return '';
    var secs = Math.max(0, Math.round((Date.now() - then.getTime()) / 1000));
    if (secs < 60) return 'just now';
    var mins = Math.round(secs / 60);
    if (mins < 60) return mins + 'm ago';
    var hours = Math.round(mins / 60);
    if (hours < 24) return hours + 'h ago';
    var days = Math.round(hours / 24);
    if (days < 7) return days + 'd ago';
    return then.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }

  function homeThumbHtml(item, fallbackIcon) {
    var thumb = item && item.thumbnail;
    var songId = (item && item.songId) || '';
    var inner = thumb
      ? '<img src="' + escHtml(thumb) + '" alt="" loading="lazy">'
      : '<i class="fas ' + (fallbackIcon || 'fa-music') + '"></i>';
    // artwork that is still being downscaled shows a placeholder carrying the song id,
    // so patchThumbnails() can drop it in without re-rendering the row
    var missing = (!thumb && songId) ? ' thumb-pending' : '';
    return '<div class="home-card-thumb' + missing + '" data-songid="' + escHtml(songId) + '">' + inner +
      '<button class="home-card-play" data-play="1" title="Play"><i class="fas fa-play"></i></button>' +
    '</div>';
  }

  function homeSongCardHtml(item, options) {
    options = options || {};
    var sub = escHtml(item.artist || 'Unknown');
    var meta = '';
    if (options.badge) {
      meta = '<div class="home-card-meta"><span class="home-card-badge' + (options.badgeClass ? ' ' + options.badgeClass : '') + '" title="' +
        escHtml(options.badge) + '">' + escHtml(options.badge) + '</span></div>';
    } else if (options.meta) {
      meta = '<div class="home-card-meta"><span class="home-card-sub">' + escHtml(options.meta) + '</span></div>';
    }
    return '<div class="home-card home-song-card" data-songid="' + escHtml(item.songId || '') + '" data-filepath="' + escHtml(item.filePath || '') + '"' +
        (item.reason ? ' title="' + escHtml(item.reason) + '"' : '') + '>' +
      homeThumbHtml(item) +
      '<div class="home-card-title">' + escHtml(item.title || 'Unknown track') + '</div>' +
      '<div class="home-card-sub">' + sub + '</div>' +
      meta +
    '</div>';
  }

  function homePlaylistCardHtml(item) {
    var thumbs = item.thumbnail
      ? '<div class="home-card-playlist-thumb"><img src="' + escHtml(item.thumbnail) + '" alt=""><div class="ph"><i class="fas fa-music"></i></div>' +
        '<div class="ph"><i class="fas fa-music"></i></div><div class="ph"><i class="fas fa-list"></i></div></div>'
      : '<span class="home-playlist-icon">' + plIconHtml(item.icon) + '</span>';
    var played = item.isDynamic
      ? (item.description || 'Dynamic playlist')
      : (item.lastPlayedUtc ? 'Played ' + homeRelativeTime(item.lastPlayedUtc) : 'Never played');
    var count = item.songCount + ' song' + (item.songCount === 1 ? '' : 's');
    return '<div class="home-card home-playlist-card' + (item.isDynamic ? ' is-dynamic' : '') + '" data-playlistid="' + escHtml(item.id || '') + '">' +
      '<div class="home-card-thumb">' + thumbs +
        '<button class="home-card-play" data-play="1" title="Play playlist"><i class="fas fa-play"></i></button>' +
      '</div>' +
      '<div class="home-card-title">' + escHtml(item.name || 'Playlist') + '</div>' +
      '<div class="home-card-sub">' + escHtml(count) + '</div>' +
      '<div class="home-card-meta"><span class="home-card-sub">' + escHtml(played) + '</span></div>' +
    '</div>';
  }

  function renderHomeRow(key, html, visible) {
    var row = homeRows[key];
    if (!row || !row.section || !row.scroller) return;
    if (!visible) { row.section.style.display = 'none'; return; }
    row.scroller.innerHTML = html;
    row.section.style.display = '';
    // stagger the card animation a little so the row slides in nicely
    var cards = row.scroller.querySelectorAll('.home-card');
    for (var i = 0; i < cards.length; i++) cards[i].style.animationDelay = Math.min(i * 22, 260) + 'ms';
    updateHomeScrollButtons(row);
  }

  function updateHomeScrollButtons(row) {
    var buttons = row.section.querySelectorAll('.home-scroll-btn');
    if (!buttons.length) return;
    var max = row.scroller.scrollWidth - row.scroller.clientWidth;
    buttons[0].disabled = row.scroller.scrollLeft <= 2;
    buttons[1].disabled = row.scroller.scrollLeft >= max - 2;
  }

  function renderHomeFeed(feed) {
    feed = feed || {};
    var stats = feed.stats || {};
    var songCount = stats.presentSongs || stats.totalSongs || 0;
    var parts = [];
    if (songCount) parts.push('<span class="home-stat"><b>' + songCount + '</b> songs</span>');
    if (stats.totalPlaylists) parts.push('<span class="home-stat"><b>' + stats.totalPlaylists + '</b> playlists</span>');
    if (stats.totalPlays) parts.push('<span class="home-stat"><b>' + stats.totalPlays + '</b> plays</span>');
    if (homeStatsEl) homeStatsEl.innerHTML = parts.join('');

    var playlists = feed.playlists || [];
    var recommended = feed.recommended || [];
    var recent = feed.recentlyPlayed || [];
    var fresh = feed.newlyAdded || [];

    renderHomeRow('playlists',
      playlists.map(homePlaylistCardHtml).join(''),
      playlists.length > 0);

    var recommendedHtml = '';
    for (var i = 0; i < recommended.length; i++) {
      var rec = recommended[i];
      recommendedHtml += homeSongCardHtml(rec, { badge: rec.reason || 'For you' });
    }
    renderHomeRow('recommended', recommendedHtml, recommended.length > 0);

    renderHomeRow('recent',
      recent.map(function(item) {
        return homeSongCardHtml(item, { meta: item.playedUtc ? homeRelativeTime(item.playedUtc) : '' });
      }).join(''),
      recent.length > 0);

    renderHomeRow('fresh',
      fresh.map(function(item) {
        return homeSongCardHtml(item, { meta: item.dateAddedUtc ? 'Added ' + homeRelativeTime(item.dateAddedUtc) : '', badgeClass: 'is-new' });
      }).join(''),
      fresh.length > 0);

    var anyVisible = playlists.length + recommended.length + recent.length + fresh.length > 0;
    if (homeEmptyEl) homeEmptyEl.style.display = anyVisible ? 'none' : '';
    patchThumbnails(homeFeedEl, 0);
  }

  // Importing from the empty state: the host picks, copies and scans, then we
  // reload the feed (the scan-completed event does not reach this page).
  function wireHomeImport() {
    var button = document.getElementById('home-empty-import');
    if (!button) return;
    button.addEventListener('click', function() {
      var label = button.innerHTML;
      button.disabled = true;
      button.innerHTML = '<i class="fas fa-circle-notch fa-spin"></i> Importing…';
      hostRequest('importLibrary', { mode: 'folder' }, 600000).then(function(res) {
        if (res && res.success) {
          refreshLibrarySongCount().then(function() { loadHomeFeed(true); });
          loadLibraryPage();
          loadRecentlyPlayed();
        }
        else if (res && res.error) { console.error('Import failed: ' + res.error); }
      }).catch(function(e) {
        console.error('Import failed: ' + (e && e.message));
      }).finally(function() {
        button.disabled = false;
        button.innerHTML = label;
      });
    });
  }

  // The feed is built from the library, so an empty library has nothing to show.
  // Knowing that locally keeps the spinner from hanging while a slow (or
  // unreachable) server request runs — the M1 device run showed it never clearing.
  var librarySongCount = -1;

  function refreshLibrarySongCount() {
    return hostRequest('getLibraryStats', {}, 15000).then(function(stats) {
      librarySongCount = stats && typeof stats.totalSongs === 'number' ? stats.totalSongs : -1;
      return librarySongCount;
    }).catch(function() {
      librarySongCount = -1;
      return -1;
    });
  }

  var homeFeedGeneration = 0;
  var homeFeedLibraryCount = -1;

  function loadHomeFeed(force) {
    // A forced reload must win over a request that is still in flight (an
    // unreachable server can keep one hanging for its whole timeout), and the
    // stale response must not overwrite the newer one.
    if (homeFeedLoading && !force) return;
    if (homeFeedData && !force) { renderHomeFeed(homeFeedData); return; }

    var generation = ++homeFeedGeneration;

    if (librarySongCount === 0) {
      // Nothing to fetch yet: show the empty state (with its import button).
      homeFeedData = {};
      homeFeedLibraryCount = 0;
      if (homeLoadingEl) homeLoadingEl.style.display = 'none';
      if (homeEmptyEl) homeEmptyEl.style.display = '';
      return;
    }

    homeFeedLoading = true;
    lastHomeLoad = Date.now();
    if (homeLoadingEl) homeLoadingEl.style.display = '';
    if (homeEmptyEl) homeEmptyEl.style.display = 'none';

    hostRequest('getHomeFeed', { count: HOME_CARD_COUNT }).then(function(feed) {
      if (generation !== homeFeedGeneration) return;   // superseded
      homeFeedLoading = false;
      homeFeedData = feed || {};
      homeFeedLibraryCount = librarySongCount;
      if (homeLoadingEl) homeLoadingEl.style.display = 'none';
      renderHomeFeed(homeFeedData);
    }).catch(function() {
      if (generation !== homeFeedGeneration) return;
      homeFeedLoading = false;
      if (homeLoadingEl) homeLoadingEl.style.display = 'none';
      if (homeEmptyEl) homeEmptyEl.style.display = '';
    });
  }

  var HOME_MIN_REFRESH_MS = 4000;
  var lastHomeLoad = 0;
  function invalidateHomeFeed() {
    var homeView = document.getElementById('view-home');
    var visible = homeView && homeView.classList.contains('active');
    homeFeedData = null;
    if (!visible) return;                       // nothing to refresh while it is hidden
    if (Date.now() - lastHomeLoad < HOME_MIN_REFRESH_MS) return;
    loadHomeFeed(true);
  }

  // Section scroll buttons + horizontal wheel scrolling
  Object.keys(homeRows).forEach(function(key) {
    var row = homeRows[key];
    if (!row || !row.section || !row.scroller) return;
    row.section.querySelectorAll('.home-scroll-btn').forEach(function(btn) {
      btn.addEventListener('click', function() {
        var dir = parseInt(this.dataset.dir, 10) || 1;
        row.scroller.scrollBy({ left: dir * Math.max(240, row.scroller.clientWidth * 0.8), behavior: 'smooth' });
      });
    });
    var scrollScheduled = false;
    row.scroller.addEventListener('scroll', function() {
      if (scrollScheduled) return;
      scrollScheduled = true;
      requestAnimationFrame(function() { scrollScheduled = false; updateHomeScrollButtons(row); });
    }, { passive: true });
    row.scroller.addEventListener('wheel', function(e) {
      if (Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return;   // horizontal gesture: let it be
      this.scrollLeft += e.deltaY;
      e.preventDefault();
    }, { passive: false });
  });

  // Card clicks: play button plays, the card itself opens/plays
  if (homeFeedEl) {
    homeFeedEl.addEventListener('click', function(e) {
      var card = e.target.closest('.home-card');
      if (!card) return;

      var playlistId = card.dataset.playlistid;
      var songId = card.dataset.songid;
      var isPlayButton = !!e.target.closest('.home-card-play');

      if (playlistId) {
        if (isPlayButton) hostSend('playPlaylist', { playlistId: playlistId });
        else openPlaylistDetail(playlistId);
        return;
      }
      if (songId) hostSend('playLibrarySong', { songId: songId });
    });

    homeFeedEl.addEventListener('contextmenu', function(e) {
      var card = e.target.closest('.home-card');
      if (!card) return;
      e.preventDefault();
      if (card.dataset.playlistid) showPlaylistContextMenu(e, card.dataset.playlistid);
      else if (card.dataset.songid) showContextMenu(e.clientX, e.clientY, card.dataset.songid, card, 'library');
    });
  }

  // Artwork that the host still has to downscale arrives as a placeholder; ask for it
  // again shortly instead of re-rendering the whole row.
  var _thumbTimers = [];
  function patchThumbnails(root, attempt) {
    attempt = attempt || 0;
    if (attempt > 8) return;
    var pending = root ? root.querySelectorAll('.home-card-thumb.thumb-pending[data-songid], .library-song-thumb.thumb-pending[data-songid]') : [];
    if (!pending.length) return;

    var ids = [];
    for (var i = 0; i < pending.length && ids.length < 24; i++) {
      var id = pending[i].dataset.songid;
      if (id && ids.indexOf(id) < 0) ids.push(id);
    }
    if (!ids.length) return;

    hostRequest('getThumbnails', { songIds: ids }).then(function(result) {
      var thumbs = (result && result.thumbs) || {};
      var patched = 0;
      for (var i = 0; i < pending.length; i++) {
        var el = pending[i];
        var url = thumbs[el.dataset.songid];
        if (!url) continue;
        el.classList.remove('thumb-pending');
        var icon = el.querySelector('i');
        if (icon) icon.remove();
        var play = el.querySelector('.home-card-play');
        var img = document.createElement('img');
        img.src = url;
        img.alt = '';
        img.loading = 'lazy';
        if (play) el.insertBefore(img, play); else el.insertBefore(img, el.firstChild);
        patched++;
      }
      if (patched < pending.length) scheduleThumbPatch(root, attempt + 1);
    }).catch(function() { scheduleThumbPatch(root, attempt + 1); });
  }

  function scheduleThumbPatch(root, attempt) {
    var timer = setTimeout(function() { patchThumbnails(root, attempt); }, 700 + attempt * 400);
    _thumbTimers.push(timer);
    if (_thumbTimers.length > 40) _thumbTimers.splice(0, 20);
  }

  // --- Output device picker (speaker icon) ---
  var deviceMenuEl = document.getElementById('device-menu');
  var deviceMenuListEl = document.getElementById('device-menu-list');
  var deviceTriggers = [document.getElementById('btn-device'), document.getElementById('btn-exp-device')].filter(Boolean);
  var deviceSelected = -1;
  var deviceMenuOpen = false;
  var deviceMenuAnchor = null;

  // The menu lives inside #player-bar-right, which is hidden in expanded mode.
  // Fixed positioning does not escape a hidden ancestor, so move it to <body>
  // where it can also paint above the fullscreen player.
  if (deviceMenuEl && deviceMenuEl.parentElement !== document.body) {
    document.body.appendChild(deviceMenuEl);
  }

  function hideDeviceMenu() {
    deviceMenuOpen = false;
    deviceMenuAnchor = null;
    if (deviceMenuEl) deviceMenuEl.style.display = 'none';
  }

  function renderDeviceMenu(devices, selected) {
    if (!deviceMenuListEl) return;
    if (!devices || !devices.length) {
      deviceMenuListEl.innerHTML = '<div class="device-menu-empty">No output devices found</div>';
      return;
    }
    var html = '';
    for (var i = 0; i < devices.length; i++) {
      var d = devices[i];
      var isSelected = d.number === selected;
      html += '<button class="device-menu-item" data-number="' + d.number + '" title="' + escHtml(d.name || '') + '">' +
        '<span class="check">' + (isSelected ? '<i class="fas fa-check"></i>' : '') + '</span>' +
        '<span class="name">' + escHtml(d.name || ('Device ' + d.number)) + '</span>' +
      '</button>';
    }
    deviceMenuListEl.innerHTML = html;
  }

  // Places the menu next to its trigger and caps the device list to the room
  // that is actually available. Without this the menu was measured while it
  // still said "Looking for devices..." and a longer list ran off the window.
  function positionDeviceMenu() {
    if (!deviceMenuEl || !deviceMenuAnchor) return;

    var rect = deviceMenuAnchor.getBoundingClientRect();
    var margin = 8;

    // Measure the natural content first; a previous cap must not affect the
    // side choice after the list changed.
    deviceMenuEl.style.maxHeight = '';
    if (deviceMenuListEl) deviceMenuListEl.style.maxHeight = '';
    var desiredHeight = deviceMenuEl.offsetHeight;
    var width = deviceMenuEl.offsetWidth;

    var spaceBelow = window.innerHeight - rect.bottom - margin;
    var spaceAbove = rect.top - margin;

    // Prefer the side where the whole menu fits. If neither does, use the
    // roomier side and let the list scroll inside it.
    var placeBelow;
    if (spaceBelow >= desiredHeight) placeBelow = true;
    else if (spaceAbove >= desiredHeight) placeBelow = false;
    else placeBelow = spaceBelow >= spaceAbove;

    var available = Math.max(0, placeBelow ? spaceBelow : spaceAbove);
    deviceMenuEl.style.maxHeight = available + 'px';

    if (deviceMenuListEl) {
      var head = deviceMenuEl.querySelector('.device-menu-head');
      var headHeight = head ? head.offsetHeight : 0;
      // The menu has 6px vertical padding, and the list may not collapse to
      // nothing even in an extremely short window.
      var listMax = Math.max(48, available - headHeight - 12);
      deviceMenuListEl.style.maxHeight = listMax + 'px';
    }

    var height = deviceMenuEl.offsetHeight;
    var left = Math.min(Math.max(margin, rect.right - width), window.innerWidth - width - margin);
    var top = placeBelow ? rect.bottom + margin : rect.top - height - margin;
    top = Math.max(margin, Math.min(top, window.innerHeight - height - margin));

    deviceMenuEl.style.left = Math.max(margin, left) + 'px';
    deviceMenuEl.style.top = top + 'px';
  }

  function openDeviceMenu(anchor) {
    if (!deviceMenuEl) return;
    deviceMenuOpen = true;
    deviceMenuAnchor = anchor;
    deviceMenuEl.style.display = 'block';
    renderDeviceMenu([], deviceSelected);
    deviceMenuListEl.innerHTML = '<div class="device-menu-empty"><i class="fas fa-circle-notch fa-spin"></i> Looking for devices...</div>';
    positionDeviceMenu();

    hostRequest('getMusicAudioDevices', {}).then(function(result) {
      if (!deviceMenuOpen) return;
      deviceSelected = (result && typeof result.selected === 'number') ? result.selected : -1;
      renderDeviceMenu((result && result.devices) || [], deviceSelected);
      positionDeviceMenu();
    }).catch(function() {
      if (deviceMenuOpen && deviceMenuListEl) {
        deviceMenuListEl.innerHTML = '<div class="device-menu-empty">Could not list devices</div>';
        positionDeviceMenu();
      }
    });
  }

  deviceTriggers.forEach(function(btn) {
    btn.addEventListener('click', function(e) {
      e.stopPropagation();
      if (deviceMenuOpen) { hideDeviceMenu(); return; }
      openDeviceMenu(btn);
    });
  });

  if (deviceMenuEl) {
    deviceMenuEl.addEventListener('click', function(e) {
      e.stopPropagation();
      var item = e.target.closest('.device-menu-item');
      if (!item) return;
      var number = parseInt(item.dataset.number, 10);
      if (isNaN(number)) return;
      deviceSelected = number;
      hostSend('setMusicOutputDevice', { deviceNumber: number });
      // reflect the choice immediately, then close
      var items = deviceMenuListEl.querySelectorAll('.device-menu-item');
      for (var i = 0; i < items.length; i++) {
        var check = items[i].querySelector('.check');
        if (check) check.innerHTML = (parseInt(items[i].dataset.number, 10) === number) ? '<i class="fas fa-check"></i>' : '';
      }
      hideDeviceMenu();
    });
  }

  document.addEventListener('click', function() { hideDeviceMenu(); });
  document.addEventListener('keydown', function(e) { if (e.key === 'Escape') hideDeviceMenu(); });
  window.addEventListener('resize', hideDeviceMenu);

  // --- Recently Played ---
  var recentlyPlayedList = document.getElementById('recently-played-list');
  var recentlyPlayedLoading = document.getElementById('recently-played-loading');
  var recentlyPlayedEmpty = document.getElementById('recently-played-empty');
  var recentlyPlayedActive = false;

  function rpDateKey(d) {
    return d.getFullYear() + '-' + ('0' + (d.getMonth() + 1)).slice(-2) + '-' + ('0' + d.getDate()).slice(-2);
  }

  function rpDateLabel(d) {
    var now = new Date();
    var today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    var target = new Date(d.getFullYear(), d.getMonth(), d.getDate());
    var diff = Math.round((today - target) / 86400000);
    if (diff === 0) return 'Today';
    if (diff === 1) return 'Yesterday';
    return d.toLocaleDateString(undefined, { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' });
  }

  function renderRecentlyPlayedSong(entry, index) {
    var thumbHtml = entry.thumbnail
      ? '<img src="' + entry.thumbnail + '" alt="">'
      : '<i class="fas fa-music"></i>';
    var thumbState = (!entry.thumbnail && entry.songId) ? ' thumb-pending' : '';
    var timeStr = '';
    if (entry.playedUtc) {
      var d = new Date(entry.playedUtc);
      timeStr = d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
    }
    var triggerLabel = '';
    if (entry.trigger === 'autopilot') triggerLabel = '<span class="pill pill-auto">Autopilot</span>';

    return '<div class="library-song rp-song" data-songid="' + entry.songId + '" data-entryid="' + (entry.entryId || '') + '" data-filepath="' + escHtml(entry.filePath || '') + '">' +
      '<div class="library-song-thumb' + thumbState + '" data-songid="' + escHtml(entry.songId || '') + '">' + thumbHtml +
        '<div class="thumb-overlay"><i class="fas fa-play"></i></div>' +
      '</div>' +
      '<div class="library-song-info">' +
        '<div class="library-song-title">' + escHtml(entry.title || '') + '</div>' +
        '<div class="library-song-artist">' + escHtml(entry.artist || '') + '</div>' +
      '</div>' +
      '<div class="rp-meta">' +
        triggerLabel +
        '<span class="rp-date">' + escHtml(timeStr) + '</span>' +
        '<span class="library-song-duration">' + escHtml(entry.durationDisplay || '') + '</span>' +
      '</div>' +
    '</div>';
  }

  function loadRecentlyPlayed() {
    recentlyPlayedLoading.style.display = '';
    recentlyPlayedEmpty.style.display = 'none';
    recentlyPlayedList.innerHTML = '';

    hostRequest('getPlayHistory', { count: 50 }).then(function(result) {
      recentlyPlayedLoading.style.display = 'none';
      var history = (result && result.history) || [];
      if (history.length === 0) {
        recentlyPlayedEmpty.style.display = '';
        return;
      }
      var html = '';
      var lastKey = '';
      var idx = 0;
      for (var i = 0; i < history.length; i++) {
        var entry = history[i];
        if (entry.playedUtc) {
          var d = new Date(entry.playedUtc);
          var key = rpDateKey(d);
          if (key !== lastKey) {
            html += '<div class="rp-date-header">' + escHtml(rpDateLabel(d)) + '</div>';
            lastKey = key;
          }
        }
        html += renderRecentlyPlayedSong(entry, idx++);
      }
      recentlyPlayedList.innerHTML = html;
      patchThumbnails(recentlyPlayedList, 0);
    }).catch(function() {
      recentlyPlayedLoading.style.display = 'none';
      recentlyPlayedEmpty.style.display = '';
    });
  }

  // Click handler for recently played songs — artwork overlay plays immediately
  recentlyPlayedList.addEventListener('click', function(e) {
    var overlay = e.target.closest('.thumb-overlay');
    if (overlay) {
      var songEl = overlay.closest('.rp-song');
      if (songEl) {
        e.stopPropagation();
        hostSend('playLibrarySong', { songId: songEl.dataset.songid });
      }
      return;
    }
    var el = e.target.closest('.rp-song');
    if (!el) return;
    var songId = el.dataset.songid;
    if (songId) hostSend('playLibrarySong', { songId: songId });
  });

  // Sidebar "Recently Played" playlist item
  document.getElementById('playlist-recently-played').addEventListener('click', function() {
    document.querySelectorAll('.sidebar-tab').forEach(function(t) { t.classList.remove('active'); });
    document.querySelectorAll('#center .view').forEach(function(v) { v.classList.remove('active'); });
    document.getElementById('view-recently-played').classList.add('active');
    recentlyPlayedActive = true;
    loadRecentlyPlayed();
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
      btn.setAttribute('aria-pressed', on ? 'true' : 'false');
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
    if (!isFinite(seconds) || seconds < 0) return '0:00';
    const total = Math.floor(seconds);
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = total % 60;
    if (h > 0) return `${h}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
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
        if (recentlyPlayedActive && data.title) loadRecentlyPlayed();
        break;
      case 'queueUpdate':
        updateQueue(data);
        if (data && data.repeatEnabled != null) setRepeatState(!!data.repeatEnabled);
        break;
      case 'queueThumbnails':
        applyQueueThumbnails(data && data.items);
        break;
      case 'lyricsUpdate':
        renderLyrics(data);
        break;
      case 'refresh':
        // Sent when this page becomes visible again (Android pauses JS in a
        // hidden web view, so events that arrived meanwhile may have been queued).
        // Do NOT rebuild the feed unconditionally: that reset the scroll position
        // and flashed the artwork on every tab switch. Reload only when the
        // library itself changed while the page was hidden; now-playing state is
        // pushed continuously.
        (function() {
          var previousCount = homeFeedLibraryCount;
          refreshLibrarySongCount().then(function(count) {
            if (homeFeedData == null || count !== previousCount) loadHomeFeed(true);
          });
        })();
        break;
      case 'libraryChanged':
        // An import/scan elsewhere changed the library this feed is built from.
        refreshLibrarySongCount().then(function() { loadHomeFeed(true); });
        break;
      case 'ytDownloadProgress':
        onYtDownloadProgress(data);
        break;
      case 'presentationClosed':
        _presOpen = false;
        if (btnPresentation) btnPresentation.classList.remove('active');
        break;
      case 'openMusicShare': {
        var shareBtnEl = document.getElementById('btn-share');
        if (shareBtnEl) shareBtnEl.click();
        break;
      }
      case 'musicPlayerConfig': {
        var annBtn = document.getElementById('btn-announcement');
        if (annBtn) annBtn.style.display = (data && data.enableAnnouncements === false) ? 'none' : '';
        break;
      }
    }
  }

  var homeRefreshTimer = null;
  function scheduleHomeRefresh() {
    if (homeRefreshTimer) clearTimeout(homeRefreshTimer);
    homeRefreshTimer = setTimeout(function() { invalidateHomeFeed(); }, 1500);
  }

  function updateNowPlaying(data) {
    // Track metadata (only sent when song changes)
    if (data.title) {
      scheduleHomeRefresh();
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
    renderQueue(data.queue || [], data.playlistQueue || [], lastPlaybackActive);
  }

  function updateLibraryPlayingState() {
    var songs = libListEl.querySelectorAll('.library-song');
    songs.forEach(function(el) {
      var fp = el.getAttribute('data-filepath');
      var isPlaying = currentPlayingPath && fp && fp === currentPlayingPath;
      el.classList.toggle('lib-now-playing', isPlaying);
    });
  }

  // Artwork that was still being downscaled when the queue was rendered arrives
  // later; patch the rows in place instead of rebuilding a large queue.
  function applyQueueThumbnails(items) {
    if (!items || !items.length) return;

    var byPath = {};
    for (var i = 0; i < items.length; i++) {
      var item = items[i];
      if (item && item.songPath && item.thumbnail) byPath[item.songPath] = item.thumbnail;
    }
    if (!Object.keys(byPath).length) return;

    if (lastQueueData) {
      (lastQueueData.queue || []).forEach(function (entry) {
        var thumb = byPath[entry.songPath];
        if (thumb) entry.thumbnail = thumb;
      });
      (lastQueueData.playlistQueue || []).forEach(function (entry) {
        var thumb = byPath[entry.songPath];
        if (thumb) entry.thumbnail = thumb;
      });
    }

    var rows = queueList.querySelectorAll('.queue-item[data-song-path]');
    for (var r = 0; r < rows.length; r++) {
      var thumb = byPath[rows[r].getAttribute('data-song-path')];
      if (!thumb) continue;
      var host = rows[r].querySelector('.queue-item-thumb');
      if (host) host.innerHTML = '<img src="' + thumb + '" alt="">';
    }
  }

  function renderQueue(items, playlistItems, isPlaying) {
    var hasQueue = items.length > 0;
    var hasPlaylist = playlistItems.length > 0;

    if (!hasQueue && !hasPlaylist) {
      queueList.innerHTML = '<div class="queue-empty">Queue is empty</div>';
      return;
    }

    var html = '';

    // Regular queue
    if (hasQueue) {
      const first = items[0];
      if (isPlaying) {
        html += '<div class="queue-now-playing-header"><i class="fas fa-play-circle"></i> Now Playing</div>';
      }
      html += renderQueueItem(first, true, false);

      if (items.length > 1) {
        html += '<div class="queue-separator"></div>';
        for (let i = 1; i < items.length; i++) {
          html += renderQueueItem(items[i], false, false);
        }
      }
    }

    // Playlist queue
    if (hasPlaylist) {
      if (hasQueue) {
        html += '<div class="queue-separator queue-separator-playlist"></div>';
      }
      html += '<div class="queue-playlist-header"><i class="fas fa-list"></i> Playlist</div>';
      for (let i = 0; i < playlistItems.length; i++) {
        html += renderQueueItem(playlistItems[i], false, true);
      }
    }

    queueList.innerHTML = html;

    // Delegate remove clicks for regular queue
    queueList.querySelectorAll('.queue-item-remove').forEach(btn => {
      btn.addEventListener('click', function(e) {
        e.stopPropagation();
        const idx = parseInt(this.dataset.index);
        if (!isNaN(idx)) hostSend('removeQueueItem', { index: idx });
      });
    });

    // Delegate remove clicks for playlist queue
    queueList.querySelectorAll('.queue-playlist-remove').forEach(btn => {
      btn.addEventListener('click', function(e) {
        e.stopPropagation();
        const idx = parseInt(this.dataset.index);
        if (!isNaN(idx)) hostSend('removePlaylistQueueItem', { index: idx });
      });
    });

    // Drag-and-drop reordering (regular queue upcoming items)
    let dragSrcIdx = null;
    let dragSource = null; // 'queue' or 'playlist'
    queueList.querySelectorAll('.queue-item[draggable]').forEach(el => {
      el.addEventListener('dragstart', function(e) {
        dragSrcIdx = parseInt(this.dataset.index);
        dragSource = 'queue';
        this.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', String(dragSrcIdx));
      });
      el.addEventListener('dragend', function(e) {
        this.classList.remove('dragging');
        queueList.querySelectorAll('.drag-over').forEach(d => d.classList.remove('drag-over'));
      });
    });

    // Drag-and-drop reordering (playlist queue items)
    queueList.querySelectorAll('.queue-playlist-item[draggable]').forEach(el => {
      el.addEventListener('dragstart', function(e) {
        dragSrcIdx = parseInt(this.dataset.index);
        dragSource = 'playlist';
        this.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', String(dragSrcIdx));
      });
      el.addEventListener('dragend', function(e) {
        this.classList.remove('dragging');
        queueList.querySelectorAll('.drag-over').forEach(d => d.classList.remove('drag-over'));
      });
    });

    queueList.addEventListener('dragover', function(e) {
      e.preventDefault();
      const target = e.target.closest('.queue-item, .queue-playlist-item');
      if (!target || !target.hasAttribute('draggable')) return;
      // Only allow drops within the same source type
      const targetIsPlaylist = target.classList.contains('queue-playlist-item');
      if (dragSource === 'playlist' && !targetIsPlaylist) return;
      if (dragSource === 'queue' && targetIsPlaylist) return;
      if (parseInt(target.dataset.index) === dragSrcIdx) return;
      target.classList.add('drag-over');
    });

    queueList.addEventListener('dragleave', function(e) {
      const target = e.target.closest('.queue-item, .queue-playlist-item');
      if (target) target.classList.remove('drag-over');
    });

    queueList.addEventListener('drop', function(e) {
      e.preventDefault();
      queueList.querySelectorAll('.drag-over').forEach(el => el.classList.remove('drag-over'));
      const target = e.target.closest('.queue-item, .queue-playlist-item');
      if (!target) return;
      const toIdx = parseInt(target.dataset.index);
      if (isNaN(toIdx) || toIdx === dragSrcIdx) return;
      if (!isNaN(dragSrcIdx)) {
        if (dragSource === 'playlist') {
          hostSend('movePlaylistQueueItem', { fromIndex: dragSrcIdx, toIndex: toIdx });
        } else {
          if (toIdx === 0) return; // never move into now-playing slot
          hostSend('moveQueueItem', { fromIndex: dragSrcIdx, toIndex: toIdx });
        }
        dragSrcIdx = null;
        dragSource = null;
      }
    });
  }

  function renderQueueItem(item, isNowPlaying, isPlaylistItem) {
    const thumbHtml = item.thumbnail
      ? `<div class="queue-item-thumb"><img src="${item.thumbnail}" alt=""></div>`
      : `<div class="queue-item-thumb"><i class="fas fa-music"></i></div>`;
    const draggable = !isNowPlaying ? ' draggable="true"' : '';
    const removeClass = isPlaylistItem ? 'queue-playlist-remove' : 'queue-item-remove';

    var pills = '';
    if (item.isMissing) pills += '<span class="pill pill-missing"><i class="fas fa-exclamation-triangle"></i> Missing</span>';
    if (item.isHashDuplicate) pills += '<span class="pill pill-dup"><i class="fas fa-clone"></i> Hash Dup</span>';

    return `
      <div class="queue-item${isNowPlaying ? ' queue-now-playing' : ''}${isPlaylistItem ? ' queue-playlist-item' : ''}" data-index="${item.index}" data-song-path="${escHtml(item.songPath)}"${draggable}>
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
        <button class="${removeClass}" data-index="${item.index}" title="Remove"><i class="fas fa-times"></i></button>
      </div>
    `;
  }

  function updateQueueFromPlayback(isPlaybackActive) {
    if (lastQueueData && isPlaybackActive !== lastPlaybackActive) {
      lastPlaybackActive = isPlaybackActive;
      renderQueue(lastQueueData.queue || [], lastQueueData.playlistQueue || [], isPlaybackActive);
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
    var msg = JSON.stringify({ type: 'request', id: 0, method, ...params });
    if (!SongRequestHost.sendToHost(msg)) console.error('hostSend failed: no host channel');
  }

  function hostRequest(method, params, timeoutMs) {
    return new Promise(function(resolve, reject) {
      var id = ++_reqId;
      _pendingReqs[id] = { resolve: resolve, reject: reject, timer: setTimeout(function() {
        delete _pendingReqs[id];
        reject(new Error('Request timeout: ' + method));
      }, timeoutMs || 30000) };
      var msg = JSON.stringify({ type: 'request', id: id, method: method, ...params });
      if (!SongRequestHost.sendToHost(msg)) {
        clearTimeout(_pendingReqs[id].timer);
        delete _pendingReqs[id];
        reject(new Error('No host channel for ' + method));
      }
    });
  }

  function postMessageToHost(msg) {
    if (!SongRequestHost.sendToHost(msg)) console.error('postMessageToHost failed: no host channel');
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
        // Home tab content (playlists / recommendations / recent / new)
        loadHomeFeed(true);
        // Load persisted lyrics settings
        if (msg.lyricsFontScale != null) {
          lyricsFontScale = msg.lyricsFontScale;
          updateFontDisplay();
          updateLyricFontSizes();
        }
        if (msg.lyricsHidden) {
          setLyricsHidden(true);
        }
      } else if (msg.type === 'event') {
        onEvent(msg.eventName, msg.data || {});
      }
    } catch (_) {}
  }

  function sendReady() {
    // Request initial state from C# (page is now loaded)
    hostSend('musicPlayerReady');
    wireHomeImport();
    refreshLibrarySongCount().then(function() { loadHomeFeed(true); });
  }

  // Register the host -> page handler. Photino exposes
  // window.external.receiveMessage(callback); it must be *called* (assigning to
  // it replaces Photino's shim, after which no host message is ever delivered).
  SongRequestHost.registerHostMessageHandler(handleIncoming);
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

  // New lyrics always start at the top: the container used to keep the previous song's
  // scroll offset, and because the highlight only moves when its index changes, the view
  // crawled up a few lines into the new song instead of jumping to the start.
  function resetLyricsScroll() {
    lastHighlightIndex = -1;
    try { lyricsContainer.scrollTop = 0; } catch (e) { }
  }

  function renderLyricsLoading() {
    lyricsContainer.innerHTML = '<div class="lyrics-loading"><i class="fas fa-circle-notch fa-spin"></i><div>Loading lyrics...</div></div>';
    if (lyricsProvider) lyricsProvider.textContent = '';
    resetLyricsScroll();
  }

  function renderNoLyrics() {
    lyricsContainer.innerHTML = '<div class="lyrics-no-lyrics"><i class="fas fa-music"></i><div>No lyrics available</div></div>';
    resetLyricsScroll();
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
        div.dataset.time = line.time;
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
    resetLyricsScroll();                       // a new song starts at line one
    if (window.SongRequestAdvancedAudio) window.SongRequestAdvancedAudio.decorateLyrics();
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
      else { lyricsFontScale = 1.0; updateFontDisplay(); updateLyricFontSizes(); fontAnimFrame = null; saveLyricsSettings(); }
    }
    fontAnimFrame = requestAnimationFrame(tick);
  }

  fontDecreaseBtn.addEventListener('click', function() { setFontScale(lyricsFontScale - 0.1); });
  fontIncreaseBtn.addEventListener('click', function() { setFontScale(lyricsFontScale + 0.1); });
  fontResetEl.addEventListener('click', function() { animateFontToDefault(); });
  updateFontDisplay();

  // --- Lyrics hide + persist ---
  let lyricsHidden = false;
  const lyricsHideBtn = document.getElementById('lyrics-hide-btn');
  let _saveLyricsTimer = null;

  function saveLyricsSettings() {
    clearTimeout(_saveLyricsTimer);
    _saveLyricsTimer = setTimeout(function() {
      hostSend('saveLyricsSettings', { fontScale: lyricsFontScale, hidden: lyricsHidden });
    }, 500);
  }

  function setLyricsHidden(hidden) {
    lyricsHidden = hidden;
    lyricsHideBtn.classList.toggle('active', hidden);
    lyricsHideBtn.title = hidden ? 'Show lyrics' : 'Hide lyrics';
    lyricsHideBtn.innerHTML = hidden ? '<i class="fas fa-eye"></i>' : '<i class="fas fa-eye-slash"></i>';
    if (lyricsWrapper) lyricsWrapper.classList.toggle('lyrics-hidden', hidden);
    if (lyricsHiddenPlaceholder) lyricsHiddenPlaceholder.classList.toggle('visible', hidden);
    saveLyricsSettings();
  }

  if (lyricsHideBtn) {
    lyricsHideBtn.addEventListener('click', function() { setLyricsHidden(!lyricsHidden); });
  }

  // Override setFontScale to also persist
  var _origSetFontScale = setFontScale;
  setFontScale = function(newScale) {
    _origSetFontScale(newScale);
    saveLyricsSettings();
  };

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
    var thumbState = (!song.thumbnail && song.id) ? ' thumb-pending' : '';
    var title = song.title || song.filePath.split(/[/\\]/).pop();
    var artist = song.artist || 'Unknown';

    var pills = '';
    if (song.isMissing) pills += '<span class="pill pill-missing"><i class="fas fa-exclamation-triangle"></i> Missing</span>';
    if (song.isHashDuplicate) pills += '<span class="pill pill-dup"><i class="fas fa-clone"></i> Hash Dup</span>';
    if (song.hasMetadataDuplicates) pills += '<span class="pill pill-dup"><i class="fas fa-clone"></i> Meta Dup' + (song.metadataDuplicateCount > 1 ? ' (' + song.metadataDuplicateCount + ')' : '') + '</span>';

    return '<div class="library-song" data-id="' + song.id + '" data-filepath="' + escHtml(song.filePath || '') + '">' +
      '<div class="library-song-thumb' + thumbState + '" data-songid="' + song.id + '" data-song-id="' + song.id + '">' + thumbHtml +
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
        patchThumbnails(libListEl, 0);
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
      invalidateHomeFeed();
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
    recentlyPlayedActive = false;
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
  var ctxSongEl = null;
  var ctxSongSource = null;
  var longPressTimer = null;
  var longPressTriggered = false;

  function showContextMenu(x, y, songId, songEl, source) {
    ctxSongId = songId;
    ctxSongEl = songEl;
    ctxSongYtEl = songEl && songEl.dataset.ytid ? songEl : null;
    ctxSongSource = source || 'library';
    ctxMenu.dataset.source = ctxSongSource;
    ctxMenu.style.display = 'block';

    var deleteLibBtn = ctxMenu.querySelector('[data-action="deleteFromLibrary"]');
    var removeHistBtn = ctxMenu.querySelector('[data-action="removeFromHistory"]');
    if (deleteLibBtn) deleteLibBtn.style.display = ctxSongSource === 'library' ? '' : 'none';
    if (removeHistBtn) removeHistBtn.style.display = ctxSongSource === 'recently-played' ? '' : 'none';

    var mw = ctxMenu.offsetWidth, mh = ctxMenu.offsetHeight;
    if (x + mw > window.innerWidth) x = window.innerWidth - mw - 4;
    if (y + mh > window.innerHeight) y = window.innerHeight - mh - 4;
    ctxMenu.style.left = x + 'px';
    ctxMenu.style.top = y + 'px';
  }

  function hideContextMenu() {
    ctxMenu.style.display = 'none';
    ctxSongId = null;
    ctxSongEl = null;
    ctxSongYtEl = null;
    ctxSongSource = null;
    hidePlaylistContextMenu();
  }

  // Playlist context menu
  var plCtxMenu = document.getElementById('playlist-context-menu');
  var plCtxPlaylistId = null;

  function showPlaylistContextMenu(e, playlistId) {
    plCtxPlaylistId = playlistId;
    plCtxMenu.dataset.playlistId = playlistId;
    var pl = allPlaylists.find(function(p) { return p.id === playlistId; });
    var dynamic = !!(pl && pl.isDynamic);
    var deleteItem = plCtxMenu.querySelector('[data-action="deletePlaylist"]');
    if (deleteItem) deleteItem.style.display = dynamic ? 'none' : '';
    plCtxMenu.style.display = 'block';
    var menuW = plCtxMenu.offsetWidth;
    var menuH = plCtxMenu.offsetHeight;
    var x = e.clientX;
    var y = e.clientY;
    if (x + menuW > window.innerWidth) x = window.innerWidth - menuW - 4;
    if (y + menuH > window.innerHeight) y = window.innerHeight - menuH - 4;
    plCtxMenu.style.left = x + 'px';
    plCtxMenu.style.top = y + 'px';
  }

  function hidePlaylistContextMenu() {
    plCtxMenu.style.display = 'none';
    plCtxPlaylistId = null;
  }

  plCtxMenu.addEventListener('click', function(e) {
    var btn = e.target.closest('.context-menu-item');
    if (!btn) return;
    var action = btn.dataset.action;
    var id = plCtxPlaylistId;
    if (!id) return;
    if (action === 'playPlaylist') hostSend('playPlaylist', { playlistId: id });
    else if (action === 'shufflePlaylist') hostSend('shufflePlaylist', { playlistId: id });
    else if (action === 'queuePlaylist') hostSend('queuePlaylist', { playlistId: id });
    else if (action === 'deletePlaylist') hostSend('deletePlaylist', { playlistId: id });
    hidePlaylistContextMenu();
    if (action === 'deletePlaylist') {
      setTimeout(function() { loadPlaylists(); }, 200);
    }
  });

  document.addEventListener('click', function(e) {
    if (!plCtxMenu.contains(e.target)) hidePlaylistContextMenu();
  });

  // Right-click on library songs
  libListEl.addEventListener('contextmenu', function(e) {
    var songEl = e.target.closest('.library-song');
    if (!songEl) return;
    e.preventDefault();
    showContextMenu(e.clientX, e.clientY, songEl.dataset.id, songEl);
  });

  // Right-click on recently played songs
  recentlyPlayedList.addEventListener('contextmenu', function(e) {
    var songEl = e.target.closest('.rp-song');
    if (!songEl) return;
    e.preventDefault();
    showContextMenu(e.clientX, e.clientY, songEl.dataset.songid, songEl, 'recently-played');
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
    var songEl = ctxSongEl;
    hideContextMenu();
    if (ytEl && action !== 'analyze') {
      var ytAction = action === 'play' ? 'play' : action === 'playNext' ? 'playNext' : 'queue';
      startYouTubeDownload(ytEl, ytAction);
    } else if (action === 'analyze') {
      if (window.SongRequestAdvancedAudio) window.SongRequestAdvancedAudio.openAnalysis({ songId: songId });
    } else if (action === 'play') {
      hostSend('playLibrarySong', { songId: songId });
    } else if (action === 'playNext') {
      hostSend('playLibrarySongNext', { songId: songId });
    } else if (action === 'addToQueue') {
      hostSend('queueLibrarySong', { songId: songId });
    } else if (action === 'deleteFromLibrary') {
      showDeleteConfirm(songId);
    } else if (action === 'removeFromHistory') {
      var entryId = songEl ? songEl.dataset.entryid : null;
      if (entryId) {
        hostRequest('removePlayHistoryEntry', { entryId: entryId }).then(function() {
          loadRecentlyPlayed();
        });
      }
    }
  });

  // Delete confirmation modal
  var deleteModal = document.getElementById('delete-confirm-modal');
  var deleteConfirmText = document.getElementById('delete-confirm-text');
  var deleteConfirmOk = document.getElementById('delete-confirm-ok');
  var deleteConfirmCancel = document.getElementById('delete-confirm-cancel');
  var deleteConfirmClose = document.getElementById('delete-confirm-close');
  var deletePendingSongId = null;

  function showDeleteConfirm(songId) {
    deletePendingSongId = songId;
    // Find the song title from the library list
    var songEl = libListEl.querySelector('[data-id="' + songId + '"]');
    var title = songEl ? (songEl.querySelector('.library-song-title') || {}).textContent || '' : '';
    var artist = songEl ? (songEl.querySelector('.library-song-artist') || {}).textContent || '' : '';
    var label = title ? (artist ? title + ' by ' + artist : title) : 'this song';
    deleteConfirmText.textContent = 'Are you sure you want to permanently delete ' + label + ' from your library and disk?';
    deleteModal.style.display = 'flex';
  }

  function hideDeleteConfirm() {
    deleteModal.style.display = 'none';
    deletePendingSongId = null;
  }

  deleteConfirmOk.addEventListener('click', function() {
    var songId = deletePendingSongId;
    hideDeleteConfirm();
    if (!songId) return;
    hostRequest('deleteLibrarySong', { songId: songId }).then(function(result) {
      if (result && result.error) {
        alert(result.error);
        return;
      }
      // Remove from library list
      var el = libListEl.querySelector('[data-id="' + songId + '"]');
      if (el) el.remove();
    }).catch(function(err) {
      console.error('Delete failed', err);
    });
  });

  deleteConfirmCancel.addEventListener('click', hideDeleteConfirm);
  deleteConfirmClose.addEventListener('click', hideDeleteConfirm);
  deleteModal.addEventListener('click', function(e) { if (e.target === deleteModal) hideDeleteConfirm(); });

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
  var shareReceiveVolume = document.getElementById('share-receive-volume');
  var shareReceiveVolumeValue = document.getElementById('share-receive-volume-value');
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

  shareReceiveVolume.addEventListener('input', function() {
    shareReceiveVolumeValue.textContent = this.value + '%';
  });
  shareReceiveVolume.addEventListener('change', function() {
    hostSend('musicShareSetVolume', { value: parseFloat(this.value) / 100 });
  });

  function shareFormatStatus(status) {
    var map = { idle:'Idle', connecting:'Connecting...', connected:'Connected', streaming:'Streaming', buffering:'Buffering', error:'Error', disconnected:'Disconnected' };
    return map[status] || status;
  }

  // Received share artwork is base64 without a MIME type. Shared songs can carry
  // embedded PNG covers even though the protocol calls the payload JPEG.
  function shareThumbnailSrc(data) {
    if (!data) return null;
    var mime = data.indexOf('iVBOR') === 0 ? 'image/png'
      : data.indexOf('R0lGOD') === 0 ? 'image/gif'
      : data.indexOf('UklGR') === 0 ? 'image/webp'
      : 'image/jpeg';
    return 'data:' + mime + ';base64,' + data;
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
        receiveThumb.innerHTML = '<img src="' + shareThumbnailSrc(data.thumbnailData) + '" alt="">';
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

  // ══════════════════════════════════════════════════════════
  //  Playlists
  // ══════════════════════════════════════════════════════════

  var allPlaylists = [];
  var currentPlaylistId = null;
  var currentPlaylistDynamic = false;

  function plIconName(name) {
    return window.SongRequestPlaylistIcons
      ? window.SongRequestPlaylistIcons.normalize(name)
      : (name && /^fa-[a-z0-9-]+$/.test(name) ? name : 'fa-list');
  }

  function plIconHtml(name) {
    return '<i class="fas ' + plIconName(name) + '"></i>';
  }

  function loadPlaylists() {
    hostRequest('getPlaylists', {}).then(function(result) {
      allPlaylists = (result && result.playlists) || [];
      renderPlaylistSidebar();
      renderPlaylistGrid();
    }).catch(function() {});
  }

  function renderPlaylistSidebar() {
    var container = document.getElementById('playlist-sidebar-items');
    if (!container) return;
    container.innerHTML = '';
    for (var i = 0; i < allPlaylists.length; i++) {
      var pl = allPlaylists[i];
      var div = document.createElement('div');
      div.className = 'playlist-item' + (currentPlaylistId === pl.id ? ' active' : '') + (pl.isDynamic ? ' is-dynamic' : '');
      div.dataset.id = pl.id;
      var iconHtml = plIconHtml(pl.icon);
      div.innerHTML = iconHtml + ' ' + escHtml(pl.name) + ' <span class="playlist-item-count" style="margin-left:auto;color:#666;font-size:11px">' + pl.songCount + '</span>';
      div.addEventListener('click', (function(id) {
        return function() { openPlaylistDetail(id); };
      })(pl.id));
      div.addEventListener('contextmenu', (function(id, e) {
        return function(ev) { ev.preventDefault(); showPlaylistContextMenu(ev, id); };
      })(pl.id));
      container.appendChild(div);
    }
  }

  function renderPlaylistGrid() {
    var grid = document.getElementById('playlist-grid');
    var emptyEl = document.getElementById('playlist-empty');
    if (!grid) return;
    grid.innerHTML = '';
    if (allPlaylists.length === 0) {
      if (emptyEl) emptyEl.style.display = '';
      return;
    }
    if (emptyEl) emptyEl.style.display = 'none';
    for (var i = 0; i < allPlaylists.length; i++) {
      var pl = allPlaylists[i];
      var card = document.createElement('div');
      card.className = 'playlist-card' + (pl.isDynamic ? ' is-dynamic' : '');
      var thumbContent = plIconHtml(pl.icon);
      card.innerHTML = '<div class="playlist-card-thumb">' + thumbContent + '</div>' +
        '<div class="playlist-card-name">' + escHtml(pl.name) + '</div>' +
        '<div class="playlist-card-count">' + pl.songCount + ' song' + (pl.songCount !== 1 ? 's' : '') +
        (pl.isDynamic ? ' · <span class="playlist-auto-badge">Auto</span>' : '') + '</div>';
      card.addEventListener('click', (function(id) {
        return function() { openPlaylistDetail(id); };
      })(pl.id));
      card.addEventListener('contextmenu', (function(id) {
        return function(ev) { ev.preventDefault(); showPlaylistContextMenu(ev, id); };
      })(pl.id));
      grid.appendChild(card);
    }
  }

  function openPlaylistDetail(playlistId) {
    currentPlaylistId = playlistId;
    // Switch to playlist detail view
    document.querySelectorAll('.sidebar-tab').forEach(function(t) { t.classList.remove('active'); });
    var plTab = document.querySelector('[data-view="playlists"]');
    if (plTab) plTab.classList.add('active');
    document.querySelectorAll('#center .view').forEach(function(v) { v.classList.remove('active'); });
    var detailView = document.getElementById('view-playlist-detail');
    if (detailView) detailView.classList.add('active');
    renderPlaylistSidebar();
    loadPlaylistDetail(playlistId);
  }

  function loadPlaylistDetail(playlistId) {
    hostRequest('getPlaylist', { playlistId: playlistId }).then(function(result) {
      if (!result || result.error) return;
      currentPlaylistDynamic = !!result.isDynamic;
      document.getElementById('playlist-detail-title').innerHTML = plIconHtml(result.icon) + ' ' + escHtml(result.name);
      document.getElementById('playlist-detail-count').textContent = result.songCount + ' song' + (result.songCount !== 1 ? 's' : '') + (result.isDynamic ? ' · Auto playlist' : '');
      var deleteBtn = document.getElementById('playlist-detail-delete');
      if (deleteBtn) deleteBtn.style.display = currentPlaylistDynamic ? 'none' : '';
      var sortSelect = document.getElementById('playlist-detail-sort');
      if (sortSelect) sortSelect.style.display = currentPlaylistDynamic ? 'none' : '';
      var listEl = document.getElementById('playlist-detail-list');
      var emptyEl = document.getElementById('playlist-detail-empty');
      if (!listEl) return;
      listEl.innerHTML = '';
      if (result.songs.length === 0) {
        if (emptyEl) emptyEl.style.display = '';
        return;
      }
      if (emptyEl) emptyEl.style.display = 'none';
      for (var i = 0; i < result.songs.length; i++) {
        listEl.appendChild(renderPlaylistSong(result.songs[i], i));
      }
      if (currentPlaylistDynamic) {
        listEl.querySelectorAll('.playlist-song-remove, .playlist-song-drag').forEach(function(el) { el.style.display = 'none'; });
      }
    }).catch(function() {});
  }

  function renderPlaylistSong(song, index) {
    var div = document.createElement('div');
    div.className = 'playlist-song';
    div.dataset.entryid = song.entryId;
    div.dataset.songid = song.songId;
    div.dataset.index = index;
    var thumbHtml = song.thumbnail ? '<img src="' + song.thumbnail + '" alt="" style="width:40px;height:40px;border-radius:4px;object-fit:cover">' :
      '<div class="library-song-thumb" style="width:40px;height:40px;flex-shrink:0;display:flex;align-items:center;justify-content:center;background:#2a2a2a;border-radius:4px;font-size:14px;color:#555"><i class="fas fa-music"></i></div>';
    div.innerHTML = '<div class="playlist-song-drag"><i class="fas fa-grip-vertical"></i></div>' +
      thumbHtml +
      '<div class="library-song-info">' +
        '<div class="library-song-title">' + escHtml(song.title) + '</div>' +
        '<div class="library-song-artist">' + escHtml(song.artist) + '</div>' +
      '</div>' +
      '<span class="library-song-duration">' + escHtml(song.durationDisplay || '') + '</span>' +
      '<button class="playlist-song-remove" title="Remove from playlist"><i class="fas fa-times"></i></button>';
    // Remove button
    div.querySelector('.playlist-song-remove').addEventListener('click', (function(eId) {
      return function(ev) {
        ev.stopPropagation();
        hostRequest('removePlaylistEntry', { playlistId: currentPlaylistId, entryId: eId }).then(function() {
          loadPlaylistDetail(currentPlaylistId);
          loadPlaylists();
        });
      };
    })(song.entryId));
    // Right-click context menu
    div.addEventListener('contextmenu', function(e) {
      e.preventDefault();
      if (currentPlaylistDynamic) return;
      showContextMenu(e.clientX, e.clientY, song.songId, div, 'playlist');
    });
    // Drag and drop
    var dragHandle = div.querySelector('.playlist-song-drag');
    setupPlaylistDrag(div, dragHandle);
    return div;
  }

  // ── Drag & Drop reorder ───────────────────────────────
  function setupPlaylistSongDnD() {
    // Handled per-item via setupPlaylistDrag
  }

  function setupPlaylistDrag(el, handle) {
    var startY = 0, startIdx = 0, dragClone = null, lastOver = null, dropAtEnd = false;
    var touchMode = false;

    function onStart(e) {
      e.preventDefault();
      touchMode = e.type === 'touchstart';
      var clientY = touchMode ? e.touches[0].clientY : e.clientY;
      startY = clientY;
      startIdx = parseInt(el.dataset.index, 10);
      el.style.opacity = '0.5';
      lastOver = el;
      dropAtEnd = false;
      document.addEventListener(touchMode ? 'touchmove' : 'mousemove', onMove);
      document.addEventListener(touchMode ? 'touchend' : 'mouseup', onEnd);
    }

    function onMove(e) {
      e.preventDefault();
      var clientY = touchMode ? e.touches[0].clientY : e.clientY;
      var listEl = document.getElementById('playlist-detail-list');
      var items = listEl.querySelectorAll('.playlist-song');
      dropAtEnd = false;
      // Find item under cursor
      var found = false;
      for (var i = 0; i < items.length; i++) {
        var rect = items[i].getBoundingClientRect();
        if (clientY >= rect.top && clientY <= rect.bottom) {
          items.forEach(function(it) { it.classList.remove('drag-over'); });
          items[i].classList.add('drag-over');
          lastOver = items[i];
          found = true;
          break;
        }
      }
      // If below the last item, highlight last and mark drop-at-end
      if (!found && items.length > 0) {
        var lastRect = items[items.length - 1].getBoundingClientRect();
        if (clientY > lastRect.bottom) {
          items.forEach(function(it) { it.classList.remove('drag-over'); });
          items[items.length - 1].classList.add('drag-over');
          lastOver = items[items.length - 1];
          dropAtEnd = true;
        }
      }
    }

    function onEnd() {
      document.removeEventListener(touchMode ? 'touchmove' : 'mousemove', onMove);
      document.removeEventListener(touchMode ? 'touchend' : 'mouseup', onEnd);
      el.style.opacity = '';
      var listEl = document.getElementById('playlist-detail-list');
      listEl.querySelectorAll('.playlist-song').forEach(function(it) { it.classList.remove('drag-over'); });
      if (lastOver && lastOver !== el) {
        var fromEntryId = el.dataset.entryid;
        var toIndex = dropAtEnd ? 999999 : parseInt(lastOver.dataset.index, 10);
        hostRequest('reorderPlaylistEntry', { playlistId: currentPlaylistId, entryId: fromEntryId, newSortOrder: toIndex }).then(function() {
          loadPlaylistDetail(currentPlaylistId);
        });
      }
    }

    handle.addEventListener('mousedown', onStart);
    handle.addEventListener('touchstart', onStart, { passive: false });
  }

  // ── Sort selector ─────────────────────────────────────
  var plSortSelect = document.getElementById('playlist-detail-sort');
  if (plSortSelect) {
    plSortSelect.addEventListener('change', function() {
      var listEl = document.getElementById('playlist-detail-list');
      if (!listEl) return;
      var items = Array.from(listEl.querySelectorAll('.playlist-song'));
      var sortBy = plSortSelect.value;
      if (sortBy === 'custom') {
        loadPlaylistDetail(currentPlaylistId);
        return;
      }
      items.sort(function(a, b) {
        var aTitle = (a.querySelector('.library-song-title') || {}).textContent || '';
        var bTitle = (b.querySelector('.library-song-title') || {}).textContent || '';
        var aArtist = (a.querySelector('.library-song-artist') || {}).textContent || '';
        var bArtist = (b.querySelector('.library-song-artist') || {}).textContent || '';
        if (sortBy === 'title') return aTitle.localeCompare(bTitle);
        if (sortBy === 'artist') return aArtist.localeCompare(bArtist) || aTitle.localeCompare(bTitle);
        if (sortBy === 'duration') {
          var aDur = (a.querySelector('.library-song-duration') || {}).textContent || '';
          var bDur = (b.querySelector('.library-song-duration') || {}).textContent || '';
          return aDur.localeCompare(bDur);
        }
        return 0;
      });
      listEl.innerHTML = '';
      items.forEach(function(item, i) { listEl.appendChild(item); });
    });
  }

  // ── Back button ───────────────────────────────────────
  var plBackBtn = document.getElementById('playlist-detail-back');
  if (plBackBtn) {
    plBackBtn.addEventListener('click', function() {
      currentPlaylistId = null;
      document.querySelectorAll('.sidebar-tab').forEach(function(t) { t.classList.remove('active'); });
      var plTab = document.querySelector('[data-view="playlists"]');
      if (plTab) plTab.classList.add('active');
      document.querySelectorAll('#center .view').forEach(function(v) { v.classList.remove('active'); });
      var plView = document.getElementById('view-playlists');
      if (plView) plView.classList.add('active');
      renderPlaylistSidebar();
      loadPlaylists();
    });
  }

  // ── Play / Shuffle playlist ─────────────────────────────
  var plPlayBtn = document.getElementById('playlist-detail-play');
  if (plPlayBtn) {
    plPlayBtn.addEventListener('click', function() {
      if (!currentPlaylistId) return;
      hostSend('playPlaylist', { playlistId: currentPlaylistId });
    });
  }
  var plShuffleBtn = document.getElementById('playlist-detail-shuffle');
  if (plShuffleBtn) {
    plShuffleBtn.addEventListener('click', function() {
      if (!currentPlaylistId) return;
      hostSend('shufflePlaylist', { playlistId: currentPlaylistId });
    });
  }
  var plQueueBtn = document.getElementById('playlist-detail-queue');
  if (plQueueBtn) {
    plQueueBtn.addEventListener('click', function() {
      if (!currentPlaylistId) return;
      hostSend('queuePlaylist', { playlistId: currentPlaylistId });
    });
  }

  // ── Delete playlist ───────────────────────────────────
  var plDeleteBtn = document.getElementById('playlist-detail-delete');
  if (plDeleteBtn) {
    plDeleteBtn.addEventListener('click', function() {
      if (!currentPlaylistId || currentPlaylistDynamic) return;
      if (!confirm('Delete this playlist?')) return;
      hostRequest('deletePlaylist', { playlistId: currentPlaylistId }).then(function() {
        currentPlaylistId = null;
        loadPlaylists();
        // Go back to playlist grid
        document.querySelectorAll('#center .view').forEach(function(v) { v.classList.remove('active'); });
        var plView = document.getElementById('view-playlists');
        if (plView) plView.classList.add('active');
      });
    });
  }

  // ── Create playlist modal ─────────────────────────────
  var createPlModal = document.getElementById('create-playlist-modal');
  var createPlName = document.getElementById('create-playlist-name');
  var createPlIcon = document.getElementById('create-playlist-icon');
  var createPlIconPicker = document.getElementById('create-playlist-icon-picker');
  var createPlOk = document.getElementById('create-playlist-ok');
  var createPlCancel = document.getElementById('create-playlist-cancel');
  var createPlClose = document.getElementById('create-playlist-close');
  var createPlBtn = document.getElementById('btn-create-playlist');
  var createPlSelectedIcon = 'fa-list';

  function renderCreatePlaylistIcons() {
    if (!createPlIconPicker || !window.SongRequestPlaylistIcons) return;
    window.SongRequestPlaylistIcons.renderPicker(createPlIconPicker, createPlSelectedIcon, function (name) {
      createPlSelectedIcon = name;
      if (createPlIcon) createPlIcon.value = name;
    });
  }

  function openCreatePlaylistModal() {
    if (createPlName) createPlName.value = '';
    createPlSelectedIcon = 'fa-list';
    if (createPlIcon) createPlIcon.value = createPlSelectedIcon;
    renderCreatePlaylistIcons();
    if (createPlModal) createPlModal.style.display = '';
    setTimeout(function() { if (createPlName) createPlName.focus(); }, 100);
  }
  function closeCreatePlaylistModal() {
    if (createPlModal) createPlModal.style.display = 'none';
  }

  if (createPlBtn) createPlBtn.addEventListener('click', openCreatePlaylistModal);
  if (createPlCancel) createPlCancel.addEventListener('click', closeCreatePlaylistModal);
  if (createPlClose) createPlClose.addEventListener('click', closeCreatePlaylistModal);
  if (createPlOk) createPlOk.addEventListener('click', function() {
    var name = (createPlName.value || '').trim();
    if (!name) { createPlName.style.borderColor = '#e74c3c'; return; }
    var icon = (createPlIcon && createPlIcon.value) || createPlSelectedIcon || 'fa-list';
    hostRequest('createPlaylist', { name: name, icon: icon }).then(function(result) {
      if (result && result.success) {
        closeCreatePlaylistModal();
        loadPlaylists();
        openPlaylistDetail(result.id);
      }
    });
  });
  if (createPlName) createPlName.addEventListener('keydown', function(e) {
    if (e.key === 'Enter') createPlOk.click();
    createPlName.style.borderColor = '';
  });

  // ── Context menu: Add to Playlist submenu ─────────────
  function populatePlaylistSubmenu() {
    var listEl = document.getElementById('ctx-playlist-list');
    if (!listEl) return;
    listEl.innerHTML = '';
    for (var i = 0; i < allPlaylists.length; i++) {
      var pl = allPlaylists[i];
      // Auto/dynamic playlists are generated and cannot receive songs.
      if (pl.isDynamic) continue;
      var btn = document.createElement('button');
      btn.className = 'context-submenu-item';
      btn.dataset.playlistId = pl.id;
      btn.innerHTML = plIconHtml(pl.icon) + ' ' + escHtml(pl.name);
      btn.addEventListener('click', (function(plId) {
        return function(ev) {
          ev.stopPropagation();
          if (ctxSongId) {
            hostRequest('addSongToPlaylist', { playlistId: plId, songId: ctxSongId }).then(function() {
              loadPlaylists();
            });
          }
          hideContextMenu();
        };
      })(pl.id));
      listEl.appendChild(btn);
    }
    // Create new playlist option
    var createBtn = document.createElement('button');
    createBtn.className = 'context-submenu-item context-submenu-create';
    createBtn.innerHTML = '<i class="fas fa-plus"></i> New Playlist';
    createBtn.addEventListener('click', function(ev) {
      ev.stopPropagation();
      hideContextMenu();
      openCreatePlaylistModal();
    });
    listEl.appendChild(createBtn);
  }

  // Show submenu on hover (touch: toggle)
  var plSubmenu = document.getElementById('ctx-playlist-submenu');
  if (plSubmenu) {
    plSubmenu.addEventListener('mouseenter', function() {
      populatePlaylistSubmenu();
      plSubmenu.classList.add('open');
    });
    plSubmenu.addEventListener('mouseleave', function() {
      plSubmenu.classList.remove('open');
    });
    plSubmenu.addEventListener('click', function(ev) {
      ev.stopPropagation();
      var wasOpen = plSubmenu.classList.contains('open');
      // Close all submenus first
      document.querySelectorAll('.context-menu-submenu').forEach(function(sm) { sm.classList.remove('open'); });
      if (!wasOpen) {
        populatePlaylistSubmenu();
        plSubmenu.classList.add('open');
      }
    });
  }

  // ── Init playlists on load ────────────────────────────
  loadPlaylists();

  // ── Presentation window ───────────────────────────────
  var btnPresentation = document.getElementById('btn-presentation');
  var presDropdown = document.getElementById('pres-source-dropdown');
  var presDropdownItems = presDropdown ? presDropdown.querySelectorAll('.pres-dropdown-item') : [];
  var _presOpen = false;

  if (btnPresentation) {
    btnPresentation.addEventListener('click', function() {
      if (_presOpen) {
        hostSend('closePresentation');
        _presOpen = false;
        btnPresentation.classList.remove('active');
      } else {
        hostSend('showPresentation');
        _presOpen = true;
        btnPresentation.classList.add('active');
      }
    });
  }

  var btnVisualization = document.getElementById('btn-visualization');
  if (btnVisualization) {
    btnVisualization.addEventListener('click', function() { hostSend('showVisualization'); });
  }

  var btnAnnouncement = document.getElementById('btn-announcement');
  if (btnAnnouncement) {
    btnAnnouncement.addEventListener('click', function() { hostSend('announcementOpen'); });
  }

  var btnPresSource = document.getElementById('btn-pres-source');
  if (btnPresSource) {
    btnPresSource.addEventListener('click', function(ev) {
      ev.stopPropagation();
      presDropdown.style.display = presDropdown.style.display === 'none' ? '' : 'none';
    });
  }

  if (presDropdownItems.length) {
    presDropdownItems.forEach(function(item) {
      item.addEventListener('click', function(ev) {
        ev.stopPropagation();
        var src = item.dataset.source;
        hostSend('setPresentationSource', { source: src });
        presDropdownItems.forEach(function(i) { i.classList.remove('active'); });
        item.classList.add('active');
        presDropdown.style.display = 'none';
      });
    });
  }

  document.addEventListener('click', function() {
    if (presDropdown) presDropdown.style.display = 'none';
  });

  // ── Keyboard shortcuts ────────────────────────────────
  const shortcutsModal = document.getElementById('shortcuts-modal');
  const shortcutsClose = document.getElementById('shortcuts-close');
  const btnShortcuts = document.getElementById('btn-shortcuts');

  if (btnShortcuts && shortcutsModal) {
    btnShortcuts.addEventListener('click', function() {
      shortcutsModal.style.display = '';
    });
  }
  if (shortcutsClose && shortcutsModal) {
    shortcutsClose.addEventListener('click', function() {
      shortcutsModal.style.display = 'none';
    });
  }
  if (shortcutsModal) {
    shortcutsModal.addEventListener('click', function(ev) {
      if (ev.target === shortcutsModal) shortcutsModal.style.display = 'none';
    });
  }

  function isInputFocused() {
    var el = document.activeElement;
    if (!el) return false;
    var tag = el.tagName;
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable;
  }

  function getVolumeSlider() {
    var s = document.getElementById('volume-slider');
    return s || document.getElementById('exp-volume-slider');
  }

  function getCurrentVolume() {
    var s = getVolumeSlider();
    return s ? parseInt(s.value, 10) : 50;
  }

  function setVolumeAndSync(newVal) {
    newVal = Math.max(0, Math.min(100, newVal));
    volSliders.forEach(function(s) { if (s) s.value = newVal; });
    var iconName = newVal === 0 ? 'fa-volume-mute' : newVal < 50 ? 'fa-volume-down' : 'fa-volume-up';
    volIcons.forEach(function(ic) { if (ic) ic.className = 'fas ' + iconName; });
    updateSliderLabels();
    sendVolume(newVal);
  }

  document.addEventListener('keydown', function(ev) {
    if (isInputFocused()) return;

    var key = ev.key;

    // Esc closes any open modal
    if (key === 'Escape') {
      if (shortcutsModal && shortcutsModal.style.display !== 'none') {
        shortcutsModal.style.display = 'none';
        ev.preventDefault();
      }
      // Also close any other open modals
      document.querySelectorAll('.modal-overlay').forEach(function(m) {
        if (m !== shortcutsModal && m.style.display !== 'none') {
          m.style.display = 'none';
        }
      });
      return;
    }

    switch (key) {
      case ' ':
        ev.preventDefault();
        onPlayClick(colPlayBtn || expPlayBtn);
        break;
      case 'n': case 'N':
        hostSend('skipNext');
        break;
      case 'p': case 'P':
        hostSend('skipPrevious');
        break;
      case 'ArrowUp':
        ev.preventDefault();
        setVolumeAndSync(getCurrentVolume() + 5);
        break;
      case 'ArrowDown':
        ev.preventDefault();
        setVolumeAndSync(getCurrentVolume() - 5);
        break;
      case 'm': case 'M':
        var cur = getCurrentVolume();
        setVolumeAndSync(cur > 0 ? 0 : 80);
        break;
      case 'r': case 'R':
        setRepeatState(!repeatEnabled);
        hostSend('toggleRepeat');
        break;
    }
  });

})();
