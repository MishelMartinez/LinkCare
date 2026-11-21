import { Config } from '../../../shared/js/core/Config.js';
import { CampanaView } from './CampanaView.js';
import { CampanaController } from './CampanaController.js';
import { createCampanaModel } from './models/Factory.js';
export default class CampanaFacade {
  constructor(app, outlet, params) { this.app = app; this.outlet = outlet; this.id = params[0]; }
  async mount() {
    const i18n = this.app.get('i18n');
    await i18n.load('pages/campana/i18n', 'campana');
    const view = new CampanaView(this.outlet, (k, v) => i18n.t(k, v));
    this.controller = new CampanaController({ app: this.app, view, model: createCampanaModel(Config.mode, this.app.get('api')), id: this.id });
    await this.controller.init();
  }
  destroy() {}
}
