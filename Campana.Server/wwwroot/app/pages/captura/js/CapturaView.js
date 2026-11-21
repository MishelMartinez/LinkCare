import { formHtml, avanceTexto } from './render.js';
import { skeletonCards } from '../../../shared/js/components/Skeleton.js';
import { errorHtml } from '../../../shared/js/components/EmptyState.js';
import { applyWidths } from '../../../shared/js/components/dom.js';

export class CapturaView {
  constructor(root, t) { this.root = root; this.t = t; this.h = {}; }
  on(ev, cb) { this.h[ev] = cb; }
  loading() { this.root.innerHTML = String(skeletonCards(2)); }
  failed(msg) { this.root.innerHTML = String(errorHtml(msg, this.t('app.reintentar'))); this.root.querySelector('[data-action=retry]').addEventListener('click', () => this.h.retry?.()); }
  render(def, values, errors, opts) {
    this.def = def;
    this.root.innerHTML = String(formHtml(def, values, errors, { t: this.t, ...opts }));
    this.form = this.root.querySelector('#form-captura');
    this.form.addEventListener('submit', (e) => { e.preventDefault(); this.h.guardar?.(this.values()); });
    this.form.addEventListener('input', () => this.avance());
    this.avance();
  }
  values() {
    const out = {};
    for (const el of this.form.elements) if (el.name && el.name !== '__motivo') out[el.name] = el.value;
    out.__motivo = this.form.elements.__motivo?.value ?? '';
    return out;
  }
  avance() {
    const { p, texto } = avanceTexto(this.def, this.values(), this.t);
    this.root.querySelector('#avance-texto').textContent = texto;
    const bar = this.root.querySelector('#avance-barra'); bar.dataset.width = String(p.percent); applyWidths(this.root);
    this.root.querySelector('[role=progressbar]').setAttribute('aria-valuenow', String(p.percent));
    for (const s of p.sections) { const el = this.root.querySelector(`[data-sec-progress="${s.id}"]`); if (el) el.textContent = `${s.filled}/${s.total}`; }
  }
  ocupado(b) { const btn = this.form.querySelector('#captura-guardar'); btn.disabled = b; btn.textContent = this.t(b ? 'captura.guardando' : 'captura.guardar'); }
  mensaje(msg) { const p = this.form.querySelector('#captura-error'); p.textContent = msg; p.classList.toggle('hidden', !msg); }
  /** Muestra los errores junto a cada campo sin perder lo escrito y lleva el foco al primero. */
  errores(byField) {
    this.form.querySelectorAll('[aria-invalid]').forEach((el) => { el.removeAttribute('aria-invalid'); el.classList.replace('border-danger', 'border-line'); });
    this.form.querySelectorAll('[data-err]').forEach((el) => el.remove());
    let first = null;
    for (const [clave, msg] of Object.entries(byField)) {
      const el = this.form.elements[clave]; if (!el) continue;
      el.setAttribute('aria-invalid', 'true'); el.classList.replace('border-line', 'border-danger');
      const p = document.createElement('p'); p.dataset.err = '1'; p.className = 'text-sm text-danger'; p.textContent = msg;
      el.insertAdjacentElement('afterend', p); first ??= el;
    }
    first?.focus();
  }
}
