// Shared player settings panel for the desktop music player window and the
// mobile music page. One source of truth for every playback / lyrics / karaoke /
// library-analysis / online-metadata preference.
(function (global) {
  'use strict';

  function esc(value) {
    return String(value == null ? '' : value)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function mount(opts) {
    var container = opts.container;
    var request = opts.request;
    var send = opts.send;
    var cfg = {};
    var devices = [];
    var selectedDevice = -1;
    var aiStatus = null;
    var analysisTimer = null;
    var providerStatusTimer = null;
    var providerStatus = {};
    var disposed = false;

    function toast(message, type) {
      if (typeof opts.toast === 'function') { opts.toast(message, type); return; }
      var host = document.getElementById('ps-toasts');
      if (!host) {
        host = document.createElement('div');
        host.id = 'ps-toasts';
        host.className = 'ps-toasts';
        document.body.appendChild(host);
      }
      var el = document.createElement('div');
      el.className = 'ps-toast' + (type ? ' is-' + type : '');
      el.textContent = message;
      host.appendChild(el);
      setTimeout(function () { el.classList.add('is-out'); }, 2200);
      setTimeout(function () { if (el.parentNode) el.remove(); }, 2800);
    }

    function $(id) { return container.querySelector('#' + id); }
    function val(id) { var e = $(id); return e ? e.value : ''; }
    function checked(id) { var e = $(id); return !!(e && e.checked); }
    function set(id, value) { var e = $(id); if (e) e.value = value == null ? '' : value; }
    function check(id, value) { var e = $(id); if (e) e.checked = !!value; }

    function saveOne(key, value) {
      var settings = {};
      settings[key] = value;
      return request('saveSettings', { settings: settings }, 20000);
    }

    var providerLabels = {
      MusicBrainz: 'MusicBrainz',
      iTunes: 'iTunes',
      Deezer: 'Deezer',
      CoverArtArchive: 'Cover Art Archive',
      AcoustID: 'AcoustID fingerprint',
      Discogs: 'Discogs'
    };
    var providerKeys = {
      MusicBrainz: 'metadataProviderMusicBrainz',
      iTunes: 'metadataProviderITunes',
      Deezer: 'metadataProviderDeezer',
      CoverArtArchive: 'metadataProviderCoverArtArchive',
      AcoustID: 'metadataProviderAcoustId',
      Discogs: 'metadataProviderDiscogs'
    };

    function priorityList() {
      var defaults = Object.keys(providerLabels);
      var source = Array.isArray(cfg.metadataProviderPriority) ? cfg.metadataProviderPriority : [];
      var result = [];
      source.forEach(function (name) {
        var match = defaults.find(function (d) { return d.toLowerCase() === String(name).toLowerCase(); });
        if (match && result.indexOf(match) < 0) result.push(match);
      });
      defaults.forEach(function (name) { if (result.indexOf(name) < 0) result.push(name); });
      return result;
    }

    function providerEnabled(name) {
      var key = providerKeys[name];
      if (!key) return false;
      if (name === 'MusicBrainz' || name === 'iTunes' || name === 'Deezer' || name === 'CoverArtArchive')
        return cfg[key] !== false;
      return cfg[key] === true;
    }

    function renderPriority() {
      var host = $('ps-provider-priority');
      if (!host) return;
      host.innerHTML = priorityList().map(function (name, index) {
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
        return '<div class="ps-priority-row" title="' + esc(title) + '">' +
          '<span class="ps-priority-index">' + (index + 1) + '</span>' +
          '<span class="ps-priority-name">' + esc(providerLabels[name]) +
            '<span class="ps-sub">' + esc((status && status.limitLabel) || '') + '</span></span>' +
          '<span class="ps-priority-state ' + className + '">' + esc(label) + '</span>' +
          '<button class="ps-btn" data-priority-up="' + index + '" aria-label="Move up"' + (index === 0 ? ' disabled' : '') + '>↑</button>' +
          '<button class="ps-btn" data-priority-down="' + index + '" aria-label="Move down"' + (index === priorityList().length - 1 ? ' disabled' : '') + '>↓</button>' +
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
      return request('getMetadataProviderStatus', {}, 15000).then(function (data) {
        providerStatus = {};
        ((data && data.providers) || []).forEach(function (p) {
          if (p && p.name) providerStatus[p.name] = p;
        });
        renderPriority();
      }).catch(function () { });
    }

    function movePriority(index, delta) {
      var list = priorityList();
      var next = index + delta;
      if (next < 0 || next >= list.length) return;
      var item = list.splice(index, 1)[0];
      list.splice(next, 0, item);
      cfg.metadataProviderPriority = list;
      saveOne('metadataProviderPriority', list).catch(function (e) { toast(e.message, 'error'); });
      renderPriority();
    }

    function render() {
      var playback = opts.getPlayback ? opts.getPlayback() : { volume: 0.8, crossfade: 4 };
      var deviceOptions = devices.map(function (d) {
        return '<option value="' + d.number + '"' + (d.number === selectedDevice ? ' selected' : '') + '>' +
          esc(d.name) + '</option>';
      }).join('');

      container.innerHTML =
        '<section class="ps-card">' +
          '<h3><i class="fas fa-sliders"></i> Playback</h3>' +
          rowRange('ps-volume', 'Volume', 'Main player volume', Math.round((playback.volume || 0) * 100), 0, 100, 1, Math.round((playback.volume || 0) * 100) + '%') +
          rowRange('ps-crossfade', 'Crossfade', 'Overlap between tracks', Number(playback.crossfade || 0), 0, 12, 0.5, Number(playback.crossfade || 0).toFixed(1) + ' s') +
          rowCheck('ps-normalize', 'Normalize volume', 'Even out loudness between tracks', cfg.normalizeVolume) +
          rowCheck('ps-fullscreen', 'Presentation fullscreen', 'Open presentation/HDMI screens in fullscreen', cfg.presentationFullscreen) +
          rowCheck('ps-auto-analyze', 'Analyse playing songs', 'BPM, chapters and drops on the progress bar', cfg.autoAnalyzeSongs !== false) +
          rowCheck('ps-karaoke', 'Karaoke mode', 'Syllable-by-syllable lyrics and pause bars', cfg.karaokeMode === true) +
          rowCheck('ps-play-karaoke', 'Play karaoke versions', 'Generate and play instrumental versions', cfg.playKaraokeVersions === true) +
          rowCheck('ps-caption', 'YouTube captions as lyrics fallback', 'Use captions when no lyrics provider has a match', cfg.useCaptionLyricsFallback) +
          rowCheck('ps-announcements', 'Announcements', 'Disable the announcement engine and its shortcut', cfg.enableAnnouncements) +
          rowCheck('ps-autopilot', 'Autopilot', 'Automatically add newly sent-in songs to the queue', cfg.autoEnqueue) +
        '</section>' +

        '<section class="ps-card">' +
          '<h3><i class="fas fa-align-left"></i> Lyrics</h3>' +
          '<div class="ps-row"><div class="ps-label">Text size' +
            '<span class="ps-sub">Applied to the player, presentation and mobile lyrics</span></div>' +
            '<button class="ps-btn" id="ps-lyrics-down" aria-label="Smaller lyrics"><i class="fas fa-minus"></i></button>' +
            '<span class="ps-value" id="ps-lyrics-value">' + Math.round((cfg.lyricsFontScale || 1) * 100) + '%</span>' +
            '<button class="ps-btn" id="ps-lyrics-up" aria-label="Larger lyrics"><i class="fas fa-plus"></i></button>' +
            '<button class="ps-btn" id="ps-lyrics-reset">Reset</button>' +
          '</div>' +
          rowCheck('ps-lyrics-hidden', 'Hide lyrics by default', 'Open the player without the lyrics panel', cfg.lyricsHidden) +
        '</section>' +

        '<section class="ps-card">' +
          '<h3><i class="fas fa-headphones"></i> Audio output</h3>' +
          '<div class="ps-row"><div class="ps-label">Output device' +
            '<span class="ps-sub">Used for the current and next tracks</span></div>' +
            '<select id="ps-device">' + (deviceOptions || '<option value="-1">System default</option>') + '</select></div>' +
          '<div class="ps-btn-row">' +
            '<button class="ps-btn" data-ps-action="equalizer"><i class="fas fa-sliders"></i> Equalizer</button>' +
            '<button class="ps-btn" data-ps-action="sleep"><i class="fas fa-moon"></i> Sleep timer</button>' +
            '<button class="ps-btn" data-ps-action="analysis"><i class="fas fa-chart-line"></i> Analyse current track</button>' +
          '</div>' +
        '</section>' +

        '<section class="ps-card ps-ai-card' + (cfg.enhancedVocalRemoval ? ' is-active' : '') + '" id="ps-ai-card">' +
          '<h3><i class="fas fa-wand-magic-sparkles"></i> Enhanced vocal removal <span class="ps-value">AI</span></h3>' +
          rowCheck('ps-ai-toggle', 'Use a local AI model for karaoke separation', 'Runs on this device; no audio is uploaded', cfg.enhancedVocalRemoval === true) +
          '<div class="ps-note" id="ps-ai-engine">Checking models…</div>' +
          '<div class="ps-row"><div class="ps-label">Model<span class="ps-sub">Fast models are best on older devices</span></div>' +
            '<select id="ps-ai-model"></select></div>' +
          '<div class="ps-note" id="ps-ai-info"></div>' +
          '<div class="ps-btn-row">' +
            '<button class="ps-btn" id="ps-ai-download"><i class="fas fa-download"></i> Download model</button>' +
            '<button class="ps-btn is-danger" id="ps-ai-delete">Delete model</button>' +
            '<button class="ps-btn" id="ps-ai-disclaimer-open">Read disclaimer</button>' +
          '</div>' +
          '<div class="ps-note" id="ps-ai-progress"></div>' +
          '<div class="ps-disclaimer" id="ps-ai-disclaimer" hidden>' +
            '<strong>AI vocal removal is experimental, and results vary by song, mix and model.</strong>' +
            '<ul><li>Vocals may remain audible, or instruments may be removed by mistake.</li>' +
            '<li>Separation can introduce artifacts, hollow instruments or distorted vocals.</li>' +
            '<li>Always listen to the generated version before using it live.</li>' +
            '<li>Models are downloaded from third-party sources and run locally.</li>' +
            '<li>You are responsible for checking the result and for having the rights to use the source song.</li></ul>' +
            '<label class="ps-check"><input type="checkbox" id="ps-ai-accept"> I have read this and understand the limitations.</label>' +
          '</div>' +
        '</section>' +

        '<section class="ps-card">' +
          '<h3><i class="fas fa-chart-simple"></i> Library analysis</h3>' +
          rowCheck('ps-lib-add', 'Analyse new library songs', 'When songs are imported or scanned', cfg.autoAnalyzeLibraryOnAdd !== false) +
          rowCheck('ps-lib-version', 'Re-analyse after algorithm updates', 'Keeps BPM/chapter data current', cfg.autoAnalyzeLibraryOnAlgorithmUpdate !== false) +
          '<div class="ps-progress"><i id="ps-lib-fill"></i></div>' +
          '<div class="ps-note" id="ps-lib-status">Idle</div>' +
          '<div class="ps-btn-row">' +
            '<button class="ps-btn" id="ps-lib-start"><i class="fas fa-play"></i> Analyse library</button>' +
            '<button class="ps-btn is-danger" id="ps-lib-stop" hidden><i class="fas fa-stop"></i> Stop</button>' +
          '</div>' +
        '</section>' +

        '<section class="ps-card">' +
          '<h3><i class="fas fa-globe"></i> Online metadata sources</h3>' +
          '<div class="ps-note">Used by Edit Metadata → Identify and artwork lookup. No audio is uploaded.</div>' +
          rowCheck('ps-prov-mb', 'MusicBrainz', 'Open music encyclopedia', cfg.metadataProviderMusicBrainz !== false) +
          rowCheck('ps-prov-itunes', 'iTunes', 'Apple catalogue and artwork', cfg.metadataProviderITunes !== false) +
          rowCheck('ps-prov-deezer', 'Deezer', 'Deezer catalogue and artwork', cfg.metadataProviderDeezer !== false) +
          rowCheck('ps-prov-caa', 'Cover Art Archive', 'Cover artwork matched through MusicBrainz', cfg.metadataProviderCoverArtArchive !== false) +
          rowCheck('ps-prov-acoustid', 'AcoustID fingerprint', 'Identify untagged files by audio fingerprint (requires fpcalc and a key)', cfg.metadataProviderAcoustId === true) +
          rowCheck('ps-prov-discogs', 'Discogs', 'Community release database (token optional, raises the rate limit)', cfg.metadataProviderDiscogs === true) +
          '<div class="ps-key-row"><input id="ps-acoustid-key" type="password" placeholder="AcoustID application key" value="' +
            esc(cfg.acoustIdApiKey || '') + '"><button class="ps-btn" id="ps-acoustid-save">Save key</button></div>' +
          '<div class="ps-key-row"><input id="ps-discogs-token" type="password" placeholder="Discogs personal token (optional)" value="' +
            esc(cfg.discogsToken || '') + '"><button class="ps-btn" id="ps-discogs-save">Save token</button></div>' +
          '<div class="ps-priority-title">Lookup priority <span class="ps-sub">First provider wins when a confident match is found</span></div>' +
          '<div id="ps-provider-priority" class="ps-priority"></div>' +
        '</section>' +

        '<section class="ps-card">' +
          '<h3><i class="fas fa-toolbox"></i> Tools</h3>' +
          '<div class="ps-btn-row">' +
            '<button class="ps-btn" data-ps-action="presentation"><i class="fas fa-tv"></i> Presentation</button>' +
            '<button class="ps-btn" data-ps-action="visualization"><i class="fas fa-wave-square"></i> Visualization</button>' +
            '<button class="ps-btn" data-ps-action="share"><i class="fas fa-tower-broadcast"></i> Music Share</button>' +
            '<button class="ps-btn" data-ps-action="announcements"><i class="fas fa-microphone"></i> Announcements</button>' +
          '</div>' +
        '</section>';

      wire();
      renderPriority();
      updateLyricsValue();
      var aiToggle = $('ps-ai-toggle');
      if (aiToggle && aiToggle.checked && !cfg.aiDisclaimerAccepted) {
        aiToggle.checked = false;
        var aiDisclaimer = $('ps-ai-disclaimer');
        if (aiDisclaimer) aiDisclaimer.hidden = false;
      }
      refreshAiStatus();
    }

    function rowRange(id, label, sub, value, min, max, step, display) {
      return '<div class="ps-row"><div class="ps-label">' + esc(label) +
        '<span class="ps-sub">' + esc(sub) + '</span></div>' +
        '<input type="range" id="' + id + '" min="' + min + '" max="' + max + '" step="' + step + '" value="' + value + '">' +
        '<span class="ps-value" id="' + id + '-value">' + esc(display) + '</span></div>';
    }

    function rowCheck(id, label, sub, value) {
      return '<label class="ps-check"><input type="checkbox" id="' + id + '"' + (value ? ' checked' : '') + '>' +
        '<span>' + esc(label) + '<span class="ps-sub">' + esc(sub) + '</span></span></label>';
    }

    function updateLyricsValue() {
      var el = $('ps-lyrics-value');
      if (el) el.textContent = Math.round((cfg.lyricsFontScale || 1) * 100) + '%';
    }

    function applyLyricsSettings() {
      request('saveLyricsSettings', {
        fontScale: cfg.lyricsFontScale || 1,
        hidden: !!cfg.lyricsHidden,
      }, 20000).catch(function () { });
      if (opts.onLyricsChanged) opts.onLyricsChanged(cfg.lyricsFontScale || 1, !!cfg.lyricsHidden);
      updateLyricsValue();
    }

    function wire() {
      var volume = $('ps-volume');
      if (volume) volume.addEventListener('input', function () {
        var value = parseFloat(this.value) / 100;
        var out = $('ps-volume-value'); if (out) out.textContent = this.value + '%';
        send('setVolume', { value: value });
        if (opts.onVolume) opts.onVolume(value);
      });
      var crossfade = $('ps-crossfade');
      if (crossfade) crossfade.addEventListener('input', function () {
        var value = parseFloat(this.value);
        var out = $('ps-crossfade-value'); if (out) out.textContent = value.toFixed(1) + ' s';
        send('setCrossfade', { value: value });
        if (opts.onCrossfade) opts.onCrossfade(value);
      });

      bindCheck('ps-normalize', 'normalizeVolume');
      bindCheck('ps-fullscreen', 'presentationFullscreen');
      bindCheck('ps-auto-analyze', 'autoAnalyzeSongs', function (value) {
        if (value && opts.refreshAnalysis) opts.refreshAnalysis();
      });
      bindCheck('ps-karaoke', 'karaokeMode', function (value) {
        if (opts.onKaraokeMode) opts.onKaraokeMode(value);
      });
      bindCheck('ps-play-karaoke', 'playKaraokeVersions');
      bindCheck('ps-caption', 'useCaptionLyricsFallback');
      bindCheck('ps-announcements', 'enableAnnouncements');
      bindCheck('ps-autopilot', 'autoEnqueue');
      bindCheck('ps-lyrics-hidden', 'lyricsHidden', function (value) {
        cfg.lyricsHidden = value; applyLyricsSettings();
      });
      bindCheck('ps-lib-add', 'autoAnalyzeLibraryOnAdd');
      bindCheck('ps-lib-version', 'autoAnalyzeLibraryOnAlgorithmUpdate');
      bindCheck('ps-prov-mb', 'metadataProviderMusicBrainz');
      bindCheck('ps-prov-itunes', 'metadataProviderITunes');
      bindCheck('ps-prov-deezer', 'metadataProviderDeezer');
      bindCheck('ps-prov-caa', 'metadataProviderCoverArtArchive');
      bindCheck('ps-prov-acoustid', 'metadataProviderAcoustId');
      bindCheck('ps-prov-discogs', 'metadataProviderDiscogs');

      var down = $('ps-lyrics-down');
      if (down) down.addEventListener('click', function () {
        cfg.lyricsFontScale = Math.max(0.6, Math.round(((cfg.lyricsFontScale || 1) - 0.1) * 10) / 10);
        applyLyricsSettings();
      });
      var up = $('ps-lyrics-up');
      if (up) up.addEventListener('click', function () {
        cfg.lyricsFontScale = Math.min(2.0, Math.round(((cfg.lyricsFontScale || 1) + 0.1) * 10) / 10);
        applyLyricsSettings();
      });
      var reset = $('ps-lyrics-reset');
      if (reset) reset.addEventListener('click', function () {
        cfg.lyricsFontScale = 1.0; applyLyricsSettings();
      });

      var device = $('ps-device');
      if (device) device.addEventListener('change', function () {
        var number = parseInt(this.value, 10);
        selectedDevice = isNaN(number) ? -1 : number;
        send('setMusicOutputDevice', { deviceNumber: selectedDevice });
        if (opts.onDevice) opts.onDevice(selectedDevice);
      });

      container.onclick = function (event) {
        var up = event.target.closest('[data-priority-up]');
        if (up) { movePriority(parseInt(up.getAttribute('data-priority-up'), 10), -1); return; }
        var down = event.target.closest('[data-priority-down]');
        if (down) { movePriority(parseInt(down.getAttribute('data-priority-down'), 10), 1); return; }
        var action = event.target.closest('[data-ps-action]');
        if (!action) return;
        var name = action.getAttribute('data-ps-action');
        if (name === 'equalizer') { if (opts.openEqualizer) opts.openEqualizer(); }
        else if (name === 'sleep') { if (opts.openSleepTimer) opts.openSleepTimer(); }
        else if (name === 'analysis') { if (opts.openAnalysis) opts.openAnalysis(); }
        else if (name === 'presentation') send('showPresentation');
        else if (name === 'visualization') send('showVisualization');
        else if (name === 'share') {
          var share = opts.openMusicShare || opts.onMusicShare;
          if (share) share(); else send('showMusicPlayer');
        }
        else if (name === 'announcements') send('announcementOpen');
      };

      var aiToggle = $('ps-ai-toggle');
      if (aiToggle) aiToggle.addEventListener('change', function () {
        var card = $('ps-ai-card');
        if (card) card.classList.toggle('is-active', this.checked);
        if (this.checked && !cfg.aiDisclaimerAccepted) {
          this.checked = false;
          var disclaimer = $('ps-ai-disclaimer');
          if (disclaimer) disclaimer.hidden = false;
          toast('Read and accept the AI disclaimer first', 'error');
          return;
        }
        saveOne('enhancedVocalRemoval', this.checked).catch(function (e) { toast(e.message, 'error'); });
      });
      var accept = $('ps-ai-accept');
      if (accept) {
        accept.checked = cfg.aiDisclaimerAccepted === true;
        accept.addEventListener('change', function () {
          cfg.aiDisclaimerAccepted = this.checked;
          saveOne('aiDisclaimerAccepted', this.checked).catch(function () { });
        });
      }
      var openDisclaimer = $('ps-ai-disclaimer-open');
      if (openDisclaimer) openDisclaimer.addEventListener('click', function () {
        var d = $('ps-ai-disclaimer'); if (d) d.hidden = false;
      });
      var aiModel = $('ps-ai-model');
      if (aiModel) aiModel.addEventListener('change', function () {
        saveOne('aiVocalModelId', this.value).catch(function () { });
        updateAiInfo();
      });
      var aiDownload = $('ps-ai-download');
      if (aiDownload) aiDownload.addEventListener('click', function () {
        var id = aiModel ? aiModel.value : '';
        if (!id) return;
        var progress = $('ps-ai-progress');
        if (progress) progress.textContent = 'Starting download…';
        request('downloadAiVocalModel', { modelId: id }, 60 * 60 * 1000).catch(function (e) {
          if (progress) progress.textContent = 'Error: ' + e.message;
        });
      });
      var aiDelete = $('ps-ai-delete');
      if (aiDelete) aiDelete.addEventListener('click', function () {
        var id = aiModel ? aiModel.value : '';
        if (!id) return;
        request('deleteAiVocalModel', { modelId: id }, 60000).then(function () {
          toast('AI model deleted', 'success');
          refreshAiStatus();
        }).catch(function (e) { toast(e.message, 'error'); });
      });

      var acoustKeySave = $('ps-acoustid-save');
      if (acoustKeySave) acoustKeySave.addEventListener('click', function () {
        var key = $('ps-acoustid-key');
        saveOne('acoustIdApiKey', key ? key.value.trim() : '').then(function () {
          toast('AcoustID key saved', 'success');
        }).catch(function (e) { toast(e.message, 'error'); });
      });
      var discogsSave = $('ps-discogs-save');
      if (discogsSave) discogsSave.addEventListener('click', function () {
        var token = $('ps-discogs-token');
        saveOne('discogsToken', token ? token.value.trim() : '').then(function () {
          toast('Discogs token saved', 'success');
        }).catch(function (e) { toast(e.message, 'error'); });
      });

      var libStart = $('ps-lib-start');
      if (libStart) libStart.addEventListener('click', function () {
        request('startLibraryAnalysis', { force: false }, 60000).then(function (r) {
          if (r && r.error) toast(r.error, 'error'); else refreshAnalysis();
        }).catch(function (e) { toast(e.message, 'error'); });
      });
      var libStop = $('ps-lib-stop');
      if (libStop) libStop.addEventListener('click', function () {
        request('stopLibraryAnalysis', {}, 60000).then(function () { refreshAnalysis(); })
          .catch(function (e) { toast(e.message, 'error'); });
      });
    }

    function bindCheck(id, key, after) {
      var el = $(id);
      if (!el) return;
      el.addEventListener('change', function () {
        saveOne(key, this.checked).catch(function (e) { toast(e.message, 'error'); });
        if (after) after(this.checked);
      });
    }

    function updateAiInfo() {
      if (!aiStatus || !aiStatus.models) return;
      var select = $('ps-ai-model');
      var info = $('ps-ai-info');
      var remove = $('ps-ai-delete');
      if (!select || !info) return;
      var model = aiStatus.models.find(function (m) { return m.id === select.value; }) || aiStatus.models[0];
      if (!model) { info.textContent = ''; if (remove) remove.disabled = true; return; }
      info.textContent = model.description + ' · ' + model.ratingLabel + ' · License: ' + model.license +
        (model.installed ? ' · Installed' : ' · Not installed');
      if (remove) remove.disabled = !model.installed;
    }

    function refreshAiStatus() {
      request('getAiVocalModels', {}, 20000).then(function (status) {
        aiStatus = status || {};
        var engine = $('ps-ai-engine');
        var toggle = $('ps-ai-toggle');
        var select = $('ps-ai-model');
        var download = $('ps-ai-download');
        if (!select) return;
        var supported = status && status.supported !== false;
        if (engine) engine.textContent = supported
          ? 'Engine ready · ' + (status.backend || 'local AI') +
            (status.installedBytes ? ' · ' + (status.installedBytes / (1024 * 1024)).toFixed(1) + ' MB installed' : ' · no models installed')
          : 'Local AI vocal separation is not available on this platform. The built-in separator is used.';
        if (toggle) toggle.disabled = !supported;
        if (download) download.disabled = !supported;
        var models = (status && status.models) || [];
        select.innerHTML = models.map(function (m) {
          return '<option value="' + esc(m.id) + '">' + esc(m.name) + ' · ' + esc(m.sizeLabel) + '</option>';
        }).join('');
        var wanted = status && (status.selectedModelId || cfg.aiVocalModelId);
        if (wanted && models.some(function (m) { return m.id === wanted; })) select.value = wanted;
        updateAiInfo();
      }).catch(function () {
        var engine = $('ps-ai-engine');
        if (engine) engine.textContent = 'Could not read AI model status.';
      });
    }

    function refreshAnalysis() {
      request('getLibraryAnalysisStatus', {}, 15000).then(function (data) {
        var fill = $('ps-lib-fill');
        var status = $('ps-lib-status');
        var start = $('ps-lib-start');
        var stop = $('ps-lib-stop');
        if (!fill || !status) return;
        var running = !!(data && data.isRunning);
        var total = (data && data.total) || 0;
        var completed = (data && data.completed) || 0;
        var pct = total > 0 ? Math.round((completed / total) * 100) : Math.round(((data && data.progress) || 0) * 100);
        fill.style.width = Math.max(0, Math.min(100, pct)) + '%';
        status.textContent = running
          ? (data.pausedForPlayback ? 'Paused while music plays' : 'Analysing library…') + ' ' + pct + '%'
          : data && data.status === 'completed' ? 'Analysis complete' : 'Idle';
        if (start) start.hidden = running;
        if (stop) stop.hidden = !running;
      }).catch(function () { });
    }

    function refresh() {
      return request('getSettings', {}, 20000).then(function (loaded) {
        cfg = loaded || {};
        return Promise.all([
          request('getMusicAudioDevices', {}, 15000).catch(function () { return {}; }),
          opts.getPlayback ? Promise.resolve() : Promise.resolve(),
        ]);
      }).then(function (result) {
        var deviceData = result && result[0] ? result[0] : {};
        devices = (deviceData && deviceData.devices) || [];
        selectedDevice = deviceData && deviceData.selected != null ? deviceData.selected : -1;
        render();
        refreshAnalysis();
        refreshProviderStatus();
        if (!disposed) {
          if (analysisTimer) clearInterval(analysisTimer);
          analysisTimer = setInterval(function () {
            if (container.offsetParent === null) return;
            refreshAnalysis();
          }, 3000);
          if (providerStatusTimer) clearInterval(providerStatusTimer);
          providerStatusTimer = setInterval(function () {
            if (container.offsetParent === null) return;
            refreshProviderStatus();
          }, 4000);
        }
      }).catch(function (error) {
        container.innerHTML = '<div class="ps-card"><h3>Player settings unavailable</h3><p class="ps-note">' +
          esc(error.message) + '</p></div>';
      });
    }

    function destroy() {
      disposed = true;
      if (analysisTimer) { clearInterval(analysisTimer); analysisTimer = null; }
      if (providerStatusTimer) { clearInterval(providerStatusTimer); providerStatusTimer = null; }
    }

    refresh();

    return {
      refresh: refresh,
      destroy: destroy,
      refreshAnalysis: refreshAnalysis,
      refreshAi: refreshAiStatus,
      onAiProgress: function (data) {
        var progress = $('ps-ai-progress');
        if (!progress || !data) return;
        var pct = data.percent ? ' ' + Math.round(data.percent) + '%' : '';
        progress.textContent = (data.message || data.stage || '') + pct;
        if (data.stage === 'done' || data.stage === 'error') refreshAiStatus();
      },
    };
  }

  global.SongRequestPlayerSettings = { mount: mount };
})(typeof window !== 'undefined' ? window : globalThis);
