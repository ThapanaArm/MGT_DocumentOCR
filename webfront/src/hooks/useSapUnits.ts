import { useEffect, useState } from 'react';
import { getSapMaterialDetail } from '../api/sap';

/* SAP units of measure for ONE material — base unit + alternative units from API_PRODUCT_SRV
   (backend GET /api/sap/material-detail, cached 30 min server-side). Feeds the UoM dropdowns in
   Master → Unit Conversion and in each Sales Order line (GLC/SAP only — MGT/Zoho takes its unit
   from the Deal, so callers pass enabled=false there).
   Best-effort like the rest of the SAP lookups: SAP not configured / material not found / call
   failed → units = [] and the picker falls back to a free-text box, never blocks the user. */

export interface SapUnitChoice {
  unit: string;
  isBase: boolean;
  /** "1 DR = 50 KG"-style hint relative to the base unit; empty for the base unit itself. */
  hint: string;
}

// Module-level cache so the same material on 20 lines (or reopening the modal) asks SAP once.
const cache = new Map<string, SapUnitChoice[]>();
const inflight = new Map<string, Promise<SapUnitChoice[]>>();

const fmt = (n: number) => String(Number(n.toFixed(6)));

function load(code: string): Promise<SapUnitChoice[]> {
  const key = code.trim().toUpperCase();
  const hit = cache.get(key);
  if (hit) return Promise.resolve(hit);
  const running = inflight.get(key);
  if (running) return running;
  const p = getSapMaterialDetail(code.trim())
    .then((r) => {
      const d = r?.detail;
      if (!d || !d.baseUnit) return [];
      const list: SapUnitChoice[] = [{ unit: d.baseUnit.toUpperCase(), isBase: true, hint: '' }];
      for (const a of d.altUnits || []) {
        const u = (a.unit || '').toUpperCase();
        if (!u || list.some((x) => x.unit === u)) continue;
        // SAP convention: Numerator/Denominator base units = 1 alternative unit.
        const ratio = a.denominator ? a.numerator / a.denominator : 0;
        list.push({ unit: u, isBase: false, hint: ratio > 0 ? `1 ${u} = ${fmt(ratio)} ${d.baseUnit.toUpperCase()}` : '' });
      }
      cache.set(key, list); // only successful lookups are cached; failures retry next time
      return list;
    })
    .catch(() => [] as SapUnitChoice[])
    .finally(() => inflight.delete(key));
  inflight.set(key, p);
  return p;
}

export function useSapUnits(materialCode: string | null | undefined, enabled = true) {
  const code = (materialCode || '').trim();
  const [units, setUnits] = useState<SapUnitChoice[]>(() => (code ? cache.get(code.toUpperCase()) ?? [] : []));
  const [loading, setLoading] = useState(false);
  useEffect(() => {
    if (!enabled || !code) { setUnits([]); setLoading(false); return; }
    const hit = cache.get(code.toUpperCase());
    if (hit) { setUnits(hit); setLoading(false); return; }
    let cancelled = false;
    setLoading(true);
    load(code).then((u) => { if (!cancelled) { setUnits(u); setLoading(false); } });
    return () => { cancelled = true; };
  }, [code, enabled]);
  return { units, loading };
}
