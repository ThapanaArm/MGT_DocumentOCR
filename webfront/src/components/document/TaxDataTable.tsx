import { fmt, num } from '../../utils/format';

/* Tax Data grid from the SAP "Enter Incoming Invoice · Tax" tab
   (header.taxItems). Columns: D/C, Tax Doc. Currency, Tax Code,
   TxValidFrm, Tax Rate. */
export type TaxItem = Record<string, any>;

export default function TaxDataTable({
  items,
  posted,
  onEdit,
  onAdd,
  onDelete,
  bare,
}: {
  items: TaxItem[];
  posted: boolean;
  onEdit: (i: number, key: string, value: string) => void;
  onAdd: () => void;
  onDelete: (i: number) => void;
  bare?: boolean; // render without the outer .card wrapper (for use inside a tabbed card)
}) {
  const total = items.reduce((a, t) => a + num(t.docCurrencyAmt), 0);
  const addBtn = !posted ? (
    <button className="btn sm" onClick={onAdd}>
      + Add Row
    </button>
  ) : null;

  return (
    <div className={bare ? undefined : 'card'}>
      {!bare && (
        <div className="card-h">
          <h2>Tax Data ({items.length})</h2>
          <div className="sp" />
          {addBtn}
        </div>
      )}
      <div className="card-b">
        {bare && (
          <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
            <p className="sec-title" style={{ margin: 0 }}>Tax Data</p>
            {addBtn}
          </div>
        )}
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th style={{ width: 90 }}>D/C</th>
                <th style={{ minWidth: 150 }}>Tax Doc. Currency</th>
                <th style={{ minWidth: 200 }}>Tax Code</th>
                <th style={{ minWidth: 130 }}>TxValidFrm</th>
                <th style={{ minWidth: 130 }}>Tax Rate</th>
                <th style={{ width: 44 }} />
              </tr>
            </thead>
            <tbody>
              {items.length ? (
                items.map((t, i) => (
                  <tr key={i}>
                    <td>
                      <select
                        value={t.drCr || ''}
                        disabled={posted}
                        onChange={(e) => onEdit(i, 'drCr', e.target.value)}
                      >
                        <option value="">—</option>
                        <option value="S">S — Debit</option>
                        <option value="H">H — Credit</option>
                      </select>
                    </td>
                    <td className="num">
                      <input
                        defaultValue={fmt(t.docCurrencyAmt)}
                        readOnly={posted}
                        onFocus={(e) => (e.target.value = String(num(e.target.value)))}
                        onBlur={(e) => (e.target.value = fmt(e.target.value))}
                        onInput={(e) => onEdit(i, 'docCurrencyAmt', (e.target as HTMLInputElement).value)}
                      />
                    </td>
                    <td>
                      <input
                        value={t.taxCode || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'taxCode', e.target.value)}
                        placeholder="e.g. V1 (Input VAT 7%)"
                      />
                    </td>
                    <td>
                      <input
                        value={t.validFrom || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'validFrom', e.target.value)}
                      />
                    </td>
                    <td>
                      <input
                        value={t.taxRate || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'taxRate', e.target.value)}
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
                  <td colSpan={6} className="empty">
                    No items
                  </td>
                </tr>
              )}
            </tbody>
            <tfoot>
              <tr className="totrow">
                <td style={{ textAlign: 'right' }}>Tax Total</td>
                <td className="num">{fmt(total)}</td>
                <td colSpan={4} />
              </tr>
            </tfoot>
          </table>
        </div>
      </div>
    </div>
  );
}
