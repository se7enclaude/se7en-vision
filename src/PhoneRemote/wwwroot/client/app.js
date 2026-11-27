// se7en Vision — the viewer page, for an iPhone (touch, trackpad, joystick) or a PC (mouse and keyboard).
// Auth (code, QR key, cookie) is unchanged from 1.4. Everything after that speaks protocol v2 and falls back
// to 1.4 behaviour against an older PC.
import { store, readQuality, MODES } from './settings.js';
import { createWs } from './ws.js';
import { createTransport } from './transport.js';
import { createLayout } from './layout.js';
import { supportedCodecs, createWebCodecsDecoder, createJpeg } from './decoder.js';
import { createCursor } from './cursor.js';
import { createStats } from './stats.js';
import { createInput } from './input.js';
import { createRtc, rtcPossible } from './rtc.js';
import { createDraw } from './draw.js';
import { createFiles } from './files.js';

const $ = (id) => document.getElementById(id);
const base = location.pathname.replace(/\/+$/, '');
let qrKey = (/[#&]k=([0-9a-f]{16,64})/.exec(location.hash) || [])[1] || '';
const params = new URLSearchParams(location.search);
if (location.hash) history.replaceState(null, '', location.pathname + location.search);

const ua = navigator.userAgent;
const ios = /iPhone|iPad|iPod/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
const isDesktop = params.get('ui') === 'desktop' || (!ios && !/Android/.test(ua) && matchMedia('(pointer: fine)').matches && navigator.maxTouchPoints === 0);
document.body.classList.toggle('desktop', isDesktop);
document.body.classList.toggle('phone', !isDesktop);

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
  requestAnimationFrame(() => layout.layout());
}
window.visualViewport?.addEventListener('resize', syncViewport);
window.visualViewport?.addEventListener('scroll', syncViewport);
window.addEventListener('resize', syncViewport);
document.addEventListener('gesturestart', (e) => e.preventDefault());

// ---------- Views ----------
const views = ['loadView', 'pinView', 'msgView', 'ctrlView'];
function show(id) { views.forEach((v) => { $(v).hidden = v !== id; }); }
function message(title, text, { icon = 'link-2-off', violet = false, button = null, onButton = null } = {}) {
  stopSession();
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

// ---------- Startup / auth (unchanged from 1.4) ----------
async function boot() {
  show('loadView');
  let res;
  try { res = await fetch(base + '/me', { cache: 'no-store', credentials: 'same-origin' }); }
  catch { return message('PC injoignable', 'Vérifie ta connexion, puis réessaie.', { icon: 'wifi-off', button: 'Réessayer', onButton: boot }); }
  if (res.status === 404) return expired();
  if (!res.ok) return message('PC injoignable', 'se7en Vision est peut-être fermé sur le PC.', { icon: 'wifi-off', button: 'Réessayer', onButton: boot });
  const me = await res.json();
  if (me.authed) return startControl();
  if (qrKey && await tryQrKey()) return;
  showPin();
}
async function tryQrKey() {
  const key = qrKey; qrKey = '';
  try {
    const res = await fetch(base + '/qr', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ key }) });
    if (res.status === 404) { expired(); return true; }
    const r = await res.json();
    if (r.result === 'ok') { startControl(); return true; }
    if (r.result === 'busy') { message('Déjà utilisé', 'Un autre appareil contrôle ce PC en ce moment.', { icon: 'smartphone', button: 'Réessayer', onButton: boot }); return true; }
  } catch {}
  return false;
}
const pinInput = $('pinInput');
const boxes = [...document.querySelectorAll('.pin-boxes .box')];
function renderPin() {
  const v = pinInput.value;
  boxes.forEach((b, i) => { b.textContent = v[i] || ''; b.classList.toggle('cur', i === Math.min(v.length, 5) && document.activeElement === pinInput); });
  $('pinBtn').disabled = v.length !== 6;
}
pinInput.addEventListener('input', () => {
  pinInput.value = pinInput.value.replace(/\D/g, '').slice(0, 6);
  $('pinMsg').textContent = ''; $('pinMsg').classList.remove('err');
  renderPin();
  if (pinInput.value.length === 6) submitPin();
});
pinInput.addEventListener('focus', renderPin);
pinInput.addEventListener('blur', renderPin);
$('pinBtn').addEventListener('click', submitPin);
function showPin() { show('pinView'); pinInput.value = ''; renderPin(); setTimeout(() => pinInput.focus(), 50); }
let submitting = false;
async function submitPin() {
  if (submitting || pinInput.value.length !== 6) return;
  submitting = true;
  $('pinBtn').disabled = true;
  $('pinMsg').textContent = 'Vérification…'; $('pinMsg').classList.remove('err');
  try {
    const res = await fetch(base + '/pin', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ pin: pinInput.value }) });
    if (res.status === 404) return expired();
    const r = await res.json();
    if (r.result === 'ok') { pinInput.blur(); startControl(); return; }
    if (r.result === 'locked') return expired('Trop de codes faux : le lien a été désactivé. Un nouveau QR code est affiché sur le PC.');
    if (r.result === 'busy') return message('Déjà utilisé', 'Un autre appareil contrôle ce PC en ce moment.', { icon: 'smartphone', button: 'Réessayer', onButton: boot });
    $('pinMsg').textContent = `Code incorrect. Encore ${r.attemptsLeft} essai${r.attemptsLeft > 1 ? 's' : ''}.`;
    $('pinMsg').classList.add('err');
    const el = $('pinBoxes'); el.classList.remove('shake'); void el.offsetWidth; el.classList.add('shake');
    pinInput.value = ''; renderPin();
  } catch {
    $('pinMsg').textContent = 'Connexion perdue. Réessaie.'; $('pinMsg').classList.add('err');
  } finally { submitting = false; }
}

