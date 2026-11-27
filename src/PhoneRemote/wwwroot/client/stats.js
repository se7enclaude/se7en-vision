// Ping · frames per second · bitrate, measured here, shown in a small badge, and reported to the PC once a
// second so it can adapt the bitrate.
export function createStats({ badge, send, ws, getRtcStats }) {
  let frames = 0, lastAt = performance.now(), fps = 0, kbps = 0, rtt = 0, mode = 'ws', shown = false, extra = '';
  let dropped = 0, lastDecoded = 0, lastBytes = 0, lastDropped = 0, path = '';
  let host = null;

  function onFrame() { frames++; }
  async function tick() {
    const now = performance.now();
    const dt = Math.max(1, now - lastAt);
    lastAt = now;
    let f = frames * 1000 / dt; frames = 0;
    let bytes = ws.takeBytes();
    rtt = ws.rtt;
    const rs = getRtcStats && await getRtcStats();
    if (rs) {
      if (rs.rtt != null) rtt = rs.rtt;
      if (rs.bytes != null) { bytes = rs.bytes - lastBytes; lastBytes = rs.bytes; }
      let decodedNow = 0;
      if (rs.framesDecoded != null) { decodedNow = rs.framesDecoded - lastDecoded; f = decodedNow * 1000 / dt; lastDecoded = rs.framesDecoded; }
      // frames the browser threw away this second, as a share of what arrived (not the running total)
      const droppedNow = Math.max(0, (rs.dropped || 0) - lastDropped); lastDropped = rs.dropped || 0;
      dropped = Math.round(droppedNow * 100 / Math.max(1, decodedNow + droppedNow));
    }
    fps = Math.round(f); kbps = Math.round(bytes * 8 / dt);
    render();
    send({ t: 'stat', viewer: { rtt, fps, kbps, drop: dropped, via: mode } });
  }
  function render() {
    if (!badge) return;
    badge.hidden = !shown;
    if (!shown) return;
    const via = mode === 'rtc' ? (path === 'local' ? 'Direct · local' : 'Direct') : 'Tunnel';
    const bw = kbps >= 1000 ? (kbps / 1000).toFixed(1).replace('.', ',') + ' Mb/s' : kbps + ' kb/s';
    badge.textContent = `${via} · ${rtt} ms · ${fps} i/s · ${bw}${extra}`;
  }
  const timer = setInterval(() => { tick().catch(() => {}); }, 1000);
  return {
    onFrame, render,
    set mode(m) { mode = m; if (m !== 'rtc') { lastDecoded = 0; lastBytes = 0; lastDropped = 0; dropped = 0; } render(); },
    set path(p) { path = p || ''; },
    set shown(v) { shown = v; render(); },
    get shown() { return shown; },
    set host(h) { host = h; extra = h && h.enc ? ` · ${h.enc.replace(/_.*/, '').toUpperCase()}` : ''; },
    get summary() { return { rtt, fps, kbps, mode }; },
    stop() { clearInterval(timer); },
  };
}
