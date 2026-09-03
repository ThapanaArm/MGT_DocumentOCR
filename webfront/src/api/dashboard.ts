import { api } from './client';

/* Shape of GET /api/dashboard?days=N — see DashboardController.cs.
   Note the casing: statusCounts/trend keys are UPPERCASE status codes,
   `recent` rows are raw PascalCase SQL columns, everything else camelCase. */

export interface StatusCounts {
  NEW: number;
  INCOMPLETE: number;
  MAPPED: number;
  POSTED: number;
  SPLIT: number;
  total: number;
}

export interface Trend {
  NEW: number;
  INCOMPLETE: number;
  MAPPED: number;
  POSTED: number;
  SPLIT: number;
  total: number;
}

export interface ByModuleRow {
  module: string;
  count: number;
}

export interface CostByModuleRow {
  module: string;
  count: number;
  tokens: number;
  cost: number;
  costCurrency: string;
}

export interface OcrPerf {
  avgConfidencePct: number | null;
  avgDurationSec: number | null;
  pctEditedByUser: number;
  tokensToday: number;
}

export interface OcrDailyRow {
  date: string;
  docCount: number;
  okCount: number;
}

export interface RecentDoc {
  DocId: number;
  Module: string;
  FileName: string | null;
  DocNo: string | null;
  PartnerName: string | null;
  Status: string;
  TotalAmount: number | null;
  SapDocNo: string | null;
  CreatedAt: string | null;
  UpdatedAt: string | null;
  CreatedBy: string | null;
  PostedBy: string | null;
}

export interface DashboardData {
  statusCounts: StatusCounts;
  trend: Trend;
  byModule: ByModuleRow[];
  costByModule: CostByModuleRow[];
  ocrPerf: OcrPerf;
  ocrDaily: OcrDailyRow[];
  recent: RecentDoc[];
}

export const getDashboard = (days: number) =>
  api.get<DashboardData>('/api/dashboard?days=' + days);
