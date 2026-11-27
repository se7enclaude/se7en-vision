// Where the PC's picture sits on the stage (letterboxed, zoomable, pannable) and how to go from a point on
// the stage to a 0..1 position on the PC's screen — or, in "all screens" mode, on the right screen.
export function createLayout({ stage, frame, zoomResetBtn }) {
  let imgW = 16, imgH = 9, bw = 0, bh = 0, zoom = 1, tx = 0, ty = 0;
  let mosaic = null; // { w, h, cells:[{i,x,y,w,h}] } when showing all screens
  const listeners = new Set();

  function setImage(w, h, mosaicLayout = null) {
    imgW = Math.max(1, w); imgH = Math.max(1, h); mosaic = mosaicLayout;
    layout();
  }
  function layout() {
    const sw = stage.clientWidth, sh = stage.clientHeight;
    if (!sw || !sh) return;
    const s = Math.min(sw / imgW, sh / imgH);
    bw = imgW * s; bh = imgH * s;
    frame.style.width = bw + 'px';
    frame.style.height = bh + 'px';
    clamp();
  }
  function clamp() {
    const sw = stage.clientWidth, sh = stage.clientHeight;
    zoom = Math.min(5, Math.max(1, zoom));
    const w = bw * zoom, h = bh * zoom;
    tx = w <= sw ? (sw - w) / 2 : Math.min(0, Math.max(sw - w, tx));
    ty = h <= sh ? (sh - h) / 2 : Math.min(0, Math.max(sh - h, ty));
    frame.style.transform = `translate(${tx}px, ${ty}px) scale(${zoom})`;
    if (zoomResetBtn) zoomResetBtn.hidden = zoom <= 1.01;
    listeners.forEach((fn) => fn());
  }
  if (zoomResetBtn) zoomResetBtn.addEventListener('click', () => { zoom = 1; clamp(); });
  new ResizeObserver(layout).observe(stage);

  const stagePoint = (ev) => { const r = stage.getBoundingClientRect(); return { x: ev.clientX - r.left, y: ev.clientY - r.top }; };
  // 0..1 across the whole picture (mosaic or single screen)
  const toPicture = (p) => ({ x: Math.min(1, Math.max(0, (p.x - tx) / (bw * zoom))), y: Math.min(1, Math.max(0, (p.y - ty) / (bh * zoom))) });
  // { x, y } 0..1 on the PC screen to click, plus the screen index in mosaic mode (null in a gap)
  function toScreen(p) {
    const f = toPicture(p);
    if (!mosaic) return { x: f.x, y: f.y };
    const px = f.x * mosaic.w, py = f.y * mosaic.h;
    for (const c of mosaic.cells) {
      if (px >= c.x && px < c.x + c.w && py >= c.y && py < c.y + c.h)
        return { x: Math.min(1, Math.max(0, (px - c.x) / c.w)), y: Math.min(1, Math.max(0, (py - c.y) / c.h)), s: c.i };
    }
    return null;
  }
  const onImage = (p) => p.x >= tx && p.y >= ty && p.x <= tx + bw * zoom && p.y <= ty + bh * zoom;
  // PC screen pixel (virtual desktop) → stage pixel, for the cursor overlay
  function fromDesktop(x, y, screen, screens) {
    let fx, fy;
    if (mosaic) {
      const c = mosaic.cells.find((k) => k.i === screen);
      const sc = screens && screens.find((k) => k.i === screen);
      if (!c || !sc) return null;
      fx = (c.x + (x - sc.x) / sc.w * c.w) / mosaic.w;
      fy = (c.y + (y - sc.y) / sc.h * c.h) / mosaic.h;
    } else {
      const sc = screens && screens.find((k) => k.i === screen);
      if (sc) { fx = (x - sc.x) / sc.w; fy = (y - sc.y) / sc.h; }
      else { fx = x / imgW; fy = y / imgH; }
    }
    return { x: tx + fx * bw * zoom, y: ty + fy * bh * zoom, scale: (bw * zoom) / imgW };
  }

  return {
    setImage, layout, clamp, stagePoint, toPicture, toScreen, onImage, fromDesktop,
    onChange(fn) { listeners.add(fn); },
    get zoom() { return zoom; }, set zoom(v) { zoom = v; },
    get tx() { return tx; }, set tx(v) { tx = v; },
    get ty() { return ty; }, set ty(v) { ty = v; },
    get bw() { return bw; }, get bh() { return bh; },
    get imgW() { return imgW; }, get imgH() { return imgH; },
    get mosaic() { return mosaic; },
  };
}
