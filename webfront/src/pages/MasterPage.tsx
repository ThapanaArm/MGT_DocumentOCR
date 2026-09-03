import { useEffect, useMemo, useState } from 'react';
import { useAppState } from '../state/AppState';
import { useMeta } from '../state/MetaContext';
import { deleteMaster } from '../api/masters';
import { MASTER_DEF, MASTER_GROUPS, MASTER_NOTE } from '../constants/fields';
import { num } from '../utils/format';
import MasterEditModal, { type MasterEditState } from '../components/master/MasterEditModal';

/* Ports renderMaster()/renderMasterLocal()/editRow()/delRow(). */
export default function MasterPage() {
  const { guard, showToast } = useAppState();
  const { masters, loadMasters } = useMeta();

  const [group, setGroup] = useState('vendor');
  const [tab, setTab] = useState('vendors');
  const [search, setSearch] = useState('');
  const [edit, setEdit] = useState<MasterEditState | null>(null);

  useEffect(() => {
    loadMasters(true);
  }, [loadMasters]);

  const grp = MASTER_GROUPS.find((g) => g.key === group) || MASTER_GROUPS[0];
  const activeTab = grp.tabs.includes(tab) ? tab : grp.tabs[0];
  const def = MASTER_DEF[activeTab];

  const rows = useMemo(() => {
    if (!masters) return [];
    const q = search.trim().toLowerCase();
    const all = masters[activeTab] || [];
    return !q
      ? all
      : all.filter((r) => def.cols.some((c) => String(r[c.k] ?? '').toLowerCase().includes(q)));
  }, [masters, activeTab, search, def]);

  if (!masters) return <div className="card"><div className="empty">Loading…</div></div>;

  const count = (k: string) => (masters[k] || []).length;

  const cell = (r: Record<string, any>, c: (typeof def.cols)[number]) => {
    if (c.sap)
      return r[c.k] ? (
        <b className="sapcode">{r[c.k]}</b>
      ) : (
        <span className="badge b-fail">✗ Not specified</span>
      );
    if (activeTab === 'uoms' && c.k === 'MaterialCode' && !r[c.k])
      return <span className="badge b-idle">All materials (global rule)</span>;
    if (activeTab === 'uoms' && c.k === 'Factor')
      return <b>{num(r[c.k]).toLocaleString('en-US', { maximumFractionDigits: 6 })}</b>;
    return r[c.k];
  };

  const delRow = (key: string) => {
    if (!window.confirm('Delete this record?')) return;
    guard(async () => {
      await deleteMaster(activeTab, key);
      await loadMasters(true);
      showToast('Record deleted');
    });
  };

  return (
    <div className="card">
      <div className="card-h">
        <h2>Master Mapping — Grouped by Matching Point</h2>
        <div className="sp" />
        <span className="hint">
          SQL Server · schema <code>ocr</code>
        </span>
      </div>

      <div className="tabs">
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
              }}
            >
              {g.label}
              {g.mod !== 'ALL' && <span className="badge b-idle"> {g.mod}</span>}{' '}
              <span className="muted">({n})</span>
            </button>
          );
        })}
      </div>

      <div className="card-b">
        <div className="result ok" style={{ marginBottom: 16, padding: '12px 16px' }}>
          <b>{grp.label}</b> — <span style={{ fontWeight: 400 }}>{grp.note}</span>
        </div>

        {grp.tabs.length >= 2 && (
          <div className="row" style={{ gap: 8, marginBottom: 16 }}>
            {grp.tabs.map((k) => (
              <button
                key={k}
                className={'btn sm ' + (activeTab === k ? 'primary' : '')}
                onClick={() => {
                  setTab(k);
                  setSearch('');
                }}
              >
                {MASTER_DEF[k].label} <span style={{ opacity: 0.7 }}>{count(k)}</span>
              </button>
            ))}
          </div>
        )}

        <div className="hint" style={{ marginBottom: 12 }}>
          🔑 The <b className="sapcode">SAP Code</b> column is the value the system actually posts to S/4HANA —
          if left blank, Mapping will fail and the document cannot be submitted
        </div>

        <div className="row" style={{ marginBottom: 14 }}>
          <button
            className="btn primary sm"
            onClick={() => setEdit({ tab: activeTab, rowKey: null })}
          >
            + Add Record
          </button>
          <input
            type="text"
            placeholder="Search..."
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            style={{ maxWidth: 220 }}
          />
          <span className="hint" dangerouslySetInnerHTML={{ __html: MASTER_NOTE[activeTab] }} />
        </div>

        <div className="tw">
          <table>
            <thead>
              <tr>
                {def.cols.map((c) => (
                  <th key={c.k}>{c.l}</th>
                ))}
                <th style={{ width: 130 }} />
              </tr>
            </thead>
            <tbody>
              {rows.length ? (
                rows.map((r, i) => (
                  <tr key={i}>
                    {def.cols.map((c) => (
                      <td key={c.k}>{cell(r, c)}</td>
                    ))}
                    <td style={{ whiteSpace: 'nowrap' }}>
                      <button
                        className="btn sm"
                        onClick={() => setEdit({ tab: activeTab, rowKey: r[def.key] })}
                      >
                        Edit
                      </button>{' '}
                      <button className="btn sm ghost" onClick={() => delRow(String(r[def.key]))}>
                        Delete
                      </button>
                    </td>
                  </tr>
                ))
              ) : (
                <tr>
                  <td colSpan={def.cols.length + 1} className="empty">
                    {search ? 'No matching records found' : 'No records yet'}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      <MasterEditModal
        state={edit}
        masters={masters}
        onClose={() => setEdit(null)}
        afterSave={async () => {
          await loadMasters(true);
        }}
      />
    </div>
  );
}
