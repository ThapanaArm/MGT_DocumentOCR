/* =====================================================================
   Shared API/domain types. Kept intentionally light for Phase A; each
   feature phase will tighten the shapes it consumes as it is ported.
   ===================================================================== */

// Module codes used across the app (matches S.module in app.js).
export type ModuleCode = 'AP' | 'PODP' | 'II' | 'SO';

export interface OcrProvider {
  id: string;
  label: string;
  desc: string;
  ready: boolean;
}

export interface ApDocCategory {
  id: string;
  label: string;
}

// Health/status shown in the sidebar footer (GET /api/health).
export interface HealthStatus {
  ok?: boolean;
  db?: { db: string; usr: string; srv: string };
  counts?: Record<string, number>;
  ocrProvider?: string;
  sapMode?: string;
  error?: string;
  [k: string]: unknown;
}