// ---------- Stage & helpers ----------
const stage = $('stage');
const layout = createLayout({ stage, frame: $('frame'), zoomResetBtn: $('zoomResetBtn') });
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
  hintTimer = setTimeout(() => c.classList.remove('show'), 1600);
}
function openSheet() { $('sheet').hidden = false; $('scrim').hidden = false; }
function closeSheet() { $('sheet').hidden = true; $('scrim').hidden = true; }
$('moreBtn').addEventListener('click', openSheet);
$('scrim').addEventListener('click', closeSheet);
$('panelBtn').addEventListener('click', () => {
  const on = $('ctrlView').classList.toggle('show-tools');
  $('panelBtn').classList.toggle('is-active', on);
});
$('fullBtn').addEventListener('click', () => {
  if (document.fullscreenElement) document.exitFullscreen(); else document.documentElement.requestFullscreen?.();
});

// ---------- Connection ----------
let info = null;          // last info from the PC
let hostV2 = false;       // the PC understands protocol 2
let alive = false, powerPending = null, codecs = [];
const ws = createWs({ base, onOpen, onText, onBinary, onClose });
const transport = createTransport(ws);
const send = (obj) => transport.send(obj);

const stats = createStats({ badge: $('statsBadge'), send, ws, getRtcStats: () => rtc.getStats() });
stats.shown = store.get('stats', '0') === '1';
$('statsToggle').checked = stats.shown;
$('statsToggle').addEventListener('change', () => { stats.shown = $('statsToggle').checked; store.set('stats', stats.shown ? '1' : '0'); });

// ---------- Direct link ----------
let via = 'ws';
let rtcWanted = rtcPossible && store.get('rtc', '1') === '1';
const rtcVideo = $('rtcVideo');
const rtc = createRtc({
  signal: (m) => ws.send(m), transport, videoEl: rtcVideo, onText, onBinary, allowHevc: false,
  onState: (s, x) => {
    if (s === 'connected') {
      via = 'rtc'; transport.mode = 'rtc'; stats.mode = 'rtc'; stats.path = x.path;
      hint(x.path === 'local' ? 'Connexion directe (réseau local)' : 'Connexion directe établie');
      if (video.active) { showSurface('rtc'); send({ t: 'video', on: true, mode: quality, via: 'rtc' }); }
    } else if (s === 'failed') {
      const had = via === 'rtc';
      via = 'ws'; transport.mode = 'ws'; stats.mode = 'ws'; stats.path = '';
      if (x.why === 'disabled' || x.why === 'refused') rtcWanted = false;
      else if (x.was !== 'connected') hint('Pas de chemin direct : passage par le tunnel');
      else hint('Connexion directe perdue : passage par le tunnel');
      if (video.active) { showSurface('ws'); decoder.close(); send({ t: 'video', on: true, mode: quality, via: 'ws' }); }
    }
    renderLive();
  },
});
rtcVideo.addEventListener('resize', () => { if (via === 'rtc' && rtcVideo.videoWidth && !layout.mosaic && !(info && info.w === rtcVideo.videoWidth)) layout.setImage(rtcVideo.videoWidth, rtcVideo.videoHeight); });
function maybeStartRtc() {
  if (!hostV2 || !rtcWanted || !alive || rtc.state === 'connecting' || rtc.state === 'connected') return;
  if (!(info && Array.isArray(info.features) && info.features.includes('rtc'))) return;
  rtc.start();
}
$('rtcToggle').checked = rtcWanted;
$('rtcToggle').addEventListener('change', () => {
  rtcWanted = rtcPossible && $('rtcToggle').checked;
  store.set('rtc', rtcWanted ? '1' : '0');
  if (!rtcWanted && rtc.state !== 'idle') { send({ t: 'rtc', sub: 'close' }); rtc.stop(); via = 'ws'; transport.mode = 'ws'; stats.mode = 'ws'; if (video.active) { showSurface('ws'); send({ t: 'video', on: true, mode: quality, via: 'ws' }); } }
  else maybeStartRtc();
});

