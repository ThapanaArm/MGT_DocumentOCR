import { api } from './client';

/* SharePoint archive admin — see ArchiveAdminController.cs (Admin only). */

export interface ArchiveTarget {
  index: number;
  company: string;
  module: string;
  siteUrl: string;
  rootFolder: string;
  tenantId: string;
  clientId: string;
  driveId: string;
  secretSet: boolean;
  usable: boolean;
  missing: string[];
}

export interface ArchiveRecentRow {
  FileName: string | null;
  CompanyCode: string;
  Status: string;
  Attempts: number;
  LastError: string | null;
  RemotePath: string | null;
  RemoteUrl: string | null;
  ArchivedAt: string | null;
  LocalDeletedAt: string | null;
  UpdatedAt: string | null;
}

export interface ArchiveStatus {
  enabled: boolean;
  cleanupEnabled: boolean;
  cleanupDryRun: boolean;
  cleanupGraceHours: number;
  cleanupGraceMinutes?: number;
  intervalSeconds: number;
  targets: ArchiveTarget[];
  /** [{ status, count }] — key casing follows the server's JSON policy. */
  counts: Array<Record<string, unknown>>;
  recent: ArchiveRecentRow[];
  dbError: string | null;
}

export interface ArchiveTestStep {
  step: string;
  ok: boolean;
  detail: string;
}

export const getArchiveStatus = () => api.get<ArchiveStatus>('/api/admin/archive');
export const testArchiveTarget = (index: number) =>
  api.post<{ ok: boolean; steps: ArchiveTestStep[] }>(`/api/admin/archive/test/${index}`);
