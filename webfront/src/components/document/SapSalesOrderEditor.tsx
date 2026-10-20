import { useState } from 'react';
import type { DocHeader, DocLine, DocModel, MapResult } from '../../api/documents';
import { SEND_DISABLED } from '../../constants/flags';
import { fmt, num } from '../../utils/format';
import type { SoGroupState } from '../../api/documents';
import SoGroupPanel, { groupStatus, type GroupShipToControl } from './SoGroupPanel';
import type { SapSalesEmployee } from '../../api/sap';
import { isFreightLine, totalFreight, freightByGroup, savedFreightSplit } from '../../utils/soCharges';
import { formatDate } from '../common/DateInput';

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

/** skipCharges (GLC): freight lines are not items of any Sales Order (see utils/soCharges), same as
 *  SoDeliveryGroups.Build(skipCharges: true) on the backend. */
export function deliveryDateGroups(doc: DocModel, headerDate: string, skipCharges = false): DeliveryDateGroup[] {
  const order: string[] = [];
  const byKey = new Map<string, DeliveryDateGroup>();
  (doc.lines || []).forEach((l, i) => {
    if (skipCharges && isFreightLine(l)) return;
    const soGroup = lineSoGroup(l);
    const rawDate = ((l.extra as Record<string, string> | undefined)?.deliveryDate || '').trim() || (headerDate || '').trim();
    const key = `${soGroup}|${rawDate}`;
    let g = byKey.get(key);
    if (!g) {
      g = { key, soGroup, rawDate, date: rawDate ? formatDate(rawDate) : 'Delivery date not specified', itemNos: [], count: 0 };
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
  onFreightSplitChange,
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
  /** Saves how the freight is shared between the Sales Orders (header.soGroupFreight); null = all on the first. */
  onFreightSplitChange?: (split: Record<string, number> | null) => void;
}) {
  const lines: DocLine[] = doc.lines || [];
  const lineStatus = (i: number) => map?.lines?.[i]?.status;
  // Freight lines (SHIPPING / ค่าขนส่ง): not items -- sent as the Freight condition on the SO header.
  const isCharge = (i: number) => lineStatus(i) === 'charge' || (!map && isFreightLine(lines[i]));
  const willSendCount = map ? lines.filter((_, i) => lineStatus(i) !== 'fail' && !isCharge(i)).length : 0;
  const freightTotal = totalFreight(lines);

  // GLC: every line needs its own Sales Employee (SAP item field); the header Sales Group is the
  // first item's Sales Employee's Division (Ms_User.Division) -- same rule as SapPayloadBuilder.
  const isTaxLine = (l: DocLine) => ['WHT', 'VAT'].includes(String(l.extCode || '').toUpperCase()) || isFreightLine(l);
  const empOf = (l: DocLine) => String((l.extra as Record<string, string> | undefined)?.salesEmployee || '').trim();
  const missingSalesEmp = lines.filter((l) => !isTaxLine(l) && !empOf(l)).map((l) => l.itemNo);
  const firstLine = lines.find((l) => !isTaxLine(l));
  const firstEmp = firstLine ? salesEmployees.find((s) => s.personId === empOf(firstLine)) : undefined;
  const firstEmpName = firstEmp?.name || (firstLine ? empOf(firstLine) : '');
  const salesGroup = String(firstEmp?.salesGroup || header.salesGroup || '').trim();
  const notMatchedCount = map ? lines.filter((_, i) => lineStatus(i) === 'fail').length : 0;

  // Delivery-date grouping (Megachem rule): one SAP Sales Order per delivery date, all sent from this
  // document by one Send. Groups already posted are skipped on the next Send (retry = the rest).
  const dateGroups = deliveryDateGroups(doc, header.deliveryDate || '', true);
  const remaining = dateGroups.filter((g) => !groupStatus(g, posts).ok).length;
  // Freight per Sales Order: all on the first by default, or the CS's own split (header.soGroupFreight).
  const freightPerGroup = freightByGroup(header, lines, dateGroups.map((g) => g.key));
  const freightSplitSum = Math.round(Object.values(freightPerGroup).reduce((s, v) => s + v, 0) * 100) / 100;
  const hasOwnSplit = Object.keys(savedFreightSplit(header)).some((k) => k in freightPerGroup);
  const [freightDraft, setFreightDraft] = useState<Record<string, string>>({});

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
  // 16 Oct 2026: the "Prices exclude VAT" choice was taken off the screen (it sent exactly what "No VAT"
  // sends -- the prices as read). A document saved earlier as 'exclude' now opens as 'none'.
  const defaultVatMode: VatMode = docHasVat ? 'document' : 'none';
  const [vatModeState, setVatModeState] = useState<VatMode | null>(
    savedVatMode === 'exclude'
      ? 'none'
      : (['document', 'include', 'none'] as const).includes(savedVatMode) ? savedVatMode : null,
  );
  // "document" is offered only while the document has its own VAT figure; fall back when it goes.
  const vatMode: VatMode = vatModeState === 'document' && !docHasVat ? 'none' : vatModeState ?? defaultVatMode;
  const setVatMode = (m: VatMode) => {
    setVatModeState(m);
    onVatModeChange?.(m);
  };

  // What SAP actually receives (same rule as SapPayloadBuilder): each line price -- and the freight --
  // as on the document, or with the 7% taken out (÷ 1.07) when the document prices include VAT.
  // SAP works out VAT and the order total itself; the VAT / Grand Total further down are a preview only.
  const stripVat = vatMode === 'include' && !isUsd;
  const sentPrice = (p: unknown) => round2(stripVat ? num(p) / (1 + GLC_VAT_RATE) : num(p));

  // Sum of the lines that will actually be sent (unmatched lines aren't part of the SO SAP sees),
  // plus the freight SAP adds on the header (Net Value + Freight = Total Amt+Freight, VAT on that).
  const itemTotal = lines.reduce(
    (sum, l, i) => (map && lineStatus(i) !== 'fail' && !isCharge(i) ? sum + num(l.qty) * num(l.price) : sum),
    0,
  );
  const lineTotal = itemTotal + (map ? freightTotal : 0);
  const sentItemTotal = lines.reduce(
    (sum, l, i) => (map && lineStatus(i) !== 'fail' && !isCharge(i) ? sum + num(l.qty) * sentPrice(l.price) : sum),
    0,
  );
  const sentFreight = map ? sentPrice(freightTotal) : 0;
  const sentNet = round2(sentItemTotal + sentFreight);
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
            <input type="text" value={formatDate(header.poDate)} disabled readOnly />
          </div>
          <div className="f">
            <label>Delivery Date</label>
            <input type="text" value={formatDate(header.deliveryDate)} disabled readOnly />
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
                <th>Price (document)</th>
                <th className="sap-sent-col">Price sent to SAP</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((l, i) => {
                const m = map.lines?.[i];
                const failed = m?.status === 'fail';
                if (m?.status === 'charge')
                  return (
                    <tr key={i}>
                      <td>{l.itemNo ?? i + 1}</td>
                      <td><b>Freight</b><div className="hint">header condition, not an item</div></td>
                      <td>{l.desc}</td>
                      <td>—</td>
                      <td>—</td>
                      <td>{fmt(num(l.amount) || num(l.qty) * num(l.price))}</td>
                      <td className="sap-sent-col">
                        {fmt(sentPrice(num(l.amount) || num(l.qty) * num(l.price)))}
                        <div className="hint">{stripVat ? 'VAT taken out' : 'as on the document'}</div>
                      </td>
                      <td><span className="badge b-idle"><i className="fa-solid fa-truck" /> Freight</span></td>
                    </tr>
                  );
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
                    <td className="sap-sent-col">
                      {failed ? (
                        <span className="hint">not sent</span>
                      ) : (
                        <>
                          {fmt(sentPrice(l.price))}
                          <div className="hint">{stripVat ? 'VAT taken out' : 'as on the document'}</div>
                        </>
                      )}
                    </td>
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

        {/* VAT choice, preview, then what SAP receives (last, like a final total) -- stacked in one
            compact column on the right under the items. */}
        <div className="so-sum-band">
          <div className="so-sum-col so-sum-vat">
            <div className="sap-box-title">
              <i className="fa-solid fa-percent" aria-hidden="true" /> Item price in document (VAT)
            </div>
            {isUsd ? (
              <>
                <input type="text" value="Currency USD — no VAT" disabled readOnly aria-label="Item price in document (VAT)" />
                <small className="so-sum-note">USD (export) — no Thai VAT; the highlighted prices are sent to SAP as they are.</small>
              </>
            ) : (
              <>
                <select value={vatMode} onChange={(e) => setVatMode(e.target.value as VatMode)} aria-label="Item price in document (VAT)">
                  {docHasVat && <option value="document">Document shows VAT separately</option>}
                  <option value="include">Prices include VAT</option>
                  <option value="none">No VAT on the document</option>
                </select>
                <small className="so-sum-note">
                  {vatMode === 'include'
                    ? 'The document prices already include 7% VAT — the system takes it out and sends the highlighted values. SAP then calculates VAT itself.'
                    : 'The highlighted prices are sent to SAP exactly as on the document. SAP calculates VAT itself.'}
                </small>
              </>
            )}
          </div>

          <div className="so-sum-col sap-preview-box">
            <div className="sap-box-title">
              <i className="fa-solid fa-calculator" aria-hidden="true" /> Preview only — not sent
            </div>
            <div className="so-sum-row"><span>Subtotal (Excl. VAT)</span><span>{fmt(subtotal)}</span></div>
            <div className="so-sum-row">
              <span>{isUsd ? 'VAT (none)' : `VAT ${(GLC_VAT_RATE * 100).toFixed(0)}% — calculated by SAP`}</span>
              <span>{fmt(vatAmount)}</span>
            </div>
            <div className="so-sum-row total"><span>Grand Total (estimate)</span><span>{fmt(grandTotal)}</span></div>
            <small className="so-sum-note">SAP calculates VAT and the total itself — only for checking against the customer&apos;s document.</small>
          </div>

          <div className="so-sum-col sap-sent-box">
            <div className="sap-box-title">
              <i className="fa-solid fa-paper-plane" aria-hidden="true" /> Sent to SAP
            </div>
            {freightTotal > 0 && (
              <>
                <div className="so-sum-row"><span>Items</span><b>{fmt(round2(sentItemTotal))}</b></div>
                <div className="so-sum-row"><span>Freight (ZFHD)</span><b>{fmt(sentFreight)}</b></div>
              </>
            )}
            <div className="so-sum-row total"><span>Net value sent to SAP</span><b>{fmt(sentNet)}</b></div>
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

        {freightTotal > 0 && dateGroups.length > 1 && (
          <div className="so-group-notice" style={{ marginTop: 12, display: 'block' }}>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              <i className="fa-solid fa-truck" aria-hidden="true" />
              <b>Freight {fmt(freightTotal)} {header.currency || 'THB'} — which Sales Order carries it?</b>
              <span className="hint">
                It is billed once: {hasOwnSplit ? 'shared as below' : 'all on the first Sales Order'}. Share it out if the
                customer wants the freight on each order.
              </span>
              {hasOwnSplit && !posted && !partial && onFreightSplitChange && (
                <button className="btn sm" onClick={() => { setFreightDraft({}); onFreightSplitChange(null); }}>
                  All on the first Sales Order
                </button>
              )}
            </div>
            <table style={{ marginTop: 8 }}>
              <tbody>
                {dateGroups.map((g, gi) => (
                  <tr key={g.key}>
                    <td>SO {gi + 1}</td>
                    <td className="hint">{g.date} · {g.count} item(s)</td>
                    <td style={{ width: 160 }}>
                      <input
                        type="text"
                        inputMode="decimal"
                        disabled={posted || partial || !onFreightSplitChange || !!groupStatus(g, posts).ok}
                        value={freightDraft[g.key] ?? String(freightPerGroup[g.key] ?? 0)}
                        onChange={(e) => setFreightDraft((d) => ({ ...d, [g.key]: e.target.value }))}
                        onBlur={() => {
                          if (freightDraft[g.key] === undefined || !onFreightSplitChange) return;
                          const next: Record<string, number> = { ...freightPerGroup };
                          next[g.key] = Math.max(0, round2(num(freightDraft[g.key])));
                          setFreightDraft((d) => { const c = { ...d }; delete c[g.key]; return c; });
                          onFreightSplitChange(next);
                        }}
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {Math.abs(freightSplitSum - freightTotal) > 0.009 && (
              <div style={{ marginTop: 6, color: 'var(--red, #c0392b)' }}>
                The Sales Orders carry {fmt(freightSplitSum)} but the document's freight is {fmt(freightTotal)} — the customer
                would be billed {freightSplitSum > freightTotal ? 'more' : 'less'} than the PO.
              </div>
            )}
          </div>
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
