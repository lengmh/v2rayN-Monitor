/** Small, interruptible UI-only transitions. Rendering and data ordering stay synchronous. */
export function createUiMotion() {
  const active = new Set<() => void>();
  const media = window.matchMedia?.('(prefers-reduced-motion: reduce)');
  const enabled = (): boolean => !media?.matches && typeof HTMLElement.prototype.animate === 'function';
  const cancel = (): void => { for (const cleanup of [...active]) cleanup(); };
  const track = (animations: Animation[], finish: () => void = () => {}): void => {
    let done = false;
    const cleanup = (): void => {
      if (done) return;
      done = true; active.delete(cleanup);
      for (const animation of animations) animation.cancel();
      finish();
    };
    active.add(cleanup);
    void Promise.all(animations.map(animation => animation.finished)).then(cleanup, cleanup);
  };
  media?.addEventListener('change', cancel);
  window.addEventListener('resize', cancel);
  // A fixed visual snapshot must never linger at an obsolete scroll position.
  window.addEventListener('scroll', event => {
    // Restoring a snapshot's scroll offsets can itself emit scroll events.
    if (event.target instanceof HTMLElement && event.target.closest('.motion-snapshot')) return;
    cancel();
  }, true);
  const options = { duration: 220, easing: 'cubic-bezier(.2,.7,.2,1)', fill: 'both' as FillMode };
  return {
    cancel,
    crossfade(target: HTMLElement, render: () => void): void {
      cancel();
      if (!enabled() || !target.getClientRects().length) { render(); return; }
      const before = target.getBoundingClientRect();
      const old = target.cloneNode(true) as HTMLElement;
      if (target.closest('.comparison-tables')) old.classList.add('motion-comparison-context');
      const sourceNodes = [target, ...target.querySelectorAll<HTMLElement>('*')];
      const oldNodes = [old, ...old.querySelectorAll<HTMLElement>('*')];
      const saved = sourceNodes.map(node => ({ top: node.scrollTop, left: node.scrollLeft,
        value: (node as HTMLInputElement).value, checked: (node as HTMLInputElement).checked }));
      // The old surface is visual only: no duplicate IDs, focus targets, or AT content.
      for (const node of [old, ...old.querySelectorAll<HTMLElement>('*')]) {
        node.removeAttribute('id'); node.removeAttribute('autofocus'); node.removeAttribute('name');
      }
      old.classList.add('motion-snapshot'); old.setAttribute('aria-hidden', 'true'); old.inert = true;
      Object.assign(old.style, { position: 'fixed', left: `${before.left}px`, top: `${before.top}px`,
        width: `${before.width}px`, height: `${before.height}px`, margin: '0', pointerEvents: 'none', overflow: 'hidden', zIndex: '30' });
      try { render(); } catch (error) { old.remove(); throw error; }
      document.body.append(old);
      oldNodes.forEach((node, index) => {
        const source = sourceNodes[index]!;
        node.scrollTop = saved[index]!.top; node.scrollLeft = saved[index]!.left;
        if (source.tagName === 'SELECT') (node as HTMLSelectElement).value = saved[index]!.value;
        if (source.tagName === 'INPUT') {
          (node as HTMLInputElement).value = saved[index]!.value;
          (node as HTMLInputElement).checked = saved[index]!.checked;
        }
      });
      const after = target.getBoundingClientRect();
      const frames: Keyframe[] = [{ opacity: 0, transform: 'translateY(4px)' }, { opacity: 1, transform: 'translateY(0)' }];
      if (Math.abs(after.height - before.height) > 1) {
        frames[0]!.height = `${before.height}px`; frames[1]!.height = `${after.height}px`;
        frames[0]!.overflow = frames[1]!.overflow = 'hidden';
      }
      track([old.animate([{ opacity: 1, transform: 'translateY(0)' }, { opacity: 0, transform: 'translateY(-4px)' }], options),
        target.animate(frames, options)], () => old.remove());
    },
    reorder(target: HTMLElement, render: () => void): void {
      cancel();
      const before = new Map<string, number>();
      if (enabled()) for (const row of target.querySelectorAll<HTMLElement>('[data-motion-key]')) {
        before.set(row.dataset.motionKey!, row.getBoundingClientRect().top);
      }
      render();
      const animations: Animation[] = [];
      for (const row of target.querySelectorAll<HTMLElement>('[data-motion-key]')) {
        const top = before.get(row.dataset.motionKey!);
        if (top === undefined) continue;
        const dy = top - row.getBoundingClientRect().top;
        if (Math.abs(dy) > .5) animations.push(row.animate([{ transform: `translateY(${dy}px)` }, { transform: 'translateY(0)' }], options));
      }
      if (animations.length) track(animations);
    },
    disclose(target: HTMLElement, control: HTMLElement, expanded: boolean): void {
      const from = target.getBoundingClientRect().height;
      const opacity = from ? getComputedStyle(target).opacity : '0';
      cancel();
      control.setAttribute('aria-expanded', String(expanded));
      if (!expanded && target.contains(document.activeElement)) control.focus();
      const finish = (): void => { target.classList.toggle('expanded', expanded); target.inert = false; };
      if (!enabled() || getComputedStyle(target).display === 'contents') { finish(); return; }
      target.classList.add('expanded');
      const to = expanded ? target.getBoundingClientRect().height : 0;
      target.inert = !expanded;
      track([target.animate([{ height: `${from}px`, opacity, overflow: 'hidden' },
        { height: `${to}px`, opacity: expanded ? 1 : 0, overflow: 'hidden' }], options)], finish);
    }
  };
}
