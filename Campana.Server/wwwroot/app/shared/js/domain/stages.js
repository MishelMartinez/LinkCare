// Etapas de la campaña. Solo avanzan, de una en una (igual que el servidor).
export const STAGES = ['asignada', 'concientizacion', 'consulta', 'concluida'];

export const stageIndex = (s) => STAGES.indexOf(s);
export const isStage = (s) => stageIndex(s) >= 0;
export const nextStage = (s) => { const i = stageIndex(s); return i >= 0 && i < STAGES.length - 1 ? STAGES[i + 1] : null; };
/** Una tarjeta solo puede soltarse en la columna siguiente a la suya. */
export const isNextOf = (from, to) => nextStage(from) === to && to !== null;
/** Solo los coordinadores mueven etapas, y la campaña concluida no se mueve. */
export const canAdvance = (role, from) =>
  (role === 'coordinador-general' || role === 'coordinador-campana') && nextStage(from) !== null;
/** Agrupa campañas (resúmenes) por etapa; las etapas sin campañas quedan vacías. */
export function groupByStage(summaries) {
  const out = Object.fromEntries(STAGES.map((s) => [s, []]));
  for (const s of summaries ?? []) { const e = s?.campaign?.estado; if (out[e]) out[e].push(s); }
  return out;
}
