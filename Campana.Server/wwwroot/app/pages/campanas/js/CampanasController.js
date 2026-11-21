import { filterCampaigns, sortCampaigns } from '../../../shared/js/domain/campaigns.js';
import { messageKey } from '../../../shared/js/domain/apiErrors.js';
import { confirmDialog } from '../../../shared/js/components/Modal.js';
import { toast } from '../../../shared/js/components/Toast.js';

export class CampanasController {
  constructor({ app, view, model }) {
    this.app = app; this.view = view; this.model = model;
    this.data = []; this.filtros = { texto: '', soloSenales: false }; this.busy = false;
    view.on('filtro', (f) => { Object.assign(this.filtros, f); this.paint(); });
    view.on('advance', (id, to) => this.advance(id, to));
    view.on('retry', () => this.load());
  }
  t(k, v) { return this.app.get('i18n').t(k, v); }
  async init() { this.view.frame(this.filtros.texto, this.filtros.soloSenales); await this.load(); }
  async load() {
    this.view.loading();
    try { this.data = await this.model.list(); this.paint(); }
    catch (e) { this.view.failed(this.t(e.code === 'network' || e.code === 'timeout' ? messageKey(e) : 'campanas.errorCarga')); }
  }
  paint() {
    if (!this.data.length) return this.view.empty(false);
    const list = sortCampaigns(filterCampaigns(this.data, this.filtros));
    if (!list.length) return this.view.empty(true);
    this.view.showBoard(list);
  }
  async advance(id, to) {
    if (this.busy) return;
    const s = this.data.find((x) => x.campaign.campaignId === id);
    if (!s) return;
    const etapa = this.t('etapa.' + to);
    const ok = await confirmDialog({
      titulo: this.t('campanas.confirmarTitulo'), texto: this.t('campanas.confirmarTexto', { lugar: s.campaign.lugar, etapa }),
      aceptar: this.t('campanas.confirmarSi'), cancelar: this.t('app.cancelar'),
    });
    if (!ok) return;
    this.busy = true;
    const avisos = document.getElementById('avisos');
    try {
      await this.model.advance(id, to);
      toast(avisos, this.t('campanas.avanzada', { etapa }), 'ok');
    } catch (e) {
      toast(avisos, this.t(messageKey(e)), 'error');
    } finally {
      this.busy = false;
      await this.load(); // la tarjeta solo se mueve con lo que confirma el servidor
    }
  }
}
