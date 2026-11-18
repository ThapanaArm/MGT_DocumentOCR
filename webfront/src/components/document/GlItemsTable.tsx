import { useState } from 'react';
import { fmt, num } from '../../utils/format';
import GlAccountHelp from './GlAccountHelp';
import { TAX_CODES } from '../../constants/fields';
import { GL_ACCOUNT_NAMES } from '../../constants/glAccounts';

/* Ports the G/L Account Items table (header.glItems) from docHtml(). */
export type GlItem = Record<string, any>;

/* Accounts AP keys by hand on a supplier invoice (PRD 5100000710 S.N.R.):
   Cr 11710000 Prepayment-Sundry for what was paid in advance (WHT, Customs Fee). */
export const GL_ACCOUNTS: Array<[string, string]> = [
  ['11710000', 'Prepayment-Sundry'],
  ['11730000', 'Deferred Input tax'],
  ['11720000', 'Input tax'],
  ['52110001', 'Purchase Variance'],
];
/** Short Text of a G/L account, as MIRO shows it (leading zeros ignored). */
export const glName = (acct: unknown) =>
  GL_ACCOUNT_NAMES[String(acct ?? '').trim().replace(/^0+(?=\d{8})/, '')] || '';
// The accounts AP uses most first, then the whole chart of accounts.
const GL_PICKER: Array<[string, string]> = [
  ...GL_ACCOUNTS,
  ...Object.entries(GL_ACCOUNT_NAMES).filter(([a]) => !GL_ACCOUNTS.some(([c]) => c === a)),
];
const GL_PRESETS: Array<{ label: string; row: GlItem }> = [
  { label: 'Prepayment', row: { glAccount: '11710000', drCr: 'C', taxCode: 'VX' } },
];

