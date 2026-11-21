import { Config } from '../../../shared/js/core/Config.js';
import { PacientesView } from './PacientesView.js';
import { PacientesController } from './PacientesController.js';
import { createPacientesModel } from './models/Factory.js';
export default class PacientesFacade {
  constructor(app, outlet) { this.app = app; this.outlet = outlet; }
  async mount() {
    const i18n = this.app.get('i18n');
    await i18n.load('pages/pacientes/i18n', 'pacientes');
    const view = new PacientesView(this.outlet, (k, v) => i18n.t(k, v));
    this.controller = new PacientesController({ app: this.app, view, model: createPacientesModel(Config.mode, this.app.get('api')), role: this.app.get('auth').session().rol });
    await this.controller.init();
  }
  destroy() { this.controller?.view.cerrarRegistro(); }
}
