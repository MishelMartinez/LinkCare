// Registro en consola. Nunca se registran tokens, contraseñas ni datos de pacientes.
const SECRET = /(token|password|contrasena|authorization)/i;
const clean = (v) => (v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, SECRET.test(k) ? '***' : x])) : v);
export const Logger = {
  info: (...a) => console.info('[app]', ...a.map(clean)),
  warn: (...a) => console.warn('[app]', ...a.map(clean)),
  error: (...a) => console.error('[app]', ...a.map(clean)),
};
