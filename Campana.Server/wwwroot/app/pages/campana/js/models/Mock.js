export class MockCampanaModel {
  async #json(p) { return (await fetch(p)).json(); }
  async get(id) {
    const list = await this.#json('pages/campanas/data/mock.summary.json');
    return list.find((x) => x.campaign.campaignId === id) ?? null;
  }
  stats() { return this.#json('pages/campana/data/mock.stats.json'); }
  async excel() { return new Blob(['demo'], { type: 'application/octet-stream' }); }
  lastSync() { return this.#json('pages/campana/data/mock.sync.json'); }
  sync() { return this.#json('pages/campana/data/mock.sync.json'); }
  medicos() { return this.#json('pages/campana/data/mock.medicos.json'); }
}