export default function GlItemsTable({
  module,
  items,
  posted,
  onEdit,
  onAdd,
  onDelete,
  bare,
  vendorOptions,
  mainVendor,
  title,
}: {
  module: string;
  items: GlItem[];
  posted: boolean;
  onEdit: (i: number, key: string, value: string) => void;
  onAdd: (preset?: GlItem) => void;
  onDelete: (i: number) => void;
  bare?: boolean; // render without the outer .card wrapper (for use inside a tabbed card)
  /** The SAP documents of this bundle, by supplier: [code, label]. With two or more, each row says
   *  which supplier's document it goes on (blank = the main supplier's, as the backend does). */
  vendorOptions?: Array<[string, string]>;
  mainVendor?: string;
  /** Heading override, e.g. which supplier's SAP document the rows shown belong to. */
  title?: string;
}) {
  // Row whose G/L acct value help is open (SAP's F4 dialog).
  const [helpFor, setHelpFor] = useState<number | null>(null);
  const addBtn = !posted ? (
    <span className="row" style={{ gap: 6 }}>
      {module !== 'II' &&
        GL_PRESETS.map((p) => (
          <button key={p.label} className="btn sm ghost" onClick={() => onAdd({ ...p.row })}>
            + {p.label}
          </button>
        ))}
      <button className="btn sm" onClick={() => onAdd()}>
        + Add Item
      </button>
    </span>
  ) : null;

  return (
    <div className={bare ? undefined : 'card'}>
      {!bare && (
        <div className="card-h">
          <h2>
            {title ?? (module === 'II' ? 'Line Items' : 'G/L Account Items')} ({items.length})
          </h2>
          <div className="sp" />
          {addBtn}
        </div>
      )}
      <div className="card-b">
        {bare && addBtn && (
          <div className="row" style={{ justifyContent: 'flex-end', marginBottom: 12 }}>{addBtn}</div>
        )}
        <GlAccountHelp
          key={helpFor ?? -1}
          open={helpFor !== null}
          initial={helpFor !== null ? String(items[helpFor]?.glAccount ?? '') : ''}
          onPick={(a) => helpFor !== null && onEdit(helpFor, 'glAccount', a)}
          onClose={() => setHelpFor(null)}
        />
        <datalist id="gl-account-options">
          {GL_PICKER.map(([a, name]) => (
            <option key={a} value={a}>{name}</option>
          ))}
        </datalist>
        <div className="tw">
          <table>
            <thead>
              <tr>
                {/* Same columns, same order as MIRO's G/L Account tab. */}
                {vendorOptions && vendorOptions.length > 1 && <th style={{ minWidth: 190 }}>เข้าเอกสารของ</th>}
                <th style={{ minWidth: 150 }}>G/L acct</th>
                <th style={{ minWidth: 150 }}>Short Text</th>
                <th style={{ width: 100 }}>D/C</th>
                <th style={{ minWidth: 130 }}>Amount in doc.curr.</th>
                <th style={{ minWidth: 100 }}>Tax code</th>
                <th style={{ minWidth: 140 }}>Assignment</th>
                <th style={{ minWidth: 180 }}>Text</th>
                <th style={{ minWidth: 130 }}>Cost Center</th>
                <th style={{ width: 44 }} />
              </tr>
            </thead>
            <tbody>
              {items.length ? (
                items.map((g, i) => (
                  <tr key={i}>
                    {vendorOptions && vendorOptions.length > 1 && (
                      <td>
                        <select
                          value={String(g.vendorCode ?? '').trim() || mainVendor || ''}
                          disabled={posted}
                          onChange={(e) => onEdit(i, 'vendorCode', e.target.value)}
                          title="ใบแจ้งหนี้ใน SAP ที่บรรทัดนี้จะไปอยู่"
                        >
                          {vendorOptions.map(([c, l]) => <option key={c} value={c}>{l}</option>)}
                        </select>
                      </td>
                    )}
                    <td>
                      <div style={{ display: 'flex', gap: 4 }}>
                        <input
                          value={g.glAccount || ''}
                          style={{ minWidth: 100 }}
                          readOnly={posted}
                          list="gl-account-options"
                          title={glName(g.glAccount)}
                          onChange={(e) => onEdit(i, 'glAccount', e.target.value)}
                          onKeyDown={(e) => { if (e.key === 'F4' && !posted) { e.preventDefault(); setHelpFor(i); } }}
                        />
                        {!posted && (
                          <button className="btn sm ghost" title="ค้นหา G/L Account (F4)" onClick={() => setHelpFor(i)}>
                            <i className="fa-regular fa-window-restore" />
                          </button>
                        )}
                      </div>
                    </td>
                    <td className="muted">
                      {glName(g.glAccount)}
                    </td>
                    <td>
                      <select
                        value={g.drCr || ''}
                        disabled={posted}
                        onChange={(e) => onEdit(i, 'drCr', e.target.value)}
                      >
                        <option value="">— Select —</option>
                        <option value="D">Debit</option>
                        <option value="C">Credit</option>
                      </select>
                    </td>
                    <td className="num">
                      <input
                        defaultValue={fmt(g.amount)}
                        readOnly={posted}
                        onFocus={(e) => (e.target.value = String(num(e.target.value)))}
                        onBlur={(e) => (e.target.value = fmt(e.target.value))}
                        onInput={(e) => onEdit(i, 'amount', (e.target as HTMLInputElement).value)}
                      />
                    </td>
                    <td>
                      {/* A picker, not free text: the code decides the tax SAP calculates, and a
                          typo here is silently wrong money. The row arrives with a suggestion
                          (services 7%, duty exempt) that anyone can change. */}
                      <select
                        value={g.taxCode || ''}
                        disabled={posted}
                        onChange={(e) => onEdit(i, 'taxCode', e.target.value)}
                      >
                        <option value="">— Select —</option>
                        {TAX_CODES.map(([code, label]) => (
                          <option key={code} value={code}>{label}</option>
                        ))}
                      </select>
                    </td>
                    <td>
                      <input
                        value={g.assignment || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'assignment', e.target.value)}
                      />
                    </td>
                    <td>
                      <input
                        value={g.itemText || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'itemText', e.target.value)}
                      />
                    </td>
                    <td>
                      <input
                        value={g.costCenter || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'costCenter', e.target.value)}
                      />
                    </td>
                    <td>
                      {!posted && (
                        <button className="btn sm ghost" onClick={() => onDelete(i)}>
                          <i className="fa-solid fa-xmark" />
                        </button>
                      )}
                    </td>
                  </tr>
                ))
              ) : (
                <tr>
                  <td colSpan={vendorOptions && vendorOptions.length > 1 ? 10 : 9} className="empty">
                    No items
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
