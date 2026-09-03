import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';

/* =====================================================================
   Global app state — the React replacement for the cross-cutting parts
   of the app.js `S` object: theme, sidebar collapse, a global busy flag,
   and toast notifications. Theme/busy are reflected onto the DOM the same
   way the old app did it (data-theme on <html>, .busy on <body>) so the
   ported legacy CSS (styles/legacy.css) keeps working unchanged.
   ===================================================================== */

const THEME_KEY = 'ocr_sap_theme';
const NAV_KEY = 'ocr_sap_nav_collapsed';

export type ThemeMode = 'light' | 'dark';

interface ToastState {
  open: boolean;
  message: string;
}

interface AppStateValue {
  themeMode: ThemeMode;
  toggleTheme: () => void;

  navCollapsed: boolean;
  toggleNav: () => void;

  busy: boolean;
  setBusy: (b: boolean) => void;

  toast: ToastState;
  showToast: (message: string) => void;
  hideToast: () => void;

  /** Wraps an async action with busy state + error toast (ports guard()). */
  guard: <T>(fn: () => Promise<T>) => Promise<T | undefined>;
}

const AppStateContext = createContext<AppStateValue | null>(null);

function readStored<T>(key: string, fallback: T, parse: (v: string) => T): T {
  try {
    const v = localStorage.getItem(key);
    return v == null ? fallback : parse(v);
  } catch {
    return fallback;
  }
}

export function AppStateProvider({ children }: { children: ReactNode }) {
  const [themeMode, setThemeMode] = useState<ThemeMode>(() =>
    readStored<ThemeMode>(THEME_KEY, 'light', (v) => (v === 'dark' ? 'dark' : 'light')),
  );
  const [navCollapsed, setNavCollapsed] = useState<boolean>(() =>
    readStored<boolean>(NAV_KEY, false, (v) => v === '1'),
  );
  const [busy, setBusyState] = useState(false);
  const [toast, setToast] = useState<ToastState>({ open: false, message: '' });

  // Reflect theme onto <html data-theme> so legacy.css [data-theme="dark"] rules apply.
  useEffect(() => {
    document.documentElement.setAttribute('data-theme', themeMode);
  }, [themeMode]);

  // Reflect busy onto <body class="busy"> so legacy .busy-indicator / dimming apply.
  useEffect(() => {
    document.body.classList.toggle('busy', busy);
  }, [busy]);

  const toggleTheme = useCallback(() => {
    setThemeMode((prev) => {
      const next = prev === 'dark' ? 'light' : 'dark';
      try {
        localStorage.setItem(THEME_KEY, next);
      } catch {
        /* ignore */
      }
      return next;
    });
  }, []);

  const toggleNav = useCallback(() => {
    setNavCollapsed((prev) => {
      const next = !prev;
      try {
        localStorage.setItem(NAV_KEY, next ? '1' : '0');
      } catch {
        /* ignore */
      }
      return next;
    });
  }, []);

  const setBusy = useCallback((b: boolean) => setBusyState(b), []);

  const showToast = useCallback((message: string) => {
    setToast({ open: true, message });
  }, []);

  const hideToast = useCallback(() => {
    setToast((t) => ({ ...t, open: false }));
  }, []);

  const guard = useCallback(
    async <T,>(fn: () => Promise<T>): Promise<T | undefined> => {
      setBusyState(true);
      try {
        return await fn();
      } catch (e) {
        showToast('⚠ ' + (e instanceof Error ? e.message : String(e)));
        return undefined;
      } finally {
        setBusyState(false);
      }
    },
    [showToast],
  );

  const value = useMemo<AppStateValue>(
    () => ({
      themeMode,
      toggleTheme,
      navCollapsed,
      toggleNav,
      busy,
      setBusy,
      toast,
      showToast,
      hideToast,
      guard,
    }),
    [themeMode, toggleTheme, navCollapsed, toggleNav, busy, setBusy, toast, showToast, hideToast, guard],
  );

  return <AppStateContext.Provider value={value}>{children}</AppStateContext.Provider>;
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAppState(): AppStateValue {
  const ctx = useContext(AppStateContext);
  if (!ctx) throw new Error('useAppState must be used within AppStateProvider');
  return ctx;
}
