import { ApiError, fromProblem } from '../domain/apiErrors.js';
import { Config } from '../core/Config.js';

/** Envoltura de fetch: token, errores uniformes, tiempo límite y aviso de 401. */
export class ApiClient {
  constructor({ auth, fetchFn = (...a) => fetch(...a), base = Config.apiBase, timeoutMs = Config.requestTimeoutMs, onResult = () => {} }) {
    this.auth = auth; this.fetchFn = fetchFn; this.base = base; this.timeoutMs = timeoutMs; this.onResult = onResult;
  }
  async request(method, path, { body, headers = {}, anonymous = false, as = 'json' } = {}) {
    const h = { Accept: 'application/json', ...headers };
    const token = anonymous ? null : this.auth.token();
    if (token) h.Authorization = `Bearer ${token}`;
    let payload;
    if (body instanceof FormData) payload = body; // el navegador pone el límite (boundary) del multipart
    else if (body !== undefined) { h['Content-Type'] = 'application/json'; payload = JSON.stringify(body); }
    const ctl = new AbortController();
    const timer = setTimeout(() => ctl.abort(), this.timeoutMs);
    let res;
    try {
      res = await this.fetchFn(this.base + path, { method, headers: h, body: payload, signal: ctl.signal });
    } catch (e) {
      this.onResult(false);
      throw new ApiError(0, e?.name === 'AbortError' ? 'timeout' : 'network', null);
    } finally { clearTimeout(timer); }
    this.onResult(true);
    if (res.ok) {
      if (res.status === 204) return null;
      if (as === 'blob') return res.blob();
      const type = res.headers?.get?.('content-type') ?? '';
      return type.includes('json') ? res.json() : res;
    }
    let data = null;
    try { data = await res.json(); } catch { /* sin cuerpo JSON */ }
    const err = fromProblem(res.status, data);
    if (res.status === 401 && token) this.auth.end('401');
    throw err;
  }
  get(path, opts) { return this.request('GET', path, opts); }
  post(path, body, opts) { return this.request('POST', path, { ...opts, body }); }
  patch(path, body, opts) { return this.request('PATCH', path, { ...opts, body }); }
  blob(path) { return this.request('GET', path, { as: 'blob' }); }
}
