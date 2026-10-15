(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const base = location.pathname.replace(/\/+$/, '');
  const store = {
    get(k, d) { try { const v = localStorage.getItem('vision-' + k); return v == null ? d : v; } catch { return d; } },
    set(k, v) { try { localStorage.setItem('vision-' + k, v); } catch {} },
  };

  // ---------- Theme & viewport ----------
  const dark = matchMedia('(prefers-color-scheme: dark)');
  const applyTheme = () => { document.documentElement.dataset.theme = dark.matches ? 'dark' : 'light'; };
  applyTheme();
  dark.addEventListener?.('change', applyTheme);

  const app = $('app');
  function syncViewport() {
    const vv = window.visualViewport;
    if (!vv) return;
    app.style.setProperty('--vvh', vv.height + 'px');
    app.style.setProperty('--vvtop', vv.offsetTop + 'px');
    requestAnimationFrame(layoutFrame);
  }
  window.visualViewport?.addEventListener('resize', syncViewport);
  window.visualViewport?.addEventListener('scroll', syncViewport);
  window.addEventListener('resize', syncViewport);
  document.addEventListener('gesturestart', (e) => e.preventDefault());
  syncViewport();

  // ---------- Views ----------
  const views = ['loadView', 'pinView', 'msgView', 'ctrlView'];
  function show(id) { views.forEach((v) => { $(v).hidden = v !== id; }); }

  function message(title, text, { icon = 'link-2-off', violet = false, button = null, onButton = null } = {}) {
    stopSocket();
    $('msgTitle').textContent = title;
    $('msgText').textContent = text;
    $('msgIcon').className = 'msg-icon' + (violet ? ' violet' : '');
    $('msgIcon').innerHTML = `<i class="ca-icon icon-${icon}"></i>`;
    const btn = $('msgBtn');
    btn.hidden = !button;
    if (button) { btn.textContent = button; btn.onclick = onButton; }
    show('msgView');
  }
  const expired = (text) => message('Lien expiré',
    text || 'se7en Vision crée un nouveau lien à chaque ouverture. Scanne le QR code affiché sur le PC.');

  // ---------- Startup ----------
  async function boot() {
    show('loadView');
    let res;
    try { res = await fetch(base + '/me', { cache: 'no-store', credentials: 'same-origin' }); }
    catch { return message('PC injoignable', 'Vérifie ta connexion, puis réessaie.', { icon: 'wifi-off', button: 'Réessayer', onButton: boot }); }
    if (res.status === 404) return expired();
    if (!res.ok) return message('PC injoignable', 'se7en Vision est peut-être fermé sur le PC.', { icon: 'wifi-off', button: 'Réessayer', onButton: boot });
    const me = await res.json();
    if (me.authed) startControl(); else showPin();
  }

  // ---------- PIN ----------
  const pinInput = $('pinInput');
  const boxes = [...document.querySelectorAll('#pinBoxes .box')];
  function renderPin() {
    const v = pinInput.value;
    boxes.forEach((b, i) => { b.textContent = v[i] || ''; b.classList.toggle('cur', i === Math.min(v.length, 5) && document.activeElement === pinInput); });
    $('pinBtn').disabled = v.length !== 6;
  }
  pinInput.addEventListener('input', () => {
    pinInput.value = pinInput.value.replace(/\D/g, '').slice(0, 6);
    $('pinMsg').textContent = '';
    $('pinMsg').classList.remove('err');
    renderPin();
    if (pinInput.value.length === 6) submitPin();
  });
  pinInput.addEventListener('focus', renderPin);
  pinInput.addEventListener('blur', renderPin);
  $('pinBtn').addEventListener('click', submitPin);
  function showPin() {
    show('pinView');
    pinInput.value = '';
    renderPin();
    setTimeout(() => pinInput.focus(), 50);
  }
  let submitting = false;
  async function submitPin() {
    if (submitting || pinInput.value.length !== 6) return;
    submitting = true;
    $('pinBtn').disabled = true;
    $('pinMsg').textContent = 'Vérification…';
    $('pinMsg').classList.remove('err');
    try {
      const res = await fetch(base + '/pin', {
        method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pin: pinInput.value }),
      });
      if (res.status === 404) return expired();
      const r = await res.json();
      if (r.result === 'ok') { pinInput.blur(); startControl(); return; }
      if (r.result === 'locked') return expired('Trop de codes faux : le lien a été désactivé. Un nouveau QR code est affiché sur le PC.');
      if (r.result === 'busy') {
        return message('Déjà utilisé', 'Un autre appareil contrôle ce PC en ce moment.', { icon: 'smartphone', button: 'Réessayer', onButton: boot });
      }
      $('pinMsg').textContent = `Code incorrect. Encore ${r.attemptsLeft} essai${r.attemptsLeft > 1 ? 's' : ''}.`;
      $('pinMsg').classList.add('err');
      const el = $('pinBoxes');
      el.classList.remove('shake'); void el.offsetWidth; el.classList.add('shake');
      pinInput.value = '';
      renderPin();
    } catch {
      $('pinMsg').textContent = 'Connexion perdue. Réessaie.';
      $('pinMsg').classList.add('err');
    } finally {
      submitting = false;
      renderPin();
    }
  }

  // ---------- Socket ----------
  let ws = null, retryTimer = 0, pingTimer = 0, alive = false;
  const QUALITY = { fluid: { w: 960, q: 45 }, balanced: { w: 1280, q: 55 }, sharp: { w: 1920, q: 70 } };
  let quality = store.get('quality', 'balanced');
  if (!QUALITY[quality]) quality = 'balanced';

  function send(obj) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj)); }

  function stopSocket() {
    clearTimeout(retryTimer);
    clearInterval(pingTimer);
    alive = false;
    if (ws) { ws.onclose = ws.onmessage = ws.onerror = null; try { ws.close(); } catch {} ws = null; }
  }

  function startControl() {
    show('ctrlView');
    requestAnimationFrame(layoutFrame);
    connect();
  }

  function connect() {
    stopSocket();
    setLive(false, 'Connexion…');
    const url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + base + '/ws';
    ws = new WebSocket(url);
    ws.binaryType = 'blob';
    ws.onopen = () => {
      alive = true;
      setLive(true, '');
      requestFrame();
      pingTimer = setInterval(() => send({ t: 'ping', ts: performance.now() }), 2000);
    };
    ws.onmessage = (e) => {
      if (typeof e.data !== 'string') return showFrame(e.data);
      const m = JSON.parse(e.data);
      if (m.t === 'info') onInfo(m);
      else if (m.t === 'same') { framePending = false; setTimeout(requestFrame, m.blocked ? 1000 : 120); }
      else if (m.t === 'pong') setLive(true, Math.round(performance.now() - m.ts) + ' ms');
    };
    ws.onclose = (e) => {
      ws = null; alive = false; framePending = false;
      clearInterval(pingTimer);
      if (e.code === 4001) return message('Accès fermé', 'Le PC a coupé l’accès. Scanne le nouveau QR code affiché sur l’écran.', { icon: 'unplug' });
      if (e.code === 4002) return message('Ouvert ailleurs', 'se7en Vision a été ouvert dans un autre onglet.', { icon: 'copy', violet: true, button: 'Reprendre ici', onButton: startControl });
      setLive(false, 'Reconnexion…');
      retryTimer = setTimeout(reconnect, 1500);
    };
  }

  async function reconnect() {
    try {
      const res = await fetch(base + '/me', { cache: 'no-store', credentials: 'same-origin' });
      if (res.status === 404) return expired();
      if (res.ok && !(await res.json()).authed) return showPin();
    } catch {}
    if (!$('ctrlView').hidden) connect();
  }

  function setLive(on, text) {
    $('liveDot').classList.toggle('on', on);
    if (text) $('latency').textContent = text;
  }

  let screenCount = 1, screenIndex = 0;
  function onInfo(m) {
    $('pcName').textContent = m.pc || 'PC';
    screenCount = m.count; screenIndex = m.screen;
    imgW = m.w; imgH = m.h;
    layoutFrame();
    const seg = $('screenSeg');
    $('screenSection').hidden = screenCount < 2;
    seg.replaceChildren(...Array.from({ length: screenCount }, (_, i) => {
      const b = document.createElement('button');
      b.className = 'ca-seg__item' + (i === screenIndex ? ' is-active' : '');
      b.textContent = i === 0 ? 'Principal' : 'Écran ' + (i + 1);
      b.onclick = () => { send({ t: 'screen', i }); closeSheet(); };
      return b;
    }));
  }

  // ---------- Frames ----------
  const shot = $('shot');
  let framePending = false, imgW = 16, imgH = 9, objectUrl = null;
  function requestFrame() {
    if (!alive || framePending || document.hidden) return;
    framePending = true;
    send({ t: 'frame', ...QUALITY[quality] });
  }
  function showFrame(blob) {
    const url = URL.createObjectURL(blob);
    shot.onload = () => {
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      objectUrl = url;
      $('stageMsg').hidden = true;
      framePending = false;
      requestFrame();
    };
    shot.onerror = () => { URL.revokeObjectURL(url); framePending = false; requestFrame(); };
    shot.src = url;
  }
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) return;
    if (!$('ctrlView').hidden && (!ws || ws.readyState > 1)) connect(); else requestFrame();
  });

  // ---------- View transform (zoom / pan of the preview) ----------
  const stage = $('stage'), frame = $('frame');
  let bw = 0, bh = 0, zoom = 1, tx = 0, ty = 0;
  function layoutFrame() {
    const sw = stage.clientWidth, sh = stage.clientHeight;
    if (!sw || !sh) return;
    const s = Math.min(sw / imgW, sh / imgH);
    bw = imgW * s; bh = imgH * s;
    frame.style.width = bw + 'px';
    frame.style.height = bh + 'px';
    clampView();
  }
  function clampView() {
    const sw = stage.clientWidth, sh = stage.clientHeight;
    zoom = Math.min(5, Math.max(1, zoom));
    const w = bw * zoom, h = bh * zoom;
    tx = w <= sw ? (sw - w) / 2 : Math.min(0, Math.max(sw - w, tx));
    ty = h <= sh ? (sh - h) / 2 : Math.min(0, Math.max(sh - h, ty));
    frame.style.transform = `translate(${tx}px, ${ty}px) scale(${zoom})`;
    $('zoomResetBtn').hidden = zoom <= 1.01;
  }
  $('zoomResetBtn').addEventListener('click', () => { zoom = 1; clampView(); });
  new ResizeObserver(layoutFrame).observe(stage);

  function stagePoint(t) {
    const r = stage.getBoundingClientRect();
    return { x: t.clientX - r.left, y: t.clientY - r.top };
  }
  function toScreen(p) {
    return {
      x: Math.min(1, Math.max(0, (p.x - tx) / (bw * zoom))),
      y: Math.min(1, Math.max(0, (p.y - ty) / (bh * zoom))),
    };
  }
  const onImage = (p) => { const s = toScreen({ ...p }); return p.x >= tx && p.y >= ty && p.x <= tx + bw * zoom && p.y <= ty + bh * zoom && s; };

  function ripple(p, kind = '') {
    const el = document.createElement('span');
    el.className = 'ripple ' + kind;
    el.style.left = p.x + 'px'; el.style.top = p.y + 'px';
    $('ripples').appendChild(el);
    if (kind !== 'hold') setTimeout(() => el.remove(), 500);
    return el;
  }
  let hintTimer = 0;
  function hint(text) {
    const c = $('hintChip');
    c.textContent = text; c.classList.add('show');
    clearTimeout(hintTimer);
    hintTimer = setTimeout(() => c.classList.remove('show'), 1400);
  }

  // ---------- Modes ----------
  let mode = store.get('mode', 'touch');
  function setMode(m) {
    mode = m;
    store.set('mode', m);
    document.querySelectorAll('[data-mode]').forEach((b) => b.classList.toggle('is-active', b.dataset.mode === m));
    stage.classList.toggle('pad', m === 'pad');
    $('ctrlView').classList.toggle('joy', m === 'joy');
    $('joypad').hidden = m !== 'joy';
    zoom = 1;
    requestAnimationFrame(layoutFrame);
    hint(m === 'pad' ? 'Glisse pour bouger, 2 doigts pour défiler'
      : m === 'joy' ? 'Pouce gauche : souris · « Clic » : maintiens pour glisser'
      : 'Touche l’écran pour cliquer');
  }
  document.querySelectorAll('[data-mode]').forEach((b) => b.addEventListener('click', () => setMode(b.dataset.mode)));
  setMode(mode);

  // Throttle movement messages to one per animation frame.
  let pendingAbs = null, pendDx = 0, pendDy = 0, flushQueued = false;
  function queueFlush() {
    if (flushQueued) return;
    flushQueued = true;
    requestAnimationFrame(() => {
      flushQueued = false;
      if (pendingAbs) { send({ t: 'abs', ...pendingAbs }); pendingAbs = null; }
      if (pendDx || pendDy) { send({ t: 'move', dx: Math.round(pendDx), dy: Math.round(pendDy) }); pendDx -= Math.round(pendDx); pendDy -= Math.round(pendDy); }
    });
  }
  function flushNow() {
    if (pendingAbs) { send({ t: 'abs', ...pendingAbs }); pendingAbs = null; }
    if (Math.round(pendDx) || Math.round(pendDy)) send({ t: 'move', dx: Math.round(pendDx), dy: Math.round(pendDy) });
    pendDx = pendDy = 0;
  }
  const moveAbs = (p) => { pendingAbs = toScreen(p); queueFlush(); };
  const clickAt = (p, b = 'l') => { flushNow(); send({ t: 'abs', ...toScreen(p) }); if (!holdLock || b === 'r') send({ t: 'click', b }); };

  // ---------- Gestures ----------
  const HOLD_MS = 450, TAP_MOVE = 10;
  let g = null;           // current gesture
  let lastTap = null;     // for trackpad tap-and-drag

  stage.addEventListener('touchstart', (e) => {
    if (e.target.closest('#wheel')) return;
    e.preventDefault();
    const now = performance.now();
    const pts = [...e.touches].map(stagePoint);
    if (pts.length >= 2) {
      // Second finger: switch to a two-finger gesture.
      cancelHold();
      if (g && g.dragging) { flushNow(); send({ t: 'up', b: 'l' }); }
      const c = mid(pts[0], pts[1]);
      g = { two: true, start: now, c0: c, last: c, d0: dist(pts[0], pts[1]), z0: zoom, tx0: tx, ty0: ty, moved: false, wheelAcc: 0, hwAcc: 0 };
      return;
    }
    const p = pts[0];
    g = { two: false, start: now, p0: p, last: p, lastT: now, moved: false, held: false, dragging: false, ring: null };
    if (mode !== 'pad') {
      g.holdTimer = setTimeout(() => { if (g && !g.moved) { g.held = true; g.ring = ripple(p, 'hold'); } }, HOLD_MS);
    } else if (lastTap && now - lastTap.t < 300 && dist(lastTap.p, p) < 40) {
      g.dragPending = true; // tap, then touch again and slide = drag
    }
  }, { passive: false });

  stage.addEventListener('touchmove', (e) => {
    if (!g) return;
    e.preventDefault();
    const pts = [...e.touches].map(stagePoint);
    const now = performance.now();

    if (g.two) {
      if (pts.length < 2) return;
      const c = mid(pts[0], pts[1]);
      if (dist(c, g.c0) > TAP_MOVE || Math.abs(dist(pts[0], pts[1]) - g.d0) > TAP_MOVE) g.moved = true;
      if (mode !== 'pad') {
        // Pinch to zoom and pan the preview around the fingers.
        const z = Math.min(5, Math.max(1, g.z0 * dist(pts[0], pts[1]) / g.d0));
        const k = z / g.z0;
        zoom = z;
        tx = c.x - (g.c0.x - g.tx0) * k;
        ty = c.y - (g.c0.y - g.ty0) * k;
        clampView();
      } else {
        // Two-finger scroll, natural direction.
        g.wheelAcc += (c.y - g.last.y) * 4;
        g.hwAcc += -(c.x - g.last.x) * 4;
        const wy = Math.trunc(g.wheelAcc / 30) * 30, wx = Math.trunc(g.hwAcc / 30) * 30;
        if (wy || wx) { send({ t: 'wheel', dy: wy, dx: wx }); g.wheelAcc -= wy; g.hwAcc -= wx; }
      }
      g.last = c;
      return;
    }

    const p = pts[0];
    if (!g.moved && dist(p, g.p0) > TAP_MOVE) {
      g.moved = true;
      if (mode !== 'pad') {
        cancelHold();
        if (g.held) {
          // Press, hold, then slide: drag and drop.
          g.dragging = true;
          if (g.ring) { g.ring.remove(); g.ring = null; }
          send({ t: 'abs', ...toScreen(g.p0) });
          send({ t: 'down', b: 'l' });
          hint('Glisser-déposer');
        }
      } else if (g.dragPending) {
        g.dragging = true;
        send({ t: 'down', b: 'l' });
        hint('Glisser-déposer');
      }
    }
    if (g.moved) {
      if (mode !== 'pad') {
        if (g.dragging || zoom <= 1.01) moveAbs(p);
        else { tx += p.x - g.last.x; ty += p.y - g.last.y; clampView(); }
      } else {
        const dt = Math.max(1, now - g.lastT);
        const dx = p.x - g.last.x, dy = p.y - g.last.y;
        const speed = Math.hypot(dx, dy) / dt; // px per ms
        const gain = parseFloat($('speed').value) * (1 + Math.min(speed, 2.5) * 1.2);
        pendDx += dx * gain; pendDy += dy * gain;
        queueFlush();
      }
    }
    g.last = p; g.lastT = now;
  }, { passive: false });

  function endGesture(e) {
    if (!g) return;
    e.preventDefault();
    if (e.touches.length > 0) {
      // Lifting one finger of a pinch: wait for the last one.
      if (g.two) return;
      return;
    }
    const now = performance.now();
    cancelHold();
    if (g.ring) { g.ring.remove(); g.ring = null; }

    if (g.two) {
      if (!g.moved && now - g.start < 300 && mode === 'pad') { send({ t: 'click', b: 'r' }); ripple(g.c0, 'right'); }
      g = null;
      return;
    }
    if (g.dragging) {
      flushNow();
      send({ t: 'up', b: 'l' });
    } else if (!g.moved) {
      if (mode !== 'pad') {
        if (onImage(g.p0)) {
          if (g.held) { clickAt(g.p0, 'r'); ripple(g.p0, 'right'); }
          else { clickAt(g.p0); ripple(g.p0); }
        }
      } else if (now - g.start < 250) {
        flushNow();
        send({ t: 'click', b: 'l' });
        ripple(g.p0);
        lastTap = { t: now, p: g.p0 };
        g = null;
        return;
      }
    } else {
      flushNow();
    }
    lastTap = null;
    g = null;
  }
  stage.addEventListener('touchend', endGesture, { passive: false });
  stage.addEventListener('touchcancel', endGesture, { passive: false });
  function cancelHold() { if (g && g.holdTimer) { clearTimeout(g.holdTimer); g.holdTimer = 0; } }
  const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
  const mid = (a, b) => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });

  // Mouse support (testing from a computer browser).
  let mouse = null;
  stage.addEventListener('mousedown', (e) => {
    if (e.target.closest('#wheel')) return;
    mouse = { p0: stagePoint(e), last: stagePoint(e), moved: false, button: e.button };
  });
  window.addEventListener('mousemove', (e) => {
    if (!mouse) return;
    const p = stagePoint(e);
    if (!mouse.moved && dist(p, mouse.p0) > 4) { mouse.moved = true; if (mode !== 'pad') { send({ t: 'abs', ...toScreen(mouse.p0) }); send({ t: 'down', b: 'l' }); } }
    if (mouse.moved) {
      if (mode !== 'pad') moveAbs(p);
      else { pendDx += (p.x - mouse.last.x) * 2; pendDy += (p.y - mouse.last.y) * 2; queueFlush(); }
    }
    mouse.last = p;
  });
  window.addEventListener('mouseup', () => {
    if (!mouse) return;
    if (mouse.moved) { flushNow(); if (mode !== 'pad') send({ t: 'up', b: 'l' }); }
    else if (mode !== 'pad') clickAt(mouse.p0, mouse.button === 2 ? 'r' : 'l');
    else send({ t: 'click', b: mouse.button === 2 ? 'r' : 'l' });
    ripple(mouse.p0, mouse.button === 2 ? 'right' : '');
    mouse = null;
  });
  stage.addEventListener('contextmenu', (e) => e.preventDefault());
  stage.addEventListener('wheel', (e) => { e.preventDefault(); send({ t: 'wheel', dy: Math.round(-e.deltaY), dx: Math.round(e.deltaX) }); }, { passive: false });

  // Scroll strip (works in both modes).
  const wheel = $('wheel');
  let wy = null, wAcc = 0;
  wheel.addEventListener('touchstart', (e) => { e.preventDefault(); e.stopPropagation(); wy = e.touches[0].clientY; wAcc = 0; wheel.classList.add('active'); }, { passive: false });
  wheel.addEventListener('touchmove', (e) => {
    e.preventDefault(); e.stopPropagation();
    const y = e.touches[0].clientY;
    wAcc += (y - wy) * 5; wy = y;
    const step = Math.trunc(wAcc / 40) * 40;
    if (step) { send({ t: 'wheel', dy: step }); wAcc -= step; }
  }, { passive: false });
  const wheelEnd = (e) => { e.preventDefault(); e.stopPropagation(); wheel.classList.remove('active'); };
  wheel.addEventListener('touchend', wheelEnd, { passive: false });
  wheel.addEventListener('touchcancel', wheelEnd, { passive: false });

  // ---------- Buttons ----------
  let holdLock = false;
  $('leftBtn').addEventListener('click', () => send({ t: 'click', b: 'l' }));
  $('rightBtn').addEventListener('click', () => send({ t: 'click', b: 'r' }));
  function toggleHold() {
    holdLock = !holdLock;
    $('holdBtn').classList.toggle('on', holdLock);
    $('jbHold').classList.toggle('on', holdLock);
    flushNow();
    send({ t: holdLock ? 'down' : 'up', b: 'l' });
    hint(holdLock ? 'Clic maintenu : déplace la souris, puis relâche' : 'Clic relâché');
  }
  $('holdBtn').addEventListener('click', toggleHold);

  // ---------- Joystick ----------
  const capture = (el, e) => { try { el.setPointerCapture(e.pointerId); } catch {} };
  // Left thumb: the stick sets the cursor's speed and direction (further = faster).
  const joyZone = $('joyZone'), joyBase = $('joyBase'), joyKnob = $('joyKnob');
  const JOY_DEAD = 0.1;
  let joy = null; // { id, cx, cy, ux, uy, n, last }
  $('joySpeed').value = store.get('joySpeed', '1.2');
  $('joySpeed').addEventListener('change', () => store.set('joySpeed', $('joySpeed').value));

  function joyRadius() { return joyBase.offsetWidth / 2; }
  joyZone.addEventListener('pointerdown', (e) => {
    if (joy) return;
    e.preventDefault();
    capture(joyZone, e);
    const zr = joyZone.getBoundingClientRect();
    const r = joyRadius();
    // The stick appears under the thumb (kept inside the zone).
    const cx = Math.min(Math.max(e.clientX, zr.left + r * 0.6), zr.right - r * 0.6);
    const cy = Math.min(Math.max(e.clientY, zr.top + r * 0.6), zr.bottom - r * 0.6);
    joyBase.style.left = (cx - zr.left) + 'px';
    joyBase.style.top = (cy - zr.top) + 'px';
    joyZone.classList.add('active');
    joy = { id: e.pointerId, cx, cy, ux: 0, uy: 0, n: 0, last: performance.now() };
    joyMove(e);
    // A plain timer, not requestAnimationFrame: it keeps a steady pace even in Low Power Mode.
    joy.timer = setInterval(joyTick, 16);
  });
  joyZone.addEventListener('pointermove', (e) => { if (joy && e.pointerId === joy.id) { e.preventDefault(); joyMove(e); } });
  const joyEnd = (e) => {
    if (!joy || e.pointerId !== joy.id) return;
    clearInterval(joy.timer);
    joy = null;
    flushNow();
    joyZone.classList.remove('active');
    joyKnob.style.transform = '';
    joyBase.style.left = joyBase.style.top = '';
  };
  joyZone.addEventListener('pointerup', joyEnd);
  joyZone.addEventListener('pointercancel', joyEnd);

  function joyMove(e) {
    const r = joyRadius();
    let dx = e.clientX - joy.cx, dy = e.clientY - joy.cy;
    const len = Math.hypot(dx, dy);
    if (len > r) { dx *= r / len; dy *= r / len; }
    joyKnob.style.transform = `translate(${dx}px, ${dy}px)`;
    const n = Math.min(1, len / r);
    joy.n = n < JOY_DEAD ? 0 : (n - JOY_DEAD) / (1 - JOY_DEAD);
    joy.ux = len ? dx / len : 0;
    joy.uy = len ? dy / len : 0;
  }

  function joyTick() {
    if (!joy) return;
    const now = performance.now();
    const dt = Math.min(0.05, Math.max(0, now - joy.last) / 1000);
    joy.last = now;
    if (joy.n > 0) {
      // Gentle near the centre for precision, fast at the edge.
      const speed = parseFloat($('joySpeed').value) * (60 + 1500 * Math.pow(joy.n, 2.2));
      pendDx += joy.ux * speed * dt;
      pendDy += joy.uy * speed * dt;
      const sx = Math.trunc(pendDx), sy = Math.trunc(pendDy);
      if (sx || sy) { send({ t: 'move', dx: sx, dy: sy }); pendDx -= sx; pendDy -= sy; }
    }
  }

  // Right thumb: "Clic". Tap = left click, double tap = right click (or double-click),
  // press and hold = keep the left button down to drag with the stick.
  const jbMain = $('jbMain');
  const HOLD_DRAG_MS = 380, DOUBLE_MS = 280;
  let dblAction = store.get('dbl', 'right');
  let mainPress = null, pendingClick = 0, lastTapAt = 0;
  function renderDbl() { document.querySelectorAll('[data-dbl]').forEach((b) => b.classList.toggle('is-active', b.dataset.dbl === dblAction)); }
  document.querySelectorAll('[data-dbl]').forEach((b) => b.addEventListener('click', () => { dblAction = b.dataset.dbl; store.set('dbl', dblAction); renderDbl(); }));
  renderDbl();

  jbMain.addEventListener('pointerdown', (e) => {
    e.preventDefault();
    capture(jbMain, e);
    jbMain.classList.add('pressed');
    const press = { id: e.pointerId, dragging: false };
    press.timer = setTimeout(() => {
      press.dragging = true;
      clearTimeout(pendingClick); pendingClick = 0;
      flushNow();
      if (!holdLock) send({ t: 'down', b: 'l' });
      jbMain.classList.add('drag');
      jbMain.querySelector('span').textContent = 'Glisser';
      hint('Glisse avec le joystick, puis relâche');
    }, HOLD_DRAG_MS);
    mainPress = press;
  });
  const mainEnd = (e) => {
    const press = mainPress;
    if (!press || e.pointerId !== press.id) return;
    mainPress = null;
    clearTimeout(press.timer);
    jbMain.classList.remove('pressed', 'drag');
    jbMain.querySelector('span').textContent = 'Clic';
    flushNow();
    if (press.dragging) { if (!holdLock) send({ t: 'up', b: 'l' }); return; }
    if (e.type === 'pointercancel') return;

    const now = performance.now();
    if (dblAction === 'double') {
      send({ t: 'click', b: 'l' }); // two quick taps reach Windows as a double-click
    } else if (pendingClick && now - lastTapAt < DOUBLE_MS) {
      clearTimeout(pendingClick); pendingClick = 0;
      send({ t: 'click', b: 'r' });
      hint('Clic droit');
    } else {
      pendingClick = setTimeout(() => { pendingClick = 0; send({ t: 'click', b: 'l' }); }, DOUBLE_MS);
    }
    lastTapAt = now;
  };
  jbMain.addEventListener('pointerup', mainEnd);
  jbMain.addEventListener('pointercancel', mainEnd);

  $('jbRight').addEventListener('click', () => { flushNow(); send({ t: 'click', b: 'r' }); });
  $('jbDouble').addEventListener('click', () => { flushNow(); send({ t: 'click', b: 'l', n: 2 }); });
  $('jbHold').addEventListener('click', toggleHold);
  $('jbKbd').addEventListener('click', () => $('kbdBtn').click());
  $('joypad').addEventListener('contextmenu', (e) => e.preventDefault());

  // Scroll strip between the stick and the buttons.
  const joyScroll = $('joyScroll');
  let jsY = null, jsAcc = 0;
  joyScroll.addEventListener('pointerdown', (e) => { e.preventDefault(); capture(joyScroll, e); jsY = e.clientY; jsAcc = 0; joyScroll.classList.add('active'); });
  joyScroll.addEventListener('pointermove', (e) => {
    if (jsY == null) return;
    jsAcc += (e.clientY - jsY) * 5; jsY = e.clientY;
    const step = Math.trunc(jsAcc / 40) * 40;
    if (step) { send({ t: 'wheel', dy: step }); jsAcc -= step; }
  });
  const jsEnd = () => { jsY = null; joyScroll.classList.remove('active'); };
  joyScroll.addEventListener('pointerup', jsEnd);
  joyScroll.addEventListener('pointercancel', jsEnd);

  // ---------- Keyboard ----------
  const kbd = $('kbd');
  const mods = new Set();
  const SENTINEL = '  ';
  function resetKbd() { kbd.value = SENTINEL; kbd.setSelectionRange(SENTINEL.length, SENTINEL.length); }
  function takeMods() {
    const list = [...mods];
    mods.clear();
    document.querySelectorAll('.key.mod').forEach((b) => b.classList.remove('on'));
    return list;
  }
  function sendKey(k) { send({ t: 'key', k, mods: takeMods() }); }

  $('kbdBtn').addEventListener('click', () => {
    const open = $('keys').hidden;
    $('keys').hidden = !open;
    $('kbdBtn').classList.toggle('on', open);
    app.classList.toggle('kbd-open', open);
    if (open) { resetKbd(); kbd.focus(); } else kbd.blur();
    requestAnimationFrame(layoutFrame);
  });
  kbd.addEventListener('blur', () => {
    // Keep the special-keys row while the user taps it; close it if the keyboard was dismissed.
    setTimeout(() => {
      if (document.activeElement !== kbd && !$('keys').hidden && !keysTouched) {
        $('keys').hidden = true; $('kbdBtn').classList.remove('on'); app.classList.remove('kbd-open');
        requestAnimationFrame(layoutFrame);
      }
    }, 150);
  });
  let keysTouched = false;
  $('keys').addEventListener('touchstart', () => { keysTouched = true; }, { passive: true });
  $('keys').addEventListener('mousedown', (e) => { if (e.target !== kbd) e.preventDefault(); });

  function afterKeyTap() {
    keysTouched = false;
    if (document.activeElement !== kbd) kbd.focus();
  }
  document.querySelectorAll('.key[data-key]').forEach((b) => b.addEventListener('click', () => { sendKey(b.dataset.key); afterKeyTap(); }));
  document.querySelectorAll('.key[data-mod]').forEach((b) => b.addEventListener('click', () => {
    const m = b.dataset.mod;
    if (mods.has(m)) mods.delete(m); else mods.add(m);
    b.classList.toggle('on', mods.has(m));
    afterKeyTap();
  }));

  kbd.addEventListener('beforeinput', (e) => {
    const type = e.inputType;
    if (type === 'insertText' || type === 'insertReplacementText') {
      e.preventDefault();
      const text = e.data ?? (e.dataTransfer && e.dataTransfer.getData('text/plain')) ?? '';
      if (!text) return;
      if (mods.size && text.length === 1 && /[a-z0-9]/i.test(text)) sendKey(text.toUpperCase());
      else if (mods.size && text === ' ') sendKey('Space');
      else send({ t: 'text', s: text });
    } else if (type === 'insertLineBreak' || type === 'insertParagraph') {
      e.preventDefault(); sendKey('Enter');
    } else if (type === 'deleteContentBackward' || type === 'deleteWordBackward' || type === 'deleteSoftLineBackward') {
      e.preventDefault(); sendKey('Backspace');
    } else if (type === 'deleteContentForward') {
      e.preventDefault(); sendKey('Delete');
    } else if (type === 'insertFromPaste') {
      e.preventDefault();
      const text = e.dataTransfer && e.dataTransfer.getData('text/plain');
      if (text) send({ t: 'text', s: text });
    }
  });
  // Fallback for browsers that ignore preventDefault on beforeinput (composition, dictation).
  kbd.addEventListener('input', () => {
    const v = kbd.value;
    if (v.startsWith(SENTINEL) && v.length > SENTINEL.length) send({ t: 'text', s: v.slice(SENTINEL.length) });
    else if (v.length < SENTINEL.length) sendKey('Backspace');
    resetKbd();
  });
  // Hardware keyboards (iPad / computer) send keydown for special keys.
  kbd.addEventListener('keydown', (e) => {
    const map = { ArrowLeft: 'Left', ArrowRight: 'Right', ArrowUp: 'Up', ArrowDown: 'Down', Escape: 'Escape', Tab: 'Tab' };
    if (map[e.key]) { e.preventDefault(); sendKey(map[e.key]); }
  });

  // ---------- Sheet ----------
  function openSheet() { $('sheet').hidden = false; $('scrim').hidden = false; }
  function closeSheet() { $('sheet').hidden = true; $('scrim').hidden = true; }
  $('moreBtn').addEventListener('click', openSheet);
  $('scrim').addEventListener('click', closeSheet);
  document.querySelectorAll('.sc').forEach((b) => b.addEventListener('click', () => {
    send({ t: 'key', k: b.dataset.k, mods: b.dataset.m ? b.dataset.m.split(',') : [] });
    hint(b.textContent.trim());
    closeSheet();
  }));
  function renderQuality() {
    document.querySelectorAll('[data-q]').forEach((b) => b.classList.toggle('is-active', b.dataset.q === quality));
  }
  document.querySelectorAll('[data-q]').forEach((b) => b.addEventListener('click', () => {
    quality = b.dataset.q; store.set('quality', quality); renderQuality();
  }));
  renderQuality();
  $('speed').value = store.get('speed', '1.4');
  $('speed').addEventListener('change', () => store.set('speed', $('speed').value));
  $('leaveBtn').addEventListener('click', () => {
    closeSheet();
    if (holdLock) toggleHold();
    message('Déconnecté', 'Tu peux revenir tant que se7en Vision reste ouvert sur le PC.', { icon: 'log-out', violet: true, button: 'Se reconnecter', onButton: boot });
  });

  boot();
})();
