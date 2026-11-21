import { access } from '../domain/permissions.js';

/**
 * Rutas con "#/". Cada ruta tiene un nombre de acceso y una función que carga su Facade (carga diferida).
 * Aplica las reglas de acceso del perfil (ayuda de uso, no seguridad).
 */
export class Router {
  constructor({ app, routes, outletFor, notFound = '#/login' }) {
    this.app = app; this.routes = routes; this.outletFor = outletFor; this.notFound = notFound;
    this.current = null; this.token = 0;
    this.onChange = () => {};
    this.onDenied = () => {};
  }
  start() {
    window.addEventListener('hashchange', () => this.resolve());
    return this.resolve();
  }
  go(hash) { if (location.hash === hash) return this.resolve(); location.hash = hash; return Promise.resolve(); }
  parse(hash) {
    const [name, ...rest] = String(hash || '').replace(/^#\/?/, '').split('/');
    return { name: name || '', params: rest.map((p) => { try { return decodeURIComponent(p); } catch { return p; } }) };
  }
  async resolve() {
    const my = ++this.token;
    const auth = this.app.get('auth');
    const { name, params } = this.parse(location.hash);
    const route = this.routes[name];
    const session = auth.session();
    if (!route) return this.go(session ? access(session.rol, session.campanas).home : this.notFound);
    if (route.public && session && name === 'login') return this.go(access(session.rol, session.campanas).home);
    if (!route.public) {
      if (!session) return this.go('#/login');
      const perm = access(session.rol, session.campanas);
      if (!perm.rutas.includes(name)) { this.onDenied(name); return this.go(perm.home); }
    }
    await this.current?.destroy?.();
    this.current = null;
    const mod = await route.load();
    if (my !== this.token) return; // llegó otra navegación mientras cargaba
    const outlet = this.outletFor(route, session);
    outlet.replaceChildren();
    const facade = new mod.default(this.app, outlet, params);
    this.current = facade;
    this.onChange(name, session);
    await facade.mount();
  }
}
