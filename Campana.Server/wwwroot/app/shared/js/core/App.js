// Localizador de servicios de la aplicación (una sola instancia).
export class App {
  #services = new Map();
  register(name, svc) { this.#services.set(name, svc); return svc; }
  get(name) {
    if (!this.#services.has(name)) throw new Error(`Servicio no registrado: ${name}`);
    return this.#services.get(name);
  }
}
