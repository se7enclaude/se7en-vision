// Files both ways. Out: drop on the stage or pick, 16 KiB chunks, never more than a window ahead of the PC's
// acknowledgements, SHA-256 at the end. In: the PC announces a file; you accept (a save dialog where the
// browser has one, otherwise the file is kept in memory and offered as a download) and it streams in.
const CHUNK = 16 * 1024, WINDOW = 32, ACK_EVERY = 8;
const MEMORY_MAX = 512 * 1024 * 1024;
const human = (b) => b < 1024 ? b + ' o' : b < 1048576 ? Math.round(b / 1024) + ' Ko' : b < 1073741824 ? (b / 1048576).toFixed(1).replace('.', ',') + ' Mo' : (b / 1073741824).toFixed(2).replace('.', ',') + ' Go';

export function createFiles({ stage, panel, send, sendBinary, hint, isDesktop }) {
  let nextId = 1, available = false;
  const out = new Map(); // id → transfer
  const inc = new Map();
  const list = panel.querySelector('.xfer-list');

  // ---------- UI ----------
  function row(id, dir, name, size) {
    const li = document.createElement('li');
    li.className = 'xfer';
    li.innerHTML = `<i class="ca-icon icon-${dir === 'in' ? 'download' : 'upload'}"></i><div class="xfer-text"><span class="xfer-name"></span><span class="xfer-meta"></span></div><div class="xfer-actions"></div>`;
    li.querySelector('.xfer-name').textContent = name;
    li.querySelector('.xfer-meta').textContent = human(size);
    list.prepend(li);
    panel.hidden = false;
    return li;
  }
  function setMeta(li, text, cls) { li.querySelector('.xfer-meta').textContent = text; if (cls) li.classList.add(cls); }
  function actions(li, buttons) {
    const box = li.querySelector('.xfer-actions');
    box.replaceChildren(...buttons.map(([label, fn, primary]) => {
      const b = document.createElement('button');
      b.className = 'ca-btn ca-btn--sm ' + (primary ? 'ca-btn--primary' : 'ca-btn--glass');
      b.textContent = label;
      b.addEventListener('click', fn);
      return b;
    }));
  }
  function finishRow(li, ms = 6000) { setTimeout(() => { li.remove(); if (!list.children.length) panel.hidden = true; }, ms); }

  // ---------- sending ----------
  async function sendFile(file) {
    if (!available) return hint('Ce PC ne reçoit pas de fichiers');
    const id = nextId++;
    const t = { id, file, acked: 0, cancelled: false, li: row(id, 'out', file.name, file.size), wake: null };
    out.set(id, t);
    actions(t.li, [['Annuler', () => cancelOut(id)]]);
    setMeta(t.li, 'En attente du PC…');
    send({ t: 'fopen', id, name: file.name, size: file.size });
    t.timer = setTimeout(() => { if (!t.opened && !t.cancelled) failOut(t, 'pas de réponse du PC'); }, 60000);
  }
  async function pump(t) {
    const total = Math.ceil(t.file.size / CHUNK);
    const hasher = await sha256Stream();
    let seq = 0;
    try {
      while (seq < total && !t.cancelled) {
        while (seq >= t.acked + WINDOW && !t.cancelled) await new Promise((r) => { t.wake = r; setTimeout(r, 45000); });
        if (t.cancelled) return;
        if (seq >= t.acked + WINDOW) return failOut(t, 'le PC ne répond plus');
        const slice = t.file.slice(seq * CHUNK, Math.min(t.file.size, (seq + 1) * CHUNK));
        const bytes = new Uint8Array(await slice.arrayBuffer());
        hasher.update(bytes);
        const msg = new Uint8Array(9 + bytes.length);
        msg[0] = 3;
        new DataView(msg.buffer).setUint32(1, t.id, true);
        new DataView(msg.buffer).setUint32(5, seq, true);
        msg.set(bytes, 9);
        await sendBinary('file', msg);
        seq++;
        if (seq % 16 === 0) setMeta(t.li, Math.round(seq * 100 / total) + ' % · ' + human(t.file.size));
      }
      setMeta(t.li, 'Vérification…');
      send({ t: 'fdone', id: t.id, sha: await hasher.digest() });
    } catch (e) { failOut(t, e.message || 'lecture impossible'); }
  }
  function failOut(t, why) {
    t.cancelled = true; clearTimeout(t.timer); out.delete(t.id);
    setMeta(t.li, why, 'failed'); actions(t.li, []); finishRow(t.li, 8000);
  }
  function cancelOut(id) {
    const t = out.get(id); if (!t) return;
    send({ t: 'fcancel', id });
    failOut(t, 'annulé');
    if (t.wake) t.wake();
  }

  // ---------- receiving ----------
  async function onOpen(m) {
    if (inc.has(m.id)) return;
    const t = { id: m.id, name: String(m.name || 'fichier').replace(/[\\/:*?"<>|]/g, '_'), size: Number(m.size) || 0, received: 0, next: 0, parts: [], writable: null, li: null, hasher: await sha256Stream() };
    if (!(typeof window.showSaveFilePicker === 'function') && t.size > MEMORY_MAX) { send({ t: 'frej', id: m.id, why: 'trop gros pour ce navigateur (' + human(MEMORY_MAX) + ' max)' }); return; }
    t.li = row(t.id, 'in', t.name, t.size);
    inc.set(t.id, t);
    setMeta(t.li, human(t.size) + ' · le PC veut t’envoyer ce fichier');
    actions(t.li, [['Enregistrer', () => accept(t), true], ['Refuser', () => { send({ t: 'frej', id: t.id, why: 'refusé' }); inc.delete(t.id); setMeta(t.li, 'refusé'); actions(t.li, []); finishRow(t.li, 3000); }]]);
  }
  async function accept(t) {
    if (typeof window.showSaveFilePicker === 'function') {
      try {
        const handle = await window.showSaveFilePicker({ suggestedName: t.name });
        t.writable = await handle.createWritable();
      } catch (e) { if (e && e.name === 'AbortError') { send({ t: 'frej', id: t.id, why: 'refusé' }); inc.delete(t.id); t.li.remove(); if (!list.children.length) panel.hidden = true; return; } t.writable = null; }
    }
    actions(t.li, [['Annuler', () => { send({ t: 'fcancel', id: t.id }); failIn(t, 'annulé'); }]]);
    setMeta(t.li, '0 % · ' + human(t.size));
    send({ t: 'fok', id: t.id });
  }
  async function onChunk(bytes) {
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const id = dv.getUint32(1, true), seq = dv.getUint32(5, true);
    const t = inc.get(id);
    if (!t) return;
    if (seq !== t.next) { send({ t: 'fcancel', id }); failIn(t, 'morceau manquant'); return; }
    const payload = bytes.subarray(9);
    t.next++; t.received += payload.length;
    t.hasher.update(payload);
    try {
      if (t.writable) await t.writable.write(payload); else t.parts.push(payload.slice());
    } catch (e) { send({ t: 'fcancel', id }); failIn(t, 'écriture impossible'); return; }
    const total = Math.ceil(t.size / CHUNK);
    if (t.next % ACK_EVERY === 0 || t.next === total) send({ t: 'fack', id, n: t.next });
    if (t.next % 16 === 0) setMeta(t.li, Math.round(t.received * 100 / Math.max(1, t.size)) + ' % · ' + human(t.size));
  }
  async function onDone(m) {
    const t = inc.get(m.id);
    if (!t) return;
    const sha = await t.hasher.digest();
    const ok = t.received === t.size && (!m.sha || m.sha === sha);
    if (!ok) { failIn(t, 'fichier abîmé en route'); send({ t: 'fdone', id: m.id, ok: false }); return; }
    if (t.writable) { try { await t.writable.close(); } catch {} setMeta(t.li, human(t.size) + ' · enregistré'); actions(t.li, []); }
    else {
      const blob = new Blob(t.parts);
      const url = URL.createObjectURL(blob);
      setMeta(t.li, human(t.size) + ' · prêt');
      actions(t.li, [['Télécharger', () => { const a = document.createElement('a'); a.href = url; a.download = t.name; document.body.appendChild(a); a.click(); a.remove(); }, true]]);
      if (navigator.canShare && navigator.canShare({ files: [new File([blob], t.name)] })) {
        actions(t.li, [['Partager', () => navigator.share({ files: [new File([blob], t.name)], title: t.name }).catch(() => {}), true], ['Télécharger', () => { const a = document.createElement('a'); a.href = url; a.download = t.name; document.body.appendChild(a); a.click(); a.remove(); }]]);
      }
      setTimeout(() => { URL.revokeObjectURL(url); }, 10 * 60 * 1000);
    }
    inc.delete(m.id);
    send({ t: 'fdone', id: m.id, ok: true });
    if (t.writable) finishRow(t.li, 6000);
  }
  function failIn(t, why) {
    inc.delete(t.id);
    if (t.writable) { try { t.writable.abort(); } catch {} }
    t.parts = [];
    setMeta(t.li, why, 'failed'); actions(t.li, []); finishRow(t.li, 8000);
  }

  // ---------- messages ----------
  function onText(m) {
    switch (m.t) {
      case 'fopen': onOpen(m); break;
      case 'fok': { const t = out.get(m.id); if (t && !t.opened) { t.opened = true; clearTimeout(t.timer); setMeta(t.li, '0 % · ' + human(t.file.size)); pump(t); } break; }
      case 'frej': { const t = out.get(m.id); if (t) failOut(t, m.why || 'refusé par le PC'); break; }
      case 'fack': { const t = out.get(m.id); if (t) { t.acked = Math.max(t.acked, Number(m.n) || 0); if (t.wake) { const w = t.wake; t.wake = null; w(); } } break; }
      case 'fdone': {
        const t = out.get(m.id);
        if (t) { out.delete(m.id); if (m.ok === false) { setMeta(t.li, 'le PC n’a pas pu l’enregistrer', 'failed'); } else { setMeta(t.li, human(t.file.size) + ' · envoyé'); hint('Fichier envoyé'); } actions(t.li, []); finishRow(t.li); }
        else onDone(m);
        break;
      }
      case 'fcancel': {
        const t = out.get(m.id); if (t) { failOut(t, 'arrêté par le PC'); if (t.wake) t.wake(); }
        const r = inc.get(m.id); if (r) failIn(r, 'arrêté par le PC');
        break;
      }
    }
  }
  function onBinary(bytes) { if (bytes[0] === 3 && bytes.length >= 9) { onChunk(bytes); return true; } return false; }

  // ---------- drop / pick ----------
  let depth = 0;
  stage.addEventListener('dragenter', (e) => { if (!available) return; e.preventDefault(); depth++; stage.classList.add('drop'); });
  stage.addEventListener('dragover', (e) => { if (!available) return; e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; });
  stage.addEventListener('dragleave', () => { if (--depth <= 0) { depth = 0; stage.classList.remove('drop'); } });
  stage.addEventListener('drop', (e) => { e.preventDefault(); depth = 0; stage.classList.remove('drop'); if (available && e.dataTransfer) [...e.dataTransfer.files].forEach(sendFile); });
  document.querySelectorAll('[data-send-file]').forEach((b) => b.addEventListener('click', () => document.getElementById('filePick').click()));
  const pick = document.getElementById('filePick');
  pick.addEventListener('change', () => { [...pick.files].forEach(sendFile); pick.value = ''; });

  return {
    onText, onBinary, sendFile,
    set available(v) { available = v; document.querySelectorAll('.file-ui').forEach((el) => { el.hidden = !v; }); },
    reset() { for (const t of [...out.values()]) failOut(t, 'connexion perdue'); for (const t of [...inc.values()]) failIn(t, 'connexion perdue'); },
  };
}

// SHA-256 over a stream of chunks (SubtleCrypto only hashes whole buffers): a small pure-JS implementation.
async function sha256Stream() {
  const K = new Uint32Array([0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2]);
  const H = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
  const W = new Uint32Array(64);
  const buf = new Uint8Array(64);
  let bufLen = 0, total = 0;
  function block(b, off) {
    for (let i = 0; i < 16; i++) W[i] = (b[off + i * 4] << 24) | (b[off + i * 4 + 1] << 16) | (b[off + i * 4 + 2] << 8) | b[off + i * 4 + 3];
    for (let i = 16; i < 64; i++) {
      const s0 = ((W[i - 15] >>> 7) | (W[i - 15] << 25)) ^ ((W[i - 15] >>> 18) | (W[i - 15] << 14)) ^ (W[i - 15] >>> 3);
      const s1 = ((W[i - 2] >>> 17) | (W[i - 2] << 15)) ^ ((W[i - 2] >>> 19) | (W[i - 2] << 13)) ^ (W[i - 2] >>> 10);
      W[i] = (W[i - 16] + s0 + W[i - 7] + s1) | 0;
    }
    let a = H[0], b2 = H[1], c = H[2], d = H[3], e = H[4], f = H[5], g = H[6], h = H[7];
    for (let i = 0; i < 64; i++) {
      const S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
      const ch = (e & f) ^ (~e & g);
      const t1 = (h + S1 + ch + K[i] + W[i]) | 0;
      const S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
      const maj = (a & b2) ^ (a & c) ^ (b2 & c);
      const t2 = (S0 + maj) | 0;
      h = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b2; b2 = a; a = (t1 + t2) | 0;
    }
    H[0] = (H[0] + a) | 0; H[1] = (H[1] + b2) | 0; H[2] = (H[2] + c) | 0; H[3] = (H[3] + d) | 0;
    H[4] = (H[4] + e) | 0; H[5] = (H[5] + f) | 0; H[6] = (H[6] + g) | 0; H[7] = (H[7] + h) | 0;
  }
  return {
    update(bytes) {
      total += bytes.length;
      let off = 0;
      if (bufLen) {
        const take = Math.min(64 - bufLen, bytes.length);
        buf.set(bytes.subarray(0, take), bufLen); bufLen += take; off = take;
        if (bufLen === 64) { block(buf, 0); bufLen = 0; } else return;
      }
      for (; off + 64 <= bytes.length; off += 64) block(bytes, off);
      if (off < bytes.length) { buf.set(bytes.subarray(off), 0); bufLen = bytes.length - off; }
    },
    async digest() {
      const bits = total * 8;
      buf[bufLen++] = 0x80;
      if (bufLen > 56) { buf.fill(0, bufLen); block(buf, 0); bufLen = 0; }
      buf.fill(0, bufLen, 56);
      const hi = Math.floor(bits / 4294967296), lo = bits >>> 0;
      buf[56] = hi >>> 24; buf[57] = hi >>> 16; buf[58] = hi >>> 8; buf[59] = hi; buf[60] = lo >>> 24; buf[61] = lo >>> 16; buf[62] = lo >>> 8; buf[63] = lo;
      block(buf, 0);
      return [...H].map((v) => (v >>> 0).toString(16).padStart(8, '0')).join('');
    },
  };
}
