import { pageHtml, kanbanHtml, vacioHtml } from './render.js';
import { skeletonColumns } from '../../../shared/js/components/Skeleton.js';
import { errorHtml } from '../../../shared/js/components/EmptyState.js';
import { applyWidths } from '../../../shared/js/components/dom.js';
import { isNextOf } from '../../../shared/js/domain/stages.js';

export class CampanasView {
  constructor(root, t, role) { this.root = root; this.t = t; this.role = role; this.h = {}; this.dragId = null; this.dragFrom = null; }
  on(ev, cb) { this.h[ev] = cb; }
  frame(texto, soloSenales) {
    this.root.innerHTML = String(pageHtml({ t: this.t, role: this.role, texto, soloSenales }));
    this.board = this.root.querySelector('#tablero');
    this.root.querySelector('#filtro-texto').addEventListener('input', (e) => this.h.filtro?.({ texto: e.target.value }));
    this.root.querySelector('#filtro-senales').addEventListener('change', (e) => this.h.filtro?.({ soloSenales: e.target.checked }));
    this.board.addEventListener('click', (e) => {
      const b = e.target.closest('[data-action]');
      if (!b) return;
      if (b.dataset.action === 'advance') this.h.advance?.(b.dataset.id, b.dataset.to);
      if (b.dataset.action === 'retry') this.h.retry?.();
    });
    this.board.addEventListener('dragstart', (e) => {
      const card = e.target.closest('.kanban-card[draggable="true"]');
      if (!card) return;
      this.dragId = card.dataset.id; this.dragFrom = card.dataset.stage;
      e.dataTransfer.setData('text/plain', this.dragId); e.dataTransfer.effectAllowed = 'move';
      card.classList.add('dragging');
      this.board.querySelectorAll('.kanban-col').forEach((c) => c.classList.toggle('drop-ok', isNextOf(this.dragFrom, c.dataset.stage)));
    });
    this.board.addEventListener('dragend', () => this.endDrag());
    this.board.addEventListener('dragover', (e) => {
      const col = e.target.closest('.kanban-col');
      if (col && this.dragId && isNextOf(this.dragFrom, col.dataset.stage)) e.preventDefault();
    });
    this.board.addEventListener('drop', (e) => {
      const col = e.target.closest('.kanban-col');
      if (!col || !this.dragId || !isNextOf(this.dragFrom, col.dataset.stage)) return;
      e.preventDefault();
      const id = this.dragId; this.endDrag();
      this.h.advance?.(id, col.dataset.stage);
    });
  }
  endDrag() {
    this.dragId = null; this.dragFrom = null;
    this.board.querySelectorAll('.drop-ok,.dragging').forEach((c) => c.classList.remove('drop-ok', 'dragging'));
  }
  loading() { this.board.innerHTML = String(skeletonColumns()); }
  failed(msg) { this.board.innerHTML = String(errorHtml(msg, this.t('app.reintentar'))); }
  empty(hayFiltro) { this.board.innerHTML = String(vacioHtml(this.t, hayFiltro)); }
  showBoard(list) {
    this.board.innerHTML = String(kanbanHtml(list, { role: this.role, t: this.t }));
    applyWidths(this.board);
  }
}
