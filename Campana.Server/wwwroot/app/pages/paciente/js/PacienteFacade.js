import { Config } from '../../../shared/js/core/Config.js';
import { PacienteView } from './PacienteView.js';
import { PacienteController } from './PacienteController.js';
import { createPacienteModel } from './models/Factory.js';
export default class PacienteFacade {
  constructor(app, outlet, params) { this.app = app; this.outlet = outlet; this.id = params[0]; }
  async mount() {
    const i18n = this.app.get('i18n');
    await i18n.load('pages/paciente/i18n', 'paciente');
    const view = new PacienteView(this.outlet, (k, v) => i18n.t(k, v));
    const outbox = this.app.get('auth').session().rol === 'medico' ? this.app.get('outbox') : null;
    await outbox?.open();
    this.controller = new PacienteController({ app: this.app, view, model: createPacienteModel(Config.mode, this.app.get('api')), id: this.id, outbox });
    await this.controller.init();
  }
  destroy() { this.controller?.destroy(); }
}
