import { MockCampanaModel } from './Mock.js';
import { ProductionCampanaModel } from './Production.js';
export const createCampanaModel = (mode, api) => (mode === 'mock' ? new MockCampanaModel() : new ProductionCampanaModel(api));