const cursor = createCursor({ canvas: $('cursorLayer'), layout, getScreens: () => (info && info.screens) || null, send });

const input = createInput({
  stage, layout, send, isDesktop, store,
  ui: { ctrl: $('ctrlView'), app, hint, ripple, closeSheet },
  joy: { pad: $('joypad'), zone: $('joyZone'), base: $('joyBase'), knob: $('joyKnob'), speed: $('joySpeed'), main: $('jbMain'), scroll: $('joyScroll') },
  keys: { kbd: $('kbd'), panel: $('keys'), speed: $('speed') },
  getScreen: () => (info ? info.screen : 0),
});

const files = createFiles({
  stage, panel: $('xferPanel'), send, hint, isDesktop,
  sendBinary: (family, buf) => sendBinaryFlow(family, buf),
});
// a chunk goes on the "file" data channel when it is open (waiting when its buffer is full), else on the WebSocket
async function sendBinaryFlow(family, buf) {
  const dc = transport.channel(family);
  if (dc && dc.readyState === 'open') {
    while (dc.bufferedAmount > 1024 * 1024) await new Promise((r) => setTimeout(r, 10));
    try { dc.send(buf); return; } catch {}
  }
  ws.sendBinary(buf);
}

const draw = createDraw({ stage, canvas: $('drawLayer'), layout, send, input, hint, store, getScreens: () => (info && info.screens) || null });
draw.screenIndex = () => (info ? info.screen : 0);

