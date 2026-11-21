import { html } from '../domain/html.js';
import { icon } from './icon.js';
/** Marco de la app: barra superior, menú, estado de conexión y zona de avisos. Devuelve HTML seguro. */
export function shellHtml({ usuario, rol, menu, t, activo }) {
  return html`
  <a href="#contenido" class="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded focus:bg-white focus:p-2">${t('app.saltar')}</a>
  <header class="sticky top-0 z-30 border-b border-line bg-surface">
    <div class="mx-auto flex max-w-7xl items-center gap-3 px-4 py-2">
      <img src="assets/logo.svg" alt="" class="h-9 w-9">
      <span class="font-bold text-brand-dark">${t('app.nombre')}</span>
      <nav class="ml-2 flex gap-1" aria-label="${t('app.menu')}">${menu.map((m) => html`
        <a href="${m.ruta}" class="touch inline-flex items-center gap-2 rounded-lg px-3 text-sm ${activo === m.ruta ? 'bg-brand-soft font-semibold text-brand-dark' : 'text-muted hover:bg-gray-100'}">${icon(m.icono)}${t(m.clave)}</a>`)}</nav>
      <div class="ml-auto flex items-center gap-3">
        <button type="button" id="envios" data-action="outbox" class="touch hidden items-center gap-1 rounded-full bg-amber-100 px-3 text-xs font-semibold text-warn"></button>
        <span id="conexion" class="hidden items-center gap-1 rounded-full bg-amber-100 px-3 py-1 text-xs font-semibold text-warn" role="status"></span>
        <span class="hidden text-right text-xs leading-tight sm:block"><span class="block font-semibold">${usuario}</span><span class="text-muted">${t('rol.' + rol)}</span></span>
        <button type="button" data-action="logout" class="touch inline-flex items-center gap-1 rounded-lg border border-line px-3 text-sm">${icon('log-out')}<span class="hidden sm:inline">${t('app.salir')}</span></button>
      </div>
    </div>
  </header>
  <main id="contenido" tabindex="-1" class="mx-auto max-w-7xl p-4"></main>
  <div id="avisos" class="pointer-events-none fixed bottom-4 right-4 z-40 flex max-w-sm flex-col gap-2"></div>`;
}
