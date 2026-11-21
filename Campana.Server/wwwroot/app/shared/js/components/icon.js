import { html } from '../domain/html.js';
/** Ícono del sprite local. El nombre sale siempre de código propio, no de datos. */
export const icon = (name, cls = '') => html`<svg class="icon ${cls}" aria-hidden="true" focusable="false"><use href="assets/icons.svg#i-${name}"></use></svg>`;
