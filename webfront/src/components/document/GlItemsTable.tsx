import { fmt, num } from '../../utils/format';

/* Ports the G/L Account Items table (header.glItems) from docHtml(). */
export type GlItem = Record<string, any>;

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
  onAdd: () => void;
  onDelete: (i: number) => void;
  bare?: boolean; // render without the outer .card wrapper (for use inside a tabbed card)
}) {
  const addBtn = !posted ? (
    <button className="btn sm" onClick={onAdd}>
      + Add Item
    </button>
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
                      <input
                        value={g.taxCode || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'taxCode', e.target.value)}
                      />
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
                          ✕
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
