// Configuración de la app. "mock" no necesita servidor; "production" usa la API real del mismo origen.
const params = new URLSearchParams(globalThis.location?.search ?? '');
export const Config = {
  mode: params.get('mode') === 'mock' ? 'mock' : 'production',
  apiBase: '',
  language: 'es',
  requestTimeoutMs: 15000,
  healthIntervalMs: 10000,
};
