import type { DocModel } from '../api/documents';
import { agencyNameEn, GL_DEFERRED_INPUT_TAX, GL_INPUT_TAX } from '../constants/fields';
import { num } from './format';

type Row = Record<string, unknown>;
const key = (value: unknown) => String(value ?? '').trim().toUpperCase();
const cents = (value: unknown) => Math.round(num(value) * 100);
const credit = (value: unknown) => ['C', 'H'].includes(key(value));
const vatText = /^(?:DEFERRED\s+)?(?:INPUT\s+)?VAT\b/i;

export const isVatTaxRow = (tax: Row) => /^[VDUN]\d$/.test(key(tax.taxCode))
  || vatText.test(key(tax.label)) || ['INPUT', 'DEFERRED'].includes(key(tax.taxKind));

export function linkedGlTaxCodes(glItems: Row[], taxItems: Row[]): Map<number, string> {
  const codes = new Map<number, string>();
  const vatRows = taxItems.filter(isVatTaxRow);

  glItems.forEach((gl, index) => {
    const amount = cents(gl.amount);
    const text = key(gl.itemText);
    const account = key(gl.glAccount);
    // The duty account may also carry a customs VAT line. Its VAT text distinguishes it from duty and fees.
    const taxAccount = account === GL_INPUT_TAX || account === GL_DEFERRED_INPUT_TAX;
    if (!amount || (!key(gl.taxDocNo) && !vatText.test(text)
      && (!taxAccount || /\b(?:FEE|DUTY|EXCISE|INTERIOR TAX)\b/.test(text)))) return;

    const vendor = key(gl.vendorCode);
    const candidates = vatRows.filter((tax) => cents(tax.docCurrencyAmt) === amount
      && credit(gl.drCr) === credit(tax.drCr)
      && (!vendor || !key(tax.vendorCode) || vendor === key(tax.vendorCode)));
    const reference = key(gl.taxDocNo) || key(gl.assignment);
    const byReference = candidates.filter((tax) => reference && reference === key(tax.taxDocNo));
    const byVendor = candidates.filter((tax) => vendor && vendor === key(tax.vendorCode));
    const vendorMatches = byVendor.length ? byVendor : candidates;
    const byIssuer = vendorMatches.filter((tax) => {
      const issuer = key(agencyNameEn(tax.issuerName));
      return issuer && (key(agencyNameEn(gl.issuerName)) === issuer || text.includes(issuer));
    });
    const matches = byReference.length ? byReference : byIssuer.length ? byIssuer : vendorMatches;
    const matchingCodes = new Set(matches.map((tax) => key(tax.taxCode)));
    if (matchingCodes.size === 1) codes.set(index, [...matchingCodes][0]);
  });
  return codes;
}

export function syncGlTaxCodes(doc: DocModel): DocModel {
  if (doc.module !== 'AP' && doc.module !== 'II') return doc;
  const { glItems, taxItems } = doc.header;
  if (!Array.isArray(glItems) || !Array.isArray(taxItems) || !glItems.length || !taxItems.length) return doc;

  const codes = linkedGlTaxCodes(glItems, taxItems);
  if (![...codes].some(([i, code]) => glItems[i].taxCode !== code)) return doc;
  const synced = glItems.map((gl, index) => codes.has(index) ? { ...gl, taxCode: codes.get(index) } : gl);
  return { ...doc, header: { ...doc.header, glItems: synced } };
}
