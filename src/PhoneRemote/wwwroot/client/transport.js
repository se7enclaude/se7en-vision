// Routes each message family to the best open channel: a WebRTC data channel when the direct link is up,
// otherwise the WebSocket. The PC accepts every message on both, so a fallback needs nothing special.
export const FAMILY = {
  ctl: new Set(['move', 'abs', 'down', 'up', 'click', 'wheel', 'text', 'key', 'kd', 'ku', 'clip', 'power', 'screen', 'video', 'keyframe', 'curreq']),
  draw: new Set(['draw']),
  file: new Set(['fopen', 'fok', 'frej', 'fack', 'fdone', 'fcancel']),
  stat: new Set(['stat']),
};

export function createTransport(ws) {
  const channels = {}; // name → RTCDataChannel (set by rtc.js)
  let mode = 'ws';
  function familyOf(t) {
    for (const [name, set] of Object.entries(FAMILY)) if (set.has(t)) return name;
    return 'ws';
  }
  function send(obj) {
    const dc = channels[familyOf(obj.t)];
    if (dc && dc.readyState === 'open') {
      try { dc.send(JSON.stringify(obj)); return true; } catch {}
    }
    return ws.send(obj);
  }
  function sendBinary(family, buf) {
    const dc = channels[family];
    if (dc && dc.readyState === 'open') { try { dc.send(buf); return true; } catch {} }
    ws.sendBinary(buf);
    return false;
  }
  return {
    send, sendBinary,
    get mode() { return mode; },
    set mode(m) { mode = m; },
    attach(name, dc) { channels[name] = dc; },
    detachAll() { for (const k of Object.keys(channels)) delete channels[k]; mode = 'ws'; },
    channel(name) { return channels[name]; },
  };
}
