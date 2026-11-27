// Turning what the PC sends into pixels on the stage:
//  - WebCodecs: H.264 / HEVC / AV1 access units from the WebSocket (1.4 framing or 2.0 extended framing);
//  - a <video> element fed by the WebRTC track (direct link);
//  - JPEG images, one at a time, when nothing else works.
const CODEC_BYTE = { 1: 'avc1', 2: 'hvc1', 3: 'av01' };

export async function supportedCodecs() {
  const out = [];
  if (!('VideoDecoder' in window)) return out;
  const tests = [['avc1', 'avc1.42E01F'], ['hvc1', 'hvc1.1.6.L120.B0'], ['av01', 'av01.0.08M.08']];
  for (const [name, codec] of tests) {
    try { if ((await VideoDecoder.isConfigSupported({ codec, optimizeForLatency: true })).supported) out.push(name); } catch {}
  }
  return out;
}

function h264Codec(data) {
  for (let i = 0; i + 6 < data.length; i++) {
    if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1 && (data[i + 3] & 0x1F) === 7) {
      const hex = (b) => b.toString(16).padStart(2, '0').toUpperCase();
      return 'avc1.' + hex(data[i + 4]) + hex(data[i + 5]) + hex(data[i + 6]);
    }
  }
  return null;
}
function hevcCodec(data) {
  // VPS/SPS carry the profile; a generic Main string decodes every stream we send
  for (let i = 0; i + 5 < data.length; i++) {
    if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1 && ((data[i + 3] >> 1) & 0x3F) === 33) return 'hvc1.1.6.L153.B0';
  }
  return null;
}

export function createWebCodecsDecoder({ canvas, onFrame, onNeedKey, onError }) {
  const ctx2d = canvas.getContext('2d', { alpha: false, desynchronized: true });
  let decoder = null, codec = '', waitingKey = true, ts = 0, lastKeyReq = 0;
  let lastTs = 0;

  function requestKey() {
    const now = performance.now();
    if (now - lastKeyReq < 1000) return;
    lastKeyReq = now;
    waitingKey = true;
    onNeedKey();
  }
  function make(c) {
    close();
    const d = new VideoDecoder({
      output: (frame) => {
        if (canvas.width !== frame.displayWidth || canvas.height !== frame.displayHeight) {
          canvas.width = frame.displayWidth; canvas.height = frame.displayHeight;
        }
        ctx2d.drawImage(frame, 0, 0);
        frame.close();
        onFrame(frame.displayWidth, frame.displayHeight);
      },
      error: (e) => { console.warn('decoder:', e); if (decoder === d) { decoder = null; codec = ''; requestKey(); onError && onError(e); } },
    });
    d.configure({ codec: c, optimizeForLatency: true });
    decoder = d; codec = c;
  }
  function close() {
    if (decoder && decoder.state !== 'closed') { try { decoder.close(); } catch {} }
    decoder = null; codec = ''; waitingKey = true;
  }
  // Returns true when the message was a video frame it handled.
  function push(bytes) {
    let key, data, name = 'avc1', tsUs = 0;
    if (bytes[0] === 1 || bytes[0] === 2) { key = bytes[0] === 1; data = bytes.subarray(1); }
    else if (bytes[0] === 4 && bytes.length > 11) {
      name = CODEC_BYTE[bytes[1]] || 'avc1';
      key = (bytes[2] & 1) !== 0;
      const dv = new DataView(bytes.buffer, bytes.byteOffset + 3, 8);
      tsUs = Number(dv.getBigUint64(0, true));
      data = bytes.subarray(11);
    } else return false;
    if (waitingKey && !key) return true;
    if (key) {
      const c = name === 'avc1' ? (h264Codec(data) || codec || 'avc1.42E01F')
        : name === 'hvc1' ? (hevcCodec(data) || codec || 'hvc1.1.6.L153.B0') : (codec || 'av01.0.08M.08');
      if (!decoder || decoder.state === 'closed' || c !== codec) make(c);
    }
    if (!decoder) return true;
    if (!key && decoder.decodeQueueSize > 6) { requestKey(); return true; } // falling behind: skip to the next keyframe
    try {
      // timestamps only need to grow; the PC's clock is used when it sends one
      if (tsUs > lastTs) ts = tsUs; else ts += 16667;
      lastTs = ts;
      decoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp: ts, data }));
      waitingKey = false;
    } catch (e) { console.warn('decode:', e); requestKey(); }
    return true;
  }
  return { push, close, get active() { return !!decoder; } };
}

export function createJpeg({ img, onShown }) {
  let objectUrl = null;
  function show(blob) {
    const url = URL.createObjectURL(blob);
    img.onload = () => { if (objectUrl) URL.revokeObjectURL(objectUrl); objectUrl = url; onShown(img.naturalWidth, img.naturalHeight); };
    img.onerror = () => { URL.revokeObjectURL(url); onShown(0, 0); };
    img.src = url;
  }
  return { show };
}
