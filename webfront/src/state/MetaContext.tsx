import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import {
  getApDocCategories,
  getMasters,
  getOcrProviders,
  type ApDocCategory,
  type MastersData,
  type OcrProvider,
} from '../api/masters';
import { getMe, type Me } from '../api/me';

/* Shared reference data — the React replacement for S.masters / S.ocrProviders
   / S.apDocCategories caching in app.js. Loads lazily and caches; masters can
   be force-reloaded after edits. */

interface MetaValue {
  ocrProviders: OcrProvider[] | null;
  loadOcrProviders: () => Promise<OcrProvider[]>;
  apDocCategories: ApDocCategory[] | null;
  loadApDocCategories: () => Promise<ApDocCategory[]>;
  masters: MastersData | null;
  loadMasters: (force?: boolean) => Promise<MastersData>;
  /** The signed-in person as the backend sees them (role + company). Screens that behave
      differently for an Admin read it from here so /api/me is fetched once, not per page. */
  me: Me | null;
  loadMe: () => Promise<Me>;
}

const MetaContext = createContext<MetaValue | null>(null);

export function MetaProvider({ children }: { children: ReactNode }) {
  const [ocrProviders, setOcrProviders] = useState<OcrProvider[] | null>(null);
  const [apDocCategories, setApDocCategories] = useState<ApDocCategory[] | null>(null);
  const [masters, setMasters] = useState<MastersData | null>(null);
  const [me, setMe] = useState<Me | null>(null);

  const provPromise = useRef<Promise<OcrProvider[]> | null>(null);
  const catPromise = useRef<Promise<ApDocCategory[]> | null>(null);
  const mePromise = useRef<Promise<Me> | null>(null);

  const loadOcrProviders = useCallback(async () => {
    if (ocrProviders) return ocrProviders;
    if (!provPromise.current) provPromise.current = getOcrProviders();
    const data = await provPromise.current;
    setOcrProviders(data);
    return data;
  }, [ocrProviders]);

  const loadApDocCategories = useCallback(async () => {
    if (apDocCategories) return apDocCategories;
    if (!catPromise.current) catPromise.current = getApDocCategories();
    const data = await catPromise.current;
    setApDocCategories(data);
    return data;
  }, [apDocCategories]);

  const loadMasters = useCallback(
    async (force?: boolean) => {
      if (masters && !force) return masters;
      const data = await getMasters();
      setMasters(data);
      return data;
    },
    [masters],
  );

  const loadMe = useCallback(async () => {
    if (me) return me;
    if (!mePromise.current) mePromise.current = getMe();
    const data = await mePromise.current;
    setMe(data);
    return data;
  }, [me]);

  const value = useMemo<MetaValue>(
    () => ({
      ocrProviders,
      loadOcrProviders,
      apDocCategories,
      loadApDocCategories,
      masters,
      loadMasters,
      me,
      loadMe,
    }),
    [ocrProviders, loadOcrProviders, apDocCategories, loadApDocCategories, masters, loadMasters, me, loadMe],
  );

  return <MetaContext.Provider value={value}>{children}</MetaContext.Provider>;
}

// eslint-disable-next-line react-refresh/only-export-components
export function useMeta(): MetaValue {
  const ctx = useContext(MetaContext);
  if (!ctx) throw new Error('useMeta must be used within MetaProvider');
  return ctx;
}
