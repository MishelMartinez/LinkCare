import { html } from '../../../shared/js/domain/html.js';
import { formatDate, formatDateTime, timeAgo } from '../../../shared/js/domain/format.js';
import { stageTrackHtml } from '../../../shared/js/components/StageTrack.js';
import { signalLightsHtml } from '../../../shared/js/components/SignalLights.js';
import { avatarHtml } from '../../../shared/js/components/Avatar.js';
import { emptyHtml } from '../../../shared/js/components/EmptyState.js';
import { icon } from '../../../shared/js/components/icon.js';

const cifra = (n, etiqueta) => html`<div class="rounded-lg bg-gray-50 p-2 text-center"><dd class="text-xl font-bold">${n}</dd><dt class="text-xs text-muted">${etiqueta}</dt></div>`;

export function headerHtml(s, { t, volver }) {
  const c = s.campaign;
  return html`
  ${volver ? html`<a href="#/campanas" class="touch mb-2 inline-flex items-center gap-1 text-sm text-brand-dark">${icon('chevron-left')}${t('campana.volver')}</a>` : ''}
  <section class="rounded-xl border border-line bg-surface p-4">
    <h1 class="text-2xl font-bold">${c.lugar}</h1>
    <p class="mt-1 flex flex-wrap gap-x-4 text-sm text-muted">
      <span class="inline-flex items-center gap-1">${icon('calendar', 'h-4 w-4')}${formatDate(c.fechaInicio)} – ${formatDate(c.fechaFin)}</span>
      ${c.unidadGeografica?.municipio ? html`<span class="inline-flex items-center gap-1">${icon('map-pin', 'h-4 w-4')}${c.unidadGeografica.municipio}${c.unidadGeografica.estado ? ', ' + c.unidadGeografica.estado : ''}</span>` : ''}
    </p>
    <div class="mt-3">${stageTrackHtml(c.estado, t)}</div>
    <dl class="mt-4 grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-6">
      ${cifra(s.pacientesRegistradas, t('campana.registradas'))}${cifra(s.pacientesAtendidas, t('campana.atendidas'))}
      ${cifra(s.visitas, t('campana.visitas'))}${cifra(s.estudios, t('campana.estudios'))}
      ${cifra(s.documentos, t('campana.documentos'))}${cifra(s.casosSospechosos, t('senales.casosSospechosos'))}
    </dl>
    <div class="mt-3">${signalLightsHtml(s.senales, t)}</div>
  </section>
  <h2 class="mb-2 mt-5 text-lg font-bold">${t('campana.medicos')}</h2>
  <div id="medicos"></div>`;
}

export function medicoCardHtml(m, t, now) {
  const ultima = timeAgo(m.ultimaActividad, now);
  return html`
  <article class="rounded-xl border border-line bg-surface p-3 shadow-sm ${m.activo && m.asignado ? '' : 'opacity-70'}">
    <div class="flex items-center gap-3">${avatarHtml(m.username)}
      <div class="min-w-0"><h3 class="truncate font-bold">${m.username}</h3>
        <p class="truncate text-xs text-muted">${m.consultorioNombre ?? t('campana.sinConsultorio')}</p></div>
      ${!m.activo ? html`<span class="ml-auto rounded-full bg-gray-100 px-2 py-0.5 text-xs">${t('campana.inactivo')}</span>` : !m.asignado ? html`<span class="ml-auto rounded-full bg-amber-100 px-2 py-0.5 text-xs text-warn">${t('campana.noAsignado')}</span>` : ''}
    </div>
    <dl class="mt-3 grid grid-cols-3 gap-1 text-center text-xs">
      <div><dt class="text-muted">${t('campana.pacientes')}</dt><dd class="text-base font-bold">${m.pacientes}</dd></div>
      <div><dt class="text-muted">${t('campana.visitasMedico')}</dt><dd class="text-base font-bold">${m.visitas}</dd></div>
      <div><dt class="text-muted">${t('campana.estudiosMedico')}</dt><dd class="text-base font-bold">${m.estudios}</dd></div>
    </dl>
    <p class="mt-2 text-xs text-muted">${t('campana.ultima')}: ${ultima ?? t('campana.sinActividad')}</p>
  </article>`;
}
export const medicosHtml = (list, t, now = new Date()) => list.length
  ? html`<div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">${list.map((m) => medicoCardHtml(m, t, now))}</div>`
  : emptyHtml(t('campana.sinMedicos'), t('campana.sinMedicosTexto'));

