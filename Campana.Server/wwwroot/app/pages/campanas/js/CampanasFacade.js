import { Config } from '../../../shared/js/core/Config.js';
import { CampanasView } from './CampanasView.js';
import { CampanasController } from './CampanasController.js';
import { createCampanasModel } from './models/Factory.js';
export default class CampanasFacade {
  constructor(app, outlet) { this.app = app; this.outlet = outlet; }
  async mount() {
    const i18n = this.app.get('i18n');
    await i18n.load('pages/campanas/i18n', 'campanas');
    const role = this.app.get('auth').session().rol;
    const view = new CampanasView(this.outlet, (k, v) => i18n.t(k, v), role);
    this.controller = new CampanasController({ app: this.app, view, model: createCampanasModel(Config.mode, this.app.get('api')) });
    await this.controller.init();
  }
  destroy() {}
}
