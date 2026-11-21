import { messageKey } from '../../../shared/js/domain/apiErrors.js';
import { canWrite, validSearch } from '../../../shared/js/domain/patients.js';

/** DTO del servidor a partir de lo escrito en el formulario. Los datos vacíos no se envían. */
export function registroDto(v, extra = {}) {
  const dto = { nombres: v.nombres, apellidos: v.apellidos, fechaNacimiento: v.fechaNacimiento, ...extra };
  if (v.entidadNacimiento) dto.entidadNacimiento = v.entidadNacimiento;
  if (v.documentoNumero) dto.documento = { tipo: v.documentoTipo || null, numero: v.documentoNumero };
  if (v.telefono || v.celular || v.localidad) dto.contacto = { telefono: v.telefono || null, celular: v.celular || null, localidad: v.localidad || null };
  return dto;
}

export class PacientesController {
  constructor({ app, view, model, role }) {
    this.app = app; this.view = view; this.model = model; this.role = role; this.campania = null; this.q = '';
    view.on('buscar', (q) => this.buscar(q));
    view.on('retry', () => this.buscar(this.q));
    view.on('abrirRegistro', () => this.abrirRegistro());
    view.on('enviarRegistro', (v, extra) => this.registrar(v, extra));
  }
  t(k, v) { return this.app.get('i18n').t(k, v); }
  async init() {
    let sinRed = false;
    try { this.campania = await this.model.activeCampaign(); } catch (e) { this.campania = null; sinRed = e.code === 'network' || e.code === 'timeout'; }
    const puede = canWrite(this.role, this.campania);
    let aviso = '';
    if (sinRed) aviso = this.t('pacientes.sinRed');
    else if (this.role !== 'medico') aviso = this.t('pacientes.soloLectura');
    else if (this.campania && !puede) aviso = this.t('pacientes.noConsulta', { etapa: this.t('etapa.' + this.campania.estado) });
    this.view.frame({ campania: this.campania, puedeEscribir: puede, aviso, texto: '' });
    this.view.resultados([], false);
  }
  async buscar(q) {
    this.q = String(q ?? '').trim();
    if (!validSearch(this.q)) { this.view.errorBusqueda(this.t('pacientes.minimo')); return; }
    this.view.errorBusqueda(''); this.view.buscando();
    try { this.view.resultados(await this.model.search(this.q), true); }
    catch (e) { this.view.falla(this.t(messageKey(e))); }
  }
  abrirRegistro() {
    if (this.app.get('monitor').state !== 'conectado') { this.app.get('toast')(this.t('pacientes.sinRed'), 'error'); return; }
    this.view.abrirRegistro();
  }
  async registrar(v, extra) {
    this.view.errorRegistro('');
    if (!v.nombres || !v.apellidos || !v.fechaNacimiento) return this.view.errorRegistro(this.t('registro.requeridos'));
    if (!/^\d{4}-\d{2}-\d{2}$/.test(v.fechaNacimiento)) return this.view.errorRegistro(this.t('registro.fechaMal'));
    this.view.ocupadoRegistro(true);
    try {
      // El alta no entra en la cola: necesita comparar con el índice del servidor y que una persona resuelva las coincidencias.
      const res = await this.model.register(this.campania.campaignId, registroDto(v, extra));
      this.view.cerrarRegistro();
      this.app.get('router').go(`#/paciente/${encodeURIComponent(res.patientId)}`);
    } catch (e) {
      if (e.code === 'duplicate_candidates') this.view.candidatas(e.extra?.candidatas ?? []);
      else { this.view.sinCandidatas(); this.view.errorRegistro(e.status === 400 && e.detail ? e.detail : this.t(messageKey(e))); }
    } finally { this.view.ocupadoRegistro(false); }
  }
}
