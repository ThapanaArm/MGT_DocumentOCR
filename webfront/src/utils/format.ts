/* Formatting & label helpers ported from app.js (num/fmt/fmtCost/dt/
   moduleLabel/statusBadge). Kept framework-agnostic. */

export const num = (v: unknown): number => {
  const n = parseFloat(String(v == null ? '' : v).replace(/[, ]/g, ''));
  return isNaN(n) ? 0 : n;
};

export const fmt = (n: unknown) =>
  num(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export const fmtCost = (n: unknown) =>
  num(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 });

export const fmtAmt = (n: unknown) =>
  num(n).toLocaleString('en-US', { minimumFractionDigits: 0, maximumFractionDigits: 2 });

export const intFmt = (n: unknown) => num(n).toLocaleString('en-US');

export const dt = (s: unknown): string =>
  s ? String(s).replace('T', ' ').slice(0, 19) : '';

// Matches app.js moduleLabel(): AP (and unknown) fall back to 'Supplier Invoice'.
export const moduleLabel = (mod: string | null | undefined): string =>
  ({ SO: 'Sales Order', II: 'Incoming Invoice', PODP: 'PO Down Payment' } as Record<string, string>)[
    mod ?? ''
  ] || 'Supplier Invoice';

export interface StatusBadge {
  cls: string;
  label: string;
}

// Ported from statusBadge() in app.js.
export const statusBadge = (s: string): StatusBadge => {
  const map: Record<string, [string, string]> = {
    NEW: ['b-idle', 'Pending Mapping'],
    INCOMPLETE: ['b-fail', 'Mapping Incomplete'],
    MAPPED: ['b-ok', 'Pending SAP Connection'],
    POSTED: ['b-ok', 'SAP Connected Successfully'],
    SPLIT: ['b-warn', 'แยกเป็นหลาย SO แล้ว'],
  };
  const [cls, label] = map[s] || ['b-idle', s];
  return { cls, label };
};
