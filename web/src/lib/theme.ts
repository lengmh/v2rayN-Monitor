type Theme = 'light' | 'dark' | 'system';

export function initializeTheme(control: HTMLSelectElement): void {
  const media = window.matchMedia?.('(prefers-color-scheme: dark)');
  let choice: Theme = 'light';
  try {
    const saved = localStorage.getItem('v2rayn-monitor-theme');
    if (saved === 'light' || saved === 'dark' || saved === 'system') choice = saved;
  } catch { /* Appearance still works when browser storage is unavailable. */ }
  const apply = (): void => {
    const resolved = choice === 'system' ? media?.matches ? 'dark' : 'light' : choice;
    document.documentElement.dataset.theme = resolved;
    document.querySelector('meta[name="color-scheme"]')?.setAttribute('content', resolved);
  };
  control.value = choice;
  control.addEventListener('change', () => {
    choice = control.value as Theme;
    apply();
    try { localStorage.setItem('v2rayn-monitor-theme', choice); } catch { /* Session-only preference. */ }
  });
  media?.addEventListener('change', () => { if (choice === 'system') apply(); });
  apply();
}
