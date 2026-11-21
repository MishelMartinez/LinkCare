import { MockPacientesModel } from './Mock.js';
import { ProductionPacientesModel } from './Production.js';
export const createPacientesModel = (mode, api) => (mode === 'mock' ? new MockPacientesModel() : new ProductionPacientesModel(api));
