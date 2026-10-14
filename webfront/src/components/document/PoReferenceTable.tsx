import { fmtAmt, num } from '../../utils/format';
import { TAX_CODES } from '../../constants/fields';
import type { DocLine } from '../../api/documents';
import type { SapPurchaseOrderItem } from '../../api/sap';

/* The MIRO "Purchasing Document References" grid, column for column as S/4HANA shows it:
   Assigned · Invoice Item · Short Text · Purchasing Document/Item · Amount · Quantity ·
   Tax Code · Tax Date · Tax Rate.

   It replaces the general DETAIL table on the PO Reference tab, which showed our own mapping
   columns (Item (SAP), Unit → SAP, Status) — those belong to a document posted WITHOUT a purchase
   order. With a PO behind the invoice, SAP takes the items from the PO itself when the number is
   entered, so nothing here is "Not found": this grid is the preview of what SAP will assign. */

// SAP prints the rate next to the code; it follows from the code, so it is derived rather than
// stored. 7% codes, 10% codes, everything else 0 (exempt, zero-rated, or no tax at all).
function rateOf(code: string): number {
  const c = (code || '').trim().toUpperCase();
  if (/^[VDUN]1$/.test(c) || c === 'V4') return 7;
  if (/^[VDUN]2$/.test(c)) return 10;
  return 0;
}

// The PO item this line is assigned to. SAP's own number wins — it is the only one MIRO accepts,
// and it is numbered with gaps (10, 40, 50, 60 on PO 2230000020), so neither the number printed
// on the PO sheet nor the position in the list can stand in for it. Empty = not assigned yet.
export function assignedItemOf(line: DocLine): string {
  return String((line.extra as Record<string, unknown> | undefined)?.sapPoItem ?? '').trim();
}

// What the PURCHASE ORDER sheet printed in its own Item column, shown only as a hint while
// nothing is assigned — never sent to SAP.
function printedItemOf(line: DocLine): string {
  return String((line.extra as Record<string, unknown> | undefined)?.poItem ?? '').trim();
}

// A second line under a goods line is a note on it ("This price already includes 0.50USD
// discount"), not part of the short text SAP carries. 50 characters, like SAP's field.
function shortText(line: DocLine): string {
  return String(line.desc ?? '').replace(/\s+/g, ' ').trim().slice(0, 50);
}

