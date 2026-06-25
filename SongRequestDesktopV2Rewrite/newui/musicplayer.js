(function() {
  'use strict';

  // --- Sidebar tab switching ---
  document.querySelectorAll('.sidebar-tab').forEach(btn => {
    btn.addEventListener('click', function() {
      document.querySelectorAll('.sidebar-tab').forEach(b => b.classList.remove('active'));
      document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
      this.classList.add('active');
      document.getElementById('view-' + this.dataset.view).classList.add('active');
    });
  });

  // --- Elements ---
  const playerBar = document.getElementById('player-bar');
  const expandBtn = document.getElementById('btn-expand');
  const collapseBtn = document.getElementById('btn-exp-collapse');
  const leftSidebar = document.getElementById('left-sidebar');
  const rightSidebar = document.getElementById('right-sidebar');
  const topbar = document.getElementById('topbar');
  const body = document.getElementById('body');

  // --- Expand / collapse ---
  function setExpanded(expanded) {
    playerBar.classList.toggle('expanded', expanded);
    expandBtn.innerHTML = expanded ? '<i class="fas fa-chevron-down"></i>' : '<i class="fas fa-chevron-up"></i>';
    if (expanded) {
      leftSidebar.classList.add('collapsed');
      rightSidebar.classList.add('collapsed');
      topbar.style.display = 'none';
      body.style.display = 'none';
      startLyricsTimer();
    } else {
      leftSidebar.classList.remove('collapsed');
      rightSidebar.classList.remove('collapsed');
      topbar.style.display = '';
      body.style.display = '';
      stopLyricsTimer();
    }
  }

  expandBtn.addEventListener('click', () => setExpanded(true));
  collapseBtn.addEventListener('click', () => setExpanded(false));

  // --- Play button toggle (sync all play buttons) ---
  const playBtns = [
    document.getElementById('btn-play'),
    document.getElementById('btn-exp-play')
  ];
  playBtns.forEach(btn => {
    if (!btn) return;
    btn.addEventListener('click', function() {
      const playing = this.classList.toggle('playing');
      const icon = playing ? 'pause' : 'play';
      this.innerHTML = `<i class="fas fa-${icon}"></i>`;
      playBtns.forEach(b => {
        if (b && b !== this) {
          b.classList.toggle('playing', playing);
          b.innerHTML = `<i class="fas fa-${icon}"></i>`;
        }
      });
      if (playing) resumeLyricsTimer(); else pauseLyricsTimer();
    });
  });

  // --- Volume sync (collapsed + expanded) ---
  const volSliders = [
    document.getElementById('volume-slider'),
    document.getElementById('exp-volume-slider')
  ];
  const volIcons = [
    document.getElementById('btn-volume')?.querySelector('i'),
    document.getElementById('btn-exp-volume')?.querySelector('i')
  ];
  volSliders.forEach(slider => {
    if (!slider) return;
    const updateAll = () => {
      const vol = slider.value;
      const iconName = vol == 0 ? 'fa-volume-mute' : vol < 50 ? 'fa-volume-down' : 'fa-volume-up';
      volSliders.forEach(s => { if (s && s !== slider) s.value = vol; });
      volIcons.forEach(icon => { if (icon) icon.className = `fas ${iconName}`; });
    };
    slider.addEventListener('input', updateAll);
  });

  // --- Timed lyrics ---
  const lyricsContainer = document.getElementById('expanded-lyrics');
  const lyricLines = Array.from(document.querySelectorAll('.lyrics-line'));
  let lyricsTimer = null;
  let lyricsPaused = false;
  let currentLineIndex = -1;

  function startLyricsTimer() {
    if (lyricLines.length === 0) return;
    currentLineIndex = -1;
    lyricsPaused = false;
    lyricLines.forEach(el => { el.className = 'lyrics-line'; });
    advanceToNextLine();
  }

  function advanceToNextLine() {
    if (lyricsPaused) return;
    lyricLines.forEach(el => { el.className = 'lyrics-line'; });

    currentLineIndex++;
    if (currentLineIndex >= lyricLines.length) {
      currentLineIndex = 0;
    }

    if (currentLineIndex > 0) {
      lyricLines[currentLineIndex - 1].classList.add('prev');
    }

    lyricLines[currentLineIndex].classList.add('active');
    lyricLines[currentLineIndex].scrollIntoView({ behavior: 'smooth', block: 'center' });

    const timeStr = lyricLines[currentLineIndex].dataset.time;
    const duration = timeStr ? parseFloat(timeStr) * 1000 : 3000;

    const nextTimeStr = currentLineIndex + 1 < lyricLines.length ? lyricLines[currentLineIndex + 1].dataset.time : null;
    let delay = duration;
    if (nextTimeStr) {
      const currentTime = parseFloat(timeStr || '0');
      const nextTime = parseFloat(nextTimeStr);
      delay = (nextTime - currentTime) * 1000;
    }

    lyricsTimer = setTimeout(() => {
      advanceToNextLine();
    }, Math.max(delay, 1500));
  }

  function pauseLyricsTimer() {
    lyricsPaused = true;
    if (lyricsTimer) { clearTimeout(lyricsTimer); lyricsTimer = null; }
  }

  function resumeLyricsTimer() {
    if (!lyricsPaused) return;
    lyricsPaused = false;
    advanceToNextLine();
  }

  function stopLyricsTimer() {
    lyricsPaused = true;
    if (lyricsTimer) { clearTimeout(lyricsTimer); lyricsTimer = null; }
    lyricLines.forEach(el => { el.className = 'lyrics-line'; });
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

})();
