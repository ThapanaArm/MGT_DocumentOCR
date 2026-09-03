import { api } from './client';

/* Master data API (GET /api/masters + CRUD /api/masters/:tab). Rows are raw
   PascalCase SQL columns, so keyed loosely. */

export type MasterRow = Record<string, any>;

export interface MastersData {
  customers: MasterRow[];
  vendors: MasterRow[];
  materials: MasterRow[];
  shiptos: MasterRow[];
  custmaterials: MasterRow[];
  venmaterials: MasterRow[];
  uoms: MasterRow[];
  [k: string]: MasterRow[];
}

export const getMasters = () => api.get<MastersData>('/api/masters');

export const createMaster = (tab: string, row: MasterRow) =>
  api.post('/api/masters/' + tab, row);

export const updateMaster = (tab: string, key: string, row: MasterRow) =>
  api.put('/api/masters/' + tab + '/' + encodeURIComponent(key), row);

export const deleteMaster = (tab: string, key: string) =>
  api.del('/api/masters/' + tab + '/' + encodeURIComponent(key));

export interface OcrProvider {
  id: string;
  label: string;
  desc: string;
  ready: boolean;
}
export const getOcrProviders = () => api.get<OcrProvider[]>('/api/ocr/providers');

export interface ApDocCategory {
  id: string;
  label: string;
}
export const getApDocCategories = () => api.get<ApDocCategory[]>('/api/ap-doc-categories');

export interface LogRow {
  LogId: number;
  PostedAt: string | null;
  Module: string;
  SapDocNo: string | null;
  DocNo: string | null;
  PartnerName: string | null;
  TotalAmount: number | null;
  Lines: number | null;
  Success: boolean;
  OcrProvider: string | null;
  FileName: string | null;
  [k: string]: unknown;
}
export const getLogs = () => api.get<LogRow[]>('/api/logs?limit=5000');
export const getLogPayload = (id: number) => api.get<Record<string, any>>('/api/logs/' + id + '/payload');

export interface AuditRow {
  Action: string;
  CreatedAt: string | null;
  Module: string;
  DocId: number | null;
  DocNo: string | null;
  FileName: string | null;
  Detail: string | null;
  OcrProvider: string | null;
  PerformedBy: string | null;
  [k: string]: unknown;
}
export const getAuditLogs = () => api.get<AuditRow[]>('/api/audit-logs?limit=5000');
