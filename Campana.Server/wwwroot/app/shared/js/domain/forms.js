// Definición de los formularios de captura y conversión entre lo que escribe la persona y el JSON que valida el servidor.
// Las claves usan puntos para anidar: 'factoresRiesgo.menarcaEdad' → { factoresRiesgo: { menarcaEdad } }.
const opt = (pares) => pares.map(([v, t]) => ({ v: String(v), t }));
const SINO = opt([['true', 'Sí'], ['false', 'No']]);
const f = (clave, etiqueta, tipo = 'texto', extra = {}) => ({ clave, etiqueta, tipo, ...extra });

export const MAMA = {
  tipo: 'mama', version: 1, titulo: 'Revisión mamaria', provisional: false,
  secciones: [
    { id: 'identificacion', titulo: 'Identificación y somatometría', campos: [
      f('folio', 'Folio EIRA', 'texto', { max: 40 }),
      f('fechaAtencion', 'Fecha de atención', 'fecha', { requerido: true }),
      f('cuentaCartillaEira', 'Cuenta con cartilla EIRA', 'lista', { opciones: SINO }),
      f('somatometria.talla', 'Talla (cm)', 'numero', { min: 0, max: 250 }),
      f('somatometria.peso', 'Peso (kg)', 'numero', { min: 0, max: 500 }),
      f('somatometria.pa', 'Presión arterial (120/80)', 'texto', { max: 20 }),
      f('somatometria.glc', 'Glucosa (GLC)', 'numero', { min: 0, max: 1000 }),
      f('somatometria.bt', 'BT (significado por confirmar)', 'texto', { max: 20 }),
      f('somatometria.at', 'AT (significado por confirmar)', 'texto', { max: 20 }),
      f('afiliacion.codigo', 'Afiliación', 'lista', { opciones: opt([[1, 'INSABI'], [2, 'IMSS'], [3, 'ISSSTE'], [4, 'SEDENA'], [5, 'SEMAR'], [6, 'Ninguno'], [7, 'Otro']]), entero: true }),
      f('afiliacion.otro', 'Otra afiliación', 'texto', { max: 100 }),
      f('motivoConsulta', 'Motivo de consulta', 'area', { max: 2000 }),
    ] },
    { id: 'riesgo', titulo: 'Factores de riesgo', campos: [
      f('factoresRiesgo.menarcaEdad', 'Edad de la primera menstruación (años)', 'numero', { min: 5, max: 25, entero: true }),
      f('factoresRiesgo.familiaresCancerMamario', 'Familiares con cáncer de mama', 'lista', { entero: true, opciones: opt([[1, 'Madre'], [2, 'Hermana'], [3, 'Hija'], [4, '1 y 2'], [5, '1 y 3'], [6, '2 y 3'], [7, 'Ninguno']]) }),
      f('factoresRiesgo.fum', 'Fecha de última menstruación', 'fecha'),
      f('factoresRiesgo.anticoncepcion.tipo', 'Anticoncepción: tipo', 'texto', { max: 100 }),
      f('factoresRiesgo.anticoncepcion.inicio', 'Anticoncepción: inicio', 'texto', { max: 50 }),
      f('factoresRiesgo.anticoncepcion.tiempo', 'Anticoncepción: tiempo de uso', 'texto', { max: 50 }),
      f('factoresRiesgo.anticoncepcion.suspension', 'Anticoncepción: suspensión', 'texto', { max: 50 }),
      f('factoresRiesgo.menopausiaEdad', 'Edad de la menopausia (años)', 'numero', { min: 30, max: 75, entero: true }),
      f('factoresRiesgo.mastografiaPrevia', 'Mastografía previa', 'lista', { opciones: SINO }),
      f('factoresRiesgo.fechaUltimaMastografia', 'Fecha de la última mastografía', 'fecha'),
      f('factoresRiesgo.resultadoUltimaMastografia', 'Resultado de la última mastografía', 'area', { max: 1000 }),
      f('factoresRiesgo.observacionesMastografia', 'Observaciones de la mastografía', 'area', { max: 1000 }),
      f('factoresRiesgo.otros', 'Otros factores de riesgo', 'area', { max: 2000 }),
    ] },
    { id: 'evaluacion', titulo: 'Evaluación', campos: [
      f('evaluacion.exploracionClinica', 'Exploración clínica', 'area', { max: 4000 }),
      f('evaluacion.termografia', 'Termografía', 'lista', { entero: true, opciones: opt([[1, 'Simétrico'], [2, 'Asimétrico']]) }),
      f('evaluacion.ultrasonido', 'Ultrasonido', 'area', { max: 8000 }),
      f('evaluacion.responsable.nombre', 'Responsable: nombre', 'texto', { max: 200 }),
      f('evaluacion.responsable.cedula', 'Responsable: cédula', 'texto', { max: 50 }),
    ] },
    { id: 'diagnostico', titulo: 'Diagnóstico y referencia', campos: [
      f('diagnostico.birads', 'BI-RADS', 'lista', { entero: true, opciones: opt([[0, '0 · No concluyente'], [1, '1 · Normal'], [2, '2 · Hallazgos benignos'], [3, '3 · Probablemente benignos'], [4, '4 · Probablemente maligno'], [5, '5 · Malignos']]) }),
      f('referencia.conducta', 'Conducta', 'lista', { entero: true, opciones: opt([[1, 'Detección de rutina en un año'], [2, 'Mastografía de tamizaje'], [3, 'Referencia a evaluación con imagen complementaria'], [4, 'Evaluación en seis meses']]) }),
      f('referencia.motivo', 'Motivo de la referencia', 'lista', { entero: true, opciones: opt([[1, 'Tumoración palpable'], [2, 'Signos sugestivos'], [3, 'Estudio anormal'], [4, 'Factores de riesgo']]) }),
      f('notas', 'Notas', 'area', { max: 4000 }),
    ] },
  ],
};

