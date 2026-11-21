import { messageKey } from '../../../shared/js/domain/apiErrors.js';
import { access } from '../../../shared/js/domain/permissions.js';
export class LoginController {
  constructor({ app, view, model }) {
    this.app = app; this.view = view; this.model = model;
    view.on('submit', (u, p) => this.submit(u, p));
  }
  async submit(username, password) {
    const t = (k) => this.app.get('i18n').t(k);
    this.view.error('');
    if (!username || !password) return this.view.error(t('login.requeridos'));
    this.view.busy(true);
    try {
      const res = await this.model.login(username, password);
      const s = this.app.get('auth').start(res);
      this.app.get('router').go(access(s.rol, s.campanas).home);
    } catch (e) {
      // 401 en el inicio de sesión significa credenciales incorrectas; no se da más detalle.
      this.view.error(t(e.status === 401 ? 'login.invalido' : messageKey(e)));
    } finally { this.view.busy(false); }
  }
}
