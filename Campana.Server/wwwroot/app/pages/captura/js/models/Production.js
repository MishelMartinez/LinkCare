export class ProductionCapturaModel {
  constructor(api) { this.api = api; }
  eventDoc(pid, eid) { return this.api.get(`/patients/${encodeURIComponent(pid)}/events/${encodeURIComponent(eid)}`); }
}
