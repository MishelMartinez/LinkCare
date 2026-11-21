// Textos de la interfaz. Solo "es" por ahora; los archivos siguen el patrón i18n.{idioma}.{ámbito}.json.
export class I18nService {
  constructor(lang = 'es', fetchFn = (...a) => fetch(...a)) { this.lang = lang; this.dict = {}; this.fetchFn = fetchFn; this.loaded = new Set(); }
  async load(base, scope) {
    if (this.loaded.has(`${base}|${scope}`)) return; // ya cargado: así las pantallas abiertas siguen funcionando sin red
    const res = await this.fetchFn(`${base}/i18n.${this.lang}.${scope}.json`);
    if (!res.ok) throw new Error(`No se pudo cargar ${scope}`);
    Object.assign(this.dict, await res.json());
    this.loaded.add(`${base}|${scope}`);
  }
  add(entries) { Object.assign(this.dict, entries); }
  /** t('clave', {n: 3}) reemplaza {n}. Si falta la clave devuelve la clave (visible en pruebas). */
  t(key, vars = {}) {
    const s = this.dict[key] ?? key;
    return s.replace(/\{(\w+)\}/g, (_, k) => (vars[k] ?? `{${k}}`));
  }
}
