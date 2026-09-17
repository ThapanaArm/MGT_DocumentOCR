import { useState } from 'react';
import type { DocHeader, DocLine, DocModel, MapResult } from '../../api/documents';
import { SEND_DISABLED } from '../../constants/flags';
import { fmt, num } from '../../utils/format';

// GLC only (this whole editor is the SAP-side, GLC-only "check before you send" screen -- see the
// module doc comment below): the Subtotal/VAT/Grand Total block below is purely an OCR-review
// DISPLAY -- it does not change what gets POSTed to SAP (the payload/pricing logic is untouched;
// see onViewPayload for the actual request body). 7% is Thailand's standard VAT rate, same rate
// this codebase already assumes elsewhere (see the AP tax code list's "V1 - Input VAT 7%"/"D1 -
// Input Deferred Tax 7%" in constants/fields.ts) -- not currently configurable since nothing else
// in this feature needs it to be; if that changes, this should move into AppConfig the same way
// SapSalesOrderPriceConditionType did.
const GLC_VAT_RATE = 0.07;
const round2 = (n: number) => Math.round(n * 100) / 100;

/* The SAP Sales Order review step, lifted onto the page as one card -- the SAP-side counterpart
   of ZohoSalesOrderEditor, built so the two "check before you send" screens look and behave the
   same even though they post to different platforms. Unlike the Zoho editor this one is
   deliberately READ-ONLY: SAP documents are already fully edited/matched back in Step 2 (Data
   Mapping / MappingCards), so this card only shows exactly what Step 2 produced and lets the
   person send it -- it does not duplicate the editing surface. Values come straight from `map`
   (the last successful mapping result) and the document header/lines. */

