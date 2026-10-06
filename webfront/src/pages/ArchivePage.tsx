import { useCallback, useEffect, useState } from 'react';
import { useAppState } from '../state/AppState';
import {
  getArchiveStatus,
  type ArchiveRecentRow,
  type ArchiveStatus,
} from '../api/archive';
import { dt } from '../utils/format';

/* SharePoint archive (Admin only): configuration, a live connection test per target,
   and the latest rows of ocr.FileArchive. Never shows the client secret itself. */

const STATUS_BADGE: Record<string, string> = { DONE: 'b-ok', FAILED: 'b-fail', PENDING: 'b-warn' };

// The server serialises counts with either casing depending on the JSON policy - accept both.
function countOf(s: ArchiveStatus, status: string): number {
  for (const r of s.counts) {
    const st = (r.status ?? r.Status) as string;
    if (st === status) return Number(r.count ?? r.Count ?? 0);
  }
  return 0;
}

// Latest archived files of one company — shown inside that company's card (MGT / GLC separately).
function filesTable(rows: ArchiveRecentRow[]) {
  return (
    <div className="tw">
      <table>
        <thead>
          <tr>
            <th>Updated</th>
            <th>File</th>
            <th>Status</th>
            <th>SharePoint</th>
            <th>Local copy</th>
          </tr>
        </thead>
        <tbody>
          {rows.length ? (
            rows.map((r, i) => (
              <tr key={i}>
                <td className="hint">{dt(r.UpdatedAt ?? '')}</td>
                <td>{r.FileName || '—'}</td>
                <td>
                  <span className={'badge ' + (STATUS_BADGE[r.Status] || 'b-idle')}>{r.Status}</span>
                  {r.Attempts > 1 && <span className="hint"> ×{r.Attempts}</span>}
                  {r.LastError && <div className="hint">{r.LastError}</div>}
                </td>
                <td>
                  {r.RemoteUrl ? (
                    <a href={r.RemoteUrl} target="_blank" rel="noreferrer">
                      {r.RemotePath || 'Open'}
                    </a>
                  ) : (
                    r.RemotePath || '—'
                  )}
                </td>
                <td className="hint">{r.LocalDeletedAt ? 'Deleted ' + dt(r.LocalDeletedAt) : 'On server'}</td>
              </tr>
            ))
          ) : (
            <tr>
              <td colSpan={5} className="empty">
                Nothing archived yet
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

export default function ArchivePage() {
  const { guard } = useAppState();
  const [data, setData] = useState<ArchiveStatus | null>(null);

  const load = useCallback(() => {
    void guard(() => getArchiveStatus()).then((r) => r && setData(r));
  }, [guard]);
  useEffect(load, [load]);

  return (
    <>
      <div className="card">
        <div className="card-h">
          <h2>SharePoint Archive</h2>
          <div className="sp" />
          <button className="btn sm" onClick={load}>
            <i className="fa-solid fa-arrow-rotate-right" /> Refresh
          </button>
        </div>
        <div className="card-b">
          {!data ? (
            <div className="empty">Loading…</div>
          ) : (
            <>
              <div className="row" style={{ gap: 8, flexWrap: 'wrap', marginBottom: 12 }}>
                <span className={'badge ' + (data.enabled ? 'b-ok' : 'b-idle')}>
                  Archive {data.enabled ? 'ON' : 'OFF'}
                </span>
                <span className={'badge ' + (data.cleanupEnabled && !data.cleanupDryRun ? 'b-ok' : 'b-idle')}>
                  Delete local file after archive:{' '}
                  {!data.cleanupEnabled ? 'OFF' : data.cleanupDryRun ? 'DRY RUN (log only)' : `ON (after ${(data.cleanupGraceMinutes ?? data.cleanupGraceHours * 60) < 60 ? `${data.cleanupGraceMinutes} min` : `${(data.cleanupGraceMinutes ?? data.cleanupGraceHours * 60) / 60} h`})`}
                </span>
                <span className="badge b-ok">Archived {countOf(data, 'DONE')}</span>
                <span className="badge b-warn">Pending {countOf(data, 'PENDING')}</span>
                <span className="badge b-fail">Failed {countOf(data, 'FAILED')}</span>
              </div>
              {data.dbError && (
                <div className="hint" style={{ color: 'var(--red)', marginBottom: 12 }}>
                  Archive information is temporarily unavailable. Please try again later.
                </div>
              )}

              {data.targets.length === 0 && <div className="empty">No SharePoint target in Archive:Targets</div>}
              {data.targets.map((t) => {
                return (
                  <div key={t.index} className="card" style={{ marginBottom: 12 }}>
                    <div className="card-h">
                      <h3 style={{ margin: 0 }}>
                        {t.company} · {t.module === '*' ? 'All modules' : t.module}
                      </h3>
                    </div>
                    <div className="card-b">
                      {/* Only the company's archived files. A target that can't run is still flagged so a
                          missing setting isn't silent (the Test button was removed on request, 5 Oct 2026;
                          POST /api/admin/archive/test/{index} still exists for troubleshooting). */}
                      {(!t.usable || !t.secretSet) && (
                        <span className="badge b-fail">
                          Not ready{t.missing?.length ? ` — missing ${t.missing.join(', ')}` : !t.secretSet ? ' — client secret not set' : ''}
                        </span>
                      )}
                      <h4 style={{ margin: '4px 0 8px' }}>Latest archived files — {t.company}</h4>
                      {filesTable(data.recent.filter((r) => (r.CompanyCode || '').toUpperCase() === t.company.toUpperCase()))}
                    </div>
                  </div>
                );
              })}
            </>
          )}
        </div>
      </div>

      {/* Archived files of a company that has no target card (e.g. a target removed later). */}
      {data && (() => {
        const known = new Set(data.targets.map((t) => t.company.toUpperCase()));
        const other = data.recent.filter((r) => !known.has((r.CompanyCode || '').toUpperCase()));
        return other.length ? (
          <div className="card">
            <div className="card-h"><h2>Other archived files</h2></div>
            <div className="card-b">{filesTable(other)}</div>
          </div>
        ) : null;
      })()}
    </>
  );
}
