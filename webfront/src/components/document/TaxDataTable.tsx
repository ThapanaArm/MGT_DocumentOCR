import { fmt, num } from '../../utils/format';
import { TAX_CODES, isDutyRow } from '../../constants/fields';

/* Tax Data grid from the SAP "Enter Incoming Invoice · Tax" tab
   (header.taxItems). Columns: D/C, Tax Doc. Currency, Tax Code,
   TxValidFrm, Tax Rate. */
export type TaxItem = Record<string, any>;

const VAT_LABELS = { INPUT: 'Input VAT', DEFERRED: 'Deferred Input VAT', EXEMPT: 'Exempt VAT' } as const;
const isDutyOrFee = (t: TaxItem) =>
  isDutyRow({ extCode: t.extCode, desc: t.label }) || /^customs fee$/i.test(String(t.label ?? '').trim());

function vatKindForCode(code: string): keyof typeof VAT_LABELS | undefined {
  if (/^D[0-2]$/.test(code)) return 'DEFERRED';
  if (/^V[0-4]$/.test(code)) return 'INPUT';
  if (code === 'VX') return 'EXEMPT';
}

function linkedEdits(t: TaxItem, key: string, value: string): Array<[string, string]> {
  if (isDutyOrFee(t)) return [[key, value]];
  let code = String(t.taxCode ?? '').trim().toUpperCase();
  if (key === 'taxKind') {
    if (value !== 'INPUT' && value !== 'DEFERRED') return [];
    // Only these rates have both a standard and a deferred SAP code.
    if (code && !/^[VD][0-2]$/.test(code)) return [];
    code = (value === 'DEFERRED' ? 'D' : 'V') + (code ? code[1] : '1');
  } else if (key === 'taxCode') {
    code = value.trim().toUpperCase();
  } else {
    return [[key, value]];
  }

  const kind = vatKindForCode(code);
  if (!kind) {
    return vatKindForCode(String(t.taxCode ?? '').trim().toUpperCase())
      ? [['taxCode', code], ['taxKind', ''], ['label', '']]
      : [['taxCode', code]];
  }
  return [['taxCode', code], ['taxKind', kind === 'EXEMPT' ? '' : kind], ['label', VAT_LABELS[kind]]];
}

