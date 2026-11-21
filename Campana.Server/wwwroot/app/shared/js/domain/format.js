const pad = (n) => String(n).padStart(2, '0');
/** Iniciales (máximo dos) de un nombre o usuario. */
export function initials(name) {
  const parts = String(name ?? '').trim().split(/[\s._-]+/).filter(Boolean);
  if (!parts.length) return '?';
  return (parts[0][0] + (parts.length > 1 ? parts[1][0] : '')).toUpperCase();
}
export const formatDate = (iso) => {
  if (!iso) return '';
  const d = new Date(iso.length === 10 ? iso + 'T00:00:00' : iso);
  return Number.isNaN(d.getTime()) ? '' : `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()}`;
};
/** Texto relativo en español: "hace 5 min", "hace 2 h", "hace 3 d". */
export function timeAgo(iso, now = new Date()) {
  if (!iso) return null;
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return null;
  const s = Math.max(0, Math.round((now.getTime() - t) / 1000));
  if (s < 60) return 'hace un momento';
  if (s < 3600) return `hace ${Math.floor(s / 60)} min`;
  if (s < 86400) return `hace ${Math.floor(s / 3600)} h`;
  return `hace ${Math.floor(s / 86400)} d`;
}
export const plural = (n, uno, otros) => `${n} ${n === 1 ? uno : otros}`;
/** Porcentaje entero acotado a 0-100; 0 si no hay denominador. */
export const percent = (parte, total) => (!total || total <= 0 ? 0 : Math.max(0, Math.min(100, Math.round((parte / total) * 100))));
export const formatDateTime = (iso) => {
  if (!iso) return '';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '' : `${formatDate(iso)} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
};
