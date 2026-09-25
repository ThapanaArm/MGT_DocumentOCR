import { useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { getBatch, type BatchJob } from '../api/documents';
import { MODULE_LABEL } from '../navConfig';
import { moduleLabel } from '../utils/format';
import type { ModuleCode } from '../api/types';
import Steps from '../components/Steps';

/* Batch OCR queue view: polls the background worker every 2s and shows each file moving
   QUEUED -> PROCESSING -> DONE / FAILED. Click a finished row to review that document. */

const STATUS_STYLE: Record<BatchJob['status'], { label: string; color: string; bg: string }> = {
  QUEUED: { label: 'Queued', color: 'var(--muted)', bg: 'var(--line-soft)' },
  PROCESSING: { label: 'Reading…', color: 'var(--amber, #b9770e)', bg: 'var(--amber-bg, #fbf3e3)' },
  DONE: { label: 'Done', color: 'var(--green)', bg: 'var(--green-bg)' },
  FAILED: { label: 'Failed', color: 'var(--red)', bg: 'var(--red-bg)' },
};

export default function BatchStatusPage() {
  const { module, batchId } = useParams<{ module: ModuleCode; batchId: string }>();
  const mod = (module ?? 'AP') as ModuleCode;
  const navigate = useNavigate();
  const [jobs, setJobs] = useState<BatchJob[]>([]);
  const [error, setError] = useState('');
  const timer = useRef<number | null>(null);

  const poll = useCallback(async () => {
    if (!batchId) return;
    try {
      const r = await getBatch(batchId);
      setJobs(r.jobs);
      const pending = r.jobs.some((j) => j.status === 'QUEUED' || j.status === 'PROCESSING');
      if (pending) timer.current = window.setTimeout(poll, 2000);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [batchId]);

  useEffect(() => {
    void poll();
    return () => {
      if (timer.current) window.clearTimeout(timer.current);
    };
  }, [poll]);

  const done = jobs.filter((j) => j.status === 'DONE').length;
  const failed = jobs.filter((j) => j.status === 'FAILED').length;
  const total = jobs.length;
  const allFinished = total > 0 && done + failed === total;
  const modName = mod === 'AP' ? 'Invoice' : MODULE_LABEL[mod] || moduleLabel(mod);

  return (
    <>
      <Steps current={1} />
      <div className="card">
        <div className="card-h">
          <h2>Reading {total} document{total === 1 ? '' : 's'} — {modName}</h2>
          <div className="sp" />
          <span className="hint">
            {allFinished ? 'All done' : `${done + failed} / ${total} finished`}
            {failed > 0 && ` · ${failed} failed`}
          </span>
        </div>
        <div className="card-b">
          {error && <div className="result bad" style={{ marginBottom: 14 }}>{error}</div>}
          {!allFinished && (
            <p className="hint" style={{ marginTop: 0 }}>
              <i className="fa-solid fa-spinner fa-spin" /> Reading in the background — you can wait here or come back later. Finished documents can be reviewed right away.
            </p>
          )}
          {allFinished && (
            <p className="hint" style={{ marginTop: 0 }}>
              Finished. Click a document below to review it one by one{failed > 0 ? ', and re-import any that failed on their own.' : '.'}
            </p>
          )}

          <div className="tw" style={{ marginTop: 12 }}>
            <table style={{ width: '100%' }}>
              <thead>
                <tr>
                  <th style={{ textAlign: 'left' }}>File</th>
                  <th style={{ width: 130 }}>Status</th>
                  <th style={{ width: 160, textAlign: 'right' }}>Action</th>
                </tr>
              </thead>
              <tbody>
                {jobs.length === 0 && !error && (
                  <tr><td colSpan={3} className="empty">Loading…</td></tr>
                )}
                {jobs.map((j) => {
                  const st = STATUS_STYLE[j.status];
                  return (
                    <tr key={j.jobId}>
                      <td style={{ overflowWrap: 'anywhere' }}>
                        {j.fileName}
                        {j.status === 'FAILED' && j.error && (
                          <div className="hint" style={{ color: 'var(--red)' }}>{j.error}</div>
                        )}
                      </td>
                      <td>
                        <span style={{
                          display: 'inline-flex', alignItems: 'center', gap: 6, padding: '3px 10px',
                          borderRadius: 'var(--pill)', fontSize: 13, fontWeight: 600,
                          color: st.color, background: st.bg,
                        }}>
                          {j.status === 'PROCESSING' && <i className="fa-solid fa-spinner fa-spin" />}
                          {st.label}
                        </span>
                      </td>
                      <td style={{ textAlign: 'right' }}>
                        {j.status === 'DONE' && j.resultDocId != null && (
                          <a className="btn sm primary" href={'/doc/' + j.resultDocId} target="_blank" rel="noopener noreferrer">
                            Review
                          </a>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          <div className="row" style={{ marginTop: 18 }}>
            <button className="btn" onClick={() => navigate('/import/' + mod)}>
              <i className="fa-solid fa-plus" /> Import more
            </button>
            <button className="btn" onClick={() => navigate('/list/' + mod)}>
              <i className="fa-solid fa-list" /> Go to document list
            </button>
          </div>
        </div>
      </div>
    </>
  );
}
