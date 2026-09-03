import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useAppState } from '../state/AppState';
import { useMeta } from '../state/MetaContext';
import { deleteDocument, listDocuments, type InboxRow } from '../api/documents';
import { dt, fmt, moduleLabel, statusBadge } from '../utils/format';
import { OCR_PROVIDER_SHORT } from '../constants/fields';
import type { ModuleCode } from '../api/types';
import Pager, { DateRange, inDateRange, paginate } from '../components/Pager';

const USER = 'it-digital@megachem.co.th';

function inboxTitle(mod?: string | null) {
  if (!mod) return 'All Documents';
  return (mod === 'II' ? 'Incoming' : moduleLabel(mod)) + ' List';
}

// Document format by module: AP = with PO (MIRO), II = no PO (FB60).
function docFormat(mod?: string | null): { label: string; cls: string } {
  if (mod === 'AP') return { label: 'With PO', cls: 'b-ok' };
  if (mod === 'II') return { label: 'Without PO', cls: 'b-warn' };
  if (mod === 'PODP') return { label: 'PO Down Payment', cls: 'b-idle' };
  if (mod === 'SO') return { label: 'Sales', cls: 'b-idle' };
  return { label: '—', cls: '' };
}

export default function InboxPage() {
  const { module } = useParams<{ module: ModuleCode }>();
  const mod = module ?? null;
  const navigate = useNavigate();
  const { guard, showToast } = useAppState();
  const { apDocCategories, loadApDocCategories } = useMeta();

  const [rows, setRows] = useState<InboxRow[] | null>(null);
  const [search, setSearch] = useState('');
  const [category, setCategory] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);

  const reload = () => {
    setRows(null);
    guard(() => listDocuments(mod, category)).then((r) => setRows(r ?? []));
  };

  useEffect(() => {
    if (mod === 'AP') loadApDocCategories();
    setCategory('');
    setPage(1);
    setRows(null);
    guard(() => listDocuments(mod, '')).then((r) => setRows(r ?? []));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mod]);

  const filtered = useMemo(() => {
    if (!rows) return [];
    const q = search.trim().toLowerCase();
    let list = !q
      ? rows
      : rows.filter(
          (r) =>
            String(r.DocNo || '').toLowerCase().includes(q) ||
            String(r.PartnerName || '').toLowerCase().includes(q),
        );
    list = list.filter((r) => inDateRange(r.DocDate, from, to));
    return list;
  }, [rows, search, from, to]);

  const pageRows = paginate(filtered, page, pageSize);
  const catLabel = (id: string | null) =>
    (apDocCategories ?? []).find((c) => c.id === id)?.label || id || '';

  const delDoc = (id: number) => {
    if (!window.confirm('Delete document #' + id + ' ?')) return;
    guard(async () => {
      await deleteDocument(id, USER);
      showToast('Document deleted');
      reload();
    });
  };

  const invColHead = ['AP', 'II', 'PODP'].includes(mod ?? '') ? 'Invoice Number' : 'PO Number';
  const colCount = 12 + (!mod ? 1 : 0) + (mod === 'AP' ? 1 : 0);

  return (
    <div className="card">
      <div className="card-h">
        <h2>
          {inboxTitle(mod)} ({filtered.length})
        </h2>
        <div className="sp" />
        <input
          type="text"
          placeholder="Search partner / document no.…"
          value={search}
          onChange={(e) => {
            setSearch(e.target.value);
            setPage(1);
          }}
          style={{ maxWidth: 220, marginRight: 8 }}
        />
        <span className="hint" style={{ marginRight: 4 }}>
          Date:
        </span>
        <DateRange from={from} to={to} setFrom={setFrom} setTo={setTo} />
        <div style={{ marginRight: 8 }} />
        {mod === 'AP' && (
          <select
            value={category}
            onChange={(e) => {
              setCategory(e.target.value);
              setPage(1);
              setRows(null);
              guard(() => listDocuments(mod, e.target.value)).then((r) => setRows(r ?? []));
            }}
            style={{ marginRight: 8 }}
          >
            <option value="">All Document Types</option>
            {(apDocCategories ?? []).map((c) => (
              <option key={c.id} value={c.id}>
                {c.label}
              </option>
            ))}
          </select>
        )}
        <button className="btn sm" onClick={reload}>
          ↻ Refresh
        </button>
      </div>
      <div className="card-b">
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th>#</th>
                {!mod && <th>Module</th>}
                <th>File</th>
                <th>{invColHead}</th>
                <th>Type</th>
                <th>PO Date</th>
                <th>Supplier</th>
                <th style={{ textAlign: 'right' }}>Total</th>
                {mod === 'AP' && <th>Document Type</th>}
                <th>Status</th>
                <th>Model OCR</th>
                <th>SAP Doc</th>
                <th>Create Date</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {!rows ? (
                <tr>
                  <td colSpan={colCount} className="empty">
                    Loading…
                  </td>
                </tr>
              ) : pageRows.length ? (
                pageRows.map((r) => {
                  const sb = statusBadge(r.Status);
                  return (
                    <tr key={r.DocId}>
                      <td>{r.DocId}</td>
                      {!mod && (
                        <td>
                          <span
                            className={
                              'badge ' +
                              (r.Module === 'AP' ? 'b-warn' : r.Module === 'II' ? 'b-idle' : 'b-ok')
                            }
                          >
                            {r.Module}
                          </span>
                        </td>
                      )}
                      <td>{r.FileName || ''}</td>
                      <td>{r.DocNo || ''}</td>
                      <td>
                        {(() => {
                          const fmtd = docFormat(r.Module);
                          return <span className={'badge ' + fmtd.cls}>{fmtd.label}</span>;
                        })()}
                      </td>
                      <td>{r.DocDate || ''}</td>
                      <td>{r.PartnerName || ''}</td>
                      <td style={{ textAlign: 'right' }}>{fmt(r.TotalAmount)}</td>
                      {mod === 'AP' && (
                        <td>
                          {r.ApDocCategory ? catLabel(r.ApDocCategory) : <span className="hint">—</span>}
                        </td>
                      )}
                      <td>
                        <span className={'badge ' + sb.cls}>{sb.label}</span>{' '}
                        {r.OcrConfidence != null && (
                          <span className="hint">{Math.round(r.OcrConfidence * 100)}%</span>
                        )}
                      </td>
                      <td>
                        <span className="hint">
                          {OCR_PROVIDER_SHORT[r.OcrProvider ?? ''] || r.OcrProvider || '—'}
                        </span>
                      </td>
                      <td>{r.SapDocNo || ''}</td>
                      <td className="hint">{dt(r.CreatedAt)}</td>
                      <td style={{ whiteSpace: 'nowrap' }}>
                        <button className="btn sm" onClick={() => navigate('/doc/' + r.DocId)}>
                          Open
                        </button>{' '}
                        {r.Status !== 'POSTED' && (
                          <button className="btn sm ghost" onClick={() => delDoc(r.DocId)}>
                            ✕
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })
              ) : (
                <tr>
                  <td colSpan={colCount} className="empty">
                    No documents in the system yet
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <Pager
          page={page}
          setPage={setPage}
          pageSize={pageSize}
          setPageSize={setPageSize}
          total={filtered.length}
        />
      </div>
    </div>
  );
}
