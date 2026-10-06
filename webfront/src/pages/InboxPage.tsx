import { useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useAppState } from '../state/AppState';
import { useMeta } from '../state/MetaContext';
import { deleteDocument, listDocumentsPaged, type DocumentsPage, type InboxRow } from '../api/documents';
import { dt, fileRetention, fmt, moduleLabel, statusBadge } from '../utils/format';
import { OCR_PROVIDER_SHORT } from '../constants/fields';
import type { ModuleCode } from '../api/types';
import Pager, { DateRange } from '../components/Pager';
import ConfirmModal from '../components/ConfirmModal';
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

  // Empty state: tell "nothing here yet" apart from "your filters hide everything".
  const isEmpty = rows != null && rows.length === 0;
  const hasFilters = !!(search.trim() || from || to || category || invTab !== 'all');
  const clearFilters = () => {
    setSearch(''); setSearchQ(''); setFrom(''); setTo(''); setCategory(''); setInvTab('all');
  };
  const importPath = isSalesOrder ? '/import/SO' : isInvoice ? '/import/AP' : null;
  const emptyNoun = isSalesOrder ? 'sales orders' : isInvoice ? 'invoices' : 'documents';
  const catLabel = (id: string | null) =>
    (apDocCategories ?? []).find((c) => c.id === id)?.label || id || '';

  // ---- Delete: one row (trash icon) or many (ticked rows), confirmed in an in-app popup ----
  // Posted documents can't be deleted (already in SAP/Zoho), so they can't be ticked either.
  // The selection survives paging (like Master Mapping) and is cleared when the list changes.
  const [selected, setSelected] = useState<Map<number, InboxRow>>(() => new Map());
  const [confirmRows, setConfirmRows] = useState<InboxRow[] | null>(null);
  const [deleting, setDeleting] = useState(false);
  useEffect(() => { setSelected(new Map()); }, [mod, category, from, to, invTab, searchQ]);

  const deletable = (r: InboxRow) => r.Status !== 'POSTED' && r.Status !== 'PARTIAL';
  const pageRows = (rows ?? []).filter(deletable);
  const allOnPage = pageRows.length > 0 && pageRows.every((r) => selected.has(r.DocId));
  const toggleRow = (r: InboxRow) =>
    setSelected((prev) => {
      const next = new Map(prev);
      if (next.has(r.DocId)) next.delete(r.DocId); else next.set(r.DocId, r);
      return next;
    });
  const toggleAll = () =>
    setSelected((prev) => {
      const next = new Map(prev);
      if (allOnPage) pageRows.forEach((r) => next.delete(r.DocId));
      else pageRows.forEach((r) => next.set(r.DocId, r));
      return next;
    });

  const delDoc = (r: InboxRow) => setConfirmRows([r]);
  const doDelete = async () => {
    const list = confirmRows ?? [];
    if (list.length === 0) return;
    setDeleting(true);
    let ok = 0;
    const failed: number[] = [];
    for (const r of list) {
      try { await deleteDocument(r.DocId, USER); ok++; }
      catch { failed.push(r.DocId); }
    }
    setDeleting(false);
    setConfirmRows(null);
    setSelected((prev) => {
      const next = new Map(prev);
      list.forEach((r) => { if (!failed.includes(r.DocId)) next.delete(r.DocId); });
      return next;
    });
    if (failed.length === 0) showToast(ok === 1 ? 'Document deleted' : `Deleted ${ok} documents`, 'success');
    else showToast(`Deleted ${ok}, could not delete ${failed.length}: #${failed.join(', #')}`, 'error');
    reload();
  };
  const docLabel = (r: InboxRow) =>
    `#${r.DocId}` + (r.DocNo ? ` · ${r.DocNo}` : '') + (r.PartnerName ? ` · ${r.PartnerName}` : '');

  const invColHead = ['AP', 'II', 'PODP'].includes(mod ?? '') ? 'Invoice Number' : 'PO Number';
  // Sales Orders hide only the Type column now; Model OCR is shown for every module
  const colCount = 14 + (!mod ? 1 : 0) + (isInvoice ? 1 : 0) - (isSalesOrder ? 1 : 0);

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
        {selected.size > 0 && (
          <div className="register-bulkbar">
            <span className="hint">{selected.size} selected</span>
            <button className="btn sm danger" onClick={() => setConfirmRows([...selected.values()])}>
              <i className="fa-solid fa-trash" /> Delete selected ({selected.size})
            </button>
            <button className="btn sm ghost" onClick={() => setSelected(new Map())}>Clear selection</button>
          </div>
        )}
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
        <div className={`tw register-table-wrap paged-table paged-table-register page-size-${pageSize}` + (isEmpty ? ' is-empty' : '')}>
          <table className="reg">
            <thead>
              <tr>
                <th className="reg-col-select">
                  <input type="checkbox" aria-label="Select all deletable documents on this page"
                    checked={allOnPage} onChange={toggleAll} disabled={pageRows.length === 0} />
                </th>
                <th className="reg-col-id">#</th>
                {!mod && <th className="reg-col-module">Module</th>}
                <th className="reg-col-file">File</th>
                <th className="reg-col-docno">{invColHead}</th>
                {!isSalesOrder && <th className="reg-col-type">Type</th>}
                <th className="reg-col-date">PO Date</th>
                <th className="reg-col-supplier">{isSalesOrder ? 'Customer' : 'Supplier'}</th>
                <th className="reg-col-total" style={{ textAlign: 'right' }}>Total</th>
                {isInvoice && <th className="reg-col-category">Document Type</th>}
                <th className="reg-col-status">Status</th>
                <th className="reg-col-ocr">Model OCR</th>
                <th className="reg-col-sap">Posted Doc No.</th>
                <th className="reg-col-created">Create Date</th>
                <th className="reg-col-by">Uploaded By</th>
                <th className="reg-col-actions" aria-label="Actions" />
              </tr>
            </thead>
            <tbody>
              {!rows ? (
                Array.from({ length: Math.min(pageSize, 6) }, (_, i) => (
                  <tr key={'sk' + i} className="reg-skeleton" aria-hidden="true">
                    <td colSpan={colCount}><span className="reg-skel-bar" /></td>
                  </tr>
                ))
              ) : rows.length ? (
                rows.map((r) => {
                  const sb = statusBadge(r.Status);
                  return (
                    <tr
                      key={r.DocId}
                      className={'reg-row' + (selected.has(r.DocId) ? ' selected' : '')}
                      style={{ cursor: 'pointer' }}
                      onClick={() => navigate('/doc/' + r.DocId)}
                    >
                      <td className="reg-col-select" onClick={(e) => e.stopPropagation()}>
                        {deletable(r) ? (
                          <input type="checkbox" aria-label={'Select document ' + r.DocId}
                            checked={selected.has(r.DocId)} onChange={() => toggleRow(r)} />
                        ) : (
                          <input type="checkbox" aria-label="Posted documents cannot be deleted"
                            title="Posted documents cannot be deleted" disabled />
                        )}
                      </td>
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
                        {(() => {
                          const fr = fileRetention(
                            { status: r.Status, hasFile: r.HasFile, fileExpiredAt: r.FileExpiredAt, updatedAt: r.UpdatedAt, createdAt: r.CreatedAt },
                            (data?.retentionModules ?? ['SO']).includes(r.Module) ? data?.retentionHours : 0,
                          );
                          if (fr.kind === 'expired')
                            return (
                              <span className="badge b-fail" title={`Original file removed ${dt(fr.expiredAt)} (not posted within the retention period)`}>
                                <i className="fa-solid fa-file-circle-xmark" /> File expired
                              </span>
                            );
                          if (fr.kind === 'soon')
                            return (
                              <span className="badge b-warn" title={`The original file will be removed around ${fr.expiresAt?.toLocaleString('en-GB')} unless the document is posted or edited`}>
                                <i className="fa-solid fa-hourglass-half" /> File removed in {fr.left}
                              </span>
                            );
                          return null;
                        })()}{' '}
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
                      <td
                        className="reg-col-by"
                        title={
                          `Uploaded by ${r.CreatedBy || '—'} · ${dt(r.CreatedAt)}` +
                          (r.PostedBy ? `\nPosted by ${r.PostedBy} · ${dt(r.PostedAt ?? null)}` : '')
                        }
                      >
                        {r.CreatedBy || <span className="hint">—</span>}
                      </td>
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
                              delDoc(r);
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
                <tr className="reg-empty-row">
                  <td colSpan={colCount}>
                    <div className="reg-empty">
                      <div className="reg-empty-icon" aria-hidden="true">
                        <i className={hasFilters ? 'fa-solid fa-magnifying-glass' : 'fa-regular fa-folder-open'} />
                      </div>
                      <div className="reg-empty-title">
                        {hasFilters ? 'No matching documents' : `No ${emptyNoun} yet`}
                      </div>
                      <div className="reg-empty-text">
                        {hasFilters
                          ? 'Try a different search term or date range.'
                          : importPath
                            ? `Import a document to start — it will appear here once it has been read.`
                            : 'Documents you import will appear here.'}
                      </div>
                      {hasFilters ? (
                        <button className="btn sm" onClick={clearFilters}>
                          <i className="fa-solid fa-xmark" /> Clear filters
                        </button>
                      ) : importPath ? (
                        <button className="btn sm primary" onClick={() => navigate(importPath)}>
                          <i className="fa-solid fa-file-import" /> {isSalesOrder ? 'Import Sales Order' : 'Import Invoice'}
                        </button>
                      ) : null}
                    </div>
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <Pager page={page} setPage={setPage} pageSize={pageSize} setPageSize={setPageSize} total={total} />
      </div>

      <ConfirmModal
        open={confirmRows != null}
        title={confirmRows && confirmRows.length > 1 ? `Delete ${confirmRows.length} documents` : 'Delete document'}
        message={
          confirmRows && confirmRows.length === 1 ? (
            <>Delete document <b>{docLabel(confirmRows[0])}</b>?<br /><span className="hint">This cannot be undone.</span></>
          ) : (
            <>
              Delete these <b>{confirmRows?.length ?? 0}</b> documents? <span className="hint">This cannot be undone.</span>
              <ul className="confirm-list">
                {(confirmRows ?? []).map((r) => <li key={r.DocId}>{docLabel(r)}</li>)}
              </ul>
            </>
          )
        }
        confirmLabel={confirmRows && confirmRows.length > 1 ? `Delete ${confirmRows.length}` : 'Delete'}
        busy={deleting}
        onConfirm={() => void doDelete()}
        onCancel={() => setConfirmRows(null)}
      />
    </div>
  );
}
