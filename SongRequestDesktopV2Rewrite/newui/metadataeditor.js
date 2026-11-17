(function () {
  'use strict';

  var el = function (id) { return document.getElementById(id); };
  var pending = {};
  var requestId = 0;

  function esc(value) {
    return String(value == null ? '' : value)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function toast(message, type) {
    var host = el('me-toasts');
    var node = document.createElement('div');
    node.className = 'me-toast' + (type ? ' is-' + type : '');
    node.textContent = message;
    host.appendChild(node);
    setTimeout(function () { node.classList.add('is-out'); }, 2400);
    setTimeout(function () { if (node.parentNode) node.remove(); }, 3000);
  }

  function request(method, params, timeoutMs) {
    return new Promise(function (resolve, reject) {
      var id = ++requestId;
      var timer = setTimeout(function () {
        delete pending[id];
        reject(new Error('Request timed out: ' + method));
      }, timeoutMs || 45000);
      pending[id] = { resolve: resolve, reject: reject, timer: timer };

      var message = { type: 'request', id: id, method: method };
      if (params) Object.keys(params).forEach(function (k) { message[k] = params[k]; });
      if (!SongRequestHost.sendToHost(JSON.stringify(message))) {
        clearTimeout(timer);
        delete pending[id];
        reject(new Error('No host channel'));
      }
    });
  }

  function send(method, params) {
    var message = { type: 'request', id: 0, method: method };
    if (params) Object.keys(params).forEach(function (k) { message[k] = params[k]; });
    SongRequestHost.sendToHost(JSON.stringify(message));
  }

  SongRequestHost.registerHostMessageHandler(function (raw) {
    try {
      var message = JSON.parse(raw);
      if (message.type === 'response') {
        var entry = pending[message.id];
        if (!entry) return;
        clearTimeout(entry.timer);
        delete pending[message.id];
        if (message.result && message.result.error) entry.reject(new Error(message.result.error));
        else entry.resolve(message.result);
        return;
      }
      if (message.type === 'event' && message.eventName === 'metadataEditorSong' && message.data) {
        load(message.data.songId);
      }
    } catch (_) { }
  });

  // ── State ────────────────────────────────────────────────
  var state = {
    songId: null,
    metadata: null,
    duration: 0,
    lyricsMode: 'synced',
    lines: [],
    plain: '',
    artworkDataUrl: null,
    artworkMime: null,
    removeArtwork: false,
    activeLine: -1,
  };
  var providerStatus = {};

  function str(id) { var node = el(id); return node ? node.value.trim() : ''; }
  function num(id) { var value = parseInt(str(id), 10); return isFinite(value) && value > 0 ? value : 0; }
  function setValue(id, value) { var node = el(id); if (node) node.value = value == null ? '' : value; }

  function setStatus(text, warn) {
    var node = el('me-status');
    node.hidden = !text;
    node.textContent = text || '';
    node.classList.toggle('is-warn', !!warn);
  }

  function load(songId) {
    if (!songId) return;
    state.songId = songId;
    request('getSongMetadata', { songId: songId }).then(function (m) {
      state.metadata = m;
      state.duration = Number(m.duration) || 0;
      state.artworkDataUrl = m.artworkDataUrl || null;
      state.artworkMime = m.artworkMimeType || null;
      state.removeArtwork = false;

      setValue('me-title', m.title);
      setValue('me-artist', m.artist);
      setValue('me-album-artist', m.albumArtist);
      setValue('me-album', m.album);
      setValue('me-genre', m.genre);
      setValue('me-year', m.year || '');
      setValue('me-track', m.track || '');
      setValue('me-disc', m.disc || '');
      setValue('me-bpm', m.bpm || '');
      setValue('me-comment', m.comment);
      setValue('me-composer', m.composer);
      setValue('me-lyricist', m.lyricist);
      setValue('me-conductor', m.conductor);
      setValue('me-publisher', m.publisher);
      setValue('me-copyright', m.copyright);
      setValue('me-isrc', m.isrc);
      setValue('me-grouping', m.grouping);

      el('me-subtitle').textContent = (m.title || 'Untitled') + (m.artist ? ' · ' + m.artist : '') +
        (m.fileFormat ? ' · ' + m.fileFormat : '');
      el('me-file-format').textContent = m.fileFormat || 'unknown format';

      var note = el('me-storage-note');
      if (m.metadataStoredInLibrary) {
        note.hidden = false;
        note.className = 'me-note is-warn';
        note.textContent = 'This file could not be written previously. The editable metadata is stored in the SongRequest library and remains authoritative until a write succeeds.';
      } else if (!m.canWriteToFile) {
        note.hidden = false;
        note.className = 'me-note is-warn';
        note.textContent = m.fileExists
          ? 'The audio file is not writable. Saving will keep the metadata in the SongRequest library.'
          : 'The audio file is missing. Saving will keep the metadata in the SongRequest library.';
      } else {
        note.hidden = true;
        note.textContent = '';
      }

      if (m.providers) {
        el('me-prov-mb').checked = m.providers.musicBrainz !== false;
        el('me-prov-itunes').checked = m.providers.iTunes !== false;
        el('me-prov-deezer').checked = m.providers.deezer !== false;
        el('me-prov-caa').checked = m.providers.coverArtArchive !== false;
        el('me-prov-acoustid').checked = m.providers.acoustId === true;
        el('me-acoustid-key').value = m.providers.acoustIdApiKey || '';
        el('me-prov-discogs').checked = m.providers.discogs === true;
        el('me-discogs-token').value = m.providers.discogsToken || '';
      }
      renderPriority();

      renderArtwork();
      setLyricsFromMetadata(m);
      clearCandidates();
      setStatus(m.warning || '', true);
      refreshProviderStatus();
    }).catch(function (error) {
      setStatus('Could not load metadata: ' + error.message, true);
    });
  }

  function setLyricsFromMetadata(m) {
    var text = m.lyrics || '';
    if (m.lyricsAreSynced) {
      state.lyricsMode = 'synced';
      state.lines = parseLrc(text);
      state.plain = '';
    } else {
      state.lyricsMode = 'plain';
      state.lines = [];
      state.plain = text;
    }
    el('me-lyrics-source').textContent = m.lyricsStoredInLibrary
      ? 'These lyrics are stored in the library because the file could not be written.'
      : '';
    renderLyrics();
  }

  function buildEdit() {
    return {
      title: str('me-title'),
      artist: str('me-artist'),
      albumArtist: str('me-album-artist'),
      album: str('me-album'),
      genre: str('me-genre'),
      year: num('me-year'),
      track: num('me-track'),
      disc: num('me-disc'),
      bpm: num('me-bpm'),
      comment: str('me-comment'),
      composer: str('me-composer'),
      lyricist: str('me-lyricist'),
      conductor: str('me-conductor'),
      publisher: str('me-publisher'),
      copyright: str('me-copyright'),
      isrc: str('me-isrc'),
      grouping: str('me-grouping'),
    };
  }

  function currentLyrics() {
    if (state.lyricsMode === 'synced') return buildLrc(state.lines);
    return el('me-lyrics-plain').value;
  }

  function save() {
    if (!state.songId) return;
    var button = el('me-save');
    button.disabled = true;
    var edit = buildEdit();
    edit.songId = state.songId;
    edit.lyrics = currentLyrics();
    edit.removeArtwork = state.removeArtwork;
    if (!state.removeArtwork && state.artworkDataUrl) {
      edit.artworkDataUrl = state.artworkDataUrl;
      edit.artworkMimeType = state.artworkMime;
    }

    request('saveSongMetadata', edit, 60000).then(function (result) {
      button.disabled = false;
      if (!result || !result.success) {
        toast((result && result.error) || 'Save failed', 'error');
        return;
      }
      state.metadata = result.metadata;
      state.removeArtwork = false;
      if (result.metadata) {
        state.artworkDataUrl = result.metadata.artworkDataUrl || null;
        state.artworkMime = result.metadata.artworkMimeType || null;
        renderArtwork();
      }
      if (result.storedInLibrary) {
        toast('Saved in the library (the file could not be written)', 'error');
        setStatus(result.warning || 'The file tags could not be written. Metadata is stored in the library.', true);
      } else {
        toast('Metadata saved', 'success');
        setStatus('', false);
      }
      if (result.warning) toast(result.warning, 'error');
      el('me-subtitle').textContent = (str('me-title') || 'Untitled') + (str('me-artist') ? ' · ' + str('me-artist') : '');
    }).catch(function (error) {
      button.disabled = false;
      toast('Save failed: ' + error.message, 'error');
    });
  }

  function close() {
    send('closeMetadataEditor');
  }

  // ── Artwork ─────────────────────────────────────────────
  function renderArtwork() {
    var box = el('me-artwork-preview');
    if (state.artworkDataUrl && !state.removeArtwork) {
      box.innerHTML = '<img src="' + esc(state.artworkDataUrl) + '" alt="Cover art">';
    } else {
      box.innerHTML = '<i class="fas fa-music"></i>';
    }
    el('me-artwork-source').textContent = state.removeArtwork
      ? 'will be removed'
      : state.artworkDataUrl ? 'current artwork' : 'no artwork';
  }

  function setArtwork(dataUrl, mime) {
    state.artworkDataUrl = dataUrl;
    state.artworkMime = mime || null;
    state.removeArtwork = false;
    renderArtwork();
  }

  el('me-artwork-pick').addEventListener('click', function () {
    request('pickSongArtwork', null, 120000).then(function (result) {
      if (result && result.cancelled) return;
      if (result && result.error) { toast(result.error, 'error'); return; }
      if (result && result.dataUrl) { setArtwork(result.dataUrl, result.mimeType); toast('Artwork selected', 'success'); }
    }).catch(function (error) { toast(error.message, 'error'); });
  });

  el('me-artwork-remove').addEventListener('click', function () {
    state.removeArtwork = true;
    state.artworkDataUrl = null;
    renderArtwork();
  });

  function renderArtworkResults(candidates) {
    var host = el('me-artwork-results');
    var withArt = (candidates || []).filter(function (c) { return c.artworkUrl; });
    if (!withArt.length) { host.innerHTML = ''; return; }
    host.innerHTML = withArt.slice(0, 8).map(function (c, index) {
      return '<div class="me-artwork-result">' +
        '<div class="me-candidate-art"><i class="fas fa-image"></i></div>' +
        '<div class="me-candidate-main"><div class="me-candidate-title">' + esc(c.album || c.title) + '</div>' +
        '<div class="me-candidate-sub">' + esc(c.provider) + ' · ' + esc(c.artist) + '</div></div>' +
        '<button class="me-btn" data-artwork-url="' + esc(c.artworkUrl) + '">Use artwork</button></div>';
    }).join('');
  }

  el('me-artwork-results').addEventListener('click', function (event) {
    var button = event.target.closest('[data-artwork-url]');
    if (!button) return;
    var url = button.getAttribute('data-artwork-url');
    button.disabled = true;
    request('fetchSongArtwork', { url: url }, 60000).then(function (result) {
      button.disabled = false;
      if (result && result.error) { toast(result.error, 'error'); return; }
      if (result && result.dataUrl) { setArtwork(result.dataUrl, result.mimeType); toast('Artwork downloaded', 'success'); }
    }).catch(function (error) { button.disabled = false; toast(error.message, 'error'); });
  });

  // ── Online sources / identify ───────────────────────────
  function providerSettings() {
    return {
      metadataProviderMusicBrainz: el('me-prov-mb').checked,
      metadataProviderITunes: el('me-prov-itunes').checked,
      metadataProviderDeezer: el('me-prov-deezer').checked,
      metadataProviderCoverArtArchive: el('me-prov-caa').checked,
      metadataProviderAcoustId: el('me-prov-acoustid').checked,
      metadataProviderDiscogs: el('me-prov-discogs').checked,
    };
  }

  var providerLabels = {
    MusicBrainz: 'MusicBrainz',
    iTunes: 'iTunes',
    Deezer: 'Deezer',
    CoverArtArchive: 'Cover Art Archive',
    AcoustID: 'AcoustID fingerprint',
    Discogs: 'Discogs'
  };
  var providerFields = {
    MusicBrainz: 'me-prov-mb',
    iTunes: 'me-prov-itunes',
    Deezer: 'me-prov-deezer',
    CoverArtArchive: 'me-prov-caa',
    AcoustID: 'me-prov-acoustid',
    Discogs: 'me-prov-discogs'
  };

  function providerPriority() {
    var defaults = Object.keys(providerLabels);
    var source = state.metadata && state.metadata.providers && state.metadata.providers.priority;
    if (!Array.isArray(source)) source = [];
    var result = [];
    source.forEach(function (name) {
      var match = defaults.find(function (d) { return d.toLowerCase() === String(name).toLowerCase(); });
      if (match && result.indexOf(match) < 0) result.push(match);
    });
    defaults.forEach(function (name) { if (result.indexOf(name) < 0) result.push(name); });
    return result;
  }

  function providerEnabled(name) {
    var field = providerFields[name];
    return field ? el(field).checked : false;
  }

  function renderPriority() {
    var host = el('me-provider-priority');
    if (!host) return;
    var list = providerPriority();
    host.innerHTML = list.map(function (name, index) {
      var status = providerStatus[name] || null;
      var enabled = providerEnabled(name);
      var label = enabled ? 'ready' : 'disabled';
      var className = enabled ? 'is-on' : '';
      if (status && enabled) {
        if (status.state === 'rate_limited') {
          label = cooldownText(status) || 'rate limited';
          className = 'is-warn';
        } else if (status.state === 'unauthorized') {
          label = 'token error';
          className = 'is-error';
        } else if (status.state === 'error') {
          label = cooldownText(status) || 'error';
          className = 'is-error';
        } else if (status.requestsInWindow > 0) {
          label = status.requestsInWindow + '/' + status.maxRequests + ' recent';
        }
      }
      var title = status ? (status.lastError || status.limitLabel || '') : '';
      return '<div class="me-priority-row" title="' + esc(title) + '">' +
        '<span class="me-priority-index">' + (index + 1) + '</span>' +
        '<span class="me-priority-name">' + esc(providerLabels[name]) +
          '<span class="me-priority-limit">' + esc((status && status.limitLabel) || '') + '</span></span>' +
        '<span class="me-priority-state ' + className + '">' + esc(label) + '</span>' +
        '<button class="me-btn" data-priority-up="' + index + '"' + (index === 0 ? ' disabled' : '') + '>↑</button>' +
        '<button class="me-btn" data-priority-down="' + index + '"' + (index === list.length - 1 ? ' disabled' : '') + '>↓</button>' +
      '</div>';
    }).join('');
  }

  function cooldownText(status) {
    if (!status || !status.cooldownUntilUtc) return '';
    var until = new Date(status.cooldownUntilUtc).getTime();
    var seconds = Math.max(0, Math.ceil((until - Date.now()) / 1000));
    return seconds > 0 ? 'wait ' + seconds + 's' : 'ready';
  }

  function refreshProviderStatus() {
    request('getMetadataProviderStatus', null, 15000).then(function (data) {
      providerStatus = {};
      ((data && data.providers) || []).forEach(function (p) {
        if (p && p.name) providerStatus[p.name] = p;
      });
      renderPriority();
    }).catch(function () { });
  }

  function movePriority(index, delta) {
    var list = providerPriority();
    var next = index + delta;
    if (next < 0 || next >= list.length) return;
    var item = list.splice(index, 1)[0];
    list.splice(next, 0, item);
    if (state.metadata && state.metadata.providers) state.metadata.providers.priority = list;
    send('saveSettings', { settings: { metadataProviderPriority: list } });
    renderPriority();
  }

  ['me-prov-mb', 'me-prov-itunes', 'me-prov-deezer', 'me-prov-caa', 'me-prov-acoustid', 'me-prov-discogs'].forEach(function (id) {
    el(id).addEventListener('change', function () {
      send('saveSettings', { settings: providerSettings() });
      renderPriority();
    });
  });

  el('me-acoustid-save').addEventListener('click', function () {
    send('saveSettings', {
      settings: {
        acoustIdApiKey: el('me-acoustid-key').value.trim(),
        metadataProviderAcoustId: el('me-prov-acoustid').checked,
      }
    });
    toast('AcoustID key saved', 'success');
  });

  el('me-discogs-save').addEventListener('click', function () {
    send('saveSettings', {
      settings: {
        discogsToken: el('me-discogs-token').value.trim(),
        metadataProviderDiscogs: el('me-prov-discogs').checked,
      }
    });
    toast('Discogs token saved', 'success');
  });

  el('me-provider-priority').addEventListener('click', function (event) {
    var up = event.target.closest('[data-priority-up]');
    if (up) { movePriority(parseInt(up.getAttribute('data-priority-up'), 10), -1); return; }
    var down = event.target.closest('[data-priority-down]');
    if (down) movePriority(parseInt(down.getAttribute('data-priority-down'), 10), 1);
  });

  function renderCandidates(result) {
    var host = el('me-candidates');
    var candidates = (result && result.candidates) || [];
    if (!candidates.length) {
      host.innerHTML = '<div class="me-empty">' + esc((result && result.error) || 'No matching releases found.') + '</div>';
      return;
    }
    host.innerHTML = candidates.map(function (c, index) {
      var bits = [];
      if (c.album) bits.push(c.album);
      if (c.year) bits.push(c.year);
      if (c.duration) bits.push(formatTime(c.duration));
      return '<div class="me-candidate" data-candidate="' + index + '">' +
        '<div class="me-candidate-art">' + (c.artworkUrl ? '<i class="fas fa-image"></i>' : '<i class="fas fa-music"></i>') + '</div>' +
        '<div class="me-candidate-main">' +
          '<div class="me-candidate-title">' + esc(c.title || 'Untitled') + '</div>' +
          '<div class="me-candidate-sub">' + esc(c.artist || 'Unknown artist') + ' · ' + esc(bits.join(' · ')) + '</div>' +
          '<div class="me-candidate-sub">' + esc(c.provider) + ' <span class="me-confidence is-' + esc(c.confidence) + '">' +
            esc(c.confidence || '') + ' ' + Math.round((c.score || 0) * 100) + '%</span></div>' +
        '</div>' +
        '<div class="me-candidate-actions">' +
          '<button class="me-primary" data-use-candidate="' + index + '">Use</button>' +
          (c.artworkUrl ? '<button class="me-btn" data-use-artwork="' + index + '">Artwork</button>' : '') +
        '</div>' +
      '</div>';
    }).join('');
    host._candidates = candidates;
    renderArtworkResults(candidates);
  }

  function clearCandidates() {
    el('me-candidates').innerHTML = '';
    el('me-artwork-results').innerHTML = '';
  }

  function applyCandidate(candidate) {
    setValue('me-title', candidate.title || '');
    setValue('me-artist', candidate.artist || '');
    setValue('me-album-artist', candidate.albumArtist || candidate.artist || '');
    setValue('me-album', candidate.album || '');
    setValue('me-genre', candidate.genre || '');
    setValue('me-year', candidate.year || '');
    setValue('me-track', candidate.track || '');
    setValue('me-disc', candidate.disc || '');
    toast('Metadata filled from ' + candidate.provider, 'success');
  }

  el('me-candidates').addEventListener('click', function (event) {
    var use = event.target.closest('[data-use-candidate]');
    if (use) {
      var candidate = el('me-candidates')._candidates[parseInt(use.getAttribute('data-use-candidate'), 10)];
      if (candidate) applyCandidate(candidate);
      return;
    }
    var art = event.target.closest('[data-use-artwork]');
    if (art) {
      var item = el('me-candidates')._candidates[parseInt(art.getAttribute('data-use-artwork'), 10)];
      if (!item || !item.artworkUrl) return;
      art.disabled = true;
      request('fetchSongArtwork', { url: item.artworkUrl }, 60000).then(function (result) {
        art.disabled = false;
        if (result && result.error) { toast(result.error, 'error'); return; }
        setArtwork(result.dataUrl, result.mimeType);
        toast('Artwork downloaded', 'success');
      }).catch(function (error) { art.disabled = false; toast(error.message, 'error'); });
    }
  });

  function runIdentify(searchOnly) {
    if (!state.songId) return;
    var query = str('me-search-query');
    var method = searchOnly && query ? 'searchSongMetadata' : 'identifySongMetadata';
    var params = { songId: state.songId };
    if (method === 'searchSongMetadata') params.query = query;
    el('me-identify').disabled = true;
    el('me-search').disabled = true;
    request(method, params, 60000).then(function (result) {
      el('me-identify').disabled = false;
      el('me-search').disabled = false;
      renderCandidates(result);
    }).catch(function (error) {
      el('me-identify').disabled = false;
      el('me-search').disabled = false;
      toast(error.message, 'error');
    });
  }

  el('me-identify').addEventListener('click', function () { runIdentify(false); });
  el('me-search').addEventListener('click', function () { runIdentify(true); });
  el('me-search-query').addEventListener('keydown', function (event) {
    if (event.key === 'Enter') runIdentify(true);
  });

  // ── Lyrics ──────────────────────────────────────────────
  function looksSynced(text) {
    return /\[(\d{1,2}:)?\d{1,2}:\d{2}(\.\d{1,3})?\]/.test(text || '');
  }

  function parseLrc(text) {
    var lines = [];
    String(text || '').split(/\r?\n/).forEach(function (raw) {
      var line = raw.trim();
      if (!line || line.charAt(0) !== '[') return;
      var close = line.indexOf(']');
      if (close < 2) return;
      var stamp = line.slice(1, close);
      var content = line.slice(close + 1).trim();
      var seconds = parseStamp(stamp);
      if (seconds == null) return;
      lines.push({ time: seconds, text: content });
    });
    lines.sort(function (a, b) { return a.time - b.time; });
    return lines;
  }

  function parseStamp(stamp) {
    var parts = String(stamp || '').split(':');
    if (parts.length < 2) return null;
    var seconds = parseFloat(parts[parts.length - 1].replace(',', '.'));
    var minutes = parseInt(parts[parts.length - 2], 10);
    if (!isFinite(seconds) || !isFinite(minutes)) return null;
    var hours = parts.length >= 3 ? parseInt(parts[0], 10) : 0;
    if (!isFinite(hours)) hours = 0;
    return hours * 3600 + minutes * 60 + seconds;
  }

  function formatTime(seconds) {
    if (!isFinite(seconds) || seconds < 0) seconds = 0;
    var total = Math.round(seconds * 100) / 100;
    var h = Math.floor(total / 3600);
    var m = Math.floor((total % 3600) / 60);
    var s = total % 60;
    var sec = (s < 10 ? '0' : '') + s.toFixed(2);
    return (h > 0 ? h + ':' + (m < 10 ? '0' : '') + m : m) + ':' + sec;
  }

  function buildLrc(lines) {
    return lines
      .filter(function (line) { return line; })
      .sort(function (a, b) { return a.time - b.time; })
      .map(function (line) {
        var seconds = Math.max(0, Number(line.time) || 0);
        var m = Math.floor(seconds / 60);
        var s = seconds % 60;
        return '[' + (m < 10 ? '0' : '') + m + ':' + (s < 10 ? '0' : '') + s.toFixed(2) + ']' + (line.text || '');
      }).join('\n');
  }

  function setLyricsMode(mode) {
    state.lyricsMode = mode;
    el('me-lyrics-mode-synced').classList.toggle('is-active', mode === 'synced');
    el('me-lyrics-mode-plain').classList.toggle('is-active', mode === 'plain');
    el('me-lyrics-synced').hidden = mode !== 'synced';
    el('me-lyrics-plain-wrap').hidden = mode !== 'plain';

    if (mode === 'plain') {
      el('me-lyrics-plain').value = state.plain || '';
      return;
    }

    // Switching from plain text to timed lyrics keeps the text as untimed rows.
    if (!state.lines.length && state.plain.trim()) {
      state.lines = state.plain.split(/\r?\n/)
        .map(function (text, index) { return { time: index * 4, text: text }; });
    }
  }

  function renderLyrics() {
    setLyricsMode(state.lyricsMode);
    if (state.lyricsMode !== 'synced') return;
    renderLineList();
    renderTimeline();
  }

  function renderLineList() {
    var host = el('me-lyrics-lines');
    el('me-lyrics-empty').hidden = state.lines.length > 0;
    host.innerHTML = state.lines.map(function (line, index) {
      return '<div class="me-lyric-row' + (state.activeLine === index ? ' is-active' : '') + '" data-line="' + index + '">' +
        '<input class="me-line-time" data-time="' + index + '" value="' + esc(formatTime(line.time)) + '" inputmode="decimal">' +
        '<input class="me-line-text" data-text="' + index + '" value="' + esc(line.text) + '" placeholder="Lyric line">' +
        '<div class="me-line-actions">' +
          '<button data-up="' + index + '" title="Move up"><i class="fas fa-chevron-up"></i></button>' +
          '<button data-down="' + index + '" title="Move down"><i class="fas fa-chevron-down"></i></button>' +
          '<button data-remove="' + index + '" title="Remove"><i class="fas fa-times"></i></button>' +
        '</div></div>';
    }).join('');
  }

  function renderTimeline() {
    var duration = state.duration > 0 ? state.duration
      : Math.max(60, (state.lines.length ? state.lines[state.lines.length - 1].time + 10 : 60));
    var track = el('me-timeline-track');
    var ruler = el('me-timeline-ruler');
    ruler.innerHTML = [0, 25, 50, 75, 100].map(function (pct) {
      return '<span style="left:' + pct + '%">' + esc(formatTime(duration * pct / 100)) + '</span>';
    }).join('');
    track.innerHTML = state.lines.map(function (line, index) {
      var pct = Math.max(0, Math.min(100, (Number(line.time) || 0) / duration * 100));
      return '<div class="me-marker' + (state.activeLine === index ? ' is-active' : '') +
        '" data-marker="' + index + '" style="left:' + pct + '%" title="' + esc(formatTime(line.time)) + '"></div>';
    }).join('');
    track.dataset.duration = String(duration);
  }

  function activateLine(index) {
    state.activeLine = index;
    renderLineList();
    renderTimeline();
  }

  el('me-lyrics-lines').addEventListener('input', function (event) {
    var time = event.target.closest('[data-time]');
    if (time) {
      var index = parseInt(time.getAttribute('data-time'), 10);
      var seconds = parseStamp(time.value);
      if (seconds != null && state.lines[index]) {
        state.lines[index].time = seconds;
        renderTimeline();
      }
      return;
    }
    var text = event.target.closest('[data-text]');
    if (text) {
      var i = parseInt(text.getAttribute('data-text'), 10);
      if (state.lines[i]) state.lines[i].text = text.value;
    }
  });

  el('me-lyrics-lines').addEventListener('focusin', function (event) {
    var row = event.target.closest('[data-line]');
    if (row) activateLine(parseInt(row.getAttribute('data-line'), 10));
  });

  el('me-lyrics-lines').addEventListener('click', function (event) {
    var up = event.target.closest('[data-up]');
    var down = event.target.closest('[data-down]');
    var remove = event.target.closest('[data-remove]');
    if (up) {
      var i = parseInt(up.getAttribute('data-up'), 10);
      if (i > 0) { var moved = state.lines.splice(i, 1)[0]; state.lines.splice(i - 1, 0, moved); activateLine(i - 1); }
    } else if (down) {
      var j = parseInt(down.getAttribute('data-down'), 10);
      if (j < state.lines.length - 1) { var m = state.lines.splice(j, 1)[0]; state.lines.splice(j + 1, 0, m); activateLine(j + 1); }
    } else if (remove) {
      var k = parseInt(remove.getAttribute('data-remove'), 10);
      state.lines.splice(k, 1);
      activateLine(Math.min(k, state.lines.length - 1));
    }
  });

  // Drag lyric markers along the timeline to retime them graphically.
  (function wireTimeline() {
    var track = el('me-timeline-track');
    var dragging = -1;

    function moveTo(clientX) {
      if (dragging < 0) return;
      var rect = track.getBoundingClientRect();
      var pct = Math.max(0, Math.min(1, (clientX - rect.left) / Math.max(1, rect.width)));
      var duration = Number(track.dataset.duration) || 60;
      state.lines[dragging].time = Math.round(duration * pct * 100) / 100;
      renderLineList();
      renderTimeline();
      activateLine(dragging);
    }

    track.addEventListener('pointerdown', function (event) {
      var marker = event.target.closest('[data-marker]');
      if (!marker) return;
      dragging = parseInt(marker.getAttribute('data-marker'), 10);
      try { track.setPointerCapture(event.pointerId); } catch (_) { }
      activateLine(dragging);
      event.preventDefault();
    });
    track.addEventListener('pointermove', function (event) {
      if (dragging >= 0) moveTo(event.clientX);
    });
    track.addEventListener('pointerup', function () { dragging = -1; });
    track.addEventListener('pointercancel', function () { dragging = -1; });
  })();

  el('me-lyrics-add').addEventListener('click', function () {
    if (state.lyricsMode !== 'synced') setLyricsMode('synced');
    var last = state.lines.length ? state.lines[state.lines.length - 1].time : 0;
    state.lines.push({ time: last + 4, text: '' });
    activateLine(state.lines.length - 1);
    setTimeout(function () {
      var input = document.querySelector('[data-text="' + (state.lines.length - 1) + '"]');
      if (input) input.focus();
    }, 20);
  });

  el('me-lyrics-sort').addEventListener('click', function () {
    state.lines.sort(function (a, b) { return a.time - b.time; });
    activateLine(-1);
  });

  el('me-lyrics-clear').addEventListener('click', function () {
    state.lines = [];
    state.plain = '';
    el('me-lyrics-plain').value = '';
    activateLine(-1);
  });

  el('me-lyrics-fetch').addEventListener('click', function () {
    if (!state.songId) return;
    el('me-lyrics-fetch').disabled = true;
    request('fetchSongLyrics', { songId: state.songId }, 60000).then(function (result) {
      el('me-lyrics-fetch').disabled = false;
      if (!result || !result.found) { toast((result && result.error) || 'No lyrics found', 'error'); return; }
      el('me-lyrics-source').textContent = 'Lyrics found via ' + (result.provider || 'online source');
      if (result.syncedLyrics && looksSynced(result.syncedLyrics)) {
        state.lines = parseLrc(result.syncedLyrics);
        state.lyricsMode = 'synced';
      } else {
        state.plain = result.plainLyrics || '';
        state.lines = [];
        state.lyricsMode = 'plain';
      }
      renderLyrics();
      toast('Lyrics fetched', 'success');
    }).catch(function (error) {
      el('me-lyrics-fetch').disabled = false;
      toast(error.message, 'error');
    });
  });

  el('me-lyrics-mode-synced').addEventListener('click', function () {
    setLyricsMode('synced');
    renderLineList();
    renderTimeline();
  });
  el('me-lyrics-mode-plain').addEventListener('click', function () {
    if (state.lyricsMode === 'synced')
      state.plain = state.lines.map(function (line) { return line.text; }).join('\n');
    setLyricsMode('plain');
  });
  el('me-lyrics-plain').addEventListener('input', function () { state.plain = this.value; });

  // ── Wiring ──────────────────────────────────────────────
  el('me-save').addEventListener('click', save);
  el('me-close').addEventListener('click', close);
  document.addEventListener('keydown', function (event) {
    if (event.key === 'Escape') close();
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') { event.preventDefault(); save(); }
  });

  // The host sends the selected song after the page is ready; if that event was
  // delivered before this script registered, the pending song is still answered
  // by getSongMetadata without an id.
  request('getSongMetadata', {}).then(function (m) {
    if (m && m.songId) load(m.songId);
  }).catch(function () { });

  setInterval(refreshProviderStatus, 5000);
})();
