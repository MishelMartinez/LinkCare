import { loginHtml } from './render.js';
export class LoginView {
  constructor(root, t, mock) { this.root = root; this.t = t; this.mock = mock; this.handlers = {}; }
  on(ev, cb) { this.handlers[ev] = cb; }
  render() {
    this.root.innerHTML = String(loginHtml({ t: this.t, mock: this.mock }));
    this.form = this.root.querySelector('#login-form');
    this.form.addEventListener('submit', (e) => {
      e.preventDefault();
      const d = new FormData(this.form);
      this.handlers.submit?.(String(d.get('username') ?? '').trim(), String(d.get('password') ?? ''));
    });
    this.form.elements.username.focus();
  }
  busy(b) {
    this.form.querySelector('button').disabled = b;
    this.root.querySelector('#login-label').textContent = this.t(b ? 'login.entrando' : 'login.entrar');
  }
  error(msg) { const p = this.root.querySelector('#login-error'); p.textContent = msg; p.classList.toggle('hidden', !msg); }
}
