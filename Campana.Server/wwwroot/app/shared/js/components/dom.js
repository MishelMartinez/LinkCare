/** Aplica anchos dinámicos con CSSOM, porque la CSP de la app no permite estilos en línea. */
export function applyWidths(root) {
  root.querySelectorAll('[data-width]').forEach((el) => { el.style.width = `${Number(el.dataset.width) || 0}%`; });
}
