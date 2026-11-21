import { App } from './shared/js/core/App.js';
import { Config } from './shared/js/core/Config.js';
import { Router } from './shared/js/core/Router.js';
import { Logger } from './shared/js/core/Logger.js';
import { AuthService } from './shared/js/services/AuthService.js';
import { ApiClient } from './shared/js/services/ApiClient.js';
import { I18nService } from './shared/js/services/I18nService.js';
import { ConnectionMonitor } from './shared/js/services/ConnectionMonitor.js';
import { shellHtml } from './shared/js/components/AppShell.js';
import { access } from './shared/js/domain/permissions.js';
import { toast } from './shared/js/components/Toast.js';
import { confirmDialog } from './shared/js/components/Modal.js';
import { OutboxService } from './shared/js/services/OutboxService.js';
import { openOutboxPanel, badgeText } from './shared/js/components/OutboxPanel.js';

const app = new App();
const root = document.getElementById('root');
const auth = app.register('auth', new AuthService());
const monitor = app.register('monitor', new ConnectionMonitor({ intervalMs: Config.healthIntervalMs }));
const api = app.register('api', new ApiClient({ auth, onResult: (ok) => monitor.report(ok) }));
const i18n = app.register('i18n', new I18nService(Config.language));
await i18n.load('shared/i18n', 'common');
const t = (k, v) => i18n.t(k, v);
app.register('toast', (msg, tipo = 'info') => toast(document.getElementById('avisos') ?? document.body, msg, tipo));
// En modo de demostración no hay servidor: los envíos de la cola se aceptan en el acto.
const outbox = app.register('outbox', new OutboxService({
  api: Config.mode === 'mock' ? { request: async () => ({ visitId: 'mock-' + crypto.randomUUID().slice(0, 8) }) } : api,
  auth, monitor,
}));

const routes = {
  login: { public: true, load: () => import('./pages/login/js/LoginFacade.js') },
  campanas: { load: () => import('./pages/campanas/js/CampanasFacade.js') },
  campana: { load: () => import('./pages/campana/js/CampanaFacade.js') },
  pacientes: { load: () => import('./pages/pacientes/js/PacientesFacade.js') },
  paciente: { load: () => import('./pages/paciente/js/PacienteFacade.js') },
  captura: { load: () => import('./pages/captura/js/CapturaFacade.js') },
};

const outletFor = (route, session) => {
  if (route.public) { root.replaceChildren(); return root; }
  const perm = access(session.rol, session.campanas);
  root.innerHTML = String(shellHtml({ usuario: session.usuario, rol: session.rol, menu: perm.menu, t, activo: location.hash.split('/').slice(0, 2).join('/') }));
  paintConnection(monitor.state);
  paintOutbox(outbox.counts);
  return root.querySelector('#contenido');
};
const paintConnection = (state) => {
  const el = document.getElementById('conexion');
  if (!el) return;
  const off = state !== 'conectado';
  el.classList.toggle('hidden', !off); el.classList.toggle('inline-flex', off);
  el.textContent = off ? t('conexion.' + state) : '';
};

const paintOutbox = (counts) => {
  const el = document.getElementById('envios');
  if (!el) return;
  const text = badgeText(counts, t);
  el.classList.toggle('hidden', !text); el.classList.toggle('inline-flex', !!text);
  el.textContent = text;
  el.classList.toggle('bg-red-100', counts.rechazados > 0); el.classList.toggle('text-danger', counts.rechazados > 0);
};
outbox.on(paintOutbox);

const router = app.register('router', new Router({ app, routes, outletFor }));
router.onDenied = () => { const a = document.getElementById('avisos'); if (a) toast(a, t('acceso.denegado'), 'error'); };
root.addEventListener('click', async (e) => {
  if (e.target.closest('[data-action="outbox"]')) return openOutboxPanel(outbox, t);
  if (!e.target.closest('[data-action="logout"]')) return;
  const { pendientes } = outbox.counts;
  if (pendientes > 0 && !(await confirmDialog({ titulo: t('envios.salirTitulo'), texto: t('envios.salirTexto', { n: pendientes }), aceptar: t('envios.salirSi'), cancelar: t('app.cancelar') }))) return;
  auth.end('cierre');
});
monitor.on(paintConnection);
auth.on((evento, datos) => {
  const a = document.getElementById('avisos');
  if (evento === 'cerrada') { router.go('#/login'); if (datos.motivo === '401') setTimeout(() => toast(document.getElementById('avisos') ?? document.body, t('error.unauthorized'), 'error'), 0); }
  if (evento === 'por-vencer' && a) toast(a, t('sesion.por_vencer'), 'error', 15000);
});
auth.schedule();
monitor.start();
outbox.start();
outbox.flush();
window.addEventListener('unhandledrejection', (e) => Logger.error('rechazo no controlado', String(e.reason?.message ?? e.reason)));
router.start();
