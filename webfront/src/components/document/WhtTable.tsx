import { fmt, num } from '../../utils/format';
import { WHT_TYPES, WHT_CODES, RECIPIENT_TYPES, WHT_CODE_RATE } from '../../constants/fields';

/* Withholding Tax grid from the SAP "Enter Incoming Invoice · Withholding tax"
   tab (header.whtItems). One row per WHT type. Numbered SAP fields:
   2) W/Tax Code, 3) W/Tax Base FC, 4) WTax Amt in FC.

   Type / Code / Recipient Type are real SAP codes (KUT-FI-AP-204), so they are
   dropdowns rather than free text — SAP rejects anything outside the master. */
export type WhtItem = Record<string, any>;

export default function WhtTable({
  items,
  posted,
  onEdit,
  onAdd,
  onDelete,
  bare,
}: {
  items: WhtItem[];
  posted: boolean;
  onEdit: (i: number, key: string, value: string) => void;
  onAdd: () => void;
  onDelete: (i: number) => void;
  bare?: boolean; // render without the outer .card wrapper (for use inside a tabbed card)
}) {
  const addBtn = !posted ? (
    <button className="btn sm" onClick={onAdd}>
      + Add Row
    </button>
  ) : null;

  // SAP works the withholding amount out of the base itself, so the amount the read found is
  // only a cross-check: if it does not match base x rate, the code or the base is wrong.
  const expected = (w: WhtItem) => {
    const rate = WHT_CODE_RATE[String(w.whtCode ?? '')];
    return rate ? Math.round((Number(w.baseFc) || 0) * rate) / 100 : 0;
  };
  const mismatch = (w: WhtItem) => {
    const amt = Number(w.amtFc) || 0;
    const exp = expected(w);
    return exp > 0 && amt > 0 && Math.abs(exp - amt) > 0.5;
  };

  const sel = (
    i: number,
    key: string,
    value: string,
    options: Array<[string, string]>,
  ) => (
    <select value={value} disabled={posted} onChange={(e) => onEdit(i, key, e.target.value)}>
      {options.map(([v, l]) => (
        <option key={v} value={v}>
          {l}
        </option>
      ))}
    </select>
  );

  return (
    <div className={bare ? undefined : 'card'}>
      {!bare && (
        <div className="card-h">
          <h2>Withholding Tax ({items.length})</h2>
          <div className="sp" />
          {addBtn}
        </div>
      )}
      <div className="card-b">
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th style={{ minWidth: 250 }}>Name of WTax Type</th>
                <th style={{ minWidth: 200 }}>W/Tax Code</th>
                <th style={{ minWidth: 190 }}>
                  Recipient Type <span style={{ color: 'var(--danger, #e5484d)' }}>*</span>
                </th>
                <th style={{ minWidth: 150 }}>
                  W/Tax Base <span style={{ color: 'var(--danger, #e5484d)' }}>*</span>
                </th>
                <th style={{ minWidth: 150 }}>W/Tax Amt (from document)</th>
                <th style={{ width: 44 }} />
              </tr>
            </thead>
            <tbody>
              {items.length ? (
                items.map((w, i) => (
                  <tr key={i}>
                    <td>{sel(i, 'wtType', w.wtType || '', WHT_TYPES)}</td>
                    <td>{sel(i, 'whtCode', w.whtCode || '', WHT_CODES)}</td>
                    <td>{sel(i, 'recipientType', w.recipientType || '', RECIPIENT_TYPES)}</td>
                    <td className="num">
                      <input
                        key={`base-${i}-${fmt(w.baseFc)}`}
                        defaultValue={fmt(w.baseFc)}
                        readOnly={posted}
                        onFocus={(e) => (e.target.value = String(num(e.target.value)))}
                        onBlur={(e) => (e.target.value = fmt(e.target.value))}
                        onInput={(e) => onEdit(i, 'baseFc', (e.target as HTMLInputElement).value)}
                      />
                    </td>
                    <td className="num">
                      <input
                        defaultValue={fmt(w.amtFc)}
                        readOnly={posted}
                        onFocus={(e) => (e.target.value = String(num(e.target.value)))}
                        onBlur={(e) => (e.target.value = fmt(e.target.value))}
                        onInput={(e) => onEdit(i, 'amtFc', (e.target as HTMLInputElement).value)}
                      />
                      {mismatch(w) && (
                        <div className="hint" style={{ color: 'var(--danger, #e5484d)', marginTop: 4 }}>
                          does not match {WHT_CODE_RATE[String(w.whtCode)]}% of base ({fmt(expected(w))})
                        </div>
                      )}
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
                  <td colSpan={6} className="empty">
                    No items
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <p className="hint" style={{ marginTop: 10 }}>
          In SAP you key only W/Tax Code and W/Tax Base — SAP computes W/Tax Amt from the code's
          rate, so the amount column here is what the document says, kept as a cross-check.
          W/Tax Base is the invoice amount before VAT; changing the code recalculates it.
        </p>
      </div>
    </div>
  );
}
