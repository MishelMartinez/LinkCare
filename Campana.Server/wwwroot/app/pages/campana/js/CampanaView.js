import { headerHtml, medicosHtml, tableroHtml, syncHtml } from './render.js';
import { skeletonCards } from '../../../shared/js/components/Skeleton.js';
import { applyWidths } from '../../../shared/js/components/dom.js';
import { errorHtml, emptyHtml } from '../../../shared/js/components/EmptyState.js';
export class CampanaView {
  constructor(root, t) { this.root = root; this.t = t; this.h = {}; }
  on(ev, cb) { this.h[ev] = cb; }
  loading() { this.root.innerHTML = String(skeletonCards(3)); }
  failed(msg) {
    this.root.innerHTML = String(errorHtml(msg, this.t('app.reintentar')));
    this.root.querySelector('[data-action="retry"]').addEventListener('click', () => this.h.retry?.());
  }
  notFound() { this.root.innerHTML = String(emptyHtml(this.t('campana.noExiste'))); }
  header(summary, volver) { this.root.innerHTML = String(headerHtml(summary, { t: this.t, volver })); this.medicosEl = this.root.querySelector('#medicos'); this.root.insertAdjacentHTML('beforeend', '<div id="tablero" class="mt-5"></div>'); this.tableroEl = this.root.querySelector('#tablero'); this.syncEl = null; this.tableroEl.addEventListener('click', (e) => { if (e.target.closest('[data-action=excel]')) this.h.excel?.(); }); }
  sincronizacion(state) { if (!this.syncEl) { this.root.insertAdjacentHTML('beforeend', '<div id="sync" class="mt-6 pb-8"></div>'); this.syncEl = this.root.querySelector('#sync'); this.syncEl.addEventListener('click', (e) => { if (e.target.closest('[data-action=sync]')) this.h.sync?.(); }); } this.syncEl.innerHTML = String(syncHtml(state, this.t)); }
  tablero(st) { this.tableroEl.innerHTML = String(tableroHtml(st, this.t)); applyWidths(this.tableroEl); }
  tableroFailed(msg) { this.tableroEl.textContent = msg; }
  descargar(blob, nombre) { const url = URL.createObjectURL(blob); const a = document.createElement('a'); a.href = url; a.download = nombre; document.body.appendChild(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(url), 1000); }
  medicosLoading() { this.medicosEl.innerHTML = String(skeletonCards(3)); }
  medicos(list) { this.medicosEl.innerHTML = String(medicosHtml(list, this.t)); }
  medicosFailed(msg) {
    this.medicosEl.innerHTML = String(errorHtml(msg, this.t('app.reintentar')));
    this.medicosEl.querySelector('[data-action="retry"]').addEventListener('click', () => this.h.retryMedicos?.());
  }
}
