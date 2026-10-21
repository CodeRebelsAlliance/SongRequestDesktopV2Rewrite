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
  var seeking = false;
  var seekDragging = false;

  var queue = [];
  var playlistQueue = [];
  var currentPlaylist = null;
  var playlistReturnTab = 'library';
  var playlistIconSelection = 'fa-list';

  var feed = null;
  var feedAt = 0;

  var lib = { mode: 'songs', page: 0, total: 0, loading: false, query: '' };
  var libSongs = [];
  var recentSongs = [];
  var playlists = [];
  var recentAt = 0;

  var lyrics = { synced: [], plain: [], active: -1, syncOffset: 0, fontScale: 1, hidden: false };
  var lastColors = '';

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

  var miniEl = $('mm-mini');
  var miniArt = $('mm-mini-art');
  var miniTitle = $('mm-mini-title');
  var miniArtist = $('mm-mini-artist');
  var miniPlay = $('mm-mini-play');

  var queueEl = $('mm-queue');
  var queueScrim = $('mm-queue-scrim');
  var queueListEl = $('mm-queue-list');

  // ── Views ────────────────────────────────────────────────
  function setTab(tab) {
    if (tab === 'queue') {
      openQueue();
      return;
    }

    if (!views[tab]) tab = 'home';
    currentTab = tab;

    for (var name in views) views[name].hidden = name !== tab;

    closeQueue(false);
    updateMini();

    if (tab === 'home') loadHomeFeed(false);
    if (tab === 'library') loadLibrary(false);
    if (tab === 'player') updatePlayer();
    if (tab === 'share') loadShareStatus();
  }

  function openQueue() {
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
    var fresh = feed && Date.now() - feedAt < 15000;
    if (!force && fresh) {
      renderHome();
      return;
    }

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
  }

  function songCard(song) {
    var id = song.songId || song.id || '';
    return '<button class="mm-card" data-song-id="' + esc(id) + '">' +
      '<div class="mm-card-art">' + (song.thumbnail ? '<img src="' + esc(song.thumbnail) + '" alt="">' : '<i class="fas fa-music"></i>') + '</div>' +
      '<div class="mm-card-title">' + esc(song.title || 'Unknown') + '</div>' +
      '<div class="mm-card-sub">' + esc(song.artist || '—') + '</div>' +
      '</button>';
  }

  function playlistCard(list) {
    return '<button class="mm-card' + (list.isDynamic ? ' is-dynamic' : '') + '" data-playlist-id="' + esc(list.id || '') + '">' +
      '<div class="mm-card-art">' + (list.thumbnail ? '<img src="' + esc(list.thumbnail) + '" alt="">' : plIconHtml(list.icon)) + '</div>' +
      '<div class="mm-card-title">' + esc(list.name || 'Playlist') + '</div>' +
      '<div class="mm-card-sub">' + (list.songCount || 0) + ' songs' + (list.isDynamic ? ' · Auto' : '') + '</div>' +
      '</button>';
  }

  // ── Library ──────────────────────────────────────────────
  function setLibraryMode(mode) {
    lib.mode = mode;
    lib.page = 0;
    lib.query = '';
    var filter = $('mm-lib-search');
    if (filter) filter.value = '';

    var chips = document.querySelectorAll('.mm-chip');
    chips.forEach(function (chip) { chip.classList.toggle('is-active', chip.dataset.lib === mode); });

    $('mm-lib-search-wrap').hidden = mode !== 'songs';
    $('mm-lib-songs').hidden = mode !== 'songs';
    $('mm-lib-recent').hidden = mode !== 'recent';
    $('mm-lib-playlists').hidden = mode !== 'playlists';
    $('mm-lib-more-wrap').hidden = true;
    $('mm-playlist-new-wrap').hidden = mode !== 'playlists';
    $('mm-library-scroll').scrollTop = 0;

    loadLibrary(true);
  }

  function loadLibrary(force) {
    if (lib.mode === 'recent') {
      if (!force && Date.now() - recentAt < 15000) { renderRecent(); return; }
      request('getPlayHistory', { count: 60 }).then(function (data) {
        recentSongs = (data && data.history) || [];
        recentAt = Date.now();
        renderRecent();
      }).catch(function () { renderRecent(); });
      return;
    }

    if (lib.mode === 'playlists') {
      request('getPlaylists').then(function (data) {
        playlists = (data && data.playlists) || [];
        renderPlaylists();
      }).catch(function () { renderPlaylists(); });
      return;
    }

    if (lib.query) {
      request('searchLibrary', { query: lib.query, maxResults: 100 }).then(function (data) {
        libSongs = (data && data.results) || [];
        lib.total = libSongs.length;
        lib.page = 1;
        renderSongs();
      }).catch(function () { renderSongs(); });
      return;
    }

    if (lib.loading) return;
    lib.loading = true;
    var page = force ? 1 : lib.page + 1;

    request('getLibrarySongs', {
      page: page,
      pageSize: 60,
      sortBy: 'title',
      descending: false,
      filterMissing: true
    }).then(function (data) {
      var songs = (data && data.songs) || [];
      libSongs = page === 1 ? songs : libSongs.concat(songs);
      lib.page = page;
      lib.total = (data && data.total) || libSongs.length;
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
  }

  function renderPlaylists() {
    $('mm-lib-playlists').innerHTML = playlists.length
      ? playlists.map(function (list) {
          return '<button class="mm-song' + (list.isDynamic ? ' is-dynamic' : '') + '" data-playlist-id="' + esc(list.id || '') + '">' +
            '<div class="mm-thumb mm-playlist-icon">' + (list.thumbnail ? '<img src="' + esc(list.thumbnail) + '" alt="">' : plIconHtml(list.icon)) + '</div>' +
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
    return '<button class="mm-song" data-song-id="' + esc(id) + '">' +
      '<div class="mm-thumb">' + (song.thumbnail ? '<img src="' + esc(song.thumbnail) + '" alt="">' : '<i class="fas fa-music"></i>') + '</div>' +
      '<div class="mm-song-text">' +
        '<div class="mm-song-title">' + esc(song.title || 'Unknown') + '</div>' +
        '<div class="mm-song-sub">' + sub + '</div>' +
      '</div>' +
      '<span class="mm-song-dur">' + esc(duration) + '</span>' +
      '</button>';
  }

  // ── Playlist detail (browse + modify) ────────────────────
  function openPlaylist(id) {
    if (!id) return;
    if (currentTab !== 'playlist') playlistReturnTab = currentTab;

    request('getPlaylist', { playlistId: id }).then(function (data) {
      if (!data || data.error) { toast((data && data.error) || 'Playlist not found', 'error'); return; }

      currentPlaylist = data;
      currentTab = 'playlist';
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
        '<div class="mm-thumb">' + (entry.thumbnail ? '<img src="' + esc(entry.thumbnail) + '" alt="">' : '<i class="fas fa-music"></i>') + '</div>' +
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
    miniEl.hidden = !show;

    if (!show) return;
    miniTitle.textContent = now.title;
    miniArtist.textContent = now.artist || '—';
    if (now.art) { miniArt.src = now.art; miniArt.hidden = false; }
    setMiniPlayIcon(now.playing);
  }

  function setPlayingUi(playing) {
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
    miniPlay.innerHTML = playing ? '<i class="fas fa-pause"></i>' : '<i class="fas fa-play"></i>';
    miniPlay.setAttribute('aria-label', playing ? 'Pause' : 'Play');
  }

  function updateSeekUi() {
    var total = now.total || 0;
    var pct = total > 0 ? Math.min(1000, Math.max(0, now.time / total * 1000)) : 0;
    if (!seekDragging) seekEl.value = String(Math.round(pct));
    curEl.textContent = formatTime(now.time);
    totalEl.textContent = formatTime(total);
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

    if (lyricsOpen) {
      applyLyricsFontScale();
      highlightLyrics(now.time);
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
    lyricsEl.innerHTML = '';
    lyrics.synced = (data && data.syncedLyrics) || [];
    lyrics.plain = (data && data.plainLyrics) || [];
    lyrics.active = -1;
    setLyricsSource(data && data.provider ? data.provider : '');

    if (!lyrics.synced.length && !lyrics.plain.length) {
      lyricsEl.innerHTML = '<div class="mm-lyrics-empty"><i class="fas fa-music"></i><span>No lyrics available</span></div>';
      return;
    }

    var html = '';
    if (lyrics.synced.length) {
      for (var i = 0; i < lyrics.synced.length; i++) {
        html += '<div class="mm-line" data-index="' + i + '">' + esc(lyrics.synced[i].text || '') + '</div>';
      }
    } else {
      for (var j = 0; j < lyrics.plain.length; j++) {
        var text = lyrics.plain[j];
        if (!text) { html += '<div class="mm-gap"></div>'; continue; }
        html += '<div class="mm-line">' + esc(text) + '</div>';
      }
    }

    lyricsEl.innerHTML = html;
    applyLyricsFontScale();
    if (lyricsOpen) highlightLyrics(now.time);
  }

  function highlightLyrics(time) {
    if (!lyrics.synced.length || !lyricsOpen) return;

    var adjustedTime = time + lyrics.syncOffset;
    var index = -1;
    for (var i = 0; i < lyrics.synced.length; i++) {
      if (lyrics.synced[i].time <= adjustedTime) index = i; else break;
    }

    if (index === lyrics.active) return;
    lyrics.active = index;

    var lines = lyricsEl.querySelectorAll('.mm-line');
    for (var j = 0; j < lines.length; j++) {
      lines[j].classList.toggle('is-active', j === index);
    }

    var active = lines[index];
    if (!active) return;

    var target = active.offsetTop - lyricsEl.clientHeight / 2 + active.offsetHeight / 2;
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
    renderQueue();
  }

  function renderQueue() {
    var html = '';

    if (queue.length) {
      html += queue.map(function (item, index) {
        return queueRow(item, index, index === 0, 'removeQueueItem', 'queue');
      }).join('');
    }

    if (playlistQueue.length) {
      html += '<div class="mm-row" style="margin:12px 0 2px"><h3>Playlist</h3></div>';
      html += playlistQueue.map(function (item, index) {
        return queueRow(item, index, false, 'removePlaylistQueueItem', 'playlist');
      }).join('');
    }

    queueListEl.innerHTML = html || '<div class="mm-empty"><i class="fas fa-list"></i><span>Queue is empty</span></div>';
  }

  function queueRow(item, index, current, removeMethod, moveSource) {
    // The first queue item is the now-playing slot and cannot be reordered (the
    // desktop engine refuses to move anything into index 0).
    var canMove = !current;
    var listLength = moveSource === 'playlist' ? playlistQueue.length : queue.length;
    var move = '';
    if (canMove) {
      var canUp = moveSource === 'playlist' ? index > 0 : index > 1;
      var canDown = index < listLength - 1;
      move =
        '<button class="mm-icon-btn" data-move="up" data-source="' + moveSource + '" data-index="' + index + '"' + (canUp ? '' : ' disabled') + ' aria-label="Move up"><i class="fas fa-chevron-up"></i></button>' +
        '<button class="mm-icon-btn" data-move="down" data-source="' + moveSource + '" data-index="' + index + '"' + (canDown ? '' : ' disabled') + ' aria-label="Move down"><i class="fas fa-chevron-down"></i></button>';
    }

    return '<div class="mm-queue-row' + (current ? ' is-current' : '') + '">' +
      (current ? '<div class="mm-now-bar"></div>' : '') +
      '<div class="mm-thumb">' + (item.thumbnail ? '<img src="' + esc(item.thumbnail) + '" alt="">' : '<i class="fas fa-music"></i>') + '</div>' +
      '<div class="mm-queue-text">' +
        '<div class="mm-queue-title">' + esc(item.title || 'Unknown') + '</div>' +
        '<div class="mm-queue-sub">' + esc(item.artist || item.estimatedStart || '—') + '</div>' +
      '</div>' +
      move +
      '<button class="mm-icon-btn" data-remove="' + esc(removeMethod) + '" data-index="' + index + '" aria-label="Remove">' +
        '<i class="fas fa-times"></i></button>' +
      '</div>';
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

      case 'mobileThemeColors':
        if (window.SongRequestTheme) window.SongRequestTheme.apply(data, 'mm', '#1d1b20', '#26232c');
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
      updateLyricsSyncUi();
      lyricsEl.innerHTML = '<div class="mm-lyrics-empty"><i class="fas fa-circle-notch fa-spin"></i><span>Loading lyrics…</span></div>';
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
  function bindTapList(container, handler) {
    container.addEventListener('click', function (event) {
      var button = event.target.closest('button[data-song-id]');
      if (button) { handler('song', button.dataset.songId); return; }

      var playlist = event.target.closest('button[data-playlist-id]');
      if (playlist) handler('playlist', playlist.dataset.playlistId);
    });

    // Long-press (Android fires contextmenu) opens the song actions.
    container.addEventListener('contextmenu', function (event) {
      var button = event.target.closest('button[data-song-id]');
      if (!button) return;
      event.preventDefault();
      var row = button.querySelector('.mm-song-title, .mm-card-title');
      openSongActions(button.dataset.songId, row ? row.textContent : '');
    });
  }

  function playSong(id) { if (id) send('playLibrarySong', { songId: id }); }
  function playPlaylist(id) { if (id) send('playPlaylist', { playlistId: id }); }

  bindTapList($('mm-home-scroll'), function (kind, id) {
    if (kind === 'song') playSong(id); else openPlaylist(id);
  });
  bindTapList($('mm-lib-songs'), function (kind, id) { if (kind === 'song') playSong(id); });
  bindTapList($('mm-lib-recent'), function (kind, id) { if (kind === 'song') playSong(id); });
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
  var libTimer = null;
  libSearch.addEventListener('input', function () {
    clearTimeout(libTimer);
    libTimer = setTimeout(function () {
      lib.query = libSearch.value.trim();
      lib.page = 0;
      loadLibrary(true);
    }, 250);
  });

  $('mm-lib-more').addEventListener('click', function () { loadLibrary(false); });

  // Player controls
  lyricsToggle.addEventListener('click', function () { toggleLyrics(); });
  if (lyricsSettingsToggle) {
    lyricsSettingsToggle.addEventListener('click', function () { toggleLyricsSettings(); });
  }
  updateLyricsSyncUi();
  updateLyricsFontUi();

  if (queueToggle) {
    queueToggle.addEventListener('click', function () {
      if (queueEl.hidden) openQueue(); else closeQueue(true);
    });
  }
  playBtn.addEventListener('click', function () { send('playPause'); });
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
  miniPlay.addEventListener('click', function () { send('playPause'); });
  $('mm-mini-next').addEventListener('click', function () { send('skipNext'); });

  // Queue sidebar
  $('mm-queue-close').addEventListener('click', function () { closeQueue(true); });
  queueScrim.addEventListener('click', function () { closeQueue(true); });
  $('mm-queue-clear').addEventListener('click', function () { send('clearQueue'); });

  queueListEl.addEventListener('click', function (event) {
    var remove = event.target.closest('button[data-remove]');
    if (!remove) return;

    var index = parseInt(remove.dataset.index, 10);
    if (!isFinite(index)) return;

    send(remove.dataset.remove, { index: index });
  });

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
          '<h3>Audio output</h3>' +
          '<div class="mm-sheet-row"><select id="mm-menu-device">' + deviceOptions + '</select></div>' +
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

  function openSettingsSheet() {
    request('getSettings').then(function (cfg) {
      cfg = cfg || {};

      var modeOptions = MODES.map(function (mode) {
        return option(mode[0], mode[1], cfg.operatingMode || 'SongRequests');
      }).join('');

      openSheet(
        '<h3>Operating mode</h3>' +
        '<div class="mm-sheet-row"><select id="mm-set-mode">' + modeOptions + '</select></div>' +
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
    }).catch(function (error) { toast(error.message, 'error'); });
  }

  function wireSettingsSheet() {
    var mode = $('mm-set-mode');
    if (mode) mode.addEventListener('change', function () {
      send('setOperatingMode', { mode: this.value });
      closeSheet();
      toast('Operating mode: ' + this.options[this.selectedIndex].text, 'success');
    });

    var save = $('mm-settings-save');
    if (save) save.addEventListener('click', function () {
      var settings = {
        address: $('mm-set-address').value.trim(),
        bearerToken: $('mm-set-token').value,
        autoGrabCookies: $('mm-set-auto-yt').checked,
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

  function openPlaylistPicker(songId) {
    request('getPlaylists').then(function (data) {
      // Auto/dynamic playlists are generated and cannot receive songs.
      var lists = ((data && data.playlists) || []).filter(function (list) { return !list.isDynamic; });
      var html = '<h3>Add to playlist</h3>';

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
      sheetBody.dataset.songId = songId;
    }).catch(function () { });
  }

  function createPlaylist(name, songId) {
    if (!name) return;
    request('createPlaylist', { name: name, icon: playlistIconSelection || 'fa-list' }).then(function (result) {
      if (result && result.error) { toast(result.error, 'error'); return; }
      if (songId && result && result.id) send('addSongToPlaylist', { playlistId: result.id, songId: songId });
      toast('Playlist created', 'success');
      closeSheet();
      refreshPlaylists();
    }).catch(function (error) { toast(error.message, 'error'); });
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

    var add = event.target.closest('button[data-add-playlist]');
    if (add) {
      var songId = sheetBody.dataset.songId;
      if (songId) {
        send('addSongToPlaylist', { playlistId: add.dataset.addPlaylist, songId: songId });
        toast('Added to playlist', 'success');
      }
      closeSheet();
      return;
    }

    var settingsAction = event.target.closest('button[data-settings-action]');
    if (settingsAction) { onSettingsAction(settingsAction); return; }

    var inlineCreate = event.target.closest('#mm-create-playlist-inline');
    if (inlineCreate) {
      var input = $('mm-new-playlist-name');
      createPlaylist(input ? input.value.trim() : '', sheetBody.dataset.songId);
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

  // Queue reordering / removal.
  queueListEl.addEventListener('click', function (event) {
    var move = event.target.closest('button[data-move]');
    if (move) {
      var index = parseInt(move.dataset.index, 10);
      if (!isFinite(index)) return;

      var isPlaylist = move.dataset.source === 'playlist';
      var list = isPlaylist ? playlistQueue : queue;
      var target = move.dataset.move === 'up' ? index - 1 : index + 1;
      // The now-playing slot (regular queue index 0) must stay first.
      var minIndex = isPlaylist ? 0 : 1;
      if (target < minIndex || target >= list.length) return;

      send(isPlaylist ? 'movePlaylistQueueItem' : 'moveQueueItem', { fromIndex: index, toIndex: target });
      return;
    }
  });

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
  }).catch(function () { });

  loadHomeFeed(true);
})();
