import { useNavigate } from 'react-router-dom';
import { useState } from 'react';
import Pager from '../Pager';
import { usePagedList } from '../../hooks/usePagedList';
import { getJobTime, type JobTimeData, type JobTimeRow } from '../../api/dashboard';
import { useAppState } from '../../state/AppState';
import { dt, moduleLabel } from '../../utils/format';

/* Overview card "Document Processing Time" — Admin only (Megachem, 20 Oct 2026).
   Minutes from the start of the upload to a successful SAP / Zoho post (JobTaskTime,
   sql/34_job_tasktime.sql). Wall-clock time: a document left overnight counts its waiting time,
   so the headline is the median and a bucket breakdown rather than an average. */

const COMPANY: Record<string, string> = { '1000': 'MGT', '2000': 'GLC' };

/** 12.5 min · 2 h 05 min · 1 d 3 h */
export function fmtMinutes(m: number | null | undefined): string {
  if (m == null) return '—';
  if (m < 60) return `${m.toLocaleString('en-US', { maximumFractionDigits: 1 })} min`;
  if (m < 1440) {
    const t = Math.round(m);
    return `${Math.floor(t / 60)} h ${String(t % 60).padStart(2, '0')} min`;
  }
  const h = Math.round(m / 60);
  return `${Math.floor(h / 24)} d ${h % 24} h`;
}

export default function JobTimeCard({ days }: { days: number }) {
  const { guard } = useAppState();
  const navigate = useNavigate();
  const [company, setCompany] = useState('');
  const [module, setModule] = useState('');
  const [sort, setSort] = useState<'slow' | 'fast'>('slow');

  const { rows, total, data, page, pageSize, setPage, setPageSize } = usePagedList<JobTimeRow, JobTimeData>(
    (pg, ps) => guard(() => getJobTime({ days, company, module, sort, page: pg, pageSize: ps })),
    [days, company, module, sort],
  );

  const s = data?.summary;
  const maxBucket = Math.max(1, ...(data?.buckets ?? []).map((b) => b.count));

  return (
    <div className="card jt-card" style={{ marginTop: 20 }}>
      <div className="card-h jt-head">
        <div>
          <h2>Document Processing Time</h2>
          <div className="hint">
            Upload → posted to SAP / Zoho · posted in the last {days} days · <i className="fa-solid fa-lock" /> Admin only
          </div>
        </div>
        <div className="sp" />
        <div className="row jt-filters" style={{ gap: 8, flexWrap: 'wrap' }}>
          <select value={company} onChange={(e) => setCompany(e.target.value)} aria-label="Company">
            <option value="">All companies</option>
            <option value="1000">MGT</option>
            <option value="2000">GLC</option>
          </select>
          <select value={module} onChange={(e) => setModule(e.target.value)} aria-label="Document type">
            <option value="">All document types</option>
            <option value="SO">Sales Order</option>
            <option value="AP">Supplier Invoice</option>
            <option value="II">Incoming Invoice</option>
          </select>
          <select value={sort} onChange={(e) => setSort(e.target.value as 'slow' | 'fast')} aria-label="Sort">
            <option value="slow">Slowest first</option>
            <option value="fast">Fastest first</option>
          </select>
        </div>
      </div>
      <div className="card-b">
        {/* Headline numbers */}
        <div className="jt-stats">
          <div className="jt-stat">
            <div className="hint">Posted documents</div>
            <b>{s ? s.count.toLocaleString('en-US') : '—'}</b>
          </div>
          <div className="jt-stat" title="Half of the documents took less than this">
            <div className="hint">Median time</div>
            <b>{s ? fmtMinutes(s.medianMin) : '—'}</b>
          </div>
          <div className="jt-stat">
            <div className="hint">Done within 15 min</div>
            <b>{s?.within15Pct != null ? s.within15Pct + '%' : '—'}</b>
          </div>
          <div className="jt-stat" title="Usually documents left waiting overnight or longer">
            <div className="hint">Took over 1 day</div>
            <b className={s && s.overDayCount > 0 ? 'jt-warn' : ''}>{s ? s.overDayCount : '—'}</b>
          </div>
        </div>

        <div className="jt-split">
          {/* Distribution */}
          <div>
            <div className="jt-sub">How long documents took</div>
            {(data?.buckets ?? []).map((b) => (
              <div key={b.label} className="jt-bar-row">
                <span className="jt-bar-label">{b.label}</span>
                <span className="jt-bar-track">
                  <span className="jt-bar-fill" style={{ width: `${(b.count / maxBucket) * 100}%` }} />
                </span>
                <span className="jt-bar-n">{b.count}</span>
              </div>
            ))}
          </div>
          {/* Median per company + document type */}
          <div>
            <div className="jt-sub">Median by company / document type</div>
            {data && data.groups.length ? (
              data.groups.map((g) => (
                <div key={g.salesOrg + g.module} className="jt-group">
                  <span>
                    <b>{COMPANY[g.salesOrg] ?? (g.salesOrg || '—')}</b> · {moduleLabel(g.module)}
                  </span>
                  <span className="hint">{g.count} docs</span>
                  <b className="jt-num">{fmtMinutes(g.medianMin)}</b>
                </div>
              ))
            ) : (
              <div className="hint">—</div>
            )}
          </div>
        </div>

        {/* Each document */}
        <div className="tw" style={{ marginTop: 16 }}>
          <table className="jt-table">
            <thead>
              <tr>
                <th>Job No.</th>
                <th>Document No.</th>
                <th>Type</th>
                <th>Company</th>
                <th>Partner</th>
                <th>Uploaded By</th>
                <th>Upload Started</th>
                <th>Posted</th>
                <th style={{ textAlign: 'right' }}>Time Taken</th>
              </tr>
            </thead>
            <tbody>
              {!rows ? (
                <tr>
                  <td colSpan={9} className="empty">Loading…</td>
                </tr>
              ) : rows.length ? (
                rows.map((r) => (
                  <tr key={r.module + r.docId} className="jt-row" onClick={() => navigate(`/doc/${r.docId}`)}>
                    <td className="jt-nowrap">{r.jobNumber || '—'}</td>
                    <td>
                      <b>{r.docNo || '#' + r.docId}</b>
                      {r.sapDocNo && <div className="hint">→ {r.sapDocNo}</div>}
                    </td>
                    <td>{moduleLabel(r.module)}</td>
                    <td>{COMPANY[r.salesOrg] ?? (r.salesOrg || '—')}</td>
                    <td className="jt-partner" title={r.partnerName}>{r.partnerName || ''}</td>
                    <td>{r.createdBy || '—'}</td>
                    <td className="hint jt-nowrap">{dt(r.jobStartedAt)}</td>
                    <td className="hint jt-nowrap" title={r.postedBy ? 'Posted by ' + r.postedBy : ''}>{dt(r.postedAt)}</td>
                    <td
                      className={'jt-num jt-nowrap' + (r.jobTaskTime != null && r.jobTaskTime >= 1440 ? ' jt-warn' : '')}
                      style={{ textAlign: 'right' }}
                      title={
                        (r.jobTaskTime != null ? `${r.jobTaskTime} minutes` : '') +
                        (r.ocrSec != null ? ` (OCR reading: ${r.ocrSec} s)` : '')
                      }
                    >
                      <b>{fmtMinutes(r.jobTaskTime)}</b>
                    </td>
                  </tr>
                ))
              ) : (
                <tr>
                  <td colSpan={9} className="empty">
                    No posted documents with a recorded time in this period
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <Pager page={page} setPage={setPage} pageSize={pageSize} setPageSize={setPageSize} total={total} />
      </div>
    </div>
  );
}