export default function SapSalesOrderEditor({
  doc,
  map,
  header,
  salesOrg,
  sending,
  posted,
  onSend,
  onViewPayload,
}: {
  doc: DocModel;
  map: MapResult | null;
  header: DocHeader;
  salesOrg: string;
  sending: boolean;
  posted: boolean;
  onSend: () => void;
  /** Opens the "View Payload" modal showing the exact JSON that will be POSTed to SAP. */
  onViewPayload: () => void;
}) {
  const lines: DocLine[] = doc.lines || [];
  const lineStatus = (i: number) => map?.lines?.[i]?.status;
  const willSendCount = map ? lines.filter((_, i) => lineStatus(i) !== 'fail').length : 0;
  const notMatchedCount = map ? lines.filter((_, i) => lineStatus(i) === 'fail').length : 0;

  // The VAT amount the document ITSELF stated (OCR'd header — the "Totals" card's VAT field). When
  // the customer already broke VAT out on their document, this is filled and we should just show
  // their figures rather than re-deriving anything.
  const docVat = num(header.vatAmount);
  const docHasVat = docVat > 0;

  // Export orders billed in USD are not subject to Thai VAT, so no VAT is calculated or shown at all
  // (the VAT-mode selector is hidden for these) — Subtotal = Grand Total, VAT = 0. Display-only, same
  // as the VAT modes below; it never touches the SAP payload.
  const isUsd = (header.currency || '').trim().toUpperCase() === 'USD';

  // How to read the document's line prices for the VAT breakdown below. Three real cases:
  //  - 'document': the customer's document already separates VAT (VAT field filled) -> show the
  //    document's own Subtotal / VAT / Grand Total as-is, no re-computation. Default when the doc
  //    has a VAT figure.
  //  - 'exclude': prices don't include VAT yet -> add 7% on top (the common GLC quote). Default
  //    when the doc has no VAT figure.
  //  - 'include': the customer's document has VAT baked into the price but didn't break it out
  //    (e.g. only "Grand Total 19,140" with Tax 0.00) -> back the 7% out so the same money is shown
  //    split into Subtotal + VAT instead of adding a second 7% on top.
  // This is a per-document human decision (the CS can tell from the customer's document which it is)
  // and is DISPLAY-ONLY — it never changes the SAP payload, only this breakdown.
  const [vatMode, setVatMode] = useState<'document' | 'exclude' | 'include'>(docHasVat ? 'document' : 'exclude');

  // Sum of the lines that will actually be sent (unmatched lines aren't part of the SO SAP sees).
  const lineTotal = lines.reduce(
    (sum, l, i) => (map && lineStatus(i) !== 'fail' ? sum + num(l.qty) * num(l.price) : sum),
    0,
  );
  let subtotal: number;
  let vatAmount: number;
  let grandTotal: number;
  if (isUsd) {
    // USD (export): no Thai VAT — Subtotal = Grand Total, VAT = 0.
    subtotal = round2(lineTotal);
    vatAmount = 0;
    grandTotal = round2(lineTotal);
  } else if (vatMode === 'document') {
    // Use the document's own OCR'd figures. Fall back to computing the missing piece if OCR only
    // captured some of the three (Subtotal/VAT/Grand should reconcile, but OCR isn't perfect).
    subtotal = round2(num(header.subTotal) || (num(header.totalAmount) - docVat));
    vatAmount = round2(docVat);
    grandTotal = round2(num(header.totalAmount) || (subtotal + vatAmount));
  } else if (vatMode === 'include') {
    // lineTotal already contains VAT -> Subtotal = lineTotal / 1.07, VAT = remainder (so the two
    // always add back to lineTotal exactly), Grand = lineTotal.
    subtotal = round2(lineTotal / (1 + GLC_VAT_RATE));
    vatAmount = round2(lineTotal - subtotal);
    grandTotal = round2(lineTotal);
  } else {
    // 'exclude': Subtotal = lineTotal, VAT added on top.
    subtotal = round2(lineTotal);
    vatAmount = round2(subtotal * GLC_VAT_RATE);
    grandTotal = round2(subtotal + vatAmount);
  }

  const body = () => {
    if (!map) return <p className="hint">Run Step 2 — Data Mapping first — the Sales Order to send will appear here.</p>;

    return (
      <>
        <p className="hint">
          This is exactly what will be created in SAP as a <b>Sales Order</b>. Values come from Step 2 (Data Mapping) — go
          back there to change anything.
        </p>

        <div className="so-counts">
          <div>
            <span>DOCUMENT LINES</span>
            <b>{lines.length}</b>
          </div>
          <div className={willSendCount ? 'ok' : ''}>
            <span>WILL SEND</span>
            <b>{willSendCount}</b>
          </div>
          <div className={notMatchedCount ? 'bad' : 'ok'}>
            <span>NOT MATCHED</span>
            <b>{notMatchedCount}</b>
          </div>
        </div>

        <div className="grid">
          <div className="f">
            <label>Sold-to</label>
            <input type="text" value={map.header.customer ? `${map.header.customer.code || ''} — ${map.header.customer.text || ''}` : ''} disabled readOnly />
          </div>
          <div className="f">
            <label>Ship-to</label>
            <input type="text" value={map.header.shipTo ? `${map.header.shipTo.code || ''} — ${map.header.shipTo.text || ''}` : ''} disabled readOnly />
          </div>
          <div className="f">
            <label>Customer PO</label>
            <input type="text" value={header.poNo || ''} disabled readOnly />
          </div>
          <div className="f">
            <label>PO Date</label>
            <input type="text" value={header.poDate || ''} disabled readOnly />
          </div>
          <div className="f">
            <label>Delivery Date</label>
            <input type="text" value={header.deliveryDate || ''} disabled readOnly />
          </div>
          <div className="f">
            <label>Currency</label>
            <input type="text" value={header.currency || 'THB'} disabled readOnly />
          </div>
          <div className="f">
            <label>Sales Organization</label>
            <input type="text" value={salesOrg} disabled readOnly />
          </div>
        </div>

        <p className="sec-title" style={{ marginTop: 20 }}>
          ORDERED ITEMS ({lines.length})
        </p>
        <div className="tw" style={{ overflowX: 'auto' }}>
          <table>
            <thead>
              <tr>
                <th>Line</th>
                <th>Material (SAP)</th>
                <th>Description</th>
                <th>Qty</th>
                <th>Unit</th>
                <th>Price</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((l, i) => {
                const m = map.lines?.[i];
                const failed = m?.status === 'fail';
                return (
                  <tr key={i}>
                    <td>{l.itemNo ?? i + 1}</td>
                    <td>
                      <b>{m?.code || '—'}</b>
                      {m?.text && <div className="hint">{m.text}</div>}
                    </td>
                    <td>{l.desc}</td>
                    <td>{l.qty}</td>
                    <td>{l.uom}</td>
                    <td>{l.price}</td>
                    <td>
                      {failed ? (
                        <span className="badge b-fail"><i className="fa-solid fa-xmark" /> Not found</span>
                      ) : (
                        <span className="badge b-ok"><i className="fa-solid fa-check" /> Matched</span>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        <div className="row" style={{ justifyContent: 'flex-end', marginTop: 10 }}>
          <div className="grid" style={{ maxWidth: 340, width: '100%' }}>
            {isUsd ? (
              <div className="f">
                <label>ราคาสินค้าในเอกสาร (VAT)</label>
                <input type="text" value="สกุลเงิน USD — ไม่คิด VAT" disabled readOnly />
                <small className="master-field-help">
                  รายการสกุลเงิน USD (ส่งออก) ไม่คิด VAT — แสดงผลอย่างเดียว ไม่กระทบราคาที่ส่ง SAP
                </small>
              </div>
            ) : (
              <div className="f">
                <label>ราคาสินค้าในเอกสาร (VAT)</label>
                <select value={vatMode} onChange={(e) => setVatMode(e.target.value as 'document' | 'exclude' | 'include')}>
                  {docHasVat && <option value="document">เอกสารแยก VAT มาแล้ว — ใช้ค่าตามเอกสาร</option>}
                  <option value="exclude">ยังไม่รวม VAT — บวก 7% เพิ่ม</option>
                  <option value="include">รวม VAT แล้ว — ถอด 7% ออกมาแสดง</option>
                </select>
                <small className="master-field-help">
                  เลือกให้ตรงกับเอกสารลูกค้า — แสดงผลอย่างเดียว ไม่กระทบราคาที่ส่ง SAP
                  {docHasVat && ' · เอกสารนี้มี VAT มาแล้ว จึงตั้งค่าเริ่มต้นเป็น “ใช้ค่าตามเอกสาร”'}
                </small>
              </div>
            )}
            <div className="f">
              <label>Subtotal (Excl. VAT)</label>
              <input type="text" value={fmt(subtotal)} disabled readOnly />
            </div>
            <div className="f">
              <label>{isUsd ? 'VAT (ไม่คิด)' : `VAT (${(GLC_VAT_RATE * 100).toFixed(0)}%)`}</label>
              <input type="text" value={fmt(vatAmount)} disabled readOnly />
            </div>
            <div className="f">
              <label><b>Grand Total</b></label>
              <input type="text" value={fmt(grandTotal)} disabled readOnly style={{ fontWeight: 'bold' }} />
            </div>
          </div>
        </div>

        {notMatchedCount > 0 && (
          <p className="hint" style={{ marginTop: 10, color: 'var(--red, #c0392b)' }}>
            {notMatchedCount} line(s) above are not matched — see the Failed panel higher on this page, fix them, then
            re-run Step 2.
          </p>
        )}

        <div className="so-send" style={{ justifyContent: 'flex-end' }}>
          <div className="row" style={{ gap: 10 }}>
            <button className="btn success" onClick={onSend} disabled={SEND_DISABLED || sending || posted || !map.pass}>
              {sending ? 'Sending…' : posted ? 'ส่งสำเร็จ ✓' : 'Send'}
            </button>
            <button className="btn" onClick={onViewPayload}>
              {'{}'} View Payload
            </button>
          </div>
        </div>
      </>
    );
  };

  return (
    <div className="card">
      <div className="card-h">
        <h2>สรุป Sales Order ที่จะส่งไป SAP</h2>
        <div className="sp" />
        {map && (
          <span className="badge b-idle">
            {lines.length} line(s) · will send {willSendCount}
            {notMatchedCount ? ` · not sent ${notMatchedCount}` : ''}
          </span>
        )}
      </div>
      <div className="card-b">{body()}</div>
    </div>
  );
}
