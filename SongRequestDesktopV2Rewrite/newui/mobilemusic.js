(function () {
  'use strict';

  // ── Small helpers ────────────────────────────────────────
  function $(id) { return document.getElementById(id); }

  function esc(value) {
    return String(value == null ? '' : value)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function formatTime(seconds) {
    if (!isFinite(seconds) || seconds < 0) seconds = 0;
    var total = Math.floor(seconds);
    var h = Math.floor(total / 3600);
    var m = Math.floor((total % 3600) / 60);
    var s = total % 60;
    if (h > 0) return h + ':' + (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s;
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  function parseDuration(text) {
    if (!text) return 0;
    var parts = String(text).split(':').map(function (p) { return parseInt(p, 10) || 0; });
    if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2];
    if (parts.length === 2) return parts[0] * 60 + parts[1];
    return parts[0] || 0;
  }

  function relativeTime(value) {
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

  // Small transient message (no external library).
  function toast(message, type) {
    var host = $('mm-toasts');
    if (!host) {
      host = document.createElement('div');
      host.id = 'mm-toasts';
      document.body.appendChild(host);
    }

    var el = document.createElement('div');
    el.className = 'mm-toast' + (type ? ' is-' + type : '');
    el.textContent = message;
    host.appendChild(el);

    setTimeout(function () { el.classList.add('is-out'); }, 2200);
    setTimeout(function () { if (el.parentNode) el.remove(); }, 2700);
  }

  function plIconName(name) {
    return window.SongRequestPlaylistIcons
      ? window.SongRequestPlaylistIcons.normalize(name)
      : (name && /^fa-[a-z0-9-]+$/.test(name) ? name : 'fa-list');
  }

  function plIconHtml(name) {
    return '<i class="fas ' + plIconName(name) + '"></i>';
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
          entry.resolve(message.result);
        }
        return;
      }

      if (message.type === 'event') onEvent(message.eventName, message.data || {});
    } catch (error) { /* ignore malformed host messages */ }
  });

  // ── State ────────────────────────────────────────────────
  var currentTab = 'home';
  var lyricsOpen = false;

  var now = { title: '', artist: '', art: '', playing: false, time: 0, total: 0, quality: null, lyricsSource: '' };
  var seekDragging = false;

  var queue = [];
  var playlistQueue = [];
  var currentPlaylist = null;
  var playlistReturnTab = 'library';
  var playlistIconSelection = 'fa-list';
  // The queue sheet is usually closed. Rendering it on every host update was a
  // large part of the input lag while queuing; only build it when visible.
  var queueDirty = true;
  var queueSignature = '';
  var queueSelecting = false;
  var queueSelection = {};

  var feed = null;
  var feedAt = 0;

  var lib = { mode: 'songs', page: 0, total: 0, loading: false, query: '', sortBy: 'title', descending: false };
  var libSongs = [];
  var recentSongs = [];
  var playlists = [];
  var recentAt = 0;
  var playlistsAt = 0;
  var libAt = 0;
  var libKey = '';
  var libSelecting = false;
  var selectedSongIds = {};
  var LIB_SORTS = [
    { id: 'title', label: 'Title', descending: false },
    { id: 'artist', label: 'Artist', descending: false },
    { id: 'dateAdded', label: 'Recently added', descending: true },
    { id: 'playCount', label: 'Most played', descending: true }
  ];
  (function restoreLibrarySort() {
    try {
      var saved = JSON.parse(localStorage.getItem('sr-mobile-library-sort') || 'null');
      if (saved && LIB_SORTS.some(function (s) { return s.id === saved.id; })) {
        lib.sortBy = saved.id;
        lib.descending = !!saved.descending;
      }
    } catch (_) { }
  })();

  var lyrics = { synced: [], plain: [], active: -1, syncOffset: 0, fontScale: 1, hidden: false, karaokeTimes: [] };
  // Lyrics are only turned into DOM while the lyrics view is open. The payload
  // is kept so opening the view is instant.
  var lyricData = null;
  var lyricLineEls = [];
  var lyricTimed = [];
  var lyricsDirty = true;
  var karaokeSettingFromPayload = false;
  var lastColors = '';
  var viewAnimTimer = null;

  // ── Element refs ─────────────────────────────────────────
  var appEl = $('mm-app');
  var views = {
    home: $('mm-view-home'),
    library: $('mm-view-library'),
    player: $('mm-view-player'),
    share: $('mm-view-share'),
    playlist: $('mm-view-playlist'),
  };

  var playback = { volume: 0.8, crossfade: 4 };
  var share = { sharing: false, receiving: false, status: 'idle', sessionId: null, volume: 1 };
  var lastQuery = '';
  var repeatEnabled = false;

  var playerView = views.player;
  var artWrap = $('mm-art-wrap');
  var artEl = $('mm-art');
  var titleEl = $('mm-title');
  var artistEl = $('mm-artist');
  var mmAiBadge = $('mm-ai-badge');
  var lyricsEl = $('mm-lyrics');
  var lyricsToggle = $('mm-lyrics-toggle');
  var lyricsSettingsToggle = $('mm-lyrics-settings-toggle');
  var lyricsSheetOpen = false;
  var playBtn = $('mm-play');
  var repeatBtn = $('mm-repeat');
  var queueToggle = $('mm-queue-toggle');
  var seekEl = $('mm-seek');
  var curEl = $('mm-cur');
  var totalEl = $('mm-total');

  function applyMobileAiBadge(active, modelName) {
    if (!mmAiBadge) return;
    mmAiBadge.hidden = !active;
    mmAiBadge.title = active
      ? 'AI vocal removal active' + (modelName ? ' · ' + modelName : '')
      : 'AI vocal removal is not active';
  }

  var miniEl = $('mm-mini');
  var miniArt = $('mm-mini-art');
  var miniTitle = $('mm-mini-title');
  var miniArtist = $('mm-mini-artist');
  var miniPlay = $('mm-mini-play');

  var queueEl = $('mm-queue');
  var queueScrim = $('mm-queue-scrim');
  var queueListEl = $('mm-queue-list');

  // ── Views ────────────────────────────────────────────────
  // A short transform/opacity enter keeps tab changes feeling connected without
  // animating layout (which is what made the old player stutter).
  function animateView(tab) {
    var el = views[tab];
    if (!el) return;
    el.classList.remove('is-entering');
    // Restart the animation even when switching back to the same view.
    void el.offsetWidth;
    el.classList.add('is-entering');
    clearTimeout(viewAnimTimer);
    viewAnimTimer = setTimeout(function () { el.classList.remove('is-entering'); }, 220);
  }

  function setTab(tab) {
    if (tab === 'queue') {
      openQueue();
      return;
    }

    // The car navigation exposes Playlists as its own tab; it reuses the
    // library view so the same list/search code serves both destinations.
    var libraryMode = null;
    if (tab === 'playlists') {
      libraryMode = 'playlists';
      tab = 'library';
    }

    if (!views[tab]) tab = 'home';
    if (libSelecting && tab !== 'library') setLibrarySelecting(false);
    currentTab = tab;
    updateLyricsScreenAwake();

    for (var name in views) views[name].hidden = name !== tab;
    animateView(tab);

    closeQueue(false);
    updateMini();

    if (tab === 'home') loadHomeFeed(false);
    if (tab === 'library') {
      if (libraryMode) {
        if (lib.mode === libraryMode) loadLibrary(false);
        else setLibraryMode(libraryMode);
      } else if (document.documentElement.getAttribute('data-car') === '1') {
        // The car's Library tab is the searchable song list; Playlists has its
        // own tab, so do not inherit the previous chip selection here.
        if (lib.mode !== 'songs') setLibraryMode('songs');
        else loadLibrary(false);
      } else {
        loadLibrary(false);
      }
    }
    if (tab === 'player') updatePlayer();
    if (tab === 'share') loadShareStatus();
  }

  function openQueue() {
    if (queueDirty) renderQueue();
    queueScrim.hidden = false;
    queueEl.hidden = false;
    updateQueueToggle(true);
    // Next frame so the transform transition runs.
    requestAnimationFrame(function () {
      queueScrim.classList.add('is-open');
      queueEl.classList.add('is-open');
    });
  }

  function closeQueue(notifyHost) {
    if (queueEl.hidden) return;
    if (queueSelecting) setQueueSelecting(false);

    queueScrim.classList.remove('is-open');
    queueEl.classList.remove('is-open');
    updateQueueToggle(false);
    setTimeout(function () {
      if (!queueEl.classList.contains('is-open')) {
        queueScrim.hidden = true;
        queueEl.hidden = true;
      }
    }, 280);

    if (notifyHost !== false) send('mobileQueueClosed');
  }

  function updateQueueToggle(open) {
    if (!queueToggle) return;
    queueToggle.classList.toggle('is-active', !!open);
    queueToggle.setAttribute('aria-pressed', open ? 'true' : 'false');
  }

  // ── Home feed ────────────────────────────────────────────
  function loadHomeFeed(force) {
    // The DOM is kept alive between tab switches, so a fresh feed needs no
    // rebuild at all (rebuilding reset the scroll position).
    if (!force && feed && Date.now() - feedAt < 8000) return;

    $('mm-home-loading').hidden = false;
    $('mm-home-empty').hidden = true;

    request('getHomeFeed', { count: 24 }).then(function (data) {
      feed = data || {};
      feedAt = Date.now();
      renderHome();
    }).catch(function () {
      $('mm-home-loading').hidden = true;
      if (!feed) $('mm-home-empty').hidden = false;
    });
  }

  function renderHome() {
    $('mm-home-loading').hidden = true;

    var stats = (feed && feed.stats) || {};
    var empty = !stats.totalSongs && !(feed && feed.newlyAdded && feed.newlyAdded.length);
    $('mm-home-empty').hidden = !empty;

    var recent = (feed && feed.recentlyPlayed) || [];
    $('mm-row-recent').hidden = recent.length === 0;
    $('mm-recent').innerHTML = recent.map(songCard).join('');

    var lists = (feed && feed.playlists) || [];
    $('mm-row-playlists').hidden = lists.length === 0;
    $('mm-playlists').innerHTML = lists.map(playlistCard).join('');

    var recommended = (feed && feed.recommended) || [];
    $('mm-row-recommended').hidden = recommended.length === 0;
    $('mm-recommended').innerHTML = recommended.map(songCard).join('');

    var added = (feed && feed.newlyAdded) || [];
    $('mm-row-new').hidden = added.length === 0;
    $('mm-new').innerHTML = added.map(songCard).join('');

    patchThumbnails($('mm-home-feed'), 0);
  }

  function songCard(song) {
    var id = song.songId || song.id || '';
    var artClass = 'mm-card-art' + (song.thumbnail ? '' : ' thumb-pending');
    return '<button class="mm-card" data-song-id="' + esc(id) + '">' +
      '<div class="' + artClass + '" data-songid="' + esc(id) + '">' + (song.thumbnail ? '<img src="' + esc(song.thumbnail) + '" alt="" loading="lazy" decoding="async">' : '<i class="fas fa-music"></i>') + '</div>' +
      '<div class="mm-card-title">' + esc(song.title || 'Unknown') + '</div>' +
      '<div class="mm-card-sub">' + esc(song.artist || '—') + '</div>' +
      '</button>';
  }

  function playlistCard(list) {
    return '<button class="mm-card' + (list.isDynamic ? ' is-dynamic' : '') + '" data-playlist-id="' + esc(list.id || '') + '">' +
      '<div class="mm-card-art">' + (list.thumbnail ? '<img src="' + esc(list.thumbnail) + '" alt="" loading="lazy" decoding="async">' : plIconHtml(list.icon)) + '</div>' +
      '<div class="mm-card-title">' + esc(list.name || 'Playlist') + '</div>' +
      '<div class="mm-card-sub">' + (list.songCount || 0) + ' songs' + (list.isDynamic ? ' · Auto' : '') + '</div>' +
      '</button>';
  }

  // ── Library ──────────────────────────────────────────────
  function setLibraryMode(mode) {
    if (libSelecting) setLibrarySelecting(false);
    lib.mode = mode;
    lib.page = 0;
    lib.query = '';
    var filter = $('mm-lib-search');
    if (filter) filter.value = '';
    var clear = $('mm-lib-clear');
    if (clear) clear.hidden = true;

    var chips = document.querySelectorAll('.mm-chip');
    chips.forEach(function (chip) { chip.classList.toggle('is-active', chip.dataset.lib === mode); });

    var title = $('mm-library-title');
    if (title) {
      title.textContent = mode === 'playlists' ? 'Playlists'
        : mode === 'recent' ? 'Recently Played'
        : 'Library';
    }

    // On a car screen the search field stays visible even while playlists are
    // shown, so there is always a way to search; typing switches to songs.
    var car = document.documentElement.getAttribute('data-car') === '1';
    $('mm-lib-search-wrap').hidden = mode !== 'songs' && !car;
    $('mm-lib-songs').hidden = mode !== 'songs';
    $('mm-lib-recent').hidden = mode !== 'recent';
    $('mm-lib-playlists').hidden = mode !== 'playlists';
    $('mm-lib-more-wrap').hidden = true;
    $('mm-playlist-new-wrap').hidden = mode !== 'playlists';
    $('mm-library-scroll').scrollTop = 0;
    updateLibraryTools();

    loadLibrary(true);
  }

  function updateLibraryTools() {
    var sort = $('mm-library-sort');
    var select = $('mm-library-select');
    if (sort) sort.hidden = lib.mode !== 'songs';
    if (select) select.hidden = lib.mode === 'playlists';
    if (lib.mode === 'playlists' && libSelecting) setLibrarySelecting(false);
  }

  // Switches the library to the song list without clearing a query the user is
  // currently typing (used by the always-visible car search field).
  function ensureSongsMode() {
    if (lib.mode === 'songs') return;
    lib.mode = 'songs';
    lib.page = 0;
    document.querySelectorAll('.mm-chip').forEach(function (chip) {
      chip.classList.toggle('is-active', chip.dataset.lib === 'songs');
    });
    var title = $('mm-library-title');
    if (title) title.textContent = 'Library';
    $('mm-lib-search-wrap').hidden = false;
    $('mm-lib-songs').hidden = false;
    $('mm-lib-recent').hidden = true;
    $('mm-lib-playlists').hidden = true;
    $('mm-lib-more-wrap').hidden = true;
    $('mm-playlist-new-wrap').hidden = true;
    updateLibraryTools();
  }

  function loadLibrary(force, more) {
    var key = lib.mode + '|' + lib.query + '|' + lib.sortBy + '|' + lib.descending;
    var fresh = libAt > 0 && key === libKey && Date.now() - libAt < 20000;

    if (lib.mode === 'recent') {
      // Tab switches reuse the list that is already in the DOM.
      if (!force && !more && recentAt > 0 && Date.now() - recentAt < 15000) return;
      request('getPlayHistory', { count: 60 }).then(function (data) {
        recentSongs = (data && data.history) || [];
        recentAt = Date.now();
        renderRecent();
      }).catch(function () { renderRecent(); });
      return;
    }

    if (lib.mode === 'playlists') {
      if (!force && !more && playlistsAt > 0 && Date.now() - playlistsAt < 15000) return;
      request('getPlaylists').then(function (data) {
        playlists = (data && data.playlists) || [];
        playlistsAt = Date.now();
        renderPlaylists();
      }).catch(function () { renderPlaylists(); });
      return;
    }

    if (!more && !force && fresh) return;

    if (lib.query) {
      request('searchLibrary', { query: lib.query, maxResults: 100 }).then(function (data) {
        libSongs = (data && data.results) || [];
        lib.total = libSongs.length;
        lib.page = 1;
        libAt = Date.now();
        libKey = key;
        renderSongs();
      }).catch(function () { renderSongs(); });
      return;
    }

    if (lib.loading) return;
    lib.loading = true;
    var page = more && !force ? lib.page + 1 : 1;

    request('getLibrarySongs', {
      page: page,
      pageSize: 60,
      sortBy: lib.sortBy,
      descending: lib.descending,
      filterMissing: true
    }).then(function (data) {
      var songs = (data && data.songs) || [];
      libSongs = page === 1 ? songs : libSongs.concat(songs);
      lib.page = page;
      lib.total = (data && data.total) || libSongs.length;
      libAt = Date.now();
      libKey = key;
      renderSongs();
    }).catch(function () {
      renderSongs();
    }).finally(function () {
      lib.loading = false;
    });
  }

  function renderSongs() {
    $('mm-lib-songs').innerHTML = libSongs.length
      ? libSongs.map(songRow).join('')
      : '<div class="mm-empty"><i class="fas fa-compact-disc"></i><span>No songs found</span></div>';

    $('mm-lib-more-wrap').hidden = libSongs.length >= lib.total || !!lib.query;
    patchThumbnails($('mm-lib-songs'), 0);
    applyLibrarySelection();
  }

  function renderRecent() {
    $('mm-lib-recent').innerHTML = recentSongs.length
      ? recentSongs.map(function (entry) {
          return songRow({
            id: entry.songId,
            title: entry.title,
            artist: entry.artist,
            thumbnail: entry.thumbnail,
            durationDisplay: entry.durationDisplay,
            playedUtc: entry.playedUtc
          });
        }).join('')
      : '<div class="mm-empty"><i class="fas fa-clock-rotate-left"></i><span>Nothing played yet</span></div>';
    patchThumbnails($('mm-lib-recent'), 0);
    applyLibrarySelection();
  }

  function renderPlaylists() {
    $('mm-lib-playlists').innerHTML = playlists.length
      ? playlists.map(function (list) {
          return '<button class="mm-song' + (list.isDynamic ? ' is-dynamic' : '') + '" data-playlist-id="' + esc(list.id || '') + '">' +
            '<div class="mm-thumb mm-playlist-icon">' + (list.thumbnail ? '<img src="' + esc(list.thumbnail) + '" alt="" loading="lazy" decoding="async">' : plIconHtml(list.icon)) + '</div>' +
            '<div class="mm-song-text">' +
              '<div class="mm-song-title">' + esc(list.name || 'Playlist') + '</div>' +
              '<div class="mm-song-sub">' + (list.songCount || 0) + ' songs' + (list.isDynamic ? ' · Auto' : '') + '</div>' +
            '</div>' +
            '<i class="fas fa-play mm-song-dur"></i>' +
            '</button>';
        }).join('')
      : '<div class="mm-empty"><i class="fas fa-list"></i><span>No playlists yet</span></div>';
  }

  function songRow(song) {
    var id = song.songId || song.id || '';
    var duration = song.durationDisplay || formatTime(song.duration || 0);
    var sub = esc(song.artist || '—');
    if (song.playedUtc) {
      var played = relativeTime(song.playedUtc);
      if (played) sub += ' · Played ' + esc(played);
    }
    var thumbClass = 'mm-thumb' + (song.thumbnail ? '' : ' thumb-pending');
    return '<button class="mm-song" data-song-id="' + esc(id) + '">' +
      '<span class="mm-select-check" aria-hidden="true"><i class="fas fa-check"></i></span>' +
      '<div class="' + thumbClass + '" data-songid="' + esc(id) + '">' + (song.thumbnail ? '<img src="' + esc(song.thumbnail) + '" alt="" loading="lazy" decoding="async">' : '<i class="fas fa-music"></i>') + '</div>' +
      '<div class="mm-song-text">' +
        '<div class="mm-song-title">' + esc(song.title || 'Unknown') + '</div>' +
        '<div class="mm-song-sub">' + sub + '</div>' +
      '</div>' +
      '<span class="mm-song-dur">' + esc(duration) + '</span>' +
      '</button>';
  }

  // Artwork that the host has not downscaled yet arrives as a placeholder.
  // Ask for it again shortly and patch only the image, without re-rendering
  // the whole list (which would reset its scroll position).
  var _thumbTimers = [];
  function patchThumbnails(root, attempt) {
    if (!root) return;
    attempt = attempt || 0;
    if (attempt > 8) return;

    var pending = root.querySelectorAll('.thumb-pending[data-songid]');
    if (!pending.length) return;

    var ids = [];
    for (var i = 0; i < pending.length && ids.length < 24; i++) {
      var id = pending[i].dataset.songid;
      if (id && ids.indexOf(id) < 0) ids.push(id);
    }
    if (!ids.length) return;

    request('getThumbnails', { songIds: ids }).then(function (result) {
      var thumbs = (result && result.thumbs) || {};
      var patched = 0;
      for (var i = 0; i < pending.length; i++) {
        var el = pending[i];
        var url = thumbs[el.dataset.songid];
        if (!url) continue;
        el.classList.remove('thumb-pending');
        var icon = el.querySelector('i');
        if (icon) icon.remove();
        var play = el.querySelector('.mm-card-play');
        var img = document.createElement('img');
        img.src = url;
        img.alt = '';
        img.loading = 'lazy';
        if (play) el.insertBefore(img, play); else el.insertBefore(img, el.firstChild);
        patched++;
      }
      if (patched < pending.length) scheduleThumbPatch(root, attempt + 1);
    }).catch(function () { scheduleThumbPatch(root, attempt + 1); });
  }

  function scheduleThumbPatch(root, attempt) {
    var timer = setTimeout(function () { patchThumbnails(root, attempt); }, 700 + attempt * 400);
    _thumbTimers.push(timer);
    if (_thumbTimers.length > 40) _thumbTimers.splice(0, 20);
  }

  // ── Playlist detail (browse + modify) ────────────────────
  function openPlaylist(id) {
    if (!id) return;
    if (libSelecting) setLibrarySelecting(false);
    if (currentTab !== 'playlist') playlistReturnTab = currentTab;

    request('getPlaylist', { playlistId: id }).then(function (data) {
      if (!data || data.error) { toast((data && data.error) || 'Playlist not found', 'error'); return; }

      currentPlaylist = data;
      currentTab = 'playlist';
      updateLyricsScreenAwake();
      for (var name in views) views[name].hidden = name !== 'playlist';
      closeQueue(false);
      updateMini();
      renderPlaylist();
    }).catch(function (error) { toast(error.message, 'error'); });
  }

  function renderPlaylist() {
    if (!currentPlaylist) return;

    $('mm-playlist-title').innerHTML = plIconHtml(currentPlaylist.icon) + ' ' + esc(currentPlaylist.name || 'Playlist');
    var subtitle = (currentPlaylist.songs ? currentPlaylist.songs.length : 0) + ' songs';
    if (currentPlaylist.isDynamic && currentPlaylist.description) subtitle += ' · ' + currentPlaylist.description;
    $('mm-playlist-sub').textContent = subtitle;

    var songs = currentPlaylist.songs || [];
    var editable = !currentPlaylist.isDynamic;
    $('mm-playlist-list').innerHTML = songs.length
      ? songs.map(function (entry, index) { return playlistSongRow(entry, index, songs.length, editable); }).join('')
      : '<div class="mm-empty"><i class="fas fa-list"></i><span>This playlist is empty</span>' +
        (editable ? '<button class="mm-btn" data-playlist-action="add"><i class="fas fa-plus"></i> Add songs</button>' : '') +
        '</div>';
    patchThumbnails($('mm-playlist-list'), 0);
  }

  function playlistSongRow(entry, index, total, editable) {
    var duration = entry.durationDisplay || formatTime(entry.duration || 0);
    var controls = editable
      ? '<div class="mm-entry-controls">' +
          '<button class="mm-icon-btn" data-entry-move="up" data-index="' + index + '" aria-label="Move up"' +
            (index === 0 ? ' disabled' : '') + '><i class="fas fa-chevron-up"></i></button>' +
          '<button class="mm-icon-btn" data-entry-move="down" data-index="' + index + '" aria-label="Move down"' +
            (index >= total - 1 ? ' disabled' : '') + '><i class="fas fa-chevron-down"></i></button>' +
          '<button class="mm-icon-btn" data-entry-remove data-index="' + index + '" aria-label="Remove from playlist">' +
            '<i class="fas fa-times"></i></button>' +
        '</div>'
      : '';

    return '<div class="mm-playlist-song" data-entry-id="' + esc(entry.entryId || '') + '">' +
      '<button class="mm-song mm-song-main" data-song-id="' + esc(entry.songId || '') + '">' +
        '<div class="mm-thumb' + (entry.thumbnail ? '' : ' thumb-pending') + '" data-songid="' + esc(entry.songId || '') + '">' + (entry.thumbnail ? '<img src="' + esc(entry.thumbnail) + '" alt="" loading="lazy" decoding="async">' : '<i class="fas fa-music"></i>') + '</div>' +
        '<div class="mm-song-text">' +
          '<div class="mm-song-title">' + esc(entry.title || 'Unknown') + '</div>' +
          '<div class="mm-song-sub">' + esc(entry.artist || '—') + '</div>' +
        '</div>' +
        '<span class="mm-song-dur">' + esc(duration) + '</span>' +
      '</button>' +
      controls +
      '</div>';
  }

  function closePlaylist() {
    currentPlaylist = null;
    setTab(playlistReturnTab || 'library');
  }

  function reloadPlaylist() {
    if (!currentPlaylist) return;
    var id = currentPlaylist.id;
    request('getPlaylist', { playlistId: id }).then(function (data) {
      if (!data || data.error) { toast((data && data.error) || 'Playlist not found', 'error'); closePlaylist(); return; }
      currentPlaylist = data;
      renderPlaylist();
      feed = null;
      feedAt = 0;
      if (lib.mode === 'playlists') loadLibrary(true);
    }).catch(function (error) { toast(error.message, 'error'); });
  }

  function openPlaylistMenu() {
    if (!currentPlaylist) return;
    var dynamic = !!currentPlaylist.isDynamic;
    openSheet(
      '<h3>' + plIconHtml(currentPlaylist.icon) + ' ' + esc(currentPlaylist.name || 'Playlist') + '</h3>' +
      '<button class="mm-sheet-action" data-playlist-action="queue"><i class="fas fa-list"></i> Queue playlist</button>' +
      (dynamic
        ? '<div class="mm-sheet-empty">' + esc(currentPlaylist.description || 'This playlist updates automatically.') + '</div>'
        : '<button class="mm-sheet-action" data-playlist-action="rename"><i class="fas fa-pen"></i> Rename playlist</button>' +
          '<button class="mm-sheet-action" data-playlist-action="icon"><i class="fas fa-icons"></i> Change icon</button>' +
          '<button class="mm-sheet-action" data-playlist-action="add"><i class="fas fa-plus"></i> Add songs from library</button>' +
          '<button class="mm-sheet-action is-danger" data-playlist-action="delete"><i class="fas fa-trash-alt"></i> Delete playlist</button>')
    );
  }

  function renderPlaylistIconPicker() {
    var picker = $('mm-playlist-icon-picker');
    if (!picker) return;
    if (!window.SongRequestPlaylistIcons) {
      picker.innerHTML = '<div class="mm-sheet-empty">Icon browser unavailable.</div>';
      return;
    }
    window.SongRequestPlaylistIcons.renderPicker(picker, playlistIconSelection, function (name) {
      playlistIconSelection = name;
    });
  }

  function onPlaylistAction(action) {
    if (!currentPlaylist) return;

    if (action === 'queue') {
      send('queuePlaylist', { playlistId: currentPlaylist.id });
      toast('Playlist added to the queue', 'success');
      closeSheet();
      return;
    }

    if (currentPlaylist.isDynamic) return;

    if (action === 'add') {
      closeSheet();
      closePlaylist();
      setTab('library');
      setLibraryMode('songs');
      toast('Long-press a song, then choose “Add to playlist”', 'info');
      return;
    }

    if (action === 'rename') {
      playlistIconSelection = plIconName(currentPlaylist.icon);
      openSheet(
        '<h3>Edit playlist</h3>' +
        '<input id="mm-playlist-rename" class="mm-sheet-input" value="' + esc(currentPlaylist.name || '') + '" placeholder="Playlist name">' +
        '<div class="mm-icon-heading">Icon</div>' +
        '<div id="mm-playlist-icon-picker" class="mm-icon-picker"></div>' +
        '<button id="mm-playlist-rename-save" class="mm-btn mm-share-primary" style="width:100%;margin-top:12px">Save changes</button>'
      );
      renderPlaylistIconPicker();
      return;
    }

    if (action === 'icon') {
      playlistIconSelection = plIconName(currentPlaylist.icon);
      openSheet(
        '<h3>Playlist icon</h3>' +
        '<div id="mm-playlist-icon-picker" class="mm-icon-picker"></div>' +
        '<button id="mm-playlist-icon-save" class="mm-btn mm-share-primary" style="width:100%;margin-top:12px">Save icon</button>'
      );
      renderPlaylistIconPicker();
      return;
    }

    if (action === 'delete') {
      var id = currentPlaylist.id;
      openConfirmSheet(
        'Delete playlist',
        'Delete “' + (currentPlaylist.name || 'Playlist') + '”? The songs stay in your library.',
        'Delete',
        function () {
          request('deletePlaylist', { playlistId: id }).then(function (result) {
            if (result && result.error) { toast(result.error, 'error'); return; }
            toast('Playlist deleted', 'success');
            currentPlaylist = null;
            feed = null;
            feedAt = 0;
            lib.page = 0;
            setTab('library');
            setLibraryMode('playlists');
          }).catch(function (error) { toast(error.message, 'error'); });
        }
      );
    }
  }

  // ── Player ───────────────────────────────────────────────
  function setArt(src) {
    if (!src) return;
    if (now.art === src) return;
    now.art = src;

    artEl.src = src;
    artEl.hidden = false;
    $('mm-art-placeholder').hidden = true;

    miniArt.src = src;
    miniArt.hidden = false;

    extractColors(src).then(applyMusicColors);
  }

  function updatePlayer() {
    titleEl.textContent = now.title || 'Nothing playing';
    artistEl.textContent = now.artist || '—';
    updateSeekUi();
    setPlayingUi(now.playing);
  }

  function formatFromPath(path) {
    if (!path) return '';
    var clean = String(path).split('?')[0];
    var dot = clean.lastIndexOf('.');
    if (dot < 0) return '';
    return clean.slice(dot + 1).toUpperCase();
  }

  function setQuality(fileType, bitrateKbps, songPath) {
    var format = String(fileType || '').trim().toUpperCase();
    if (!format) format = formatFromPath(songPath);
    var bitrate = Number(bitrateKbps) || 0;

    if (!format && bitrate <= 0) {
      now.quality = null;
      refreshOptionsMeta();
      return;
    }

    var lossless = ['FLAC', 'WAV', 'ALAC', 'AIFF', 'APE', 'DSF'].indexOf(format) >= 0;
    var text = format || 'Audio';
    if (lossless) text += ' · Lossless';
    else if (bitrate > 0) text += ' · ' + bitrate + ' kbps';

    var tier = 'is-unknown';
    if (lossless) tier = 'is-lossless';
    else if (bitrate >= 256) tier = 'is-high';
    else if (bitrate >= 160) tier = 'is-good';
    else if (bitrate > 0) tier = 'is-low';

    now.quality = { text: text, tier: tier };
    refreshOptionsMeta();
  }

  function setLyricsSource(provider) {
    now.lyricsSource = String(provider || '').trim();
    refreshOptionsMeta();
  }

  function qualityBadgeHtml() {
    if (!now.quality) return '';
    return '<span class="mm-badge mm-quality ' + now.quality.tier + '">' +
      '<i class="fas fa-signal"></i><span>' + esc(now.quality.text) + '</span></span>';
  }

  function lyricsBadgeHtml() {
    if (!now.lyricsSource) return '';
    return '<span class="mm-badge mm-lyrics-source">' +
      '<i class="fas fa-align-left"></i><span>' + esc(now.lyricsSource) + '</span></span>';
  }

  function optionsMetaHtml() {
    var html = qualityBadgeHtml() + lyricsBadgeHtml();
    return html || '<div class="mm-sheet-empty">No audio metadata available yet.</div>';
  }

  function refreshOptionsMeta() {
    var host = $('mm-options-meta');
    if (host) host.innerHTML = optionsMetaHtml();
  }

  function updateMini() {
    var show = !!(now.title && (currentTab === 'home' || currentTab === 'library' || currentTab === 'playlist'));
    if (show && miniEl.hidden) {
      miniEl.classList.remove('is-entering');
      void miniEl.offsetWidth;
      miniEl.classList.add('is-entering');
    }
    miniEl.hidden = !show;

    if (!show) return;
    if (miniTitle.textContent !== now.title) miniTitle.textContent = now.title;
    var artist = now.artist || '—';
    if (miniArtist.textContent !== artist) miniArtist.textContent = artist;
    if (now.art && miniArt.getAttribute('src') !== now.art) {
      miniArt.src = now.art;
      miniArt.hidden = false;
    }
    setMiniPlayIcon(now.playing);
  }

  function setPlayingUi(playing) {
    playing = !!playing;
    if (playBtn.dataset.playing === (playing ? '1' : '0')) return;
    playBtn.dataset.playing = playing ? '1' : '0';
    playBtn.innerHTML = playing ? '<i class="fas fa-pause"></i>' : '<i class="fas fa-play"></i>';
    playBtn.setAttribute('aria-label', playing ? 'Pause' : 'Play');
    setMiniPlayIcon(playing);
  }

  function setRepeatState(on) {
    repeatEnabled = !!on;
    if (!repeatBtn) return;
    repeatBtn.classList.toggle('is-active', repeatEnabled);
    repeatBtn.setAttribute('aria-pressed', repeatEnabled ? 'true' : 'false');
    repeatBtn.setAttribute('aria-label', repeatEnabled ? 'Repeat on' : 'Repeat off');
  }

  function setMiniPlayIcon(playing) {
    playing = !!playing;
    if (miniPlay.dataset.playing === (playing ? '1' : '0')) return;
    miniPlay.dataset.playing = playing ? '1' : '0';
    miniPlay.innerHTML = playing ? '<i class="fas fa-pause"></i>' : '<i class="fas fa-play"></i>';
    miniPlay.setAttribute('aria-label', playing ? 'Pause' : 'Play');
  }

  function updateSeekUi() {
    var total = now.total || 0;
    var pct = total > 0 ? Math.min(1000, Math.max(0, now.time / total * 1000)) : 0;
    var value = String(Math.round(pct));
    if (!seekDragging && seekEl.value !== value) seekEl.value = value;
    var current = formatTime(now.time);
    if (curEl.textContent !== current) curEl.textContent = current;
    var totalText = formatTime(total);
    if (totalEl.textContent !== totalText) totalEl.textContent = totalText;
  }

  // Keep the phone awake only while the lyrics view is actually visible on the
  // player tab. The host also releases this when the app is backgrounded.
  function updateLyricsScreenAwake() {
    send('setLyricsScreenAwake', { enabled: lyricsOpen && currentTab === 'player' });
  }

  function toggleLyrics(force) {
    lyricsOpen = force == null ? !lyricsOpen : !!force;
    playerView.classList.toggle('is-lyrics', lyricsOpen);
    lyricsEl.hidden = !lyricsOpen;
    lyricsToggle.setAttribute('aria-pressed', lyricsOpen ? 'true' : 'false');

    // The chips keep their labels in lyrics mode; only the extra settings chip
    // is revealed (and hidden again when lyrics are closed).
    if (lyricsSettingsToggle) {
      lyricsSettingsToggle.hidden = !lyricsOpen;
      if (!lyricsOpen) closeLyricsSettings();
    }

    updateLyricsScreenAwake();

    if (lyricsOpen) {
      lyricsEl.classList.remove('is-opening');
      void lyricsEl.offsetWidth;
      lyricsEl.classList.add('is-opening');
      if (lyricsDirty) renderLyricsNow();
      applyLyricsFontScale();
      highlightLyrics(now.time, true);
    }
  }

  function setLyricsSettingsChipActive(active) {
    if (!lyricsSettingsToggle) return;
    lyricsSettingsToggle.classList.toggle('is-active', !!active);
    lyricsSettingsToggle.setAttribute('aria-expanded', active ? 'true' : 'false');
  }

  function toggleLyricsSettings() {
    if (lyricsSheetOpen) closeLyricsSettings(); else openLyricsSettings();
  }

  // The lyrics panel uses the same bottom sheet as the Options chip, so both
  // player menus look and behave the same way.
  function openLyricsSettings() {
    lyricsSheetOpen = true;
    setLyricsSettingsChipActive(true);
    openSheet(lyricsSettingsSheetHtml(), 'lyrics');
    wireLyricsSettingsSheet();
  }

  function closeLyricsSettings() {
    lyricsSheetOpen = false;
    setLyricsSettingsChipActive(false);
    var body = $('mm-sheet-body');
    if (body && body.dataset.sheet === 'lyrics') closeSheet();
  }

  function lyricsSettingsSheetHtml() {
    return '<h3>Lyrics</h3>' +
      '<div class="mm-sheet-row">' +
        '<div class="mm-sheet-label">Sync<div class="mm-sheet-sub">Match lyrics to the song</div></div>' +
        '<button id="mm-sync-back" class="mm-settings-btn" aria-label="Show lyrics earlier" title="Earlier"><i class="fas fa-minus"></i></button>' +
        '<span id="mm-sync-value" class="mm-settings-value">0.00s</span>' +
        '<button id="mm-sync-forward" class="mm-settings-btn" aria-label="Show lyrics later" title="Later"><i class="fas fa-plus"></i></button>' +
        '<button id="mm-sync-reset" class="mm-settings-btn" aria-label="Reset lyrics synchronization" title="Reset sync"><i class="fas fa-undo"></i></button>' +
      '</div>' +
      '<div class="mm-sheet-row">' +
        '<div class="mm-sheet-label">Size<div class="mm-sheet-sub">Lyrics text size</div></div>' +
        '<button id="mm-font-decrease" class="mm-settings-btn mm-font-btn" aria-label="Decrease lyrics size" title="Smaller">A<span>-</span></button>' +
        '<span id="mm-font-value" class="mm-settings-value">100%</span>' +
        '<button id="mm-font-increase" class="mm-settings-btn mm-font-btn" aria-label="Increase lyrics size" title="Larger">A<span>+</span></button>' +
        '<button id="mm-font-reset" class="mm-settings-btn" aria-label="Reset lyrics size" title="Reset size"><i class="fas fa-undo"></i></button>' +
      '</div>';
  }

  function wireLyricsSettingsSheet() {
    var back = $('mm-sync-back');
    var forward = $('mm-sync-forward');
    var syncReset = $('mm-sync-reset');
    var fontDecrease = $('mm-font-decrease');
    var fontIncrease = $('mm-font-increase');
    var fontReset = $('mm-font-reset');

    if (back) back.addEventListener('click', function () { setLyricsSyncOffset(lyrics.syncOffset - 0.25); });
    if (forward) forward.addEventListener('click', function () { setLyricsSyncOffset(lyrics.syncOffset + 0.25); });
    if (syncReset) syncReset.addEventListener('click', function () { setLyricsSyncOffset(0); });
    if (fontDecrease) fontDecrease.addEventListener('click', function () { setLyricsFontScale(lyrics.fontScale - 0.1); });
    if (fontIncrease) fontIncrease.addEventListener('click', function () { setLyricsFontScale(lyrics.fontScale + 0.1); });
    if (fontReset) fontReset.addEventListener('click', function () { setLyricsFontScale(1); });

    updateLyricsSyncUi();
    updateLyricsFontUi();
  }

  function updateLyricsSyncUi() {
    var valueEl = $('mm-sync-value');
    if (!valueEl) return;
    var sign = lyrics.syncOffset >= 0 ? '+' : '';
    valueEl.textContent = sign + lyrics.syncOffset.toFixed(2) + 's';
    var resetEl = $('mm-sync-reset');
    if (resetEl) resetEl.classList.toggle('is-muted', lyrics.syncOffset === 0);
  }

  function setLyricsSyncOffset(value) {
    lyrics.syncOffset = Math.max(-5, Math.min(5, Math.round(value * 4) / 4));
    updateLyricsSyncUi();
    lyrics.active = -1;
    highlightLyrics(now.time);
  }

  function applyLyricsFontScale() {
    lyricsEl.style.setProperty('--mm-lyric-scale', String(lyrics.fontScale));
    updateLyricsFontUi();
  }

  function updateLyricsFontUi() {
    var valueEl = $('mm-font-value');
    if (!valueEl) return;
    valueEl.textContent = Math.round(lyrics.fontScale * 100) + '%';
    var resetEl = $('mm-font-reset');
    if (resetEl) resetEl.classList.toggle('is-muted', Math.abs(lyrics.fontScale - 1) < 0.001);
  }

  function setLyricsFontScale(value) {
    lyrics.fontScale = Math.max(0.5, Math.min(2, Math.round(value * 20) / 20));
    lyricsEl.style.setProperty('--mm-lyric-scale', String(lyrics.fontScale));
    updateLyricsFontUi();
    saveLyricsSettings();
  }

  var _saveLyricsTimer = null;
  function saveLyricsSettings() {
    clearTimeout(_saveLyricsTimer);
    _saveLyricsTimer = setTimeout(function () {
      send('saveLyricsSettings', { fontScale: lyrics.fontScale, hidden: lyrics.hidden });
    }, 250);
  }

  // ── Lyrics ───────────────────────────────────────────────
  function onLyrics(data) {
    lyricData = data || null;
    lyrics.synced = (data && data.syncedLyrics) || [];
    lyrics.plain = (data && data.plainLyrics) || [];
    lyrics.karaokeTimes = [];
    if (data && data.karaoke && data.karaoke.lines) {
      for (var kt = 0; kt < data.karaoke.lines.length; kt++) {
        var karaokeTime = Number(data.karaoke.lines[kt].time);
        lyrics.karaokeTimes[kt] = isFinite(karaokeTime) ? karaokeTime : null;
      }
    }
    lyrics.active = -1;
    setLyricsSource(data && data.provider ? data.provider : '');

    if (window.SongRequestKaraoke) {
      // The karaokechange listener must not force a second render while we are
      // already processing the payload that carries the setting.
      karaokeSettingFromPayload = true;
      window.SongRequestKaraoke.setEnabled(data && data.karaokeMode === true);
      karaokeSettingFromPayload = false;
    }

    lyricsDirty = true;
    // Lyrics are expensive to build (one span per syllable). Skip the DOM work
    // completely while the lyrics view is closed; opening it renders once.
    if (lyricsOpen) renderLyricsNow();
  }

  /**
   * Builds the lyrics DOM from the last payload. Called on song change only
   * while the lyrics view is open, when the view is opened, or when karaoke
   * mode is toggled.
   */
  function renderLyricsNow() {
    lyricsDirty = false;
    lyricLineEls = [];
    lyricTimed = [];

    if (!lyricData) {
      lyricsEl.innerHTML = now.title
        ? '<div class="mm-lyrics-empty"><i class="fas fa-circle-notch fa-spin"></i><span>Loading lyrics…</span></div>'
        : '<div class="mm-lyrics-empty"><i class="fas fa-music"></i><span>No lyrics available</span></div>';
      return;
    }

    var synced = lyrics.synced;
    var plain = lyrics.plain;
    var data = lyricData;

    if (!synced.length && !plain.length) {
      lyricsEl.innerHTML = '<div class="mm-lyrics-empty"><i class="fas fa-music"></i><span>No lyrics available</span></div>';
      return;
    }

    var karaokeOn = !!(window.SongRequestKaraoke && window.SongRequestKaraoke.isEnabled());
    var fragment = document.createDocumentFragment();

    if (synced.length) {
      var karaokeLines = data.karaoke && data.karaoke.lines ? data.karaoke.lines : null;
      for (var i = 0; i < synced.length; i++) {
        var item = synced[i] || {};
        var line = document.createElement('div');
        line.className = 'mm-line';
        line.dataset.index = i;
        line.dataset.time = item.time || 0;

        var karaokeLine = karaokeLines ? karaokeLines[i] : null;
        if (karaokeOn && karaokeLine) {
          window.SongRequestKaraoke.renderLine(line, karaokeLine);
        } else {
          line.textContent = item.text || '';
        }

        var timed = karaokeLine && isFinite(Number(karaokeLine.time))
          ? Number(karaokeLine.time)
          : Number(item.time) || 0;
        lyricTimed.push({ time: timed, empty: !String(item.text || '').trim() });
        lyricLineEls.push(line);
        fragment.appendChild(line);
      }
    } else {
      for (var j = 0; j < plain.length; j++) {
        var text = plain[j];
        if (!text) {
          var gap = document.createElement('div');
          gap.className = 'mm-gap';
          fragment.appendChild(gap);
          continue;
        }
        var plainLine = document.createElement('div');
        plainLine.className = 'mm-line';
        plainLine.textContent = text;
        lyricLineEls.push(plainLine);
        fragment.appendChild(plainLine);
      }
    }

    lyricsEl.innerHTML = '';
    lyricsEl.appendChild(fragment);

    if (window.SongRequestKaraoke) {
      window.SongRequestKaraoke.invalidate(lyricsEl);
      if (karaokeOn && data.karaoke) {
        window.SongRequestKaraoke.attachPauses(lyricsEl, data.karaoke.pauses);
      }
      if (karaokeOn) {
        window.SongRequestKaraoke.update(lyricsEl, now.time || 0, now.playing);
      }
    }

    applyLyricsFontScale();
    if (window.SongRequestAdvancedAudio) window.SongRequestAdvancedAudio.decorateLyrics();
    if (lyricsOpen) highlightLyrics(now.time, true);
  }

  // Last non-empty timed line at/before the given time (binary search).
  function lyricIndexAt(time) {
    var lo = 0;
    var hi = lyricTimed.length - 1;
    var found = -1;
    while (lo <= hi) {
      var mid = (lo + hi) >> 1;
      if (lyricTimed[mid].time <= time) {
        found = mid;
        lo = mid + 1;
      } else {
        hi = mid - 1;
      }
    }
    while (found >= 0 && lyricTimed[found].empty) found--;
    return found;
  }

  function highlightLyrics(time, force) {
    if (!lyrics.synced.length || !lyricsOpen) return;
    if (lyricsDirty) renderLyricsNow();

    var adjustedTime = time + lyrics.syncOffset;
    if (window.SongRequestKaraoke) {
      window.SongRequestKaraoke.update(lyricsEl, adjustedTime, now.playing);
    }

    var index = lyricIndexAt(adjustedTime);
    if (!force && index === lyrics.active) return;

    var previous = lyrics.active;
    lyrics.active = index;

    if (previous >= 0 && previous !== index && lyricLineEls[previous]) {
      lyricLineEls[previous].classList.remove('is-active');
    }

    var active = index >= 0 ? lyricLineEls[index] : null;
    if (!active) return;
    if (!active.classList.contains('is-active')) active.classList.add('is-active');

    // Measure against the scroll container instead of assuming it is the
    // offsetParent (it is not: the view is absolutely positioned).
    var lineBox = active.getBoundingClientRect();
    var lyricsBox = lyricsEl.getBoundingClientRect();
    var target = lyricsEl.scrollTop + (lineBox.top - lyricsBox.top) -
      lyricsBox.height / 2 + lineBox.height / 2;
    lyricsEl.scrollTo({ top: Math.max(0, target), behavior: 'smooth' });
  }

  // ── Queue ────────────────────────────────────────────────
  function onQueue(data) {
    queue = (data && data.queue) || [];
    playlistQueue = (data && data.playlistQueue) || [];
    if (data && data.isPlaying != null) {
      now.playing = !!data.isPlaying;
      setPlayingUi(now.playing);
    }
    if (data && data.repeatEnabled != null) setRepeatState(data.repeatEnabled);
    var signature = queueSignatureOf(queue) + '\u0003' + queueSignatureOf(playlistQueue);
    // Queue indexes shift when the host changes the queue; keeping stale
    // selected indexes would remove the wrong song.
    if (queueSelecting && signature !== queueSignature) queueSelection = {};
    queueDirty = signature !== queueSignature;
    // Do not build a queue DOM that nobody can see. Opening the sheet renders
    // the latest state in one pass; while open, identical updates are a no-op.
    if (!queueEl.hidden) renderQueue();
  }

  function queueSignatureOf(items) {
    var parts = [];
    for (var i = 0; i < items.length; i++) {
      var item = items[i];
      parts.push((item.songPath || '') + '\u0001' + (item.title || '') + '\u0001' +
        (item.artist || '') + '\u0001' + (item.estimatedStart || '') + '\u0001' +
        (item.thumbnail ? '1' : '0'));
    }
    return parts.join('\u0002');
  }

  function renderQueue(force) {
    var signature = queueSignatureOf(queue) + '\u0003' + queueSignatureOf(playlistQueue);
    if (!force && signature === queueSignature && !queueDirty) return;
    queueSignature = signature;
    queueDirty = false;

    var html = '';

    if (queue.length) {
      html += queue.map(function (item, index) {
        return queueRow(item, index, index === 0, 'queue');
      }).join('');
    }

    if (playlistQueue.length) {
      html += '<div class="mm-row" style="margin:12px 0 2px"><h3>Playlist</h3></div>';
      html += playlistQueue.map(function (item, index) {
        return queueRow(item, index, false, 'playlist');
      }).join('');
    }

    if (!html) {
      queueListEl.innerHTML = '<div class="mm-empty"><i class="fas fa-list"></i><span>Queue is empty</span></div>';
      return;
    }

    queueListEl.innerHTML = html;

    // Stagger only the first screenful; animating hundreds of rows would undo
    // the point of deferring the render.
    var rows = queueListEl.querySelectorAll('.mm-queue-row');
    for (var r = 0; r < rows.length && r < 14; r++) {
      rows[r].classList.add('is-enter');
      rows[r].style.animationDelay = (r * 16) + 'ms';
    }
    queueListEl.classList.toggle('is-selecting', queueSelecting);
    applyQueueSelection();
  }

  function queueRow(item, index, current, moveSource) {
    // Compact row: no per-row move/remove buttons. Actions live behind a
    // long-press sheet, and multi-select exposes batch removal, so more of the
    // queue fits on screen without losing any action.
    return '<div class="mm-queue-row' + (current ? ' is-current' : '') + '" data-song-path="' + esc(item.songPath || '') + '" data-queue-source="' + moveSource + '" data-queue-index="' + index + '">' +
      '<span class="mm-select-check" aria-hidden="true"><i class="fas fa-check"></i></span>' +
      (current ? '<div class="mm-now-bar"></div>' : '') +
      '<div class="mm-thumb">' + (item.thumbnail ? '<img src="' + esc(item.thumbnail) + '" alt="" loading="lazy" decoding="async">' : '<i class="fas fa-music"></i>') + '</div>' +
      '<div class="mm-queue-text">' +
        '<div class="mm-queue-title">' + esc(item.title || 'Unknown') + '</div>' +
        '<div class="mm-queue-sub">' + esc(item.artist || item.estimatedStart || '—') + '</div>' +
      '</div>' +
      '</div>';
  }

  // ── Queue multi-select ───────────────────────────────────
  function queueSelectionKey(source, index) { return source + ':' + index; }

  function setQueueSelecting(on) {
    queueSelecting = !!on;
    queueSelection = {};

    var button = $('mm-queue-select');
    if (button) {
      button.classList.toggle('is-active', queueSelecting);
      button.setAttribute('aria-pressed', queueSelecting ? 'true' : 'false');
      button.setAttribute('aria-label', queueSelecting ? 'Finish selecting queue items' : 'Select queue items');
    }

    var bar = $('mm-queue-multi');
    if (bar) {
      bar.hidden = !queueSelecting;
      if (queueSelecting) {
        bar.classList.remove('is-enter');
        void bar.offsetWidth;
        bar.classList.add('is-enter');
      }
    }
    queueListEl.classList.toggle('is-selecting', queueSelecting);
    applyQueueSelection();
  }

  function applyQueueSelection() {
    var count = 0;
    var rows = queueListEl.querySelectorAll('.mm-queue-row[data-queue-index]');
    for (var i = 0; i < rows.length; i++) {
      var source = rows[i].dataset.queueSource;
      var index = parseInt(rows[i].dataset.queueIndex, 10);
      var selected = queueSelecting && !!queueSelection[queueSelectionKey(source, index)];
      rows[i].classList.toggle('is-selected', selected);
      if (selected) count++;
    }

    var countEl = $('mm-queue-multi-count');
    if (countEl) countEl.textContent = count + ' selected';
    var remove = document.querySelector('#mm-queue-multi [data-queue-multi="remove"]');
    if (remove) remove.disabled = count === 0;
  }

  function toggleQueueSelection(source, index) {
    var key = queueSelectionKey(source, index);
    if (queueSelection[key]) delete queueSelection[key];
    else queueSelection[key] = true;
    applyQueueSelection();
  }

  function clearQueueSelection() {
    queueSelection = {};
    applyQueueSelection();
  }

  function removeSelectedQueueItems() {
    var queueIndexes = [];
    var playlistIndexes = [];
    Object.keys(queueSelection).forEach(function (key) {
      var split = key.split(':');
      var index = parseInt(split[1], 10);
      if (!isFinite(index)) return;
      if (split[0] === 'playlist') playlistIndexes.push(index);
      else queueIndexes.push(index);
    });

    queueIndexes.sort(function (a, b) { return b - a; });
    playlistIndexes.sort(function (a, b) { return b - a; });
    var total = queueIndexes.length + playlistIndexes.length;
    if (!total) return;

    queueIndexes.forEach(function (index) { send('removeQueueItem', { index: index }); });
    playlistIndexes.forEach(function (index) { send('removePlaylistQueueItem', { index: index }); });

    toast('Removed ' + total + (total === 1 ? ' item' : ' items') + ' from the queue', 'success');
    setQueueSelecting(false);
  }

  function openQueueItemActions(source, index) {
    var list = source === 'playlist' ? playlistQueue : queue;
    var item = list[index];
    if (!item) return;
    var canUp = source === 'playlist' ? index > 0 : index > 1;
    var canDown = index < list.length - 1;

    openSheet(
      '<h3>' + esc(item.title || 'Queue item') + '</h3>' +
      '<button class="mm-sheet-action" data-queue-action="up"' + (canUp ? '' : ' disabled') + '><i class="fas fa-chevron-up"></i> Move up</button>' +
      '<button class="mm-sheet-action" data-queue-action="down"' + (canDown ? '' : ' disabled') + '><i class="fas fa-chevron-down"></i> Move down</button>' +
      '<button class="mm-sheet-action is-danger" data-queue-action="remove"><i class="fas fa-trash-alt"></i> Remove from queue</button>'
    );
    sheetBody.dataset.queueSource = source;
    sheetBody.dataset.queueIndex = String(index);
  }

  function onQueueItemAction(button) {
    var source = sheetBody.dataset.queueSource || 'queue';
    var index = parseInt(sheetBody.dataset.queueIndex, 10);
    if (!isFinite(index)) return;
    var action = button.dataset.queueAction;
    var list = source === 'playlist' ? playlistQueue : queue;

    if (action === 'remove') {
      send(source === 'playlist' ? 'removePlaylistQueueItem' : 'removeQueueItem', { index: index });
      closeSheet();
      return;
    }

    var target = action === 'up' ? index - 1 : index + 1;
    var minIndex = source === 'playlist' ? 0 : 1;
    if (target < minIndex || target >= list.length) { closeSheet(); return; }

    send(source === 'playlist' ? 'movePlaylistQueueItem' : 'moveQueueItem', { fromIndex: index, toIndex: target });
    closeSheet();
  }

  // Artwork arrives after the queue when the background cache was cold; patch the
  // existing rows instead of re-rendering a potentially huge queue.
  function applyQueueThumbnails(items) {
    if (!items || !items.length) return;

    var byPath = {};
    for (var i = 0; i < items.length; i++) {
      var item = items[i];
      if (item && item.songPath && item.thumbnail) byPath[item.songPath] = item.thumbnail;
    }
    if (!Object.keys(byPath).length) return;

    [queue, playlistQueue].forEach(function (list) {
      list.forEach(function (entry) {
        var thumb = byPath[entry.songPath];
        if (thumb) entry.thumbnail = thumb;
      });
    });

    var rows = queueListEl.querySelectorAll('.mm-queue-row[data-song-path]');
    for (var r = 0; r < rows.length; r++) {
      var thumb = byPath[rows[r].getAttribute('data-song-path')];
      if (!thumb) continue;
      var host = rows[r].querySelector('.mm-thumb');
      if (host) host.innerHTML = '<img src="' + esc(thumb) + '" alt="">';
    }
  }

  // ── Colours from the artwork ─────────────────────────────
  function extractColors(src) {
    return new Promise(function (resolve) {
      var image = new Image();
      image.onload = function () {
        try {
          var size = 40;
          var canvas = document.createElement('canvas');
          canvas.width = size;
          canvas.height = size;
          var context = canvas.getContext('2d');
          context.drawImage(image, 0, 0, size, size);
          var data = context.getImageData(0, 0, size, size).data;

          var buckets = {};
          for (var i = 0; i < data.length; i += 4) {
            var r = data[i], g = data[i + 1], b = data[i + 2], a = data[i + 3];
            if (a < 128) continue;

            var lum = 0.299 * r + 0.587 * g + 0.114 * b;
            if (lum < 20 || lum > 240) continue;

            var saturation = Math.max(r, g, b) - Math.min(r, g, b);
            if (saturation < 20) continue;

            var key = Math.round(r / 48) + ',' + Math.round(g / 48) + ',' + Math.round(b / 48);
            if (!buckets[key]) buckets[key] = { r: 0, g: 0, b: 0, n: 0 };
            buckets[key].r += r;
            buckets[key].g += g;
            buckets[key].b += b;
            buckets[key].n++;
          }

          var sorted = Object.keys(buckets).map(function (key) { return buckets[key]; })
            .filter(function (bucket) { return bucket.n >= 3; })
            .sort(function (a, b) { return b.n - a.n; });

          if (!sorted.length) { resolve(null); return; }

          var first = sorted[0];
          var c1 = {
            r: Math.round(first.r / first.n),
            g: Math.round(first.g / first.n),
            b: Math.round(first.b / first.n)
          };

          var c2 = null;
          for (var j = 1; j < sorted.length; j++) {
            var other = sorted[j];
            var r2 = Math.round(other.r / other.n);
            var g2 = Math.round(other.g / other.n);
            var b2 = Math.round(other.b / other.n);
            if (Math.abs(r2 - c1.r) + Math.abs(g2 - c1.g) + Math.abs(b2 - c1.b) > 60) {
              c2 = { r: r2, g: g2, b: b2 };
              break;
            }
          }

          if (!c2) c2 = { r: Math.max(0, c1.r - 40), g: Math.max(0, c1.g - 40), b: Math.max(0, c1.b - 40) };
          resolve({ primary: c1, secondary: c2 });
        } catch (error) {
          resolve(null);
        }
      };
      image.onerror = function () { resolve(null); };
      image.src = src;
    });
  }

  function darken(color, factor) {
    return 'rgb(' + Math.round(color.r * factor) + ',' +
      Math.round(color.g * factor) + ',' + Math.round(color.b * factor) + ')';
  }

  function hex(color) {
    function channel(value) {
      var text = Math.max(0, Math.min(255, Math.round(value))).toString(16);
      return text.length === 1 ? '0' + text : text;
    }
    return '#' + channel(color.r) + channel(color.g) + channel(color.b);
  }

  function applyMusicColors(colors) {
    if (!colors) {
      playerView.style.setProperty('--mm-a1', '#1e1e1e');
      playerView.style.setProperty('--mm-a2', '#1e1e1e');
      playerView.style.setProperty('--mm-a3', '#1a1a1a');
      if (window.SongRequestTheme) {
        document.documentElement.style.removeProperty('--mm-primary');
        document.documentElement.style.removeProperty('--mm-primary-soft');
        document.documentElement.style.removeProperty('--mm-accent-soft');
        document.documentElement.style.removeProperty('--mm-surface');
        document.documentElement.style.removeProperty('--mm-surface-2');
      }
      send('setNavColors', {});
      return;
    }

    var palettePrimary = hex(colors.primary);
    var paletteSecondary = hex(colors.secondary);
    if (window.SongRequestTheme) {
      window.SongRequestTheme.apply({ primary: palettePrimary, secondary: paletteSecondary }, 'mm', '#1d1b20', '#26232c');
    }

    var signature = palettePrimary + paletteSecondary;
    if (signature === lastColors) return;
    lastColors = signature;

    playerView.style.setProperty('--mm-a1', darken(colors.primary, 0.40));
    playerView.style.setProperty('--mm-a2', darken(colors.secondary, 0.36));
    playerView.style.setProperty('--mm-a3', darken({
      r: (colors.primary.r + colors.secondary.r) / 2,
      g: (colors.primary.g + colors.secondary.g) / 2,
      b: (colors.primary.b + colors.secondary.b) / 2
    }, 0.24));

    // The native Material navigation bar animates to these two colours.
    send('setNavColors', {
      primary: hex(colors.primary),
      secondary: hex(colors.secondary)
    });
  }

  // ── Host events ──────────────────────────────────────────
  function onEvent(name, data) {
    switch (name) {
      case 'mobileTab':
        setTab(data && data.tab);
        break;

      case 'refresh':
        // Page became visible again after Android paused its JavaScript.
        send('musicPlayerReady');
        if (currentTab === 'home') loadHomeFeed(false);
        if (currentTab === 'library') loadLibrary(false);
        if (currentTab === 'playlist') reloadPlaylist();
        break;

      case 'nowPlayingUpdate':
        onNowPlaying(data);
        break;

      case 'queueUpdate':
        onQueue(data);
        break;

      case 'queueThumbnails':
        applyQueueThumbnails(data && data.items);
        break;

      case 'aiModelProgress':
        onMobileAiModelProgress(data);
        break;

      case 'aiModelStatus':
        applyMobileAiModelStatus(data);
        break;

      case 'aiVocalModeChanged':
        applyMobileAiBadge(data && data.active === true, data && data.modelName);
        break;

      case 'lyricsUpdate':
        onLyrics(data);
        break;

      case 'musicShareStatus':
        onShareStatus(data);
        break;

      case 'libraryChanged':
        feed = null;
        feedAt = 0;
        recentAt = 0;
        lib.page = 0;
        if (currentTab === 'home') loadHomeFeed(true);
        if (currentTab === 'library') loadLibrary(true);
        if (currentTab === 'playlist') reloadPlaylist();
        break;

      case 'libraryScanProgress':
        updateImportProgress(data);
        break;

      case 'libraryScanCompleted':
        finishImport(data);
        break;

      case 'libraryAnalysisProgress':
        updateLibraryAnalysisUi(data);
        break;

      case 'mobileThemeColors':
        if (window.SongRequestTheme) window.SongRequestTheme.apply(data, 'mm', '#1d1b20', '#26232c');
        break;

      case 'carMode':
        document.documentElement.setAttribute('data-car', data && data.enabled ? '1' : '0');
        break;
    }
  }

  function onNowPlaying(data) {
    var newSong = false;

    if (data.title != null) {
      if (data.title !== now.title || (data.artist || '') !== now.artist) newSong = true;
      now.title = data.title;
      now.artist = data.artist || '';
      titleEl.textContent = now.title || 'Nothing playing';
      artistEl.textContent = now.artist || '—';
    }

    if (newSong) {
      // Reset the previous song *before* applying the new artwork. The host sends
      // the title and thumbnail in the same event; resetting afterwards wiped the
      // freshly loaded image, which only reappeared after a later state catch-up
      // (tab switch / opening the player tab).
      now.art = '';
      artEl.removeAttribute('src');
      artEl.hidden = true;
      $('mm-art-placeholder').hidden = false;
      miniArt.removeAttribute('src');
      miniArt.hidden = true;
      lastColors = '';
      if (!data.thumbnail) applyMusicColors(null);

      now.quality = null;
      now.lyricsSource = '';
      refreshOptionsMeta();

      lyrics.synced = [];
      lyrics.plain = [];
      lyrics.active = -1;
      lyrics.syncOffset = 0;
      lyricData = null;
      lyricLineEls = [];
      lyricTimed = [];
      lyricsDirty = true;
      updateLyricsSyncUi();
      if (lyricsOpen) {
        lyricsEl.innerHTML = '<div class="mm-lyrics-empty"><i class="fas fa-circle-notch fa-spin"></i><span>Loading lyrics…</span></div>';
      }

      // A fast, non-layout entrance for the new artwork/meta rather than a
      // hard swap that makes the whole player flash.
      playerView.classList.remove('is-song-change');
      void playerView.offsetWidth;
      playerView.classList.add('is-song-change');
    }

    if (data.fileType || data.bitrateKbps || data.songPath || data.currentSongPath) {
      setQuality(data.fileType, data.bitrateKbps, data.currentSongPath || data.songPath);
    }

    if (data.thumbnail) setArt(data.thumbnail);
    updateMini();

    if (data.totalTime != null) {
      now.total = data.totalTime;
    } else if (data.duration) {
      now.total = parseDuration(data.duration);
    }

    if (data.currentTime != null) {
      now.time = data.currentTime;
      if (!seekDragging) updateSeekUi();
      highlightLyrics(now.time);
    }

    if (data.isPlaying != null) {
      now.playing = !!data.isPlaying;
      setPlayingUi(now.playing);
    }

    if (newSong) updatePlayer();
  }

  // ── Wiring ───────────────────────────────────────────────
  function bindTapList(container, handler, selectable) {
    container.addEventListener('click', function (event) {
      var button = event.target.closest('button[data-song-id]');
      if (button) {
        if (selectable && libSelecting) toggleSongSelection(button.dataset.songId);
        else handler('song', button.dataset.songId);
        return;
      }

      var playlist = event.target.closest('button[data-playlist-id]');
      if (playlist) handler('playlist', playlist.dataset.playlistId);
    });

    // Long-press (Android fires contextmenu) opens the song actions. While
    // selecting, it is a faster way to toggle a row instead.
    container.addEventListener('contextmenu', function (event) {
      var button = event.target.closest('button[data-song-id]');
      if (!button) return;
      event.preventDefault();
      if (selectable && libSelecting) {
        toggleSongSelection(button.dataset.songId);
        return;
      }
      var row = button.querySelector('.mm-song-title, .mm-card-title');
      openSongActions(button.dataset.songId, row ? row.textContent : '');
    });
  }

  function playSong(id) { if (id) send('playLibrarySong', { songId: id }); }
  function playPlaylist(id) { if (id) send('playPlaylist', { playlistId: id }); }

  bindTapList($('mm-home-scroll'), function (kind, id) {
    if (kind === 'song') playSong(id); else openPlaylist(id);
  });
  bindTapList($('mm-lib-songs'), function (kind, id) { if (kind === 'song') playSong(id); }, true);
  bindTapList($('mm-lib-recent'), function (kind, id) { if (kind === 'song') playSong(id); }, true);
  bindTapList($('mm-lib-playlists'), function (kind, id) { if (kind === 'playlist') openPlaylist(id); });
  bindTapList($('mm-playlist-list'), function (kind, id) { if (kind === 'song') playSong(id); });

  // Search: Home shows library matches instead of the feed.
  var homeSearch = $('mm-home-search');
  var homeClear = $('mm-home-clear');
  var homeResults = $('mm-home-results');
  var homeYoutube = $('mm-home-youtube');
  var searchTimer = null;
  var youtubeSearchMode = false;

  function runHomeSearch() {
    var query = homeSearch.value.trim();
    homeClear.hidden = !query;

    if (!query) {
      homeResults.hidden = true;
      homeResults.innerHTML = '';
      $('mm-home-feed').hidden = false;
      return;
    }

    $('mm-home-feed').hidden = true;
    homeResults.hidden = false;

    clearTimeout(searchTimer);
    searchTimer = setTimeout(function () {
      if (homeSearch.value.trim() !== query) return;
      lastQuery = query;

      if (youtubeSearchMode) {
        searchYoutubeFor(query);
        return;
      }

      request('searchLibrary', { query: query, maxResults: 80 }).then(function (data) {
        if (homeSearch.value.trim() !== query) return;
        var results = (data && data.results) || [];

        homeResults.innerHTML = (results.length
          ? results.map(songRow).join('')
          : '<div class="mm-empty"><i class="fas fa-search"></i><span>Nothing found in your library</span></div>') +
          '<div class="mm-more"><button class="mm-btn" id="mm-search-youtube">' +
          '<i class="fab fa-youtube"></i> Search YouTube for “' + esc(query) + '”</button></div>';
      }).catch(function () { });
    }, 250);
  }

  homeSearch.addEventListener('input', runHomeSearch);
  homeYoutube.addEventListener('click', function () {
    youtubeSearchMode = !youtubeSearchMode;
    homeYoutube.classList.toggle('is-active', youtubeSearchMode);
    homeYoutube.setAttribute('aria-pressed', youtubeSearchMode ? 'true' : 'false');
    homeSearch.placeholder = youtubeSearchMode ? 'Search YouTube' : 'Search your library';
    runHomeSearch();
    if (youtubeSearchMode) homeSearch.focus();
  });
  homeClear.addEventListener('click', function () {
    homeSearch.value = '';
    runHomeSearch();
    homeSearch.focus();
  });
  bindTapList(homeResults, function (kind, id) { if (kind === 'song') playSong(id); });

  // Library
  document.querySelectorAll('.mm-chip').forEach(function (chip) {
    chip.addEventListener('click', function () { setLibraryMode(chip.dataset.lib); });
  });

  var libSearch = $('mm-lib-search');
  var libClear = $('mm-lib-clear');
  var libTimer = null;
  libSearch.addEventListener('input', function () {
    // Cars show the search field on every library destination; start typing and
    // the list switches to song results without losing the text.
    ensureSongsMode();
    if (libClear) libClear.hidden = !libSearch.value;
    clearTimeout(libTimer);
    libTimer = setTimeout(function () {
      lib.query = libSearch.value.trim();
      lib.page = 0;
      loadLibrary(true);
    }, 250);
  });
  if (libClear) libClear.addEventListener('click', function () {
    libSearch.value = '';
    libClear.hidden = true;
    lib.query = '';
    lib.page = 0;
    loadLibrary(true);
    libSearch.focus();
  });

  $('mm-library-sort').addEventListener('click', cycleLibrarySort);
  $('mm-library-select').addEventListener('click', function () {
    setLibrarySelecting(!libSelecting);
  });
  $('mm-lib-multi-cancel').addEventListener('click', function () { setLibrarySelecting(false); });
  document.querySelectorAll('#mm-lib-multi [data-lib-multi]').forEach(function (button) {
    button.addEventListener('click', function () { runLibraryMultiAction(button.dataset.libMulti); });
  });

  $('mm-home-refresh').addEventListener('click', function () {
    feedAt = 0;
    loadHomeFeed(true);
    toast('Home refreshed', 'info');
  });

  $('mm-lib-more').addEventListener('click', function () { loadLibrary(false, true); });

  // Player controls
  lyricsToggle.addEventListener('click', function () { toggleLyrics(); });
  if (lyricsSettingsToggle) {
    lyricsSettingsToggle.addEventListener('click', function () { toggleLyricsSettings(); });
  }
  updateLyricsSyncUi();
  updateLyricsFontUi();

  // Karaoke mode can be toggled from the settings sheet or the desktop. The
  // lyrics payload already carries syllable timings, so rebuild the line spans
  // locally instead of waiting for another host round-trip.
  document.addEventListener('karaokechange', function () {
    if (karaokeSettingFromPayload || !lyricData) return;
    lyricsDirty = true;
    if (lyricsOpen) renderLyricsNow();
  });

  if (queueToggle) {
    queueToggle.addEventListener('click', function () {
      if (queueEl.hidden) openQueue(); else closeQueue(true);
    });
  }
  // Toggle immediately so the button answers the tap even while the engine is
  // preparing the next track; the 4 Hz state tick reconciles any failure.
  function togglePlay() {
    now.playing = !now.playing;
    setPlayingUi(now.playing);
    send('playPause');
  }

  playBtn.addEventListener('click', togglePlay);
  $('mm-next').addEventListener('click', function () { send('skipNext'); });
  $('mm-prev').addEventListener('click', function () { send('skipPrevious'); });

  seekEl.addEventListener('input', function () {
    seekDragging = true;
    if (now.total > 0) curEl.textContent = formatTime(seekEl.value / 1000 * now.total);
  });

  seekEl.addEventListener('change', function () {
    var total = now.total || 0;
    // The host expects a normalized position (0..1). Sending absolute seconds
    // clamped every seek to the end of the track, which looked like the player
    // was skipping the song as soon as the thumb was released.
    var position = Math.max(0, Math.min(1, seekEl.value / 1000));
    seekDragging = false;
    now.time = position * total;
    send('seek', { position: position });
    updateSeekUi();
  });

  // Mini player
  miniEl.addEventListener('click', function (event) {
    if (event.target.closest('button')) return;
    send('mobileOpenPlayer');
  });
  miniPlay.addEventListener('click', togglePlay);
  $('mm-mini-next').addEventListener('click', function () { send('skipNext'); });

  // Queue sidebar
  $('mm-queue-close').addEventListener('click', function () { closeQueue(true); });
  queueScrim.addEventListener('click', function () { closeQueue(true); });
  $('mm-queue-clear').addEventListener('click', function () { send('clearQueue'); });

  // Empty library: import music.
  $('mm-home-import').addEventListener('click', function () { send('importLibrary', { mode: 'files' }); });

  // ── Bottom sheet / player options ────────────────────────
  var sheet = $('mm-sheet');
  var sheetScrim = $('mm-sheet-scrim');
  var sheetBody = $('mm-sheet-body');
  var pendingConfirm = null;

  function openSheet(html, kind) {
    // Opening any other sheet leaves lyrics mode's settings menu.
    if (kind !== 'lyrics') {
      lyricsSheetOpen = false;
      setLyricsSettingsChipActive(false);
    }
    sheetBody.dataset.sheet = kind || '';
    delete sheetBody.dataset.songId;
    delete sheetBody.dataset.songIds;
    delete sheetBody.dataset.queueSource;
    delete sheetBody.dataset.queueIndex;
    sheetBody.innerHTML = html;
    sheetScrim.hidden = false;
    sheet.hidden = false;
    requestAnimationFrame(function () {
      sheet.classList.add('is-open');
      sheetScrim.classList.add('is-open');
    });
  }

  // window.confirm() is unreliable inside Android's HybridWebView; destructive
  // actions use an in-app confirmation sheet instead.
  function openConfirmSheet(title, message, confirmLabel, callback) {
    pendingConfirm = callback;
    openSheet(
      '<h3>' + esc(title) + '</h3>' +
      '<div class="mm-confirm-text">' + esc(message) + '</div>' +
      '<div class="mm-confirm-actions">' +
        '<button id="mm-confirm-cancel" class="mm-btn">Cancel</button>' +
        '<button id="mm-confirm-ok" class="mm-btn mm-danger">' + esc(confirmLabel) + '</button>' +
      '</div>'
    );
  }

  function closeSheet() {
    if (lyricsSheetOpen || sheetBody.dataset.sheet === 'lyrics') {
      lyricsSheetOpen = false;
      setLyricsSettingsChipActive(false);
    }
    sheetBody.dataset.sheet = '';
    sheet.classList.remove('is-open');
    sheetScrim.classList.remove('is-open');
    setTimeout(function () {
      if (!sheet.classList.contains('is-open')) {
        sheet.hidden = true;
        sheetScrim.hidden = true;
      }
    }, 250);
  }

  sheetScrim.addEventListener('click', closeSheet);

  function openPlayerMenu() {
    request('getMusicAudioDevices').then(function (data) {
      var devices = (data && data.devices) || [];
      var selected = data && data.selected != null ? data.selected : -1;

      var deviceOptions = devices.map(function (device) {
        return '<option value="' + device.number + '"' + (device.number === selected ? ' selected' : '') + '>' +
          esc(device.name) + '</option>';
      }).join('');

      request('getSettings').then(function (cfg) {
        openSheet(
          '<h3>Track</h3>' +
          '<div id="mm-options-meta" class="mm-track-meta">' + optionsMetaHtml() + '</div>' +
          '<h3>Playback</h3>' +
          '<div class="mm-sheet-row"><div class="mm-sheet-label">Volume</div>' +
            '<input id="mm-menu-volume" type="range" min="0" max="100" value="' + Math.round(playback.volume * 100) + '"></div>' +
          '<div class="mm-sheet-row"><div class="mm-sheet-label">Crossfade</div>' +
            '<input id="mm-menu-crossfade" type="range" min="0" max="12" step="0.5" value="' + playback.crossfade + '"></div>' +
          '<div class="mm-sheet-row"><div class="mm-sheet-label">Normalize volume</div>' +
            '<input id="mm-menu-normalize" type="checkbox"' + (cfg && cfg.normalizeVolume ? ' checked' : '') + '></div>' +
          '<div class="mm-sheet-row"><div class="mm-sheet-label">Auto-analyze songs<div class="mm-sheet-sub">BPM, chapters &amp; drops on the progress bar</div></div>' +
            '<input id="mm-menu-auto-analyze" type="checkbox"' + ((!cfg || cfg.autoAnalyzeSongs !== false) ? ' checked' : '') + '></div>' +
          '<div class="mm-sheet-row"><div class="mm-sheet-label">Karaoke mode<div class="mm-sheet-sub">Syllable-by-syllable lyrics and pause bars</div></div>' +
            '<input id="mm-menu-karaoke" type="checkbox"' + (cfg && cfg.karaokeMode ? ' checked' : '') + '></div>' +
          '<div class="mm-sheet-row"><div class="mm-sheet-label">Play karaoke versions<div class="mm-sheet-sub">Generate and play instrumental tracks</div></div>' +
            '<input id="mm-menu-play-karaoke" type="checkbox"' + (cfg && cfg.playKaraokeVersions ? ' checked' : '') + '></div>' +
          '<h3>Audio output</h3>' +
          '<div class="mm-sheet-row"><select id="mm-menu-device">' + deviceOptions + '</select></div>' +
          '<h3>Sound</h3>' +
          '<button class="mm-sheet-action" data-menu="equalizer"><i class="fas fa-sliders"></i> Equalizer</button>' +
          '<button class="mm-sheet-action" data-menu="sleep"><i class="fas fa-moon"></i> Sleep timer</button>' +
          '<button class="mm-sheet-action" data-menu="analysis"><i class="fas fa-chart-line"></i> Analyse current track</button>' +
          '<h3>Tools</h3>' +
          '<button class="mm-sheet-action" data-menu="presentation"><i class="fas fa-tv"></i> Presentation</button>' +
          '<button class="mm-sheet-action" data-menu="visualization"><i class="fas fa-wave-square"></i> Visualization</button>' +
          '<button class="mm-sheet-action" data-menu="share"><i class="fas fa-tower-broadcast"></i> Music Share</button>' +
          '<button class="mm-sheet-action" data-menu="announcements"><i class="fas fa-microphone"></i> Announcements</button>'
        );
        wirePlayerMenu();
      }).catch(function () { });
    }).catch(function () { });
  }

  function wirePlayerMenu() {
    var volume = $('mm-menu-volume');
    if (volume) volume.addEventListener('input', function () {
      playback.volume = parseFloat(this.value) / 100;
      send('setVolume', { value: playback.volume });
    });

    var crossfade = $('mm-menu-crossfade');
    if (crossfade) crossfade.addEventListener('input', function () {
      playback.crossfade = parseFloat(this.value);
      send('setCrossfade', { value: playback.crossfade });
    });

    var normalize = $('mm-menu-normalize');
    if (normalize) normalize.addEventListener('change', function () {
      send('saveSettings', { settings: { normalizeVolume: this.checked } });
    });

    var autoAnalyze = $('mm-menu-auto-analyze');
    if (autoAnalyze) autoAnalyze.addEventListener('change', function () {
      send('saveSettings', { settings: { autoAnalyzeSongs: this.checked } });
      if (window.SongRequestAdvancedAudio) window.SongRequestAdvancedAudio.refresh();
    });

    var karaoke = $('mm-menu-karaoke');
    if (karaoke) karaoke.addEventListener('change', function () {
      send('saveSettings', { settings: { karaokeMode: this.checked } });
      if (window.SongRequestKaraoke) window.SongRequestKaraoke.setEnabled(this.checked);
    });

    var playKaraoke = $('mm-menu-play-karaoke');
    if (playKaraoke) playKaraoke.addEventListener('change', function () {
      send('saveSettings', { settings: { playKaraokeVersions: this.checked } });
      toast(this.checked ? 'Karaoke playback enabled' : 'Original songs enabled', 'success');
    });

    var device = $('mm-menu-device');
    if (device) device.addEventListener('change', function () {
      // Device 0 is a real output; `|| -1` used to turn it into the default.
      var number = parseInt(this.value, 10);
      send('setMusicOutputDevice', { deviceNumber: isNaN(number) ? -1 : number });
    });
  }

  function onSheetAction(button) {
    switch (button.dataset.menu) {
      case 'presentation':
        send('showPresentation');
        closeSheet();
        break;
      case 'visualization':
        send('showVisualization');
        closeSheet();
        break;
      case 'share':
        closeSheet();
        setTab('share');
        break;
      case 'announcements':
        send('announcementOpen');
        closeSheet();
        break;
      case 'equalizer':
        closeSheet();
        if (window.SongRequestAdvancedAudio) window.SongRequestAdvancedAudio.openEqualizer();
        break;
      case 'sleep':
        closeSheet();
        if (window.SongRequestAdvancedAudio) window.SongRequestAdvancedAudio.openSleepTimer();
        break;
      case 'analysis':
        closeSheet();
        if (window.SongRequestAdvancedAudio) window.SongRequestAdvancedAudio.openAnalysis({});
        break;
    }
  }

  // ── Mobile settings ─────────────────────────────────────
  var MODES = [
    ['SongRequests', 'Song Requests'],
    ['MusicPlayer', 'Music Player'],
    ['MusicShare', 'Music Share'],
    ['Soundboard', 'Soundboard']
  ];

  function option(value, label, selected) {
    return '<option value="' + value + '"' + (value === selected ? ' selected' : '') + '>' + label + '</option>';
  }

  var mobileAiStatus = null;
  var mobileAiDisclaimerAccepted = false;

  function openSettingsSheet() {
    request('getSettings').then(function (cfg) {
      cfg = cfg || {};
      mobileAiDisclaimerAccepted = cfg.aiDisclaimerAccepted === true;

      var modeOptions = MODES.map(function (mode) {
        return option(mode[0], mode[1], cfg.operatingMode || 'SongRequests');
      }).join('');

      openSheet(
        '<h3>Operating mode</h3>' +
        '<div class="mm-sheet-row"><select id="mm-set-mode">' + modeOptions + '</select></div>' +
        '<h3>Launch</h3>' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-new-launch"' + (cfg.newLaunchExperience === true ? ' checked' : '') + '> New animated launch experience</label>' +
        '<div class="mm-sheet-note">Starts in your previous mode, skips the mode picker and tries YouTube sign-in quietly in the background.</div>' +
        '<h3>Playback</h3>' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-auto-analyze"' + (cfg.autoAnalyzeSongs !== false ? ' checked' : '') + '> Automatically analyze playing songs</label>' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-karaoke"' + (cfg.karaokeMode === true ? ' checked' : '') + '> Karaoke mode (syllable-by-syllable lyrics and pause bars)</label>' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-play-karaoke"' + (cfg.playKaraokeVersions === true ? ' checked' : '') + '> Play karaoke (instrumental) versions of songs</label>' +
        '<div class="mm-ai-settings" id="mm-ai-settings">' +
        '<h3>Enhanced vocal removal <span class="ai-badge"><i class="fas fa-wand-magic-sparkles"></i> AI</span></h3>' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-ai-vocal"' + (cfg.enhancedVocalRemoval === true ? ' checked' : '') + '> Use a local AI model for karaoke vocal separation</label>' +
        '<div id="mm-ai-engine" class="mm-sheet-note">Checking models…</div>' +
        '<select id="mm-set-ai-model" class="mm-sheet-input"></select>' +
        '<div id="mm-ai-model-info" class="mm-sheet-note"></div>' +
        '<div class="mm-sheet-note">AI separation uses more memory and time than the built-in separator; the fast model is best on older devices.</div>' +
        '<div style="display:flex;gap:8px;flex-wrap:wrap;margin-top:8px">' +
          '<button id="mm-ai-download" class="mm-btn"><i class="fas fa-download"></i> Download model</button>' +
          '<button id="mm-ai-delete" class="mm-btn mm-danger">Delete model</button>' +
          '<button id="mm-ai-disclaimer-open" class="mm-btn mm-quiet">Read disclaimer</button>' +
        '</div>' +
        '<div id="mm-ai-disclaimer" class="mm-ai-disclaimer" hidden>' +
          '<p><strong>AI vocal removal is experimental, and results vary by song, mix and model.</strong></p>' +
          '<ul>' +
            '<li>Vocals may still be audible, or instruments may be removed by mistake.</li>' +
            '<li>Separation can introduce artifacts, hollow instruments or distorted vocals.</li>' +
            '<li>Always listen to the generated karaoke version before using it live.</li>' +
            '<li>Models are downloaded from third-party sources and run locally; no song audio is uploaded.</li>' +
            '<li>AI versions are versioned and may be regenerated when the model changes.</li>' +
            '<li>You are responsible for checking the result and for having the rights to use the source song.</li>' +
          '</ul>' +
          '<label><input type="checkbox" id="mm-ai-disclaimer-accept"> I have read this and understand that AI vocal removal can be inaccurate and that I must check the result before use.</label>' +
        '</div>' +
        '<div id="mm-ai-progress" class="mm-sheet-note"></div>' +
        '</div>' +
        '<div class="mm-sheet-note" style="margin-top:4px">Shows BPM, song chapters and drops on the player progress bar from the cached analysis.</div>' +
        '<h3>Library analysis</h3>' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-library-analyze-add"' + (cfg.autoAnalyzeLibraryOnAdd !== false ? ' checked' : '') + '> Analyze new library songs automatically</label>' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-library-analyze-version"' + (cfg.autoAnalyzeLibraryOnAlgorithmUpdate !== false ? ' checked' : '') + '> Re-analyze after algorithm updates</label>' +
        '<div id="mm-library-analysis-progress" class="mm-import-progress" hidden>' +
          '<div class="mm-import-head"><span id="mm-library-analysis-status">Idle</span><span id="mm-library-analysis-pct">0%</span></div>' +
          '<div class="mm-import-track"><div id="mm-library-analysis-fill"></div></div>' +
          '<div id="mm-library-analysis-current" class="mm-sheet-note" style="margin-top:6px"></div>' +
        '</div>' +
        '<div style="display:flex;gap:8px;flex-wrap:wrap;margin-top:10px">' +
          '<button id="mm-library-analysis-start" class="mm-btn"><i class="fas fa-play"></i> Analyze library</button>' +
          '<button id="mm-library-analysis-stop" class="mm-btn mm-danger" hidden><i class="fas fa-stop"></i> Stop</button>' +
        '</div>' +
        '<h3>Server</h3>' +
        '<input id="mm-set-address" class="mm-sheet-input" placeholder="http://127.0.0.1:5000" value="' + esc(cfg.address || '') + '">' +
        '<input id="mm-set-token" class="mm-sheet-input" type="password" placeholder="Bearer token" value="' + esc(cfg.bearerToken || '') + '" style="margin-top:8px">' +
        '<h3>YouTube</h3>' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-auto-yt"' + (cfg.autoGrabCookies ? ' checked' : '') + '> Automatically finish YouTube sign-in when the account page has loaded</label>' +
        '<h3>Library</h3>' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-auto-add"' + (cfg.libraryAutoAddDownloads ? ' checked' : '') + '> Add downloads to the library automatically</label>' +
        '<div style="display:flex;gap:8px;flex-wrap:wrap">' +
          '<button class="mm-btn" data-settings-action="import-files"><i class="fas fa-file-import"></i> Import files</button>' +
          '<button class="mm-btn" data-settings-action="import-folder"><i class="fas fa-folder-open"></i> Import folder</button>' +
          '<button class="mm-btn" data-settings-action="scan"><i class="fas fa-rotate"></i> Scan</button>' +
        '</div>' +
        '<div id="mm-import-progress" class="mm-import-progress" hidden>' +
          '<div class="mm-import-head"><span id="mm-import-label">Preparing…</span><span id="mm-import-pct">0%</span></div>' +
          '<div class="mm-import-track"><div id="mm-import-fill"></div></div>' +
        '</div>' +
        '<div id="mm-import-result" class="mm-import-result" hidden></div>' +
        '<h3>API &amp; MCP</h3>' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-api-enabled"' + (cfg.apiEnabled ? ' checked' : '') + '> Enable local HTTP/WebSocket API</label>' +
        '<input id="mm-set-api-bind" class="mm-sheet-input" placeholder="API bind address (127.0.0.1)" value="' + esc(cfg.apiBindAddress || '127.0.0.1') + '" style="margin-top:8px">' +
        '<input id="mm-set-api-port" class="mm-sheet-input" type="number" min="1" max="65535" placeholder="API port (8730)" value="' + esc(cfg.apiPort || 8730) + '" style="margin-top:8px">' +
        '<input id="mm-set-api-token" class="mm-sheet-input" type="password" placeholder="API token (optional)" value="' + esc(cfg.apiToken || '') + '" style="margin-top:8px">' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-mcp-enabled"' + (cfg.mcpEnabled ? ' checked' : '') + '> Enable local MCP server</label>' +
        '<input id="mm-set-mcp-bind" class="mm-sheet-input" placeholder="MCP bind address (127.0.0.1)" value="' + esc(cfg.mcpBindAddress || '127.0.0.1') + '" style="margin-top:8px">' +
        '<input id="mm-set-mcp-port" class="mm-sheet-input" type="number" min="1" max="65535" placeholder="MCP port (8731)" value="' + esc(cfg.mcpPort || 8731) + '" style="margin-top:8px">' +
        '<input id="mm-set-mcp-token" class="mm-sheet-input" type="password" placeholder="MCP token (optional)" value="' + esc(cfg.mcpToken || '') + '" style="margin-top:8px">' +
        '<div class="mm-sheet-note">Both servers are off by default and bind to loopback unless you change the address. See docs/API-AND-MCP.md.</div>' +
        '<button id="mm-settings-save" class="mm-btn mm-share-primary" style="width:100%;margin-top:16px">Save</button>'
      );
      wireSettingsSheet();
      refreshMobileAiModels();
    }).catch(function (error) { toast(error.message, 'error'); });
  }

  function updateLibraryAnalysisUi(data) {
    if (!data) return;
    var wrap = $('mm-library-analysis-progress');
    var fill = $('mm-library-analysis-fill');
    var status = $('mm-library-analysis-status');
    var pct = $('mm-library-analysis-pct');
    var current = $('mm-library-analysis-current');
    var start = $('mm-library-analysis-start');
    var stop = $('mm-library-analysis-stop');
    if (!wrap || !fill) return;

    var running = !!data.isRunning;
    var total = data.total || 0;
    var completed = data.completed || 0;
    var percent = total > 0 ? Math.round(completed / total * 100) : Math.round((data.progress || 0) * 100);
    if (running && data.currentSongProgress > 0 && total > 0) {
      percent = Math.min(99, Math.round((completed + data.currentSongProgress) / total * 100));
    }

    wrap.hidden = !(running || data.status === 'completed' || data.status === 'stopped');
    fill.style.width = Math.max(0, Math.min(100, percent)) + '%';
    if (pct) pct.textContent = percent + '%';
    if (status) {
      status.textContent = running
        ? (data.pausedForPlayback ? 'Paused while music plays' : 'Analyzing library…')
        : data.status === 'completed' ? 'Analysis complete'
        : data.status === 'stopped' ? 'Analysis stopped'
        : 'Idle';
    }
    if (current) {
      current.textContent = running && data.currentSong
        ? data.currentSong + ' · ' + completed + '/' + total
        : completed + '/' + total + ' · ' + (data.analyzed || 0) + ' analyzed, ' +
          (data.failed || 0) + ' failed, ' + (data.skipped || 0) + ' skipped';
    }
    if (start) start.hidden = running;
    if (stop) stop.hidden = !running;
  }

  function refreshMobileAiModels() {
    request('getAiVocalModels').then(function (status) {
      applyMobileAiModelStatus(status);
    }).catch(function () {
      var engine = $('mm-ai-engine');
      if (engine) engine.textContent = 'Could not read AI model status.';
    });
  }

  function updateMobileAiActive() {
    var section = $('mm-ai-settings');
    var toggle = $('mm-set-ai-vocal');
    if (section && toggle) section.classList.toggle('is-active', toggle.checked);
  }

  function applyMobileAiModelStatus(status) {
    if (!status || !status.models) return;
    mobileAiStatus = status;

    var toggle = $('mm-set-ai-vocal');
    var engine = $('mm-ai-engine');
    var select = $('mm-set-ai-model');
    var info = $('mm-ai-model-info');
    var download = $('mm-ai-download');
    var remove = $('mm-ai-delete');
    if (!select || !info) return;

    var supported = status.supported !== false;
    if (engine) {
      engine.textContent = supported
        ? 'Engine ready · ' + (status.backend || 'local AI') +
          (status.installedBytes ? ' · ' + (status.installedBytes / (1024 * 1024)).toFixed(1) + ' MB installed' : ' · no models installed')
        : 'Local AI vocal separation is not available on this platform. The built-in separator is used.';
      engine.style.color = supported ? '' : '#e08080';
    }
    if (toggle) toggle.disabled = !supported;
    if (download) download.disabled = !supported;

    select.innerHTML = status.models.map(function (m) {
      return '<option value="' + esc(m.id) + '">' + esc(m.name) + ' · ' + esc(m.sizeLabel) + '</option>';
    }).join('');
    var wanted = status.selectedModelId || (status.models[0] && status.models[0].id) || '';
    var chosen = status.models.find(function (m) { return m.id === wanted; }) || status.models[0];
    if (chosen) select.value = chosen.id;

    function updateInfo() {
      var model = status.models.find(function (m) { return m.id === select.value; }) || chosen;
      if (!model) { info.textContent = ''; return; }
      info.textContent = model.description + ' · ' + model.ratingLabel +
        ' · License: ' + model.license +
        (model.installed ? ' · Installed' : ' · Not installed');
      if (remove) remove.disabled = !supported || !model.installed;
    }
    select.onchange = updateInfo;
    updateInfo();

    if (download && !download.dataset.bound) {
      download.dataset.bound = '1';
      download.onclick = function () {
        var id = select.value;
        if (!id) return;
        var progress = $('mm-ai-progress');
        if (progress) progress.textContent = 'Starting download…';
        request('downloadAiVocalModel', { modelId: id }).then(function (r) {
          if (r && r.error && progress) progress.textContent = 'Error: ' + r.error;
        }).catch(function (e) {
          if (progress) progress.textContent = 'Error: ' + e.message;
        });
      };
    }

    if (remove && !remove.dataset.bound) {
      remove.dataset.bound = '1';
      remove.onclick = function () {
        var id = select.value;
        var current = mobileAiStatus || status;
        var model = current.models.find(function (m) { return m.id === id; });
        if (!id || !model || !model.installed) return;
        request('deleteAiVocalModel', { modelId: id }).then(function (r) {
          if (r && r.error) toast('Delete failed: ' + r.error, 'error');
          else toast('AI model deleted', 'success');
          refreshMobileAiModels();
        }).catch(function (e) { toast('Delete failed: ' + e.message, 'error'); });
      };
    }
    updateMobileAiActive();
  }

  function onMobileAiModelProgress(data) {
    if (!data) return;
    var progress = $('mm-ai-progress');
    if (progress) {
      var pct = data.percent ? ' ' + Math.round(data.percent) + '%' : '';
      progress.textContent = (data.message || data.stage || '') + pct;
    }
    if (data.stage === 'done' || data.stage === 'error') refreshMobileAiModels();
  }

  function wireSettingsSheet() {
    var mode = $('mm-set-mode');
    if (mode) mode.addEventListener('change', function () {
      send('setOperatingMode', { mode: this.value });
      closeSheet();
      toast('Operating mode: ' + this.options[this.selectedIndex].text, 'success');
    });

    var analysisStart = $('mm-library-analysis-start');
    if (analysisStart) analysisStart.addEventListener('click', function () {
      request('startLibraryAnalysis', { force: false }).then(function (result) {
        if (result && result.error) toast(result.error, 'error');
        else { updateLibraryAnalysisUi(result); toast('Library analysis started', 'success'); }
      }).catch(function (error) { toast(error.message, 'error'); });
    });

    var analysisStop = $('mm-library-analysis-stop');
    if (analysisStop) analysisStop.addEventListener('click', function () {
      request('stopLibraryAnalysis').then(function (result) {
        if (result && result.error) toast(result.error, 'error');
        else { updateLibraryAnalysisUi(result); toast('Stopping analysis…', 'info'); }
      }).catch(function (error) { toast(error.message, 'error'); });
    });

    request('getLibraryAnalysisStatus').then(function (status) {
      if (status && !status.error) updateLibraryAnalysisUi(status);
    }).catch(function () { });

    var aiToggle = $('mm-set-ai-vocal');
    var aiDisclaimer = $('mm-ai-disclaimer');
    var aiAccept = $('mm-ai-disclaimer-accept');
    var aiOpen = $('mm-ai-disclaimer-open');
    if (aiAccept) aiAccept.checked = mobileAiDisclaimerAccepted;
    if (aiToggle && aiToggle.checked && !mobileAiDisclaimerAccepted) {
      aiToggle.checked = false;
      if (aiDisclaimer) aiDisclaimer.hidden = false;
      toast('Accept the AI disclaimer to keep AI vocal removal enabled', 'info');
    }
    if (aiOpen) aiOpen.addEventListener('click', function () {
      if (aiDisclaimer) aiDisclaimer.hidden = false;
    });
    if (aiToggle) aiToggle.addEventListener('change', function () {
      if (this.checked && !mobileAiDisclaimerAccepted) {
        this.checked = false;
        if (aiDisclaimer) aiDisclaimer.hidden = false;
        toast('Read and accept the AI disclaimer first', 'info');
      }
      updateMobileAiActive();
    });
    if (aiAccept) aiAccept.addEventListener('change', function () {
      mobileAiDisclaimerAccepted = this.checked;
      if (this.checked) toast('AI disclaimer accepted', 'success');
    });
    updateMobileAiActive();

    var save = $('mm-settings-save');
    if (save) save.addEventListener('click', function () {
      var aiSaveToggle = $('mm-set-ai-vocal');
      if (aiSaveToggle && aiSaveToggle.checked && !mobileAiDisclaimerAccepted) {
        toast('Read and accept the AI disclaimer first', 'error');
        if (aiDisclaimer) aiDisclaimer.hidden = false;
        return;
      }
      var settings = {
        newLaunchExperience: !!($('mm-set-new-launch') && $('mm-set-new-launch').checked),
        address: $('mm-set-address').value.trim(),
        bearerToken: $('mm-set-token').value,
        autoGrabCookies: $('mm-set-auto-yt').checked,
        autoAnalyzeSongs: $('mm-set-auto-analyze').checked,
        karaokeMode: $('mm-set-karaoke').checked,
        playKaraokeVersions: $('mm-set-play-karaoke').checked,
        enhancedVocalRemoval: $('mm-set-ai-vocal').checked,
        aiVocalModelId: $('mm-set-ai-model').value || 'uvr-kara-2',
        aiDisclaimerAccepted: mobileAiDisclaimerAccepted,
        autoAnalyzeLibraryOnAdd: $('mm-set-library-analyze-add').checked,
        autoAnalyzeLibraryOnAlgorithmUpdate: $('mm-set-library-analyze-version').checked,
        libraryAutoAddDownloads: $('mm-set-auto-add').checked,
        apiEnabled: $('mm-set-api-enabled').checked,
        apiBindAddress: $('mm-set-api-bind').value.trim() || '127.0.0.1',
        apiPort: parseInt($('mm-set-api-port').value, 10) || 8730,
        apiToken: $('mm-set-api-token').value || '',
        mcpEnabled: $('mm-set-mcp-enabled').checked,
        mcpBindAddress: $('mm-set-mcp-bind').value.trim() || '127.0.0.1',
        mcpPort: parseInt($('mm-set-mcp-port').value, 10) || 8731,
        mcpToken: $('mm-set-mcp-token').value || ''
      };

      request('saveSettings', { settings: settings }).then(function (result) {
        if (result && result.error) { toast(result.error, 'error'); return; }
        toast('Settings saved', 'success');
        closeSheet();
      }).catch(function (error) { toast(error.message, 'error'); });
    });
  }

  function onSettingsAction(button) {
    switch (button.dataset.settingsAction) {
      case 'import-files':
        startImport('files');
        break;
      case 'import-folder':
        startImport('folder');
        break;
      case 'scan':
        startImport('scan');
        break;
    }
  }

  function showImportProgress(message) {
    var wrap = $('mm-import-progress');
    if (!wrap) return;
    wrap.hidden = false;
    $('mm-import-result').hidden = true;
    $('mm-import-label').textContent = message || 'Preparing…';
    $('mm-import-pct').textContent = '0%';
    $('mm-import-fill').style.width = '0%';
  }

  function updateImportProgress(data) {
    var wrap = $('mm-import-progress');
    if (!wrap) return;
    var pct = Math.round((data && data.progress || 0) * 100);
    wrap.hidden = false;
    $('mm-import-fill').style.width = pct + '%';
    $('mm-import-pct').textContent = pct + '%';
    $('mm-import-label').textContent = (data && data.message) || 'Scanning…';
  }

  function finishImport(data) {
    var wrap = $('mm-import-progress');
    if (!wrap) return;
    $('mm-import-fill').style.width = '100%';
    $('mm-import-pct').textContent = '100%';
    $('mm-import-label').textContent = 'Import complete';

    var result = $('mm-import-result');
    if (result && data) {
      var text = 'Found ' + (data.filesFound || 0) + ' files · +' + (data.songsAdded || 0) +
        ' added · ' + (data.songsUpdated || 0) + ' updated' +
        (data.errorCount ? ' · ' + data.errorCount + ' error(s)' : '');
      result.textContent = text;
      result.hidden = false;
    }

    setTimeout(function () {
      if (wrap) wrap.hidden = true;
      if (result) result.hidden = true;
    }, 6000);
  }

  function startImport(mode) {
    showImportProgress(mode === 'scan' ? 'Scanning library…' : 'Choose music…');
    var status = $('mm-import-status');
    if (status) { status.hidden = false; status.textContent = mode === 'scan' ? 'Scanning…' : 'Choose music to import…'; }

    var method = mode === 'scan' ? 'scanLibrary' : 'importLibrary';
    var params = mode === 'scan' ? null : { mode: mode };

    request(method, params, 900000).then(function (result) {
      if (mode === 'scan' || (result && result.success)) {
        finishImport(result || {});
      } else if (result && result.cancelled) {
        if (status) status.textContent = 'Import cancelled.';
        var wrap = $('mm-import-progress');
        if (wrap) wrap.hidden = true;
      } else if (result && result.error) {
        if (status) status.textContent = 'Import failed: ' + result.error;
        toast(result.error, 'error');
      }
    }).catch(function (error) {
      if (status) status.textContent = 'Import failed: ' + error.message;
      toast(error.message, 'error');
    });
  }

  // ── Song actions ─────────────────────────────────────────
  function openSongActions(songId, title) {
    openSheet(
      '<h3>' + esc(title || 'Song') + '</h3>' +
      '<button class="mm-sheet-action" data-song-action="play"><i class="fas fa-play"></i> Play now</button>' +
      '<button class="mm-sheet-action" data-song-action="next"><i class="fas fa-forward-step"></i> Play next</button>' +
      '<button class="mm-sheet-action" data-song-action="queue"><i class="fas fa-list"></i> Add to queue</button>' +
      '<button class="mm-sheet-action" data-song-action="analyze"><i class="fas fa-chart-line"></i> Analyse track</button>' +
      '<button class="mm-sheet-action" data-song-action="karaoke"><i class="fas fa-microphone-slash"></i> Create karaoke version</button>' +
      '<button class="mm-sheet-action" data-song-action="playlist"><i class="fas fa-plus"></i> Add to playlist</button>' +
      '<button class="mm-sheet-action is-danger" data-song-action="delete"><i class="fas fa-trash-alt"></i> Remove from library</button>'
    );
    sheetBody.dataset.songId = songId;
  }

  function onSongAction(button) {
    var songId = sheetBody.dataset.songId;
    if (!songId) return;

    switch (button.dataset.songAction) {
      case 'play':
        playSong(songId);
        closeSheet();
        break;
      case 'next':
        send('playLibrarySongNext', { songId: songId });
        closeSheet();
        break;
      case 'queue':
        send('queueLibrarySong', { songId: songId });
        closeSheet();
        break;
      case 'analyze':
        closeSheet();
        if (window.SongRequestAdvancedAudio) window.SongRequestAdvancedAudio.openAnalysis({ songId: songId });
        break;
      case 'karaoke':
        closeSheet();
        request('generateKaraokeVersion', { songId: songId }, 30 * 60 * 1000)
          .then(function (result) {
            if (result && result.success) toast('Karaoke version ready', 'success');
            else toast((result && result.error) || 'Karaoke generation failed', 'error');
          })
          .catch(function (error) { toast(error.message, 'error'); });
        break;
      case 'playlist':
        openPlaylistPicker(songId);
        break;
      case 'delete':
        openConfirmSheet(
          'Remove from library',
          'Remove this song from the library?',
          'Remove',
          function () {
            request('deleteLibrarySong', { songId: songId }).then(function (result) {
              if (result && result.error) toast(result.error, 'error');
              else { toast('Removed from library', 'success'); refreshLibraryViews(); }
            }).catch(function (error) { toast(error.message, 'error'); });
          }
        );
        break;
    }
  }

  function openPlaylistPicker(songId, songIds) {
    var ids = songIds && songIds.length ? songIds : (songId ? [songId] : []);
    request('getPlaylists').then(function (data) {
      // Auto/dynamic playlists are generated and cannot receive songs.
      var lists = ((data && data.playlists) || []).filter(function (list) { return !list.isDynamic; });
      var html = '<h3>Add to playlist</h3>' +
        (ids.length > 1 ? '<div class="mm-sheet-empty">' + ids.length + ' songs selected</div>' : '');

      if (!lists.length) html += '<div class="mm-sheet-empty">No playlists yet — create one below.</div>';

      lists.forEach(function (list) {
        html += '<button class="mm-sheet-action" data-add-playlist="' + esc(list.id) + '">' +
          plIconHtml(list.icon) + ' ' + esc(list.name) +
          ' <span style="font-weight:400;color:var(--mm-muted)">(' + (list.songCount || 0) + ')</span></button>';
      });

      html += '<div style="display:flex;gap:8px;margin-top:12px">' +
        '<input id="mm-new-playlist-name" class="mm-sheet-input" placeholder="New playlist">' +
        '<button id="mm-create-playlist-inline" class="mm-btn">Create</button></div>' +
        '<div class="mm-icon-heading">Icon</div>' +
        '<div id="mm-playlist-icon-picker" class="mm-icon-picker"></div>';

      playlistIconSelection = 'fa-list';
      openSheet(html);
      renderPlaylistIconPicker();
      sheetBody.dataset.songId = ids.length === 1 ? ids[0] : '';
      sheetBody.dataset.songIds = JSON.stringify(ids);
    }).catch(function () { });
  }

  function sheetSongIds() {
    try {
      var ids = JSON.parse(sheetBody.dataset.songIds || '[]');
      if (Array.isArray(ids) && ids.length) return ids;
    } catch (_) { }
    var single = sheetBody.dataset.songId;
    return single ? [single] : [];
  }

  function createPlaylist(name, songIds) {
    if (!name) return;
    var ids = Array.isArray(songIds) ? songIds : (songIds ? [songIds] : []);
    request('createPlaylist', { name: name, icon: playlistIconSelection || 'fa-list' }).then(function (result) {
      if (result && result.error) { toast(result.error, 'error'); return; }
      if (ids.length && result && result.id) {
        if (ids.length === 1) send('addSongToPlaylist', { playlistId: result.id, songId: ids[0] });
        else send('addSongsToPlaylist', { playlistId: result.id, songIds: ids });
      }
      toast('Playlist created', 'success');
      closeSheet();
      if (libSelecting) setLibrarySelecting(false);
      refreshPlaylists();
    }).catch(function (error) { toast(error.message, 'error'); });
  }

  // ── Library multi-select ─────────────────────────────────
  function setLibrarySelecting(on) {
    libSelecting = !!on;
    selectedSongIds = {};

    var button = $('mm-library-select');
    if (button) {
      button.classList.toggle('is-active', libSelecting);
      button.setAttribute('aria-pressed', libSelecting ? 'true' : 'false');
      button.setAttribute('aria-label', libSelecting ? 'Finish selecting songs' : 'Select songs');
    }

    var songs = $('mm-lib-songs');
    var recent = $('mm-lib-recent');
    if (songs) songs.classList.toggle('is-selecting', libSelecting);
    if (recent) recent.classList.toggle('is-selecting', libSelecting);

    var bar = $('mm-lib-multi');
    if (bar) {
      bar.hidden = !libSelecting;
      if (libSelecting) {
        bar.classList.remove('is-enter');
        void bar.offsetWidth;
        bar.classList.add('is-enter');
      }
    }

    if (!libSelecting) {
      // Leaving selection mode also leaves the extra bottom padding behind.
      var view = views.library;
      if (view) view.classList.remove('is-selecting');
      applyLibrarySelection();
      return;
    }

    if (views.library) views.library.classList.add('is-selecting');
    applyLibrarySelection();
  }

  function applyLibrarySelection() {
    var count = 0;
    var seen = {};
    if (views.library) {
      var rows = views.library.querySelectorAll('.mm-song[data-song-id]');
      for (var i = 0; i < rows.length; i++) {
        var id = rows[i].dataset.songId;
        var selected = !!(libSelecting && selectedSongIds[id]);
        rows[i].classList.toggle('is-selected', selected);
        // The same song can exist in both the songs and recent lists; count it
        // once even though both DOM rows show the selected state.
        if (selected && !seen[id]) { seen[id] = true; count++; }
      }
    }

    var countEl = $('mm-lib-multi-count');
    if (countEl) countEl.textContent = count + ' selected';
    var actions = document.querySelectorAll('#mm-lib-multi .mm-multi-btn');
    for (var a = 0; a < actions.length; a++) actions[a].disabled = count === 0;
  }

  function toggleSongSelection(id) {
    if (!id) return;
    if (selectedSongIds[id]) delete selectedSongIds[id];
    else selectedSongIds[id] = true;
    applyLibrarySelection();
  }

  function selectedSongIdList() {
    return Object.keys(selectedSongIds);
  }

  function runLibraryMultiAction(action) {
    var ids = selectedSongIdList();
    if (!ids.length) return;

    if (action === 'queue') {
      send('queueLibrarySongs', { songIds: ids });
      toast(ids.length + (ids.length === 1 ? ' song queued' : ' songs queued'), 'success');
      setLibrarySelecting(false);
      return;
    }

    if (action === 'playlist') {
      openPlaylistPicker(null, ids);
      return;
    }

    if (action === 'remove') {
      openConfirmSheet(
        'Remove from library',
        'Remove ' + ids.length + (ids.length === 1 ? ' song' : ' songs') + ' from the library?',
        'Remove',
        function () {
          request('deleteLibrarySongs', { songIds: ids }).then(function (result) {
            if (result && result.error) { toast(result.error, 'error'); return; }
            toast('Removed ' + (result.count || ids.length) + ' from the library', 'success');
            setLibrarySelecting(false);
            refreshLibraryViews();
          }).catch(function (error) { toast(error.message, 'error'); });
        }
      );
    }
  }

  function cycleLibrarySort() {
    var index = 0;
    for (var i = 0; i < LIB_SORTS.length; i++) {
      if (LIB_SORTS[i].id === lib.sortBy) { index = i; break; }
    }
    var next = LIB_SORTS[(index + 1) % LIB_SORTS.length];
    lib.sortBy = next.id;
    lib.descending = next.descending;
    libAt = 0;
    try {
      localStorage.setItem('sr-mobile-library-sort', JSON.stringify({ id: lib.sortBy, descending: lib.descending }));
    } catch (_) { }
    toast('Sorted by ' + next.label, 'info');
    if (lib.mode === 'songs' && !lib.query) loadLibrary(true);
  }

  function refreshPlaylists() {
    if (lib.mode === 'playlists') loadLibrary(true);
  }

  function refreshLibraryViews() {
    feed = null;
    feedAt = 0;
    recentAt = 0;
    lib.page = 0;
    if (currentTab === 'home') loadHomeFeed(true);
    if (currentTab === 'library') loadLibrary(true);
  }

  // ── YouTube search ───────────────────────────────────────
  function searchYoutubeFor(query) {
    lastQuery = query;
    homeResults.hidden = false;
    $('mm-home-feed').hidden = true;
    homeResults.innerHTML = '<div class="mm-empty"><i class="fab fa-youtube"></i><span>Searching YouTube…</span></div>';

    request('searchYoutube', { query: query, maxResults: 10 }).then(function (data) {
      var results = (data && data.results) || [];
      homeResults.hidden = false;
      $('mm-home-feed').hidden = true;

      var html = '<div class="mm-sheet-empty" style="padding:6px 16px">YouTube results for “' + esc(query) + '”</div>';
      if (!results.length) html += '<div class="mm-empty"><i class="fab fa-youtube"></i><span>Nothing found</span></div>';

      results.forEach(function (item) {
        html += '<button class="mm-song" data-ytid="' + esc(item.videoId) + '" data-title="' + esc(item.title) +
          '" data-author="' + esc(item.author) + '">' +
          '<div class="mm-thumb"><i class="fab fa-youtube"></i></div>' +
          '<div class="mm-song-text"><div class="mm-song-title">' + esc(item.title) + '</div>' +
          '<div class="mm-song-sub">' + esc(item.author || 'YouTube') + '</div></div>' +
          '<span class="mm-song-dur"><i class="fas fa-download"></i></span></button>';
      });

      homeResults.innerHTML = html;
    }).catch(function (error) {
      homeResults.innerHTML = '<div class="mm-empty"><i class="fas fa-triangle-exclamation"></i><span>' +
        esc(error.message || 'YouTube search failed') + '</span></div>';
    });
  }

  // ── Music Share ──────────────────────────────────────────
  function loadShareStatus() {
    request('musicShareGetStatus').then(onShareStatus).catch(function () { });
  }

  function onShareStatus(data) {
    if (!data) return;

    share.sharing = !!data.sharing;
    share.receiving = !!data.receiving;
    share.status = data.status || 'idle';
    share.sessionId = data.sessionId || null;
    if (data.receiveVolume != null) share.volume = data.receiveVolume;

    renderShare();
  }

  function renderShare() {
    var label = share.sharing ? 'Sharing' : share.receiving ? 'Receiving' : 'Idle';
    if (share.status && share.status !== 'idle') label += ' · ' + share.status;
    $('mm-share-status').textContent = label;

    var codeEl = $('mm-share-code');
    codeEl.hidden = !share.sessionId;
    if (share.sessionId) codeEl.textContent = share.sessionId;

    var start = $('mm-share-start');
    start.disabled = share.receiving;
    start.innerHTML = share.sharing
      ? '<i class="fas fa-stop"></i> Stop sharing'
      : '<i class="fas fa-tower-broadcast"></i> Start sharing';

    var join = $('mm-share-join');
    join.disabled = share.sharing;
    join.innerHTML = share.receiving
      ? '<i class="fas fa-right-from-bracket"></i> Disconnect'
      : '<i class="fas fa-right-to-bracket"></i> Connect';

    $('mm-share-join-code').disabled = share.receiving || share.sharing;
    $('mm-share-volume').value = String(Math.round(share.volume * 100));
  }

  function toggleSharing() {
    if (share.sharing) {
      send('musicShareStop');
      return;
    }

    var metadataOnly = $('mm-share-metadata-only').checked;
    request('musicShareStart', { metadataOnly: metadataOnly }).then(function (result) {
      if (result && result.error) toast(result.error, 'error');
      else toast('Sharing started', 'success');
      loadShareStatus();
    }).catch(function (error) { toast(error.message, 'error'); });
  }

  function toggleJoining() {
    if (share.receiving) {
      send('musicShareDisconnect');
      return;
    }

    var code = $('mm-share-join-code').value.trim();
    if (code.length !== 6) { toast('Enter the 6-digit session code', 'error'); return; }

    var receiveAudio = $('mm-share-receive-audio').checked;
    request('musicShareConnect', { sessionId: code, receiveAudio: receiveAudio }).then(function (result) {
      if (result && result.error) toast(result.error, 'error');
      else toast('Connected', 'success');
      loadShareStatus();
    }).catch(function (error) { toast(error.message, 'error'); });
  }

  // ── Wire the new controls ────────────────────────────────
  $('mm-more').addEventListener('click', openPlayerMenu);
  $('mm-home-settings').addEventListener('click', openSettingsSheet);
  $('mm-library-settings').addEventListener('click', openSettingsSheet);

  sheetBody.addEventListener('click', function (event) {
    var confirmOk = event.target.closest('#mm-confirm-ok');
    if (confirmOk) {
      var callback = pendingConfirm;
      pendingConfirm = null;
      closeSheet();
      if (callback) callback();
      return;
    }
    if (event.target.closest('#mm-confirm-cancel')) {
      pendingConfirm = null;
      closeSheet();
      return;
    }

    var playlistAction = event.target.closest('button[data-playlist-action]');
    if (playlistAction) { onPlaylistAction(playlistAction.dataset.playlistAction); return; }

    var renameSave = event.target.closest('#mm-playlist-rename-save');
    if (renameSave) {
      var input = $('mm-playlist-rename');
      var name = input ? input.value.trim() : '';
      if (!name || !currentPlaylist) { toast('Enter a playlist name', 'error'); return; }
      request('updatePlaylist', {
        playlistId: currentPlaylist.id,
        name: name,
        icon: playlistIconSelection || 'fa-list'
      }).then(function (result) {
        if (result && result.error) { toast(result.error, 'error'); return; }
        currentPlaylist.name = name;
        currentPlaylist.icon = playlistIconSelection || 'fa-list';
        renderPlaylist();
        toast('Playlist updated', 'success');
        closeSheet();
        feed = null;
        feedAt = 0;
        if (lib.mode === 'playlists') loadLibrary(true);
      }).catch(function (error) { toast(error.message, 'error'); });
      return;
    }

    var iconSave = event.target.closest('#mm-playlist-icon-save');
    if (iconSave) {
      if (!currentPlaylist) return;
      request('updatePlaylist', {
        playlistId: currentPlaylist.id,
        icon: playlistIconSelection || 'fa-list'
      }).then(function (result) {
        if (result && result.error) { toast(result.error, 'error'); return; }
        currentPlaylist.icon = playlistIconSelection || 'fa-list';
        renderPlaylist();
        toast('Playlist icon updated', 'success');
        closeSheet();
        feed = null;
        feedAt = 0;
        if (lib.mode === 'playlists') loadLibrary(true);
      }).catch(function (error) { toast(error.message, 'error'); });
      return;
    }

    var menu = event.target.closest('button[data-menu]');
    if (menu) { onSheetAction(menu); return; }

    var action = event.target.closest('button[data-song-action]');
    if (action) { onSongAction(action); return; }

    var queueAction = event.target.closest('button[data-queue-action]');
    if (queueAction) { onQueueItemAction(queueAction); return; }

    var add = event.target.closest('button[data-add-playlist]');
    if (add) {
      var ids = sheetSongIds();
      if (ids.length === 1) {
        send('addSongToPlaylist', { playlistId: add.dataset.addPlaylist, songId: ids[0] });
      } else if (ids.length > 1) {
        send('addSongsToPlaylist', { playlistId: add.dataset.addPlaylist, songIds: ids });
      }
      if (ids.length) toast('Added ' + ids.length + (ids.length === 1 ? ' song' : ' songs') + ' to playlist', 'success');
      closeSheet();
      if (libSelecting) setLibrarySelecting(false);
      return;
    }

    var settingsAction = event.target.closest('button[data-settings-action]');
    if (settingsAction) { onSettingsAction(settingsAction); return; }

    var inlineCreate = event.target.closest('#mm-create-playlist-inline');
    if (inlineCreate) {
      var input = $('mm-new-playlist-name');
      createPlaylist(input ? input.value.trim() : '', sheetSongIds());
    }
  });

  $('mm-shuffle').addEventListener('click', function () {
    send('shuffleQueue');
  });

  $('mm-repeat').addEventListener('click', function () {
    setRepeatState(!repeatEnabled);
    send('toggleRepeat');
  });

  $('mm-playlist-new').addEventListener('click', function () {
    playlistIconSelection = 'fa-list';
    openSheet(
      '<h3>New playlist</h3>' +
      '<input id="mm-new-playlist-name" class="mm-sheet-input" placeholder="Playlist name">' +
      '<div class="mm-icon-heading">Icon</div>' +
      '<div id="mm-playlist-icon-picker" class="mm-icon-picker"></div>' +
      '<button id="mm-create-playlist-inline" class="mm-btn" style="margin-top:12px;width:100%">Create playlist</button>'
    );
    renderPlaylistIconPicker();
  });

  // Playlist detail: browse songs, reorder them, remove entries, play/shuffle.
  $('mm-playlist-back').addEventListener('click', closePlaylist);
  $('mm-playlist-menu').addEventListener('click', openPlaylistMenu);
  $('mm-playlist-play').addEventListener('click', function () {
    if (currentPlaylist) send('playPlaylist', { playlistId: currentPlaylist.id });
  });
  $('mm-playlist-shuffle').addEventListener('click', function () {
    if (currentPlaylist) send('shufflePlaylist', { playlistId: currentPlaylist.id });
  });
  $('mm-playlist-queue').addEventListener('click', function () {
    if (!currentPlaylist) return;
    send('queuePlaylist', { playlistId: currentPlaylist.id });
    toast('Playlist added to the queue', 'success');
  });

  $('mm-playlist-list').addEventListener('click', function (event) {
    var add = event.target.closest('button[data-playlist-action]');
    if (add) { onPlaylistAction(add.dataset.playlistAction); return; }

    var row = event.target.closest('.mm-playlist-song');
    if (!row || !currentPlaylist) return;

    var index = (currentPlaylist.songs || []).findIndex(function (entry) { return entry.entryId === row.dataset.entryId; });
    if (index < 0) return;
    var entry = currentPlaylist.songs[index];

    var move = event.target.closest('button[data-entry-move]');
    if (move) {
      var target = move.dataset.entryMove === 'up' ? index - 1 : index + 1;
      if (target < 0 || target >= currentPlaylist.songs.length) return;
      request('reorderPlaylistEntry', {
        playlistId: currentPlaylist.id,
        entryId: entry.entryId,
        newSortOrder: target
      }).then(reloadPlaylist).catch(function (error) { toast(error.message, 'error'); });
      return;
    }

    var remove = event.target.closest('button[data-entry-remove]');
    if (remove) {
      request('removePlaylistEntry', {
        playlistId: currentPlaylist.id,
        entryId: entry.entryId
      }).then(function (result) {
        if (result && result.error) { toast(result.error, 'error'); return; }
        toast('Removed from playlist', 'success');
        reloadPlaylist();
      }).catch(function (error) { toast(error.message, 'error'); });
    }
  });

  $('mm-queue-shuffle').addEventListener('click', function () { send('shuffleQueue'); });

  // Home search results: YouTube results and the "search YouTube" button.
  homeResults.addEventListener('click', function (event) {
    var youtube = event.target.closest('button[data-ytid]');
    if (youtube) {
      send('downloadAndPlayYouTube', {
        videoId: youtube.dataset.ytid,
        action: 'play',
        title: youtube.dataset.title,
        author: youtube.dataset.author
      });
      toast('Downloading…', 'info');
      return;
    }

    var searchButton = event.target.closest('#mm-search-youtube');
    if (searchButton) searchYoutubeFor(lastQuery);
  });

  // Queue rows: tap while selecting toggles; long-press opens the compact item
  // actions (move/remove). No per-row buttons, so the list stays dense.
  function queueRowAt(event) {
    var row = event.target.closest('.mm-queue-row[data-queue-index]');
    if (!row) return null;
    var index = parseInt(row.dataset.queueIndex, 10);
    if (!isFinite(index)) return null;
    return { row: row, source: row.dataset.queueSource || 'queue', index: index };
  }

  queueListEl.addEventListener('click', function (event) {
    var target = queueRowAt(event);
    if (!target || !queueSelecting) return;
    toggleQueueSelection(target.source, target.index);
  });

  queueListEl.addEventListener('contextmenu', function (event) {
    var target = queueRowAt(event);
    if (!target) return;
    event.preventDefault();
    if (queueSelecting) toggleQueueSelection(target.source, target.index);
    else openQueueItemActions(target.source, target.index);
  });

  $('mm-queue-select').addEventListener('click', function () {
    setQueueSelecting(!queueSelecting);
  });
  $('mm-queue-multi-cancel').addEventListener('click', function () { setQueueSelecting(false); });
  var queueMultiRemove = document.querySelector('#mm-queue-multi [data-queue-multi="remove"]');
  if (queueMultiRemove) queueMultiRemove.addEventListener('click', removeSelectedQueueItems);

  // Share wiring.
  $('mm-share-start').addEventListener('click', toggleSharing);
  $('mm-share-join').addEventListener('click', toggleJoining);
  $('mm-share-volume').addEventListener('change', function () {
    share.volume = parseFloat(this.value) / 100;
    send('musicShareSetVolume', { value: share.volume });
  });

  // ── Start ────────────────────────────────────────────────
  lyricsEl.innerHTML = '<div class="mm-lyrics-empty"><i class="fas fa-music"></i><span>No lyrics available</span></div>';

  request('mobileReady').then(function (ready) {
    if (ready && ready.car) document.documentElement.setAttribute('data-car', '1');
    setTab((ready && ready.tab) || 'home');
  }).catch(function () {
    setTab('home');
  });

  // State catch-up (now playing, queue, lyrics) through the interop.
  request('musicPlayerReady').then(function (state) {
    if (!state) return;
    if (state.volume != null) playback.volume = state.volume;
    if (state.crossfade != null) playback.crossfade = state.crossfade;
    if (state.repeatEnabled != null) setRepeatState(state.repeatEnabled);
    if (state.lyricsFontScale != null) {
      lyrics.fontScale = Math.max(0.5, Math.min(2, state.lyricsFontScale));
      applyLyricsFontScale();
    }
    if (state.lyricsHidden != null) lyrics.hidden = !!state.lyricsHidden;
    applyMobileAiBadge(state.aiVocalActive === true, state.aiVocalModelName || '');
  }).catch(function () { });

  loadHomeFeed(true);
})();
