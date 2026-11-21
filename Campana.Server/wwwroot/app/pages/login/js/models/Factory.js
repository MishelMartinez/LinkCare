import { MockLoginModel } from './Mock.js';
import { ProductionLoginModel } from './Production.js';
export const createLoginModel = (mode, api) => (mode === 'mock' ? new MockLoginModel() : new ProductionLoginModel(api));
