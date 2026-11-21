export class ProductionCampanaModel {
  constructor(api) { this.api = api; }
  // El resumen ya trae las cifras y las luces; se filtra la campaña pedida (el servidor respeta el alcance del usuario).
  async get(id) { return (await this.api.get('/campaigns/summary')).find((x) => x.campaign.campaignId === id) ?? null; }
  stats(id) { return this.api.get(`/campaigns/${encodeURIComponent(id)}/stats`); }
  excel(id) { return this.api.blob(`/campaigns/${encodeURIComponent(id)}/export.xlsx`); }
  async lastSync(id) { try { return await this.api.get(`/campaigns/${encodeURIComponent(id)}/sync`); } catch (e) { if (e.status === 404) return null; throw e; } }
  sync(id) { return this.api.post(`/campaigns/${encodeURIComponent(id)}/sync`, {}); }
  medicos(id) { return this.api.get(`/campaigns/${encodeURIComponent(id)}/medicos`); }
}
