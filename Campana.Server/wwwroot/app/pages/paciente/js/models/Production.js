const enc = encodeURIComponent;
export class ProductionPacienteModel {
  constructor(api) { this.api = api; }
  async activeCampaign() { try { return await this.api.get('/campaigns/active'); } catch (e) { if (e.status === 404) return null; throw e; } }
  record(id) { return this.api.get(`/patients/${enc(id)}`); }
  visits(id) { return this.api.get(`/patients/${enc(id)}/visits`); }
  eventDoc(pid, eid) { return this.api.get(`/patients/${enc(pid)}/events/${enc(eid)}`); }
  attachment(cid, pid, aid) { return this.api.blob(`/campaigns/${enc(cid)}/patients/${enc(pid)}/attachments/${enc(aid)}`); }
  incorporate(cid, dto) { return this.api.post(`/campaigns/${enc(cid)}/patients`, dto); }
}
