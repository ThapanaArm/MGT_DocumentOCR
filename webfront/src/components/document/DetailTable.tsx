import { useEffect, useState } from 'react';
import { fmtAmt, num } from '../../utils/format';
import { qtyTxt } from './MappingCards';
import type { DocLine, DocModel, MapResult } from '../../api/documents';
import type { MastersData } from '../../api/masters';
import { PO_LINE_EXTRA_FIELDS } from '../../constants/fields';
import MaterialSearchSelect from './MaterialSearchSelect';

/* Ports the DETAIL lines table from docHtml() incl. uomCell()/lineExtraCount(). */

function lineExtraCount(l: DocLine) {
  const ex = l.extra || {};
  return PO_LINE_EXTRA_FIELDS.filter((f) => (ex[f[0]] || '') !== '').length;
}

function UomCell({
  map,
  i,
  posted,
  onAddUomRule,
}: {
  map: MapResult | null;
  i: number;
  posted: boolean;
  onAddUomRule: (i: number) => void;
}) {
  if (!map) return <span className="badge b-idle">Pending Mapping</span>;
  const r = map.lines[i];
  const u = r.uom || ({} as NonNullable<typeof r.uom>);
  if (!r.code) return <span className="hint">—</span>;
  if (u.status === 'fail')
    return (
      <>
        <span className="badge b-fail"><i className="fa-solid fa-xmark" /> No unit conversion rule</span>
        {!posted && (
          <div style={{ marginTop: 6 }}>
            <button className="btn sm" onClick={() => onAddUomRule(i)}>
              + Add Rule
            </button>
          </div>
        )}
      </>
    );
  if (u.status === 'convert')
    return (
      <>
        <b>
          {qtyTxt(u.sapQty)} {u.sapUom}
        </b>
        <div className="sub">
          <span className="badge b-warn"><i className="fa-solid fa-right-left" /> ×{u.factor}</span>
        </div>
        <div className="hint" style={{ marginTop: 2 }}>
          {u.method}
        </div>
      </>
    );
  return (
    <>
      <b>
        {qtyTxt(u.sapQty)} {u.sapUom}
      </b>
      <div className="hint">{u.method}</div>
    </>
  );
}

// Controlled numeric cell for Quantity / Unit Price / Amount. It must be CONTROLLED (value=), not an
// uncontrolled defaultValue input: a defaultValue input never updates its shown value after mount, so a
// programmatic change to the line (e.g. the "Ask AI to fix data" chat setting a new Unit Price) was
// applied to state but the cell still displayed the old value ("AI updated it but I don't see it").
// While the user is typing (focused) we hold their raw text and don't clobber it; when they're not
// editing we mirror the external value, showing it comma-formatted (raw while focused, like before).
function NumCell({ value, posted, onInput }: { value: unknown; posted: boolean; onInput: (v: string) => void }) {
  const [text, setText] = useState(() => fmtAmt(value));
  const [editing, setEditing] = useState(false);
  useEffect(() => {
    if (!editing) setText(fmtAmt(value));
  }, [value, editing]);
  return (
    <input
      value={text}
      readOnly={posted}
      onFocus={() => { setEditing(true); setText(String(num(value))); }}
      onBlur={() => { setEditing(false); setText(fmtAmt(value)); }}
      onChange={(e) => { setText(e.target.value); onInput(e.target.value); }}
    />
  );
}

