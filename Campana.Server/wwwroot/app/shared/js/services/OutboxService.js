import { Outbox, marker } from '../domain/outbox.js';
import { IndexedDbAdapter } from './IndexedDbAdapter.js';

const POLL_MS = 15000;

/**
 * Cola de envío de la sesión: toda escritura clínica se encola y se intenta enviar de inmediato.
 * Así el orden y el clientRequestId quedan fijos aunque falle la red a mitad.
 */
export class OutboxService {
  constructor({ api, auth, monitor, adapterFor = (u) => new IndexedDbAdapter(u), win = globalThis.window, setTimer = (f, ms) => setTimeout(f, ms), clearTimer = (id) => clearTimeout(id) }) {
    this.api = api; this.auth = auth; this.monitor = monitor; this.adapterFor = adapterFor; this.win = win;
    this.setTimer = setTimer; this.clearTimer = clearTimer;
    this.box = null; this.usuario = null; this.listeners = new Set(); this.timer = null; this.pausa = null;
    this.counts = { pendientes: 0, rechazados: 0 };
  }
  on(cb) { this.listeners.add(cb); return () => this.listeners.delete(cb); }
  emit() { this.listeners.forEach((cb) => cb(this.counts)); }

  /** Abre la cola del usuario con sesión. Si cambió el usuario, abre la suya. */
  async open() {
    const s = this.auth.session();
    if (!s || s.rol !== 'medico') { this.close(); return null; }
    if (this.box && this.usuario === s.usuario) return this.box;
    this.usuario = s.usuario;
    this.box = new Outbox({ adapter: this.adapterFor(s.usuario), usuario: s.usuario, send: (rec) => this.#send(rec) });
    await this.refresh();
    return this.box;
  }
  close() { this.clearTimer(this.timer); this.timer = null; this.box = null; this.usuario = null; this.counts = { pendientes: 0, rechazados: 0 }; this.emit(); }
  start() {
    this.win?.addEventListener?.('online', () => this.flush());
    this.monitor?.on?.((state) => { if (state === 'conectado') this.flush(); });
    this.auth.on((ev) => { if (ev === 'cerrada') this.close(); if (ev === 'iniciada') this.flush(); });
    this.#schedule(POLL_MS);
  }

  #send(rec) {
    if (rec.archivo) {
      const form = new FormData();
      for (const [k, v] of Object.entries(rec.campos ?? {})) if (v !== null && v !== undefined && v !== '') form.append(k, v);
      form.append('file', rec.archivo, rec.nombreArchivo ?? 'archivo'); // el archivo va al final, como pide el servidor
      return this.api.request(rec.metodo, rec.ruta, { body: form });
    }
    return this.api.request(rec.metodo, rec.ruta, { body: rec.cuerpo ?? undefined });
  }

  async refresh() { this.counts = this.box ? await this.box.counts() : { pendientes: 0, rechazados: 0 }; this.emit(); return this.counts; }
  /** Para armar el visitId de un servicio: el real si la visita ya se envió; si no, el marcador que se resuelve al enviar. */
  async visitRef(visitId) {
    if (!visitId?.startsWith('local:')) return visitId;
    const box = await this.open();
    return (await box?.resolveLocal(visitId)) ?? marker(visitId);
  }
  list() { return this.box ? this.box.list() : Promise.resolve([]); }

  #schedule(ms) {
    this.clearTimer(this.timer);
    this.timer = this.setTimer(() => this.flush().finally(() => this.#schedule(Math.max(POLL_MS, this.box?.espera ?? 0))), ms);
  }

  async flush() {
    const box = await this.open();
    if (!box) return null;
    const res = await box.flush();
    this.pausa = res.pausa;
    await this.refresh();
    return res;
  }

  /**
   * Encola y envía de inmediato. Devuelve { estado: 'enviado' | 'pendiente' | 'rechazado', error?, id }.
   * Si el envío quedó rechazado, se quita de la cola: la pantalla muestra el error y la persona lo corrige.
   */
  async submit(spec) {
    const box = await this.open();
    if (!box) throw new Error('Sin sesión');
    const rec = await box.enqueue(spec);
    await this.flush();
    const now = await box.get(rec.id);
    if (!now) return { estado: 'enviado', id: rec.id };
    if (now.estado === 'rechazado') { await box.discard(rec.id); await this.refresh(); return { estado: 'rechazado', error: now.ultimoError, id: rec.id }; }
    return { estado: 'pendiente', id: rec.id, error: now.ultimoError };
  }
  async retry(id) { await this.box?.retry(id); return this.flush(); }
  async discard(id) { await this.box?.discard(id); return this.refresh(); }
}
