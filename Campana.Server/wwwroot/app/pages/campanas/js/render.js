import { html } from '../../../shared/js/domain/html.js';
import { STAGES, canAdvance, nextStage } from '../../../shared/js/domain/stages.js';
import { groupByStage } from '../../../shared/js/domain/stages.js';
import { formatDate, percent } from '../../../shared/js/domain/format.js';
import { signalLightsHtml } from '../../../shared/js/components/SignalLights.js';
import { emptyHtml } from '../../../shared/js/components/EmptyState.js';
import { icon } from '../../../shared/js/components/icon.js';

const TONO = { asignada: 'border-t-gray-400', concientizacion: 'border-t-amber-500', consulta: 'border-t-brand', concluida: 'border-t-ok' };

export function cardHtml(s, { role, t }) {
  const c = s.campaign;
  const mueve = canAdvance(role, c.estado);
  const sig = nextStage(c.estado);
  const avance = percent(s.pacientesAtendidas, s.pacientesRegistradas);
  return html`
  <article class="kanban-card rounded-xl border border-line bg-surface p-3 shadow-sm" data-id="${c.campaignId}" data-stage="${c.estado}" ${mueve ? html`draggable="true"` : ''}>
    <h3 class="font-bold leading-tight">${c.lugar}</h3>
    <p class="mt-1 flex flex-wrap items-center gap-x-3 text-xs text-muted">
      <span class="inline-flex items-center gap-1">${icon('calendar', 'h-4 w-4')}${formatDate(c.fechaInicio)} – ${formatDate(c.fechaFin)}</span>
      ${c.unidadGeografica?.municipio ? html`<span class="inline-flex items-center gap-1">${icon('map-pin', 'h-4 w-4')}${c.unidadGeografica.municipio}</span>` : ''}
    </p>
    <dl class="mt-2 grid grid-cols-3 gap-1 text-center text-xs">
      <div><dt class="text-muted">${t('campanas.medicos')}</dt><dd class="text-base font-bold">${s.medicos}</dd></div>
      <div><dt class="text-muted">${t('campanas.visitas')}</dt><dd class="text-base font-bold">${s.visitas}</dd></div>
      <div><dt class="text-muted">${t('campanas.estudios')}</dt><dd class="text-base font-bold">${s.estudios}</dd></div>
    </dl>
    <div class="mt-2" title="${t('campanas.atendidas')}: ${s.pacientesAtendidas}/${s.pacientesRegistradas}">
      <div class="h-1.5 w-full overflow-hidden rounded bg-gray-200"><div class="h-full bg-brand" data-width="${avance}"></div></div>
    </div>
    <div class="mt-2">${signalLightsHtml(s.senales, t)}</div>
    <div class="mt-3 flex flex-wrap gap-2">
      <a href="#/campana/${encodeURIComponent(c.campaignId)}" class="touch inline-flex flex-1 items-center justify-center gap-1 rounded-lg border border-line px-3 text-sm font-medium">${t('app.abrir')}</a>
      ${mueve ? html`<button type="button" data-action="advance" data-id="${c.campaignId}" data-to="${sig}" class="touch inline-flex flex-1 items-center justify-center gap-1 rounded-lg bg-brand px-3 text-sm font-semibold text-white">${t('campanas.avanzar', { etapa: t('etapa.' + sig) })}${icon('arrow-right', 'h-4 w-4')}</button>` : ''}
    </div>
  </article>`;
}

export function kanbanHtml(summaries, { role, t }) {
  const groups = groupByStage(summaries);
  return html`<div class="grid gap-3 md:grid-cols-2 xl:grid-cols-4">${STAGES.map((st) => html`
    <section class="kanban-col flex min-h-[8rem] flex-col gap-2 rounded-xl border border-line border-t-4 ${TONO[st]} bg-gray-50 p-2" data-stage="${st}" aria-label="${t('etapa.' + st)}">
      <h2 class="flex items-center justify-between px-1 text-sm font-bold">${t('etapa.' + st)}<span class="rounded-full bg-white px-2 text-xs text-muted">${groups[st].length}</span></h2>
      ${groups[st].length ? groups[st].map((s) => cardHtml(s, { role, t })) : html`<p class="p-3 text-center text-xs text-muted">${t('campanas.columnaVacia')}</p>`}
    </section>`)}</div>`;
}

export function pageHtml({ t, role, texto, soloSenales }) {
  return html`
  <div class="mb-3 flex flex-wrap items-center gap-3">
    <h1 class="text-2xl font-bold">${t('campanas.titulo')}</h1>
    <label class="relative ml-auto"><span class="sr-only">${t('campanas.buscar')}</span>
      <input id="filtro-texto" type="search" value="${texto}" placeholder="${t('campanas.buscar')}" class="touch w-64 rounded-lg border border-line px-3"></label>
    <label class="inline-flex touch items-center gap-2 text-sm"><input id="filtro-senales" type="checkbox" ${soloSenales ? html`checked` : ''}>${t('campanas.soloSenales')}</label>
  </div>
  ${role === 'coordinador-general' || role === 'coordinador-campana' ? html`<p class="mb-3 text-xs text-muted">${t('campanas.arrastrar')}</p>` : ''}
  <div id="tablero"></div>`;
}
export const vacioHtml = (t, hayFiltro) => emptyHtml(t(hayFiltro ? 'campanas.sinResultados' : 'campanas.vacio'), hayFiltro ? '' : t('campanas.vacioTexto'));
