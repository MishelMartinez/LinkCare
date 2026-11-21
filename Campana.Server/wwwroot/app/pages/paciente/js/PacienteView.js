import { datosHtml, historialHtml, pendientesHtml, incorporarHtml, detalleHtml, edicionHtml, conflictoHtml } from './render.js';
import { skeletonCards } from '../../../shared/js/components/Skeleton.js';
import { errorHtml, emptyHtml } from '../../../shared/js/components/EmptyState.js';
import { html } from '../../../shared/js/domain/html.js';

export class PacienteView {
  constructor(root, t) {
    this.root = root; this.t = t; this.h = {}; this.urls = [];
    // Un solo oyente para toda la vida de la página: render() reemplaza el contenido, no el contenedor.
    root.addEventListener('click', (e) => {
      const b = e.target.closest('[data-action]'); if (!b) return;
      const a = b.dataset.action;
      if (a === 'abrir-visita') this.h.abrirVisita?.();
      if (a === 'editar') this.h.editar?.();
      if (a === 'incorporar') this.h.incorporar?.();
      if (a === 'ver') this.h.ver?.({ ...b.dataset });
      if (a === 'adjuntar') { this.visitForFile = b.dataset.visit; this.file.click(); }
    });
  }
  on(ev, cb) { this.h[ev] = cb; }
  loading() { this.root.innerHTML = String(skeletonCards(3)); }
  failed(msg) { this.root.innerHTML = String(errorHtml(msg, this.t('app.reintentar'))); this.root.querySelector('[data-action=retry]').addEventListener('click', () => this.h.retry?.()); }
  notFound() { this.root.innerHTML = String(emptyHtml(this.t('paciente.noEncontrada'))); }

  render(s) {
    const t = this.t;
    this.root.innerHTML = String(html`
      <a href="#/pacientes" class="touch mb-2 inline-flex items-center gap-1 text-sm text-brand-dark">← ${t('paciente.volver')}</a>
      ${datosHtml(s.record, { t, puedeEditar: s.puedeEditar })}
      ${s.faltaIncorporar ? incorporarHtml(t) : ''}
      ${pendientesHtml(s.pendientes, t)}
      ${historialHtml({ record: s.record, visits: s.visits, pendientes: s.pendientes, campania: s.campania, t, puedeCapturar: s.puedeCapturar, puedeAbrirVisita: s.puedeAbrirVisita })}
      <input id="archivo" type="file" accept="image/jpeg,image/png,application/pdf" class="hidden" tabindex="-1">`);
    this.file = this.root.querySelector('#archivo');
    this.file.addEventListener('change', () => { const f = this.file.files[0]; this.file.value = ''; if (f) this.h.archivo?.(this.visitForFile, f); });
  }

  #dialog(content, { ancho = '36rem' } = {}) {
    const d = document.createElement('dialog');
    d.className = 'max-h-[90vh] overflow-auto rounded-xl p-0 shadow-xl backdrop:bg-black/40';
    d.style.width = `min(94vw, ${ancho})`;
    d.append(content);
    d.addEventListener('cancel', (e) => { e.preventDefault(); this.#close(d); });
    document.body.appendChild(d); d.showModal();
    return d;
  }
  #close(d) { d.close(); d.remove(); this.urls.splice(0).forEach((u) => URL.revokeObjectURL(u)); }
  closeAll() { document.querySelectorAll('dialog[data-paciente]').forEach((d) => this.#close(d)); }

  detalle(tipo, payload, { puedeCorregir }) {
    const box = document.createElement('div'); box.className = 'flex flex-col gap-3 p-5';
    box.innerHTML = String(html`<h2 class="text-lg font-bold">${this.t('detalle.titulo')} · ${this.t('tipo.' + tipo)}</h2>${detalleHtml(tipo, payload, this.t)}
      <div class="flex justify-end gap-2">${puedeCorregir ? html`<button type="button" data-action="corregir" class="touch rounded-lg border border-brand px-4 text-brand-dark">${this.t('detalle.corregir')}</button>` : ''}
      <button type="button" data-action="cerrar" class="touch rounded-lg border border-line px-4">${this.t('detalle.cerrar')}</button></div>`);
    const d = this.#dialog(box); d.dataset.paciente = '1';
    box.addEventListener('click', (e) => { const a = e.target.closest('[data-action]')?.dataset.action; if (a === 'cerrar') this.#close(d); if (a === 'corregir') { this.#close(d); this.h.corregir?.(); } });
  }

  /** Muestra una imagen o PDF desde un Blob, sin descargarlo ni mostrar su ruta. */
  adjunto(blob, nombre) {
    const url = URL.createObjectURL(blob); this.urls.push(url);
    const box = document.createElement('div'); box.className = 'flex flex-col gap-3 p-3';
    const media = blob.type === 'application/pdf' ? document.createElement('iframe') : document.createElement('img');
    media.src = url; media.title = nombre; media.alt = nombre;
    media.className = blob.type === 'application/pdf' ? 'h-[70vh] w-full rounded' : 'max-h-[70vh] w-full rounded object-contain';
    const close = document.createElement('button'); close.type = 'button'; close.className = 'touch self-end rounded-lg border border-line px-4'; close.textContent = this.t('adjunto.cerrar');
    box.append(media, close);
    const d = this.#dialog(box, { ancho: '48rem' }); d.dataset.paciente = '1';
    close.addEventListener('click', () => this.#close(d));
  }

  edicion(flat, onSubmit, onCancel) {
    const wrap = document.createElement('div'); wrap.innerHTML = String(edicionHtml(flat, this.t));
    const d = this.#dialog(wrap); d.dataset.paciente = '1';
    this.editDialog = d; this.editForm = wrap.querySelector('form');
    this.editForm.addEventListener('submit', (e) => { e.preventDefault(); onSubmit(Object.fromEntries(new FormData(this.editForm))); });
    this.editForm.addEventListener('click', (e) => {
      const a = e.target.closest('[data-action]')?.dataset.action;
      if (a === 'cancelar-edicion') { this.cerrarEdicion(); onCancel?.(); }
      if (a === 'usar-actual') this.h.usarActual?.();
      if (a === 'mantener') this.h.mantener?.();
    });
    this.editForm.elements.nombres.focus();
  }
  cerrarEdicion() { if (this.editDialog) this.#close(this.editDialog); this.editDialog = null; }
  errorEdicion(msg) { const p = this.editForm.querySelector('#edicion-error'); p.textContent = msg; p.classList.toggle('hidden', !msg); }
  conflicto(rows) { this.editForm.querySelector('#conflicto').innerHTML = String(conflictoHtml(rows, this.t)); }
}
