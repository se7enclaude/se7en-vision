// Laser pointer and pen: show something on the other PC's screen without touching anything.
// Positions are sent as 0..1 fractions of a screen; a local copy is drawn on the stage when the PC says
// its own overlay stays out of the video (otherwise the strokes come back inside the picture).
export function createDraw({ stage, canvas, layout, send, input, hint, store, getScreens }) {
  const ctx = canvas.getContext('2d');
  let tool = null;          // null | 'laser' | 'pen'
  let color = store.get('penColor', '#ff4646');
  let width = Number(store.get('penWidth', '4'));
  let echo = true;          // draw locally too
  let nextId = 1, cur = null, pending = [], flushRaf = 0, lastLaser = 0, laser = null, laserTimer = 0;
  const strokes = [];       // { s, c, w, pts:[fx,fy,...] } (screen-normalised)

  function resize() {
    const r = stage.getBoundingClientRect();
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const w = Math.round(r.width * dpr), h = Math.round(r.height * dpr);
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
    canvas.style.width = r.width + 'px'; canvas.style.height = r.height + 'px';
    redraw();
  }
  new ResizeObserver(resize).observe(stage);
  layout.onChange(redraw);

  function toStage(fx, fy, s) {
    const screens = getScreens();
    const sc = screens && screens.find((k) => k.i === s);
    if (sc) return layout.fromDesktop(sc.x + fx * sc.w, sc.y + fy * sc.h, s, screens);
    return { x: layout.tx + fx * layout.bw * layout.zoom, y: layout.ty + fy * layout.bh * layout.zoom, scale: layout.bw * layout.zoom / layout.imgW };
  }
  function redraw() {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    if (!echo) return;
    const dpr = canvas.width / Math.max(1, canvas.clientWidth);
    ctx.save();
    ctx.scale(dpr, dpr);
    ctx.lineCap = ctx.lineJoin = 'round';
    for (const st of strokes) {
      const p0 = toStage(st.pts[0], st.pts[1], st.s);
      if (!p0) continue;
      ctx.strokeStyle = st.c; ctx.fillStyle = st.c;
      ctx.lineWidth = Math.max(1, st.w * p0.scale);
      if (st.pts.length <= 2) { ctx.beginPath(); ctx.arc(p0.x, p0.y, ctx.lineWidth / 2, 0, Math.PI * 2); ctx.fill(); continue; }
      ctx.beginPath(); ctx.moveTo(p0.x, p0.y);
      for (let i = 2; i < st.pts.length; i += 2) { const p = toStage(st.pts[i], st.pts[i + 1], st.s); if (p) ctx.lineTo(p.x, p.y); }
      ctx.stroke();
    }
    if (laser) {
      const p = toStage(laser.x, laser.y, laser.s);
      if (p) {
        const g = ctx.createRadialGradient(p.x, p.y, 2, p.x, p.y, 14);
        g.addColorStop(0, color); g.addColorStop(1, 'rgba(0,0,0,0)');
        ctx.fillStyle = g; ctx.beginPath(); ctx.arc(p.x, p.y, 14, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = color; ctx.beginPath(); ctx.arc(p.x, p.y, 5, 0, Math.PI * 2); ctx.fill();
      }
    }
    ctx.restore();
  }

  function setTool(t) {
    if (tool === 'pen' && cur) endStroke();
    tool = tool === t ? null : t;
    input.annotating = !!tool;
    stage.classList.toggle('annotating', !!tool);
    stage.classList.toggle('tool-pen', tool === 'pen');
    stage.classList.toggle('tool-laser', tool === 'laser');
    document.querySelectorAll('[data-tool]').forEach((b) => b.classList.toggle('is-active', b.dataset.tool === tool));
    if (!tool) { laser = null; redraw(); }
    hint(tool === 'laser' ? 'Pointeur laser : montre sans cliquer · Échap pour quitter'
      : tool === 'pen' ? 'Crayon : dessine sur l’écran du PC · Échap pour quitter' : 'Souris et clavier');
  }
  function setColor(c) { color = c; store.set('penColor', c); document.querySelectorAll('[data-color]').forEach((b) => b.classList.toggle('is-active', b.dataset.color === c)); }
  function setWidth(w) { width = w; store.set('penWidth', String(w)); }

  // ---- pointer handling (mouse, pen or touch, while a tool is active) ----
  const pos = (ev) => layout.toScreen(layout.stagePoint(ev));
  function onDown(ev) {
    if (!tool) return;
    ev.preventDefault();
    const p = pos(ev);
    if (!p) return;
    const s = p.s ?? currentScreen();
    if (tool === 'laser') { sendLaser(p.x, p.y, s, true); return; }
    cur = { id: nextId++, s, c: color, w: width, pts: [p.x, p.y] };
    strokes.push(cur);
    send({ t: 'draw', op: 'begin', id: cur.id, x: p.x, y: p.y, s, c: color, w: width });
    redraw();
  }
  function onMove(ev) {
    if (!tool) return;
    ev.preventDefault();
    const p = pos(ev);
    if (!p) return;
    if (tool === 'laser') { if (ev.buttons !== 0 || ev.pointerType !== 'mouse' || true) sendLaser(p.x, p.y, p.s ?? currentScreen(), false); return; }
    if (!cur || (p.s != null && p.s !== cur.s)) return;
    cur.pts.push(p.x, p.y);
    pending.push(p.x, p.y);
    if (!flushRaf) flushRaf = requestAnimationFrame(flush);
    redraw();
  }
  function onUp(ev) {
    if (!tool) return;
    ev.preventDefault();
    if (tool === 'pen') endStroke();
  }
  function endStroke() {
    if (!cur) return;
    flush();
    send({ t: 'draw', op: 'end', id: cur.id });
    cur = null;
  }
  function flush() {
    flushRaf = 0;
    if (cur && pending.length) send({ t: 'draw', op: 'pts', id: cur.id, p: pending.map((v) => Math.round(v * 10000) / 10000) });
    pending = [];
  }
  function sendLaser(x, y, s, force) {
    const now = performance.now();
    laser = { x, y, s };
    clearTimeout(laserTimer);
    laserTimer = setTimeout(() => { laser = null; redraw(); }, 1200);
    redraw();
    if (!force && now - lastLaser < 16) return;
    lastLaser = now;
    send({ t: 'draw', op: 'laser', x: Math.round(x * 10000) / 10000, y: Math.round(y * 10000) / 10000, s, c: color });
  }
  function currentScreen() { return layout.mosaic ? 0 : (getScreenIndex ? getScreenIndex() : 0); }
  let getScreenIndex = null;

  stage.addEventListener('pointerdown', onDown);
  stage.addEventListener('pointermove', onMove);
  stage.addEventListener('pointerup', onUp);
  stage.addEventListener('pointercancel', onUp);
  // phones: touch events would also reach input.js; it ignores them while a tool is active
  stage.addEventListener('touchstart', (e) => { if (tool) e.preventDefault(); }, { passive: false });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && tool && !e.ctrlKey) { e.stopPropagation(); setTool(tool); } }, true);

  document.querySelectorAll('[data-tool]').forEach((b) => b.addEventListener('click', () => setTool(b.dataset.tool)));
  document.querySelectorAll('[data-color]').forEach((b) => { b.style.setProperty('--c', b.dataset.color); b.addEventListener('click', () => setColor(b.dataset.color)); });
  document.querySelectorAll('[data-draw-clear]').forEach((b) => b.addEventListener('click', () => { clearAll(); send({ t: 'draw', op: 'clear' }); hint('Dessins effacés'); }));
  document.querySelectorAll('[data-draw-undo]').forEach((b) => b.addEventListener('click', () => { if (cur) endStroke(); strokes.pop(); redraw(); send({ t: 'draw', op: 'undo' }); }));
  setColor(color);

  function clearAll() { strokes.length = 0; cur = null; laser = null; redraw(); }

  return {
    setTool, clearAll,
    get tool() { return tool; },
    set echo(v) { echo = v; redraw(); },
    set available(v) { document.querySelectorAll('.draw-ui').forEach((el) => { el.hidden = !v; }); if (!v && tool) setTool(tool); },
    set screenIndex(fn) { getScreenIndex = fn; },
  };
}
