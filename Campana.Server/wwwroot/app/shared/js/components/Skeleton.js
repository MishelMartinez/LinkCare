import { html } from '../domain/html.js';
export const skeletonCards = (n = 3) => html`<div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-busy="true" aria-live="polite">${Array.from({ length: n }, () => html`<div class="skeleton h-32"></div>`)}</div>`;
export const skeletonColumns = () => html`<div class="grid gap-3 md:grid-cols-2 xl:grid-cols-4" aria-busy="true">${[1, 2, 3, 4].map(() => html`<div class="skeleton h-64"></div>`)}</div>`;
