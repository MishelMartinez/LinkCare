// Cola de envío: lógica pura. El almacenamiento (adapter) y el envío (send) se inyectan para poder probarla sin navegador.
// adapter: { all(), put(rec), delete(id), getMeta(k), setMeta(k, v) }
// send(rec) debe lanzar un error con { status, code, detail, errors } si el servidor responde mal (status 0 = sin red).

export const LOCAL_VISIT = /^local:[0-9a-zA-Z-]+$/;
const MARKER = /\{\{visita:(local:[0-9a-zA-Z-]+)\}\}/g;
export const marker = (localId) => `{{visita:${localId}}}`;
export const MAX_WAIT_MS = 60000;
export const backoff = (intentos) => Math.min(MAX_WAIT_MS, 1000 * 2 ** Math.max(0, intentos - 1));
const markersIn = (text) => [...text.matchAll(MARKER)].map((m) => m[1]);

const identity = (r) => r;

export class Outbox {
  constructor({ adapter, send, now = () => Date.now(), newId = () => crypto.randomUUID(), usuario = '', protect = identity, unprotect = identity }) {
    this.adapter = adapter; this.send = send; this.now = now; this.newId = newId; this.usuario = usuario;
    this.protect = protect; this.unprotect = unprotect;
    this.running = null; this.espera = 0;
  }

  async #load() {
    const all = await this.adapter.all();
    return all.map((r) => this.unprotect(r)).sort((a, b) => a.orden - b.orden);
  }
  #save(rec) { return this.adapter.put(this.protect(rec)); }

  list() { return this.#load(); }
  async counts() {
    const all = await this.#load();
    return { pendientes: all.filter((r) => r.estado !== 'rechazado').length, rechazados: all.filter((r) => r.estado === 'rechazado').length };
  }

  /**
   * Agrega un envío al final. spec: { clientRequestId, metodo, ruta, cuerpo?, campos?, archivo?, nombreArchivo?, etiqueta, localId? }
   * localId: identificador local de la visita que este envío va a crear.
   */
  async enqueue(spec) {
    const orden = ((await this.adapter.getMeta('seq')) ?? 0) + 1;
    await this.adapter.setMeta('seq', orden);
    const rec = {
      id: this.newId(), usuario: this.usuario, orden, clientRequestId: spec.clientRequestId ?? this.newId(),
      metodo: spec.metodo, ruta: spec.ruta, cuerpo: spec.cuerpo ?? null, campos: spec.campos ?? null,
      archivo: spec.archivo ?? null, nombreArchivo: spec.nombreArchivo ?? null,
      etiqueta: spec.etiqueta ?? '', localId: spec.localId ?? null,
      creadoEn: this.now(), intentos: 0, estado: 'pendiente', ultimoError: null,
    };
    await this.#save(rec);
    return rec;
  }

  async get(id) { return (await this.#load()).find((r) => r.id === id) ?? null; }
  async discard(id) { await this.adapter.delete(id); }
  /** Un envío rechazado vuelve a la cola solo si la persona lo pide. */
  async retry(id) {
    const rec = await this.get(id);
    if (rec?.estado === 'rechazado') { rec.estado = 'pendiente'; rec.ultimoError = null; rec.intentos = 0; await this.#save(rec); }
  }

  /** visitId real de una visita local ya enviada, o null si todavía no se envió. */
  async resolveLocal(localId) { return (await this.#mapa())[localId] ?? null; }
  async #mapa() { return (await this.adapter.getMeta('mapa')) ?? {}; }

  /** Sustituye {{visita:local:...}} por el visitId real. Devuelve null en lugar del registro si falta alguna correspondencia. */
  async #resolve(rec) {
    const mapa = await this.#mapa();
    const parts = { ruta: rec.ruta, cuerpo: rec.cuerpo, campos: rec.campos };
    const text = JSON.stringify(parts);
    const falta = markersIn(text).filter((id) => !mapa[id]);
    if (falta.length) return { falta };
    const done = JSON.parse(text.replace(MARKER, (_, id) => mapa[id]));
    return { rec: { ...rec, ...done } };
  }

  /** Envía en orden hasta vaciar la cola o hasta que algo obligue a parar. Un solo envío a la vez. */
  flush() {
    if (this.running) return this.running;
    this.running = this.#run().finally(() => { this.running = null; });
    return this.running;
  }

  async #run() {
    const out = { enviados: 0, rechazados: 0, pausa: null, esperaMs: 0 };
    for (;;) {
      const next = (await this.#load()).find((r) => r.estado !== 'rechazado');
      if (!next) { this.espera = 0; return out; }
      const resolved = await this.#resolve(next);
      if (resolved.falta) {
        // La visita de la que depende fue rechazada o descartada: sin ella este envío no se puede enviar.
        next.estado = 'rechazado'; out.rechazados++;
        next.ultimoError = { code: 'visita_no_enviada', detail: 'Depende de una visita que no se envió.', errors: null };
        await this.#save(next);
        continue;
      }
      next.estado = 'enviando'; await this.#save(next);
      try {
        const res = await this.send(resolved.rec);
        if (next.localId && res?.visitId) {
          const mapa = await this.#mapa(); mapa[next.localId] = res.visitId; await this.adapter.setMeta('mapa', mapa);
        }
        await this.adapter.delete(next.id);
        out.enviados++; this.espera = 0;
      } catch (e) {
        const status = e?.status ?? 0;
        next.ultimoError = { code: e?.code ?? 'error', detail: e?.detail ?? null, errors: e?.errors ?? null, extra: e?.extra ?? null, status };
        if (status === 401) { next.estado = 'pendiente'; await this.#save(next); out.pausa = 'auth'; return out; }
        if (status === 0 || status === 429 || status >= 500) {
          next.estado = 'pendiente'; next.intentos++; await this.#save(next);
          this.espera = backoff(next.intentos); out.pausa = status === 0 ? 'red' : 'servidor'; out.esperaMs = this.espera;
          return out;
        }
        next.estado = 'rechazado'; out.rechazados++; await this.#save(next);
      }
    }
  }
}

/** Almacenamiento en memoria para pruebas. */
export class MemoryAdapter {
  constructor() { this.recs = new Map(); this.meta = new Map(); }
  async all() { return [...this.recs.values()].map((r) => structuredClone(r)); }
  async put(rec) { this.recs.set(rec.id, structuredClone(rec)); }
  async delete(id) { this.recs.delete(id); }
  async getMeta(k) { return this.meta.has(k) ? structuredClone(this.meta.get(k)) : undefined; }
  async setMeta(k, v) { this.meta.set(k, structuredClone(v)); }
}
