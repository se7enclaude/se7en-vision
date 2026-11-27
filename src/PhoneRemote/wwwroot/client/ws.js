// The WebSocket to the PC: control messages, signalling, and the video when there is no direct link.
// Reconnects by itself; the owner is told about close codes it must act on (4001 revoked, 4002 replaced).
export function createWs({ base, onOpen, onText, onBinary, onClose }) {
  let ws = null, retryTimer = 0, pingTimer = 0;
  let bytesIn = 0, lastRtt = 0, rttMin = Infinity;
  const url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + base + '/ws';

  function connect() {
    stop();
    ws = new WebSocket(url);
    ws.binaryType = 'arraybuffer';
    ws.onopen = () => {
      pingTimer = setInterval(() => send({ t: 'ping', ts: performance.now() }), 2000);
      onOpen();
    };
    ws.onmessage = (e) => {
      if (typeof e.data !== 'string') { bytesIn += e.data.byteLength; onBinary(new Uint8Array(e.data)); return; }
      let m;
      try { m = JSON.parse(e.data); } catch { return; }
      if (m.t === 'pong') {
        lastRtt = Math.round(performance.now() - m.ts);
        rttMin = Math.min(rttMin, lastRtt);
        send({ t: 'rtt', ms: lastRtt });
      }
      onText(m);
    };
    ws.onclose = (e) => {
      const was = ws; ws = null;
      clearInterval(pingTimer);
      onClose(e.code, was);
    };
  }

  function stop() {
    clearTimeout(retryTimer);
    clearInterval(pingTimer);
    if (ws) { ws.onclose = ws.onmessage = ws.onerror = ws.onopen = null; try { ws.close(); } catch {} ws = null; }
  }

  function send(obj) { if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj)); return !!ws && ws.readyState === 1; }
  function sendBinary(buf) { if (ws && ws.readyState === 1) ws.send(buf); }
  function retry(fn, ms) { clearTimeout(retryTimer); retryTimer = setTimeout(fn, ms); }

  return {
    connect, stop, send, sendBinary, retry,
    get open() { return !!ws && ws.readyState === 1; },
    get rtt() { return lastRtt; },
    get rttMin() { return rttMin === Infinity ? 0 : rttMin; },
    takeBytes() { const b = bytesIn; bytesIn = 0; return b; },
  };
}
