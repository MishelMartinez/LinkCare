import { MockPacienteModel } from './Mock.js';
import { ProductionPacienteModel } from './Production.js';
export const createPacienteModel = (mode, api) => (mode === 'mock' ? new MockPacienteModel() : new ProductionPacienteModel(api));
