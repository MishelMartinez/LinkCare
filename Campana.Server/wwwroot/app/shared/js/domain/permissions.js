// Ayuda de uso para la interfaz. La seguridad real está en el servidor.
const MENU = {
  campanas: { ruta: '#/campanas', clave: 'nav.campanas', icono: 'flag' },
  pacientes: { ruta: '#/pacientes', clave: 'nav.pacientes', icono: 'users' },
};
const CAMPANAS = ['campanas', 'campana'];
const PACIENTES = ['pacientes', 'paciente'];

/** Devuelve la ruta de inicio, el menú y las rutas permitidas del perfil. */
export function access(role, campanas = []) {
  if (role === 'coordinador-general') {
    return { home: '#/campanas', menu: [MENU.campanas, MENU.pacientes], rutas: [...CAMPANAS, ...PACIENTES] };
  }
  if (role === 'coordinador-campana') {
    const home = campanas.length === 1 ? `#/campana/${encodeURIComponent(campanas[0])}` : '#/campanas';
    return { home, menu: [MENU.campanas, MENU.pacientes], rutas: [...CAMPANAS, ...PACIENTES] };
  }
  if (role === 'medico') {
    return { home: '#/pacientes', menu: [MENU.pacientes], rutas: [...PACIENTES, 'captura'] };
  }
  return { home: '#/login', menu: [], rutas: [] };
}
export const canOpen = (role, campanas, ruta) => access(role, campanas).rutas.includes(ruta);
