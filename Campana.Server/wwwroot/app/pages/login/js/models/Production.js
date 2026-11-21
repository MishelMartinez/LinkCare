export class ProductionLoginModel {
  constructor(api) { this.api = api; }
  login(username, password) { return this.api.post('/auth/login', { username, password }, { anonymous: true }); }
}
