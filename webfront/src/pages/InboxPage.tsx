import { useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useAppState } from '../state/AppState';
import { useMeta } from '../state/MetaContext';
import { deleteDocument, listDocumentsPaged, type DocumentsPage, type InboxRow } from '../api/documents';
import { dt, fmt, moduleLabel, statusBadge } from '../utils/format';
import { OCR_PROVIDER_SHORT } from '../constants/fields';
import type { ModuleCode } from '../api/types';
import Pager, { DateRange } from '../components/Pager';
import { usePagedList } from '../hooks/usePagedList';

// Deprecated. The backend stamps identity from the validated token; whatever is passed here is
// discarded. Kept only so the delete call signature keeps compiling.
const USER = '(ignored by the server)';

function inboxTitle(mod?: string | null) {
  if (!mod) return 'All Documents';
  if (mod === 'AP') return 'Invoice List';
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
  const isInvoice = mod === 'AP';
  const isSalesOrder = mod === 'SO';
  const navigate = useNavigate();
  const { guard, showToast } = useAppState();
  const { apDocCategories, loadApDocCategories } = useMeta();

  // Page-local filter state; the current page of rows + total + counts come from the shared hook.
  const [search, setSearch] = useState('');
  const [searchQ, setSearchQ] = useState(''); // debounced value sent to the server
  const [category, setCategory] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [invTab, setInvTab] = useState<'all' | 'AP' | 'II'>('all');

  // Reset filters when the module tab changes (page reset is handled by the hook when deps change).
  useEffect(() => {
    if (isInvoice) loadApDocCategories();
    setCategory('');
    setInvTab('all');
    setSearch('');
    setSearchQ('');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mod]);

  // Debounce the search box so typing doesn't hit the server on every keystroke.
  useEffect(() => {
    const t = setTimeout(() => setSearchQ(search), 350);
    return () => clearTimeout(t);
  }, [search]);

  const { rows, total, data, page, pageSize, setPage, setPageSize, reload } = usePagedList<InboxRow, DocumentsPage>(
    (pg, ps) =>
      guard(() =>
        listDocumentsPaged({
          module: mod,
          apDocCategory: category,
          search: searchQ,
          dateFrom: from,
          dateTo: to,
          invModule: invTab === 'all' ? '' : invTab,
          page: pg,
          pageSize: ps,
        }),
      ),
    [mod, category, from, to, invTab, searchQ],
  );

  const invCounts = data?.counts ?? { all: total, AP: 0, II: 0 };
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
  // Sales Orders hide only the Type column now; Model OCR is shown for every module
  const colCount = 12 + (!mod ? 1 : 0) + (isInvoice ? 1 : 0) - (isSalesOrder ? 1 : 0);

  return (
    <div className="card">
      <div className="card-h register-toolbar">
        <h2>
          {inboxTitle(mod)} ({total})
        </h2>
        <div className="register-filters">
          <input
            className="register-search"
            type="search"
            aria-label="Search documents"
            placeholder="Search partner / document no.…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <div className="register-date-filter">
            <span className="hint register-date-label">Date:</span>
            <DateRange from={from} to={to} setFrom={setFrom} setTo={setTo} />
          </div>
          {isInvoice && (
            <select value={category} onChange={(e) => setCategory(e.target.value)}>
              <option value="">All Document Types</option>
              {(apDocCategories ?? []).map((c) => (
                <option key={c.id} value={c.id}>
                  {c.label}
                </option>
              ))}
            </select>
          )}
          <button className="btn sm" onClick={reload}>
            <i className="fa-solid fa-arrow-rotate-right" /> Refresh
          </button>
        </div>
      </div>
      <div className="card-b">
        {isInvoice && (
          <div className="row" style={{ gap: 8, marginBottom: 12, flexWrap: 'wrap' }}>
            {(
              [
                ['all', 'All', invCounts.all],
                ['AP', 'Supplier Invoice · With PO', invCounts.AP],
                ['II', 'Incoming Invoice · Without PO', invCounts.II],
              ] as const
            ).map(([key, label, count]) => (
              <button
                key={key}
                className={'btn sm' + (invTab === key ? '' : ' ghost')}
                onClick={() => setInvTab(key)}
              >
                {label} ({count})
              </button>
            ))}
          </div>
        )}
        <div className={`tw register-table-wrap paged-table paged-table-register page-size-${pageSize}`}>
          <table className="reg">
            <thead>
              <tr>
                <th className="reg-col-id">#</th>
                {!mod && <th className="reg-col-module">Module</th>}
                <th className="reg-col-file">File</th>
                <th className="reg-col-docno">{invColHead}</th>
                {!isSalesOrder && <th className="reg-col-type">Type</th>}
                <th className="reg-col-date">PO Date</th>
                <th className="reg-col-supplier">Supplier</th>
                <th className="reg-col-total" style={{ textAlign: 'right' }}>Total</th>
                {isInvoice && <th className="reg-col-category">Document Type</th>}
                <th className="reg-col-status">Status</th>
                <th className="reg-col-ocr">Model OCR</th>
                <th className="reg-col-sap">SAP Doc</th>
                <th className="reg-col-created">Create Date</th>
                <th className="reg-col-actions" aria-label="Actions" />
              </tr>
            </thead>
            <tbody>
              {!rows ? (
                <tr>
                  <td colSpan={colCount} className="empty">
                    Loading…
                  </td>
                </tr>
              ) : rows.length ? (
                rows.map((r) => {
                  const sb = statusBadge(r.Status);
                  return (
                    <tr
                      key={r.DocId}
                      className="reg-row"
                      style={{ cursor: 'pointer' }}
                      onClick={() => navigate('/doc/' + r.DocId)}
                    >
                      <td className="reg-col-id">{r.DocId}</td>
                      {!mod && (
                        <td className="reg-col-module">
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
                      <td className="reg-col-file" title={r.FileName || ''}>{r.FileName || ''}</td>
                      <td className="reg-col-docno" title={r.DocNo || ''}>{r.DocNo || ''}</td>
                      {!isSalesOrder && (
                        <td className="reg-col-type">
                          {(() => {
                            const fmtd = docFormat(r.Module);
                            return <span className={'badge ' + fmtd.cls}>{fmtd.label}</span>;
                          })()}
                        </td>
                      )}
                      <td className="reg-col-date">{r.DocDate || ''}</td>
                      <td className="reg-col-supplier" title={r.PartnerName || ''}>{r.PartnerName || ''}</td>
                      <td className="reg-col-total" style={{ textAlign: 'right' }}>{fmt(r.TotalAmount)}</td>
                      {isInvoice && (
                        <td className="reg-col-category">
                          {r.ApDocCategory ? catLabel(r.ApDocCategory) : <span className="hint">—</span>}
                        </td>
                      )}
                      <td className="reg-col-status">
                        <span className={'badge ' + sb.cls}>{sb.label}</span>{' '}
                        {r.OcrConfidence != null && (
                          <span className="hint">{Math.round(r.OcrConfidence * 100)}%</span>
                        )}
                      </td>
                      <td className="reg-col-ocr">
                        <span className="hint">
                          {OCR_PROVIDER_SHORT[r.OcrProvider ?? ''] || r.OcrProvider || '—'}
                        </span>
                      </td>
                      <td className="reg-col-sap">{r.SapDocNo || ''}</td>
                      <td className="hint reg-col-created">{dt(r.CreatedAt)}</td>
                      <td className="reg-col-actions" style={{ whiteSpace: 'nowrap' }}>
                        {r.Status === 'POSTED' ? (
                          // Posted docs can't be deleted (already sent to SAP/Zoho), so instead of
                          // an empty cell show a "view" button that opens the document's detail
                          // page (same destination as clicking the row).
                          <button
                            className="btn sm ghost"
                            title="View"
                            aria-label="View"
                            onClick={(e) => {
                              e.stopPropagation();
                              navigate('/doc/' + r.DocId);
                            }}
                          >
                            <i className="fa-solid fa-eye" />
                          </button>
                        ) : (
                          <button
                            className="btn sm ghost"
                            title="Delete"
                            aria-label="Delete"
                            onClick={(e) => {
                              e.stopPropagation();
                              delDoc(r.DocId);
                            }}
                          >
                            <i className="fa-solid fa-trash-can" />
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })
              ) : (
                <tr>
                  <td colSpan={colCount} className="empty">
                    No documents found
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
