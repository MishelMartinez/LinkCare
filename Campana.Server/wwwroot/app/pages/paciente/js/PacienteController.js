import { messageKey } from '../../../shared/js/domain/apiErrors.js';
import { canWrite, identityFlat, identityChanges, conflictRows, registeredIn, pendingFor } from '../../../shared/js/domain/patients.js';
import { isCapturable } from '../../../shared/js/domain/patients.js';

// Último expediente leído de cada paciente, solo en memoria: permite seguir capturando si se cae la red a media consulta.
const cache = new Map();
const TIPOS_ARCHIVO = ['image/jpeg', 'image/png', 'application/pdf'];
const MAX_LOCAL = 25 * 1024 * 1024; // límite local de cortesía; el servidor decide el real (413)

export class PacienteController {
  constructor({ app, view, model, id, outbox }) {
    this.app = app; this.view = view; this.model = model; this.id = id; this.outbox = outbox;
    this.session = app.get('auth').session(); this.state = null; this.cambiosPendientes = null;
    view.on('retry', () => this.init());
    view.on('abrirVisita', () => this.abrirVisita());
    view.on('archivo', (visit, file) => this.adjuntar(visit, file));
    view.on('editar', () => this.editar());
    view.on('incorporar', () => this.incorporar());
    view.on('ver', (d) => this.ver(d));
    view.on('usarActual', () => this.usarActual());
    view.on('mantener', () => this.mantener());
    app.get('i18n'); this.off = outbox?.on(() => this.refrescarPendientes());
  }
  t(k, v) { return this.app.get('i18n').t(k, v); }
  toast(msg, tipo) { this.app.get('toast')(msg, tipo); }
  get monitor() { return this.app.get('monitor'); }

  async init() {
    this.view.loading();
    try {
      const [record, visits, campania] = await Promise.all([this.model.record(this.id), this.model.visits(this.id), this.model.activeCampaign().catch(() => null)]);
      this.state = { record, visits, campania };
      cache.set(this.id, this.state);
    } catch (e) {
      if (e.status === 404) return this.view.notFound();
      if ((e.code === 'network' || e.code === 'timeout') && cache.has(this.id)) {
        this.state = cache.get(this.id);
        this.toast(this.t('paciente.datosGuardados'), 'info');
        return this.paint();
      }
      return this.view.failed(this.t(e.code === 'network' || e.code === 'timeout' ? messageKey(e) : 'paciente.errorCarga'));
    }
    await this.paint();
  }
  async paint() {
    const { record, visits, campania } = this.state;
    const medico = this.session.rol === 'medico';
    const escribe = canWrite(this.session.rol, campania);
    const incorporada = campania ? registeredIn(record, campania.campaignId) : false;
    const pendientes = medico && this.outbox ? pendingFor(await this.outbox.list(), this.id) : [];
    this.view.render({
      record, visits, campania, pendientes,
      puedeEditar: escribe && incorporada,
      faltaIncorporar: escribe && !incorporada,
      puedeCapturar: escribe && incorporada ? campania.campaignId : '',
      puedeAbrirVisita: escribe && incorporada,
    });
  }
  async refrescarPendientes() { if (this.state) { try { this.state.visits = await this.model.visits(this.id); } catch { /* sin red: se conserva lo último */ } await this.paint(); } }

  async abrirVisita() {
    const { campania } = this.state;
    const localId = `local:${crypto.randomUUID()}`;
    const requestId = crypto.randomUUID();
    const r = await this.outbox.submit({
      clientRequestId: requestId, metodo: 'POST', ruta: `/campaigns/${encodeURIComponent(campania.campaignId)}/patients/${encodeURIComponent(this.id)}/visits`,
      cuerpo: { clientRequestId: requestId, consultorioId: this.session.consultorio }, localId, etiqueta: this.t('paciente.visita'),
    }).catch(() => null);
    if (!r) return this.toast(this.t('envios.sinSesion'), 'error');
    this.resultado(r);
    await this.refrescarPendientes();
  }
  resultado(r) {
    if (r.estado === 'enviado') this.toast(this.t('envios.enviado'), 'ok');
    else if (r.estado === 'pendiente') this.toast(this.t('envios.guardadoLocal'), 'info');
    else this.toast(r.error?.detail || this.t(messageKey(r.error ?? {})), 'error');
  }

