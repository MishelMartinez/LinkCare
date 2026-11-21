export class MockPacienteModel {
  async #data() { return (await fetch('pages/paciente/data/mock.paciente.json')).json(); }
  async activeCampaign() { return (await (await fetch('pages/campanas/data/mock.summary.json')).json())[0].campaign; }
  async record() { return (await this.#data()).record; }
  async visits() { return (await this.#data()).visits; }
  async eventDoc() { return { payload: {} }; }
  async attachment() { throw new Error('Sin archivos en modo de demostración'); }
  async incorporate() { return { outcome: 'linkedExisting', patientId: 'pat-mock-0001', candidatas: [] }; }
}
