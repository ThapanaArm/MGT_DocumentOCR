import { useState } from 'react';
import type { DocHeader, DocLine, DocModel, MapResult } from '../../api/documents';
import { SEND_DISABLED } from '../../constants/flags';
import { fmt, num } from '../../utils/format';
import type { SoGroupState } from '../../api/documents';
import SoGroupPanel, { groupStatus, type GroupShipToControl } from './SoGroupPanel';
import type { SapSalesEmployee } from '../../api/sap';

// GLC only (this whole editor is the SAP-side, GLC-only "check before you send" screen -- see the
// module doc comment below): the Subtotal/VAT/Grand Total block below is purely an OCR-review
// DISPLAY -- it does not change what gets POSTed to SAP (the payload/pricing logic is untouched;
// see onViewPayload for the actual request body). 7% is Thailand's standard VAT rate, same rate
// this codebase already assumes elsewhere (see the AP tax code list's "V1 - Input VAT 7%"/"D1 -
// Input Deferred Tax 7%" in constants/fields.ts) -- not currently configurable since nothing else
// in this feature needs it to be; if that changes, this should move into AppConfig the same way
// SapSalesOrderPriceConditionType did.
const GLC_VAT_RATE = 0.07;
type VatMode = 'document' | 'exclude' | 'include' | 'none';
const round2 = (n: number) => Math.round(n * 100) / 100;

/* How one Sales Order document becomes several SAP / Zoho Sales Orders (12 Oct 2026). Every line
   lands in exactly ONE group, keyed by
     1. its SO Group -- a number the CS types per line in the DETAIL table (extra.soGroup) to keep
        products on separate Sales Orders; no business rule, the person decides; blank = 1, and
     2. its delivery date -- extra.deliveryDate, else the header date (Megachem rule: one Sales Order
        per delivery date).
   One key per line means a line can never be sent twice, however the order is cut. The ship-to is
   chosen per SO Group (header.soGroupShipTos); a group without one uses the document's ship-to.
   Ordered by SO Group number, then first-seen date, so "SO 1 / SO 2" stays stable.
   Mirrors MgtOcr.Core.Mapping.SoDeliveryGroups — keep the two in step (same key format). */
export interface DeliveryDateGroup {
  /** Grouping key exactly as the backend builds it (SoDeliveryGroups.KeyOf): "{soGroup}|{date}". */
  key: string;
  /** SO Group number ("1", "2", ...). */
  soGroup: string;
  /** Delivery date as stored (YYYY-MM-DD) or '' when none. */
  rawDate: string;
  /** Display label: the date, or a concise fallback when no date is available. */
  date: string;
  itemNos: number[];
  count: number;
}

/** The line's SO Group as a canonical string; blank/invalid = "1" (same as the backend). */
export function lineSoGroup(line: DocLine): string {
  const raw = String((line.extra as Record<string, string> | undefined)?.soGroup ?? '').trim();
  const n = /^\d+$/.test(raw) ? parseInt(raw, 10) : 0;
  return n > 0 ? String(n) : '1';
}

export function deliveryDateGroups(doc: DocModel, headerDate: string): DeliveryDateGroup[] {
  const order: string[] = [];
  const byKey = new Map<string, DeliveryDateGroup>();
  (doc.lines || []).forEach((l, i) => {
    const soGroup = lineSoGroup(l);
    const rawDate = ((l.extra as Record<string, string> | undefined)?.deliveryDate || '').trim() || (headerDate || '').trim();
    const key = `${soGroup}|${rawDate}`;
    let g = byKey.get(key);
    if (!g) {
      g = { key, soGroup, rawDate, date: rawDate || 'Delivery date not specified', itemNos: [], count: 0 };
      byKey.set(key, g);
      order.push(key);
    }
    g.itemNos.push(Number(l.itemNo ?? (i + 1) * 10));
    g.count++;
  });
  // Array.prototype.sort is stable, so within one SO Group the first-seen date order is kept.
  return order.map((k) => byKey.get(k)!).sort((a, b) => Number(a.soGroup) - Number(b.soGroup));
}

