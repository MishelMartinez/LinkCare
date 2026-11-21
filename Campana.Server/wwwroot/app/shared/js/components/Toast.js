/** Avisos breves. Usa textContent: nunca interpreta HTML. */
export function toast(host, mensaje, tipo = 'info', ms = 5000) {
  const el = document.createElement('div');
  el.setAttribute('role', tipo === 'error' ? 'alert' : 'status');
  el.className = `pointer-events-auto rounded-lg px-4 py-3 text-sm text-white shadow-lg ${tipo === 'error' ? 'bg-danger' : tipo === 'ok' ? 'bg-ok' : 'bg-ink'}`;
  el.textContent = mensaje;
  host.appendChild(el);
  setTimeout(() => el.remove(), ms);
  return el;
}
