// Mouse and keyboard, from a phone (touch, trackpad, joystick) or a PC (real mouse, real keyboard).
// Everything ends up as the same small messages: abs/move/down/up/click/wheel/key/kd/ku/text.
export function createInput({ stage, layout, send, isDesktop, ui, store, joy: joyEls, keys: keyEls, getScreen }) {
  let mode = store.get('mode', 'touch');
  let holdLock = false;
  const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
  const mid = (a, b) => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });
  let annotating = false; // draw.js takes the pointer while a tool is active

  // ---- throttled movement ----
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
  const absOf = (p) => { const s = layout.toScreen(p); return s; };
  const moveAbs = (p) => { const s = absOf(p); if (s) { pendingAbs = s; queueFlush(); } };
  const clickAt = (p, b = 'l') => { flushNow(); const s = absOf(p); if (!s) return; send({ t: 'abs', ...s }); if (!holdLock || b === 'r') send({ t: 'click', b }); };

  // ---- modes (phone) ----
  function setMode(m) {
    mode = m;
    store.set('mode', m);
    document.querySelectorAll('[data-mode]').forEach((b) => b.classList.toggle('is-active', b.dataset.mode === m));
    stage.classList.toggle('pad', m === 'pad');
    ui.ctrl.classList.toggle('joy', m === 'joy');
    if (joyEls.pad) joyEls.pad.hidden = m !== 'joy';
    layout.zoom = 1;
    requestAnimationFrame(() => layout.layout());
    ui.hint(m === 'pad' ? 'Glisse pour bouger, 2 doigts pour défiler'
      : m === 'joy' ? 'Pouce gauche : souris · « Clic » : maintiens pour glisser'
      : 'Touche l’écran pour cliquer');
  }
  document.querySelectorAll('[data-mode]').forEach((b) => b.addEventListener('click', () => setMode(b.dataset.mode)));

  // ---- touch gestures (phone) ----
  const HOLD_MS = 450, TAP_MOVE = 10;
  let g = null, lastTap = null;
  function cancelHold() { if (g && g.holdTimer) { clearTimeout(g.holdTimer); g.holdTimer = 0; } }

  stage.addEventListener('touchstart', (e) => {
    if (annotating || isDesktop) return;
    if (e.target.closest('#wheel')) return;
    e.preventDefault();
    const now = performance.now();
    const pts = [...e.touches].map(layout.stagePoint);
    if (pts.length >= 2) {
      cancelHold();
      if (g && g.dragging) { flushNow(); send({ t: 'up', b: 'l' }); }
      const c = mid(pts[0], pts[1]);
      g = { two: true, start: now, c0: c, last: c, d0: dist(pts[0], pts[1]), z0: layout.zoom, tx0: layout.tx, ty0: layout.ty, moved: false, wheelAcc: 0, hwAcc: 0 };
      return;
    }
    const p = pts[0];
    g = { two: false, start: now, p0: p, last: p, lastT: now, moved: false, held: false, dragging: false, ring: null };
    if (mode !== 'pad') g.holdTimer = setTimeout(() => { if (g && !g.moved) { g.held = true; g.ring = ui.ripple(p, 'hold'); } }, HOLD_MS);
    else if (lastTap && now - lastTap.t < 300 && dist(lastTap.p, p) < 40) g.dragPending = true;
  }, { passive: false });

  stage.addEventListener('touchmove', (e) => {
    if (!g) return;
    e.preventDefault();
    const pts = [...e.touches].map(layout.stagePoint);
    const now = performance.now();
    if (g.two) {
      if (pts.length < 2) return;
      const c = mid(pts[0], pts[1]);
      if (dist(c, g.c0) > TAP_MOVE || Math.abs(dist(pts[0], pts[1]) - g.d0) > TAP_MOVE) g.moved = true;
      if (mode !== 'pad') {
        const z = Math.min(5, Math.max(1, g.z0 * dist(pts[0], pts[1]) / g.d0));
        const k = z / g.z0;
        layout.zoom = z;
        layout.tx = c.x - (g.c0.x - g.tx0) * k;
        layout.ty = c.y - (g.c0.y - g.ty0) * k;
        layout.clamp();
      } else {
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
          g.dragging = true;
          if (g.ring) { g.ring.remove(); g.ring = null; }
          const s = absOf(g.p0); if (s) send({ t: 'abs', ...s });
          send({ t: 'down', b: 'l' });
          ui.hint('Glisser-déposer');
        }
      } else if (g.dragPending) { g.dragging = true; send({ t: 'down', b: 'l' }); ui.hint('Glisser-déposer'); }
    }
    if (g.moved) {
      if (mode !== 'pad') {
        if (g.dragging || layout.zoom <= 1.01) moveAbs(p);
        else { layout.tx += p.x - g.last.x; layout.ty += p.y - g.last.y; layout.clamp(); }
      } else {
        const dt = Math.max(1, now - g.lastT);
        const dx = p.x - g.last.x, dy = p.y - g.last.y;
        const speed = Math.hypot(dx, dy) / dt;
        const gain = parseFloat(keyEls.speed.value) * (1 + Math.min(speed, 2.5) * 1.2);
        pendDx += dx * gain; pendDy += dy * gain;
        queueFlush();
      }
    }
    g.last = p; g.lastT = now;
  }, { passive: false });

  function endGesture(e) {
    if (!g) return;
    e.preventDefault();
    if (e.touches.length > 0) return;
    const now = performance.now();
    cancelHold();
    if (g.ring) { g.ring.remove(); g.ring = null; }
    if (g.two) {
      if (!g.moved && now - g.start < 300 && mode === 'pad') { send({ t: 'click', b: 'r' }); ui.ripple(g.c0, 'right'); }
      g = null; return;
    }
    if (g.dragging) { flushNow(); send({ t: 'up', b: 'l' }); }
    else if (!g.moved) {
      if (mode !== 'pad') {
        if (layout.onImage(g.p0)) {
          if (g.held) { clickAt(g.p0, 'r'); ui.ripple(g.p0, 'right'); } else { clickAt(g.p0); ui.ripple(g.p0); }
        }
      } else if (now - g.start < 250) {
        flushNow(); send({ t: 'click', b: 'l' }); ui.ripple(g.p0);
        lastTap = { t: now, p: g.p0 }; g = null; return;
      }
    } else flushNow();
    lastTap = null; g = null;
  }
  stage.addEventListener('touchend', endGesture, { passive: false });
  stage.addEventListener('touchcancel', endGesture, { passive: false });

  // ---- scroll strip ----
  const wheel = document.getElementById('wheel');
  let wy = null, wAcc = 0;
  if (wheel) {
    wheel.addEventListener('touchstart', (e) => { e.preventDefault(); e.stopPropagation(); wy = e.touches[0].clientY; wAcc = 0; wheel.classList.add('active'); }, { passive: false });
    wheel.addEventListener('touchmove', (e) => {
      e.preventDefault(); e.stopPropagation();
      const y = e.touches[0].clientY; wAcc += (y - wy) * 5; wy = y;
      const step = Math.trunc(wAcc / 40) * 40;
      if (step) { send({ t: 'wheel', dy: step }); wAcc -= step; }
    }, { passive: false });
    const wheelEnd = (e) => { e.preventDefault(); e.stopPropagation(); wheel.classList.remove('active'); };
    wheel.addEventListener('touchend', wheelEnd, { passive: false });
    wheel.addEventListener('touchcancel', wheelEnd, { passive: false });
  }

  // ---- real mouse (PC viewer, or a computer browser testing the phone page) ----
  const BTN = { 0: 'l', 1: 'm', 2: 'r' };
  let mouse = null;
  if (isDesktop) {
    // the stage is the PC's screen: absolute moves, every button, the wheel, no gestures
    let lastMoveSent = 0;
    stage.addEventListener('pointermove', (e) => {
      if (annotating || e.pointerType === 'touch') return;
      const p = layout.stagePoint(e);
      if (!layout.onImage(p)) return;
      const now = performance.now();
      if (now - lastMoveSent < 4) { moveAbs(p); return; } // coalesced by the animation frame
      lastMoveSent = now;
      moveAbs(p);
    });
    stage.addEventListener('pointerdown', (e) => {
      if (annotating || e.pointerType === 'touch') return;
      const p = layout.stagePoint(e);
      if (!layout.onImage(p)) return;
      stage.focus();
      try { stage.setPointerCapture(e.pointerId); } catch {}
      flushNow();
      const s = absOf(p); if (s) send({ t: 'abs', ...s });
      send({ t: 'down', b: BTN[e.button] || 'l' });
    });
    const up = (e) => {
      if (e.pointerType === 'touch') return;
      flushNow();
      send({ t: 'up', b: BTN[e.button] || 'l' });
    };
    stage.addEventListener('pointerup', up);
    stage.addEventListener('pointercancel', up);
    stage.addEventListener('wheel', (e) => {
      if (annotating) return;
      e.preventDefault();
      const k = e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? 400 : 1;
      send({ t: 'wheel', dy: Math.round(-e.deltaY * k), dx: Math.round(e.deltaX * k) });
    }, { passive: false });
    stage.addEventListener('contextmenu', (e) => e.preventDefault());
    // keyboard: physical keys, as they are, while the stage has the focus
    const held = new Set();
    stage.addEventListener('keydown', (e) => {
      if (e.repeat) { e.preventDefault(); return; }
      if (e.code === 'Escape' && e.ctrlKey && e.shiftKey) { stage.blur(); return; } // give the keyboard back
      e.preventDefault();
      held.add(e.code);
      send({ t: 'kd', c: e.code });
    });
    stage.addEventListener('keyup', (e) => { e.preventDefault(); held.delete(e.code); send({ t: 'ku', c: e.code }); });
    stage.addEventListener('blur', () => { for (const c of held) send({ t: 'ku', c }); held.clear(); });
    window.addEventListener('blur', () => { for (const c of held) send({ t: 'ku', c }); held.clear(); flushNow(); send({ t: 'up', b: 'l' }); });
    stage.addEventListener('paste', (e) => { const text = e.clipboardData && e.clipboardData.getData('text/plain'); if (text) { e.preventDefault(); send({ t: 'text', s: text.slice(0, 2000) }); } });
  } else {
    stage.addEventListener('mousedown', (e) => {
      if (annotating || e.target.closest('#wheel')) return;
      mouse = { p0: layout.stagePoint(e), last: layout.stagePoint(e), moved: false, button: e.button };
    });
    window.addEventListener('mousemove', (e) => {
      if (!mouse) return;
      const p = layout.stagePoint(e);
      if (!mouse.moved && dist(p, mouse.p0) > 4) { mouse.moved = true; if (mode !== 'pad') { const s = absOf(mouse.p0); if (s) send({ t: 'abs', ...s }); send({ t: 'down', b: 'l' }); } }
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
      ui.ripple(mouse.p0, mouse.button === 2 ? 'right' : '');
      mouse = null;
    });
    stage.addEventListener('contextmenu', (e) => e.preventDefault());
    stage.addEventListener('wheel', (e) => { e.preventDefault(); send({ t: 'wheel', dy: Math.round(-e.deltaY), dx: Math.round(e.deltaX) }); }, { passive: false });
  }

  // ---- buttons (phone toolbar) ----
  function toggleHold() {
    holdLock = !holdLock;
    document.querySelectorAll('[data-hold]').forEach((b) => b.classList.toggle('on', holdLock));
    flushNow();
    send({ t: holdLock ? 'down' : 'up', b: 'l' });
    ui.hint(holdLock ? 'Clic maintenu : déplace la souris, puis relâche' : 'Clic relâché');
  }
  document.querySelectorAll('[data-click]').forEach((b) => b.addEventListener('click', () => { flushNow(); send({ t: 'click', b: b.dataset.click, n: Number(b.dataset.n || 1) }); }));
  document.querySelectorAll('[data-hold]').forEach((b) => b.addEventListener('click', toggleHold));

  // ---- joystick (phone) ----
  const capture = (el, e) => { try { el.setPointerCapture(e.pointerId); } catch {} };
  if (joyEls.zone) {
    const { zone: joyZone, base: joyBase, knob: joyKnob, speed: joySpeed, main: jbMain, scroll: joyScroll } = joyEls;
    const JOY_DEAD = 0.1;
    let joy = null;
    joySpeed.value = store.get('joySpeed', '1.2');
    joySpeed.addEventListener('change', () => store.set('joySpeed', joySpeed.value));
    const joyRadius = () => joyBase.offsetWidth / 2;
    joyZone.addEventListener('pointerdown', (e) => {
      if (joy) return;
      e.preventDefault(); capture(joyZone, e);
      const zr = joyZone.getBoundingClientRect(), r = joyRadius();
      const cx = Math.min(Math.max(e.clientX, zr.left + r * 0.6), zr.right - r * 0.6);
      const cy = Math.min(Math.max(e.clientY, zr.top + r * 0.6), zr.bottom - r * 0.6);
      joyBase.style.left = (cx - zr.left) + 'px'; joyBase.style.top = (cy - zr.top) + 'px';
      joyZone.classList.add('active');
      joy = { id: e.pointerId, cx, cy, ux: 0, uy: 0, n: 0, last: performance.now() };
      joyMove(e);
      joy.timer = setInterval(joyTick, 16);
    });
    joyZone.addEventListener('pointermove', (e) => { if (joy && e.pointerId === joy.id) { e.preventDefault(); joyMove(e); } });
    const joyEnd = (e) => {
      if (!joy || e.pointerId !== joy.id) return;
      clearInterval(joy.timer); joy = null; flushNow();
      joyZone.classList.remove('active'); joyKnob.style.transform = ''; joyBase.style.left = joyBase.style.top = '';
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
      joy.ux = len ? dx / len : 0; joy.uy = len ? dy / len : 0;
    }
    function joyTick() {
      if (!joy) return;
      const now = performance.now();
      const dt = Math.min(0.05, Math.max(0, now - joy.last) / 1000);
      joy.last = now;
      if (joy.n > 0) {
        const speed = parseFloat(joySpeed.value) * (60 + 1500 * Math.pow(joy.n, 2.2));
        pendDx += joy.ux * speed * dt; pendDy += joy.uy * speed * dt;
        const sx = Math.trunc(pendDx), sy = Math.trunc(pendDy);
        if (sx || sy) { send({ t: 'move', dx: sx, dy: sy }); pendDx -= sx; pendDy -= sy; }
      }
    }
    const HOLD_DRAG_MS = 380, DOUBLE_MS = 280;
    let dblAction = store.get('dbl', 'right');
    let mainPress = null, pendingClick = 0, lastTapAt = 0;
    const renderDbl = () => document.querySelectorAll('[data-dbl]').forEach((b) => b.classList.toggle('is-active', b.dataset.dbl === dblAction));
    document.querySelectorAll('[data-dbl]').forEach((b) => b.addEventListener('click', () => { dblAction = b.dataset.dbl; store.set('dbl', dblAction); renderDbl(); }));
    renderDbl();
    jbMain.addEventListener('pointerdown', (e) => {
      e.preventDefault(); capture(jbMain, e);
      jbMain.classList.add('pressed');
      const press = { id: e.pointerId, dragging: false };
      press.timer = setTimeout(() => {
        press.dragging = true;
        clearTimeout(pendingClick); pendingClick = 0;
        flushNow();
        if (!holdLock) send({ t: 'down', b: 'l' });
        jbMain.classList.add('drag');
        jbMain.querySelector('span').textContent = 'Glisser';
        ui.hint('Glisse avec le joystick, puis relâche');
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
      if (dblAction === 'double') send({ t: 'click', b: 'l' });
      else if (pendingClick && now - lastTapAt < DOUBLE_MS) { clearTimeout(pendingClick); pendingClick = 0; send({ t: 'click', b: 'r' }); ui.hint('Clic droit'); }
      else pendingClick = setTimeout(() => { pendingClick = 0; send({ t: 'click', b: 'l' }); }, DOUBLE_MS);
      lastTapAt = now;
    };
    jbMain.addEventListener('pointerup', mainEnd);
    jbMain.addEventListener('pointercancel', mainEnd);
    joyEls.pad.addEventListener('contextmenu', (e) => e.preventDefault());
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
  }

  // ---- soft keyboard (phone) ----
  const kbd = keyEls.kbd, mods = new Set(), SENTINEL = '  ';
  function resetKbd() { kbd.value = SENTINEL; kbd.setSelectionRange(SENTINEL.length, SENTINEL.length); }
  function takeMods() { const list = [...mods]; mods.clear(); document.querySelectorAll('.key.mod').forEach((b) => b.classList.remove('on')); return list; }
  function sendKey(k) { send({ t: 'key', k, mods: takeMods() }); }
  let keysTouched = false;
  function openKeyboard(open) {
    keyEls.panel.hidden = !open;
    document.querySelectorAll('[data-kbd]').forEach((b) => b.classList.toggle('on', open));
    ui.app.classList.toggle('kbd-open', open);
    if (open) { resetKbd(); kbd.focus(); } else kbd.blur();
    requestAnimationFrame(() => layout.layout());
  }
  document.querySelectorAll('[data-kbd]').forEach((b) => b.addEventListener('click', () => openKeyboard(keyEls.panel.hidden)));
  kbd.addEventListener('blur', () => {
    setTimeout(() => {
      if (document.activeElement !== kbd && !keyEls.panel.hidden && !keysTouched) openKeyboard(false);
    }, 150);
  });
  keyEls.panel.addEventListener('touchstart', () => { keysTouched = true; }, { passive: true });
  keyEls.panel.addEventListener('mousedown', (e) => { if (e.target !== kbd) e.preventDefault(); });
  const afterKeyTap = () => { keysTouched = false; if (document.activeElement !== kbd) kbd.focus(); };
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
    } else if (type === 'insertLineBreak' || type === 'insertParagraph') { e.preventDefault(); sendKey('Enter'); }
    else if (type === 'deleteContentBackward' || type === 'deleteWordBackward' || type === 'deleteSoftLineBackward') { e.preventDefault(); sendKey('Backspace'); }
    else if (type === 'deleteContentForward') { e.preventDefault(); sendKey('Delete'); }
    else if (type === 'insertFromPaste') { e.preventDefault(); const text = e.dataTransfer && e.dataTransfer.getData('text/plain'); if (text) send({ t: 'text', s: text }); }
  });
  kbd.addEventListener('input', () => {
    const v = kbd.value;
    if (v.startsWith(SENTINEL) && v.length > SENTINEL.length) send({ t: 'text', s: v.slice(SENTINEL.length) });
    else if (v.length < SENTINEL.length) sendKey('Backspace');
    resetKbd();
  });
  kbd.addEventListener('keydown', (e) => {
    const map = { ArrowLeft: 'Left', ArrowRight: 'Right', ArrowUp: 'Up', ArrowDown: 'Down', Escape: 'Escape', Tab: 'Tab' };
    if (map[e.key]) { e.preventDefault(); sendKey(map[e.key]); }
  });
  document.querySelectorAll('.sc[data-k]').forEach((b) => b.addEventListener('click', () => {
    send({ t: 'key', k: b.dataset.k, mods: b.dataset.m ? b.dataset.m.split(',') : [] });
    ui.hint(b.textContent.trim());
    ui.closeSheet();
  }));
  keyEls.speed.value = store.get('speed', '1.4');
  keyEls.speed.addEventListener('change', () => store.set('speed', keyEls.speed.value));

  if (!isDesktop) setMode(mode);

  return {
    flushNow, toggleHold,
    get holdLock() { return holdLock; },
    set annotating(v) { annotating = v; },
    releaseAll() { if (holdLock) toggleHold(); flushNow(); },
  };
}
