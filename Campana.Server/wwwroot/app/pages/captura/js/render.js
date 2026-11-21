import { html } from '../../../shared/js/domain/html.js';
import { progress } from '../../../shared/js/domain/forms.js';

function control(field, value, error, t) {
  const id = `f-${field.clave.replace(/\./g, '-')}`;
  const aria = error ? html`aria-invalid="true" aria-describedby="${id}-err"` : '';
  const cls = `touch w-full rounded-lg border ${error ? 'border-danger' : 'border-line'} px-3 font-normal`;
  let input;
  if (field.tipo === 'area') input = html`<textarea name="${field.clave}" id="${id}" rows="3" ${aria} class="${cls} py-2">${value ?? ''}</textarea>`;
  else if (field.tipo === 'lista') input = html`<select name="${field.clave}" id="${id}" ${aria} class="${cls}"><option value="">${t('captura.elegir')}</option>${field.opciones.map((o) => html`<option value="${o.v}" ${String(value) === o.v ? html`selected` : ''}>${o.t}</option>`)}</select>`;
  else if (field.tipo === 'fecha') input = html`<input type="date" name="${field.clave}" id="${id}" value="${value ?? ''}" ${aria} class="${cls}">`;
  else if (field.tipo === 'numero') input = html`<input type="number" step="${field.entero ? '1' : 'any'}" inputmode="decimal" name="${field.clave}" id="${id}" value="${value ?? ''}" ${aria} class="${cls}">`;
  else input = html`<input type="text" name="${field.clave}" id="${id}" value="${value ?? ''}" ${aria} class="${cls}">`;
  return html`<div class="flex flex-col gap-1 ${field.tipo === 'area' ? 'sm:col-span-2' : ''}">
    <label for="${id}" class="text-sm font-medium">${field.etiqueta}${field.requerido ? html` <span class="text-danger" aria-hidden="true">*</span>` : ''}</label>
    ${input}${error ? html`<p id="${id}-err" class="text-sm text-danger">${error}</p>` : ''}</div>`;
}

export function formHtml(def, values, errors, { t, titulo, texto, extra }) {
  return html`
  <a href="#/paciente/${encodeURIComponent(extra.pacienteId)}" class="touch mb-2 inline-flex items-center gap-1 text-sm text-brand-dark">← ${t('captura.volver')}</a>
  <h1 class="text-2xl font-bold">${titulo}</h1>
  ${texto ? html`<p class="mt-1 text-sm text-muted">${texto}</p>` : ''}
  ${def.provisional ? html`<p class="mt-2 rounded-lg bg-amber-50 p-3 text-sm text-warn" role="note">${t('captura.provisional')}</p>` : ''}
  <form id="form-captura" class="mt-3 flex flex-col gap-4 pb-28" novalidate>
    ${extra.motivo ? html`<section class="rounded-xl border border-line bg-surface p-4"><label class="flex flex-col gap-1 text-sm font-medium">${t('captura.motivo')}<input name="__motivo" maxlength="2000" class="touch rounded-lg border border-line px-3 font-normal"></label></section>` : ''}
    ${def.secciones.map((s, i) => html`<section class="rounded-xl border border-line bg-surface p-4" id="sec-${s.id}">
      <h2 class="mb-3 flex items-center gap-2 font-bold"><span class="inline-flex h-6 w-6 items-center justify-center rounded-full bg-brand-soft text-xs text-brand-dark">${i + 1}</span>${s.titulo}<span class="ml-auto text-xs font-normal text-muted" data-sec-progress="${s.id}"></span></h2>
      <div class="grid gap-3 sm:grid-cols-2">${s.campos.map((c) => control(c, values[c.clave], errors[c.clave], t))}</div></section>`)}
    <p id="captura-error" role="alert" class="hidden rounded-lg bg-red-50 p-3 text-sm text-danger"></p>
    <div class="fixed inset-x-0 bottom-0 z-20 border-t border-line bg-surface p-3"><div class="mx-auto flex max-w-7xl items-center gap-3">
      <div class="flex-1" aria-live="polite"><p class="text-xs text-muted" id="avance-texto"></p>
        <div class="h-2 w-full overflow-hidden rounded bg-gray-200" role="progressbar" aria-label="${t('captura.avance')}" aria-valuemin="0" aria-valuemax="100"><div id="avance-barra" class="h-full bg-brand" data-width="0"></div></div></div>
      <button type="submit" id="captura-guardar" class="touch rounded-lg bg-brand px-6 font-semibold text-white">${t('captura.guardar')}</button></div></div>
  </form>`;
}
export const avanceTexto = (def, values, t) => { const p = progress(def, values); return { p, texto: t('captura.avanceTexto', { a: p.filled, b: p.total }) }; };
