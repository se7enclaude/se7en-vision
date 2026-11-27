(() => {
  const $ = (id) => document.getElementById(id);
  const host = window.chrome && window.chrome.webview;
  const send = (type, extra) => host && host.postMessage(Object.assign({ type }, extra || {}));

  // Theme: remembered choice, else the system setting.
  function readTheme() {
    try { return localStorage.getItem('vision-theme'); } catch { return null; }
  }
  function applyTheme(theme) {
    document.documentElement.dataset.theme = theme;
    document.querySelectorAll('[data-theme-btn]').forEach((b) =>
      b.classList.toggle('is-active', b.dataset.themeBtn === theme));
  }
  applyTheme(readTheme() || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'));
  document.querySelectorAll('[data-theme-btn]').forEach((b) => b.addEventListener('click', () => {
    applyTheme(b.dataset.themeBtn);
    try { localStorage.setItem('vision-theme', b.dataset.themeBtn); } catch {}
  }));

  function toast(text, icon = 'check') {
    const el = document.createElement('div');
    el.className = 'toast';
    el.innerHTML = `<span class="dot"><i class="ca-icon icon-${icon}"></i></span><span></span>`;
    el.lastElementChild.textContent = text;
    const hostEl = $('toastHost');
    hostEl.replaceChildren(el);
    setTimeout(() => el.remove(), 2200);
  }

  $('copyBtn').addEventListener('click', () => { send('copy'); toast('Lien copié'); });
  $('newLinkBtn').addEventListener('click', () => { send('newLink'); toast('Nouveau lien et nouveau code', 'refresh-cw'); });
  $('disconnectBtn').addEventListener('click', () => { send('disconnect'); toast('Téléphone déconnecté', 'unplug'); });
  $('retryBtn').addEventListener('click', () => send('retryTunnel'));

  // ---------- Tabs ----------
  function showTab(name) {
    document.querySelectorAll('[data-tab]').forEach((b) => b.classList.toggle('is-active', b.dataset.tab === name));
    $('tabHost').hidden = name !== 'host';
    $('tabConnect').hidden = name !== 'connect';
    $('tabSettings').hidden = name !== 'settings';
    try { localStorage.setItem('vision-tab', name); } catch {}
    if (name === 'connect') setTimeout(() => $('connectUrl').focus(), 50);
  }
  document.querySelectorAll('[data-tab]').forEach((b) => b.addEventListener('click', () => showTab(b.dataset.tab)));
  try { const t = localStorage.getItem('vision-tab'); if (t === 'connect' || t === 'settings') showTab(t); } catch {}

  // ---------- Réglages ----------
  const setSetting = (key, value) => send('setSetting', { key, value });
  document.querySelectorAll('input[data-setting]').forEach((el) => el.addEventListener('change', () => setSetting(el.dataset.setting, el.checked)));
  $('setEncoder').addEventListener('change', () => setSetting('encoder', $('setEncoder').value));
  document.querySelectorAll('[data-setting-seg]').forEach((seg) => seg.querySelectorAll('button').forEach((b) => b.addEventListener('click', () => {
    const v = b.dataset.v;
    setSetting(seg.dataset.settingSeg, seg.dataset.settingSeg === 'fpsCap' ? Number(v) : v);
    seg.querySelectorAll('button').forEach((x) => x.classList.toggle('is-active', x === b));
  })));
  $('setPickDir').addEventListener('click', () => send('pickDownloads'));
  $('setOpenDir').addEventListener('click', () => send('openDownloads'));
  $('reprobeBtn').addEventListener('click', () => send('reprobe'));
  function renderSettings(s) {
    if (!s.settings) return;
    const st = s.settings;
    document.querySelectorAll('input[data-setting]').forEach((el) => { if (document.activeElement !== el) el.checked = !!st[el.dataset.setting]; });
    document.querySelectorAll('[data-setting-seg]').forEach((seg) => seg.querySelectorAll('button').forEach((b) => b.classList.toggle('is-active', String(st[seg.dataset.settingSeg]) === b.dataset.v)));
    const sel = $('setEncoder');
    const wanted = [['auto', 'Automatique'], ...(s.encoders || []).filter((e) => e.ok).map((e) => [e.id, e.label])];
    if ([...sel.options].map((o) => o.value).join() !== wanted.map((w) => w[0]).join()) {
      sel.replaceChildren(...wanted.map(([v, l]) => { const o = document.createElement('option'); o.value = v; o.textContent = l; return o; }));
    }
    sel.value = wanted.some((w) => w[0] === st.encoder) ? st.encoder : 'auto';
    $('setDownloadsDir').textContent = s.downloadsDir || '';
    const list = $('encList');
    const items = (s.encoders || []).map((e) => {
      const li = document.createElement('li');
      li.className = e.ok ? 'ok' : '';
      li.innerHTML = '<span class="dot"></span><span class="name"></span><span class="meta"></span>';
      li.querySelector('.name').textContent = e.label;
      li.querySelector('.meta').textContent = e.ok ? (e.fps ? `${e.fps} i/s` : 'OK') : (e.error || 'indisponible');
      return li;
    });
    if (s.probing) { const li = document.createElement('li'); li.innerHTML = '<span class="spinner" style="width:14px;height:14px;border-width:2px"></span><span>Test en cours…</span>'; items.unshift(li); }
    list.replaceChildren(...items);
    $('reprobeBtn').disabled = !!s.probing;
  }

  // ---------- Se connecter à un PC ----------
  const connectHint = $('connectHint');
  const HINT_DEFAULT = connectHint.textContent;
  function connectError(text) {
    connectHint.textContent = text;
    connectHint.classList.add('err');
  }
  $('connectUrl').addEventListener('input', () => { connectHint.textContent = HINT_DEFAULT; connectHint.classList.remove('err'); });
  $('connectForm').addEventListener('submit', (e) => {
    e.preventDefault();
    const url = $('connectUrl').value.trim();
    if (!url) return;
    if (!/^https?:\/\/\S+\/s\/[0-9a-f]{16,64}/i.test(url)) return connectError('Ce n’est pas un lien se7en Vision. Il ressemble à https://….trycloudflare.com/s/…');
    send('connect', { url });
  });
  // ---------- Files: drop or pick → the PC sends them to the connected device ----------
  const transfers = new Map(); // id → row state
  function sendFiles(files) {
    const list = [...files];
    if (!list.length) return;
    if (!host) return;
    if (!lastState || !lastState.phone) return toast('Personne n’est connecté pour recevoir', 'unplug');
    try { host.postMessageWithAdditionalObjects({ type: 'sendFiles', count: list.length }, list); }
    catch (e) { toast('Envoi impossible : ' + e.message, 'x'); }
  }
  $('pickFileBtn').addEventListener('click', () => $('filePicker').click());
  $('filePicker').addEventListener('change', () => { sendFiles($('filePicker').files); $('filePicker').value = ''; });
  $('openDownloadsBtn').addEventListener('click', () => send('openDownloads'));
  let dragDepth = 0;
  document.addEventListener('dragenter', (e) => { e.preventDefault(); dragDepth++; document.body.classList.add('dragging'); });
  document.addEventListener('dragover', (e) => { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; });
  document.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; document.body.classList.remove('dragging'); } });
  document.addEventListener('drop', (e) => { e.preventDefault(); dragDepth = 0; document.body.classList.remove('dragging'); if (e.dataTransfer && e.dataTransfer.files.length) sendFiles(e.dataTransfer.files); });
  const human = (b) => b < 1024 ? b + ' o' : b < 1048576 ? Math.round(b / 1024) + ' Ko' : b < 1073741824 ? (b / 1048576).toFixed(1).replace('.', ',') + ' Mo' : (b / 1073741824).toFixed(2).replace('.', ',') + ' Go';
  function onFile(m) {
    const key = m.dir + ':' + m.id;
    let row = transfers.get(key);
    if (!row) {
      row = document.createElement('li');
      row.className = 'file-row';
      row.innerHTML = `<i class="ca-icon icon-${m.dir === 'in' ? 'download' : 'upload'}"></i><span class="name"></span><progress max="100" value="0"></progress><span class="meta"></span>`;
      $('filesList').prepend(row);
      transfers.set(key, row);
      while ($('filesList').children.length > 4) $('filesList').lastElementChild.remove();
    }
    row.querySelector('.name').textContent = m.name;
    const pct = m.size ? Math.round(m.done * 100 / m.size) : 100;
    row.querySelector('progress').value = pct;
    const meta = row.querySelector('.meta');
    if (m.kind === 'done') {
      row.querySelector('progress').hidden = true;
      meta.textContent = human(m.size) + (m.dir === 'in' ? ' · reçu' : ' · envoyé');
      toast((m.dir === 'in' ? 'Reçu : ' : 'Envoyé : ') + m.name, m.dir === 'in' ? 'download' : 'upload');
      if (m.risky) toast('Attention : ' + m.name + ' est un programme. Ne l’ouvre que si tu l’attendais.', 'triangle-alert');
    } else if (m.kind === 'failed') {
      row.classList.add('failed');
      row.querySelector('progress').hidden = true;
      meta.textContent = m.why || 'échec';
      toast(m.name + ' : ' + (m.why || 'échec'), 'x');
    } else meta.textContent = pct + ' % · ' + human(m.size);
  }

  function renderRecent(list) {
    const box = $('recentBox');
    box.hidden = !list || !list.length;
    if (box.hidden) return;
    $('recentList').replaceChildren(...list.map((r) => {
      const li = document.createElement('li');
      li.className = 'recent-item';
      const when = new Date(r.at);
      const date = isNaN(when) ? '' : when.toLocaleDateString('fr-FR', { day: 'numeric', month: 'short' }) + ' ' + when.toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' });
      li.innerHTML = `<span class="well${r.open ? ' on' : ''}"><i class="ca-icon icon-monitor"></i></span>
        <div class="recent-text"><span class="recent-name"></span><span class="recent-sub"></span></div>
        <button class="ca-btn ca-btn--primary ca-btn--sm" data-go><i class="ca-icon icon-cable"></i>${r.open ? 'Afficher' : 'Se connecter'}</button>
        <button class="ca-ibtn ca-ibtn--glass" data-forget title="Oublier ce lien"><i class="ca-icon icon-x"></i></button>`;
      li.querySelector('.recent-name').textContent = r.name || r.host;
      li.querySelector('.recent-sub').textContent = (r.name ? r.host + ' · ' : '') + (r.open ? 'connecté' : date);
      li.querySelector('[data-go]').addEventListener('click', () => send('connect', { url: r.url }));
      li.querySelector('[data-forget]').addEventListener('click', () => send('forget', { url: r.url }));
      return li;
    }));
  }

  // ---------- Code choisi par l'utilisateur (gardé d'une ouverture à l'autre) ----------
  let editing = false, lastState = null;
  function setEditing(on) {
    editing = on;
    $('pinEdit').hidden = !on;
    $('pin').hidden = on;
    $('editPinBtn').hidden = on;
    if (on) {
      $('pinField').value = '';
      $('pinHint').textContent = 'Choisis 6 chiffres faciles à retenir pour toi, difficiles à deviner pour les autres.';
      $('pinHint').classList.remove('warn');
      setTimeout(() => $('pinField').focus(), 30);
    } else if (lastState) {
      render(lastState);
    }
  }
  $('editPinBtn').addEventListener('click', () => setEditing(true));
  $('pinCancel').addEventListener('click', () => setEditing(false));
  $('pinField').addEventListener('input', () => {
    $('pinField').value = $('pinField').value.replace(/\D/g, '').slice(0, 6);
  });
  $('pinField').addEventListener('keydown', (e) => { if (e.key === 'Escape') setEditing(false); });
  $('pinEdit').addEventListener('submit', (e) => {
    e.preventDefault();
    const pin = $('pinField').value;
    if (pin.length !== 6) {
      $('pinHint').textContent = 'Le code doit faire 6 chiffres.';
      $('pinHint').classList.add('warn');
      return;
    }
    send('setPin', { pin });
  });
  function onPinSaved(m) {
    if (m.ok) { setEditing(false); toast('Code enregistré : il sera gardé', 'lock'); return; }
    $('pinHint').textContent = m.error || 'Impossible d’enregistrer ce code.';
    $('pinHint').classList.add('warn');
    $('pinField').select();
  }

  let lastPhone = false;

  function render(s) {
    // QR / link area
    $('qrWait').hidden = s.tunnel !== 'starting';
    $('qrError').hidden = s.tunnel !== 'error';
    $('qr').hidden = s.tunnel !== 'ready' || !s.qr;
    if (s.tunnel === 'error') $('qrErrorText').textContent = s.tunnelError || 'Impossible de créer le lien.';
    if (s.qr && $('qr').dataset.for !== s.qrFor) {
      $('qr').innerHTML = s.qr;
      $('qr').dataset.for = s.qrFor;
    }
    $('link').textContent = s.link || (s.tunnel === 'error' ? 'Lien indisponible' : 'Création du lien…');
    $('link').title = s.link || '';
    $('copyBtn').disabled = !s.link;

    // PIN
    lastState = s;
    $('pin').textContent = s.pin.slice(0, 3) + ' ' + s.pin.slice(3);
    $('editPinBtn').innerHTML = s.pinPersonal
      ? '<i class="ca-icon icon-pencil"></i>Modifier'
      : '<i class="ca-icon icon-pencil"></i>Choisir mon code';
    const hint = $('pinHint');
    if (editing) {
      // le formulaire garde son message
    } else if (s.failed > 0) {
      const left = s.maxAttempts - s.failed;
      hint.textContent = `${s.failed} code${s.failed > 1 ? 's' : ''} faux. Encore ${left} essai${left > 1 ? 's' : ''} avant un nouveau lien.`;
      hint.classList.add('warn');
    } else {
      hint.textContent = s.pinPersonal
        ? 'Ton code est gardé d’une ouverture à l’autre. Ne le donne qu’à une personne de confiance.'
        : 'Code provisoire, il change à chaque ouverture. Choisis le tien : il sera gardé.';
      hint.classList.remove('warn');
    }

    // Status
    const well = $('statusWell');
    const badge = $('badge');
    well.className = 'well';
    if (s.phone) {
      well.classList.add('on');
      $('statusIcon').className = 'ca-icon icon-smartphone';
      $('statusTitle').textContent = `${s.phoneName || 'Téléphone'} connecté`;
      $('statusDesc').textContent = s.connectedMinutes < 1
        ? 'Il contrôle ce PC en ce moment.'
        : `Il contrôle ce PC depuis ${s.connectedMinutes} min.`;
      badge.className = 'ca-badge ca-badge--success';
      $('badgeText').textContent = 'Connecté';
    } else if (s.tunnel === 'error') {
      well.classList.add('err');
      $('statusIcon').className = 'ca-icon icon-cloud-off';
      $('statusTitle').textContent = 'Lien indisponible';
      $('statusDesc').textContent = 'Vérifie ta connexion Internet puis réessaie.';
      badge.className = 'ca-badge ca-badge--danger';
      $('badgeText').textContent = 'Hors ligne';
    } else if (s.tunnel === 'starting') {
      $('statusIcon').className = 'ca-icon icon-loader';
      $('statusTitle').textContent = 'Préparation du lien';
      $('statusDesc').textContent = 'Quelques secondes, le temps de joindre Cloudflare.';
      badge.className = 'ca-badge ca-badge--well';
      $('badgeText').textContent = 'Démarrage';
    } else {
      $('statusIcon').className = 'ca-icon icon-smartphone';
      $('statusTitle').textContent = 'En attente du téléphone';
      $('statusDesc').textContent = 'Scanne le QR code : la connexion est automatique.';
      badge.className = 'ca-badge ca-badge--accent';
      $('badgeText').textContent = 'Prêt';
    }
    $('disconnectBtn').hidden = !s.phone;
    renderSettings(s);
    if (s.phone && s.viewer) {
      const v = s.viewer;
      const via = v.transport === 'local' ? 'en direct (réseau local)' : v.transport === 'rtc' ? 'en direct' : 'par le tunnel';
      $('statusDesc').textContent = `${via} · ${v.rtt} ms · ${v.fps} i/s · ${v.kbps >= 1000 ? (v.kbps / 1000).toFixed(1).replace('.', ',') + ' Mb/s' : v.kbps + ' kb/s'}`;
    }
    $('filesDropText').textContent = s.phone
      ? `Dépose un fichier ici pour l’envoyer à ${s.phoneName || 'l’appareil connecté'}`
      : 'Quand un appareil est connecté, dépose un fichier ici pour le lui envoyer';
    renderRecent(s.recent);
    if (s.phone && !lastPhone) toast(`${s.phoneName || 'Téléphone'} connecté`, 'smartphone');
    lastPhone = s.phone;
  }

  if (host) {
    host.addEventListener('message', (e) => {
      if (!e.data) return;
      if (e.data.type === 'state') render(e.data);
      else if (e.data.type === 'pinSaved') onPinSaved(e.data);
      else if (e.data.type === 'connectError') connectError(e.data.error);
      else if (e.data.type === 'file') onFile(e.data);
      else if (e.data.type === 'fileError') toast(e.data.error, 'x');
      else if (e.data.type === 'toast') toast(e.data.text, e.data.icon || 'check');
    });
    send('ready');
  }
})();