  async adjuntar(visit, file) {
    if (!TIPOS_ARCHIVO.includes(file.type) || file.size > MAX_LOCAL) return this.toast(this.t('paciente.archivoTipo'), 'error');
    const { campania } = this.state;
    const requestId = crypto.randomUUID();
    const r = await this.outbox.submit({
      clientRequestId: requestId, metodo: 'POST', ruta: `/campaigns/${encodeURIComponent(campania.campaignId)}/patients/${encodeURIComponent(this.id)}/attachments`,
      campos: { clientRequestId: requestId, consultorioId: this.session.consultorio, visitId: await this.outbox.visitRef(visit) },
      archivo: file, nombreArchivo: file.name, etiqueta: `${this.t('tipo.adjunto')}: ${file.name}`,
    });
    this.resultado(r);
    await this.refrescarPendientes();
  }

  async ver({ event, tipo, campana }) {
    try {
      const doc = await this.model.eventDoc(this.id, event);
      if (tipo === 'adjunto') {
        const blob = await this.model.attachment(campana, this.id, doc.payload.attachmentId);
        this.view.adjunto(blob, doc.payload.nombreOriginal ?? this.t('tipo.adjunto'));
      } else {
        this.eventoAbierto = { id: event, tipo, campana, payload: doc.payload };
        const puedeCorregir = canWrite(this.session.rol, this.state.campania) && isCapturable(tipo) && campana === this.state.campania?.campaignId;
        this.view.detalle(tipo, doc.payload, { puedeCorregir });
        this.view.on('corregir', () => this.app.get('router').go(`#/captura/${encodeURIComponent(campana)}/${encodeURIComponent(this.id)}/correccion/${encodeURIComponent(event)}`));
      }
    } catch (e) { this.toast(this.t(tipo === 'adjunto' ? 'adjunto.error' : messageKey(e)), 'error'); }
  }

  async incorporar() {
    if (this.monitor.state !== 'conectado') return this.toast(this.t('paciente.sinRed'), 'error');
    const d = this.state.record.identity;
    const dto = { nombres: d.nombres, apellidos: d.apellidos, fechaNacimiento: d.fechaNacimiento, usarPacienteId: this.id,
      ...(d.documento?.numero ? { documento: d.documento } : {}), ...(d.contacto ? { contacto: d.contacto } : {}), ...(d.entidadNacimiento ? { entidadNacimiento: d.entidadNacimiento } : {}) };
    try { await this.model.incorporate(this.state.campania.campaignId, dto); this.toast(this.t('paciente.incorporada'), 'ok'); await this.init(); }
    catch (e) { this.toast(e.detail || this.t(messageKey(e)), 'error'); }
  }

  // ------------------------------------------------------------------ cambio parcial de datos de identidad
  editar() {
    this.base = { flat: identityFlat(this.state.record.identity), version: this.state.record.identityVersion };
    this.view.edicion(this.base.flat, (vals) => this.guardarCambios(vals));
  }
  async guardarCambios(vals) {
    const cambios = identityChanges(this.base.flat, vals);
    if (!Object.keys(cambios).length) return this.view.errorEdicion(this.t('edicion.sinCambios'));
    if ((vals.nombres ?? '').trim() === '' || (vals.apellidos ?? '').trim() === '' || (vals.fechaNacimiento ?? '').trim() === '') return this.view.errorEdicion(this.t('edicion.obligatorio'));
    this.view.errorEdicion('');
    this.cambiosPendientes = cambios;
    await this.enviarCambios(cambios, this.base.version);
  }
  async enviarCambios(cambios, baseVersion) {
    const { campania } = this.state;
    const requestId = crypto.randomUUID();
    const r = await this.outbox.submit({
      clientRequestId: requestId, metodo: 'PATCH', ruta: `/campaigns/${encodeURIComponent(campania.campaignId)}/patients/${encodeURIComponent(this.id)}/identity`,
      cuerpo: { clientRequestId: requestId, consultorioId: this.session.consultorio, baseVersion, cambios }, etiqueta: this.t('tipo.actualizacion-datos'),
    });
    if (r.estado === 'rechazado') {
      if (r.error?.code === 'identity_conflict') return this.view.conflicto(conflictRows(r.error.errors));
      return this.view.errorEdicion(r.error?.detail || this.t(messageKey(r.error ?? {})));
    }
    this.view.cerrarEdicion(); this.resultado(r);
    await this.init();
  }
  async usarActual() { this.view.cerrarEdicion(); await this.init(); }
  /** Mantener mi cambio: se vuelve a leer la versión actual y se reenvía lo mismo con ella. */
  async mantener() {
    try {
      const fresh = await this.model.record(this.id);
      this.state.record = fresh;
      await this.enviarCambios(this.cambiosPendientes, fresh.identityVersion);
    } catch (e) { this.view.errorEdicion(this.t(messageKey(e))); }
  }
  destroy() { this.off?.(); this.view.closeAll(); }
}
