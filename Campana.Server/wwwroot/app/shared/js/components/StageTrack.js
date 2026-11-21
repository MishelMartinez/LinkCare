import { html, raw } from '../domain/html.js';
import { STAGES, stageIndex } from '../domain/stages.js';
import { icon } from './icon.js';
/** Recorrido de etapas: hechas, actual y pendientes. */
export function stageTrackHtml(estado, t) {
  const cur = stageIndex(estado);
  return html`<ol class="flex flex-wrap items-center gap-2" aria-label="${t('etapa.titulo')}">${STAGES.map((s, i) => html`
    <li class="inline-flex items-center gap-1 rounded-full px-3 py-1 text-sm ${i < cur ? 'bg-green-100 text-ok' : i === cur ? 'bg-brand text-white font-semibold' : 'bg-gray-100 text-muted'}" ${i === cur ? raw('aria-current="step"') : ''}>
      ${i < cur ? icon('check', 'h-4 w-4') : ''}${t('etapa.' + s)}
    </li>`)}</ol>`;
}
