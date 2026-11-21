import { nextStage } from '../../../../shared/js/domain/stages.js';
export class MockCampanasModel {
  async list() {
    if (!this.cache) { const r = await fetch('pages/campanas/data/mock.summary.json'); this.cache = await r.json(); }
    return structuredClone(this.cache);
  }
  async advance(id, estado) {
    await this.list();
    const s = this.cache.find((x) => x.campaign.campaignId === id);
    if (!s || nextStage(s.campaign.estado) !== estado) { const e = new Error('estado'); e.code = 'campaign_state'; e.status = 409; throw e; }
    s.campaign.estado = estado;
    return s.campaign;
  }
}
