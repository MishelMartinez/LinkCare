export class ProductionCampanasModel {
  constructor(api) { this.api = api; }
  list() { return this.api.get('/campaigns/summary'); }
  advance(id, estado) { return this.api.post(`/campaigns/${encodeURIComponent(id)}/transitions`, { estado }); }
}