export default function PoReferenceTable({
  lines,
  poRef,
  taxDate,
  headerTaxCode,
  posted,
  onTaxCode,
  onAssignAll,
  onUnassignAll,
  assigning,
  sapItemCount,
  sapItems,
  onAssignItem,
  onGrItem,
  matDoc,
  onDelLine,
}: {
  lines: DocLine[];
  poRef: string;
  taxDate: string;
  headerTaxCode: string;
  posted: boolean;
  /** Pull the purchase order from SAP and assign its items to these lines, as MIRO's "Assign all"
   *  does. Absent when the rows cannot be edited (the header's PO copy, or a posted document). */
  onAssignAll?: () => void;
  onUnassignAll?: () => void;
  assigning?: boolean;
  /** How many items the last lookup returned, for the "n of m" line under the toolbar. */
  sapItemCount?: number;
  /** The purchase order's own items, once "Assign all" has read them. They fill the per-row
   *  dropdown so a line the automatic match did not recognise can still be tied to its PO item
   *  by hand — SAP numbers items with gaps (10, 40, 50, 60 on PO 2230000020), so the number
   *  cannot be typed from the paper document. */
  sapItems?: SapPurchaseOrderItem[];
  /** Set or clear one line's PO item. Absent = the grid cannot be edited. */
  onAssignItem?: (i: number, item: string) => void;
  /** The goods-receipt item each line settles, shown only when the document carries a material
   *  document (GR-based invoice verification). A material document numbers its items 1, 2, 3 …
   *  of its own, so these are NOT the PO's 10, 40, 50, 60 — they have to be read off the
   *  material document in SAP. Blank = the line's position is sent. */
  onGrItem?: (i: number, item: string) => void;
  /** The material document number, for the column heading. Empty = the column is hidden. */
  matDoc?: string;
  /** Drop a line the purchase order does not cover. A MIRO keyed against a PO can only bill
   *  items the PO holds, so a line with no counterpart there (the supplier shipped something
   *  that was never ordered, or the read picked up a note as a line) has to come off the
   *  invoice — or be added to the PO in SAP first. Absent = the grid cannot be edited. */
  onDelLine?: (i: number) => void;
  /** Absent when the rows come from the header's PO copy (header.poLines), which has nowhere to
   *  save an edit — the grid is then read-only, as it is for a posted document. */
  onTaxCode?: (i: number, code: string) => void;
}) {
  const total = lines.reduce((a, l) => a + num(l.amount), 0);
  const editable = !posted && !!onTaxCode;
  const assignedCount = lines.filter((l) => assignedItemOf(l).length > 0).length;
  /* A PO item can appear once per invoice document — SAP answers a second one with M8/321
     "Document contains same order item more than once" and takes nothing. Marked here rather
     than left for SAP to find. */
  const itemCounts = lines.reduce<Record<string, number>>((a, l) => {
    const it = assignedItemOf(l);
    if (it) a[it] = (a[it] ?? 0) + 1;
    return a;
  }, {});
  const isDupe = (l: DocLine) => (itemCounts[assignedItemOf(l)] ?? 0) > 1;
  const grItemOf = (l: DocLine) =>
    String((l.extra as Record<string, unknown> | undefined)?.sapGrItem ?? '').trim();
  const showGr = !!onGrItem && !posted && !!(matDoc ?? '').trim();

  return (
    <>
      {onAssignAll && (
        <div
          className="row"
          style={{ justifyContent: 'space-between', alignItems: 'center', marginBottom: 10, gap: 12 }}
        >
          <div className="row" style={{ gap: 8, alignItems: 'center' }}>
            <button className="btn sm" onClick={onAssignAll} disabled={assigning || !poRef}>
              <i className="fa-solid fa-link" /> {assigning ? 'Reading PO…' : 'Assign all'}
            </button>
            <button
              className="btn sm ghost"
              onClick={onUnassignAll}
              disabled={assigning || assignedCount === 0}
            >
              Unassign all
            </button>
          </div>
          <span className="hint">
            {!poRef
              ? 'Enter the purchase order number first'
              : `${assignedCount} of ${lines.length} assigned${
                  sapItemCount ? ` · ${sapItemCount} items on the PO` : ''
                }`}
          </span>
        </div>
      )}
    <div className="tw">
      <table>
        <thead>
          <tr>
            <th style={{ width: 80 }}>Assigned</th>
            <th style={{ width: 100 }}>Invoice Item</th>
            <th>Short Text</th>
            <th style={{ minWidth: 190 }}>Purchasing Document/Item</th>
            <th style={{ textAlign: 'right', minWidth: 110 }}>Amount</th>
            <th style={{ textAlign: 'right', minWidth: 110 }}>Quantity</th>
            <th style={{ minWidth: 150 }}>Tax Code</th>
            <th style={{ minWidth: 110 }}>Tax Date</th>
            <th style={{ textAlign: 'right', width: 90 }}>Tax Rate</th>
            {showGr && <th style={{ minWidth: 110 }}>GR Item</th>}
            {onDelLine && <th style={{ width: 44 }} />}
          </tr>
        </thead>
        <tbody>
          {lines.length ? (
            lines.map((l, i) => {
              const code = String(
                (l.extra as Record<string, unknown> | undefined)?.taxCode ?? headerTaxCode ?? '',
              ).trim();
              return (
                <tr key={i}>
                  <td>
                    {isDupe(l) ? (
                      <span className="badge b-fail" title="PO item นี้ถูกใช้ซ้ำในเอกสารเดียวกัน">
                        ซ้ำ
                      </span>
                    ) : assignedItemOf(l) ? (
                      <i className="fa-solid fa-check" aria-label="Assigned" />
                    ) : (
                      <span className="badge b-warn">Not assigned</span>
                    )}
                  </td>
                  <td>{(i + 1) * 10}</td>
                  <td>{shortText(l)}</td>
                  <td>
                    {!poRef ? (
                      <span className="hint">—</span>
                    ) : onAssignItem && !posted && (sapItems?.length ?? 0) > 0 ? (
                      <div className="row" style={{ gap: 6, alignItems: 'center' }}>
                        <span className="hint">{poRef} /</span>
                        <select
                          value={assignedItemOf(l)}
                          onChange={(e) => onAssignItem(i, e.target.value)}
                          style={{ minWidth: 120 }}
                        >
                          <option value="">— not assigned —</option>
                          {(sapItems ?? []).map((it) => (
                            <option key={it.item} value={it.item}>
                              {it.item} · {(it.material || it.text).slice(0, 28)}
                            </option>
                          ))}
                        </select>
                      </div>
                    ) : onAssignItem && !posted ? (
                      // No item list yet — the lookup has not run, or SAP refused it (the
                      // communication arrangement for API_PURCHASEORDER_PROCESS_SRV answers 403
                      // "no start authorization"). The number can still be typed from the PO
                      // screen in SAP: it is 10, 40, 50, 60 there, never 1, 2, 3.
                      <div className="row" style={{ gap: 6, alignItems: 'center' }}>
                        <span className="hint">{poRef} /</span>
                        <input
                          value={assignedItemOf(l)}
                          onChange={(e) => onAssignItem(i, e.target.value.replace(/\D/g, ''))}
                          placeholder={printedItemOf(l) || 'item'}
                          inputMode="numeric"
                          style={{ width: 80 }}
                        />
                      </div>
                    ) : assignedItemOf(l) ? (
                      `${poRef} / ${assignedItemOf(l)}`
                    ) : (
                      <span className="hint">
                        {poRef} / ?{printedItemOf(l) ? ` (PO sheet: ${printedItemOf(l)})` : ''}
                      </span>
                    )}
                  </td>
                  <td style={{ textAlign: 'right' }}>{fmtAmt(l.amount)}</td>
                  <td style={{ textAlign: 'right' }}>
                    {num(l.qty).toLocaleString('en-US')} {String(l.uom ?? '')}
                  </td>
                  <td>
                    {editable ? (
                      <select value={code} onChange={(e) => onTaxCode!(i, e.target.value)}>
                        <option value="">— Select —</option>
                        {TAX_CODES.map(([c, label]) => (
                          <option key={c} value={c}>{label}</option>
                        ))}
                      </select>
                    ) : (
                      code || <span className="hint">—</span>
                    )}
                  </td>
                  <td>{taxDate || <span className="hint">—</span>}</td>
                  <td style={{ textAlign: 'right' }}>{rateOf(code)}%</td>
                  {showGr && (
                    <td>
                      <input
                        value={grItemOf(l)}
                        onChange={(e) => onGrItem!(i, e.target.value.replace(/\D/g, ''))}
                        placeholder={String(i + 1)}
                        inputMode="numeric"
                        style={{ width: 80 }}
                        title={`รายการที่เท่าไรในใบรับของ ${matDoc}`}
                      />
                    </td>
                  )}
                  {onDelLine && (
                    <td style={{ textAlign: 'center' }}>
                      <button
                        className="btn sm ghost"
                        onClick={() => onDelLine(i)}
                        title="เอาบรรทัดนี้ออกจากใบแจ้งหนี้ (ไม่มีใน PO)"
                        aria-label="Remove line"
                      >
                        <i className="fa-solid fa-trash" />
                      </button>
                    </td>
                  )}
                </tr>
              );
            })
          ) : (
            <tr>
              <td colSpan={(onDelLine ? 10 : 9) + (showGr ? 1 : 0)} className="hint" style={{ textAlign: 'center' }}>
                No data found. Enter a reference document, for example a purchase order.
              </td>
            </tr>
          )}
        </tbody>
        {lines.length > 0 && (
          <tfoot>
            <tr>
              <td colSpan={4} style={{ textAlign: 'right' }}><b>Total</b></td>
              <td style={{ textAlign: 'right' }}><b>{fmtAmt(total)}</b></td>
              <td colSpan={(onDelLine ? 5 : 4) + (showGr ? 1 : 0)} />
            </tr>
          </tfoot>
        )}
      </table>
    </div>
    </>
  );
}
