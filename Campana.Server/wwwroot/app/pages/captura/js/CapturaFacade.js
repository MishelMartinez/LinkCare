import { Config } from '../../../shared/js/core/Config.js';
import { CapturaView } from './CapturaView.js';
import { CapturaController } from './CapturaController.js';
import { createCapturaModel } from './models/Factory.js';
export default class CapturaFacade {
  constructor(app, outlet, params) { this.app = app; this.outlet = outlet; this.params = params; }
  async mount() {
    const i18n = this.app.get('i18n');
    await i18n.load('pages/captura/i18n', 'captura');
    const view = new CapturaView(this.outlet, (k, v) => i18n.t(k, v));
    const outbox = this.app.get('outbox'); await outbox.open();
    this.controller = new CapturaController({ app: this.app, view, model: createCapturaModel(Config.mode, this.app.get('api')), params: this.params, outbox });
    await this.controller.init();
  }
  destroy() { this.controller?.destroy(); }
}
