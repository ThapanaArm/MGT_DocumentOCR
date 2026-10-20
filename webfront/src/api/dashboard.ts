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

/* GET /api/dashboard/job-time — Admin only (JobTimeController.cs). Minutes from the start of the
   upload to a successful SAP / Zoho post, for documents posted in the last `days` days. */
export interface JobTimeRow {
  docId: number;
  module: string;
  salesOrg: string;
  jobNumber: string;
  docNo: string;
  partnerName: string;
  createdBy: string;
  postedBy: string;
  sapDocNo: string;
  jobStartedAt: string | null;
  postedAt: string | null;
  jobTaskTime: number | null;
  ocrSec: number | null;
}

export interface JobTimeData {
  days: number;
  summary: { count: number; medianMin: number | null; within15Pct: number | null; overDayCount: number };
  buckets: { label: string; count: number }[];
  groups: { module: string; salesOrg: string; count: number; medianMin: number | null }[];
  total: number;
  results: JobTimeRow[];
}

export const getJobTime = (q: {
  days: number; company: string; module: string; sort: string; page: number; pageSize: number;
}) =>
  api.get<JobTimeData>(
    `/api/dashboard/job-time?days=${q.days}&company=${encodeURIComponent(q.company)}` +
      `&module=${encodeURIComponent(q.module)}&sort=${q.sort}&page=${q.page}&pageSize=${q.pageSize}`,
  );
