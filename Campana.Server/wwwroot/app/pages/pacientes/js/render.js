import { html } from '../../../shared/js/domain/html.js';
import { formatDate } from '../../../shared/js/domain/format.js';
import { icon } from '../../../shared/js/components/icon.js';
import { emptyHtml } from '../../../shared/js/components/EmptyState.js';

export function pageHtml({ t, campania, puedeEscribir, aviso, texto }) {
  return html`
  <div class="mb-3 flex flex-wrap items-center gap-3">
    <h1 class="text-2xl font-bold">${t('pacientes.titulo')}</h1>
    ${campania ? html`<span class="rounded-full bg-brand-soft px-3 py-1 text-xs font-semibold text-brand-dark">${t('pacientes.campaniaActiva')}: ${campania.lugar} · ${t('etapa.' + campania.estado)}</span>` : html`<span class="text-sm text-muted">${t('pacientes.sinCampania')}</span>`}
    ${puedeEscribir ? html`<button type="button" data-action="registrar" class="touch ml-auto inline-flex items-center gap-2 rounded-lg bg-brand px-4 font-semibold text-white">${icon('users')}${t('pacientes.registrar')}</button>` : ''}
  </div>
  ${aviso ? html`<p class="mb-3 rounded-lg bg-amber-50 p-3 text-sm text-warn" role="status">${aviso}</p>` : ''}
  <form id="busqueda" class="mb-4 flex gap-2" role="search">
    <label class="flex-1"><span class="sr-only">${t('pacientes.buscar')}</span>
      <input name="q" type="search" value="${texto}" placeholder="${t('pacientes.buscar')}" autocomplete="off" class="touch w-full rounded-lg border border-line px-3"></label>
    <button type="submit" class="touch inline-flex items-center gap-2 rounded-lg border border-line bg-surface px-4">${icon('search')}${t('pacientes.buscarBoton')}</button>
  </form>
  <p id="busqueda-error" role="alert" class="mb-2 hidden text-sm text-danger"></p>
  <div id="resultados"></div>
  <div id="registro"></div>`;
}

export function resultadosHtml(list, t, hayTexto) {
  if (!hayTexto) return emptyHtml(t('pacientes.vacio'));
  if (!list.length) return emptyHtml(t('pacientes.sinResultados'));
  return html`<ul class="grid gap-2 sm:grid-cols-2">${list.map((p) => html`
    <li><a href="#/paciente/${encodeURIComponent(p.patientId)}" class="flex flex-col gap-1 rounded-xl border border-line bg-surface p-3 shadow-sm hover:border-brand">
      <span class="font-bold">${p.nombre}</span>
      <span class="text-xs text-muted">${t('pacientes.nacimiento')}: ${formatDate(p.fechaNacimiento)} · ${t('pacientes.campanas')}: ${p.campanas.length}</span>
    </a></li>`)}</ul>`;
}

const campo = (name, label, extra = '') => html`<label class="flex flex-col gap-1 text-sm font-medium">${label}<input name="${name}" ${extra ? html`placeholder="${extra}"` : ''} class="touch rounded-lg border border-line px-3 font-normal"></label>`;

export function registroHtml(t) {
  return html`
  <dialog id="dlg-registro" class="w-[min(94vw,34rem)] max-h-[90vh] overflow-auto rounded-xl p-0 shadow-xl backdrop:bg-black/40">
    <form id="form-registro" class="flex flex-col gap-3 p-5" novalidate>
      <h2 class="text-lg font-bold">${t('registro.titulo')}</h2>
      <div class="grid gap-3 sm:grid-cols-2">
        ${campo('nombres', t('registro.nombres'))}${campo('apellidos', t('registro.apellidos'))}
        ${campo('fechaNacimiento', t('registro.fechaNacimiento'), 'AAAA-MM-DD')}${campo('entidadNacimiento', t('registro.entidadNacimiento'))}
        ${campo('documentoTipo', t('registro.documentoTipo'), 'INE')}${campo('documentoNumero', t('registro.documentoNumero'))}
        ${campo('telefono', t('registro.telefono'))}${campo('celular', t('registro.celular'))}
        ${campo('localidad', t('registro.localidad'))}
      </div>
      <p id="registro-error" role="alert" class="hidden text-sm text-danger"></p>
      <div id="duplicadas"></div>
      <div class="flex justify-end gap-2">
        <button type="button" data-action="cancelar-registro" class="touch rounded-lg border border-line px-4">${t('registro.cancelar')}</button>
        <button type="submit" id="registro-enviar" class="touch rounded-lg bg-brand px-4 font-semibold text-white">${t('registro.guardar')}</button>
      </div>
    </form>
  </dialog>`;
}

/** Candidatas devueltas por duplicate_candidates, con puntaje y atributos que coinciden. */
export function candidatasHtml(candidatas, t) {
  return html`<section class="rounded-lg border border-warn/40 bg-amber-50 p-3" aria-label="${t('dup.titulo')}">
    <h3 class="font-bold text-warn">${t('dup.titulo')}</h3>
    <p class="mb-2 text-sm">${t('dup.texto')}</p>
    <ul class="flex flex-col gap-2">${candidatas.map((c) => html`
      <li class="rounded-lg border border-line bg-surface p-3">
        <div class="flex flex-wrap items-center gap-2"><span class="font-bold">${c.nombre}</span><span class="text-xs text-muted">${formatDate(c.fechaNacimiento)}</span>
          <span class="ml-auto rounded-full bg-amber-100 px-2 py-0.5 text-xs font-semibold text-warn">${t('dup.puntaje')}: ${c.puntaje}</span></div>
        <p class="mt-1 text-xs text-muted">${t('dup.coincide')}: ${(c.coincidencias ?? [c.motivo]).join(', ')} · ${t('dup.campanas')}: ${(c.campanas ?? []).length}</p>
        <button type="button" data-action="misma" data-id="${c.patientId}" class="touch mt-2 rounded-lg bg-brand px-3 text-sm font-semibold text-white">${t('dup.misma')}</button>
      </li>`)}</ul>
    <button type="button" data-action="otra" class="touch mt-3 rounded-lg border border-line bg-surface px-3 text-sm">${t('dup.otra')}</button>
  </section>`;
}