const BIRADS = ['0', '1', '2', '3', '4', '5', 'sin_dato'];
/** Tablero de la campaña: cifras, BI-RADS y consultorios. Las barras usan data-width (la CSP no permite estilos en línea). */
export function tableroHtml(st, t) {
  const max = Math.max(1, ...BIRADS.map((k) => st.porBirads?.[k] ?? 0));
  return html`
  <div class="flex items-center gap-2"><h2 class="text-lg font-bold">${t('tablero.titulo')}</h2>
    <button type="button" data-action="excel" class="touch ml-auto rounded-lg border border-line bg-surface px-4 text-sm">${t('tablero.excel')}</button></div>
  <dl class="mt-2 grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-5">
    ${cifra(st.estudiosMama, t('tablero.mama'))}${cifra(st.estudiosPapanicolaou, t('tablero.papa'))}${cifra(st.casosSospechosos, t('senales.casosSospechosos'))}${cifra(st.casosSeguimiento, t('tablero.seguimiento'))}${cifra(st.adjuntos, t('tablero.adjuntos'))}
  </dl>
  <div class="mt-3 grid gap-3 lg:grid-cols-2">
    <section class="rounded-xl border border-line bg-surface p-3"><h3 class="mb-2 font-bold">${t('tablero.birads')}</h3>
      <ul class="flex flex-col gap-1">${BIRADS.map((k) => html`<li class="flex items-center gap-2 text-sm"><span class="w-16 text-muted">${k === 'sin_dato' ? t('tablero.sin_dato') : k}</span>
        <div class="h-3 flex-1 overflow-hidden rounded bg-gray-100"><div class="h-full ${k === '4' || k === '5' ? 'bg-danger' : 'bg-brand'}" data-width="${Math.round(((st.porBirads?.[k] ?? 0) / max) * 100)}"></div></div>
        <span class="w-8 text-right font-semibold">${st.porBirads?.[k] ?? 0}</span></li>`)}</ul></section>
    <section class="rounded-xl border border-line bg-surface p-3"><h3 class="mb-2 font-bold">${t('tablero.consultorios')}</h3>
      <table class="w-full text-left text-sm"><thead><tr><th>${t('campana.consultorio')}</th><th>${t('tablero.visitas')}</th><th>${t('tablero.estudios')}</th></tr></thead>
      <tbody>${(st.porConsultorio ?? []).map((c) => html`<tr class="border-t border-line"><td class="py-1">${c.nombre}</td><td>${c.visitas}</td><td>${c.estudios}</td></tr>`)}</tbody></table></section>
  </div>`;
}

/** Estado de la sincronización con el fileshare. `rep` es el último informe (o null). */
export function syncHtml({ rep, estado, configurado, ocupado }, t) {
  const hora = rep ? formatDateTime(rep.hora) : '';
  return html`
  <div class="flex flex-wrap items-center gap-2"><h2 class="text-lg font-bold">${t('sync.titulo')}</h2>
    ${configurado ? html`<button type="button" data-action="sync" ${ocupado ? html`disabled` : ''} class="touch ml-auto rounded-lg ${estado === 'concluida' ? 'bg-brand text-white font-semibold' : 'border border-line bg-surface'} px-4 text-sm">${t(ocupado ? 'sync.sincronizando' : 'sync.ahora')}</button>` : ''}</div>
  <p class="mt-1 text-sm text-muted">${t('sync.texto')}</p>
  ${!configurado ? html`<p class="mt-2 rounded-lg bg-amber-50 p-3 text-sm text-warn">${t('sync.sinShare')}</p>`
    : html`<p class="mt-2 text-sm ${estado === 'concluida' && !rep ? 'font-semibold text-brand-dark' : 'text-muted'}">${estado === 'concluida' ? (rep ? '' : t('sync.sugerencia')) : t('sync.pendiente')}</p>`}
  ${configurado ? (rep ? html`<section class="mt-2 rounded-xl border ${rep.completo ? 'border-line' : 'border-warn/50'} bg-surface p-3">
      <p class="text-sm text-muted">${t('sync.ultima')}: ${hora} · <span class="font-semibold ${rep.completo ? 'text-ok' : 'text-warn'}">${t(rep.completo ? 'sync.completo' : 'sync.incompleta')}</span></p>
      <dl class="mt-2 grid grid-cols-2 gap-2 sm:grid-cols-4">${cifra(rep.subidos, t('sync.subidos'))}${cifra(rep.omitidos, t('sync.omitidos'))}${cifra(rep.conflictos.length, t('sync.conflictos'))}${cifra(rep.errores.length, t('sync.errores'))}</dl>
      ${rep.conflictos.length || rep.errores.length ? html`<ul class="mt-2 list-disc pl-5 text-sm text-warn">${[...rep.conflictos.map((c) => `${t('sync.conflictos')}: ${c.ruta ?? c.Ruta ?? JSON.stringify(c)}`), ...rep.errores]. map((x) => html`<li>${x}</li>`)}</ul>` : ''}
    </section>` : html`<p class="mt-2 text-sm text-muted">${t('sync.nunca')}</p>`) : ''}`;
}
