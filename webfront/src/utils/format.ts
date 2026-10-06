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
    // Neutral wording: a document is posted to SAP (GLC, liability modules) or Zoho CRM (MGT SO).
    MAPPED: ['b-ok', 'Ready to Post'],
    // Sales Order sent as one SO per delivery date and only some went through — retry the rest.
    PARTIAL: ['b-warn', 'Partially Posted'],
    POSTED: ['b-ok', 'Post Success'],
    SPLIT: ['b-warn', 'Split into multiple SOs'],
  };
  const [cls, label] = map[s] || ['b-idle', s];
  return { cls, label };
};

/* File retention of a never-posted document (backend: Archive:CleanupDraftHours / CleanupDraftDays,
   sent as retentionHours). The cleanup worker removes the uploaded file once the document has been
   untouched (UpdatedAt, else CreatedAt) for that long; FileExpiredAt is stamped when it does. */
export interface FileRetention {
  kind: 'none' | 'ok' | 'soon' | 'expired';
  expiredAt?: string;
  expiresAt?: Date;
  left?: string; // "35 min", "5 h", "3 days", or "next cleanup run" when already past due
}

const leftLabel = (ms: number): string => {
  if (ms <= 0) return 'next cleanup run';
  const min = Math.ceil(ms / 60000);
  if (min < 60) return `${min} min`;
  const h = Math.floor(min / 60);
  if (h < 48) return `${h} h`;
  return `${Math.floor(h / 24)} days`;
};

export const fileRetention = (
  d: { status?: string | null; hasFile?: unknown; fileExpiredAt?: unknown; updatedAt?: unknown; createdAt?: unknown },
  retentionHours: number | null | undefined,
): FileRetention => {
  if (d.fileExpiredAt) return { kind: 'expired', expiredAt: String(d.fileExpiredAt) };
  const hours = Number(retentionHours) || 0;
  const status = String(d.status || '');
  if (hours <= 0 || !d.hasFile || status === 'POSTED' || status === 'PARTIAL' || status === 'SPLIT') return { kind: 'none' };
  const last = new Date(String(d.updatedAt || d.createdAt || ''));
  if (isNaN(last.getTime())) return { kind: 'none' };
  const expiresAt = new Date(last.getTime() + hours * 3600000);
  const ms = expiresAt.getTime() - Date.now();
  // Warn in the last 20% of the period (at least the last hour, never more than the whole period).
  const warnMs = Math.min(hours, Math.max(hours * 0.2, 1)) * 3600000;
  return { kind: ms <= warnMs ? 'soon' : 'ok', expiresAt, left: leftLabel(ms) };
};
