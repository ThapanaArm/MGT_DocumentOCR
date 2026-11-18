import { fmt, num } from '../../utils/format';
import { TAX_CODES } from '../../constants/fields';

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
}: {
  module: string;
  items: GlItem[];
  posted: boolean;
  onEdit: (i: number, key: string, value: string) => void;
  onAdd: (preset?: GlItem) => void;
  onDelete: (i: number) => void;
  bare?: boolean; // render without the outer .card wrapper (for use inside a tabbed card)
}) {
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
            {module === 'II' ? 'Line Items' : 'G/L Account Items'} ({items.length})
          </h2>
          <div className="sp" />
          {addBtn}
        </div>
      )}
      <div className="card-b">
        {bare && addBtn && (
          <div className="row" style={{ justifyContent: 'flex-end', marginBottom: 12 }}>{addBtn}</div>
        )}
        <datalist id="gl-account-options">
          {GL_ACCOUNTS.map(([a, name]) => (
            <option key={a} value={a}>{name}</option>
          ))}
        </datalist>
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th style={{ minWidth: 140 }}>G/L Account</th>
                <th style={{ width: 100 }}>Dr/Cr</th>
                <th style={{ minWidth: 130 }}>Amount</th>
                <th style={{ minWidth: 100 }}>Tax Code</th>
                <th style={{ minWidth: 140 }}>Assignment</th>
                <th style={{ minWidth: 180 }}>Item Text</th>
                <th style={{ minWidth: 130 }}>Cost Center</th>
                <th style={{ width: 44 }} />
              </tr>
            </thead>
            <tbody>
              {items.length ? (
                items.map((g, i) => (
                  <tr key={i}>
                    <td>
                      <input
                        value={g.glAccount || ''}
                        readOnly={posted}
                        list="gl-account-options"
                        title={GL_ACCOUNTS.find(([a]) => a === String(g.glAccount || '').trim())?.[1] || ''}
                        onChange={(e) => onEdit(i, 'glAccount', e.target.value)}
                      />
                    </td>
                    <td>
                      <select
                        value={g.drCr || ''}
                        disabled={posted}
                        onChange={(e) => onEdit(i, 'drCr', e.target.value)}
                      >
                        <option value="">— Select —</option>
                        <option value="D">S-Debit</option>
                        <option value="C">H-Credit</option>
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
                  <td colSpan={8} className="empty">
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
