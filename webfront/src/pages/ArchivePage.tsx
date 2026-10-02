import { useCallback, useEffect, useState } from 'react';
import { useAppState } from '../state/AppState';
import {
  getArchiveStatus,
  testArchiveTarget,
  type ArchiveStatus,
  type ArchiveTestStep,
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

export default function ArchivePage() {
  const { guard } = useAppState();
  const [data, setData] = useState<ArchiveStatus | null>(null);
  const [results, setResults] = useState<Record<number, { ok: boolean; steps: ArchiveTestStep[] }>>({});
  const [testing, setTesting] = useState<number | null>(null);

  const load = useCallback(() => {
    void guard(() => getArchiveStatus()).then((r) => r && setData(r));
  }, [guard]);
  useEffect(load, [load]);

  const runTest = async (index: number) => {
    setTesting(index);
    try {
      const r = await guard(() => testArchiveTarget(index));
      if (r) setResults((p) => ({ ...p, [index]: r }));
    } finally {
      setTesting(null);
    }
  };

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
                  {!data.cleanupEnabled ? 'OFF' : data.cleanupDryRun ? 'DRY RUN (log only)' : `ON (after ${data.cleanupGraceHours} h)`}
                </span>
                <span className="badge b-ok">Archived {countOf(data, 'DONE')}</span>
                <span className="badge b-warn">Pending {countOf(data, 'PENDING')}</span>
                <span className="badge b-fail">Failed {countOf(data, 'FAILED')}</span>
              </div>
              {data.dbError && (
                <div className="hint" style={{ color: 'var(--red)', marginBottom: 12 }}>
                  Cannot read ocr.FileArchive (run sql/27_file_archive.sql): {data.dbError}
                </div>
              )}

              {data.targets.length === 0 && <div className="empty">No SharePoint target in Archive:Targets</div>}
              {data.targets.map((t) => {
                const res = results[t.index];
                return (
                  <div key={t.index} className="card" style={{ marginBottom: 12 }}>
                    <div className="card-h">
                      <h3 style={{ margin: 0 }}>
                        {t.company} · {t.module === '*' ? 'All modules' : t.module}
                      </h3>
                      <div className="sp" />
                      <button
                        className="btn sm primary"
                        disabled={testing !== null}
                        onClick={() => void runTest(t.index)}
                      >
                        <i className="fa-solid fa-plug-circle-check" />{' '}
                        {testing === t.index ? 'Testing…' : 'Test connection'}
                      </button>
                    </div>
                    <div className="card-b">
                      <table>
                        <tbody>
                          <tr><td className="hint">Site</td><td>{t.siteUrl || '—'}</td></tr>
                          <tr><td className="hint">Folder</td><td>
                              {t.company.toUpperCase() === 'MGT' && t.module.toUpperCase() === 'SO'
                                ? `${t.rootFolder}/{Sales}/{CustomerCode}_{CustomerName}/yyyy/MM`
                                : `${t.rootFolder}/${t.company}/${t.module === '*' ? '{module}' : t.module}/yyyy/MM`}
                            </td></tr>
                          <tr><td className="hint">Tenant ID</td><td>{t.tenantId || '—'}</td></tr>
                          <tr><td className="hint">Client ID</td><td>{t.clientId || '—'}</td></tr>
                          <tr><td className="hint">Drive ID</td><td style={{ wordBreak: 'break-all' }}>{t.driveId || '—'}</td></tr>
                          <tr>
                            <td className="hint">Client secret</td>
                            <td>
                              {t.secretSet ? (
                                <span className="badge b-ok">Set</span>
                              ) : (
                                <span className="badge b-fail">Not set — Archive__Targets__{t.index}__ClientSecret</span>
                              )}
                            </td>
                          </tr>
                        </tbody>
                      </table>
                      {res && (
                        <div style={{ marginTop: 12 }}>
                          <div style={{ marginBottom: 6 }}>
                            <span className={'badge ' + (res.ok ? 'b-ok' : 'b-fail')}>
                              {res.ok ? 'Connection OK' : 'Connection failed'}
                            </span>
                          </div>
                          <table>
                            <tbody>
                              {res.steps.map((s, i) => (
                                <tr key={i}>
                                  <td style={{ width: 28 }}>
                                    <i
                                      className={'fa-solid ' + (s.ok ? 'fa-circle-check' : 'fa-circle-xmark')}
                                      style={{ color: s.ok ? 'var(--green)' : 'var(--red)' }}
                                    />
                                  </td>
                                  <td style={{ whiteSpace: 'nowrap' }}>{s.step}</td>
                                  <td className="hint" style={{ wordBreak: 'break-word' }}>{s.detail}</td>
                                </tr>
                              ))}
                            </tbody>
                          </table>
                        </div>
                      )}
                    </div>
                  </div>
                );
              })}
            </>
          )}
        </div>
      </div>

      {data && (
        <div className="card">
          <div className="card-h">
            <h2>Latest archived files</h2>
          </div>
          <div className="card-b">
            <div className="tw">
              <table>
                <thead>
                  <tr>
                    <th>Updated</th>
                    <th>File</th>
                    <th>Company</th>
                    <th>Status</th>
                    <th>SharePoint</th>
                    <th>Local copy</th>
                  </tr>
                </thead>
                <tbody>
                  {data.recent.length ? (
                    data.recent.map((r, i) => (
                      <tr key={i}>
                        <td className="hint">{dt(r.UpdatedAt ?? '')}</td>
                        <td>{r.FileName || '—'}</td>
                        <td>{r.CompanyCode}</td>
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
                      <td colSpan={6} className="empty">
                        Nothing archived yet
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
