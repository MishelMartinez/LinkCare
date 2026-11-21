import { html } from '../../../shared/js/domain/html.js';
import { formatDate, formatDateTime } from '../../../shared/js/domain/format.js';
import { fullName, age, visitsNewestFirst, isCapturable } from '../../../shared/js/domain/patients.js';
import { describePayload, formFor } from '../../../shared/js/domain/forms.js';
import { icon } from '../../../shared/js/components/icon.js';
import { emptyHtml } from '../../../shared/js/components/EmptyState.js';

const fila = (label, value, t) => html`<div><dt class="text-xs text-muted">${label}</dt><dd class="font-medium">${value || t('paciente.sinDato')}</dd></div>`;

export function datosHtml(record, { t, puedeEditar }) {
  const d = record.identity; const c = d.contacto ?? {}; const doc = d.documento ?? {};
  const edad = age(d.fechaNacimiento);
  return html`
  <section class="rounded-xl border border-line bg-surface p-4">
    <div class="flex flex-wrap items-center gap-2">
      <h1 class="text-2xl font-bold">${fullName(d)}</h1>
      ${puedeEditar ? html`<button type="button" data-action="editar" class="touch ml-auto rounded-lg border border-line px-3 text-sm">${t('paciente.actualizar')}</button>` : ''}
    </div>
    <dl class="mt-3 grid grid-cols-2 gap-3 text-sm sm:grid-cols-3 lg:grid-cols-4">
      ${fila(t('paciente.nacimiento'), formatDate(d.fechaNacimiento) + (edad === null ? '' : ` · ${t('paciente.edad', { n: edad })}`), t)}
      ${fila(t('paciente.entidad'), d.entidadNacimiento, t)}
      ${fila(t('paciente.documento'), [doc.tipo, doc.numero].filter(Boolean).join(' '), t)}
      ${fila(t('paciente.telefono'), c.telefono, t)}${fila(t('paciente.celular'), c.celular, t)}
      ${fila(t('paciente.localidad'), c.localidad, t)}${fila(t('paciente.residencia'), c.residencia, t)}
    </dl>
  </section>`;
}

export const incorporarHtml = (t) => html`<div class="mt-3 flex flex-wrap items-center gap-3 rounded-xl border border-warn/40 bg-amber-50 p-3">
  <p class="flex-1 text-sm">${t('paciente.incorporarTexto')}</p>
  <button type="button" data-action="incorporar" class="touch rounded-lg bg-brand px-4 font-semibold text-white">${t('paciente.incorporar')}</button></div>`;

function servicioHtml(s, { t, campaniaId }) {
  const ver = s.tipo === 'adjunto' || s.tipo === 'mama' || s.tipo === 'papanicolaou' || s.tipo === 'correccion';
  return html`<li class="flex items-center gap-2 rounded-lg bg-gray-50 px-3 py-2 text-sm">
    <span class="font-medium">${t('tipo.' + s.tipo)}</span>
    <span class="text-xs text-muted">${formatDateTime(s.createdAt)} · ${s.consultorioId}${s.campaignId !== campaniaId ? ' · ' + t('paciente.otraCampania') : ''}</span>
    ${ver ? html`<button type="button" data-action="ver" data-event="${s.eventId}" data-tipo="${s.tipo}" data-campana="${s.campaignId}" class="touch ml-auto rounded-lg border border-line px-3 text-xs">${t('paciente.ver')}</button>` : ''}
  </li>`;
}

function accionesVisita(visitRef, { t, puedeCapturar }) {
  if (!puedeCapturar) return '';
  return html`<div class="mt-2 flex flex-wrap gap-2">
    <a href="#/captura/${encodeURIComponent(puedeCapturar)}/${encodeURIComponent(visitRef.paciente)}/mama/${encodeURIComponent(visitRef.id)}" class="touch inline-flex items-center rounded-lg bg-brand px-3 text-sm font-semibold text-white">${t('paciente.mama')}</a>
    <a href="#/captura/${encodeURIComponent(puedeCapturar)}/${encodeURIComponent(visitRef.paciente)}/papanicolaou/${encodeURIComponent(visitRef.id)}" class="touch inline-flex items-center rounded-lg border border-brand px-3 text-sm font-semibold text-brand-dark">${t('paciente.papanicolaou')}</a>
    <button type="button" data-action="adjuntar" data-visit="${visitRef.id}" class="touch rounded-lg border border-line px-3 text-sm">${t('paciente.adjuntar')}</button>
  </div>`;
}

