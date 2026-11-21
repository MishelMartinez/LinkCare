// Almacenamiento de la cola en IndexedDB: una base por usuario. Los datos quedan sin cifrar en el equipo
// hasta que se decida la política (ver protect/unprotect en OutboxService).
export class IndexedDbAdapter {
  constructor(usuario, idb = globalThis.indexedDB) { this.name = `campana-envios:${encodeURIComponent(usuario)}`; this.idb = idb; this.dbp = null; }
  #db() {
    this.dbp ??= new Promise((resolve, reject) => {
      const req = this.idb.open(this.name, 1);
      req.onupgradeneeded = () => { req.result.createObjectStore('envios', { keyPath: 'id' }); req.result.createObjectStore('meta'); };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
    return this.dbp;
  }
  async #tx(store, mode, fn) {
    const db = await this.#db();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(store, mode);
      const req = fn(tx.objectStore(store));
      tx.oncomplete = () => resolve(req?.result);
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  }
  all() { return this.#tx('envios', 'readonly', (s) => s.getAll()); }
  put(rec) { return this.#tx('envios', 'readwrite', (s) => s.put(rec)); }
  delete(id) { return this.#tx('envios', 'readwrite', (s) => s.delete(id)); }
  getMeta(k) { return this.#tx('meta', 'readonly', (s) => s.get(k)); }
  setMeta(k, v) { return this.#tx('meta', 'readwrite', (s) => s.put(v, k)); }
  async close() { (await this.dbp)?.close(); }
}
