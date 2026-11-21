export class MockLoginModel {
  async login(username, password) {
    if (!password) { const e = new Error('invalido'); e.status = 401; e.code = 'unauthorized'; throw e; }
    const u = String(username).toLowerCase();
    const role = u.startsWith('cgen') ? 'coordinador-general' : u.startsWith('ccamp') ? 'coordinador-campana' : 'medico';
    return { token: 'mock-token', expiresAt: new Date(Date.now() + 8 * 3600e3).toISOString(), username, role,
      consultorioId: role === 'medico' ? 'c02' : null, campanas: role === 'coordinador-general' ? [] : ['camp-mock-001'] };
  }
}
