import type { DocModel, DocLine } from '../api/documents';
import { vendorInvoiceTotals } from '../components/document/ShippingCheck';
import { isVatTaxRow, syncGlTaxCodes } from './glTaxCodes';

type Path = (string | number)[];
export type InvoiceChange = { path: Path; before: unknown; after: unknown };
type DraftFields = Pick<DocModel, 'header' | 'lines'>;

const isRecord = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
const missing = (value: unknown) => value == null || (typeof value === 'string' && !value.trim());
const safeKey = (key: string) => !['__proto__', 'prototype', 'constructor'].includes(key);
const same = (a: unknown, b: unknown) => Object.is(a, b) || JSON.stringify(a) === JSON.stringify(b);

export function invoiceChanges(before: DraftFields, after: DraftFields): InvoiceChange[] {
  const changes: InvoiceChange[] = [];
  const compare = (a: unknown, b: unknown, path: Path) => {
    if (Object.is(a, b)) return;
    if (Array.isArray(a) && Array.isArray(b) && a.length === b.length) {
      a.forEach((value, i) => compare(value, b[i], [...path, i]));
    } else if (isRecord(a) && isRecord(b)) {
      for (const key of new Set([...Object.keys(a), ...Object.keys(b)])) {
        if (safeKey(key)) compare(a[key], b[key], [...path, key]);
      }
    } else if (!same(a, b)) changes.push({ path, before: a, after: b });
  };
  compare(before.header, after.header, ['header']);
  compare(before.lines, after.lines, ['lines']);
  return changes;
}

function valueAt(value: unknown, path: Path): unknown {
  for (const key of path) {
    if (Array.isArray(value) && typeof key === 'number') value = value[key];
    else if (isRecord(value) && typeof key === 'string') value = value[key];
    else return undefined;
  }
  return value;
}

export function applyInvoiceChanges(doc: DocModel, base: DraftFields, changes: InvoiceChange[]) {
  const conflicts = changes.filter(({ path, before, after }) => {
    // A deletion or reordering can shift a numeric row path onto another line.
    if (path[0] === 'lines' && typeof path[1] === 'number') {
      if (base.lines.length !== doc.lines.length || base.lines[path[1]]?.itemNo !== doc.lines[path[1]]?.itemNo) return true;
    } else if (path[0] === 'header' && typeof path[2] === 'number') {
      // Header collections have no stable row ID. Review again if the collection changed.
      if (!same(valueAt(base, path.slice(0, 2)), valueAt(doc, path.slice(0, 2)))) return true;
    }
    const current = valueAt(doc, path);
    return !same(current, before) && !same(current, after);
  });
  if (conflicts.length || !changes.length) return { doc, conflicts };
  const draft = structuredClone({ header: doc.header, lines: doc.lines });
  for (const { path, after } of changes) {
    const owner = valueAt(draft, path.slice(0, -1)) as Record<string | number, unknown>;
    const key = path[path.length - 1];
    if (after === undefined) delete owner[key];
    else owner[key] = after;
  }
  return { doc: { ...doc, ...draft }, conflicts };
}

const normalized = (value: unknown) => String(value ?? '').trim().toUpperCase();

function fillFields(original: Record<string, unknown>, read: Record<string, unknown>) {
  const filled = { ...original };
  for (const [field, value] of Object.entries(read)) {
    if (safeKey(field) && missing(original[field]) && !missing(value) && !Array.isArray(value) && !isRecord(value)) {
      filled[field] = value;
    }
  }
  return filled;
}

function uniqueMatch<T>(current: T, all: T[], candidates: T[], match: (a: T, b: T) => boolean) {
  const hits = candidates.filter(candidate => match(current, candidate));
  return hits.length === 1 && all.filter(row => match(row, hits[0])).length === 1 ? hits[0] : undefined;
}

