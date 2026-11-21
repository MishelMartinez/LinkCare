import { html } from '../domain/html.js';
import { icon } from './icon.js';

/** Texto del botón de la barra: pendientes y rechazados. Vacío si no hay nada. */
export function badgeText(counts, t) {
  const parts = [];
  if (counts.pendientes > 0) parts.push(t('envios.pendientes', { n: counts.pendientes }));
  if (counts.rechazados > 0) parts.push(t('envios.rechazados', { n: counts.rechazados }));
  return parts.join(' · ');
}

const detalle = (rec, t) => {
  const e = rec.ultimoError;
  if (!e) return '';
  if (rec.estado === 'rechazado') return e.detail || t('error.' + (['validation_error', 'campaign_state', 'campaign_closed', 'forbidden', 'not_found'].includes(e.code) ? e.code : 'generic'));
  return e.code === 'network' || e.status === 0 ? t('error.network') : '';
};

export function panelHtml(records, t) {
  if (!records.length) return html`<p class="py-6 text-center text-muted">${t('envios.vacio')}</p>`;
  return html`<ul class="flex flex-col gap-2">${records.map((r) => html`
    <li class="rounded-lg border ${r.estado === 'rechazado' ? 'border-danger/40 bg-red-50' : 'border-line'} p-3" data-id="${r.id}">
      <div class="flex items-center gap-2">
        <span class="font-semibold">${r.etiqueta || r.ruta}</span>
        <span class="ml-auto rounded-full bg-gray-100 px-2 py-0.5 text-xs">${t('envios.estado.' + r.estado)}</span>
      </div>
      ${detalle(r, t) ? html`<p class="mt-1 text-sm ${r.estado === 'rechazado' ? 'text-danger' : 'text-muted'}">${detalle(r, t)}</p>` : ''}
      ${r.estado === 'rechazado' ? html`<div class="mt-2 flex gap-2">
        <button type="button" data-action="retry" data-id="${r.id}" class="touch rounded-lg border border-line px-3 text-sm">${icon('refresh', 'h-4 w-4')} ${t('envios.reintentar')}</button>
        <button type="button" data-action="discard" data-id="${r.id}" class="touch rounded-lg border border-danger px-3 text-sm text-danger">${t('envios.descartar')}</button>
      </div>` : ''}
    </li>`)}</ul>`;
}

/** Panel con los envíos del equipo. Los rechazados se pueden reintentar o descartar; nunca se reintentan solos. */
export async function openOutboxPanel(service, t) {
  const d = document.createElement('dialog');
  d.className = 'w-[min(94vw,32rem)] max-h-[85vh] overflow-auto rounded-xl p-0 shadow-xl backdrop:bg-black/40';
  const box = document.createElement('div'); box.className = 'flex flex-col gap-3 p-5';
  const h = document.createElement('h2'); h.className = 'text-lg font-bold'; h.textContent = t('envios.titulo');
  const body = document.createElement('div');
  const close = document.createElement('button'); close.type = 'button'; close.className = 'touch self-end rounded-lg border border-line px-4'; close.textContent = t('envios.cerrar');
  box.append(h, body, close); d.append(box);
  const paint = async () => { body.innerHTML = String(panelHtml(await service.list(), t)); };
  body.addEventListener('click', async (e) => {
    const b = e.target.closest('[data-action]'); if (!b) return;
    if (b.dataset.action === 'retry') await service.retry(b.dataset.id);
    if (b.dataset.action === 'discard') await service.discard(b.dataset.id);
    await paint();
  });
  const off = service.on(() => paint());
  const done = () => { off(); d.close(); d.remove(); };
  close.addEventListener('click', done);
  d.addEventListener('cancel', (e) => { e.preventDefault(); done(); });
  document.body.appendChild(d); await paint(); d.showModal(); close.focus();
}
