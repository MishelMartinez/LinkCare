/** Estado de conexión: conectado, sin_red o servidor_no_responde. */
export class ConnectionMonitor {
  constructor({ fetchFn = (...a) => fetch(...a), intervalMs = 10000, win = globalThis.window } = {}) {
    this.fetchFn = fetchFn; this.intervalMs = intervalMs; this.win = win;
    this.state = 'conectado'; this.listeners = new Set(); this.timer = null; this.browserOnline = true;
  }
  on(cb) { this.listeners.add(cb); return () => this.listeners.delete(cb); }
  set(next) { if (next !== this.state) { this.state = next; this.listeners.forEach((cb) => cb(next)); } }
  /** Lo llama ApiClient con cada respuesta, para no esperar al siguiente sondeo. */
  report(ok) { this.set(ok ? 'conectado' : (this.browserOnline ? 'servidor_no_responde' : 'sin_red')); }
  async check() {
    if (!this.browserOnline) return this.set('sin_red');
    try { const r = await this.fetchFn('/health', { cache: 'no-store' }); this.set(r.ok ? 'conectado' : 'servidor_no_responde'); }
    catch { this.set('servidor_no_responde'); }
  }
  start() {
    this.win?.addEventListener?.('online', () => { this.browserOnline = true; this.check(); });
    this.win?.addEventListener?.('offline', () => { this.browserOnline = false; this.set('sin_red'); });
    this.browserOnline = this.win?.navigator?.onLine ?? true;
    this.check();
    this.timer = setInterval(() => this.check(), this.intervalMs);
  }
  stop() { clearInterval(this.timer); }
}
