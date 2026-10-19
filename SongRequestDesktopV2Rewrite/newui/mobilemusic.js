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

  var now = { title: '', artist: '', art: '', playing: false, time: 0, total: 0 };
  var seeking = false;
  var seekDragging = false;

  var queue = [];
  var playlistQueue = [];

  var feed = null;
  var feedAt = 0;

  var lib = { mode: 'songs', page: 0, total: 0, loading: false, query: '' };
  var libSongs = [];
  var recentSongs = [];
  var playlists = [];
  var recentAt = 0;

  var lyrics = { synced: [], plain: [], active: -1 };
  var lastColors = '';

  // ── Element refs ─────────────────────────────────────────
  var appEl = $('mm-app');
  var views = {
    home: $('mm-view-home'),
    library: $('mm-view-library'),
    player: $('mm-view-player'),
    share: $('mm-view-share'),
  };

  var playback = { volume: 0.8, crossfade: 4 };
  var share = { sharing: false, receiving: false, status: 'idle', sessionId: null, volume: 1 };
  var lastQuery = '';

  var playerView = views.player;
  var artWrap = $('mm-art-wrap');
  var artEl = $('mm-art');
  var titleEl = $('mm-title');
  var artistEl = $('mm-artist');
  var lyricsEl = $('mm-lyrics');
  var lyricsToggle = $('mm-lyrics-toggle');
  var playBtn = $('mm-play');
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
    setTimeout(function () {
      if (!queueEl.classList.contains('is-open')) {
        queueScrim.hidden = true;
        queueEl.hidden = true;
      }
    }, 280);

    if (notifyHost !== false) send('mobileQueueClosed');
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
    return '<button class="mm-card" data-playlist-id="' + esc(list.id || '') + '">' +
      '<div class="mm-card-art">' + (list.thumbnail ? '<img src="' + esc(list.thumbnail) + '" alt="">' : '<i class="fas fa-list"></i>') + '</div>' +
      '<div class="mm-card-title">' + esc(list.emoji ? list.emoji + ' ' : '') + esc(list.name || 'Playlist') + '</div>' +
      '<div class="mm-card-sub">' + (list.songCount || 0) + ' songs</div>' +
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
          return '<button class="mm-song" data-playlist-id="' + esc(list.id || '') + '">' +
            '<div class="mm-thumb">' + (list.thumbnail ? '<img src="' + esc(list.thumbnail) + '" alt="">' : '<i class="fas fa-list"></i>') + '</div>' +
            '<div class="mm-song-text">' +
              '<div class="mm-song-title">' + esc(list.emoji ? list.emoji + ' ' : '') + esc(list.name || 'Playlist') + '</div>' +
              '<div class="mm-song-sub">' + (list.songCount || 0) + ' songs</div>' +
            '</div>' +
            '<i class="fas fa-play mm-song-dur"></i>' +
            '</button>';
        }).join('')
      : '<div class="mm-empty"><i class="fas fa-list"></i><span>No playlists yet</span></div>';
  }

  function songRow(song) {
    var id = song.songId || song.id || '';
    var duration = song.durationDisplay || formatTime(song.duration || 0);
    return '<button class="mm-song" data-song-id="' + esc(id) + '">' +
      '<div class="mm-thumb">' + (song.thumbnail ? '<img src="' + esc(song.thumbnail) + '" alt="">' : '<i class="fas fa-music"></i>') + '</div>' +
      '<div class="mm-song-text">' +
        '<div class="mm-song-title">' + esc(song.title || 'Unknown') + '</div>' +
        '<div class="mm-song-sub">' + esc(song.artist || '—') + '</div>' +
      '</div>' +
      '<span class="mm-song-dur">' + esc(duration) + '</span>' +
      '</button>';
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

  function updateMini() {
    var show = !!(now.title && (currentTab === 'home' || currentTab === 'library'));
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
    if (lyricsOpen) highlightLyrics(now.time);
  }

  // ── Lyrics ───────────────────────────────────────────────
  function onLyrics(data) {
    lyricsEl.innerHTML = '';
    lyrics.synced = (data && data.syncedLyrics) || [];
    lyrics.plain = (data && data.plainLyrics) || [];
    lyrics.active = -1;

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
    if (lyricsOpen) highlightLyrics(now.time);
  }

  function highlightLyrics(time) {
    if (!lyrics.synced.length || !lyricsOpen) return;

    var index = -1;
    for (var i = 0; i < lyrics.synced.length; i++) {
      if (lyrics.synced[i].time <= time + 0.15) index = i; else break;
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
    renderQueue();
  }

  function renderQueue() {
    var html = '';

    if (queue.length) {
      html += queue.map(function (item, index) {
        return queueRow(item, index, index === 0, 'removeQueueItem', true);
      }).join('');
    }

    if (playlistQueue.length) {
      html += '<div class="mm-row" style="margin:12px 0 2px"><h3>Playlist</h3></div>';
      html += playlistQueue.map(function (item, index) {
        return queueRow(item, index, false, 'removePlaylistQueueItem');
      }).join('');
    }

    queueListEl.innerHTML = html || '<div class="mm-empty"><i class="fas fa-list"></i><span>Queue is empty</span></div>';
  }

  function queueRow(item, index, current, removeMethod, canMove) {
    var move = canMove
      ? '<button class="mm-icon-btn" data-move="up" data-index="' + index + '" aria-label="Move up"><i class="fas fa-chevron-up"></i></button>' +
        '<button class="mm-icon-btn" data-move="down" data-index="' + index + '" aria-label="Move down"><i class="fas fa-chevron-down"></i></button>'
      : '';

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
      send('setNavColors', {});
      return;
    }

    var signature = hex(colors.primary) + hex(colors.secondary);
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
      updateMini();
    }

    if (data.thumbnail) setArt(data.thumbnail);

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

    if (newSong) {
      // Clear the previous song's artwork/colours; the same event normally carries
      // the new thumbnail right after this.
      now.art = '';
      artEl.removeAttribute('src');
      artEl.hidden = true;
      $('mm-art-placeholder').hidden = false;
      miniArt.removeAttribute('src');
      miniArt.hidden = true;
      if (!data.thumbnail) applyMusicColors(null);

      lyrics.synced = [];
      lyrics.plain = [];
      lyrics.active = -1;
      lyricsEl.innerHTML = '<div class="mm-lyrics-empty"><i class="fas fa-circle-notch fa-spin"></i><span>Loading lyrics…</span></div>';
      updatePlayer();
    }
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
    if (kind === 'song') playSong(id); else playPlaylist(id);
  });
  bindTapList($('mm-lib-songs'), function (kind, id) { if (kind === 'song') playSong(id); });
  bindTapList($('mm-lib-recent'), function (kind, id) { if (kind === 'song') playSong(id); });
  bindTapList($('mm-lib-playlists'), function (kind, id) { if (kind === 'playlist') playPlaylist(id); });

  // Search: Home shows library matches instead of the feed.
  var homeSearch = $('mm-home-search');
  var homeClear = $('mm-home-clear');
  var homeResults = $('mm-home-results');
  var searchTimer = null;

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
  playBtn.addEventListener('click', function () { send('playPause'); });
  $('mm-next').addEventListener('click', function () { send('skipNext'); });
  $('mm-prev').addEventListener('click', function () { send('skipPrevious'); });

  seekEl.addEventListener('input', function () {
    seekDragging = true;
    if (now.total > 0) curEl.textContent = formatTime(seekEl.value / 1000 * now.total);
  });

  seekEl.addEventListener('change', function () {
    var total = now.total || 0;
    var target = seekEl.value / 1000 * total;
    seekDragging = false;
    now.time = target;
    send('seek', { position: target });
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

  function openSheet(html) {
    sheetBody.innerHTML = html;
    sheetScrim.hidden = false;
    sheet.hidden = false;
    requestAnimationFrame(function () {
      sheet.classList.add('is-open');
      sheetScrim.classList.add('is-open');
    });
  }

  function closeSheet() {
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
      send('setMusicOutputDevice', { deviceNumber: parseInt(this.value, 10) || -1 });
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
        '<h3>Library</h3>' +
        '<label class="mm-check"><input type="checkbox" id="mm-set-auto-add"' + (cfg.libraryAutoAddDownloads ? ' checked' : '') + '> Add downloads to the library automatically</label>' +
        '<div style="display:flex;gap:8px;flex-wrap:wrap">' +
          '<button class="mm-btn" data-settings-action="import-files"><i class="fas fa-file-import"></i> Import files</button>' +
          '<button class="mm-btn" data-settings-action="import-folder"><i class="fas fa-folder-open"></i> Import folder</button>' +
          '<button class="mm-btn" data-settings-action="scan"><i class="fas fa-rotate"></i> Scan</button>' +
        '</div>' +
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
        libraryAutoAddDownloads: $('mm-set-auto-add').checked
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
        send('importLibrary', { mode: 'files' });
        toast('Choose audio files', 'info');
        closeSheet();
        break;
      case 'import-folder':
        send('importLibrary', { mode: 'folder' });
        toast('Choose a folder', 'info');
        closeSheet();
        break;
      case 'scan':
        request('scanLibrary').then(function () {
          toast('Library scanned', 'success');
          refreshLibraryViews();
        }).catch(function (error) { toast(error.message, 'error'); });
        break;
    }
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
        if (window.confirm('Remove this song from the library?')) {
          request('deleteLibrarySong', { songId: songId }).then(function (result) {
            if (result && result.error) toast(result.error, 'error');
            else { toast('Removed from library', 'success'); refreshLibraryViews(); }
            closeSheet();
          }).catch(function (error) { toast(error.message, 'error'); });
        }
        break;
    }
  }

  function openPlaylistPicker(songId) {
    request('getPlaylists').then(function (data) {
      var lists = (data && data.playlists) || [];
      var html = '<h3>Add to playlist</h3>';

      if (!lists.length) html += '<div class="mm-sheet-empty">No playlists yet — create one below.</div>';

      lists.forEach(function (list) {
        html += '<button class="mm-sheet-action" data-add-playlist="' + esc(list.id) + '">' +
          '<i class="fas fa-list"></i> ' + esc(list.name) +
          ' <span style="font-weight:400;color:var(--mm-muted)">(' + (list.songCount || 0) + ')</span></button>';
      });

      html += '<div style="display:flex;gap:8px;margin-top:12px">' +
        '<input id="mm-new-playlist-name" class="mm-sheet-input" placeholder="New playlist">' +
        '<button id="mm-create-playlist-inline" class="mm-btn">Create</button></div>';

      openSheet(html);
      sheetBody.dataset.songId = songId;
    }).catch(function () { });
  }

  function createPlaylist(name, songId) {
    if (!name) return;
    request('createPlaylist', { name: name }).then(function (result) {
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
    request('searchYoutube', { query: query, maxResults: 10 }).then(function (data) {
      var results = (data && data.results) || [];
      homeResults.hidden = false;
      $('mm-home-feed').hidden = true;

      var html = '<div class="mm-sheet-empty" style="padding:6px 16px">YouTube results</div>';
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
    }).catch(function (error) { toast(error.message, 'error'); });
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
    send('toggleRepeat');
  });

  $('mm-playlist-new').addEventListener('click', function () {
    openSheet(
      '<h3>New playlist</h3>' +
      '<input id="mm-new-playlist-name" class="mm-sheet-input" placeholder="Playlist name">' +
      '<button id="mm-create-playlist-inline" class="mm-btn" style="margin-top:12px;width:100%">Create playlist</button>'
    );
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
      var target = move.dataset.move === 'up' ? index - 1 : index + 1;
      if (target < 0 || target >= queue.length) return;
      send('moveQueueItem', { fromIndex: index, toIndex: target });
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
  }).catch(function () { });

  loadHomeFeed(true);
})();
