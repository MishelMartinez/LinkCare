import { STAGES } from './stages.js';
const norm = (s) => String(s ?? '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();

/** Filtra por texto (lugar o clave geográfica) y por señales activas. */
export function filterCampaigns(list, { texto = '', soloSenales = false } = {}) {
  const q = norm(texto).trim();
  return (list ?? []).filter((s) => {
    const c = s.campaign ?? {};
    if (q && !norm(`${c.lugar} ${c.unidadGeografica?.clave ?? ''} ${c.unidadGeografica?.municipio ?? ''}`).includes(q)) return false;
    if (soloSenales && !hasSignals(s)) return false;
    return true;
  });
}
export const hasSignals = (s) => Object.values(s?.senales ?? {}).some((n) => n > 0);
/** En campo primero, luego por fecha de inicio descendente. */
export function sortCampaigns(list) {
  const peso = (e) => STAGES.indexOf(e);
  return [...(list ?? [])].sort((a, b) => {
    const c = (x) => x.campaign;
    const enCampo = (x) => (['concientizacion', 'consulta'].includes(c(x).estado) ? 0 : 1);
    return enCampo(a) - enCampo(b) || String(c(b).fechaInicio).localeCompare(String(c(a).fechaInicio)) || peso(c(a).estado) - peso(c(b).estado);
  });
}
/** Las luces de la tarjeta: cada una se enciende si su contador es mayor que cero. */
export function lights(senales) {
  const s = senales ?? {};
  return [
    { clave: 'casosSospechosos', n: s.casosSospechosos ?? 0, tono: 'danger', icono: 'alert' },
    { clave: 'sinDocumentos', n: s.sinDocumentos ?? 0, tono: 'warn', icono: 'file' },
    { clave: 'conflictosSincronizacion', n: s.conflictosSincronizacion ?? 0, tono: 'danger', icono: 'refresh' },
    { clave: 'sincronizacionPendiente', n: s.sincronizacionPendiente ?? 0, tono: 'warn', icono: 'cloud' },
  ].map((l) => ({ ...l, on: l.n > 0 }));
}
