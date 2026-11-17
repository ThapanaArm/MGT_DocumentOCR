import type { DocModel } from '../api/documents';
import { NEW_DEAL_ID, type ZohoDeal, type ZohoDealItem } from '../api/zoho';
import { num } from './format';

/* MGT / Zoho Deal matching (moved out of MappingCards, 16 Nov 2026).

   Deal search (Megachem, 10 Nov 2026):
   1. Deals of this Customer Code, Stage Draft/Qualification (only, 16 Nov 2026), whose
      Material Codes AND quantities match the PO.
   2. No Deal matches the quantities -> fall back to Customer Code + Material Code only, and the Deal
      card shows "Qty mismatch" (was "สินค้าไม่ครบ"; the UI is English only).
   PO rows of the same material are added together before the quantity is compared (one Deal item may
   be ordered on two PO rows with different delivery dates, equal or different quantities), and Deal
   items of the same material are added together too.

   One PO, several Deals (Megachem, 16 Nov 2026): the Deal is chosen PER ITEM. Each line stores its Deal
   key in line.extra.zohoDeal -- a Zoho Deal id, or "__new__", "__new__2" ... for a Deal that Send opens
   (items with no open Deal). The system assigns the items (autoAssignDeals) and CS can regroup them on
   the "Group items into Deals" board, like the Split Sales Order board. A Zoho Sales Order links to one
   Deal, so every Deal gets its own Quote and Sales Order(s). */

type Line = DocModel['lines'][number];

// Megachem, 16 Nov 2026: a PO's Deal must be at Draft/Qualification -- an existing one (even with Sales
// Orders already) or one Send opens. Was also Meeting/Memo and Sample & Result (10 Nov 2026).
export const DEAL_SEARCH_STAGES = ['draft/qualification'];
export const isDraftDeal = (d: { stage?: string | null }) => DEAL_SEARCH_STAGES.includes(dealStageKey(d.stage));
export const dealStageKey = (stage?: string | null) => String(stage || '').replace(/\s+/g, '').toLowerCase();

// A line's Material Code candidates: the code on the PO (OCR/CS) first, then the code mapped by
// Master Mapping / CS -- either may be the Zoho Material Code (the backend tries them in the same order).
export function lineCodes(line: Line) {
  return [...new Set([line.extCode, line.materialCode].map((c) => String(c || '').trim().toLowerCase()).filter(Boolean))];
}
export const dealItemCode = (it: ZohoDealItem) => String(it.materialCode || '').trim().toLowerCase();

// Unit names written differently on the PO and in Zoho ("Kilogram" vs "KG") count as the same unit.
const UNIT_ALIASES: Record<string, string> = {
  kilogram: 'kg', kilograms: 'kg', kgs: 'kg', 'kg.': 'kg',
  litre: 'l', litres: 'l', liter: 'l', liters: 'l', ltr: 'l', lt: 'l',
};
const unitKey = (unit?: string | null) => {
  const k = String(unit || '').trim().toLowerCase();
  return UNIT_ALIASES[k] ?? k;
};

// A quantity given in `unit`, converted into `base`'s Unit (1 Unit = Conversion Ratio Sub Units,
// same rule as the backend). null = the unit cannot be converted.
export function toDealUnit(qty: number, unit: string | null | undefined, base: ZohoDealItem): number | null {
  const from = unitKey(unit);
  const to = unitKey(base.unit);
  if (!from || !to || from === to) return qty;
  const ratio = Number(base.conversionRatio);
  return from === unitKey(base.subUnit) && ratio > 0 ? qty / ratio : null;
}

export type DealCodeCheck = {
  lines: Line[];
  items: ZohoDealItem[];
  poQty: number | null;
  dealQty: number | null;
  unit: string;
  qtyOk: boolean;
};

// Per Material Code that is on both the PO and the Deal: the PO total vs the Deal total, in the Deal's Unit.
export function dealCodeChecks(deal: ZohoDeal, lines: Line[]) {
  const dealCodes = new Set(deal.items.map(dealItemCode).filter(Boolean));
  const byCode = new Map<string, Line[]>();
  lines.forEach((line) => {
    const code = lineCodes(line).find((c) => dealCodes.has(c));
    if (code) byCode.set(code, [...(byCode.get(code) || []), line]);
  });
  const total = (values: (number | null)[]) =>
    values.every((v): v is number => v != null) ? values.reduce((sum, v) => sum + v, 0) : null;
  const checks = new Map<string, DealCodeCheck>();
  byCode.forEach((codeLines, code) => {
    const items = deal.items.filter((it) => dealItemCode(it) === code);
    if (!items.length) return;
    const base = items[0];
    const poQty = total(codeLines.map((l) => (num(l.qty) > 0 ? toDealUnit(num(l.qty), l.uom, base) : null)));
    const dealQty = total(items.map((it) => (Number(it.quantity) > 0 ? toDealUnit(Number(it.quantity), it.unit, base) : null)));
    checks.set(code, {
      lines: codeLines, items, poQty, dealQty, unit: base.unit || '',
      qtyOk: poQty != null && dealQty != null && Math.abs(poQty - dealQty) < 0.001,
    });
  });
  return checks;
}

