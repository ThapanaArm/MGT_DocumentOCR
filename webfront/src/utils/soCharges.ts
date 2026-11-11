import type { DocHeader, DocLine } from '../api/documents';

/* GLC Sales Orders (15 Oct 2026): a PO line that is a shipping charge, not goods ("SHIPPING /
   ค่าขนส่ง 1 ครั้ง 200.00"), is not mapped to a Material and not sent as an item -- its amount goes
   on the SAP Sales Order header as the Freight condition (ZFHD).
   A line is freight when the person said so (extra.charge = 'freight'), or -- unless they said it
   is an item (extra.charge = 'item') -- when its code or text reads like a shipping charge.
   Mirrors MgtOcr.Core.Mapping.SoCharges -- keep the two word lists in step. */

const FREIGHT_CODES = new Set([
  'SHIPPING', 'SHIPMENT', 'FREIGHT', 'DELIVERY', 'TRANSPORT', 'TRANSPORTATION', 'CARRIAGE', 'ค่าขนส่ง', 'ค่าส่ง',
]);
const FREIGHT_TEXT =
  /ค่าขนส่ง|ค่าจัดส่ง|ค่าบริการขนส่ง|ค่าบริการจัดส่ง|ค่าส่งสินค้า|ค่ารถขนส่ง|ค่าขนย้าย|\bfreight\b|\bshipping\b|\bdelivery (charge|fee|cost)s?\b|\btransport(ation)? (charge|fee|cost)s?\b|\bcarriage\b/i;

const n = (v: unknown) => {
  const x = typeof v === 'number' ? v : parseFloat(String(v ?? '').replace(/,/g, ''));
  return Number.isFinite(x) ? x : 0;
};

export type ChargeChoice = 'freight' | 'item' | '';

export function lineChargeChoice(line: DocLine): ChargeChoice {
  const c = String((line.extra as Record<string, unknown> | undefined)?.charge ?? '').trim().toLowerCase();
  return c === 'freight' || c === 'item' ? c : '';
}

export function looksLikeFreight(line: DocLine): boolean {
  const code = String(line.extCode ?? '').trim();
  const desc = String(line.desc ?? '').trim();
  if (code && FREIGHT_CODES.has(code.toUpperCase())) return true;
  if (desc && FREIGHT_CODES.has(desc.toUpperCase())) return true;
  return FREIGHT_TEXT.test(code) || FREIGHT_TEXT.test(desc);
}

export function isFreightLine(line: DocLine): boolean {
  const c = lineChargeChoice(line);
  return c === 'freight' ? true : c === 'item' ? false : looksLikeFreight(line);
}

/** Amount, or Quantity x Unit Price when no amount was read. */
export function chargeAmount(line: DocLine): number {
  const a = n(line.amount);
  if (a > 0) return a;
  const q = n(line.qty);
  const p = n(line.price);
  return q > 0 && p > 0 ? Math.round(q * p * 100) / 100 : 0;
}

export function totalFreight(lines: DocLine[]): number {
  return Math.round(lines.filter(isFreightLine).reduce((s, l) => s + chargeAmount(l), 0) * 100) / 100;
}

/** header.soGroupFreight -- the person's split of the freight per Sales Order (group key -> amount). */
export function savedFreightSplit(header: DocHeader): Record<string, number> {
  const raw = (header as Record<string, unknown>).soGroupFreight;
  if (!raw || typeof raw !== 'object') return {};
  const out: Record<string, number> = {};
  for (const [k, v] of Object.entries(raw as Record<string, unknown>)) out[k] = Math.max(0, Math.round(n(v) * 100) / 100);
  return out;
}

/* Freight per Sales Order, same rule as SoCharges.FreightByGroup: the saved split when it names at
   least one current group (a group not in it gets 0), otherwise the whole amount on the first. */
export function freightByGroup(header: DocHeader, lines: DocLine[], groupKeys: string[]): Record<string, number> {
  const out: Record<string, number> = Object.fromEntries(groupKeys.map((k) => [k, 0]));
  const total = totalFreight(lines);
  if (total <= 0 || groupKeys.length === 0) return out;
  const split = savedFreightSplit(header);
  if (Object.keys(split).some((k) => k in out)) {
    for (const [k, v] of Object.entries(split)) if (k in out) out[k] = v;
    return out;
  }
  out[groupKeys[0]] = total;
  return out;
}
