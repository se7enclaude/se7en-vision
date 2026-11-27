// Per-device preferences (localStorage, "vision-" keys). Nothing here is secret.
export const store = {
  get(k, d) { try { const v = localStorage.getItem('vision-' + k); return v == null ? d : v; } catch { return d; } },
  set(k, v) { try { localStorage.setItem('vision-' + k, v); } catch {} },
};

// Quality modes: the PC chooses resolution, frame rate and bitrate range from the mode (see QualityPlanner).
export const MODES = ['fast', 'balanced', 'sharp'];
export function readQuality() {
  const q = store.get('quality', 'balanced');
  if (q === 'fluid') return 'fast'; // 1.4 name
  return MODES.includes(q) ? q : 'balanced';
}
