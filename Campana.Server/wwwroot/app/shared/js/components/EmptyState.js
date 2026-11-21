import { html } from '../domain/html.js';
import { icon } from './icon.js';
export const emptyHtml = (titulo, texto = '') => html`<div class="flex flex-col items-center gap-2 rounded-xl border border-dashed border-line bg-surface p-8 text-center text-muted">${icon('inbox', 'h-8 w-8')}<p class="font-semibold text-ink">${titulo}</p>${texto ? html`<p class="text-sm">${texto}</p>` : ''}</div>`;
export const errorHtml = (mensaje, reintentarTxt) => html`<div role="alert" class="flex flex-col items-center gap-3 rounded-xl border border-danger/30 bg-red-50 p-6 text-center text-danger">${icon('alert', 'h-8 w-8')}<p>${mensaje}</p><button type="button" data-action="retry" class="touch rounded-lg bg-danger px-4 text-white">${reintentarTxt}</button></div>`;