interface Props {
  doc: DocModel;
  map: MapResult | null;
  masters: MastersData;
  posted: boolean;
  onEditLine: (i: number, key: string, value: string) => void;
  onEditLineExtra: (i: number, key: string, value: string) => void;
  onManualLine: (i: number, value: string) => void;
  onDelLine: (i: number) => void;
  onAddLine: () => void;
  onLearn: (i: number) => void;
  onShowLineExtra: (i: number) => void;
  onAddUomRule: (i: number) => void;
  /** Vendor lookup (AP/II bundles): the code currently filtered on ("" = show all) and the
   *  handlers the page wires to it. Omitted -> the lookup still shows but only filters. */
  vendorFilter?: string;
  onVendorFilter?: (code: string) => void;
  /** GLC/SO: resolve a line's chosen Sales Employee Person ID (extra.salesEmployee) to a display
   *  name for the "Sales Employee Name" column. Returns undefined when the ID isn't in the pick
   *  list (then the raw ID is shown). Omitted for non-SO / MGT call sites. */
  resolveSalesEmp?: (personId: string) => string | undefined;
  bare?: boolean; // render without the outer .card wrapper (for use inside a tabbed card)
  /** F01: this table checks OCR lines against shared Master Data (materials/UoM rules) — for the
   *  SAP/GLC path that master data IS the SAP material master, but for MGT the actual Sales Order
   *  is matched and sent separately in Step 3 (Zoho CRM), so labeling these columns "(SAP)" here
   *  implies a destination this check doesn't decide. Defaults to the SAP wording (unchanged for
   *  GLC and for the AP/PO Reference tab, which do post to SAP) — pass true only for the MGT/Zoho
   *  document view. */
  isMgt?: boolean;
}

