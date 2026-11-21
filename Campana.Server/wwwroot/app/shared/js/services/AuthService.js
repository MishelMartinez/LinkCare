const KEY = 'campana.session';
const WARN_BEFORE_MS = 5 * 60 * 1000;

/** Sesión en sessionStorage (se borra al cerrar la pestaña). Nunca en localStorage, cookies ni URL. */
export class AuthService {
  constructor({ storage = globalThis.sessionStorage, now = () => Date.now(), setTimer = (fn, ms) => setTimeout(fn, ms), clearTimer = (id) => clearTimeout(id) } = {}) {
    this.storage = storage; this.now = now; this.setTimer = setTimer; this.clearTimer = clearTimer;
    this.listeners = new Set(); this.timer = null;
  }
  on(cb) { this.listeners.add(cb); return () => this.listeners.delete(cb); }
  emit(evento, datos) { this.listeners.forEach((cb) => cb(evento, datos)); }

  /** Devuelve la sesión vigente o null (y la borra si venció o está dañada). */
  session() {
    const rawValue = this.storage.getItem(KEY);
    if (!rawValue) return null;
    try {
      const s = JSON.parse(rawValue);
      if (!s?.token || !s.exp || s.exp <= this.now()) throw new Error('vencida');
      return s;
    } catch { this.storage.removeItem(KEY); return null; }
  }
  token() { return this.session()?.token ?? null; }

  /** Guarda la respuesta de /auth/login. */
  start(login) {
    const s = {
      token: login.token, exp: Date.parse(login.expiresAt), usuario: login.username, rol: login.role,
      consultorio: login.consultorioId ?? null, campanas: Array.isArray(login.campanas) ? login.campanas : [],
    };
    this.storage.setItem(KEY, JSON.stringify(s));
    this.schedule(s);
    this.emit('iniciada', { usuario: s.usuario });
    return s;
  }
  schedule(s = this.session()) {
    this.clearTimer(this.timer);
    if (!s) return;
    const wait = Math.max(0, s.exp - this.now() - WARN_BEFORE_MS);
    this.timer = this.setTimer(() => this.emit('por-vencer', { exp: s.exp }), wait);
  }
  end(motivo = 'cierre') {
    this.storage.removeItem(KEY);
    this.clearTimer(this.timer);
    this.emit('cerrada', { motivo });
  }
}