export const PAPANICOLAOU = {
  tipo: 'papanicolaou', version: 1, titulo: 'Papanicolaou', provisional: true,
  secciones: [{ id: 'unica', titulo: 'Atención', campos: [
    f('folio', 'Folio', 'texto', { max: 40 }),
    f('fechaAtencion', 'Fecha de atención', 'fecha', { requerido: true }),
    f('resultado', 'Resultado', 'area', { max: 2000 }),
    f('sospechosa', 'Resultado de alerta', 'lista', { opciones: SINO }),
    f('observaciones', 'Observaciones', 'area', { max: 4000 }),
  ] }],
};

export const FORMS = { mama: MAMA, papanicolaou: PAPANICOLAOU };
export const formFor = (tipo) => FORMS[tipo] ?? null;
export const allFields = (def) => def.secciones.flatMap((s) => s.campos);

const empty = (v) => v === undefined || v === null || String(v).trim() === '';
function convert(field, raw) {
  const s = String(raw).trim();
  if (field.tipo === 'numero') return Number(s.replace(',', '.'));
  if (field.tipo === 'lista') return s === 'true' ? true : s === 'false' ? false : field.entero ? Number(s) : s;
  return s;
}
function setPath(obj, path, value) {
  const keys = path.split('.');
  let o = obj;
  for (const k of keys.slice(0, -1)) o = (o[k] ??= {});
  o[keys.at(-1)] = value;
}
/** values: { clave: texto }. Los vacíos no se envían. */
export function buildPayload(def, values) {
  const out = {};
  for (const field of allFields(def)) {
    const raw = values[field.clave];
    if (!empty(raw)) setPath(out, field.clave, convert(field, raw));
  }
  return out;
}
/** Revisión local antes de enviar: lo mismo que el servidor, para avisar sin esperar. El servidor sigue siendo quien decide. */
export function validate(def, values) {
  const errors = {};
  for (const field of allFields(def)) {
    const raw = values[field.clave];
    if (empty(raw)) { if (field.requerido) errors[field.clave] = 'Este dato es obligatorio.'; continue; }
    const s = String(raw).trim();
    if (field.tipo === 'fecha' && !/^\d{4}-\d{2}-\d{2}$/.test(s)) errors[field.clave] = 'Use el formato AAAA-MM-DD.';
    if (field.tipo === 'numero') {
      const n = Number(s.replace(',', '.'));
      if (!Number.isFinite(n)) errors[field.clave] = 'Escriba un número.';
      else if (field.entero && !Number.isInteger(n)) errors[field.clave] = 'Escriba un número entero.';
      else if (field.min !== undefined && n < field.min) errors[field.clave] = `Mínimo ${field.min}.`;
      else if (field.max !== undefined && n > field.max) errors[field.clave] = `Máximo ${field.max}.`;
    }
    if ((field.tipo === 'texto' || field.tipo === 'area') && field.max && s.length > field.max) errors[field.clave] = `Hasta ${field.max} caracteres.`;
    if (field.tipo === 'lista' && !field.opciones.some((o) => o.v === s)) errors[field.clave] = 'Elija una opción de la lista.';
  }
  return errors;
}
/** Avance: campos llenos entre los del formulario, general y por sección. */
export function progress(def, values) {
  const sections = def.secciones.map((s) => {
    const total = s.campos.length; const filled = s.campos.filter((c) => !empty(values[c.clave])).length;
    return { id: s.id, filled, total };
  });
  const filled = sections.reduce((a, s) => a + s.filled, 0); const total = sections.reduce((a, s) => a + s.total, 0);
  return { sections, filled, total, percent: total ? Math.round((filled / total) * 100) : 0 };
}
/** Errores del servidor ([{path, message}]) → { clave: mensaje } y los que no corresponden a ningún campo. */
export function mapServerErrors(def, errors) {
  const byField = {}; const general = [];
  const claves = new Set(allFields(def).map((f2) => f2.clave));
  for (const e of Array.isArray(errors) ? errors : []) {
    const path = String(e.path ?? '').replace(/^\/payload/, '').replace(/^\//, '').replace(/\//g, '.');
    const message = e.message ?? 'Dato no válido.';
    if (claves.has(path)) byField[path] ??= message; else general.push(path ? `${path}: ${message}` : message);
  }
  return { byField, general };
}
/** payload del servidor → valores del formulario (texto). */
export function valuesFromPayload(def, payload) {
  const out = {};
  for (const field of allFields(def)) {
    let v = payload;
    for (const k of field.clave.split('.')) v = v?.[k];
    if (v !== undefined && v !== null) out[field.clave] = String(v);
  }
  return out;
}
/** Solo los campos que cambiaron respecto al original, ya como JSON para el campo "datos" de una corrección. */
export function diffPayload(def, original, values) {
  const before = valuesFromPayload(def, original);
  const changed = {};
  for (const field of allFields(def)) {
    const now = values[field.clave];
    if (!empty(now) && String(now).trim() !== (before[field.clave] ?? '')) changed[field.clave] = now;
  }
  return buildPayload(def, changed);
}
export const today = (now = new Date()) => `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
/** Filas legibles de una atención guardada: etiqueta y valor (con el texto de la opción, no el número). */
export function describePayload(def, payload) {
  const rows = [];
  for (const field of allFields(def)) {
    let v = payload;
    for (const k of field.clave.split('.')) v = v?.[k];
    if (v === undefined || v === null || v === '') continue;
    const op = field.opciones?.find((o) => o.v === String(v));
    rows.push({ etiqueta: field.etiqueta, valor: op ? op.t : String(v) });
  }
  return rows;
}