/** Alias with the newer name -- the groups are no longer by delivery date alone. */
export const soGroups = deliveryDateGroups;

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
  posts = [],
  partial = false,
  groupShipTo,
  onVatModeChange,
  salesEmployees = [],
  unitIssues = [],
  onFixUnits,
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
  /** Send results per group (SO Group + delivery date) — shown in the group panel. */
  posts?: SoGroupState[];
  /** Some groups already posted (status PARTIAL): only the remaining ones can be sent. */
  partial?: boolean;
  /** Per-SO-Group ship-to picker (SAP ship-to parties of the sold-to). */
  groupShipTo?: GroupShipToControl;
  /** Saves the chosen VAT mode on the document (header.vatMode) -- 'include' changes the SAP price. */
  onVatModeChange?: (mode: string) => void;
  /** Sales Employee pick list (Person ID -> name, Sales Group) for the summary. */
  salesEmployees?: SapSalesEmployee[];
  /** Lines whose unit SAP doesn't know for their material (see SapSalesOrderStep). */
  unitIssues?: { itemNo: unknown; uom: string }[];
  onFixUnits?: () => void;
}) {
  const lines: DocLine[] = doc.lines || [];
  const lineStatus = (i: number) => map?.lines?.[i]?.status;
  const willSendCount = map ? lines.filter((_, i) => lineStatus(i) !== 'fail').length : 0;

  // GLC: every line needs its own Sales Employee (SAP item field); the header Sales Group is the
  // first item's Sales Employee's Division (Ms_User.Division) -- same rule as SapPayloadBuilder.
  const isTaxLine = (l: DocLine) => ['WHT', 'VAT'].includes(String(l.extCode || '').toUpperCase());
  const empOf = (l: DocLine) => String((l.extra as Record<string, string> | undefined)?.salesEmployee || '').trim();
  const missingSalesEmp = lines.filter((l) => !isTaxLine(l) && !empOf(l)).map((l) => l.itemNo);
  const firstLine = lines.find((l) => !isTaxLine(l));
  const firstEmp = firstLine ? salesEmployees.find((s) => s.personId === empOf(firstLine)) : undefined;
  const firstEmpName = firstEmp?.name || (firstLine ? empOf(firstLine) : '');
  const salesGroup = String(firstEmp?.salesGroup || header.salesGroup || '').trim();
  const notMatchedCount = map ? lines.filter((_, i) => lineStatus(i) === 'fail').length : 0;

  // Delivery-date grouping (Megachem rule): one SAP Sales Order per delivery date, all sent from this
  // document by one Send. Groups already posted are skipped on the next Send (retry = the rest).
  const dateGroups = deliveryDateGroups(doc, header.deliveryDate || '');
  const remaining = dateGroups.filter((g) => !groupStatus(g, posts).ok).length;

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
  // This is a per-document human decision (the CS can tell from the customer's document which it is).
  // Saved on the document (header.vatMode, 12 Oct 2026). 'include' ALSO changes what is sent: SAP
  // adds VAT itself, so the price goes out with the 7% taken out (107 -> 100); otherwise the
  // customer would be billed VAT twice. The other modes send the prices as read.
  //  - 'none': leave VAT alone entirely -> show the line prices as they are, VAT = 0, no 7% added
  //    or extracted (added 7 Oct 2026 at the user's request).
  const savedVatMode = String(header.vatMode || '') as VatMode;
  const defaultVatMode: VatMode = docHasVat ? 'document' : 'exclude';
  const [vatModeState, setVatModeState] = useState<VatMode | null>(
    (['document', 'exclude', 'include', 'none'] as const).includes(savedVatMode) ? savedVatMode : null,
  );
  // "document" is offered only while the document has its own VAT figure; fall back when it goes.
  const vatMode: VatMode = vatModeState === 'document' && !docHasVat ? 'exclude' : vatModeState ?? defaultVatMode;
  const setVatMode = (m: VatMode) => {
    setVatModeState(m);
    onVatModeChange?.(m);
  };

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
  } else if (vatMode === 'none') {
    // 'none': no VAT handling at all — Subtotal = Grand Total = lineTotal, VAT = 0.
    subtotal = round2(lineTotal);
    vatAmount = 0;
    grandTotal = round2(lineTotal);
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
            <input
              type="text"
              value={
                [map.header.shipTo?.code, map.header.shipTo?.text].filter((x) => String(x || '').trim()).join(' — ') ||
                'Same as Sold-to'
              }
              disabled
              readOnly
            />
            {Object.keys(groupShipTo?.current || {}).length > 0 && (
              <div className="hint">Some SO Groups use their own Ship-to — see the Sales Orders table below</div>
            )}
          </div>
          <div className="f">
            <label>Customer PO</label>
            {/* No PO number on the document -> "-" is what SAP receives (Megachem rule 8). */}
            <input type="text" value={String(header.poNo || '').trim() || '-'} disabled readOnly />
          </div>
          <div className="f">
            <label>Sales Group</label>
            <input type="text" value={salesGroup || '—'} disabled readOnly />
            <small className="master-field-help">
              From the first item's Sales Employee{firstEmpName ? ` (${firstEmpName})` : ''} — each Sales Order uses its own first item
            </small>
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
                <th>Item (SAP)</th>
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
                <label>Item price in document (VAT)</label>
                <input type="text" value="Currency USD — no VAT" disabled readOnly />
                <small className="master-field-help">
                  USD (export) line — no VAT; display only, does not affect the price sent to SAP
                </small>
              </div>
            ) : (
              <div className="f">
                <label>Item price in document (VAT)</label>
                <select value={vatMode} onChange={(e) => setVatMode(e.target.value as VatMode)}>
                  {docHasVat && <option value="document">Document already separates VAT — use the document value</option>}
                  <option value="exclude">VAT not included — add 7%</option>
                  <option value="include">VAT included — send prices without VAT (÷ 1.07)</option>
                  <option value="none">No VAT — leave prices as they are</option>
                </select>
                <small className="master-field-help">
                  {vatMode === 'include'
                    ? 'Prices on the document already include 7% VAT — SAP receives each price ÷ 1.07 and adds VAT itself'
                    : 'Match the customer document — the price sent to SAP is the price on the line'}
                  {docHasVat && ' · this document already has VAT, so the default is set to "use the document value"'}
                </small>
              </div>
            )}
            <div className="f">
              <label>Subtotal (Excl. VAT)</label>
              <input type="text" value={fmt(subtotal)} disabled readOnly />
            </div>
            <div className="f">
              <label>{isUsd ? 'VAT (none)' : `VAT (${(GLC_VAT_RATE * 100).toFixed(0)}%)`}</label>
              <input type="text" value={fmt(vatAmount)} disabled readOnly />
            </div>
            <div className="f">
              <label><b>Grand Total</b></label>
              <input type="text" value={fmt(grandTotal)} disabled readOnly style={{ fontWeight: 'bold' }} />
            </div>
          </div>
        </div>

        {unitIssues.length > 0 && (
          <div className="so-group-notice" role="alert" style={{ marginTop: 12, alignItems: 'center' }}>
            <i className="fa-solid fa-triangle-exclamation" aria-hidden="true" />
            <span style={{ flex: 1 }}>
              SAP doesn&apos;t know the unit of item{' '}
              <b>{unitIssues.map((x) => `${String(x.itemNo)} (${x.uom || 'no unit'})`).join(', ')}</b> for its material —
              change it before sending.
            </span>
            {onFixUnits && !posted && (
              <button className="btn sm primary" onClick={onFixUnits}>Change unit</button>
            )}
          </div>
        )}
        {missingSalesEmp.length > 0 && (
          <div className="so-group-notice" role="alert" style={{ marginTop: 12 }}>
            <i className="fa-solid fa-triangle-exclamation" aria-hidden="true" />
            <span>
              Sales Employee is required for SAP — pick it for item <b>{missingSalesEmp.join(', ')}</b> in each
              item's Material card above (<b>Sales Employee — this line</b>), then send.
            </span>
          </div>
        )}
        {notMatchedCount > 0 && (
          <p className="hint" style={{ marginTop: 10, color: 'var(--red, #c0392b)' }}>
            {notMatchedCount} line(s) above are not matched — see the Failed panel higher on this page, fix them, then
            re-run Step 2.
          </p>
        )}

        <SoGroupPanel groups={dateGroups} posts={posts} target="SAP" shipTo={groupShipTo} />

        <div className="so-send" style={{ justifyContent: 'flex-end' }}>
          <div className="row" style={{ gap: 10 }}>
            <button
              className="btn success"
              onClick={onSend}
              disabled={SEND_DISABLED || sending || posted || (!partial && !map.pass) || remaining === 0 || missingSalesEmp.length > 0}
            >
              {sending
                ? 'Sending…'
                : posted
                  ? 'Sent ✓'
                  : dateGroups.length > 1
                    ? `Send ${remaining} Sales Order${remaining === 1 ? '' : 's'}`
                    : 'Send'}
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
        <h2>Sales Order summary to send to SAP</h2>
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
