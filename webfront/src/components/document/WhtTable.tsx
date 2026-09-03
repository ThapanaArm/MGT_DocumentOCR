import { fmt, num } from '../../utils/format';

/* Withholding Tax grid from the SAP "Enter Incoming Invoice · Withholding tax"
   tab (header.whtItems). One row per WHT type. Numbered SAP fields:
   2) W/Tax Code, 3) W/Tax Base FC, 4) WTax Amt in FC. */
export type WhtItem = Record<string, any>;

const WHT_TYPES = [
  'WHT Type for Payment Posting',
  'WHT Type for Invoice Posting',
];

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
      + เพิ่มแถว
    </button>
  ) : null;

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
        {/* {bare && (
          <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
            <p className="sec-title" style={{ margin: 0 }}>Withholding Tax</p>
            {addBtn}
          </div>
        )} */}
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th style={{ minWidth: 240 }}>Name of WTax Type</th>
                <th style={{ minWidth: 130 }}>W/Tax Code</th>
                <th style={{ minWidth: 150 }}>W/Tax Base FC</th>
                <th style={{ minWidth: 150 }}>
                  WTax Amt in FC <span style={{ color: 'var(--danger, #e5484d)' }}>*</span>
                </th>
                <th style={{ width: 44 }} />
              </tr>
            </thead>
            <tbody>
              {items.length ? (
                items.map((w, i) => (
                  <tr key={i}>
                    <td>
                      <input
                        list="wht-types"
                        value={w.wtType || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'wtType', e.target.value)}
                      />
                    </td>
                    <td>
                      <input
                        value={w.whtCode || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'whtCode', e.target.value)}
                      />
                    </td>
                    <td className="num">
                      <input
                        defaultValue={fmt(w.baseFc)}
                        readOnly={posted}
                        onFocus={(e) => (e.target.value = String(num(e.target.value)))}
                        onBlur={(e) => (e.target.value = fmt(e.target.value))}
                        onInput={(e) => onEdit(i, 'baseFc', (e.target as HTMLInputElement).value)}
                      />
                    </td>
                    <td className="num">
                      <input
                        required
                        defaultValue={fmt(w.amtFc)}
                        readOnly={posted}
                        onFocus={(e) => (e.target.value = String(num(e.target.value)))}
                        onBlur={(e) => (e.target.value = fmt(e.target.value))}
                        onInput={(e) => onEdit(i, 'amtFc', (e.target as HTMLInputElement).value)}
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
                  <td colSpan={5} className="empty">
                    ไม่มีรายการ
                  </td>
                </tr>
              )}
            </tbody>
          </table>
          <datalist id="wht-types">
            {WHT_TYPES.map((t) => (
              <option key={t} value={t} />
            ))}
          </datalist>
        </div>
        <p className="hint" style={{ marginTop: 10 }}>
          หากไม่ระบุ W/Tax Base FC ระบบจะดึงจากจำนวนเงินในเอกสาร (ไม่รวม VAT)
        </p>
      </div>
    </div>
  );
}