export function missingInvoiceFields(doc: DocModel, read: DocModel): DraftFields {
  const header = fillFields(doc.header, read.header);
  const sourceDocs = Array.isArray(doc.header.sourceDocs) ? doc.header.sourceDocs.filter(isRecord) : [];
  const readSources = Array.isArray(read.header.sourceDocs) ? read.header.sourceDocs.filter(isRecord) : [];
  if (readSources.length) {
    header.sourceDocs = sourceDocs.length ? sourceDocs.map(source => {
      const match = uniqueMatch(source, sourceDocs, readSources, (a, b) => {
        const left = normalized(a.docNo);
        const right = normalized(b.docNo);
        if (left && right) return left === right;
        const issuer = normalized(a.issuerName);
        return !!issuer && issuer === normalized(b.issuerName) && normalized(a.docType) === normalized(b.docType)
          && normalized(a.costType) === normalized(b.costType);
      });
      return match ? fillFields(source, match) : source;
    }) : readSources;
  }
  const currentTaxes = Array.isArray(doc.header.taxItems) ? doc.header.taxItems.filter(isRecord) : [];
  const readTaxes = Array.isArray(read.header.taxItems) ? read.header.taxItems.filter(isRecord) : [];
  if (readTaxes.length) {
    header.taxItems = currentTaxes.length ? currentTaxes.map(tax => {
      const match = uniqueMatch(tax, currentTaxes, readTaxes, (a, b) => {
        if (isVatTaxRow(a) !== isVatTaxRow(b)) return false;
        const left = normalized(a.taxDocNo), right = normalized(b.taxDocNo);
        if (left && right) return left === right;
        const issuer = normalized(a.issuerName);
        return !!issuer && issuer === normalized(b.issuerName) && invoiceNumber(a.docCurrencyAmt) === invoiceNumber(b.docCurrencyAmt);
      });
      return match ? fillFields(tax, match) : tax;
    }) : readTaxes;
  }
  const lines = doc.lines.length ? doc.lines.map(line => {
    const match = uniqueMatch(line, doc.lines, read.lines, (a, b) => {
      const code = normalized(a.extCode);
      const desc = normalized(a.desc);
      const taxNo = normalized(a.extra?.taxDocNo);
      if (taxNo && taxNo === normalized(b.extra?.taxDocNo)) return code === normalized(b.extCode);
      if (desc && desc === normalized(b.desc)) return !code || !normalized(b.extCode) || code === normalized(b.extCode);
      return !!code && !/^(VAT|WHT|DUTY)$/.test(code) && code === normalized(b.extCode);
    });
    if (!match) return line;
    const filled = { ...fillFields(line, match), itemNo: line.itemNo } as DocLine;
    if (match.extra) filled.extra = fillFields(line.extra ?? {}, match.extra) as DocLine['extra'];
    return filled;
  }) : read.lines;
  return { header, lines };
}

export function invoiceNumber(value: unknown) {
  const text = String(value ?? '').trim().replace(/[, ]/g, '');
  if (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/.test(text)) return undefined;
  const amount = Number(text);
  return Number.isFinite(amount) ? amount : undefined;
}

export function invalidInvoiceChange(changes: InvoiceChange[]) {
  const numericFields = new Set(['amount', 'qty', 'price', 'subTotal', 'vatAmount', 'vatRate', 'whtAmount', 'totalAmount',
    'docCurrencyAmt', 'baseAmount', 'amountBeforeVat', 'nonVatAmount', 'baseFc', 'amtFc']);
  const invalid = changes.find(({ path, after }) => numericFields.has(String(path.at(-1)))
    && !missing(after) && invoiceNumber(after) === undefined);
  return invalid ? `Enter a valid number for ${invalid.path.join(' / ')}.` : undefined;
}

const money = (amount: number) => Math.sign(amount) * Math.round((Math.abs(amount) + Number.EPSILON) * 100) / 100;

