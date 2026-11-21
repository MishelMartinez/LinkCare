import { messageKey } from '../../../shared/js/domain/apiErrors.js';
export class CampanaController {
  constructor({ app, view, model, id }) {
    this.app = app; this.view = view; this.model = model; this.id = id;
    view.on('retry', () => this.init());
    view.on('retryMedicos', () => this.loadMedicos());
    view.on('excel', () => this.excel());
    view.on('sync', () => this.sync());
  }
  t(k, v) { return this.app.get('i18n').t(k, v); }
  async init() {
    this.view.loading();
    try {
      const s = await this.model.get(this.id);
      if (!s) return this.view.notFound();
      const volver = this.app.get('auth').session().campanas.length !== 1 || this.app.get('auth').session().rol === 'coordinador-general';
      this.summary = s; this.view.header(s, volver);
    } catch (e) { return this.view.failed(this.t(e.code === 'network' || e.code === 'timeout' ? messageKey(e) : 'campana.errorCarga')); }
    await this.loadMedicos();
    try { this.view.tablero(await this.model.stats(this.id)); } catch { this.view.tableroFailed(this.t('tablero.error')); }
    await this.loadSync();
  }
  async loadSync(ocupado = false) {
    let rep = null; try { rep = await this.model.lastSync(this.id); } catch { /* sin informe */ }
    this.view.sincronizacion({ rep: this.rep ?? rep, estado: this.summary.campaign.estado, configurado: this.summary.fileShareConfigurado, ocupado });
  }
  async sync() {
    this.view.sincronizacion({ rep: this.rep ?? null, estado: this.summary.campaign.estado, configurado: true, ocupado: true });
    try { this.rep = await this.model.sync(this.id); this.view.sincronizacion({ rep: this.rep, estado: this.summary.campaign.estado, configurado: true, ocupado: false }); }
    catch (e) { this.app.get('toast')(this.t('sync.error'), 'error'); await this.loadSync(); }
  }
  async excel() {
    try { this.view.descargar(await this.model.excel(this.id), `${this.id}.xlsx`); } catch { this.app.get('toast')(this.t('tablero.excelError'), 'error'); }
  }
  async loadMedicos() {
    this.view.medicosLoading();
    try { this.view.medicos(await this.model.medicos(this.id)); }
    catch (e) { this.view.medicosFailed(this.t(messageKey(e))); }
  }
}
