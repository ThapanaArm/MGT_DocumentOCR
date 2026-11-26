import type { DocLine, DocModel } from '../api/documents';
import { isDutyRow } from '../constants/fields';

export function invoiceNumber(value: unknown): number | undefined {
  const text = String(value ?? '').trim().replace(/[, ]/g, '');
  if (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/.test(text)) return undefined;
  const amount = Number(text);
  return Number.isFinite(amount) ? amount : undefined;
}

const blank = (value: unknown) => String(value ?? '').trim() === '';
const money = (amount: number) => Math.sign(amount) * Math.round((Math.abs(amount) + Number.EPSILON) * 100) / 100;
const codeOf = (value: unknown) => String(value ?? '').trim().toUpperCase();
const taxType = (line: DocLine) => isDutyRow(line) ? 'DUTY' : codeOf(line.extCode);

export function invoiceRecalTotals(doc: DocModel, mainVendor: string) {
  const totals = new Map<string, { subTotal: number; vatAmount: number; whtAmount: number; duty: number; totalAmount: number }>();
  const forVendor = (value: unknown) => {
    const vendor = String(value ?? '').trim() || mainVendor;
    const total = totals.get(vendor) ?? { subTotal: 0, vatAmount: 0, whtAmount: 0, duty: 0, totalAmount: 0 };
    totals.set(vendor, total);
    return total;
  };
  for (const line of doc.lines) {
    const total = forVendor(line.extra?.vendorCode);
    const amount = invoiceNumber(line.amount) ?? 0;
    switch (taxType(line)) {
      case 'VAT': total.vatAmount += amount; break;
      case 'WHT': total.whtAmount += amount; break;
      case 'DUTY': total.duty += amount; break;
      default: total.subTotal += amount;
    }
  }
  const applyTaxRows = (rows: Record<string, unknown>[], field: string, target: 'vatAmount' | 'whtAmount') => {
    const sums = new Map<string, number>();
    for (const row of rows) {
      const amount = invoiceNumber(row[field]);
      if (amount === undefined) continue;
      const vendor = String(row.vendorCode ?? '').trim() || mainVendor;
      sums.set(vendor, (sums.get(vendor) ?? 0) + amount);
    }
    for (const [vendor, amount] of sums) forVendor(vendor)[target] = money(amount);
  };
  const taxItems: Record<string, unknown>[] = Array.isArray(doc.header.taxItems) ? doc.header.taxItems : [];
  applyTaxRows(taxItems.filter(row => !isDutyRow({ desc: row.label })), 'docCurrencyAmt', 'vatAmount');
  if (Array.isArray(doc.header.whtItems)) applyTaxRows(doc.header.whtItems, 'amtFc', 'whtAmount');
  for (const total of totals.values()) {
    total.subTotal = money(total.subTotal);
    total.vatAmount = money(total.vatAmount);
    total.whtAmount = money(total.whtAmount);
    // MGT follows the amount payable before withholding: costs + VAT + customs duty.
    total.totalAmount = money(total.subTotal + total.vatAmount + total.duty);
  }
  return totals;
}

export function recalculateInvoice(doc: DocModel, previous: DocModel, mainVendor: string): { document: DocModel } | { error: string } {
  if (!doc.lines.length) return { error: 'Add at least one line item before recalculating.' };
  const previousLines = new Map(previous.lines.map(line => [line.itemNo, line]));
  const lines = doc.lines.map(line => {
    const before = previousLines.get(line.itemNo);
    if (!before || ['VAT', 'WHT', 'DUTY'].includes(taxType(line))) return line;
    const qty = invoiceNumber(line.qty), price = invoiceNumber(line.price);
    if (qty === undefined || price === undefined) return line;
    if (qty === invoiceNumber(before.qty) && price === invoiceNumber(before.price)) return line;
    if (!blank(line.amount) && invoiceNumber(line.amount) !== invoiceNumber(before.amount)) return line;
    return { ...line, amount: money(qty * price) };
  });
  const invalid = lines.find(line => {
    const before = previousLines.get(line.itemNo);
    return invoiceNumber(line.amount) === undefined || (['qty', 'price'] as const).some(key =>
      invoiceNumber(line[key]) === undefined && (!blank(line[key]) || !blank(before?.[key])));
  });
  if (invalid) return { error: `Check the quantity, unit price and amount on line ${invalid.itemNo}.` };

  const header = { ...doc.header };
  const refreshTaxRows = (collection: 'taxItems' | 'whtItems', code: string, field: string) => {
    const rows: Record<string, unknown>[] = header[collection];
    const oldRows: Record<string, unknown>[] = previous.header[collection];
    if (!Array.isArray(rows) || rows.length !== oldRows?.length) return;
    header[collection] = rows.map((row, index) => {
      const old = oldRows[index];
      if (JSON.stringify(row) !== JSON.stringify(old) || isDutyRow({ desc: row.label })) return row;
      const ref = codeOf(row.taxDocNo);
      const amount = invoiceNumber(row[field]);
      const vendor = codeOf(row.vendorCode) || codeOf(mainVendor);
      const matches = previous.lines.filter(line => taxType(line) === code
        && (ref ? ref === codeOf(line.extra?.taxDocNo) : amount !== undefined && amount === invoiceNumber(line.amount))
        && vendor === (codeOf(line.extra?.vendorCode) || codeOf(mainVendor)));
      if (matches.length !== 1) return row;
      const match = lines.find(line => line.itemNo === matches[0].itemNo && taxType(line) === code);
      if (!match) return row;
      const original = matches[0];
      const amountChanged = invoiceNumber(match.amount) !== invoiceNumber(original.amount);
      const vendorChanged = codeOf(match.extra?.vendorCode) !== codeOf(original.extra?.vendorCode);
      if (!amountChanged && !vendorChanged) return row;
      return {
        ...row,
        ...(amountChanged ? { [field]: match.amount } : {}),
        ...(vendorChanged ? { vendorCode: match.extra?.vendorCode ?? '' } : {}),
      };
    });
  };
  refreshTaxRows('taxItems', 'VAT', 'docCurrencyAmt');
  refreshTaxRows('whtItems', 'WHT', 'amtFc');
  for (const [rows, field, label] of [
    [header.taxItems, 'docCurrencyAmt', 'Tax'],
    [header.whtItems, 'amtFc', 'Withholding Tax'],
  ] as const) {
    if (Array.isArray(rows) && rows.some(row => !blank(row[field]) && invoiceNumber(row[field]) === undefined)) {
      return { error: `Enter a valid amount in ${label}.` };
    }
  }

  const totals = [...invoiceRecalTotals({ ...doc, header, lines }, mainVendor).values()];
  for (const key of ['subTotal', 'vatAmount', 'whtAmount', 'totalAmount'] as const) {
    // Keep a header amount explicitly edited since the previous Recal or document load.
    const current = invoiceNumber(header[key]);
    if (current === undefined && !blank(header[key])) return { error: `Enter a valid number for ${key}.` };
    if (current === undefined || current === invoiceNumber(previous.header[key])) {
      header[key] = money(totals.reduce((sum, total) => sum + total[key], 0));
    }
  }
  return { document: { ...doc, header, lines } };
}
