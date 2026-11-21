import { html } from '../domain/html.js';
import { lights } from '../domain/campaigns.js';
import { icon } from './icon.js';
const TONO = { danger: 'bg-red-100 text-danger', warn: 'bg-amber-100 text-warn' };
/** Luces de señales: encendidas con color y número, apagadas en gris. `t` traduce las etiquetas. */
export function signalLightsHtml(senales, t) {
  return html`<ul class="flex flex-wrap gap-1.5" aria-label="${t('senales.titulo')}">${lights(senales).map((l) => html`
    <li class="inline-flex items-center gap-1 rounded-full px-2 py-1 text-xs font-semibold ${l.on ? TONO[l.tono] : 'bg-gray-100 text-gray-400'}" title="${t('senales.' + l.clave)}">
      ${icon(l.icono, 'h-4 w-4')}<span>${l.n}</span><span class="sr-only">${t('senales.' + l.clave)}${l.on ? '' : ' (' + t('senales.ninguna') + ')'}</span>
    </li>`)}</ul>`;
}
