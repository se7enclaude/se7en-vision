// The PC's mouse pointer, drawn here as soon as the PC reports where it is — long before the video shows it.
// Shapes arrive once as PNGs (curimg) and are cached by id; the position is eased toward the latest report.
export function createCursor({ canvas, layout, getScreens, send }) {
  const ctx = canvas.getContext('2d');
  const shapes = new Map(); // id → { img, hx, hy, w, h, ready }
  let target = null, shown = null, visible = false, shapeId = '', enabled = false, raf = 0, lastMove = 0, hidden = false;
  const asked = new Map();

  function resize() {
    const r = canvas.parentElement.getBoundingClientRect();
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const w = Math.round(r.width * dpr), h = Math.round(r.height * dpr);
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
    canvas.style.width = r.width + 'px'; canvas.style.height = r.height + 'px';
    draw();
  }
  new ResizeObserver(resize).observe(canvas.parentElement);
  layout.onChange(() => draw());

  function onShape(m) {
    const img = new Image();
    const s = { img, hx: m.hx || 0, hy: m.hy || 0, w: m.w || 32, h: m.h || 32, ready: false };
    img.onload = () => { s.ready = true; draw(); };
    img.src = 'data:image/png;base64,' + m.png;
    shapes.set(m.s, s);
    asked.delete(m.s);
  }
  function onMove(m) {
    target = { x: m.x, y: m.y };
    visible = m.v !== false;
    shapeId = m.s || '';
    lastMove = performance.now();
    if (shapeId && !shapes.has(shapeId) && !asked.has(shapeId)) { asked.set(shapeId, 1); send({ t: 'curreq', s: shapeId }); }
    if (!raf) raf = requestAnimationFrame(tick);
  }
  function tick() {
    raf = 0;
    if (!target) return;
    if (!shown) shown = { ...target };
    else {
      // ease toward the latest position: smooth at 120 Hz feeds, snappy when reports are sparse
      const k = performance.now() - lastMove > 100 ? 1 : 0.55;
      shown.x += (target.x - shown.x) * k; shown.y += (target.y - shown.y) * k;
    }
    draw();
    if (Math.abs(shown.x - target.x) > 0.5 || Math.abs(shown.y - target.y) > 0.5) raf = requestAnimationFrame(tick);
  }
  function currentScreen() {
    const screens = getScreens();
    if (!shown || !screens) return null;
    return screens.find((s) => shown.x >= s.x && shown.x < s.x + s.w && shown.y >= s.y && shown.y < s.y + s.h) || null;
  }
  function draw() {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    if (!enabled || hidden || !visible || !shown) return;
    const sc = currentScreen();
    if (!sc) return;
    const p = layout.fromDesktop(shown.x, shown.y, sc.i, getScreens());
    if (!p) return;
    const dpr = canvas.width / Math.max(1, canvas.clientWidth);
    const s = shapes.get(shapeId);
    ctx.save();
    ctx.scale(dpr, dpr);
    if (s && s.ready) {
      const k = Math.max(0.5, Math.min(1.5, p.scale)) * (window.devicePixelRatio > 1.5 ? 1 : 1); // keep it near native size
      ctx.drawImage(s.img, p.x - s.hx * k, p.y - s.hy * k, s.w * k, s.h * k);
    } else {
      // generic arrow while the shape image is on its way
      ctx.beginPath(); ctx.moveTo(p.x, p.y); ctx.lineTo(p.x, p.y + 16); ctx.lineTo(p.x + 4.5, p.y + 12.5); ctx.lineTo(p.x + 7.5, p.y + 18.5);
      ctx.lineTo(p.x + 10, p.y + 17.5); ctx.lineTo(p.x + 7, p.y + 11.5); ctx.lineTo(p.x + 12, p.y + 11.5); ctx.closePath();
      ctx.fillStyle = '#fff'; ctx.fill(); ctx.lineWidth = 1; ctx.strokeStyle = '#000'; ctx.stroke();
    }
    ctx.restore();
  }
  return {
    onShape, onMove, draw,
    set enabled(v) { enabled = v; canvas.hidden = !v; draw(); },
    get enabled() { return enabled; },
    set hidden(v) { hidden = v; draw(); },
    reset() { shapes.clear(); asked.clear(); target = shown = null; draw(); },
  };
}
