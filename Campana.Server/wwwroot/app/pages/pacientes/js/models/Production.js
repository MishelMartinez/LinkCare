export class ProductionPacientesModel {
  constructor(api) { this.api = api; }
  async activeCampaign() {
    try { return await this.api.get('/campaigns/active'); }
    catch (e) { if (e.status === 404) return null; throw e; }
  }
  search(q) { return this.api.get(`/patients?q=${encodeURIComponent(q)}&limit=50`); }
  register(campaignId, dto) { return this.api.post(`/campaigns/${encodeURIComponent(campaignId)}/patients`, dto); }
}
