(() => {
  const $ = (id) => document.getElementById(id);
  const host = window.chrome && window.chrome.webview;
  const send = (type) => host && host.postMessage({ type });

  // Theme: remembered choice, else the system setting.
  function readTheme() {
    try { return localStorage.getItem('relais-theme'); } catch { return null; }
  }
  function applyTheme(theme) {
    document.documentElement.dataset.theme = theme;
    document.querySelectorAll('[data-theme-btn]').forEach((b) =>
      b.classList.toggle('is-active', b.dataset.themeBtn === theme));
  }
  applyTheme(readTheme() || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'));
  document.querySelectorAll('[data-theme-btn]').forEach((b) => b.addEventListener('click', () => {
    applyTheme(b.dataset.themeBtn);
    try { localStorage.setItem('relais-theme', b.dataset.themeBtn); } catch {}
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

  let lastPhone = false;

  function render(s) {
    // QR / link area
    $('qrWait').hidden = s.tunnel !== 'starting';
    $('qrError').hidden = s.tunnel !== 'error';
    $('qr').hidden = s.tunnel !== 'ready' || !s.qr;
    if (s.tunnel === 'error') $('qrErrorText').textContent = s.tunnelError || 'Impossible de créer le lien.';
    if (s.qr && $('qr').dataset.for !== s.link) {
      $('qr').innerHTML = s.qr;
      $('qr').dataset.for = s.link;
    }
    $('link').textContent = s.link || (s.tunnel === 'error' ? 'Lien indisponible' : 'Création du lien…');
    $('link').title = s.link || '';
    $('copyBtn').disabled = !s.link;

    // PIN
    $('pin').textContent = s.pin.slice(0, 3) + ' ' + s.pin.slice(3);
    const hint = $('pinHint');
    if (s.failed > 0) {
      const left = s.maxAttempts - s.failed;
      hint.textContent = `${s.failed} code${s.failed > 1 ? 's' : ''} faux. Encore ${left} essai${left > 1 ? 's' : ''} avant un nouveau lien.`;
      hint.classList.add('warn');
    } else {
      hint.textContent = 'Ne le donne qu’à la personne qui contrôle ce PC.';
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
      $('statusDesc').textContent = 'Le lien et le code changent à chaque ouverture.';
      badge.className = 'ca-badge ca-badge--accent';
      $('badgeText').textContent = 'Prêt';
    }
    $('disconnectBtn').hidden = !s.phone;
    if (s.phone && !lastPhone) toast(`${s.phoneName || 'Téléphone'} connecté`, 'smartphone');
    lastPhone = s.phone;
  }

  if (host) {
    host.addEventListener('message', (e) => { if (e.data && e.data.type === 'state') render(e.data); });
    send('ready');
  }
})();