export function dealProductMatch(deal: ZohoDeal, lines: Line[], requireQuantity = true) {
  let matched = 0;
  let byPoCode = 0; // lines matched by the code on the PO itself (stronger than a mapped code)
  dealCodeChecks(deal, lines).forEach((c, code) => {
    if (requireQuantity && !c.qtyOk) return;
    matched += c.lines.length;
    byPoCode += c.lines.filter((l) => lineCodes(l)[0] === code).length;
  });
  return { matched, byPoCode, total: lines.length, ok: lines.length > 0 && matched === lines.length };
}

/* ── One PO, several Deals ──────────────────────────────────────────────────────────────────── */

export const isNewDealKey = (key?: string | null) => !!key && key.startsWith(NEW_DEAL_ID);

/** The Deal key saved on a line (extra.zohoDeal), '' when none. */
export const lineDealKey = (line: Line) =>
  String((line.extra as Record<string, unknown> | undefined)?.zohoDeal ?? '').trim();

/** "__new__" -> 1, "__new__2" -> 2 ... */
export const newDealNo = (key: string) => Number(key.slice(NEW_DEAL_ID.length)) || 1;
export const newDealKey = (n: number) => (n <= 1 ? NEW_DEAL_ID : NEW_DEAL_ID + n);

/** Name shown for a Deal key: the Deal's name, or "New Deal 1/2/..." (just "New Deal" when there is one).
 *  Deals that share a name (same material, e.g. one per delivery date) get their Deal Number / date added. */
export function dealKeyLabel(key: string, deals: ZohoDeal[], keys?: string[]) {
  if (isNewDealKey(key)) {
    const several = (keys || []).filter(isNewDealKey).length > 1 || newDealNo(key) > 1;
    return several ? `New Deal ${newDealNo(key)}` : 'New Deal';
  }
  const d = deals.find((x) => x.id === key);
  if (!d) return key;
  const twin = deals.some((x) => x.id !== d.id && x.dealName.trim() === d.dealName.trim());
  return twin ? `${d.dealName} · ${[d.dealNumber, d.deliveryDate && `Delivery ${d.deliveryDate}`].filter(Boolean).join(' · ') || d.id}` : d.dealName;
}

/** A line's delivery date (YYYY-MM-DD as stored): its own, else the document's. */
export const lineDeliveryDate = (line: Line, headerDate?: string | null) =>
  String((line.extra as Record<string, unknown> | undefined)?.deliveryDate ?? '').trim() || String(headerDate || '').trim();

/** How well a PO row's delivery date fits a Deal's Delivery Date: 2 = same day, 1 = same month, 0 = no / unknown. */
export function deliveryDateFit(line: Line, deal: ZohoDeal, headerDate?: string | null) {
  const a = lineDeliveryDate(line, headerDate).slice(0, 10);
  const b = String(deal.deliveryDate || '').trim().slice(0, 10);
  if (!a || !b) return 0;
  return a === b ? 2 : a.slice(0, 7) === b.slice(0, 7) ? 1 : 0;
}

/** Distinct Deal keys in first-seen line order (the order the Sales Orders are created in). */
export const distinctKeys = (assignment: string[]) => [...new Set(assignment.filter(Boolean))];

/** A saved key that still points at something: a new Deal, or one of the Deals on the list. */
export const isKnownDealKey = (key: string, deals: ZohoDeal[]) => isNewDealKey(key) || deals.some((d) => d.id === key);

/**
 * The system's Deal for every line (Megachem, 16 Nov 2026). Lines are taken per material: all PO rows of
 * one material (e.g. several delivery dates) go to the same Deal, whose quantity is compared with their
 * total. Among the open Deals of this customer (Stage Draft/Qualification only
 * -- plus the Deal of a Quote No. the PO quotes) that carry the material, the order is:
 *   1. the Deal of the Quote No. on the PO;
 *   2. Deal quantity = PO quantity (else Material Code only -> "Qty mismatch" on the card);
 *   3. the Deal that also carries more of this PO's other materials (fewer Deals per PO);
 *   4. found by the code on the PO itself rather than the mapped code;
 *   5. Customer Ref = the PO no.;
 *   6. the newest Deal.
 * Either of a line's codes (PO code / mapped code) may be the Deal's Material Code.
 * Several open Deals with the same material (e.g. one Deal per delivery month, 16 Nov 2026): each PO row
 * goes to the Deal whose Delivery Date fits the row's delivery date (same day, then same month), then
 * whose quantity equals the row's; rows with no such hint stay on the Deal picked by the order above.
 * A line no open Deal carries goes to "__new__" (one new Deal, not WCD) -- CS can split it on the board.
 */
