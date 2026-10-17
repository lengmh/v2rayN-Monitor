export interface TrafficDetails {
  key: string;
  total: string;
  upload?: string;
  download?: string;
  recovery?: string;
  note?: string;
  exact: string;
}
export interface GroupDetails { key: string; groups: string[]; note?: string }

/** One shared, non-modal detail surface for pointer, keyboard and touch input. */
export function createDetailPopover(showExact: (value: string) => void) {
  const popup = document.createElement('div');
  popup.id = 'detail-popover';
  popup.className = 'detail-popover';
  popup.setAttribute('role', 'dialog');
  popup.setAttribute('aria-label', '流量明细');
  popup.tabIndex = -1;
  popup.hidden = true;
  document.body.append(popup);
  let anchor: HTMLButtonElement | undefined;
  let details: TrafficDetails | GroupDetails | undefined;
  let pinned = false;
  let restoreAnchorFocus = false;
  let closeTimer: ReturnType<typeof setTimeout> | undefined;

  const cancelClose = (): void => { clearTimeout(closeTimer); };
  const close = (): void => {
    cancelClose();
    anchor?.setAttribute('aria-expanded', 'false');
    popup.hidden = true;
    pinned = false;
    restoreAnchorFocus = false;
    anchor = undefined;
    details = undefined;
  };
  const position = (): void => {
    if (popup.hidden || !anchor?.isConnected) return;
    const box = anchor.getBoundingClientRect();
    const width = popup.offsetWidth, height = popup.offsetHeight;
    popup.style.left = `${Math.max(12, Math.min(box.right - width, window.innerWidth - width - 12))}px`;
    const below = box.bottom + 9;
    const preferredTop = below + height < window.innerHeight - 12 ? below : box.top - height - 9;
    popup.style.top = `${Math.max(12, Math.min(preferredTop, window.innerHeight - height - 12))}px`;
  };
  const fill = (): void => {
    if (!details) return;
    popup.replaceChildren();
    const heading = document.createElement('h3'); heading.textContent = 'groups' in details ? '策略组' : '流量明细';
    popup.setAttribute('aria-label', heading.textContent);
    const actions = document.createElement('div'); actions.className = 'popover-actions';
    popup.append(heading);
    if ('groups' in details) {
      const groups = document.createElement('ul'); groups.className = 'node-group-list';
      for (const group of details.groups) {
        const item = document.createElement('li'); item.textContent = group; groups.append(item);
      }
      popup.append(groups);
      if (details.note) { const note = document.createElement('p'); note.textContent = details.note; popup.append(note); }
    } else {
      const list = document.createElement('dl');
      for (const [label, value] of [['合计', details.total], ['上传', details.upload ?? '—'], ['下载', details.download ?? '—'],
        ...(details.recovery ? [['其中恢复量', details.recovery]] : [])]) {
        const term = document.createElement('dt'); term.textContent = label;
        const amount = document.createElement('dd'); amount.textContent = value;
        if (label === '其中恢复量') { term.className = 'recovery-detail'; amount.className = 'recovery-detail'; }
        list.append(term, amount);
      }
      const note = document.createElement('p');
      note.textContent = details.note ?? (details.recovery ? '恢复量已包含在上传、下载及合计中，不额外相加。' :
        details.upload === undefined ? '数据源未提供上下行方向。' : '当前合计不含恢复量。');
      const exact = document.createElement('button'); exact.type = 'button'; exact.className = 'text-button'; exact.textContent = '查看精确值';
      exact.addEventListener('click', () => { const value = details && 'exact' in details ? details.exact : undefined; close(); if (value) showExact(value); });
      popup.append(list, note); actions.append(exact);
    }
    const dismiss = document.createElement('button'); dismiss.type = 'button'; dismiss.className = 'text-button muted'; dismiss.textContent = '关闭';
    dismiss.addEventListener('click', () => { anchor?.focus(); close(); });
    actions.append(dismiss);
    popup.append(actions);
  };
  const scheduleClose = (): void => {
    cancelClose();
    if (!pinned) closeTimer = setTimeout(() => {
      if (!popup.contains(document.activeElement) && document.activeElement !== anchor) close();
    }, 160);
  };
  popup.addEventListener('pointerenter', cancelClose);
  popup.addEventListener('pointerleave', scheduleClose);
  popup.addEventListener('focusout', scheduleClose);
  document.addEventListener('pointerdown', event => {
    if (!popup.hidden && !popup.contains(event.target as Node) && !anchor?.contains(event.target as Node)) close();
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && !popup.hidden) { anchor?.focus(); close(); }
  });
  window.addEventListener('resize', position);
  window.addEventListener('scroll', position, true);

  return {
    close,
    beforeReplace(container: HTMLElement): void {
      if (anchor && container.contains(anchor) && document.activeElement === anchor) restoreAnchorFocus = true;
    },
    connect(button: HTMLButtonElement, value: TrafficDetails | GroupDetails): void {
      button.setAttribute('aria-haspopup', 'dialog');
      button.setAttribute('aria-controls', popup.id);
      button.setAttribute('aria-expanded', 'false');
      const open = (): void => {
        cancelClose();
        if (anchor !== button) { anchor?.setAttribute('aria-expanded', 'false'); pinned = false; }
        anchor = button; details = value; fill(); popup.hidden = false;
        button.setAttribute('aria-expanded', 'true'); position();
      };
      button.addEventListener('pointerenter', open);
      button.addEventListener('pointerleave', scheduleClose);
      button.addEventListener('focus', open);
      button.addEventListener('blur', scheduleClose);
      button.addEventListener('click', () => { open(); pinned = true; popup.focus(); });
      // A collector tick may replace ranking rows. Keep the same open item current.
      if (!popup.hidden && details?.key === value.key) {
        cancelClose();
        const focusInside = popup.contains(document.activeElement);
        anchor = button; details = value; fill(); button.setAttribute('aria-expanded', 'true');
        requestAnimationFrame(() => {
          position();
          if (restoreAnchorFocus && button.isConnected) { restoreAnchorFocus = false; button.focus(); }
        });
        if (focusInside) popup.focus();
      }
    },
    finishRender(): void { if (!popup.hidden && !anchor?.isConnected) close(); }
  };
}
