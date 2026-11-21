import { Config } from '../../../shared/js/core/Config.js';
import { LoginView } from './LoginView.js';
import { LoginController } from './LoginController.js';
import { createLoginModel } from './models/Factory.js';
export default class LoginFacade {
  constructor(app, outlet) { this.app = app; this.outlet = outlet; }
  async mount() {
    const i18n = this.app.get('i18n');
    await i18n.load('pages/login/i18n', 'login');
    const view = new LoginView(this.outlet, (k, v) => i18n.t(k, v), Config.mode === 'mock');
    view.render();
    this.controller = new LoginController({ app: this.app, view, model: createLoginModel(Config.mode, this.app.get('api')) });
  }
  destroy() {}
}
