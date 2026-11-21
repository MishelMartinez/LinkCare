export class MockPacientesModel {
  async activeCampaign() { return (await (await fetch('pages/campanas/data/mock.summary.json')).json())[0].campaign; }
  async search(q) {
    const all = await (await fetch('pages/pacientes/data/mock.pacientes.json')).json();
    const n = String(q).toLowerCase();
    return all.filter((p) => p.nombre.toLowerCase().includes(n));
  }
  async register() { return { outcome: 'created', patientId: 'pat-mock-0001', candidatas: [] }; }
}