/** puedeCapturar: id de la campaña en campo si se puede capturar ahora; si no, ''. */
export function historialHtml({ record, visits, pendientes, campania, t, puedeCapturar, puedeAbrirVisita }) {
  const patientId = record.patientId;
  const locales = (pendientes ?? []).filter((r) => r.localId);
  const lista = visitsNewestFirst(visits);
  return html`
  <div class="mb-2 mt-5 flex flex-wrap items-center gap-2">
    <h2 class="text-lg font-bold">${t('paciente.historial')}</h2>
    ${puedeAbrirVisita ? html`<button type="button" data-action="abrir-visita" class="touch ml-auto inline-flex items-center gap-2 rounded-lg bg-brand px-4 font-semibold text-white">${icon('flag')}${t('paciente.abrirVisita')}</button>` : ''}
  </div>
  <ol class="flex flex-col gap-3">
    ${locales.map((r) => html`<li class="rounded-xl border border-dashed border-warn bg-amber-50 p-3">
      <div class="flex items-center gap-2"><span class="font-bold">${t('paciente.visita')}</span><span class="rounded-full bg-amber-100 px-2 py-0.5 text-xs font-semibold text-warn">${t('paciente.pendienteEnvio')}</span></div>
      ${accionesVisita({ id: r.localId, paciente: patientId }, { t, puedeCapturar })}</li>`)}
    ${lista.map((v) => html`<li class="rounded-xl border border-line bg-surface p-3">
      <div class="flex flex-wrap items-center gap-2"><span class="font-bold">${t('paciente.visita')} · ${formatDateTime(v.fecha)}</span>
        <span class="text-xs text-muted">${v.consultorioId} · ${v.creadaPor ?? ''}</span>
        <span class="ml-auto rounded-full ${v.campaignId === campania?.campaignId ? 'bg-brand-soft text-brand-dark' : 'bg-gray-100 text-muted'} px-2 py-0.5 text-xs">${v.campaignId === campania?.campaignId ? t('paciente.esta') : t('paciente.otraCampania')}</span></div>
      ${v.servicios.length ? html`<ul class="mt-2 flex flex-col gap-1">${v.servicios.map((s) => servicioHtml(s, { t, campaniaId: campania?.campaignId }))}</ul>` : html`<p class="mt-2 text-sm text-muted">${t('paciente.sinServicios')}</p>`}
      ${v.campaignId === campania?.campaignId ? accionesVisita({ id: v.visitId, paciente: patientId }, { t, puedeCapturar }) : ''}
    </li>`)}
  </ol>
  ${!lista.length && !locales.length ? emptyHtml(t('paciente.sinVisitas'), t('paciente.sinVisitasTexto')) : ''}
  ${visits?.serviciosSinVisita?.length ? html`<h3 class="mb-1 mt-4 font-bold">${t('paciente.sinVisita')}</h3><ul class="flex flex-col gap-1">${visits.serviciosSinVisita.map((s) => servicioHtml(s, { t, campaniaId: campania?.campaignId }))}</ul>` : ''}`;
}

export function pendientesHtml(records, t) {
  const otros = (records ?? []).filter((r) => !r.localId);
  if (!otros.length) return '';
  return html`<section class="mt-4 rounded-xl border border-dashed border-warn bg-amber-50 p-3"><h3 class="font-bold text-warn">${t('paciente.pendientes')}</h3>
    <ul class="mt-1 text-sm">${otros.map((r) => html`<li>${r.etiqueta || r.ruta} · ${t('envios.estado.' + r.estado)}</li>`)}</ul></section>`;
}

export function detalleHtml(tipo, payload, t) {
  const def = formFor(tipo);
  const rows = def ? describePayload(def, payload) : [];
  return html`${def?.provisional ? html`<p class="mb-2 rounded bg-amber-50 p-2 text-xs text-warn">${t('detalle.provisional')}</p>` : ''}
    ${rows.length ? html`<dl class="grid gap-2 sm:grid-cols-2">${rows.map((r) => html`<div><dt class="text-xs text-muted">${r.etiqueta}</dt><dd class="whitespace-pre-wrap font-medium">${r.valor}</dd></div>`)}</dl>` : html`<p class="text-muted">${t('detalle.vacio')}</p>`}`;
}

const EDIT_FIELDS = ['nombres', 'apellidos', 'fechaNacimiento', 'entidadNacimiento', 'documento.tipo', 'documento.numero', 'contacto.telefono', 'contacto.celular', 'contacto.localidad', 'contacto.residencia'];
export function edicionHtml(flat, t) {
  return html`<form id="form-edicion" class="flex flex-col gap-3 p-5" novalidate>
    <h2 class="text-lg font-bold">${t('edicion.titulo')}</h2><p class="text-sm text-muted">${t('edicion.texto')}</p>
    <div class="grid gap-3 sm:grid-cols-2">${EDIT_FIELDS.map((k) => html`<label class="flex flex-col gap-1 text-sm font-medium">${t('campo.' + k)}
      <input name="${k}" value="${flat[k] ?? ''}" ${k === 'fechaNacimiento' ? html`placeholder="AAAA-MM-DD"` : ''} class="touch rounded-lg border border-line px-3 font-normal"></label>`)}</div>
    <p id="edicion-error" role="alert" class="hidden text-sm text-danger"></p>
    <div id="conflicto"></div>
    <div class="flex justify-end gap-2"><button type="button" data-action="cancelar-edicion" class="touch rounded-lg border border-line px-4">${t('edicion.cancelar')}</button>
      <button type="submit" class="touch rounded-lg bg-brand px-4 font-semibold text-white">${t('edicion.guardar')}</button></div>
  </form>`;
}
export const EDIT_KEYS = EDIT_FIELDS;

export function conflictoHtml(rows, t) {
  return html`<section class="rounded-lg border border-warn/40 bg-amber-50 p-3"><h3 class="font-bold text-warn">${t('conflicto.titulo')}</h3><p class="mb-2 text-sm">${t('conflicto.texto')}</p>
    <table class="w-full text-left text-sm"><thead><tr><th class="py-1">${t('conflicto.campo')}</th><th>${t('conflicto.actual')}</th><th>${t('conflicto.tuyo')}</th><th>${t('conflicto.por')}</th></tr></thead>
      <tbody>${rows.map((r) => html`<tr class="border-t border-line"><td class="py-1">${t('campo.' + r.campo)}</td><td class="font-semibold">${r.actual || '—'}</td><td>${r.enviado || '—'}</td><td>${r.por}</td></tr>`)}</tbody></table>
    <div class="mt-3 flex flex-wrap gap-2"><button type="button" data-action="usar-actual" class="touch rounded-lg border border-line bg-surface px-3 text-sm">${t('conflicto.usarActual')}</button>
      <button type="button" data-action="mantener" class="touch rounded-lg bg-brand px-3 text-sm font-semibold text-white">${t('conflicto.mantener')}</button></div></section>`;
}
