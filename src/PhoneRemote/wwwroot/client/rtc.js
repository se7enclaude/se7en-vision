// The direct link: a WebRTC peer connection to the PC. This page makes the offer (video receive-only plus five
// data channels); the PC answers through the WebSocket, ICE finds a path through the home routers, and the
// video then arrives as RTP, decoded by the browser's own hardware decoder into a <video> element.
// When no path is found in time, everything stays on the WebSocket as before.
const ICE = [{ urls: 'stun:stun.l.google.com:19302' }, { urls: 'stun:stun.cloudflare.com:3478' }];
const CHANNELS = [
  ['ctl', { ordered: true }],
  ['cur', { ordered: false, maxRetransmits: 0 }],
  ['draw', { ordered: true }],
  ['file', { ordered: true }],
  ['stat', { ordered: false, maxRetransmits: 0 }],
];
const CONNECT_TIMEOUT = 10000;

export const rtcPossible = typeof RTCPeerConnection === 'function';

export function createRtc({ signal, transport, videoEl, onText, onBinary, onState, allowHevc }) {
  let pc = null, state = 'idle', timer = 0, path = '', codec = '';
  let statsTrack = null;

  function setState(s, extra) {
    if (state === s) return;
    state = s;
    onState(s, extra || {});
  }
  function pickCodecs(transceiver) {
    if (!RTCRtpReceiver.getCapabilities || !transceiver.setCodecPreferences) return;
    const all = RTCRtpReceiver.getCapabilities('video').codecs;
    const h264 = all.filter((c) => c.mimeType === 'video/H264' && /packetization-mode=1/.test(c.sdpFmtpLine || ''));
    // constrained baseline first: what the PC's encoder produces
    h264.sort((a, b) => (/42e01f/i.test(b.sdpFmtpLine) ? 1 : 0) - (/42e01f/i.test(a.sdpFmtpLine) ? 1 : 0));
    const hevc = allowHevc ? all.filter((c) => c.mimeType === 'video/H265') : [];
    const rtx = all.filter((c) => c.mimeType === 'video/rtx');
    const list = [...h264, ...hevc, ...rtx];
    if (list.length) try { transceiver.setCodecPreferences(list); } catch {}
  }

  async function start() {
    stop();
    setState('connecting');
    pc = new RTCPeerConnection({ iceServers: ICE, bundlePolicy: 'max-bundle' });
    const my = pc;
    for (const [name, init] of CHANNELS) {
      const dc = my.createDataChannel(name, init);
      dc.binaryType = 'arraybuffer';
      dc.onmessage = (e) => {
        if (typeof e.data === 'string') { let m; try { m = JSON.parse(e.data); } catch { return; } onText(m); }
        else onBinary(new Uint8Array(e.data));
      };
      dc.onopen = () => { transport.attach(name, dc); if (name === 'ctl') checkReady(); };
      dc.onclose = () => { if (pc === my) transport.detach(name); };
    }
    const tr = my.addTransceiver('video', { direction: 'recvonly' });
    pickCodecs(tr);
    try { if ('playoutDelayHint' in tr.receiver) tr.receiver.playoutDelayHint = 0; } catch {}
    try { if ('jitterBufferTarget' in tr.receiver) tr.receiver.jitterBufferTarget = 0; } catch {}
    my.ontrack = (e) => {
      statsTrack = e.receiver;
      videoEl.srcObject = e.streams[0] || new MediaStream([e.track]);
      videoEl.play().catch(() => {});
      const p = e.receiver.getParameters && e.receiver.getParameters();
      codec = p && p.codecs && p.codecs[0] ? p.codecs[0].mimeType.replace('video/', '') : '';
    };
    my.onicecandidate = (e) => { if (e.candidate && pc === my) signal({ t: 'rtc', sub: 'ice', cand: e.candidate.toJSON() }); };
    my.onconnectionstatechange = () => {
      if (pc !== my) return;
      const s = my.connectionState;
      if (s === 'connected') checkReady();
      else if (s === 'failed' || s === 'closed') fail('failed');
      else if (s === 'disconnected') { clearTimeout(timer); timer = setTimeout(() => { if (pc === my && my.connectionState !== 'connected') fail('failed'); }, 5000); }
    };
    timer = setTimeout(() => { if (pc === my && state !== 'connected') fail('timeout'); }, CONNECT_TIMEOUT);
    try {
      const offer = await my.createOffer();
      if (pc !== my) return;
      await my.setLocalDescription(offer);
      signal({ t: 'rtc', sub: 'offer', sdp: my.localDescription.sdp });
    } catch (e) { console.warn('rtc offer:', e); fail('offer'); }
  }
  function checkReady() {
    if (!pc || state === 'connected') return;
    const ctl = transport.channel('ctl');
    if (pc.connectionState === 'connected' && ctl && ctl.readyState === 'open') {
      clearTimeout(timer);
      describePath().then((p) => { path = p; setState('connected', { path, codec }); });
    }
  }
  async function describePath() {
    try {
      const st = await pc.getStats();
      let pair = null;
      st.forEach((r) => { if (r.type === 'candidate-pair' && (r.selected || r.nominated) && r.state === 'succeeded') pair = r; });
      if (!pair) return '';
      const local = st.get(pair.localCandidateId), remote = st.get(pair.remoteCandidateId);
      if (local && remote && local.candidateType === 'host' && remote.candidateType === 'host') return 'local';
      if (remote && remote.candidateType === 'relay') return 'relay';
      return 'direct';
    } catch { return ''; }
  }
  function fail(why) {
    if (!pc) return;
    const was = state;
    stop();
    setState('failed', { why, was });
  }
  function stop() {
    clearTimeout(timer);
    if (pc) { const old = pc; pc = null; try { old.close(); } catch {} }
    transport.detachAll();
    statsTrack = null;
    if (videoEl.srcObject) { videoEl.srcObject = null; }
    if (state !== 'idle' && state !== 'failed') state = 'idle';
  }
  function onMessage(m) {
    if (!pc) return;
    const my = pc;
    if (m.sub === 'answer') {
      my.setRemoteDescription({ type: 'answer', sdp: m.sdp }).catch((e) => { console.warn('rtc answer:', e); fail('answer'); });
    } else if (m.sub === 'ice' && m.cand) {
      my.addIceCandidate(m.cand).catch(() => {});
    } else if (m.sub === 'fail') {
      fail(m.why || 'refused');
    } else if (m.sub === 'state') {
      if (m.path) path = m.path;
      if (m.s === 'failed' || m.s === 'closed') fail('host');
    }
  }
  async function getStats() {
    if (!pc || state !== 'connected') return null;
    try {
      const st = await pc.getStats();
      const out = { rtt: null, bytes: null, framesDecoded: null, dropped: 0 };
      st.forEach((r) => {
        if (r.type === 'inbound-rtp' && r.kind === 'video') { out.bytes = r.bytesReceived; out.framesDecoded = r.framesDecoded; out.dropped = r.framesDropped || 0; out.lost = r.packetsLost || 0; }
        if (r.type === 'candidate-pair' && (r.selected || r.nominated) && r.currentRoundTripTime != null) out.rtt = Math.round(r.currentRoundTripTime * 1000);
      });
      return out;
    } catch { return null; }
  }
  return {
    start, stop, onMessage, getStats,
    get state() { return state; },
    get path() { return path; },
    get codec() { return codec; },
    get active() { return state === 'connected'; },
  };
}
