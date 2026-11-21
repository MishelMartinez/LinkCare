// Funciones puras sobre pacientes y su expediente.
export const fullName = (p) => [p?.nombres, p?.apellidos].filter(Boolean).join(' ');

/** Identidad del servidor → mapa plano con los nombres de campo que acepta el PATCH de identidad. */
export function identityFlat(d) {
  const g = (v) => (v === undefined || v === null ? '' : String(v));
  return {
    nombres: g(d?.nombres), apellidos: g(d?.apellidos), fechaNacimiento: g(d?.fechaNacimiento), entidadNacimiento: g(d?.entidadNacimiento),
    'documento.tipo': g(d?.documento?.tipo), 'documento.numero': g(d?.documento?.numero),
    'contacto.telefono': g(d?.contacto?.telefono), 'contacto.celular': g(d?.contacto?.celular),
    'contacto.localidad': g(d?.contacto?.localidad), 'contacto.residencia': g(d?.contacto?.residencia),
  };
}
const REQUIRED = new Set(['nombres', 'apellidos', 'fechaNacimiento']);
/** Solo lo que cambió. Vaciar un dato opcional se envía como null; los obligatorios no se pueden vaciar. */
export function identityChanges(current, edited) {
  const out = {};
  for (const [k, v] of Object.entries(edited)) {
    const before = current[k] ?? '';
    const now = String(v ?? '').trim();
    if (now === before) continue;
    if (now === '') { if (!REQUIRED.has(k)) out[k] = null; continue; }
    out[k] = now;
  }
  return out;
}
/** Detalle de un identity_conflict (errors del servidor) en filas legibles. */
export function conflictRows(errors) {
  return (Array.isArray(errors) ? errors : []).map((e) => ({
    campo: e.campo ?? '', actual: e.valorActual ?? '', enviado: e.valorEnviado ?? '', por: e.modificadoPor ?? '', version: e.versionActual ?? null,
  }));
}
/** Edad en años cumplidos. */
export function age(fechaNacimiento, now = new Date()) {
  const d = new Date(String(fechaNacimiento) + 'T00:00:00');
  if (Number.isNaN(d.getTime())) return null;
  let a = now.getFullYear() - d.getFullYear();
  if (now.getMonth() < d.getMonth() || (now.getMonth() === d.getMonth() && now.getDate() < d.getDate())) a--;
  return a < 0 ? null : a;
}
/** Visitas de la más reciente a la más antigua. */
export const visitsNewestFirst = (v) => [...(v?.visitas ?? [])].sort((a, b) => String(b.fecha).localeCompare(String(a.fecha)));
/** Qué tipos de servicio tienen formulario en la interfaz. */
export const CAPTURABLES = ['mama', 'papanicolaou'];
export const isCapturable = (tipo) => CAPTURABLES.includes(tipo);
/** Una paciente solo se puede capturar si la campaña activa está en consulta. */
export const canWrite = (role, campaign) => role === 'medico' && campaign?.estado === 'consulta';
/** Texto de búsqueda: al menos 2 caracteres útiles. */
export const validSearch = (q) => String(q ?? '').trim().length >= 2;
/** ¿La paciente ya está registrada en esa campaña? El servidor entrega la lista en "campanas". */
export const registeredIn = (record, campaignId) => (record?.campanas ?? []).includes(campaignId);
/** Envíos pendientes de la cola que pertenecen a esta paciente. */
export const pendingFor = (records, patientId) => (records ?? []).filter((r) => String(r.ruta).includes(`/patients/${patientId}/`) || String(r.ruta).includes(`/patients/${encodeURIComponent(patientId)}/`));