export default function TaxDataTable({
  items,
  posted,
  onEdit,
  onAdd,
  onDelete,
  bare,
  title = 'Tax Data',
}: {
  items: TaxItem[];
  posted: boolean;
  onEdit: (i: number, key: string, value: string) => void;
  onAdd: () => void;
  onDelete: (i: number) => void;
  bare?: boolean; // render without the outer .card wrapper (for use inside a tabbed card)
  title?: string;
}) {
  const edit = (i: number, key: string, value: string) => {
    for (const [k, v] of linkedEdits(items[i] || {}, key, value)) onEdit(i, k, v);
  };

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
          <h2>{title} ({items.length})</h2>
          <div className="sp" />
          {addBtn}
        </div>
      )}
      <div className="card-b">
        {bare && (
          <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
            <p className="sec-title" style={{ margin: 0 }}>{title}</p>
            {addBtn}
          </div>
        )}
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th style={{ minWidth: 190 }}>Item</th>
                <th style={{ width: 130 }}>D/C</th>
                <th style={{ minWidth: 150 }}>Tax Doc. Currency</th>
                <th style={{ minWidth: 200 }}>Tax Code</th>
                <th style={{ minWidth: 200 }}>Input Tax Type</th>
                <th style={{ minWidth: 200 }}>Tax Invoice Issuer</th>
                <th style={{ minWidth: 150 }}>Tax ID No.</th>
                <th style={{ width: 100 }}>Branch</th>
                <th style={{ minWidth: 150 }}>Tax Invoice No.</th>
                <th style={{ minWidth: 130 }}>Tax Invoice Date</th>
                <th style={{ minWidth: 140 }}>Base Amount</th>
                <th style={{ minWidth: 130 }}>TxValidFrm</th>
                <th style={{ minWidth: 130 }}>Tax Rate</th>
                <th style={{ width: 44 }} />
              </tr>
            </thead>
            <tbody>
              {items.length ? (
                items.map((t, i) => {
                  const code = String(t.taxCode ?? '').trim().toUpperCase();
                  const dutyOrFee = isDutyOrFee(t);
                  const vatKind = dutyOrFee ? undefined : vatKindForCode(code);
                  return <tr key={i}>
                    <td>
                      <input
                        value={vatKind ? VAT_LABELS[vatKind] : String(t.label ?? '')}
                        readOnly={posted || Boolean(vatKind)}
                        onChange={(e) => edit(i, 'label', e.target.value)}
                        placeholder="e.g. Input VAT, Import Duty"
                      />
                    </td>
                    <td>
                      {/* Input tax on a supplier invoice is a debit by default, but a credit memo
                          reverses it — so the default is S and the user can still switch it. */}
                      <select
                        value={t.drCr || 'S'}
                        disabled={posted}
                        onChange={(e) => onEdit(i, 'drCr', e.target.value)}
                      >
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
                      <select
                        value={code}
                        disabled={posted}
                        onChange={(e) => edit(i, 'taxCode', e.target.value)}
                      >
                        <option value="">— Select —</option>
                        {TAX_CODES.map(([code, label]) => (
                          <option key={code} value={code}>
                            {label}
                          </option>
                        ))}
                      </select>
                    </td>
                    <td>
                      {/* Tax invoice / receipt -> input tax claimable this period (goes on the
                          input-VAT report); invoice / billing note -> deferred input tax.
                          A duty row (Excise Tax, Interior Tax, Import Duty) is not input VAT at
                          all — there is nothing to claim and nothing to defer — so it shows a dash
                          rather than an empty dropdown that reads as a field somebody forgot. */}
                      {dutyOrFee || vatKind === 'EXEMPT' ? (
                        <span className="muted" title="No input-tax type">
                          —
                        </span>
                      ) : (
                        <select
                          value={vatKind ?? t.taxKind ?? ''}
                          disabled={posted || (code !== '' && !/^[VD][0-2]$/.test(code))}
                          title={/^V[34]$/.test(code) ? 'Select the Tax Code to change this tax type' : undefined}
                          onChange={(e) => edit(i, 'taxKind', e.target.value)}
                        >
                          <option value="" disabled>— Select —</option>
                          <option value="INPUT">Input tax</option>
                          <option value="DEFERRED">Deferred tax</option>
                        </select>
                      )}
                    </td>
                    {/* Identity of the tax invoice this VAT came from — the columns the Input VAT
                        file needs, shown here so they can be checked before the file is made.
                        Blank on a duty row, which has no tax invoice of its own. */}
                    <td>
                      <input
                        value={t.issuerName || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'issuerName', e.target.value)}
                      />
                    </td>
                    <td>
                      <input
                        value={t.issuerTaxId || ''}
                        readOnly={posted}
                        maxLength={13}
                        onChange={(e) => onEdit(i, 'issuerTaxId', e.target.value.replace(/\D/g, ''))}
                      />
                      {!!t.issuerTaxId && String(t.issuerTaxId).length !== 13 && (
                        <div className="hint" style={{ color: 'var(--danger, #e5484d)', marginTop: 4 }}>
                          must be 13 digits
                        </div>
                      )}
                    </td>
                    <td>
                      <input
                        value={t.issuerBranch || ''}
                        readOnly={posted}
                        maxLength={4}
                        onChange={(e) => onEdit(i, 'issuerBranch', e.target.value.replace(/\D/g, ''))}
                      />
                    </td>
                    <td>
                      <input
                        value={t.taxDocNo || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'taxDocNo', e.target.value)}
                      />
                    </td>
                    <td>
                      <input
                        type="date"
                        value={t.taxDocDate || ''}
                        readOnly={posted}
                        onChange={(e) => onEdit(i, 'taxDocDate', e.target.value)}
                      />
                    </td>
                    <td className="num">
                      <input
                        defaultValue={fmt(t.baseAmount)}
                        readOnly={posted}
                        onFocus={(e) => (e.target.value = String(num(e.target.value)))}
                        onBlur={(e) => (e.target.value = fmt(e.target.value))}
                        onInput={(e) => onEdit(i, 'baseAmount', (e.target as HTMLInputElement).value)}
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
                          <i className="fa-solid fa-xmark" />
                        </button>
                      )}
                    </td>
                  </tr>;
                })
              ) : (
                <tr>
                  <td colSpan={14} className="empty">
                    No items
                  </td>
                </tr>
              )}
            </tbody>
            <tfoot>
              <tr className="totrow">
                <td colSpan={2} style={{ textAlign: 'right' }}>{title === 'Tax Data' ? 'Tax Total' : 'ยอดภาษีตามใบกำกับ'}</td>
                <td className="num">{fmt(total)}</td>
                <td colSpan={11} />
              </tr>
            </tfoot>
          </table>
        </div>
      </div>
    </div>
  );
}
