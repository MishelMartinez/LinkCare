import { messageKey } from '../../../shared/js/domain/apiErrors.js';
import { formFor, buildPayload, validate, mapServerErrors, valuesFromPayload, diffPayload, today } from '../../../shared/js/domain/forms.js';

export class CapturaController {
  constructor({ app, view, model, params, outbox }) {
    this.app = app; this.view = view; this.model = model; this.outbox = outbox;
    [this.campaignId, this.pacienteId, this.tipo, this.ref] = params;
    this.session = app.get('auth').session();
    view.on('guardar', (v) => this.guardar(v));
    view.on('retry', () => this.init());
  }
  t(k, v) { return this.app.get('i18n').t(k, v); }
  async init() {
    this.view.loading();
    this.correccion = this.tipo === 'correccion';
    try {
      if (this.correccion) {
        const doc = await this.model.eventDoc(this.pacienteId, this.ref);
        this.def = formFor(doc.tipo); this.original = doc.payload; this.tipoOriginal = doc.tipo;
        this.values = valuesFromPayload(this.def, doc.payload);
      } else {
        this.def = formFor(this.tipo);
        this.values = { fechaAtencion: today() };
      }
    } catch (e) { return this.view.failed(this.t(e.code === 'network' || e.code === 'timeout' ? messageKey(e) : 'captura.errorCarga')); }
    if (!this.def) return this.view.failed(this.t('captura.noDisponible'));
    this.view.render(this.def, this.values, {}, {
      titulo: this.correccion ? `${this.t('captura.corregirTitulo')} · ${this.def.titulo}` : this.def.titulo,
      texto: this.correccion ? this.t('captura.corregirTexto') : '', extra: { pacienteId: this.pacienteId, motivo: this.correccion },
    });
  }
  async guardar(vals) {
    this.view.mensaje('');
    const errores = validate(this.def, vals);
    if (Object.keys(errores).length) { this.view.errores(errores); return this.view.mensaje(this.t('captura.errores')); }
    this.view.errores({});
    let payload;
    if (this.correccion) {
      const datos = diffPayload(this.def, this.original, vals);
      if (!Object.keys(datos).length) return this.view.mensaje(this.t('captura.sinCambios'));
      payload = { correctsEventId: this.ref, ...(vals.__motivo?.trim() ? { motivo: vals.__motivo.trim() } : {}), datos };
    } else payload = buildPayload(this.def, vals);

    const requestId = crypto.randomUUID();
    const sobre = {
      clientRequestId: requestId, tipo: this.correccion ? 'correccion' : this.tipo, tipoSchemaVersion: this.correccion ? 1 : this.def.version,
      consultorioId: this.session.consultorio, payload,
    };
    if (!this.correccion) {
      if (!this.ref) return this.view.mensaje(this.t('captura.necesitaVisita'));
      sobre.visitId = await this.outbox.visitRef(this.ref);
    }
    this.view.ocupado(true);
    try {
      const r = await this.outbox.submit({
        clientRequestId: requestId, metodo: 'POST', ruta: `/campaigns/${encodeURIComponent(this.campaignId)}/patients/${encodeURIComponent(this.pacienteId)}/events`,
        cuerpo: sobre, etiqueta: `${this.def.titulo}${this.correccion ? ' (' + this.t('tipo.correccion').toLowerCase() + ')' : ''}`,
      });
      if (r.estado === 'rechazado') return this.rechazo(r.error);
      this.app.get('toast')(this.t(r.estado === 'pendiente' ? 'envios.guardadoLocal' : 'captura.guardadoOk'), r.estado === 'pendiente' ? 'info' : 'ok');
      this.app.get('router').go(`#/paciente/${encodeURIComponent(this.pacienteId)}`);
    } finally { this.view.ocupado(false); }
  }
  rechazo(err) {
    const { byField, general } = mapServerErrors(this.def, err?.errors);
    this.view.errores(byField);
    const texto = [err?.detail && !general.length && !Object.keys(byField).length ? err.detail : '', ...general].filter(Boolean).join(' · ');
    this.view.mensaje(texto ? `${this.t('captura.erroresServidor')} ${texto}` : this.t(messageKey(err ?? {})));
  }
  destroy() {}
}
