import { useEffect, useMemo, useState } from 'react';
import { useAppState } from '../state/AppState';
import { getAuditLogs, type AuditRow } from '../api/masters';
import { dt, moduleLabel } from '../utils/format';
import { OCR_PROVIDER_SHORT } from '../constants/fields';
import Pager, { DateRange, inDateRange, paginate } from '../components/Pager';

/* Ports renderAuditLog()/renderAuditLogLocal(). */
const ACTION_BADGE: Record<string, [string, string]> = {
  CREATE: ['b-ok', 'Document Created'],
  UPDATE: ['b-warn', 'Edited'],
  DELETE: ['b-fail', 'Document Deleted'],
  REOCR: ['b-idle', 'Re-run OCR'],
};
const MODULE_FILTERS: [string, string][] = [
  ['', 'All'],
  ['AP', 'Supplier Invoice'],
  ['PODP', 'PO Down Payment'],
  ['II', 'Incoming Invoice'],
  ['SO', 'Sales Order'],
];

export default function AuditLogPage() {
  const { guard } = useAppState();
  const [rows, setRows] = useState<AuditRow[] | null>(null);
  const [modFilter, setModFilter] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);

  const reload = () => {
    setRows(null);
    guard(() => getAuditLogs()).then((r) => setRows(r ?? []));
  };
  useEffect(reload, [guard]);

  const filtered = useMemo(() => {
    let list = rows ?? [];
    if (modFilter) list = list.filter((l) => l.Module === modFilter);
    return list.filter((l) => inDateRange(l.CreatedAt, from, to));
  }, [rows, modFilter, from, to]);
  const pageRows = paginate(filtered, page, pageSize);

  return (
    <div className="card">
      <div className="card-h">
        <h2>Activity Log ({filtered.length})</h2>
        <div className="sp" />
        <span className="hint" style={{ marginRight: 4 }}>
          Date:
        </span>
        <DateRange from={from} to={to} setFrom={setFrom} setTo={setTo} />
        <div style={{ marginRight: 8 }} />
        <button className="btn sm" onClick={reload}>
          ↻ Refresh
        </button>
      </div>
      <div className="card-b">
        <div className="row" style={{ gap: 6, marginBottom: 16, flexWrap: 'wrap' }}>
          {MODULE_FILTERS.map(([v, l]) => (
            <button
              key={v}
              className={'btn sm ' + (v === modFilter ? 'primary' : 'ghost')}
              onClick={() => {
                setModFilter(v);
                setPage(1);
              }}
            >
              {l}
            </button>
          ))}
        </div>
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th>Time</th>
                <th>Module</th>
                <th>Process</th>
                <th>Document</th>
                <th>Details</th>
                <th>Model</th>
                <th>Performed By</th>
              </tr>
            </thead>
            <tbody>
              {!rows ? (
                <tr>
                  <td colSpan={7} className="empty">
                    Loading…
                  </td>
                </tr>
              ) : pageRows.length ? (
                pageRows.map((l, i) => {
                  const b = ACTION_BADGE[l.Action] || ['b-idle', l.Action];
                  return (
                    <tr key={i}>
                      <td className="hint">{dt(l.CreatedAt)}</td>
                      <td>
                        <span
                          className={
                            'badge ' + (l.Module === 'AP' ? 'b-warn' : l.Module === 'II' ? 'b-idle' : 'b-ok')
                          }
                        >
                          {moduleLabel(l.Module)}
                        </span>
                      </td>
                      <td>
                        <span className={'badge ' + b[0]}>{b[1]}</span>
                      </td>
                      <td>
                        #{l.DocId ?? '-'}
                        {l.DocNo ? ' · ' + l.DocNo : ''}
                        {l.FileName && <div className="hint">{l.FileName}</div>}
                      </td>
                      <td>{l.Detail || ''}</td>
                      <td>
                        <span className="hint">
                          {OCR_PROVIDER_SHORT[l.OcrProvider ?? ''] || l.OcrProvider || '—'}
                        </span>
                      </td>
                      <td>{l.PerformedBy || ''}</td>
                    </tr>
                  );
                })
              ) : (
                <tr>
                  <td colSpan={7} className="empty">
                    No history yet
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <Pager page={page} setPage={setPage} pageSize={pageSize} setPageSize={setPageSize} total={filtered.length} />
      </div>
    </div>
  );
}
