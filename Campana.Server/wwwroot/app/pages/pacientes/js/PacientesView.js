import { pageHtml, resultadosHtml, registroHtml, candidatasHtml } from './render.js';
import { skeletonCards } from '../../../shared/js/components/Skeleton.js';
import { errorHtml } from '../../../shared/js/components/EmptyState.js';

export class PacientesView {
  constructor(root, t) { this.root = root; this.t = t; this.h = {}; }
  on(ev, cb) { this.h[ev] = cb; }
  frame(state) {
    this.root.innerHTML = String(pageHtml({ t: this.t, ...state }));
    this.res = this.root.querySelector('#resultados');
    this.root.querySelector('#busqueda').addEventListener('submit', (e) => { e.preventDefault(); this.h.buscar?.(new FormData(e.target).get('q')); });
    this.root.querySelector('[data-action=registrar]')?.addEventListener('click', () => this.h.abrirRegistro?.());
    this.res.addEventListener('click', (e) => { if (e.target.closest('[data-action=retry]')) this.h.retry?.(); });
  }
  buscando() { this.res.innerHTML = String(skeletonCards(4)); }
  resultados(list, hayTexto) { this.res.innerHTML = String(resultadosHtml(list, this.t, hayTexto)); }
  falla(msg) { this.res.innerHTML = String(errorHtml(msg, this.t('app.reintentar'))); }
  errorBusqueda(msg) { const p = this.root.querySelector('#busqueda-error'); p.textContent = msg; p.classList.toggle('hidden', !msg); }

  abrirRegistro() {
    const host = this.root.querySelector('#registro');
    host.innerHTML = String(registroHtml(this.t));
    this.dlg = host.querySelector('dialog'); this.form = host.querySelector('form');
    this.form.addEventListener('submit', (e) => { e.preventDefault(); this.h.enviarRegistro?.(this.valores(), {}); });
    this.form.addEventListener('click', (e) => {
      const b = e.target.closest('[data-action]'); if (!b) return;
      if (b.dataset.action === 'cancelar-registro') this.cerrarRegistro();
      if (b.dataset.action === 'misma') this.h.enviarRegistro?.(this.valores(), { usarPacienteId: b.dataset.id });
      if (b.dataset.action === 'otra') this.h.enviarRegistro?.(this.valores(), { confirmarNueva: true });
    });
    this.dlg.addEventListener('cancel', (e) => { e.preventDefault(); this.cerrarRegistro(); });
    this.dlg.showModal(); this.form.elements.nombres.focus();
  }
  cerrarRegistro() { this.dlg?.close(); this.dlg?.remove(); this.dlg = null; }
  valores() {
    const f = this.form.elements; const v = (n) => String(f[n].value).trim();
    return { nombres: v('nombres'), apellidos: v('apellidos'), fechaNacimiento: v('fechaNacimiento'), entidadNacimiento: v('entidadNacimiento'),
      documentoTipo: v('documentoTipo'), documentoNumero: v('documentoNumero'), telefono: v('telefono'), celular: v('celular'), localidad: v('localidad') };
  }
  ocupadoRegistro(b) { const btn = this.form.querySelector('#registro-enviar'); btn.disabled = b; btn.textContent = this.t(b ? 'registro.guardando' : 'registro.guardar'); }
  errorRegistro(msg) { const p = this.form.querySelector('#registro-error'); p.textContent = msg; p.classList.toggle('hidden', !msg); }
  candidatas(list) { this.form.querySelector('#duplicadas').innerHTML = String(candidatasHtml(list, this.t)); }
  sinCandidatas() { this.form.querySelector('#duplicadas').innerHTML = ''; }
}
