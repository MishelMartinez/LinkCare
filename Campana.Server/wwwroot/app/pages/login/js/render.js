import { html } from '../../../shared/js/domain/html.js';
import { icon } from '../../../shared/js/components/icon.js';
export function loginHtml({ t, mock }) {
  return html`
  <div class="mx-auto mt-12 max-w-sm rounded-2xl border border-line bg-surface p-6 shadow-sm">
    <img src="assets/logo.svg" alt="" class="mx-auto h-14 w-14">
    <h1 class="mt-3 text-center text-xl font-bold">${t('login.titulo')}</h1>
    <p class="mb-4 text-center text-sm text-muted">${t('login.subtitulo')}</p>
    <form id="login-form" class="flex flex-col gap-3" novalidate>
      <label class="flex flex-col gap-1 text-sm font-medium">${t('login.usuario')}
        <input name="username" autocomplete="username" autocapitalize="none" required class="touch rounded-lg border border-line px-3 font-normal">
      </label>
      <label class="flex flex-col gap-1 text-sm font-medium">${t('login.contrasena')}
        <input name="password" type="password" autocomplete="current-password" required class="touch rounded-lg border border-line px-3 font-normal">
      </label>
      <p id="login-error" role="alert" class="hidden text-sm text-danger"></p>
      <button type="submit" class="touch inline-flex items-center justify-center gap-2 rounded-lg bg-brand px-4 font-semibold text-white">${icon('lock')}<span id="login-label">${t('login.entrar')}</span></button>
    </form>
    ${mock ? html`<p class="mt-4 rounded bg-amber-50 p-2 text-xs text-warn">${t('login.mock')}</p>` : ''}
  </div>`;
}
