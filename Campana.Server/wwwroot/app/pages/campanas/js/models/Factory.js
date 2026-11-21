import { MockCampanasModel } from './Mock.js';
import { ProductionCampanasModel } from './Production.js';
export const createCampanasModel = (mode, api) => (mode === 'mock' ? new MockCampanasModel() : new ProductionCampanasModel(api));