export default function DetailTable({
  doc,
  map,
  masters,
  posted,
  onEditLine,
  onEditLineExtra,
  onManualLine,
  onDelLine,
  onAddLine,
  onLearn,
  onShowLineExtra,
  onAddUomRule,
  vendorFilter = '',
  onVendorFilter,
  resolveSalesEmp,
  bare,
  isMgt,
}: Props) {
  // SO documents match Material through ocr.CustomerMaterial, not the (deprecated for SO) generic
  // ocr.Material master -- same source, same cross-customer-borrow ordering and label format the
  // "Material — Row N" card in MappingCards.tsx uses. Before this fix, this table pulled its
  // options from masters.materials while MappingCards pulled from masters.custmaterials -- the
  // same underlying SAP code could carry a different (often stale/wrong) description in each
  // master, so picking a code here and looking at "Material — Row N" above could show two
  // different product names for what's actually the same selection. AP/II keep the original
  // generic Material master unchanged (no CustomerMaterial concept there).
  const currentCustomerCode = map?.header.customer?.code;
  const matOpts = doc.module === 'SO'
    ? masters.custmaterials
        .filter((cm) => String(cm.SalesOrg) === (doc.header.salesOrg || (isMgt ? '1000' : '2000')))
        .sort((a, b) => {
          const aMine = a.CustomerCode === currentCustomerCode ? 0 : 1;
          const bMine = b.CustomerCode === currentCustomerCode ? 0 : 1;
          if (aMine !== bMine) return aMine - bMine;
          return String(a.MaterialCodeName || '').localeCompare(String(b.MaterialCodeName || ''));
        })
        .map((m) => ({
          value: m.MaterialCodeSAP,
          description: m.MaterialCodeName || '',
          searchText: `${m.MaterialCodeCode ?? ''} ${m.MaterialCodeName ?? ''}`,
          // Always show the Customer Code, own customer included -- see MappingCards.tsx's matOpts
          // for the same rule (keep both in sync: this table and the "Material — Row N" card must
          // show the same label for the same underlying code, per the note above).
          label: m.CustomerCode === currentCustomerCode
            ? `${m.MaterialCodeSAP} — ${m.MaterialCodeName || ''} (CustomerMaterial · ${m.CustomerCode})`
            : `${m.MaterialCodeSAP} — ${m.MaterialCodeName || ''} (CustomerMaterial · ${m.CustomerCode}, other customer)`,
        }))
    : masters.materials.map((m) => ({
        value: m.MaterialCode,
        description: m.Description || '',
        label: m.MaterialCode + ' — ' + m.Description,
      }));
  // Vendor code per line comes from the FORM SHIPPING EXPENSE "VENDOR" column. One SAP document
  // can only be posted to one vendor, so when a bundle carries more than one the table offers a
  // lookup that splits it into one document per vendor.
  const vendorOf = (l: DocLine) => String(l.extra?.vendorCode ?? '').trim();
  // WHT / VAT / DUTY rows are taxes, not items: they live in the Tax and Withholding Tax tabs.
  const isTaxRow = (l: DocLine) =>
    l.extCode === 'WHT' || l.extCode === 'VAT' || l.extCode === 'DUTY';
  const vendorGroups = doc.lines.reduce<Record<string, { rows: number; total: number }>>((acc, l) => {
    const code = vendorOf(l);
    if (!code || isTaxRow(l)) return acc;
    const g = acc[code] || (acc[code] = { rows: 0, total: 0 });
    g.rows += 1;
    g.total += num(l.amount);
    return acc;
  }, {});
  const vendorCodes = Object.keys(vendorGroups).sort();
  // Shown for every AP/II document, not only when codes were read: a re-read of an older document
  // has no vendorCode yet, and the column is where the user types or fixes one by hand.
  const showVendor = doc.module === 'AP' || doc.module === 'II';
  const showPoExtra = doc.module === 'AP';
  const showSoExtra = doc.module === 'SO';
  const extraCols = (showPoExtra ? 1 : 0) + (showSoExtra ? 3 : 0) + (showVendor ? 1 : 0);
  // Withholding tax is not part of what is billed — it is deducted at payment — so the footer
  // total leaves those rows out (they stay in the table as a record). The total also follows the
  // vendor lookup, so filtering by a vendor shows exactly what that vendor's MIRO run is worth.
  // Filtered to one vendor the table is the basis for that vendor's MIRO run, so it shows only
  // that vendor's cost rows — the tax rows live in the Tax / Withholding tabs and would make the
  // total unreadable here. The unfiltered view stays the full picture (tax rows shown, withholding
  // left out of the total because it is deducted at payment, not billed).
  const hideRow = (l: DocLine) =>
    l.extCode === 'DUTY' || (vendorFilter ? isTaxRow(l) || vendorOf(l) !== vendorFilter : false);
  const sum = doc.lines
    .filter((l) => l.extCode !== 'WHT' && l.extCode !== 'DUTY')
    .filter((l) => !hideRow(l))
    .reduce((a, l) => a + num(l.amount), 0);

  const numInput = (
    value: unknown,
    onInput: (v: string) => void,
  ) => <NumCell value={value} posted={posted} onInput={onInput} />;

  const addBtn = !posted ? (
    <button className="btn sm" onClick={onAddLine}>
      + Add Row
    </button>
  ) : null;

  return (
    <div className={bare ? undefined : 'card'}>
      {!bare && (
        <div className="card-h">
          <h2>DETAIL — Line Items ({doc.lines.length} rows)</h2>
          <div className="sp" />
          {addBtn}
        </div>
      )}
      <div className="card-b">
        {bare && addBtn && (
          <div className="row" style={{ justifyContent: 'flex-end', marginBottom: 12 }}>{addBtn}</div>
        )}
        {showVendor && (
          <div
            className="row"
            style={{ alignItems: 'center', gap: 10, flexWrap: 'wrap', marginBottom: 12 }}
          >
            <span className="hint">Vendor in this document:</span>
            <select
              value={vendorFilter}
              onChange={(e) => onVendorFilter?.(e.target.value)}
              disabled={vendorCodes.length === 0}
              style={{ minWidth: 240 }}
            >
              <option value="">All vendors ({doc.lines.length} items)</option>
              {vendorCodes.map((code) => (
                <option key={code} value={code}>
                  {code} — {vendorGroups[code].rows} items · {fmtAmt(vendorGroups[code].total)}
                </option>
              ))}
            </select>
            {vendorCodes.length === 0 && (
              <span className="hint">
                No vendor code on any line yet — click Re-read Document so the VENDOR column is
                read, or type the code yourself in the Vendor column
              </span>
            )}
          </div>
        )}
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th style={{ width: 54 }}>Item</th>
                <th style={{ width: 150 }}>Item Code (Partner)</th>
                {showVendor && <th style={{ width: 110 }}>Vendor</th>}
                <th style={{ minWidth: 260 }}>Item Description (per Document)</th>
                <th style={{ minWidth: 110 }}>Quantity</th>
                <th style={{ width: 74 }}>Unit</th>
                <th style={{ minWidth: 130 }}>Unit Price</th>
                <th style={{ minWidth: 140 }}>Amount</th>
                <th style={{ minWidth: 340 }}>{isMgt ? 'Material (Master Data)' : 'Material (SAP)'}</th>
                <th style={{ minWidth: 170 }}>{isMgt ? 'Unit → Master Data' : 'Unit → SAP'}</th>
                <th>Status</th>
                {showPoExtra && <th style={{ width: 120 }}>PO Detail</th>}
                {showSoExtra && (
                  <>
                    <th style={{ minWidth: 160 }}>Sales Employee Name</th>
                    <th style={{ minWidth: 140 }}>Delivery Date</th>
                    <th style={{ minWidth: 220 }}>Item Note 1</th>
                  </>
                )}
                <th style={{ width: 44 }} />
              </tr>
            </thead>
            <tbody>
              {doc.lines.length ? (
                doc.lines.map((l, i) => {
                  // Vendor lookup filters the view only — nothing is removed from the document.
                  if (hideRow(l)) return null;
                  const r = map ? map.lines[i] : null;
                  return (
                    <tr key={i}>
                      <td style={{ textAlign: 'center' }}>{l.itemNo}</td>
                      <td>
                        <input
                          value={l.extCode ?? ''}
                          readOnly={posted}
                          onChange={(e) => onEditLine(i, 'extCode', e.target.value)}
                        />
                      </td>
                      {showVendor && (
                        <td>
                          <input
                            value={(l.extra?.vendorCode as string) ?? ''}
                            readOnly={posted}
                            onChange={(e) => onEditLineExtra(i, 'vendorCode', e.target.value)}
                            style={{ width: 96 }}
                          />
                        </td>
                      )}
                      <td>
                        <input
                          value={l.desc ?? ''}
                          readOnly={posted}
                          onChange={(e) => onEditLine(i, 'desc', e.target.value)}
                        />
                      </td>
                      <td className="num">{numInput(l.qty, (v) => onEditLine(i, 'qty', v))}</td>
                      <td>
                        <input
                          value={l.uom ?? ''}
                          readOnly={posted}
                          onChange={(e) => onEditLine(i, 'uom', e.target.value)}
                          style={{ width: 64 }}
                        />
                      </td>
                      <td className="num">{numInput(l.price, (v) => onEditLine(i, 'price', v))}</td>
                      <td className="num">{numInput(l.amount, (v) => onEditLine(i, 'amount', v))}</td>
                      <td
                        className={r && r.status === 'fail' ? 'cell-fail' : ''}
                        style={{ minWidth: 340 }}
                      >
                        {!map ? (
                          <span className="badge b-idle">Pending Mapping</span>
                        ) : (
                          <MaterialSearchSelect
                            options={matOpts}
                            value={r?.code || ''}
                            disabled={posted}
                            onChange={(value) => onManualLine(i, value)}
                          />
                        )}
                      </td>
                      <td
                        className={r && r.uom?.status === 'fail' ? 'cell-fail' : ''}
                        style={{ minWidth: 170 }}
                      >
                        <UomCell map={map} i={i} posted={posted} onAddUomRule={onAddUomRule} />
                      </td>
                      <td style={{ whiteSpace: 'nowrap' }}>
                        {r &&
                          (r.status === 'fail' ? (
                            <span className="badge b-fail"><i className="fa-solid fa-xmark" /> Not found</span>
                          ) : r.status === 'manual' ? (
                            <span className="badge b-warn"><i className="fa-solid fa-pen" /> Manual</span>
                          ) : (
                            <span className="badge b-ok"><i className="fa-solid fa-check" /> {r.method}</span>
                          ))}
                        {r && r.status === 'manual' && r.code && !posted && (
                          <button
                            className="btn sm"
                            style={{ marginLeft: 4 }}
                            onClick={() => onLearn(i)}
                          >
                            + Master
                          </button>
                        )}
                      </td>
                      {showPoExtra && (
                        <td style={{ whiteSpace: 'nowrap' }}>
                          <button
                            className={'btn sm ' + (lineExtraCount(l) ? '' : 'ghost')}
                            onClick={() => onShowLineExtra(i)}
                          >
                            <i className="fa-solid fa-clipboard-list" /> PO{' '}
                            {lineExtraCount(l)
                              ? `(${lineExtraCount(l)}/${PO_LINE_EXTRA_FIELDS.length})`
                              : ''}
                          </button>
                        </td>
                      )}
                      {showSoExtra && (
                        <>
                          <td style={{ minWidth: 160 }}>
                            {(() => {
                              // Show the Sales Employee chosen for THIS line. The person is picked
                              // per line in the "Sales Employee — this line" card above (stored as the
                              // SAP Person ID in extra.salesEmployee); here we resolve it to a name via
                              // the pick list. Falls back to the raw ID, then to the legacy free-text
                              // salesEmployeeName, then "—". Read-only — pick/change it in the card above.
                              const ex = l.extra || {};
                              const id = ex.salesEmployee || '';
                              if (id) {
                                const name = resolveSalesEmp?.(id);
                                return (
                                  <>
                                    <b>{name || id}</b>
                                    {name && name !== id && <div className="hint">{id}</div>}
                                  </>
                                );
                              }
                              return ex.salesEmployeeName ? (
                                <span>{ex.salesEmployeeName}</span>
                              ) : (
                                <span className="hint">—</span>
                              );
                            })()}
                          </td>
                          <td style={{ minWidth: 140 }}>
                            {/* Delivery Date is a calendar date picker (pick a date instead of typing).
                                type="date" uses/returns YYYY-MM-DD — the same format stored in
                                extra.deliveryDate and used by SAP scheduling + the split-by-date grouping.
                                disabled (not readOnly) when posted, since date inputs ignore readOnly. */}
                            <input
                              type="date"
                              value={(l.extra || {}).deliveryDate || ''}
                              disabled={posted}
                              onChange={(e) => onEditLineExtra(i, 'deliveryDate', e.target.value)}
                            />
                          </td>
                          <td style={{ minWidth: 220 }}>
                            <input
                              value={(l.extra || {}).itemNote1 || ''}
                              readOnly={posted}
                              placeholder="OCR prefilled — editable"
                              onChange={(e) => onEditLineExtra(i, 'itemNote1', e.target.value)}
                            />
                          </td>
                        </>
                      )}
                      <td>
                        {!posted && (
                          <button className="btn sm ghost" onClick={() => onDelLine(i)}>
                            <i className="fa-solid fa-xmark" />
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })
              ) : (
                <tr>
                  <td colSpan={11 + extraCols} className="empty">
                    No items
                  </td>
                </tr>
              )}
            </tbody>
            <tfoot>
              <tr className="totrow">
                <td colSpan={5 + (showVendor ? 1 : 0)} style={{ textAlign: 'right' }}>
                  Total{' '}
                  <span className="hint" style={{ fontWeight: 400 }}>
                    {vendorFilter
                      ? `(costs of vendor ${vendorFilter} only · taxes are on the Tax / Withholding Tax tabs)`
                      : '(excludes withholding tax)'}
                  </span>
                </td>
                <td style={{ textAlign: 'right' }}>{fmtAmt(sum)}</td>
                <td colSpan={4 + extraCols - (showVendor ? 1 : 0)} />
              </tr>
            </tfoot>
          </table>
        </div>
      </div>
    </div>
  );
}
