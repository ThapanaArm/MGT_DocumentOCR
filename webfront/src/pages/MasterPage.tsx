import { useEffect, useMemo, useState } from 'react';
import { useOutletContext } from 'react-router-dom';
import { useAppState } from '../state/AppState';
import { useMeta } from '../state/MetaContext';
import { deleteMaster, getMasters, setMasterActive, type MastersData } from '../api/masters';
import { MASTER_DEF, MASTER_GROUPS } from '../constants/fields';
import { num } from '../utils/format';
import MasterEditModal, { type MasterEditState } from '../components/master/MasterEditModal';
import ConfirmModal from '../components/ConfirmModal';
import type { Me } from '../api/me';
import Pager, { paginate } from '../components/Pager';

/* Ports renderMaster()/renderMasterLocal()/editRow()/delRow(). */
export default function MasterPage() {
  const { guard, showToast } = useAppState();
  const { loadMasters } = useMeta();
  const [masters, setMasters] = useState<MastersData | null>(null);
  const [loadError, setLoadError] = useState('');
  const reload = async () => { setMasters(await getMasters(true)); await loadMasters(true); };
  // Same company split as DocumentPage: MGT -> Zoho CRM, others (Green Leaf) -> SAP, for the
  // quick-add "Found in ..." lookup inside MasterEditModal.
  const { me } = useOutletContext<{ me: Me | null }>();
  const isMgt = me?.primaryCompany?.companyCode === 'MGT';

  const [group, setGroup] = useState('customer');
  const [tab, setTab] = useState('customers');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [edit, setEdit] = useState<MasterEditState | null>(null);
  const [toggling, setToggling] = useState<Set<string>>(() => new Set());
  // Keys of the rows ticked for bulk delete (def.key value, stringified).
  const [selected, setSelected] = useState<Set<string>>(() => new Set());
  // Bulk-delete confirmation popup (ConfirmModal) — declared up here, before the early "Loading…" return.
  const [confirmDel, setConfirmDel] = useState(false);
  const [deleting, setDeleting] = useState(false);

  useEffect(() => {
    let cancelled = false;
    getMasters(true).then((data) => { if (!cancelled) setMasters(data); })
      .catch((e) => { if (!cancelled) setLoadError(e.message); });
    return () => { cancelled = true; };
  }, []);

  const grp = MASTER_GROUPS.find((g) => g.key === group) || MASTER_GROUPS[0];
  const activeTab = grp.tabs.includes(tab) ? tab : grp.tabs[0];
  const def = MASTER_DEF[activeTab];
  // uoms headings read the same for MGT and GLC now (Document Unit / Order Unit / Factor), so the
  // old per-company "Zoho Unit" relabel is gone — headings come straight from MASTER_DEF.
  const columnLabel = (c: (typeof def.cols)[number]) => c.l;

  const rows = useMemo(() => {
    if (!masters) return [];
    const q = search.trim().toLowerCase();
    const all = masters[activeTab] || [];
    return !q
      ? all
      : all.filter((r) => def.cols.some((c) => String(r[c.k] ?? '').toLowerCase().includes(q)));
  }, [masters, activeTab, search, def]);
  const pagedRows = useMemo(() => paginate(rows, page, pageSize), [rows, page, pageSize]);

  // Selections don't carry across tabs — clear when the active tab changes.
  useEffect(() => { setSelected(new Set()); }, [activeTab]);

  if (!masters) return <div className="card"><div className="empty">{loadError || 'Loading…'}</div></div>;

  const count = (k: string) => (masters[k] || []).length;

  const toggleActive = async (r: Record<string, any>, activeKey: string) => {
    const key = String(r[def.key]);
    const tabAtClick = activeTab;
    const busyKey = `${tabAtClick}:${key}`;
    const nextValue = Number(r[activeKey]) ? 0 : 1;
    setToggling((prev) => new Set(prev).add(busyKey));
    // Dedicated status endpoint: only the active flag is written, so the rest of the row is
    // never re-sent or re-validated (a full-row PUT failed on rows with a blank required field).
    const ok = await guard(async () => {
      await setMasterActive(tabAtClick, key, !!nextValue);
      return true;
    });
    setToggling((prev) => {
      const next = new Set(prev);
      next.delete(busyKey);
      return next;
    });
    if (!ok) return;
    // Update the row in place (keeps the page/sort position), then refresh the shared active-only
    // masters cache so the Sales Order mapping screens stop/start using this record right away.
    setMasters((prev) => prev && {
      ...prev,
      [tabAtClick]: (prev[tabAtClick] || []).map((row) =>
        String(row[def.key]) === key ? { ...row, [activeKey]: nextValue } : row),
    });
    void loadMasters(true);
    showToast(nextValue ? 'Record activated' : 'Record deactivated');
  };

  const cell = (r: Record<string, any>, c: (typeof def.cols)[number]) => {
    if (c.source === 'system') {
      const active = !!Number(r[c.k]);
      const busy = toggling.has(`${activeTab}:${String(r[def.key])}`);
      return (
        <button
          type="button"
          className={`master-active-switch ${active ? 'on' : ''}`}
          role="switch"
          aria-checked={active}
          aria-label={`${active ? 'Deactivate' : 'Activate'} record`}
          disabled={busy}
          onClick={(e) => { e.stopPropagation(); void toggleActive(r, c.k); }}
        >
          <span className="master-switch-track"><span className="master-switch-knob" /></span>
        </button>
      );
    }
    if (c.sap)
      return r[c.k] ? (
        <b className="sapcode">{r[c.k]}</b>
      ) : (
        <span className="badge b-fail"><i className="fa-solid fa-xmark" /> Not specified</span>
      );
    if (c.k === 'SalesOrg')
      return r[c.k]
        ? <span className="master-org">{r[c.k]} <span className="hint">{String(r[c.k]) === '1000' ? 'MGT' : String(r[c.k]) === '2000' ? 'GLC' : ''}</span></span>
        : activeTab === 'uoms' ? <span className="badge b-idle">All companies</span> : <span className="hint">—</span>;
    if (activeTab === 'uoms' && c.k === 'MaterialCode' && !r[c.k])
      return <span className="badge b-idle">All materials (global rule)</span>;
    if (activeTab === 'uoms' && c.k === 'Factor')
      return <b>{num(r[c.k]).toLocaleString('en-US', { maximumFractionDigits: 6 })}</b>;
    const v = r[c.k];
    if (v == null || String(v).trim() === '') return <span className="hint">—</span>;
    // Long text (names, addresses) wraps to at most 2 lines; the full value is in the tooltip.
    return <span className="master-cell-text" title={String(v)}>{String(v)}</span>;
  };

  // Table layout. Columns that are empty on EVERY record of this tab (e.g. Branch / Distribution
  // Channel / Division / Currency / Payment Terms that nobody filled) are hidden so the table isn't
  // half blank columns; they come back as soon as one record has a value. Required, SAP-code and
  // status columns always show. Each column gets a width by what it holds instead of the browser's
  // equal split (the paged table uses table-layout: fixed).
  const visibleCols = rows.length === 0 ? def.cols : def.cols.filter((c) =>
    c.required || c.sap || c.source === 'system' || c.k === def.key || c.k === 'SalesOrg'
    || rows.some((r) => r[c.k] != null && String(r[c.k]).trim() !== ''));
  const colWidth = (k: string): number => {
    if (['IsActive', 'Isactive'].includes(k)) return 96;
    if (['ShipToAddress', 'Note', 'Address'].includes(k)) return 240;
    if (['CompanyName', 'CompanyNameSAP', 'ShipToName', 'MaterialCodeName', 'Description', 'VendorName', 'ExtDesc'].includes(k)) return 180;
    if (k === 'TaxId') return 150;
    if (['SalesOrg', 'Branch', 'DistChannel', 'Division', 'Currency', 'Uom', 'ExtUom', 'SapUom', 'SapUomIso', 'Factor', 'Plant', 'MatGroup'].includes(k)) return 120;
    if (k === 'PaymentTerms') return 140;
    return 160; // codes
  };
  const rowKeys = pagedRows.map((r) => String(r[def.key]));
  const allSelected = rowKeys.length > 0 && rowKeys.every((k) => selected.has(k));
  const toggleRow = (key: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });
  const toggleAll = () =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (rowKeys.every((k) => next.has(k))) rowKeys.forEach((k) => next.delete(k));
      else rowKeys.forEach((k) => next.add(k));
      return next;
    });
  const delSelected = () => { if (selected.size > 0) setConfirmDel(true); };
  const doDelSelected = async () => {
    const keys = [...selected];
    setDeleting(true);
    await guard(async () => {
      for (const k of keys) await deleteMaster(activeTab, k);
      await reload();
      setSelected(new Set());
      showToast(`Deleted ${keys.length} record(s)`, 'success');
    });
    setDeleting(false);
    setConfirmDel(false);
  };

  return (
    <div className="card master-card">
      <div className="tabs master-group-tabs">
        {MASTER_GROUPS.map((g) => {
          const n = g.tabs.reduce((a, k) => a + count(k), 0);
          return (
            <button
              key={g.key}
              className={g.key === grp.key ? 'on' : ''}
              onClick={() => {
                setGroup(g.key);
                setTab(g.tabs[0]);
                setSearch('');
                setPage(1);
              }}
            >
              {g.label}
              {g.mod !== 'ALL' && <span className="badge b-idle"> {g.mod}</span>}{' '}
              <span className="muted">({n})</span>
            </button>
          );
        })}
      </div>

      <div className="card-b master-body">
        <div className="master-controls">
          {grp.tabs.length >= 2 && (
            <div className="master-subtabs" role="tablist" aria-label="Master data type">
              {grp.tabs.map((k) => (
                <button
                  key={k}
                  className={activeTab === k ? 'on' : ''}
                  role="tab"
                  aria-selected={activeTab === k}
                  onClick={() => {
                    setTab(k);
                    setSearch('');
                    setPage(1);
                  }}
                >
                  {MASTER_DEF[k].label} <span>{count(k)}</span>
                </button>
              ))}
            </div>
          )}
          {grp.tabs.length < 2 && (
            <div className="master-record-summary">
              <b>{def.label}</b>
              <span>{rows.length.toLocaleString()} records</span>
            </div>
          )}

          <div className="master-toolbar">
            <label className="master-search">
              <i className="fa-solid fa-magnifying-glass" aria-hidden="true" />
              <input
                type="search"
                aria-label="Search master records"
                placeholder="Search records…"
                value={search}
                onChange={(e) => { setSearch(e.target.value); setPage(1); }}
              />
            </label>
            <button
              className="btn primary sm"
              onClick={() => setEdit({ tab: activeTab, rowKey: null })}
            >
              <i className="fa-solid fa-plus" /> Add Record
            </button>
            {selected.size > 0 && (
              <button className="btn sm ghost" style={{ color: 'var(--red)' }} onClick={delSelected}>
                <i className="fa-solid fa-trash" /> Delete selected ({selected.size})
              </button>
            )}
          </div>
        </div>

        <div className={`tw master-table-wrap paged-table paged-table-master page-size-${pageSize}`}>
          <table className="master-table" style={{ minWidth: 44 + visibleCols.reduce((a, c) => a + colWidth(c.k), 0) }}>
            <colgroup>
              <col style={{ width: 44 }} />
              {visibleCols.map((c) => <col key={c.k} style={{ width: colWidth(c.k) }} />)}
            </colgroup>
            <thead>
              <tr>
                <th style={{ textAlign: 'center' }}>
                  <input type="checkbox" aria-label="Select all rows on this page" checked={allSelected}
                    onChange={toggleAll} disabled={pagedRows.length === 0} />
                </th>
                {visibleCols.map((c) => (
                  <th key={c.k} className={`master-col-${c.k.toLowerCase()}`} title={columnLabel(c)}>{columnLabel(c)}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {pagedRows.length ? (
                pagedRows.map((r) => {
                  const key = String(r[def.key]);
                  return (
                  <tr
                    key={key}
                    className={`master-data-row${selected.has(key) ? ' selected' : ''}`}
                    tabIndex={0}
                    onClick={() => setEdit({ tab: activeTab, rowKey: r[def.key] })}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        setEdit({ tab: activeTab, rowKey: r[def.key] });
                      }
                    }}
                  >
                    <td style={{ textAlign: 'center' }}>
                      <input type="checkbox" aria-label="Select row" checked={selected.has(key)}
                        onClick={(e) => e.stopPropagation()} onChange={() => toggleRow(key)} />
                    </td>
                    {visibleCols.map((c) => (
                      <td key={c.k} className={`master-col-${c.k.toLowerCase()}`}>{cell(r, c)}</td>
                    ))}
                  </tr>
                  );
                })
              ) : (
                <tr>
                  <td colSpan={visibleCols.length + 1} className="empty">
                    {search ? 'No matching records found' : 'No records yet'}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        {rows.length > pageSize && (
          <Pager page={page} setPage={setPage} pageSize={pageSize} setPageSize={setPageSize} total={rows.length} />
        )}
      </div>

      <MasterEditModal
        state={edit}
        masters={masters}
        onClose={() => setEdit(null)}
        afterSave={async () => {
          await reload();
        }}
        isMgt={isMgt}
      />
      <ConfirmModal
        open={confirmDel}
        title={`Delete ${selected.size} record(s)`}
        message={<>Delete the <b>{selected.size}</b> selected record(s) from this table?<br /><span className="hint">This cannot be undone.</span></>}
        confirmLabel={`Delete ${selected.size}`}
        busy={deleting}
        onConfirm={() => void doDelSelected()}
        onCancel={() => setConfirmDel(false)}
      />
    </div>
  );
}
