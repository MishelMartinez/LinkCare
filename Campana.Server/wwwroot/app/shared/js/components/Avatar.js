import { html } from '../domain/html.js';
import { initials } from '../domain/format.js';
export const avatarHtml = (name) => html`<span class="inline-flex h-10 w-10 flex-none items-center justify-center rounded-full bg-brand-soft text-sm font-bold text-brand-dark" aria-hidden="true">${initials(name)}</span>`;