export function autoAssignDeals(
  deals: ZohoDeal[],
  lines: Line[],
  opts: { poNo?: string | null; quoteDealId?: string | null; customerCode?: string | null; headerDate?: string | null } = {},
): string[] {
  const cust = String(opts.customerCode || '').trim().toLowerCase();
  const sameCustomer = (d: ZohoDeal) => !!cust && String(d.accountCode || '').trim().toLowerCase() === cust;
  // The Deal of a Quote No. on the PO counts only when it is at Draft/Qualification too (16 Nov 2026).
  const quoteDeal = opts.quoteDealId
    ? deals.find((d) => d.id === opts.quoteDealId && sameCustomer(d) && DEAL_SEARCH_STAGES.includes(dealStageKey(d.stage)))
    : undefined;
  const eligible = deals.filter((d) => d === quoteDeal || (DEAL_SEARCH_STAGES.includes(dealStageKey(d.stage)) && sameCustomer(d)));
  const carries = (d: ZohoDeal, code: string) => d.items.some((it) => dealItemCode(it) === code);

  const out = lines.map(() => NEW_DEAL_ID);
  // Material code (as found on some open Deal) -> its PO rows.
  const byCode = new Map<string, number[]>();
  lines.forEach((l, i) => {
    const code = lineCodes(l).find((c) => eligible.some((d) => carries(d, c)));
    if (code) byCode.set(code, [...(byCode.get(code) || []), i]);
  });
  const po = String(opts.poNo || '').trim().toLowerCase();
  const refMatch = (d: ZohoDeal) => (po && String(d.customerRef || '').trim().toLowerCase() === po ? 1 : 0);
  const created = (d: ZohoDeal) => Date.parse(String(d.createdTime || '')) || 0;
  const onDeal = (d: ZohoDeal, l: Line) => lineCodes(l).some((c) => carries(d, c));
  // How many of this PO's materials the Deal carries.
  const groups = [...byCode.values()].map((idx) => idx.map((i) => lines[i]));
  const coverage = (d: ZohoDeal) => groups.filter((rows) => rows.some((l) => onDeal(d, l))).length;

  byCode.forEach((idx) => {
    const rows = idx.map((i) => lines[i]);
    const qtyOk = (d: ZohoDeal) => {
      const checks = [...dealCodeChecks(d, rows).values()];
      return checks.length > 0 && checks.every((c) => c.qtyOk) ? 1 : 0;
    };
    const byPoCode = (d: ZohoDeal) => (rows.some((l) => !!lineCodes(l)[0] && carries(d, lineCodes(l)[0])) ? 1 : 0);
    const ranked = eligible
      .filter((d) => rows.every((l) => onDeal(d, l)))
      .sort((a, b) => Number(b === quoteDeal) - Number(a === quoteDeal)
        || qtyOk(b) - qtyOk(a)
        || coverage(b) - coverage(a)
        || byPoCode(b) - byPoCode(a)
        || refMatch(b) - refMatch(a)
        || created(b) - created(a));
    const best = ranked[0];
    if (!best) return;
    if (ranked.length > 1 && best !== quoteDeal) {
      // Several open Deals carry this material: put each row on the Deal its delivery date (then its
      // own quantity) fits best; ties keep the order above.
      const rowQtyOk = (l: Line, d: ZohoDeal) => ([...dealCodeChecks(d, [l]).values()][0]?.qtyOk ? 1 : 0);
      const per = idx.map((i) => {
        let pick = best;
        let score = 0;
        ranked.forEach((d) => {
          const s = deliveryDateFit(lines[i], d, opts.headerDate) * 2 + rowQtyOk(lines[i], d);
          if (s > score) { score = s; pick = d; }
        });
        return { i, pick };
      });
      if (per.some((p) => p.pick !== best)) {
        per.forEach((p) => { out[p.i] = p.pick.id; });
        return;
      }
    }
    idx.forEach((i) => { out[i] = best.id; });
  });
  return out;
}

/** The Deal behind a key, or null for a new Deal / unknown key. */
export const dealOfKey = (key: string, deals: ZohoDeal[]) => (isNewDealKey(key) ? null : deals.find((d) => d.id === key) ?? null);
