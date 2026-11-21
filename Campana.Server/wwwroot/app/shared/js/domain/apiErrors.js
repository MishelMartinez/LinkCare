export class ApiError extends Error {
  constructor(status, code, detail, errors = null, extra = null) {
    super(detail || code || `Error ${status}`);
    this.name = 'ApiError';
    this.status = status; this.code = code; this.detail = detail; this.errors = errors; this.extra = extra;
  }
}
/** Convierte la respuesta de error (ProblemDetails con "code") en un ApiError. */
export function fromProblem(status, body) {
  const b = body && typeof body === 'object' ? body : {};
  const { type, title, status: _s, detail, code, errors, traceId, ...extra } = b;
  return new ApiError(status, code || codeForStatus(status), detail || title || null, errors ?? null, Object.keys(extra).length ? extra : null);
}
export function codeForStatus(status) {
  if (status === 0) return 'network';
  if (status === 401) return 'unauthorized';
  if (status === 403) return 'forbidden';
  if (status === 404) return 'not_found';
  if (status === 429) return 'too_many_requests';
  return status >= 500 ? 'internal_error' : 'error';
}
/** Clave de traducción para mostrar al usuario. Nunca se muestra el detalle técnico del servidor tal cual. */
export function messageKey(err) {
  const code = err?.code;
  const known = ['campaign_state', 'campaign_closed', 'campaign_conflict', 'unauthorized', 'forbidden', 'not_found',
    'too_many_requests', 'network', 'timeout', 'validation_error', 'internal_error'];
  return `error.${known.includes(code) ? code : 'generic'}`;
}
