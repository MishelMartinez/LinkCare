/** Confirmación accesible con <dialog>. Devuelve una promesa con true o false. Todo el texto entra por textContent. */
export function confirmDialog({ titulo, texto, aceptar, cancelar }) {
  return new Promise((resolve) => {
    const d = document.createElement('dialog');
    d.className = 'w-[min(92vw,26rem)] rounded-xl p-0 shadow-xl backdrop:bg-black/40';
    const box = document.createElement('div'); box.className = 'flex flex-col gap-4 p-6';
    const h = document.createElement('h2'); h.className = 'text-lg font-bold'; h.textContent = titulo;
    const p = document.createElement('p'); p.className = 'text-muted'; p.textContent = texto;
    const row = document.createElement('div'); row.className = 'flex justify-end gap-2';
    const no = document.createElement('button'); no.type = 'button'; no.className = 'touch rounded-lg border border-line px-4'; no.textContent = cancelar;
    const ok = document.createElement('button'); ok.type = 'button'; ok.className = 'touch rounded-lg bg-brand px-4 font-semibold text-white'; ok.textContent = aceptar;
    row.append(no, ok); box.append(h, p, row); d.append(box);
    const done = (v) => { d.close(); d.remove(); resolve(v); };
    ok.addEventListener('click', () => done(true));
    no.addEventListener('click', () => done(false));
    d.addEventListener('cancel', (e) => { e.preventDefault(); done(false); });
    document.body.appendChild(d); d.showModal(); no.focus();
  });
}