function startControl() {
  show('ctrlView');
  requestAnimationFrame(() => layout.layout());
  connect();
  showSafariTip();
  if (isDesktop) hint('Clique dans l’écran pour envoyer ton clavier · Ctrl+Maj+Échap pour le reprendre');
}
function connect() {
  setLive(false, 'Connexion…');
  ws.connect();
}
function stopSession() {
  files.reset();
  draw.clearAll();
  if (draw.tool) draw.setTool(draw.tool);
  ws.stop();
  rtc.stop();
  via = 'ws'; transport.mode = 'ws'; stats.mode = 'ws';
  alive = false;
  resetVideo();
  cursor.reset();
  cursor.enabled = false;
}
async function onOpen() {
  alive = true;
  setLive(true, '');
  codecs = await codecsReady;
  send({
    t: 'hello', v: 2, ua: isDesktop ? 'desktop' : 'phone', rtc: rtcWanted, codecs,
    screen: { w: screen.width, h: screen.height, dpr: window.devicePixelRatio || 1 },
    caps: ['cur', 'stat', 'kd'],
  });
  startPicture();
}
function onText(m) {
  switch (m.t) {
    case 'info': onInfo(m); break;
    case 'same': framePending = false; setTimeout(requestFrame, m.blocked ? 1000 : 120); break;
    case 'pong': renderLive(); break;
    case 'video': onVideoState(m); break;
    case 'cur': cursor.onMove(m); break;
    case 'curimg': cursor.onShape(m); break;
    case 'stat': if (m.host) { stats.host = m.host; hostStat = m.host; renderLive(); } break;
    case 'rtc': rtc.onMessage(m); break;
    case 'fopen': case 'fok': case 'frej': case 'fack': case 'fdone': case 'fcancel': files.onText(m); break;
    case 'power': onPowerAck(); break;
    case 'bye': byeWhy = m.why || ''; break;
  }
}
let byeWhy = '', hostStat = null;
function onBinary(bytes) {
  if (bytes[0] === 0xFF) { jpeg.show(new Blob([bytes], { type: 'image/jpeg' })); return; }
  if (bytes[0] === 3) { files.onBinary(bytes); return; }
  if (video.active && decoder.push(bytes)) return;
}
function onClose(code) {
  alive = false; framePending = false;
  resetVideo();
  rtc.stop();
  via = 'ws'; transport.mode = 'ws'; stats.mode = 'ws';
  cursor.enabled = false;
  if (powerPending) return powerDone();
  if (code === 4001) return message('Accès fermé', 'Le PC a coupé l’accès. Scanne le nouveau QR code affiché sur l’écran.', { icon: 'unplug' });
  if (code === 4002 || byeWhy === 'replaced') { byeWhy = ''; return message('Ouvert ailleurs', 'se7en Vision a été ouvert sur un autre appareil ou un autre onglet.', { icon: 'copy', violet: true, button: 'Reprendre ici', onButton: startControl }); }
  setLive(false, 'Reconnexion…');
  ws.retry(reconnect, 1500);
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
function renderLive() {
  const s = stats.summary;
  let text = (via === 'rtc' && s.rtt != null ? s.rtt : ws.rtt) + ' ms';
  if (video.active && s.fps) text += ' · ' + s.fps + ' i/s';
  if (hostStat && hostStat.plan && hostStat.plan.h) text += ' · ' + hostStat.plan.h + 'p';
  setLive(true, text);
}

// ---------- Info (screens, encoder, quality) ----------
function onInfo(m) {
  info = m;
  hostV2 = (m.v || 1) >= 2;
  $('pcName').textContent = m.pc || 'PC';
  document.title = 'se7en Vision — ' + (m.pc || 'PC');
  if (!m.screens) m.screens = [{ i: m.screen || 0, x: 0, y: 0, w: m.w, h: m.h, primary: true }];
  const mosaic = m.mosaic && m.mosaic.cells ? m.mosaic : null;
  if (mosaic) layout.setImage(mosaic.w, mosaic.h, mosaic); else layout.setImage(m.w, m.h, null);
  if (m.quality && MODES.includes(m.quality) && m.quality !== quality) { quality = m.quality; store.set('quality', quality); }
  renderQuality();
  renderScreens(m);
  cursor.enabled = hostV2 && Array.isArray(m.features) && m.features.includes('cur');
  draw.available = hostV2 && Array.isArray(m.features) && m.features.includes('draw');
  files.available = hostV2 && Array.isArray(m.features) && m.features.includes('file');
  draw.echo = !!m.overlayExcluded;
  $('rtcToggle').closest('label').hidden = !(hostV2 && Array.isArray(m.features) && m.features.includes('rtc') && rtcPossible);
  maybeStartRtc();
  stats.host = m.enc ? { enc: m.enc.id } : null;
  $('statsToggle').closest('label').hidden = !hostV2;
  if (m.enc) $('qualityHint').textContent = qualityText(m);
}
function qualityText(m) {
  const gpu = m.enc && m.enc.gpu;
  const fps = m.enc ? m.enc.maxFps : 30;
  if (quality === 'fast') return `Priorité à la réactivité : image un peu plus douce, ${Math.min(60, fps)} i/s.`;
  if (quality === 'sharp') return gpu ? `Résolution native, jusqu’à ${fps} i/s. Demande une bonne connexion.` : 'Le PC encode sur son processeur : résolution et cadence limitées.';
  return `Le bon compromis, ${Math.min(60, fps)} i/s. La qualité suit ta connexion.`;
}
function renderScreens(m) {
  const seg = $('screenSeg');
  const screens = m.screens || [];
  $('screenSection').hidden = screens.length < 2;
  const items = screens.map((s) => {
    const b = document.createElement('button');
    b.className = 'ca-seg__item' + (!m.mosaic && s.i === m.screen ? ' is-active' : '');
    b.textContent = s.i === 0 ? 'Principal' : 'Écran ' + (s.i + 1);
    b.onclick = () => { send({ t: 'screen', i: s.i }); closeSheet(); };
    return b;
  });
  if (screens.length > 1 && hostV2) {
    const b = document.createElement('button');
    b.className = 'ca-seg__item' + (m.mosaic ? ' is-active' : '');
    b.textContent = 'Tous';
    b.onclick = () => { send({ t: 'screen', i: 'all' }); closeSheet(); hint('Tous les écrans : image réduite, 30 i/s'); };
    items.push(b);
  }
  seg.replaceChildren(...items);
}

// ---------- Picture: video (WebCodecs) or JPEG ----------
const JPEG_Q = { fast: { w: 960, q: 45 }, balanced: { w: 1280, q: 60 }, sharp: { w: 1920, q: 75 } };
let quality = readQuality();
let imageMode = store.get('image', 'video');
let framePending = false;
const codecsReady = supportedCodecs();
let videoSupported = false;
codecsReady.then((c) => { videoSupported = c.length > 0; renderImageMode(); });

const shot = $('shot'), canvas = $('vid');
// which element shows the picture: 'jpeg' (img), 'ws' (canvas, WebCodecs) or 'rtc' (<video>)
function showSurface(which) {
  shot.hidden = which !== 'jpeg';
  canvas.hidden = which !== 'ws';
  rtcVideo.hidden = which !== 'rtc';
  if (which === 'rtc') $('stageMsg').hidden = true;
}
const jpeg = createJpeg({ img: shot, onShown: (w, h) => {
  if (w) { $('stageMsg').hidden = true; if (!video.active) showSurface('jpeg'); }
  framePending = false;
  requestFrame();
} });
function requestFrame() {
  if (!alive || framePending || document.hidden || video.active) return;
  framePending = true;
  send({ t: 'frame', ...JPEG_Q[quality] });
}

const video = { active: false, retryTimer: 0, retryDelay: 2000, plan: null };
const decoder = createWebCodecsDecoder({
  canvas,
  onFrame: (w, h) => {
    stats.onFrame();
    if (canvas.hidden && via !== 'rtc') { showSurface('ws'); $('stageMsg').hidden = true; }
    if (!layout.mosaic && (w !== layout.imgW || h !== layout.imgH) && !info) layout.setImage(w, h);
  },
  onNeedKey: () => send({ t: 'keyframe' }),
});
function startPicture() {
  if (!alive) return;
  if (imageMode === 'video' && videoSupported) startVideo();
  else { if (video.active) { send({ t: 'video', on: false }); resetVideo(); } requestFrame(); }
}
function startVideo() {
  clearTimeout(video.retryTimer); video.retryTimer = 0;
  resetVideo();
  video.active = true;
  framePending = false;
  // 1.4 PCs read w/kbps; 2.0 PCs read mode
  const v1 = { fast: { w: 1280, kbps: 2500 }, balanced: { w: 1600, kbps: 4000 }, sharp: { w: 1920, kbps: 8000 } }[quality];
  if (via === 'rtc') showSurface('rtc');
  send({ t: 'video', on: true, mode: quality, via, ...v1 });
}
function resetVideo() {
  video.active = false;
  decoder.close();
}
function onVideoState(m) {
  if (m.state === 'error') return videoFailed(m.reason);
  if (m.state === 'on') { video.plan = m.plan || null; video.retryDelay = 2000; if (m.plan) hostStat = { ...(hostStat || {}), plan: m.plan }; renderLive(); }
  // "restart": the PC switches encoders or settings; the next frame is a keyframe, nothing to do here
}
function videoFailed(reason) {
  console.warn('video:', reason);
  resetVideo();
  const delay = video.retryDelay;
  video.retryDelay = Math.min(30000, delay * 2);
  if (delay === 2000) hint('Vidéo indisponible : images classiques');
  clearTimeout(video.retryTimer);
  video.retryTimer = setTimeout(() => {
    video.retryTimer = 0;
    if (alive && !document.hidden && imageMode === 'video' && videoSupported && !video.active) startVideo();
  }, delay);
  requestFrame();
}
document.addEventListener('visibilitychange', () => {
  if (document.hidden) { if (video.active) { send({ t: 'video', on: false }); resetVideo(); } return; }
  if (!$('ctrlView').hidden && !ws.open) connect(); else startPicture();
});

function renderQuality() {
  document.querySelectorAll('[data-q]').forEach((b) => b.classList.toggle('is-active', b.dataset.q === quality));
  if (info) $('qualityHint').textContent = qualityText(info);
}
document.querySelectorAll('[data-q]').forEach((b) => b.addEventListener('click', () => {
  quality = b.dataset.q; store.set('quality', quality); renderQuality();
  if (video.active) {
    if (hostV2) send({ t: 'video', on: true, mode: quality, via }); else startVideo();
  }
  hint(b.textContent.trim());
}));
renderQuality();
function renderImageMode() {
  document.querySelectorAll('[data-img]').forEach((b) => {
    b.classList.toggle('is-active', b.dataset.img === (videoSupported ? imageMode : 'jpeg'));
    if (b.dataset.img === 'video') b.disabled = !videoSupported;
  });
  $('imgHint').textContent = videoSupported
    ? (imageMode === 'video' ? 'Vidéo fluide, encodée par le PC. Choisis « Images » si ta connexion est très faible.' : 'Une image à la fois : plus lent, mais marche partout.')
    : 'La vidéo demande iOS 16.4 ou un navigateur récent : images classiques.';
}
document.querySelectorAll('[data-img]').forEach((b) => b.addEventListener('click', () => {
  imageMode = b.dataset.img; store.set('image', imageMode); renderImageMode();
  startPicture();
}));
renderImageMode();

// ---------- Safari tip ----------
const tip = $('safariTip');
function showSafariTip() {
  if (!ios || navigator.standalone === true || store.get('safari-tip', '') === 'vu') return;
  tip.hidden = false;
}
['pointerdown', 'pointermove', 'pointerup', 'touchstart', 'touchmove', 'touchend', 'mousedown', 'click', 'wheel']
  .forEach((ev) => tip.addEventListener(ev, (e) => e.stopPropagation()));
$('safariTipOk').addEventListener('click', () => { tip.hidden = true; store.set('safari-tip', 'vu'); });

// ---------- Power ----------
const POWER = {
  lock: { title: 'Verrouiller le PC ?', text: 'L’écran de verrouillage s’affiche sur le PC. Il faudra le déverrouiller sur place.', ok: 'Verrouiller', done: null },
  sleep: { title: 'Mettre le PC en veille ?', text: 'La connexion va s’arrêter. Réveille le PC pour reprendre.', ok: 'Mettre en veille', done: ['PC en veille', 'Réveille le PC, puis reconnecte-toi.'] },
  restart: { title: 'Redémarrer le PC ?', text: 'Les applis ouvertes vont se fermer. se7en Vision devra être relancé sur le PC, avec un nouveau lien.', ok: 'Redémarrer', done: ['PC en redémarrage', 'Relance se7en Vision sur le PC pour obtenir un nouveau lien.'] },
  shutdown: { title: 'Éteindre le PC ?', text: 'Les applis ouvertes vont se fermer et le lien ne marchera plus.', ok: 'Éteindre', done: ['Extinction demandée', 'Le PC s’éteint. Si une appli demande d’enregistrer, il attend une réponse sur son écran.'] },
};
function askConfirm({ title, text, ok }, onOk) {
  $('cTitle').textContent = title; $('cText').textContent = text; $('cOk').textContent = ok;
  $('cOk').onclick = () => { closeConfirm(); onOk(); };
  $('confirmDlg').hidden = false; $('confirmScrim').hidden = false;
}
function closeConfirm() { $('confirmDlg').hidden = true; $('confirmScrim').hidden = true; }
$('cCancel').addEventListener('click', closeConfirm);
$('confirmScrim').addEventListener('click', closeConfirm);
document.querySelectorAll('[data-power]').forEach((b) => b.addEventListener('click', () => {
  const a = b.dataset.power;
  closeSheet();
  askConfirm(POWER[a], () => {
    powerPending = a;
    input.releaseAll();
    send({ t: 'power', a });
    setTimeout(() => { if (powerPending === a) powerDone(); }, 4000);
  });
}));
function onPowerAck() { if (powerPending === 'lock') { powerPending = null; hint('PC verrouillé'); } }
function powerDone() {
  const a = powerPending;
  powerPending = null;
  const d = a && POWER[a].done;
  if (!d) { if (!$('ctrlView').hidden && !ws.open) ws.retry(reconnect, 1500); return; }
  message(d[0], d[1], { icon: a === 'sleep' ? 'moon' : 'power', violet: true, button: 'Se reconnecter', onButton: boot });
}
$('leaveBtn').addEventListener('click', () => {
  closeSheet();
  input.releaseAll();
  message('Déconnecté', 'Tu peux revenir tant que se7en Vision reste ouvert sur le PC.', { icon: 'log-out', violet: true, button: 'Se reconnecter', onButton: boot });
});

syncViewport();
boot();
