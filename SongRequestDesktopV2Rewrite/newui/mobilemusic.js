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
  };

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
        return queueRow(item, index, index === 0, 'removeQueueItem');
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

  function queueRow(item, index, current, removeMethod) {
    return '<div class="mm-queue-row' + (current ? ' is-current' : '') + '">' +
      (current ? '<div class="mm-now-bar"></div>' : '') +
      '<div class="mm-thumb">' + (item.thumbnail ? '<img src="' + esc(item.thumbnail) + '" alt="">' : '<i class="fas fa-music"></i>') + '</div>' +
      '<div class="mm-queue-text">' +
        '<div class="mm-queue-title">' + esc(item.title || 'Unknown') + '</div>' +
        '<div class="mm-queue-sub">' + esc(item.artist || item.estimatedStart || '—') + '</div>' +
      '</div>' +
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
      request('searchLibrary', { query: query, maxResults: 80 }).then(function (data) {
        if (homeSearch.value.trim() !== query) return;
        var results = (data && data.results) || [];
        homeResults.innerHTML = results.length
          ? results.map(songRow).join('')
          : '<div class="mm-empty"><i class="fas fa-search"></i><span>Nothing found</span></div>';
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

  // ── Start ────────────────────────────────────────────────
  lyricsEl.innerHTML = '<div class="mm-lyrics-empty"><i class="fas fa-music"></i><span>No lyrics available</span></div>';

  request('mobileReady').then(function (ready) {
    setTab((ready && ready.tab) || 'home');
  }).catch(function () {
    setTab('home');
  });

  // State catch-up (now playing, queue, lyrics) through the interop.
  send('musicPlayerReady');
  loadHomeFeed(true);
})();
