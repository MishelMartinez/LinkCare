import { MockCapturaModel } from './Mock.js';
import { ProductionCapturaModel } from './Production.js';
export const createCapturaModel = (mode, api) => (mode === 'mock' ? new MockCapturaModel() : new ProductionCapturaModel(api));