export function invoiceRecalTotals(doc: DocModel, mainVendor: string) {
  const totals = vendorInvoiceTotals(doc, mainVendor);
  const applyRows = (rows: Record<string, unknown>[], target: 'vatAmount' | 'whtAmount', amountField: string) => {
    const byVendor = new Map<string, number>();
    for (const row of rows) {
      const amount = invoiceNumber(row[amountField]);
      if (amount === undefined) continue;
      const vendor = String(row.vendorCode ?? '').trim() || mainVendor;
      byVendor.set(vendor, (byVendor.get(vendor) ?? 0) + amount);
    }
    for (const [vendor, amount] of byVendor) {
      const total = totals.get(vendor) ?? { subTotal: 0, vatAmount: 0, whtAmount: 0, totalAmount: 0 };
      if (target === 'vatAmount') total.totalAmount = money(total.totalAmount + amount - total.vatAmount);
      total[target] = money(amount);
      totals.set(vendor, total);
    }
  };
  if (Array.isArray(doc.header.taxItems)) applyRows(doc.header.taxItems.filter(isVatTaxRow), 'vatAmount', 'docCurrencyAmt');
  if (Array.isArray(doc.header.whtItems)) applyRows(doc.header.whtItems, 'whtAmount', 'amtFc');
  return totals;
}

export function recalculateInvoice(doc: DocModel, previous: DocModel, mainVendor: string): DocModel {
  const lines = doc.lines.map(line => {
    const before = previous.lines.find(old => old.itemNo === line.itemNo);
    if (!before || /^(VAT|WHT|DUTY)$/i.test(line.extCode)) return line;
    const qty = invoiceNumber(line.qty), price = invoiceNumber(line.price);
    if (qty === undefined || price === undefined) return line;
    if (qty === invoiceNumber(before.qty) && price === invoiceNumber(before.price)) return line;
    if (invoiceNumber(line.amount) !== invoiceNumber(before.amount) && !missing(line.amount)) return line;
    return { ...line, amount: money(qty * price) };
  });
  const header = { ...doc.header };
  const updateTaxRows = (collection: 'taxItems' | 'whtItems', code: string, amountField: string) => {
    const oldRows = previous.header[collection];
    const rows = header[collection];
    if (!Array.isArray(rows) || !Array.isArray(oldRows) || rows.length !== oldRows.length) return;
    header[collection] = rows.map((row: Record<string, unknown>, index: number) => {
      const old = oldRows[index] as Record<string, unknown>;
      if (!same(row, old) || (code === 'VAT' && !isVatTaxRow(row))) return row;
      const amount = invoiceNumber(old[amountField]);
      const vendor = normalized(row.vendorCode);
      const reference = normalized(row.taxDocNo);
      const matched = previous.lines.filter(line => normalized(line.extCode) === code && (reference
        ? reference === normalized(line.extra?.taxDocNo)
        : amount !== undefined && amount === invoiceNumber(line.amount) && vendor === normalized(line.extra?.vendorCode)));
      if (matched.length !== 1) return row;
      const line = lines.find(current => current.itemNo === matched[0].itemNo);
      if (!line || normalized(line.extCode) !== code) return row;
      const after = invoiceNumber(line.amount);
      return after !== undefined && after !== invoiceNumber(matched[0].amount) ? { ...row, [amountField]: after } : row;
    });
  };
  updateTaxRows('taxItems', 'VAT', 'docCurrencyAmt');
  updateTaxRows('whtItems', 'WHT', 'amtFc');
  const draft = syncGlTaxCodes({ ...doc, header, lines });
  const totals = (d: DocModel) => {
    const totals = { subTotal: 0, vatAmount: 0, whtAmount: 0, totalAmount: 0 };
    for (const total of invoiceRecalTotals(d, mainVendor).values()) {
      totals.subTotal += total.subTotal;
      totals.vatAmount += total.vatAmount;
      totals.whtAmount += total.whtAmount;
      totals.totalAmount += total.totalAmount;
    }
    return totals;
  };
  const beforeTotals = totals(previous);
  const afterTotals = totals(draft);
  const calculatedHeader = { ...draft.header };
  for (const key of ['subTotal', 'vatAmount', 'whtAmount', 'totalAmount'] as const) {
    // Preserve an explicitly entered header total; otherwise refresh derived totals when their basis changes.
    if (money(beforeTotals[key]) !== money(afterTotals[key]) && same(doc.header[key], previous.header[key])) {
      calculatedHeader[key] = money(afterTotals[key]);
    }
  }
  return { ...draft, header: calculatedHeader };
}
