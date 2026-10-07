import { useEffect, useRef, useState } from 'react';
import { useNavigate, useOutletContext, useParams } from 'react-router-dom';
import { useAppState } from '../state/AppState';
import { useMeta } from '../state/MetaContext';
import type { Me } from '../api/me';
import {
  chatFix,
  getChat,
  getDocument,
  getPayload,
  getRawText,
  learnMaterial,
  postToSap,
  reocrDocument,
  setDocCategory,
  type ChatMessage,
  type DocModel,
} from '../api/documents';
import type { DocLine } from '../api/documents';
import {
  AP_TOTALS_H,
  AP_TRADE_GROUPS,
  headerDefFor,
  PODP_TOTALS_H,
  SO_TOTALS_H,
  WHT_CODE_RATE,
  agencyNameEn,
  AGENCY_TAX_ID,
  isDutyRow,
  GL_FREIGHT_HANDLING,
  GL_INPUT_TAX,
  GL_DEFERRED_INPUT_TAX,
  GL_DUTY,
  GL_WITHHOLDING_TAX,
  dutyLabelEn,
} from '../constants/fields';
import { SEND_DISABLED } from '../constants/flags';
import { dt, fileRetention, fmt, fmtCost, intFmt, moduleLabel, statusBadge } from '../utils/format';
import { findDupes } from '../utils/dupes';
import Steps from '../components/Steps';
import Modal, { ModalHeader } from '../components/Modal';
import OcrProviderSelect, { READ_ENGINE_IDS } from '../components/OcrProviderSelect';
import FieldGrid from '../components/document/FieldGrid';
import TabbedGroups from '../components/document/TabbedGroups';
import DetailTable from '../components/document/DetailTable';
import GlItemsTable from '../components/document/GlItemsTable';
import TaxDataTable from '../components/document/TaxDataTable';
import WhtTable from '../components/document/WhtTable';
import IncomingInvoiceCard from '../components/document/IncomingInvoiceCard';
import MappingCards from '../components/document/MappingCards';
import ChatFixCard from '../components/document/ChatFixCard';
import LineExtraModal from '../components/document/LineExtraModal';
import MasterEditModal, {
  type MasterEditState,
} from '../components/master/MasterEditModal';
import { fetchBlobUrl,
  ApiError,
} from '../api/client';
import { createMaster, updateMaster } from '../api/masters';
import type { SapBusinessPartner, SapLastPrice, SapMaterial, SapMaterialDetail, SapPartnerFunctionLink, SapSalesEmployee, SapSalesEmployeeSuggestion } from '../api/sap';
import { getSapLastPrice, getSapMaterialDetail, getSapSalesEmployees, getSapLastSalesEmployee } from '../api/sap';
import type {
  ZohoAccount,
  ZohoShipToInfo,
  ZohoDeal,
  ZohoDealItem,
} from '../api/zoho';
import { getZohoAccountSoldTo } from '../api/zoho';
import { compareCandidates, type CompareField, type CompareResult } from '../api/compare';
import type { CustomerMatchProposal } from '../components/document/MappingCards';
import { resolveDocTarget } from '../utils/salesTarget';
import { useDocumentEditor } from '../components/document/shell/useDocumentEditor';
import SapSalesOrderStep, {
  type SalesOrderStepHandle,
} from '../components/document/steps/SapSalesOrderStep';
import ZohoSalesOrderStep from '../components/document/steps/ZohoSalesOrderStep';

// Deprecated. The backend no longer reads any "user" value sent by the client — it stamps the
// identity from the validated Entra ID token instead, so whatever is passed here is discarded.
// Left in place only so the existing call signatures keep compiling; remove it together with the
// `user` parameters in api/documents.ts.
const USER = '(ignored by the server)';
// SAP-like withholding-tax prefill: on a Supplier Invoice / liability-recording
// document (module AP/II), if OCR captured a WHT amount but no WHT rows exist yet,
// seed one row the way SAP's Create Supplier Invoice / Journal Entry WHT tab does —
// base defaults to the document amount excluding VAT. A goods invoice (whtAmount 0)
// gets no row, exactly like SAP for a non-WHT vendor.
// SAP withholding-tax defaults. MGT withholds at payment, so the type defaults to the first
// payment-posting type; recipient type 53 is the ภ.ง.ด.53 form used for company suppliers, which
// is what a shipping bundle always is. Both are dropdowns the user can change per row.
const DEFAULT_WHT_TYPE = 'OA';
const DEFAULT_RECIPIENT_TYPE = '53';

// Pick the AP Withholding Tax Code from the row the read produced. The description is written as
// "Withholding Tax <rate>% <issuer> <doc no>", so the wording decides first and the rate breaks
// the tie (1% -> transport, 2% -> advertising, 3% -> service, which is how the shipping bundles
// this system reads are made up). Anything unrecognised is left blank for the user to pick.
function guessWhtCode(desc: string): string {
  const d = desc.toLowerCase();
  if (/transport|freight|ขนส่ง|ค่าระวาง/.test(d)) return '01';
  if (/interest|ดอกเบี้ย/.test(d)) return '02';
  if (/insur|ประกันภัย/.test(d)) return '03';
  if (/advertis|โฆษณา/.test(d)) return '04';
  if (/hire of work|จ้างทำของ/.test(d)) return '05';
  if (/software|ซอฟต์แวร์/.test(d)) return '06';
  if (/repair|maintenance|ซ่อม/.test(d)) return '07';
  if (/commission|นายหน้า/.test(d)) return '08';
  if (/licen[cs]e|ใบอนุญาต/.test(d)) return '10';
  if (/service|บริการ/.test(d)) return '09';
  const m = desc.match(/(\d+(?:\.\d+)?)\s*%/);
  const rate = m ? Number(m[1]) : 0;
  if (rate === 1) return '01';
  if (rate === 2) return '04';
  if (rate === 3) return '09';
  return '';
}

// Rows saved before the Type / Recipient Type dropdowns existed hold free text ("WHT Type for
// Payment Posting") or nothing at all, which no <select> can show. Map them onto the real SAP
// codes once, on open, so an old document does not look blank.
function normalizeWhtItems(d: DocModel): DocModel {
  const items = d.header.whtItems as Array<Record<string, any>> | undefined;
  if (!items || !items.length) return d;
  let changed = false;
  const next = items.map((w) => {
    const row = { ...w };
    const t = String(row.wtType ?? '');
    if (/invoice/i.test(t) && !/^T[IJK]$/.test(t)) { row.wtType = 'TI'; changed = true; }
    else if (/payment/i.test(t) && !/^O[ABC]$/.test(t)) { row.wtType = 'OA'; changed = true; }
    if (!row.recipientType) { row.recipientType = DEFAULT_RECIPIENT_TYPE; changed = true; }
    if (!row.whtCode) {
      const guess = guessWhtCode(String(row.itemText ?? row.desc ?? ''));
      if (guess) { row.whtCode = guess; changed = true; }
    }
    return row;
  });
  return changed ? { ...d, header: { ...d.header, whtItems: next } } : d;
}

function seedWhtItems(d: DocModel): DocModel {
  if (d.module !== 'AP' && d.module !== 'II') return d;
  if (d.header.whtItems && d.header.whtItems.length) return normalizeWhtItems(d);
  // Prefer the withholding-tax rows the read appended (extCode "WHT") over the header total:
  // on a shipping bundle the header figure came off a different page and disagreed with them.
  const fromLines = (d.lines || [])
    .filter((l) => String(l.extCode || '').toUpperCase() === 'WHT')
    .reduce((sum, l) => sum + (Number(l.amount) || 0), 0);
  const amt = fromLines > 0 ? Math.round(fromLines * 100) / 100 : Number(d.header.whtAmount) || 0;
  if (amt <= 0) return d;
  const base = Number(d.header.subTotal) || 0;
  const whtLines = (d.lines || []).filter((l) => String(l.extCode || '').toUpperCase() === 'WHT');
  const rows = whtLines.length
    ? whtLines.map((l) => {
        const amtFc = Number(l.amount) || 0;
        const code = guessWhtCode(String(l.desc || ''));
        const rate = WHT_CODE_RATE[code];
        return {
          wtType: DEFAULT_WHT_TYPE,
          whtCode: code,
          recipientType: DEFAULT_RECIPIENT_TYPE,
          // The description the read writes carries the rate ("Withholding Tax 1% ..."), so the
          // base of the invoice that was actually withheld can be worked back out of it. Falls
          // back to the document subtotal when the rate is unknown.
          // base = amount / (rate%). WHT_CODE_RATE holds whole percents (3 means 3%), so the rate
          // has to be divided by 100 first — dividing by 3 instead of 0.03 made the base 100x too
          // small (63.00 at 3% came out as 21.00 instead of 2,100.00).
          baseFc: rate && amtFc > 0 ? Math.round((amtFc / (rate / 100)) * 100) / 100 : base,
          amtFc,
          vendorCode: String(l.extra?.vendorCode ?? ''),
        };
      })
    : [{
        wtType: DEFAULT_WHT_TYPE,
        whtCode: '',
        recipientType: DEFAULT_RECIPIENT_TYPE,
        baseFc: base,
        amtFc: amt,
        vendorCode: '',
      }];
  return { ...d, header: { ...d.header, whtItems: rows } };
}

// SAP-like VAT prefill for the Tax tab, mirroring seedWhtItems: on an AP/II
// supplier invoice, if OCR captured a VAT amount/rate but no tax rows exist yet,
// seed one input-VAT row (D/C = S, amount = vatAmount, rate = vatRate). Tax Code
// defaults to V1 for the standard Thai 7% input VAT; a non-VAT invoice gets no row.
// Incoming Invoice (FB60) shows its items as G/L rows (header.glItems), but OCR returns the
// document's rows in `lines` — which the II page never displays — so the table stayed empty even
// when the read found every row. Seed one debit G/L row per OCR line that carries an amount; the
// G/L account / cost center are not on the document, so they are left for the user to fill.
// Only when glItems is still empty, so rows the user already edited/saved are never replaced.
// What the purchase order does NOT cover, and so must be posted as a G/L line even on a document
// that references a PO: anything the Customs or Excise Department charges. isDutyRow alone is too
// narrow here — its word list is anchored and holds only the four duty names, so a line read as
// "OTHER : CUSTOMS FEE" did not match it and the fee of 200 was dropped from the document for good.
// This one matches anywhere in the wording, which is how the item list actually writes them.
const CUSTOMS_WORDING =
  /customs|excise|interior tax|import duty|ศุลกากร|สรรพสามิต|มหาดไทย|อากร/i;

function notCoveredByPo(line: { extCode?: unknown; desc?: unknown }): boolean {
  return isDutyRow(line) || CUSTOMS_WORDING.test(String(line.desc ?? ''));
}

function seedGlItems(d: DocModel): DocModel {
  // Both invoice modules. It used to seed FB60 (II) only, so a PO-referenced invoice (AP/MIRO)
  // showed an empty G/L Account Items table and there was nowhere to pick the G/L account, tax
  // code or assignment before exporting — the file came out with those columns blank.
  if (d.module !== 'II' && d.module !== 'AP') return d;
  // Duty and customs fees belong in G/L Account Items and NOT on the Tax tab — that tab is the VAT
  // alone, which is the only figure SAP posts from it. This moves them rather than just deleting
  // them: a row missing from G/L is added before it leaves the tab, so nothing can fall through the
  // gap (the customs fee of 200 disappeared from the document exactly that way). It runs even when
  // G/L is already filled in, because that is the document where the seeding below is skipped, and
  // it is idempotent — running it again on a tidy document changes nothing.
  const moveDutyToGl = (doc: DocModel): DocModel => {
    const gl = ((doc.header.glItems as Array<Record<string, any>>) || []).slice();
    const tax = (doc.header.taxItems as Array<Record<string, any>>) || [];
    const amt = (v: unknown) => Number(v) || 0;
    // What may stay on the Tax tab is decided by the TAX CODE, not by the row's wording. Only the
    // real input-VAT codes are a tax SAP posts from that tab: V0/V1/V2 claimable and D0/D1/D2
    // deferred. Anything carrying VX is exempt — duty, excise, interior tax, a customs fee — and
    // belongs in G/L Account Items. Matching on the label failed here: the duty word list has no
    // "Customs Fee" in it, so that row kept its seat on the tab through two attempted fixes.
    const isVatRow = (t: Record<string, any>) => {
      const code = String(t.taxCode ?? '').trim().toUpperCase();
      const kind = String(t.taxKind ?? '').trim().toUpperCase();
      return /^[VD][012]$/.test(code) || kind === 'INPUT' || kind === 'DEFERRED';
    };
    const isDutyTax = (t: Record<string, any>) => !isVatRow(t) && amt(t.docCurrencyAmt) !== 0;
    // Either side may carry no vendor on an older row, so a blank matches anything.
    const sameVendor = (a: unknown, b: unknown) => {
      const x = String(a ?? '').trim();
      const y = String(b ?? '').trim();
      return x === y || x === '' || y === '';
    };
    const poRef = String(doc.header.poRef ?? '').trim();
    const missing: Array<Record<string, any>> = [];
    const alreadyThere = (amount: number, vendor: unknown) =>
      gl.some((g) => amt(g.amount) === amount && sameVendor(g.vendorCode, vendor))
      || missing.some((g) => amt(g.amount) === amount && sameVendor(g.vendorCode, vendor));

    // Source 1: duty rows sitting on the Tax tab. They carry the customs receipt's number and the
    // issuing agency, so they make the better G/L line when both sources describe the same money.
    tax.filter(isDutyTax).forEach((t) => {
      if (alreadyThere(amt(t.docCurrencyAmt), t.vendorCode)) return;
      missing.push({
        glAccount: GL_INPUT_TAX,
        drCr: 'D',
        amount: amt(t.docCurrencyAmt),
        taxCode: String(t.taxCode ?? '').trim() || 'VX',
        assignment: String(t.taxDocNo ?? ''),
        itemText: String(t.issuerName ?? '').slice(0, 50),
        costCenter: '',
        vendorCode: String(t.vendorCode ?? '').trim(),
      });
    });

    // Source 2: duty rows still only in the item list. This is the one that was missed: a document
    // whose G/L table had already been saved skipped the seeding below entirely, so a customs fee
    // read as a line item (OTHER : CUSTOMS FEE, 200) had no way in and the money simply vanished.
    (doc.lines || []).filter((l) => notCoveredByPo(l) && amt(l.amount) !== 0).forEach((l) => {
      const vendor = String(l.extra?.vendorCode ?? '').trim();
      if (alreadyThere(amt(l.amount), vendor)) return;
      missing.push({
        glAccount: GL_DUTY,
        drCr: 'D',
        amount: amt(l.amount),
        taxCode: 'VX',
        assignment: poRef,
        itemText: String(l.desc ?? '').slice(0, 50),
        costCenter: '',
        vendorCode: vendor,
      });
    });

    const kept = tax.filter((t) => !isDutyTax(t));
    if (!missing.length && kept.length === tax.length) return doc;
    return {
      ...doc,
      header: { ...doc.header, glItems: [...gl, ...missing], taxItems: kept },
    };
  };
  if (d.header.glItems && d.header.glItems.length) return moveDutyToGl(d);

  // The shape of a posted document (5100001269 and the MIRO examples Finance keyed by hand):
  // the costs go in exempt, the VAT is its own line per tax invoice, and withholding tax is a
  // credit line marked "WHT". Assignment carries the PO number on the cost lines and the tax
  // invoice number on the tax lines, which is how Finance ties them back afterwards.
  const poRef = String(d.header.poRef ?? '').trim();
  const text = (v: unknown) => String(v ?? '').slice(0, 50); // SAP item text is 50 chars
  // vendorCode travels with the row: the export groups G/L items by it (GroupByVendor), and the
  // screen's "Vendor in this document" filter hides the rows that are not this supplier's. Seeded
  // rows used to leave it blank, so every tax row — the Customs Department's included — landed on
  // whichever supplier was selected.
  const row = (o: Record<string, unknown>) => ({
    glAccount: '', drCr: 'D', amount: 0, taxCode: 'VX',
    assignment: '', itemText: '', costCenter: '', vendorCode: '', ...o,
  });

  // With a purchase order behind the invoice, the costs are posted against the PO — in SAP they
  // sit under Purchasing Document References, which is what our PO Reference tab shows. Repeating
  // them as G/L Account Items would post them a second time and throw the balance out, so the
  // G/L table then carries only the taxes. Without a PO (FB60) there is nothing to post the costs
  // against, so they stay here.
  const hasPo = poRef.length > 0;

  // Duty and customs fees are NOT posted against the purchase order — the PO covers the goods, not
  // what the Customs Department charges — so they stay here as G/L lines even when a PO is present.
  // Only the freight and handling the PO already carries is left out. Without this the customs fee
  // of 200 had nowhere to go and simply vanished from the document.
  const dutyTaxRows = ((d.header.taxItems as Array<Record<string, any>>) || [])
    .filter((t) => isDutyRow({ desc: t.label }) && (Number(t.docCurrencyAmt) || 0) !== 0);
  const dutyKey = (label: unknown, amount: unknown) =>
    `${dutyLabelEn(label)}|${Number(amount) || 0}`;
  const alreadyOnTaxTab = new Set(dutyTaxRows.map((t) => dutyKey(t.label, t.docCurrencyAmt)));

  const costs = (d.lines || [])
    // VAT and withholding rows live in their own tabs; they come back below, reviewed, rather
    // than twice — once raw from the read and once from the tab.
    .filter((l) => !['VAT', 'WHT'].includes(String(l.extCode || '').toUpperCase()))
    .filter((l) => (Number(l.amount) || 0) !== 0)
    .filter((l) => !hasPo || notCoveredByPo(l))
    // A duty already carried by a Tax-tab row becomes one G/L line, not two.
    .filter((l) => !alreadyOnTaxTab.has(dutyKey(l.desc, l.amount)))
    .map((l) =>
      row({
        // Duty repeated from the customs paperwork is not a freight cost and has no account yet.
        glAccount: isDutyRow(l) ? GL_DUTY : GL_FREIGHT_HANDLING,
        amount: Number(l.amount) || 0,
        assignment: poRef,
        itemText: text(l.desc),
        vendorCode: String(l.extra?.vendorCode ?? '').trim(),
      }),
    );

  // One line per tax invoice, to the account that matches it. Which account is decided by the
  // Input Tax Type the person picked in the Tax tab — that dropdown IS the mapping, and it is
  // what they reviewed. The tax code's first letter is only a fallback for a row read before
  // anyone touched it: D1/D0/D2 are the deferred codes, so they imply deferred tax.
  // VAT belongs on the Tax tab and nowhere else: that tab is what SAP posts the input tax from, so
  // repeating the same amount as a G/L line books it twice — the customs document showed 60,049 on
  // the Tax tab AND again under Basic Data. Duty and fees have no tab of their own, so those rows
  // become G/L lines here and come off the tab below.
  const taxes = dutyTaxRows
    .map((t) => {
      const code = String(t.taxCode ?? '').trim();
      const kind = String(t.taxKind ?? '').trim().toUpperCase();
      const deferred = kind ? kind === 'DEFERRED' : code.toUpperCase().startsWith('D');
      return row({
        glAccount: deferred ? GL_DEFERRED_INPUT_TAX : GL_INPUT_TAX,
        amount: Number(t.docCurrencyAmt) || 0,
        taxCode: code,
        assignment: String(t.taxDocNo ?? ''),
        itemText: text(t.issuerName),
        vendorCode: String(t.vendorCode ?? '').trim(),
      });
    });

  // Withholding tax on a PO-referenced invoice is carried by SAP's own Withholding Tax tab, which
  // computes and posts it — a credit G/L line as well would deduct it twice.
  const wht = (hasPo ? [] : ((d.header.whtItems as Array<Record<string, any>>) || []))
    .filter((w) => (Number(w.amtFc) || 0) !== 0)
    .map((w) =>
      row({
        glAccount: GL_WITHHOLDING_TAX,
        drCr: 'C', // withholding is deducted from what we pay, so it is the credit side
        amount: Number(w.amtFc) || 0,
        assignment: poRef,
        itemText: 'WHT',
        vendorCode: String(w.vendorCode ?? '').trim(),
      }),
    );

  const items = [...costs, ...taxes, ...wht];
  if (!items.length) return d;
return moveDutyToGl({ ...d, header: { ...d.header, glItems: items } });
}

// MIRO / FB60 header Tax Code: fixed to VX (Input VAT Exempt Purchases) per Finance — the real
// tax per invoice is carried by the rows in the Tax tab (V1 / D1 / ...), so the header code is
// only the document-level default. Change this constant when Finance settles on another code.
const HEADER_TAX_CODE = 'VX';

// Business Place = branch for Thai tax purposes. Per the SAP training material both company
// codes (1000 MGT, 2000 GLC) have only Head Office = 0000, so it is filled in rather than left
// for the user; a branch document can still be changed by hand.
const HEAD_OFFICE_BUSINESS_PLACE = '0000';

function seedHeaderTaxCode(d: DocModel): DocModel {
  if (d.module !== 'AP' && d.module !== 'II') return d;
  const businessPlace = String(d.header.businessPlace ?? '').trim() || HEAD_OFFICE_BUSINESS_PLACE;
  // Per Finance, the vendor branch on these documents is always head office, so an empty Branch
  // is filled rather than left for the person to type the same four characters every time.
  const branch = String(d.header.branch ?? '').trim() || HEAD_OFFICE_BUSINESS_PLACE;
  if (
    d.header.taxCode === HEADER_TAX_CODE
    && d.header.businessPlace === businessPlace
    && d.header.branch === branch
  ) {
    return d;
  }
  return { ...d, header: { ...d.header, taxCode: HEADER_TAX_CODE, businessPlace, branch } };
}

// Branch is four digits, head office 0000 — per Finance. A five-digit value (an older document,
// or a read that padded it) is trimmed to its last four rather than shown as it came.
function padBranch(value: unknown): string {
  const digits = String(value ?? '').replace(/\D/g, '');
  return digits.length ? digits.slice(-4).padStart(4, '0') : HEAD_OFFICE_BUSINESS_PLACE;
}

function seedTaxItems(d: DocModel): DocModel {
  if (d.module !== 'AP' && d.module !== 'II') return d;
  // Rows the user already has stay as they are — only a row with no D/C at all gets the S default.
  if (d.header.taxItems && d.header.taxItems.length) {
    const items = d.header.taxItems.map((t: Record<string, unknown>) => {
      const row: Record<string, unknown> = t.drCr ? { ...t } : { ...t, drCr: 'S' };
      // A row read before the agency names were translated still holds the Thai name; swap it
      // here so the screen and the exported file are in English without needing a re-read.
      const en = agencyNameEn(row.issuerName);
      if (en !== String(row.issuerName ?? '')) row.issuerName = en;
      return row;
    });
    // Duty rows that are not on the Tax tab yet are appended rather than ignored: a document read
    // before duties were recognised has its VAT row already, so the whole seed used to be skipped
    // and Import Duty / Excise Tax / Interior Tax stayed stranded in the item list.
    const missing = (d.lines || [])
      .filter((l) => isDutyRow(l) && (Number(l.amount) || 0) !== 0)
      .filter((l) => !items.some(
        (t: Record<string, unknown>) => String(t.label ?? '').trim() === String(l.desc ?? '').trim(),
      ))
      .map((l) => ({
        label: dutyLabelEn(l.desc),
        drCr: 'S',
        docCurrencyAmt: Number(l.amount) || 0,
        taxCode: 'VX',
        validFrom: '',
        taxRate: '0',
        taxKind: '',
        vendorCode: String(l.extra?.vendorCode ?? ''),
        issuerName: '', issuerTaxId: '', issuerBranch: '', taxDocNo: '', taxDocDate: '',
        baseAmount: 0,
      }));
    return { ...d, header: { ...d.header, taxItems: linkDutyRows([...items, ...missing]) } };
  }
  const amt = Number(d.header.vatAmount) || 0;
  const rate = Number(d.header.vatRate) || 0;
  if (amt <= 0 && rate <= 0) return d;
  // SAP codes (procedure 0TXTH): claimable input VAT is V0/V1/V2, and VAT that is still waiting
  // for the supplier's tax invoice is booked as deferred tax D0/D1/D2 at the same rate.
  const codeFor = (kind: string) => {
    const suffix = rate === 10 ? '2' : rate === 0 ? '0' : '1';
    return (kind === 'DEFERRED' ? 'D' : 'V') + suffix;
  };
  const mkRow = (
    value: number,
    taxKind = '',
    vendorCode = '',
    label = 'Input VAT',
    src?: DocLine,
  ) => ({
    label,
    drCr: 'S',
    docCurrencyAmt: value,
    taxCode: codeFor(taxKind),
    validFrom: '',
    taxRate: rate ? String(rate) : '7',
    // INPUT = ใบกำกับภาษี/ใบเสร็จ (เข้ารายงานภาษีซื้องวดนี้), DEFERRED = ใบแจ้งหนี้ (รอเรียกเก็บ)
    taxKind,
    // which vendor's invoice this tax belongs to — the MIRO tabs are keyed per vendor
    vendorCode,
    // Identity of the tax invoice behind this row. The Input VAT file Finance sends to SAP is one
    // row per tax invoice, so these ride on the tax row and are reviewable before export. The
    // read fills them; base amount falls back to VAT / rate when the document did not spell it out.
    issuerName: agencyNameEn(src?.extra?.issuerName),
    issuerTaxId: String(src?.extra?.issuerTaxId ?? ''),
    // Branch is 5 digits in the Input VAT file, head office = 00000. A read that returns 0 or "0"
    // must still show as 00000, so pad rather than take the value as it comes.
    issuerBranch: src ? padBranch(src.extra?.issuerBranch) : '',
    taxDocNo: String(src?.extra?.taxDocNo ?? ''),
    taxDocDate: String(src?.extra?.taxDocDate ?? ''),
    baseAmount:
      Number(src?.extra?.baseAmount) ||
      (value > 0 && rate ? Math.round((value / (rate / 100)) * 100) / 100 : 0),
  });
  // A shipping bundle carries VAT on several invoices; the read returns one "VAT" line per
  // invoice, so seed a tax row for each instead of a single row for one page's VAT.
  const vatLines = (d.lines || []).filter((l) => String(l.extCode || '').toUpperCase() === 'VAT');
  // Fallback when the read did not classify a VAT row: the document's own supplier bills us with
  // an invoice / billing note, so its VAT is only claimable once the tax invoice arrives
  // (DEFERRED). VAT on the other documents in the bundle comes from tax invoices / receipts and
  // goes straight on the input-VAT report (INPUT).
  const onInvoice = /ใบแจ้งหนี้|ใบวางบิล|invoice|billing/i.test(String(d.header.docType ?? ''));
  const vendorWords = String(d.header.vendorName ?? '')
    .replace(/บริษัท|จำกัด|มหาชน|ห้างหุ้นส่วน|company|limited|public|co\.|ltd\.?/gi, ' ')
    .split(/[\s.,()-]+/)
    .filter((w) => w.length >= 3);
  const kindOf = (l: DocLine) => {
    const given = String(l.extra?.taxKind ?? '').toUpperCase();
    if (given === 'INPUT' || given === 'DEFERRED') return given;
    const desc = String(l.desc ?? '');
    const isOwnVendor = vendorWords.length > 0 && vendorWords.some((w) => desc.includes(w));
    return isOwnVendor && onInvoice ? 'DEFERRED' : 'INPUT';
  };
  const vatRows = vatLines.length
    ? vatLines
        .map((l) => mkRow(Number(l.amount) || 0, kindOf(l), String(l.extra?.vendorCode ?? ''), 'Input VAT', l))
        .filter((r) => r.docCurrencyAmt > 0)
    : [mkRow(amt, onInvoice ? 'DEFERRED' : 'INPUT')];
  // Customs charges (import duty, excise, interior tax) are not items on the FORM, so the read
  // returns them as "DUTY" rows and they are recorded here in the Tax tab, named per row.
  const dutyRows = (d.lines || [])
    .filter((l) => isDutyRow(l))
    .map((l) =>
      mkRow(Number(l.amount) || 0, '', String(l.extra?.vendorCode ?? ''), dutyLabelEn(l.desc)),
    )
    .filter((r) => r.docCurrencyAmt > 0)
    // Import duty / excise carry no input VAT of their own — booked with VX (Input VAT Exempt).
    .map((r) => ({ ...r, taxCode: 'VX', taxRate: '0' }));
  const rows = linkDutyRows(fillMissingTaxIds([...vatRows, ...dutyRows]));
  if (!rows.length) return d;
  return { ...d, header: { ...d.header, taxItems: rows } };
}

// A tax invoice from the same issuer appears more than once in a bundle (a government receipt
// especially), and the read does not always pick the tax ID off every copy — the number is often
// printed small under the agency's name. When one row has it and another from the same issuer does
// not, copy it across rather than exporting a blank cell. Only ever copies within the same
// document, and never overwrites a value that was read.
// A duty row has no tax invoice of its own — it is a line on the SAME customs receipt as that
// vendor's VAT row. Take the receipt's issuer, tax ID, branch, number and date from that row so the
// Tax tab shows one consistent document instead of a dated VAT row beside blank duty rows. Also
// puts the label into English. Never overwrites a value that is already there.
function linkDutyRows<T extends Record<string, unknown>>(rows: T[]): T[] {
  const source = new Map<string, T>();
  for (const r of rows) {
    if (!String(r.taxDocDate ?? '').trim()) continue;
    const key = String(r.vendorCode ?? '').trim();
    if (!source.has(key)) source.set(key, r);
  }
  return rows.map((r) => {
    const label = dutyLabelEn(r.label);
    const next: Record<string, unknown> = label === r.label ? { ...r } : { ...r, label };
    const isDuty = String(r.taxCode ?? '').toUpperCase() === 'VX';
    if (isDuty && !String(r.taxDocDate ?? '').trim()) {
      const from = source.get(String(r.vendorCode ?? '').trim());
      if (from) {
        for (const k of ['issuerName', 'issuerTaxId', 'issuerBranch', 'taxDocNo', 'taxDocDate']) {
          if (!String(next[k] ?? '').trim()) next[k] = from[k];
        }
      }
    }
    return next as T;
  });
}

function fillMissingTaxIds<T extends { issuerName?: string; issuerTaxId?: string }>(rows: T[]): T[] {
  const byIssuer = new Map<string, string>();
  for (const r of rows) {
    const name = String(r.issuerName ?? '').trim().toLowerCase();
    const id = String(r.issuerTaxId ?? '').trim();
    if (name && id && !byIssuer.has(name)) byIssuer.set(name, id);
  }
  if (!byIssuer.size) return rows;
  return rows.map((r) => {
    if (String(r.issuerTaxId ?? '').trim()) return r;
    const name = String(r.issuerName ?? '').trim();
    // Another copy of the same issuer's invoice in this bundle, then the confirmed list of
    // government-agency numbers. Never an invented value.
    const found = byIssuer.get(name.toLowerCase()) ?? AGENCY_TAX_ID[name];
    return found ? { ...r, issuerTaxId: found } : r;
  });
}

// Merges person-confirmed AI material matches into a GET .../preview response for display only
// -- moves a skipped line into `lines` (built from the chosen Deal Item + the document's own
// qty/price/unit, the same sourcing rule the backend's TryBuildMatchedLine uses) so the Material
// cards and the Send-to-Zoho confirm modal show it as matched immediately, without waiting on a
// re-fetch. Never changes what's actually sent to Zoho by itself -- that only happens once the
// same override is included as a line's materialId in the POST .../create request (see
// the Zoho step), which the backend independently re-validates against the Deal's own Items.
/* "Use & Save" learning (SAP and Zoho paths). The mapping engine (MappingEngine.MatchMaterial)
   recognises a line next time by the CUSTOMER's own wording: ExtCode = MaterialCodeCode, then
   ExtDesc = MaterialCodeName (>= 85% similar). So the row must store the DOCUMENT's code/description,
   not the SAP/Zoho English name — storing the SAP name meant the next PO never matched and the user
   had to pick the item again every time (Megachem, 6 Oct 2026, customer 1000609 / HYCE02-JP-LO-01).
   A customer can word the same item differently on different POs, so an existing row only counts
   when it would actually match THIS line; otherwise another row is added for the same SAP code. */
const normItemText = (s: unknown) => String(s ?? '').toLowerCase().replace(/\s+/g, ' ').trim();
function customerMaterialCoversLine(m: Record<string, any>, line: { extCode?: string; desc?: string }): boolean {
  const ext = String(line.extCode ?? '').trim().toUpperCase();
  const desc = normItemText(line.desc);
  if (!ext && !desc) return true; // nothing on the line to learn from
  if (ext && String(m.MaterialCodeCode ?? '').trim().toUpperCase() === ext) return true;
  return !!desc && normItemText(m.MaterialCodeName) === desc;
}

export default function DocumentPage() {
  const { docId } = useParams<{ docId: string }>();
  const id = Number(docId);
  const navigate = useNavigate();
  const { guard, showToast } = useAppState();
  const { ocrProviders, loadOcrProviders, apDocCategories, loadApDocCategories, masters, loadMasters } =
    useMeta();
  const { me } = useOutletContext<{ me: Me | null }>();

  // Provider-agnostic document core (working doc, mapping result, and every edit that is the
  // same regardless of SAP vs Zoho). Everything else on this page -- loading, re-OCR, chat,
  // split, master-edit, and the SAP/Zoho-specific steps -- consumes these.
  const {
    doc, setDoc, map, setMap, failed, setFailed, manual, runMap,
    editHeader, editLine, editLineExtra, addLine, delLine,
    editGlItem, addGlItem, delGlItem,
    editTaxItem, addTaxItem, delTaxItem,
    editWhtItem, addWhtItem, delWhtItem,
    setManualLine,
  } = useDocumentEditor(USER);
  // Where this document is posted, decided by company x module: an MGT Sales Order goes to
  // Zoho CRM, everything else (liability modules, and Sales Orders under any other company)
  // goes to SAP -- see resolveDocTarget. `isMgt` is kept as the in-file alias for "the Zoho
  // target" so the existing branches keep working during the flow split; unlike the old
  // company-only flag it is now module-aware, so an MGT user opening a non-SO document (e.g.
  // an Incoming Invoice) correctly routes to SAP. New code should call resolveDocTarget.
  const isMgt = resolveDocTarget(doc, me) === 'zoho';
  const [chatHistory, setChatHistory] = useState<ChatMessage[]>([]);
  const [chatImage, setChatImage] = useState<string | null>(null);
  const [chatProvider, setChatProvider] = useState('gemini');
  const [reocrEngine, setReocrEngine] = useState('gemini');

  // Keep the working document's salesOrg in step with the active company view. The /map, /payload
  // and /post requests all send doc.header, and the backend resolves the company from
  // header.salesOrg — so for a Sales Order it must carry the CURRENT view's salesOrg (isMgt tracks
  // the "View as" dev toggle via effectiveMe), not a stale stored value or the signed-in user's own
  // company. Without this, simulating GLC still mapped/posted as the real MGT login. SO only.
  useEffect(() => {
    if (!doc || doc.module !== 'SO') return;
    const want = isMgt ? '1000' : '2000';
    if (doc.header.salesOrg !== want) editHeader('salesOrg', want);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [doc?.module, doc?.header?.salesOrg, isMgt]);

  // A Customer search that comes back ambiguous (>1 candidate) gets resolved by chatting about
  // it right in the Chat to Fix Data box below, instead of a separate popup — see
  // proposeCustomerMatch/sendMatchChat. matchChatLog is appended (not persisted server-side, on
  // purpose — this is a lightweight local conversation, not a document field-fix) after the real
  // chatHistory when handed to ChatFixCard, so it reads as one continuous conversation.
  interface PendingCustomerMatch extends CustomerMatchProposal {
    /** The candidate the AI last proposed, if any — lets a plain "yes"/"ใช่" reply resolve it
     *  without another round trip. */
    lastSuggestedId?: string;
  }
  const [pendingMatch, setPendingMatch] = useState<PendingCustomerMatch | null>(null);
  const [matchChatLog, setMatchChatLog] = useState<ChatMessage[]>([]);
  // Signal to make the on-screen Zoho customer search run a query the AI chose (the AI "pressing"
  // the Search Zoho button). nonce lets the same query fire again. Read-only: the person still picks.
  const [customerSearchSignal, setCustomerSearchSignal] = useState<{ query: string; nonce: number } | undefined>();

  // modals
  const [reviewOpen, setReviewOpen] = useState(false);
  // The original file lives behind the same Bearer-token auth as every other endpoint, and a plain
  // <iframe src="/api/..."> cannot send that header — the request came back 401 and the viewer was
  // simply blank. Fetch it with the token instead and hand the iframe/img a blob URL.
  const [fileUrl, setFileUrl] = useState<string | null>(null);
  const [fileErr, setFileErr] = useState<string | null>(null);
  const [exporting, setExporting] = useState(false);
  // Why the document could not be loaded, shown in place of a bare "Failed to load document".
  const [failedReason, setFailedReason] = useState('');
  // The three export files live behind one button so the header stays a single row — adding a
  // fourth file later costs a line in the menu, not another button competing for the width.
  const [exportOpen, setExportOpen] = useState(false);
  const exportMenuRef = useRef<HTMLDivElement | null>(null);

  // Close the export menu on a click anywhere outside it, and on Escape.
  useEffect(() => {
    if (!exportOpen) return;
    const onDown = (e: MouseEvent) => {
      if (!exportMenuRef.current?.contains(e.target as Node)) setExportOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setExportOpen(false); };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [exportOpen]);

  // Load (and release) the original file whenever the viewer is opened.
  useEffect(() => {
    if (!reviewOpen || !doc || doc.provider === 'demo') return;
    let url: string | null = null;
    let alive = true;
    setFileErr(null);
    setFileUrl(null);
    fetchBlobUrl(`/api/documents/${doc.docId}/file`)
      .then((u) => {
        url = u;
        if (alive) setFileUrl(u);
        else URL.revokeObjectURL(u);
      })
      .catch((e) => {
        if (alive) setFileErr(e?.message || 'Could not open the original file');
      });
    return () => {
      alive = false;
      if (url) URL.revokeObjectURL(url);
    };
  }, [reviewOpen, doc?.docId, doc?.provider]);

  const [rawText, setRawText] = useState<string | null>(null);
  const [payload, setPayload] = useState<Record<string, any> | null>(null);
  const [vendorFilter, setVendorFilter] = useState('');
  const [lineExtraIdx, setLineExtraIdx] = useState<number | null>(null);
  const [postOpen, setPostOpen] = useState(false);
  const [posting, setPosting] = useState(false);
  const sapStepRef = useRef<SalesOrderStepHandle | null>(null);
  const zohoStepRef = useRef<SalesOrderStepHandle | null>(null);
  // Mapping owns the selected Deal/Ship-to seam; the Zoho Step owns preview, editable payload,
  // posting state, result, and its payload modal.
  const [selectedDealId, setSelectedDealId] = useState('');
  const [resolvedDeal, setResolvedDeal] = useState<ZohoDeal | null>(null);
  const [selectedZohoShipTo, setSelectedZohoShipTo] = useState<ZohoShipToInfo | null>(null);

  const [masterEdit, setMasterEdit] = useState<MasterEditState | null>(null);

  // GLC Sales Order — per-line Sales Employee (SD item custom field YY1_SDSalesEmployeeI_SDI).
  // salesEmps: the full pick list (DB master + live SAP), loaded once. lineSalesEmpSuggest: the
  // history-based suggestion per line (who handled this customer+material last time), shown as a hint.
  // salesEmpAutoRef guards the auto-fill effect so it runs once per set of matched materials.
  const [salesEmps, setSalesEmps] = useState<SapSalesEmployee[]>([]);
  const [lineSalesEmpSuggest, setLineSalesEmpSuggest] = useState<Record<number, SapSalesEmployeeSuggestion | null>>({});
  const salesEmpAutoRef = useRef<string>('');

  // Load document on mount / id change.
  useEffect(() => {
    let alive = true;
    setDoc(null);
    setMap(null);
    setFailed(false);
    setPendingMatch(null);
    setMatchChatLog([]);
    manual.current = { header: {}, lines: {} };
    loadOcrProviders();
    loadMasters();
    (async () => {
      let reason = '';
      const d = await guard(async () => {
        try {
          return await getDocument(id);
        } catch (e) {
          // Keep why it failed. "Failed to load document" on its own sends people hunting
          // through the API logs for what is usually a stale link to a document that is not
          // there — the id and the status say it outright.
          reason = e instanceof ApiError && e.status === 404
            ? `Document #${id} does not exist. The link may be from an older session — open it from the Invoice List instead.`
            : e instanceof Error ? e.message : String(e);
          throw e;
        }
      });
      if (!alive) return;
      if (!d) {
        setFailedReason(reason || `Document #${id} could not be loaded.`);
        setFailed(true);
        return;
      }
      // Locked to Gemini (per Megachem) — the re-OCR engine is always Gemini regardless of which
      // engine last read the document.
      setReocrEngine('gemini');
      setDoc(seedHeaderTaxCode(seedGlItems(seedTaxItems(seedWhtItems(d)))));
      if (d.module === 'AP') loadApDocCategories();
      try {
        const chat = await getChat(d.docId);
        if (alive) setChatHistory(chat);
      } catch {
        /* ignore */
      }
      if (d.mapStatus) runMap(true, d);
    })();
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  // GLC Sales Order: load the full Sales Employee pick list once (Ms_User.PersonID + live SAP).
  // MGT/Zoho and non-SO documents don't use it. Best-effort — an empty list just means the per-line
  // picker shows only whatever history suggested. (Declared here, above the early returns below, so
  // the hook order stays stable on every render — React requires every hook to run unconditionally.)
  useEffect(() => {
    if (isMgt || !doc || doc.module !== 'SO') return;
    let alive = true;
    getSapSalesEmployees()
      .then((r) => { if (alive) setSalesEmps(r.results || []); })
      .catch(() => {});
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isMgt, doc?.module]);

  // GLC Sales Order: once the mapping is in, look up who was the Sales Employee the LAST time this
  // customer bought each matched material, and auto-fill the line with it (the person can still
  // change it via the per-line picker). Runs once per set of matched material codes (salesEmpAutoRef),
  // fetches every line's suggestion in parallel, then persists all found suggestions with ONE re-map.
  // Only fills lines that don't already have a chosen sales employee, so it never overwrites a manual
  // pick and converges (after the re-map the filled lines are no longer empty).
  useEffect(() => {
    if (isMgt || !doc || doc.module !== 'SO' || !map) return;
    const cust = map.header.customer?.sapCode || map.header.customer?.code;
    if (!cust) return;
    const sig = doc.docId + '|' + (map.lines || []).map((m) => m?.code || '').join(',');
    if (salesEmpAutoRef.current === sig) return;
    salesEmpAutoRef.current = sig;

    let alive = true;
    (async () => {
      const targets: { i: number; code: string }[] = [];
      doc.lines.forEach((l, i) => {
        const code = map.lines?.[i]?.code;
        const has = (l.extra as Record<string, string> | undefined)?.salesEmployee;
        if (code && !has) targets.push({ i, code });
      });
      if (!targets.length) return;
      const results = await Promise.all(
        targets.map((t) =>
          getSapLastSalesEmployee(cust, t.code)
            .then((r) => ({ i: t.i, s: r.suggestion }))
            .catch(() => ({ i: t.i, s: null as SapSalesEmployeeSuggestion | null })),
        ),
      );
      if (!alive) return;
      const suggMap: Record<number, SapSalesEmployeeSuggestion | null> = {};
      results.forEach((r) => { suggMap[r.i] = r.s; });
      setLineSalesEmpSuggest((prev) => ({ ...prev, ...suggMap }));

      const found = results.filter((r) => r.s && r.s.personId);
      if (!found.length) return;
      const lines = doc.lines.slice();
      found.forEach((r) => {
        lines[r.i] = { ...lines[r.i], extra: { ...(lines[r.i].extra || {}), salesEmployee: r.s!.personId } };
      });
      const updated = { ...doc, lines };
      setDoc(updated);
      await runMap(true, updated);
    })();
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isMgt, doc?.docId, map]);

  if (failed)
    return (
      <div className="card">
        <div className="empty">
          <p>{failedReason || 'Failed to load document'}</p>
          <button className="btn sm ghost" onClick={() => navigate('/list/AP')}>
            <i className="fa-solid fa-list" /> Go to Invoice List
          </button>
        </div>
      </div>
    );
  if (!doc || !masters) return <div className="card"><div className="empty">Loading…</div></div>;

  const h = doc.header;
  // For a Sales Order the company context follows the CURRENT view — `isMgt` already tracks the
  // "View as" dev toggle through effectiveMe (AppLayout), so an admin simulating GLC actually
  // maps/posts as GLC (salesOrg 2000), not as their own signed-in company or a stale stored
  // salesOrg. A real GLC user's SO resolves the same way. Non-SO modules keep the original
  // resolution (stored value → the user's own SalesOrg → the isMgt fallback).
  const salesOrg = doc.module === 'SO'
    ? (isMgt ? '1000' : '2000')
    : (h.salesOrg || me?.salesOrganization || (isMgt ? '1000' : '2000'));
  const companyCode = doc.module === 'SO' ? salesOrg : (me?.sapCompanyCode || salesOrg);
  const posted = doc.status === 'POSTED';
  const isSplit = doc.status === 'SPLIT';
  // Some of this document's delivery-date Sales Orders are already in SAP/Zoho: locked like a posted
  // document (editing would diverge from what was sent) — only "send the remaining ones" is allowed.
  const partial = doc.status === 'PARTIAL';
  // A split parent must be read-only: its lines now live in the child Sales Orders, so editing it
  // would diverge from what was actually created. `locked` gates every editing surface (header/detail/
  // mapping/tax/wht/gl) for BOTH posted and split, while `posted` alone still drives the posted-only
  // UI (the "sent to SAP" banner, step 3). Split shows its own banner + status below.
  const locked = posted || isSplit || partial;
  // Uploaded file of a never-posted document is removed after the retention period (FileCleanupWorker).
  const retention = fileRetention(doc, doc.retentionHours);
  const fileExpired = retention.kind === 'expired';
  const glItems = h.glItems || [];
  const showDetail = doc.module !== 'II' && doc.module !== 'PODP';
  const showGlItems = doc.module === 'AP' || doc.module === 'II';

  // Document-editing handlers (patchDoc / header / line / GL / Tax / WHT + setManualLine) now
  // live in useDocumentEditor and are destructured above.

  const setManualHeader = (k: string, v: string) => {
    manual.current.header[k] = v;
    if (k === 'customer') {
      manual.current.header.shipTo = '';
      // a different customer invalidates any prior "no ship-to" fallback choice (GLC)
      manual.current.header.shipToFallback = '';
      setSelectedZohoShipTo(null);
    }
    // picking a real ship-to supersedes the "use sold-to / omit" fallback choice (GLC)
    if (k === 'shipTo') {
      if (v) manual.current.header.shipToFallback = '';
      setSelectedZohoShipTo(null);
    }
    runMap(true);
  };

  // GLC: apply the SAP sales area the person picked (or the single one auto-used) on the Customer
  // card — persists DistributionChannel / Division / Sales Group onto the document header (so the
  // SAP payload uses them) and re-maps. Values live on the header, not as a manual match override,
  // because the payload is rebuilt from the stored header (see PayloadForAsync / HeaderJson).
  const setSalesArea = (fields: { distChannel?: string; division?: string; salesGroup?: string }) =>
    guard(async () => {
      const updated = { ...doc, header: { ...doc.header, ...fields } };
      setDoc(updated);
      await runMap(true, updated);
    });

  // GLC: record which sales person handled/verified THIS line's mapping. Stored in the line's
  // `extra` bag (extra.salesEmployee) so it survives the DB round-trip and is emitted per item as the
  // SAP custom field YY1_SDSalesEmployeeI_SDI. Re-maps to persist (same reason as setSalesArea — the
  // payload is rebuilt from the stored document at send time).
  const setLineSalesEmployee = (i: number, personId: string) =>
    guard(async () => {
      const lines = doc.lines.slice();
      lines[i] = { ...lines[i], extra: { ...(lines[i].extra || {}), salesEmployee: personId } };
      const updated = { ...doc, lines };
      setDoc(updated);
      await runMap(true, updated);
    });

  const learn = (i: number) =>
    guard(async () => {
      const code = map?.lines[i]?.code;
      const l = doc.lines[i];
      if (!code) return;
      await learnMaterial(doc.docId, {
        partnerCode: doc.partnerCode,
        extCode: l.extCode,
        extDesc: l.desc,
        materialCode: code,
      });
      await loadMasters(true);
      showToast('Saved to Master Mapping — the system will match it automatically next time', 'success');
      await runMap(true);
    });

  const doReocr = () =>
    guard(async () => {
      const d = await reocrDocument(doc.docId, reocrEngine, USER);
      setDoc(seedHeaderTaxCode(seedGlItems(seedTaxItems(seedWhtItems(d)))));
      setMap(null);
      manual.current = { header: {}, lines: {} };
      if (d.provider === 'failed')
        // Surface the real reason the read failed (e.g. "Gemini HTTP 503 …", "timed out", bad key)
        // instead of a generic message — and never silently fall back to sample/demo data. The
        // backend now returns Provider="failed" with the reason in confidenceNote for any read
        // failure (see OcrEngine.FailedResult) rather than substituting demo data.
        showToast(
          d.confidenceNote
            ? `Could not read the document — ${d.confidenceNote}`
            : 'Could not read the document — the OCR engine is unavailable. Try again, choose another engine, or attach an image in the AI chat below.',
        );
      else
        showToast(
          'Document re-read complete — engine ' +
            (d.provider || '') +
            ' · found ' +
            d.lines.length +
            ' items',
        );
    });

  const openRaw = () =>
    guard(async () => {
      const r = await getRawText(doc.docId);
      setRawText(r.text || '(No text — this is a scanned file or generated from sample data)');
    });
  const openPayload = () =>
    guard(async () => {
      const r = await getPayload(doc.docId);
      setPayload(r.payload);
    });

  const confirmPost = () =>
    guard(async () => {
      setPosting(true);
      try {
        const r = await postToSap(doc.docId, USER);
        setDoc(r.document);
        setPostOpen(false);
        const sapReference = r.sapDocNo ? ` — SAP document ${r.sapDocNo}` : '';
        showToast(
          `${r.simulated ? 'Simulation completed' : 'Document created in SAP successfully'}${sapReference}`,
          'success',
        );
        document.querySelector('.content')?.scrollTo({ top: 0, behavior: 'smooth' }); window.scrollTo({ top: 0, behavior: 'smooth' });
      } finally {
        setPosting(false);
      }
    });

  const changeCategory = (v: string) =>
    guard(async () => {
      const d = await setDocCategory(doc.docId, v, USER);
      setDoc(d);
    });

  // ---- quick-add master flows ----
  const quickAddVendor = () =>
    setMasterEdit({
      tab: 'vendors',
      rowKey: null,
      prefill: { VendorName: h.vendorName || '', TaxId: h.vendorTaxId || '', Currency: h.currency || 'THB' },
      dupes: findDupes(masters, 'vendors', h.vendorName, h.vendorTaxId),
      onSaved: async (code) => setManualHeader('vendor', code),
      onUseDupe: (code) => setManualHeader('vendor', code),
    });
  const quickAddCustomer = () =>
    setMasterEdit({
      tab: 'customers',
      rowKey: null,
      prefill: { CompanyName: h.customerName || '', TaxId: h.customerTaxId || '', Branch: h.branch || '', SalesOrg: companyCode },
      dupes: findDupes(masters, 'customers', h.customerName, h.customerTaxId, (x) => String(x.SalesOrg) === companyCode),
      onSaved: async (code) => setManualHeader('customer', code),
      onUseDupe: (code) => setManualHeader('customer', code),
    });

  // One-click path from the Customer card's live "Data from SAP" panel: create the local
  // Customer master row straight from the chosen SAP Business Partner and apply it as the
  // match — no modal, no second save step (the person already picked the right one).
  const useSapCustomer = (bp: SapBusinessPartner) =>
    guard(async () => {
      const code = bp.businessPartnerId;
      await createMaster('customers', {
        ComcompyCodeSAP: code,
        CompanyName: h.customerName || '',
        CompanyNameSAP: bp.businessPartnerFullName || bp.businessPartnerName || '',
        SalesOrg: companyCode,
        Branch: h.branch || '',
        IsActive: 1,
        // SAP's own Tax ID wins when it has one — same rationale as useZohoCustomer below: SAP is
        // the source of truth here, and should show up even when the document had no Tax ID (or a
        // stale/wrong one) of its own to go on.
        TaxId: bp.taxId || h.customerTaxId || '',
        // Address sub-fields added 2026-09-22, so the Customer card's persisted "main table" can
        // show the Sold-to's real SAP address (previously never saved here at all).
        HouseNumber: bp.addressHouseNumber || '', Street: bp.addressStreet || '',
        Street2: bp.addressStreet2 || '', Street3: bp.addressStreet3 || '',
        Street4: bp.addressStreet4 || '', Street5: bp.addressStreet5 || '',
        District: bp.addressDistrict || '', City: bp.addressCity || '',
        DifferenceCity: bp.addressDifferenceCity || '',
        PostCode: bp.addressPostalCode || '', CountryReg: bp.addressCountry || '',
      });
      await loadMasters(true);
      setManualHeader('customer', code);
      if (pendingMatch) {
        setMatchChatLog((log) => [...log, { role: 'assistant', text: `Selected "${code}" directly from the table.` }]);
        setPendingMatch(null);
      }
      showToast('Customer added from SAP and matched');
    });

  // Add a supplier to the local vendor master straight from SAP. ocr.Vendor is a local table
  // that starts with only the sample rows, so before this every new supplier failed mapping and
  // had to be typed in by hand. A supplier is an A_BusinessPartner in S/4HANA exactly like a
  // customer, so the same lookup serves both — VendorCode is the SAP Business Partner id, which
  // is what MIRO/FB60 need anyway.
  // Download the Input VAT workbook for this document. The endpoint sits behind the same Bearer
  // auth as everything else, so the file is fetched with the token and handed to the browser as a
  // blob rather than linked to directly (a plain <a href> would come back 401).
  const downloadExport = (kind: 'input-vat' | 'journal-voucher' | 'sap-import', fileName: string, toast: string) =>
    guard(async () => {
      setExporting(true);
      try {
        setExportOpen(false);
        const url = await fetchBlobUrl(`/api/documents/${doc.docId}/export/${kind}`);
        // Guard against saving something that is not a workbook. This bit once: the API route was
        // missing from the running build, the SPA fallback answered with index.html under a plain
        // 200, and the browser wrote that HTML to disk as an .xlsx that Excel then refused to
        // open. Check what actually came back rather than trusting the status code.
        const blob = await (await fetch(url)).blob();
        if (!/sheet|excel|octet-stream/i.test(blob.type)) {
          URL.revokeObjectURL(url);
          throw new Error(
            'The server did not return a spreadsheet — the API is most likely running an older ' +
              'build without this export. Rebuild and restart the backend, then try again.',
          );
        }
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        // Give the browser a moment to start the download before the blob is released.
        setTimeout(() => URL.revokeObjectURL(url), 10_000);
        showToast(toast);
      } finally {
        setExporting(false);
      }
    });

  const exportInputVat = () =>
    downloadExport('input-vat', `InputVat_${doc.docId}.xlsx`, 'Input VAT file downloaded');

  const exportJournalVoucher = () =>
    downloadExport('journal-voucher', `JournalVoucher_${doc.docId}.xlsx`, 'Journal Voucher file downloaded');

  // The file the SAP Fiori app "Import Supplier Invoices" reads. Same content the direct post
  // sends, in the shape that app's Download Template produces, for whoever wants to check it
  // before it goes in.
  const exportSapImport = () =>
    downloadExport(
      'sap-import',
      `SapImportSupplierInvoice_${doc.docId}.xlsx`,
      'SAP import file downloaded',
    );

  const useSapVendor = (bp: SapBusinessPartner) =>
    guard(async () => {
      const code = bp.businessPartnerId;
      // ocr.Vendor's columns are short (VendorCode/TaxId 20, VendorName 200, Branch 10,
      // Currency 5), and SQL Server rejects the whole INSERT rather than trimming, so every value
      // is cut to fit here. Branch especially: the document says "Head Office" but the master
      // stores the 5-digit code, so it goes through the same normalizer the Tax tab uses.
      const cut = (v: unknown, n: number) => String(v ?? '').trim().slice(0, n);
      const row = {
        VendorCode: cut(code, 20),
        SapVendorCode: cut(code, 20),
        VendorName: cut(bp.businessPartnerFullName || bp.businessPartnerName || h.vendorName, 200),
        // SAP's own Tax ID wins when it has one; the document's is the fallback, same rule as
        // useSapCustomer — SAP is the source of truth and the document may carry a stale one.
        TaxId: cut(bp.taxId || h.vendorTaxId, 20),
        Branch: padBranch(h.branch),
        Currency: cut(h.currency || 'THB', 5),
        IsActive: 1,
      };
      // VendorCode is the primary key, so a second "Use" on a supplier already in the master used
      // to fail with a duplicate-key error instead of simply matching. Refresh the list first so
      // the check is not made against a stale copy, then update in place rather than insert.
      await loadMasters(true);
      const already = (masters?.vendors ?? []).some((v) => String(v.VendorCode).trim() === row.VendorCode);
      if (already) await updateMaster('vendors', row.VendorCode, row);
      else await createMaster('vendors', row);
      await loadMasters(true);
      setManualHeader('vendor', code);
      showToast(already ? 'Vendor refreshed from SAP and matched' : 'Vendor added from SAP and matched');
    });

  // MGT uses the real Zoho Account Code stored in Customer.ComcompyCodeSAP.
  const useZohoCustomer = (acc: ZohoAccount) =>
    guard(async () => {
      const code = acc.accountCode?.trim();
      if (!code) { showToast('This Zoho record has no Account Code'); return; }
      // Sold-to address sub-fields added 2026-09-22: the account search result itself doesn't
      // carry them (see ZohoAccount), so fetch the same Sold-to address the live "Sold-to
      // Address" panel shows (getZohoAccountSoldTo) and save it into the Customer master row too
      // -- previously this was never persisted, only shown live/transiently.
      const soldTo = await getZohoAccountSoldTo(acc.accountId).catch(() => null);
      await createMaster('customers', {
        ComcompyCodeSAP: code,
        CompanyName: h.customerName || '',
        CompanyNameSAP: acc.accountName || '',
        SalesOrg: companyCode,
        IsActive: 1,
        TaxId: acc.taxId || h.customerTaxId || '',
        Branch: h.branch || '',
        HouseNumber: soldTo?.houseNumber || '', Street: soldTo?.street || '',
        Street2: soldTo?.street2 || '', Street3: soldTo?.street3 || '',
        Street4: soldTo?.street4 || '', Street5: soldTo?.street5 || '',
        District: soldTo?.district || '', City: soldTo?.city || '',
        DifferenceCity: soldTo?.differenceCity || '', PostCode: soldTo?.postCode || '',
        CountryReg: soldTo?.countryReg || '',
      });
      await loadMasters(true);
      setManualHeader('customer', code);
      if (pendingMatch) {
        setMatchChatLog((log) => [...log, { role: 'assistant', text: `Selected "${code}" directly from the table.` }]);
        setPendingMatch(null);
      }
      showToast('Customer added from Zoho CRM and matched');
    });
  // One-click path for MGT: pull the Ship-to address straight from the same Zoho Account
  // record as the already-matched Sold-to customer, and save it as a local Ship-to master row.
  // Zoho's ship-to sub-block on the Account has no separate location-name field, so we fall
  // back to the document's own shipToName (or the customer name) for ShipToName.
  const useZohoShipTo = (info: ZohoShipToInfo, _accountId: string) =>
    guard(async () => {
      const custCode = map?.header.customer.code;
      if (!custCode) {
        showToast('Please identify the customer first');
        return;
      }
      setMasterEdit({
        tab: 'shiptos', rowKey: null,
        // ShipToCode / ShipToName / ShipToAddress are the DOCUMENT's own text (constants/fields.ts
        // marks them source:'document'): the mapping engine matches the next PO's ship-to name /
        // address against them, so SAP/Zoho wording here would stop it recognising the location.
        // Zoho's own code and address live in SapShipToCode and the per-field columns below.
        prefill: { SalesOrg: companyCode, ShipToCode: h.shipToCode || '', CustomerCode: custCode,
          SapShipToCode: info.code || custCode, ShipToName: h.shipToName || '',
          ShipToAddress: h.shipToAddress || '',
          // Address sub-fields added 2026-09-22 -- info already carries every Zoho field
          // individually (see ZohoShipToInfo); previously only the collapsed info.address string
          // above was kept, so the persisted "main table" only ever showed one joined row.
          HouseNumber: info.houseNumber || '', Street: info.street || '',
          Street2: info.street2 || '', Street3: info.street3 || '', Street4: info.street4 || '',
          Street5: info.street5 || '', District: info.district || '', City: info.city || '',
          DifferenceCity: info.differenceCity || '', PostCode: info.postCode || '',
          CountryReg: info.countryReg || '' },
        onSaved: async (code) => {
          setManualHeader('shipTo', code);
          setSelectedZohoShipTo(info);
        },
      });
    });
  // Same one-click path, GLC/SAP side: create the local Ship-to master row from a chosen SAP
  // A_CustSalesPartnerFunc partner-function link (see SapShipToPanel — looked up by the already-
  // matched Sold-to's own SAP code, not a name search) and apply it.
  const useSapShipTo = (link: SapPartnerFunctionLink) =>
    guard(async () => {
      const custCode = map?.header.customer.code;
      if (!custCode) {
        showToast('Please identify the customer first');
        return;
      }
      const code = link.partnerCustomer;
      const bp = link.partner;
      setMasterEdit({
        tab: 'shiptos', rowKey: null,
        prefill: { SalesOrg: companyCode, ShipToCode: h.shipToCode || '', CustomerCode: custCode, SapShipToCode: code,
          ShipToName: h.shipToName || '',
          // The DOCUMENT's address (Megachem, 6 Oct 2026): ShipToAddress is what the next PO's
          // address is matched against, so it must be the customer's own wording. SAP's real
          // address is kept in the per-field columns below (shown on the mapping card).
          ShipToAddress: h.shipToAddress || '',
          HouseNumber: bp?.addressHouseNumber || '', Street: bp?.addressStreet || '',
          Street2: bp?.addressStreet2 || '', Street3: bp?.addressStreet3 || '',
          Street4: bp?.addressStreet4 || '', Street5: bp?.addressStreet5 || '',
          District: bp?.addressDistrict || '', City: bp?.addressCity || '',
          DifferenceCity: bp?.addressDifferenceCity || '',
          PostCode: bp?.addressPostalCode || '', CountryReg: bp?.addressCountry || '' },
        onSaved: async (savedCode) => setManualHeader('shipTo', savedCode),
      });
    });
  const quickAddShipTo = () => {
    const custCode = map?.header.customer.code;
    if (!custCode) {
      showToast('Please identify the customer first');
      return;
    }
    setMasterEdit({
      tab: 'shiptos',
      rowKey: null,
      prefill: { SalesOrg: companyCode, CustomerCode: custCode, ShipToCode: h.shipToCode || '', ShipToName: h.shipToName || '', ShipToAddress: h.shipToAddress || '' },
      dupes: findDupes(masters, 'shiptos', h.shipToName, null, (x) => x.CustomerCode === custCode && String(x.SalesOrg) === companyCode),
      onSaved: async (code) => setManualHeader('shipTo', code),
      onUseDupe: (code) => setManualHeader('shipTo', code),
    });
  };
  const quickAddMaterial = (i: number) => {
    const l = doc.lines[i];
    const customerCode = map?.header.customer?.code || doc.partnerCode;
    const so = doc.module === 'SO';
    if (so && !customerCode) { showToast('Please identify the customer first'); return; }
    setMasterEdit({
      tab: so ? 'custmaterials' : 'apmaterials',
      rowKey: null,
      prefill: so ? { SalesOrg: companyCode, CustomerCode: customerCode, MaterialCodeCode: l.extCode || '', MaterialCodeName: l.desc || '' } : { Description: l.desc || '', Uom: l.uom || '', Plant: '1000' },
      dupes: so ? findDupes(masters, 'custmaterials', l.desc, null, (x) => x.CustomerCode === customerCode && String(x.SalesOrg) === companyCode) : findDupes(masters, 'apmaterials', l.desc, null),
      onSaved: async (code) => {
        manual.current.lines[i] = code;
        if (!so) await learnMaterial(doc.docId, { partnerCode: doc.partnerCode, extCode: l.extCode, extDesc: l.desc, materialCode: code });
        await loadMasters(true);
        await runMap(true);
      },
      onUseDupe: (code) => setManualLine(i, code),
    });
  };

  // Best-effort text -> SAP-unit-code normalizer. Only used to decide whether the document's unit
  // already matches SAP's Base Unit (so no conversion rule is needed) and to guess a starting
  // point for the SAP Unit field in the confirmation popup below — the person still reviews and
  // can correct it in that popup, this is never applied blind to what gets saved or posted.
  const normUom = (s: string) => {
    const t = (s || '').trim().toLowerCase().replace(/s$/, '');
    const map: Record<string, string> = {
      kilogram: 'KG', kilo: 'KG', kg: 'KG',
      // Thai spellings — GLC documents write units in Thai ("กิโลกรัม", "กรัม"), which must still
      // resolve to the SAP unit code, otherwise the raw Thai word ends up as the SAP unit.
      'กิโลกรัม': 'KG', 'กิโล': 'KG', 'กก': 'KG', 'กก.': 'KG',
      gram: 'G', g: 'G', 'กรัม': 'G', 'ก.': 'G',
      ton: 'TON', tonne: 'TON', metricton: 'TON', 'ตัน': 'TON',
      liter: 'L', litre: 'L', l: 'L', 'ลิตร': 'L',
      milliliter: 'ML', millilitre: 'ML', ml: 'ML', 'มิลลิลิตร': 'ML',
      meter: 'M', metre: 'M', m: 'M', 'เมตร': 'M',
      piece: 'EA', pc: 'EA', each: 'EA', ea: 'EA', 'ชิ้น': 'EA',
      box: 'BOX', bag: 'BAG', drum: 'DRUM',
      'กล่อง': 'BOX', 'ถุง': 'BAG', 'ถัง': 'DRUM',
    };
    return map[t] || t.toUpperCase();
  };

  // Picking a SAP search result saves the CustomerMaterial mapping, then (only when needed) pops an
  // editable confirmation for the Unit Conversion rule, pre-filled from SAP. The old ocr.Material
  // ("apmaterials") master step was removed — material data now lives entirely in CustomerMaterial,
  // and unit handling in ocr.UomConversion. Because there is no material base unit anymore, a
  // conversion rule is created whenever the document's unit isn't already a code SAP recognises.
  const useSapMaterial = (i: number, material: SapMaterial) =>
    guard(async () => {
      const customerCode = map?.header.customer?.code || doc.partnerCode;
      if (!customerCode) {
        showToast('Please identify the customer first');
        return;
      }
      const line = doc.lines[i];
      const docUnit = (line.uom || '').trim();

      let detail: SapMaterialDetail | null = null;
      try {
        detail = (await getSapMaterialDetail(material.materialCode)).detail;
      } catch {
        detail = null; // best-effort only — the popup below just opens with blanker defaults
      }

      const finishLine = async () => {
        await setManualLine(i, material.materialCode);
        showToast(`Selected Material ${material.materialCode} from SAP`);
      };

      const maybeAddUomRule = async () => {
        const baseUnit = (detail?.baseUnit || '').trim();
        const alreadyHasRule = masters.uoms.some((u) =>
          u.MaterialCode === material.materialCode
            && String(u.ExtUom || '').trim().toLowerCase() === docUnit.toLowerCase(),
        );
        // With ocr.Material gone the SO mapping has no base unit and relies entirely on these
        // UomConversion rules. Skip only when the document literally already uses a unit SAP knows
        // for this material (its base unit or an alternative, e.g. "KG"/"BAG") — SAP takes that
        // as-is. A word like "Kilogram" always needs a rule.
        const alts = detail?.altUnits || [];
        const sapUnits = [baseUnit, ...alts.map((a) => a.unit)].filter((u) => !!u && u.length > 0);
        const docIsSapCode = sapUnits.some((u) => u.toLowerCase() === docUnit.toLowerCase());
        if (alreadyHasRule || docUnit.length === 0 || docIsSapCode) {
          await finishLine();
          return;
        }

        // GLC only (this whole useSapMaterial path is GLC/SAP-only — see the MGT/Zoho counterpart
        // below): NO unit conversion. Per the user, GLC sells in the document's own unit — almost
        // always KG, occasionally G — even when the material is physically packaged as a BAG/DRUM,
        // so the SAP unit must be the DOCUMENT's unit normalized to a SAP code (กิโลกรัม -> KG),
        // NOT the material's SAP base unit (which for a "...-BG-..." material comes back as BAG and
        // would wrongly turn "40 KG" into "40 BAG"). Factor is always 1 — this row is a pure
        // KG->KG normalization, not a pack-size conversion. Picking the actual unit is a human
        // decision (CS asks Sales) made outside the software; the popup just lets them confirm/
        // correct the defaulted unit. Last Price (best-effort, live from SAP — see getSapLastPrice)
        // is shown alongside purely as reference, never written to the saved row.
        // Prefer the matched customer's SAP code (SoldToParty) for the billing lookup; for GLC it
        // equals customerCode (both are ComcompyCodeSAP), but sapCode is the authoritative SAP key.
        const lastPriceCustomer = map?.header.customer?.sapCode || customerCode;
        let lastPrice: SapLastPrice | null = null;
        try {
          lastPrice = (await getSapLastPrice(lastPriceCustomer, material.materialCode)).price;
        } catch {
          lastPrice = null; // best-effort only — never blocks confirming the unit
        }

        setMasterEdit({
          tab: 'uoms',
          rowKey: null,
          prefill: {
            MaterialCode: material.materialCode,
            ExtUom: docUnit,
            // The document's own unit normalized to a SAP code (KG/G), NOT the material's SAP base
            // unit — GLC never converts to the pack unit (BAG/DRUM). Falls back to the raw doc unit
            // only if normalization can't map it, so the person can fix it in the popup.
            SapUom: normUom(docUnit) || docUnit,
            Factor: 1,
          },
          lastPrice,
          onSaved: async () => {
            await loadMasters(true);
            await finishLine();
          },
        });
      };

      const finishSapMaterial = async () => {
        const existingCm = masters.custmaterials.find((m) =>
          m.MaterialCodeSAP === material.materialCode
            && m.CustomerCode === customerCode
            && String(m.SalesOrg) === companyCode
            && customerMaterialCoversLine(m, line),
        );
        if (!existingCm) {
          await createMaster('custmaterials', {
            SalesOrg: companyCode,
            CustomerCode: customerCode,
            // MaterialCodeCode is required by the master form, so the SAP code stands in when the
            // document has no customer item code (harmless: matching only uses it when the line has one).
            MaterialCodeCode: line.extCode || material.materialCode,
            // The DOCUMENT's wording, so the next PO is recognised (see customerMaterialCoversLine).
            MaterialCodeName: line.desc || material.materialDescription,
            MaterialCodeSAP: material.materialCode,
            Isactive: 1,
          });
          await loadMasters(true);
        }
        await maybeAddUomRule();
      };

      // ocr.Material (apmaterials) master was removed — no material-master step anymore. Save the
      // CustomerMaterial mapping directly, then (only if needed) confirm the UoM conversion rule.
      await finishSapMaterial();
    });

  // MGT/Zoho counterpart of useSapMaterial: the candidate came from the matched Deal's Ordered
  // Items, so there is nothing to look up in SAP — save a CustomerMaterial mapping straight away
  // and, when the document unit differs from the Deal item's selling unit, save the conversion
  // rule too. Example: Deal unit BAG, Sub Unit KG, ratio 25 => 1 KG = 0.04 BAG.
  const saveZohoMaterial = (i: number, item: ZohoDealItem) =>
    guard(async () => {
      const customerCode = map?.header.customer?.code || doc.partnerCode;
      if (!customerCode) {
        showToast('Please identify the customer first');
        return;
      }
      const matCode = (item.materialCode || '').trim();
      if (!matCode) {
        showToast('This item has no Material Code in Zoho');
        return;
      }
      const line = doc.lines[i];
      const existingCm = masters.custmaterials.find((m) =>
        m.MaterialCodeSAP === matCode
          && m.CustomerCode === customerCode
          && String(m.SalesOrg) === companyCode
          && customerMaterialCoversLine(m, line),
      );
      if (!existingCm) {
        await createMaster('custmaterials', {
          SalesOrg: companyCode,
          CustomerCode: customerCode,
          MaterialCodeCode: line.extCode || matCode,
          // The DOCUMENT's wording, so the next PO is recognised (see customerMaterialCoversLine).
          MaterialCodeName: line.desc || item.materialName || item.materialDescription,
          MaterialCodeSAP: matCode,
          Isactive: 1,
        });
        await loadMasters(true);
      }

      const docUnit = (line.uom || '').trim();
      const zohoUnit = (item.unit || '').trim();
      const existingRule = masters.uoms.find((u) =>
        String(u.MaterialCode ?? u.MaterialCodeSAP ?? '') === matCode
          && String(u.ExtUom || '').trim().toLowerCase() === docUnit.toLowerCase()
          && (!u.SalesOrg || String(u.SalesOrg) === companyCode),
      );
      const ratio = Number(item.conversionRatio);
      const zohoDefaultFactor = Number.isFinite(ratio) && ratio > 0 ? 1 / ratio : 1;
      const existingFactor = Number(existingRule?.Factor);
      const existingRuleMatchesZoho = !!existingRule
        && String(existingRule.SapUom || '').trim().toLowerCase() === zohoUnit.toLowerCase()
        && (!Number.isFinite(ratio) || ratio <= 0 || Math.abs(existingFactor - zohoDefaultFactor) < 0.000001);
      const finishLine = async () => {
        await loadMasters(true);
        await setManualLine(i, matCode);
        showToast(`Saved Material ${matCode} from the Deal`);
      };

      if (!docUnit || !zohoUnit || docUnit.toLowerCase() === zohoUnit.toLowerCase() || existingRuleMatchesZoho) {
        await finishLine();
        return;
      }

      const factor = zohoDefaultFactor;
      setMasterEdit({
        tab: 'uoms',
        rowKey: existingRule?.Id ?? null,
        prefill: {
          MaterialCode: matCode,
          ExtUom: docUnit,
          SapUom: zohoUnit,
          Factor: factor,
          Note: Number.isFinite(ratio) && ratio > 0
            ? `Zoho: 1 ${zohoUnit} = ${ratio} ${docUnit} (document #${doc.docId})`
            : `Zoho unit conversion — please verify Factor (document #${doc.docId})`,
        },
        onSaved: finishLine,
      });
    });

  const addUomRule = (i: number) => {
    const l = doc.lines[i];
    const code = map?.lines[i]?.code;
    const mat = masters.materials.find((m) => m.MaterialCode === code) || {};
    // Open the rule this line actually uses (this company's first, then an all-company one) so a
    // wrong rule can be corrected in place; only when there is none is a new one pre-filled.
    const docUnit = (l.uom || '').trim().toLowerCase();
    const sameRule = (u: Record<string, any>) =>
      String(u.MaterialCode ?? u.MaterialCodeSAP ?? '') === String(code || '')
        && String(u.ExtUom || '').trim().toLowerCase() === docUnit;
    const existingRule =
      masters.uoms.find((u) => sameRule(u) && String(u.SalesOrg || '') === companyCode)
      ?? masters.uoms.find((u) => sameRule(u) && !u.SalesOrg);
    setMasterEdit({
      tab: 'uoms',
      rowKey: existingRule?.Id ?? null,
      prefill: existingRule ? undefined : {
        SalesOrg: companyCode,
        MaterialCode: code || '',
        ExtUom: l.uom || '',
        SapUom: mat.Uom || '',
        Note: 'Added from document #' + doc.docId,
      },
      onSaved: async () => {
        showToast('Unit conversion rule added — re-running Mapping');
        await runMap(true);
      },
    });
  };

  // Raised by MappingCards' SapCustomerPanel/ZohoCustomerPanel as soon as a Customer search
  // comes back with more than one candidate. Posts the question into the chat log (as if the AI
  // asked it) and kicks off loading each candidate's full record in the background so it's ready
  // once the person actually replies.
  const proposeCustomerMatch = (p: CustomerMatchProposal) => {
    setPendingMatch({ ...p });
    const lines = [
      `Found ${p.candidates.length} possible customer matches — please review:`,
      '',
      ...p.candidates.map((c, i) => {
        const bits = c.fields.filter((f) => f.value).map((f) => `${f.label}: ${f.value}`).join(' · ');
        return `${i + 1}. ${c.label}${bits ? ' — ' + bits : ''}`;
      }),
      '',
      'Just tell me which one to use (e.g. "use 1000698"), or ask me to "compare them" and I will analyze.',
    ];
    setMatchChatLog((log) => [...log, { role: 'assistant', text: lines.join('\n') }]);

    const fetchFull = p.fetchFull;
    if (fetchFull) {
      Promise.all(
        p.candidates.map((c) =>
          fetchFull(c.id)
            .then((fields) => [c.id, fields] as const)
            .catch(() => [c.id, null] as const),
        ),
      ).then((pairs) => {
        setPendingMatch((cur) => {
          if (!cur) return cur;
          const fullById = new Map(
            pairs.filter((pair): pair is [string, CompareField[]] => pair[1] != null),
          );
          if (fullById.size === 0) return cur;
          return {
            ...cur,
            candidates: cur.candidates.map((c) => (fullById.has(c.id) ? { ...c, fields: fullById.get(c.id)! } : c)),
          };
        });
      });
    }
  };

  // Raised when a Customer search stops being ambiguous (0 or 1 result) — e.g. the person edited
  // the customer name/tax ID and it now resolves cleanly. Drops a stale question rather than
  // leaving it sitting in the chat unanswered.
  const clearCustomerMatch = () => {
    setPendingMatch(null);
  };

  const applyPendingMatch = (pm: PendingCustomerMatch, candidateId: string, reason?: string) => {
    const c = pm.candidates.find((x) => x.id === candidateId);
    pm.onSelect(candidateId);
    setMatchChatLog((log) => [
      ...log,
      { role: 'assistant', text: `Selected "${c?.label ?? candidateId}" for you.${reason ? ' — ' + reason : ''}` },
    ]);
    // pm.onSelect() above runs useSapCustomer/useZohoCustomer, which already clears pendingMatch
    // itself once the master row is saved — not duplicated here to avoid racing that async save.
  };

  const AFFIRMATIONS = new Set([
    'ใช่', 'ใช่ครับ', 'ใช่ค่ะ', 'ใช้เลย', 'ตกลง', 'โอเค', 'yes', 'ok', 'okay', 'confirm', 'ยืนยัน',
  ]);

  // While pendingMatch is set, chat messages are about resolving that ambiguity, not about
  // fixing the document's OCR'd field values — route them to /api/compare instead of chatFix(),
  // and critically, never call setMap(null) here (that's what made the whole Mapping section
  // disappear before: chatFix() always cleared it, since a real field-fix really can invalidate
  // an old SAP/Zoho search — but proposing/discussing a match doesn't change any field).
  const sendMatchChat = (pm: PendingCustomerMatch, message: string) => {
    setMatchChatLog((log) => [...log, { role: 'user', text: message }]);
    const normalized = message.trim().toLowerCase();
    if (pm.lastSuggestedId && AFFIRMATIONS.has(normalized)) {
      applyPendingMatch(pm, pm.lastSuggestedId);
      return;
    }
    (async () => {
      let result: CompareResult;
      try {
        result = await compareCandidates(
          pm.docFields,
          pm.candidates.map((c) => ({ id: c.id, label: c.label, fields: c.fields })),
          chatProvider,
          message,
          pm.lastSuggestedId,
        );
      } catch (e) {
        setMatchChatLog((log) => [
          ...log,
          { role: 'assistant', text: 'Sorry, the analysis failed: ' + (e instanceof Error ? e.message : String(e)) },
        ]);
        return;
      }
      if (result.decision?.action === 'select' && result.decision.candidateId) {
        applyPendingMatch(pm, result.decision.candidateId, result.decision.reason);
        return;
      }
      if (result.decision?.action === 'suggest' && result.decision.candidateId) {
        const candidateId = result.decision.candidateId;
        const reason = result.decision.reason;
        const c = pm.candidates.find((x) => x.id === candidateId);
        setMatchChatLog((log) => [
          ...log,
          {
            role: 'assistant',
            text: `I think it's probably "${c?.label ?? candidateId}" — ${reason}\n\nType "yes" to confirm, or tell me which one to use instead.`,
          },
        ]);
        setPendingMatch((cur) => (cur ? { ...cur, lastSuggestedId: candidateId } : cur));
        return;
      }
      const summary = result.verdicts.length
        ? result.verdicts
            .map((v) => {
              const c = pm.candidates.find((x) => x.id === v.candidateId);
              return `- ${c?.label ?? v.candidateId}: ${v.match} (${v.confidence}%) — ${v.reason}`;
            })
            .join('\n')
        : 'I could not analyze that yet — try being more specific, e.g. "use the first one".';
      setMatchChatLog((log) => [...log, { role: 'assistant', text: summary }]);
    })();
  };

  const sendChat = (message: string) => {
    if (pendingMatch) {
      sendMatchChat(pendingMatch, message);
      return;
    }
    const image = chatImage;
    setChatHistory((hist) => [...hist, { role: 'user', text: message, image: image || undefined }]);
    setChatImage(null);
    (async () => {
      try {
        const r = await chatFix(doc.docId, {
          message,
          image,
          user: USER,
          provider: chatProvider,
          // Give the chat the current Deal's own Ordered Items so it can also pick a Material for
          // the Zoho Sales Order editor's lines (matched separately from Step 2 Data Mapping) --
          // backend has no visibility into which Deal is picked, that's pure page state.
          dealItems: isMgt && resolvedDeal
            ? resolvedDeal.items.map((it) => ({
                materialId: it.materialId,
                materialCode: it.materialCode,
                materialName: it.materialName,
              }))
            : undefined,
        });
        setDoc(r.document);
        // The AI can add or remove lines (e.g. "it's 2 lines, not 1"). The old mapping result still
        // has the OLD line count until the re-map below returns, and every card/table indexes
        // map.lines[i] by document line — so line 2 had no mapping row and the page crashed into
        // the error screen. Drop a mapping whose line count no longer matches; runMap refills it.
        setMap((m) => (m && m.lines.length !== (r.document?.lines?.length ?? 0) ? null : m));
        // The AI asked the screen to run the Zoho customer search for it (MGT documents) — open the
        // Customer card's Search Zoho panel and search the AI's query; the person picks + saves.
        if (r.action?.type === 'searchZoho' && r.action.query) {
          setCustomerSearchSignal({ query: r.action.query, nonce: Date.now() });
        }
        // The AI may also have picked a whole new Customer/Account from a live SAP search -- e.g.
        // "ลูกค้าไม่ใช่รายนี้ ช่วยหาใหม่จาก SAP" -- staged the same way setManualHeader('customer', …)
        // does (including its own side effect of clearing the stale Ship-to, since a different
        // customer means the old Ship-to list no longer applies), minus that function's immediate
        // runMap so it lands in the single re-map below. Applied before shipToCode so a Ship-to the
        // AI also picked in this same turn isn't wiped out by the customer change that precedes it.
        if (r.customerCode) {
          manual.current.header.customer = r.customerCode;
          manual.current.header.shipTo = '';
          setSelectedZohoShipTo(null);
        }
        // The AI may also have picked a Material for one or more lines -- e.g. "แถวที่ 2 เปลี่ยน
        // Material เป็น SOCA01-CN-BG-12" -- when the person asked for that in the chat message.
        // Neither branch below creates/updates a CustomerMaterial or UomConversion row itself
        // (that stays an explicit save action, e.g. picking straight from the SAP/Zoho search
        // panel, or "+ Master") -- this only stages the pick the same way a plain manual selection
        // does, for the person to review before Send.
        if (r.materialCodes) {
          for (const [idxStr, code] of Object.entries(r.materialCodes)) {
            const idx = Number(idxStr);
            if (Number.isNaN(idx) || !code) continue;
            const dl = doc.lines[idx];
            // Zoho path: if the code matches one of the current Deal's Ordered Items, stage it as
            // that Deal Item the same (non-saving) way the "Ask AI to suggest a match" popup does
            // (see useAiMaterialMatch) -- this is what the Zoho Sales Order editor's own line items
            // (and so the actual Zoho POST) read from, unlike Step 2's map.lines[i].code below.
            const dealItem = isMgt && dl ? resolvedDeal?.items.find((it) => it.materialCode === code) : undefined;
            if (dealItem && dl) {
              zohoStepRef.current?.stageMaterialMatch?.(idx, dealItem);
              continue;
            }
            // SAP path (or a Zoho code that only matched a CustomerMaterial row, not a Deal Item):
            // set the manual override for Step 2, same as picking a code straight from the Material
            // dropdown's plain (non-SAP-search) path.
            manual.current.lines[idx] = code;
          }
        }
        // The AI may also have picked a Ship-to for the whole document -- e.g. "เปลี่ยนที่อยู่จัดส่ง
        // เป็น ..." -- staged the same way picking one from the Ship-to dropdown does (setManualHeader),
        // just without that function's own immediate runMap so it lands in the single re-map below.
        if (r.shipToCode) {
          manual.current.header.shipTo = r.shipToCode;
          setSelectedZohoShipTo(null);
        }
        // A field fix can genuinely invalidate the old SAP/Zoho search, so the stale map can't
        // just be kept -- but leaving it null and stopping here (the old behaviour) made the
        // whole Mapping Results section disappear until the person noticed and pressed "Run
        // Mapping" again themselves, which reads as the page "going back" to before mapping ran.
        // Re-run it ourselves, silently, against the just-updated document, the same way every
        // other data-changing action on this page already does (quickAddMaterial, addUomRule, …).
        await runMap(true, r.document);
      } catch (e) {
        showToast(e instanceof Error ? e.message : String(e));
      } finally {
        try {
          setChatHistory(await getChat(doc.docId));
        } catch {
          /* ignore */
        }
      }
    })();
  };

  // ---- derived render pieces ----
  // Totals follow the vendor lookup: picking a vendor in the DETAIL table shows what that
  // vendor's own MIRO run is worth, so the figures can be keyed straight into SAP. VAT and
  // withholding tax sit on the main vendor's invoices (the shipping agent that re-bills the
  // rest), so they only show up when that vendor is the one selected. Read-only while filtered —
  // the stored header totals still cover the whole document.
  // The vendor carrying the largest cost total is the one the bundle is paid to — the shipping
  // agent that re-bills everything else. A tax row with no vendor code of its own belongs to it:
  // the supporting pages of a long bundle are read without the form in view, so their VAT rows
  // come back untagged, and the split already keeps untagged rows with this vendor.
  const mainVendorCode = (() => {
    if (doc.module !== 'AP' && doc.module !== 'II') return '';
    const totals = new Map<string, number>();
    for (const l of doc.lines) {
      if (l.extCode === 'WHT' || l.extCode === 'VAT' || l.extCode === 'DUTY') continue;
      const c = String(l.extra?.vendorCode ?? '').trim();
      if (!c) continue;
      totals.set(c, (totals.get(c) ?? 0) + (Number(l.amount) || 0));
    }
    return [...totals.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? '';
  })();

  const vendorTotals = (() => {
    if (!vendorFilter || (doc.module !== 'AP' && doc.module !== 'II')) return null;
    const codeOf = (l: DocLine) => String(l.extra?.vendorCode ?? '').trim();
    const isTax = (l: DocLine) =>
      l.extCode === 'WHT' || l.extCode === 'VAT' || l.extCode === 'DUTY';
    const costByVendor = new Map<string, number>();
    for (const l of doc.lines) {
      if (isTax(l)) continue;
      const c = codeOf(l);
      if (!c) continue;
      costByVendor.set(c, (costByVendor.get(c) ?? 0) + (Number(l.amount) || 0));
    }
    const mainVendor = mainVendorCode;
    const subTotal = costByVendor.get(vendorFilter) ?? 0;
    // A tax row carries its own vendor code when the read could match it to a form row; one that
    // does not belongs to the main vendor. Deciding this row by row — rather than letting a
    // single tagged row decide it for all of them — is what keeps these totals and the Tax tab
    // showing the same thing: a bundle whose duty rows were tagged and whose VAT rows were not
    // put the whole VAT here and nothing at all in the tab.
    const taxSum = (code: string) =>
      doc.lines
        .filter((l) => l.extCode === code)
        .filter((l) => (codeOf(l) || mainVendor) === vendorFilter)
        .reduce((a, l) => a + (Number(l.amount) || 0), 0);
    const vatAmount = taxSum('VAT');
    // Customs duty is not billed as an item but it is still paid to this vendor, so it counts
    // towards the net total even though it sits in the Tax tab.
    const dutyAmount = taxSum('DUTY');
    return {
      ...h,
      subTotal,
      vatAmount,
      whtAmount: taxSum('WHT'),
      totalAmount: subTotal + vatAmount + dutyAmount,
    };
  })();

  const totalsFields =
    doc.module === 'AP' ? (
      <FieldGrid
        // The numeric inputs are uncontrolled (defaultValue, so typing is not fought by a
        // re-render), which means switching vendors would leave the old figures on screen —
        // remount the grid when the lookup changes.
        key={vendorFilter || 'all'}
        fields={AP_TOTALS_H}
        values={vendorTotals ?? h}
        posted={locked || !!vendorTotals}
        numeric
        onEdit={editHeader}
      />
    ) : doc.module === 'SO' ? (
      <>
        <FieldGrid fields={SO_TOTALS_H} values={h} posted={locked} numeric onEdit={editHeader} />
      </>
    ) : doc.module === 'PODP' ? (
      <FieldGrid fields={PODP_TOTALS_H} values={h} posted={locked} numeric onEdit={editHeader} />
    ) : null;

  const sb = statusBadge(doc.status);
  // Locked to Gemini everywhere (per Megachem): every AI/engine dropdown fed by `providers`
  // (re-OCR engine, Chat-fix AI, Zoho step) shows only Gemini. Falls back to the full list only if
  // no Gemini engine is present, so a control is never empty.
  const allProviders = ocrProviders ?? [];
  // Exact id: 'paddle_gemini' also contains "gemini" but is an OCR engine, not a chat AI.
  const geminiProviders = allProviders.filter((p) => p.id.toLowerCase() === 'gemini');
  const providers = geminiProviders.length ? geminiProviders : allProviders;

  // Shared props for the item tables (used standalone for SO/II and inside the AP item tabs).
  const detailProps = {
    doc,
    map,
    masters,
    posted: locked,
    onEditLine: editLine,
    onEditLineExtra: editLineExtra,
    // Save + re-map silently (same path setLineSalesEmployee uses), so the note is stored and
    // the SAP/Zoho payload — built from the stored document — includes it.
    onCommitLineExtra: () => { if (!locked) runMap(true); },
    onManualLine: setManualLine,
    onDelLine: delLine,
    onAddLine: addLine,
    onLearn: learn,
    onShowLineExtra: (i: number) => setLineExtraIdx(i),
    onAddUomRule: addUomRule,
    vendorFilter,
    onVendorFilter: setVendorFilter,
    // Resolve a line's chosen Sales Employee Person ID to a display name for the DETAIL table's
    // "Sales Employee Name" column (GLC/SO). Falls back to the raw ID inside DetailTable when unknown.
    resolveSalesEmp: (id: string) => salesEmps.find((s) => s.personId === id)?.name || undefined,
  };
  // The MIRO tabs are read per vendor, so the Tax / Withholding rows show only that vendor's
  // invoices. Rows keep their real position in the header so editing and deleting still hit the
  // right one; when no row carries a vendor code (older documents) nothing is filtered out.
  const forVendor = <T extends { vendorCode?: string }>(rows: T[]) => {
    if (!vendorFilter) return { items: rows, at: rows.map((_, i) => i) };
    const at: number[] = [];
    rows.forEach((r, i) => {
      // Untagged rows belong to the main vendor, the same rule the totals above use.
      if ((String(r.vendorCode ?? '').trim() || mainVendorCode) === vendorFilter) at.push(i);
    });
    if (at.length === 0 && !mainVendorCode) {
      return { items: rows, at: rows.map((_, i) => i) };
    }
    return { items: at.map((i) => rows[i]), at };
  };
  const taxRows = forVendor<Record<string, any>>(h.taxItems || []);
  const whtRows = forVendor<Record<string, any>>(h.whtItems || []);
  // The G/L table follows the same filter. Without it, picking a supplier narrowed the Tax tab but
  // left every tax row in the G/L table — a GLC bundle showed the Customs Department's duty and VAT
  // under the shipping agent, and the exported file posted them there too.
  const glRows = forVendor<Record<string, any>>(glItems);
  const glProps = {
    module: doc.module,
    items: glRows.items,
    posted: locked,
    onEdit: (i: number, k: string, v: string) => editGlItem(glRows.at[i], k, v),
    onAdd: addGlItem,
    onDelete: (i: number) => delGlItem(glRows.at[i]),
  };
  const taxProps = {
    items: taxRows.items,
    posted: locked,
    onEdit: (i: number, k: string, v: string) => editTaxItem(taxRows.at[i], k, v),
    onAdd: addTaxItem,
    onDelete: (i: number) => delTaxItem(taxRows.at[i]),
  };
  const whtProps = {
    items: whtRows.items,
    posted: locked,
    onEdit: (i: number, k: string, v: string) => editWhtItem(whtRows.at[i], k, v),
    onAdd: addWhtItem,
    onDelete: (i: number) => delWhtItem(whtRows.at[i]),
  };

  return (
    <>
      <Steps current={posted ? 3 : 2} isMgt={isMgt} />

      {/* Map result panel */}
      {map &&
        (map.pass ? (
          <div className="result ok">
            <h3>
              <span className="badge b-ok"><i className="fa-solid fa-check" /> Passed</span> Data mapping complete —{' '}
              {isMgt ? 'continue to Step 3 to review the Sales Order' : 'ready to send to SAP'}
            </h3>
            {map.warns.length > 0 && (
              <ul>
                {map.warns.map((w, i) => (
                  <li key={i}><i className="fa-solid fa-triangle-exclamation" /> {w}</li>
                ))}
              </ul>
            )}
          </div>
        ) : (
          <div className="result bad">
            <h3>
              <span className="badge b-fail"><i className="fa-solid fa-xmark" /> Failed</span> {map.errors.length} items not found
            </h3>
            {isMgt && (
              <p className="hint">
                This checks items against SAP-style Master Data and does not block Step 3 — you can still open the Sales
                Order and review it before sending to Zoho CRM. The items below still need a match before they can be sent.
              </p>
            )}
            <ul>
              {map.errors.map((e, i) => (
                <li key={i}>
                  <b>{e.field}:</b> {e.msg}
                  <br />
                  <span className="hint"><i className="fa-solid fa-arrow-turn-down" /> How to fix: {e.fix}</span>
                </li>
              ))}
            </ul>
            <div className="row" style={{ marginTop: 14 }}>
              <button className="btn" onClick={() => navigate('/master')}>
                <i className="fa-solid fa-gear" /> Go to Mapping data
              </button>
              <span className="hint">or select the correct value from the dropdown below</span>
            </div>
          </div>
        ))}

      {posted && (
        // Where it actually went: CompanyCode is stamped at post time (MGT Sales Order = Zoho CRM,
        // everything else = SAP); older rows without it fall back to the current routing.
        (() => {
          const toZoho =
            doc.module === 'SO' && (doc.companyCode ? String(doc.companyCode).toUpperCase() === 'MGT' : isMgt);
          return (
            <div className="result ok">
              <h3>
                <i className="fa-solid fa-check" /> {toZoho ? 'Sent to Zoho CRM successfully' : 'Sent to SAP S/4HANA successfully'}
              </h3>
              <div>
                {toZoho ? 'Zoho Sales Order' : 'SAP Document'}: <code>{doc.sapDocNo}</code> | {moduleLabel(doc.module)} |{' '}
                {dt(doc.postedAt)}
              </div>
            </div>
          );
        })()
      )}

      {fileExpired && (
        <div className="result warn">
          <h3>
            <i className="fa-solid fa-file-circle-xmark" /> Original file removed
          </h3>
          <div>
            The uploaded file was removed on {dt(retention.expiredAt)} because this document was not posted within
            the retention period. The data read from it is still here and can be edited and posted, but View document
            and Re-read Document are no longer available. Upload the file again if you need it.
          </div>
        </div>
      )}
      {retention.kind === 'soon' && (
        <div className="result warn">
          <h3>
            <i className="fa-solid fa-hourglass-half" /> Original file will be removed in {retention.left}
          </h3>
          <div>
            This document has not been posted. Its uploaded file is kept for a limited time and will be removed around{' '}
            {retention.expiresAt?.toLocaleString('en-GB')}. Post the document, or save a change to it to restart the
            countdown.
          </div>
        </div>
      )}

      {isSplit && (
        <div className="result">
          <h3>
            <span className="badge b-idle"><i className="fa-solid fa-code-branch" /> Split</span> This Sales Order was
            split into separate Sales Orders — it is now locked and read-only
          </h3>
          <div>
            The line items were moved into the new Sales Orders created from the split
            {Array.isArray(doc.splitChildren) && doc.splitChildren.length > 0
              ? ` (${doc.splitChildren.length} document(s))`
              : ''}
            . Open those documents to review, edit, or send them to SAP. This original is kept only as a reference.
          </div>
          {/* Navigation links straight to each split-off Sales Order (from the parent's splitChildren,
              populated by GetDocumentAsync via ocr.SalesOrder.SourceDocId — persists across reloads). */}
          {Array.isArray(doc.splitChildren) && doc.splitChildren.length > 0 && (
            <div className="split-links">
              {doc.splitChildren.map((c: Record<string, any>) => {
                const cid = c.DocId ?? c.docId;
                const cno = c.DocNo ?? c.docNo;
                const cStatus = c.Status ?? c.status;
                const cTotal = c.TotalAmount ?? c.totalAmount;
                // Open each split-off Sales Order in a NEW TAB (anchor, not router navigate) so the
                // person keeps this reference document open while working the children.
                return (
                  <a key={cid} className="btn sm split-link" href={'/doc/' + cid} target="_blank" rel="noopener noreferrer">
                    <i className="fa-solid fa-arrow-up-right-from-square" /> Open #{cid}
                    {cno ? ` · ${cno}` : ''}
                    {cTotal != null ? ` · ${fmt(cTotal)}` : ''}
                    {cStatus ? (
                      <span className={'badge ' + statusBadge(String(cStatus)).cls} style={{ marginLeft: 6 }}>
                        {cStatus}
                      </span>
                    ) : null}
                  </a>
                );
              })}
            </div>
          )}
        </div>
      )}

      {/* Document header card */}
      <div className="card">
        <div className="card-h">
          <h2>Document #{doc.docId}</h2>
          <span
            className={
              'badge ' +
              (doc.provider === 'failed' ? 'b-fail' : (doc.confidence ?? 0) >= 0.9 ? 'b-ok' : 'b-warn')
            }
            title={doc.confidenceNote || undefined}
          >
            OCR {Math.round((doc.confidence || 0) * 100)}% · {doc.provider || ''}
          </span>
          {doc.tokensIn != null && (
            <span className="badge" title="Tokens used to read this document">
              <i className="fa-solid fa-bolt" /> {intFmt(doc.tokensIn)} in / {intFmt(doc.tokensOut)} out
            </span>
          )}
          {doc.cost != null && (
            <span className="badge">
              <i className="fa-solid fa-money-bill-wave" /> {fmtCost(doc.cost)} {doc.costCurrency || ''}
            </span>
          )}
          <span className="filechip"><i className="fa-solid fa-file-lines" /> {doc.fileName}</span>
          {doc.createdBy && (
            <span className="badge" title={`Uploaded ${dt(doc.createdAt)}`}>
              <i className="fa-solid fa-user" /> Uploaded by {doc.createdBy}
            </span>
          )}
          {doc.postedBy && (
            <span className="badge" title={`Posted ${dt(doc.postedAt)}`}>
              <i className="fa-solid fa-paper-plane" /> Posted by {doc.postedBy}
            </span>
          )}
          <span className={'badge ' + sb.cls}>{sb.label}</span>
          {/* Every action in one nowrap group on its own row, left aligned under the title. The
              buttons never split across rows, so nobody has to hunt for "Change Document" on a
              line of its own, and the row starts where the eye already is. */}
          <div className="card-h-actions">
          {!posted && !isSplit && !doc.sourceDocId && (
            <OcrProviderSelect
              providers={allProviders}
              value={reocrEngine}
              onChange={setReocrEngine}
              choices={READ_ENGINE_IDS}
            />
          )}
          <button
            className="btn sm primary"
            onClick={doReocr}
            disabled={posted || isSplit || !!doc.sourceDocId || fileExpired}
            title={fileExpired ? 'The original file was removed after the retention period' : undefined}
          >
            <i className="fa-solid fa-arrow-rotate-right" /> Re-read Document
          </button>
          <button className="btn sm ghost" onClick={openRaw}>
            <i className="fa-solid fa-file-lines" /> Extracted Text
          </button>
          {(doc.module === 'AP' || doc.module === 'II') && (
            <div className="btnmenu" ref={exportMenuRef}>
              <button
                className="btn sm ghost"
                onClick={() => setExportOpen((v) => !v)}
                disabled={exporting}
                aria-haspopup="menu"
                aria-expanded={exportOpen}
              >
                <i className="fa-solid fa-file-excel" /> {exporting ? 'Preparing…' : 'Export'}
                <i className="fa-solid fa-chevron-down btnmenu-caret" />
              </button>
              {exportOpen && (
                <div className="btnmenu-list" role="menu">
                  <button role="menuitem" onClick={exportInputVat} disabled={exporting}>
                    <i className="fa-solid fa-file-excel" /> Input VAT
                  </button>
                  <button role="menuitem" onClick={exportJournalVoucher} disabled={exporting}>
                    <i className="fa-solid fa-file-excel" /> Journal Voucher
                  </button>
                  <button role="menuitem" onClick={exportSapImport} disabled={exporting}>
                    <i className="fa-solid fa-file-excel" /> SAP Import
                  </button>
                </div>
              )}
            </div>
          )}
          <button
            className="btn sm ghost"
            onClick={() => setReviewOpen(true)}
            disabled={fileExpired}
            title={fileExpired ? 'The original file was removed after the retention period' : undefined}
          >
            <i className="fa-solid fa-eye" /> View document
          </button>
          <button className="btn sm ghost" onClick={() => navigate('/import/' + doc.module)}>
            Change Document
          </button>
          </div>
        </div>
        <div className="card-b">
          {/* Shown for both invoice modules: picking "Expense" moves the document from AP (MIRO,
              matched against materials) to II (FB60, posted to G/L accounts), so the dropdown has
              to stay reachable afterwards to move it back. */}
          {(doc.module === 'AP' || doc.module === 'II') && (
            <div className="f" style={{ maxWidth: 320, marginBottom: 14 }}>
              <label>Document Type</label>
              <select
                disabled={posted}
                value={doc.apDocCategory || ''}
                onChange={(e) => changeCategory(e.target.value)}
              >
                <option value="">— Select Document Type —</option>
                {(apDocCategories ?? []).map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.label}
                  </option>
                ))}
              </select>
            </div>
          )}
          {doc.sourceDocId && (
            <div
              className="hint"
              style={{ margin: '-6px 0 14px', padding: '8px 12px', background: 'var(--line-soft)', borderRadius: 'var(--r2)' }}
            >
              <i className="fa-solid fa-reply" /> Split from document{' '}
              <a href="#" onClick={(e) => { e.preventDefault(); navigate('/doc/' + doc.sourceDocId); }}>
                #{doc.sourceDocId}
              </a>
            </div>
          )}
          {doc.confidenceNote && (
            <div
              className="hint"
              style={{ margin: '-6px 0 14px', padding: '8px 12px', background: 'var(--line-soft)', borderRadius: 'var(--r2)' }}
            >
              <i className="fa-solid fa-triangle-exclamation" /> Reason accuracy is below 100%: {doc.confidenceNote}
            </div>
          )}
          {doc.module !== 'II' && (
            <>
              <p className="sec-title">HEADER — Header Information</p>
              <FieldGrid fields={headerDefFor(doc.module)} values={h} posted={locked} onEdit={editHeader} />
            </>
          )}
        </div>
      </div>

      {/* Mapping cards */}
      {map && (
        <MappingCards
          doc={doc}
          map={map}
          masters={masters}
          companyCode={companyCode}
          plant={me?.defaultPlant || ''}
          posted={locked}
          onManualHeader={setManualHeader}
          onSetSalesArea={setSalesArea}
          onManualLine={setManualLine}
          onQuickAddVendor={quickAddVendor}
          onQuickAddCustomer={quickAddCustomer}
          onQuickAddShipTo={quickAddShipTo}
          onQuickAddMaterial={quickAddMaterial}
          onUseSapMaterial={useSapMaterial}
          onAddUomRule={addUomRule}
          salesEmployees={salesEmps}
          lineSalesEmpSuggest={lineSalesEmpSuggest}
          onSetLineSalesEmployee={setLineSalesEmployee}
          onUseSapCustomer={useSapCustomer}
          onUseSapVendor={useSapVendor}
          onUseZohoAccount={useZohoCustomer}
          onUseSapShipTo={useSapShipTo}
          isMgt={isMgt}
          customerSearchSignal={customerSearchSignal}
          onProposeMatch={proposeCustomerMatch}
          onClearMatch={clearCustomerMatch}
          onUseZohoShipTo={useZohoShipTo}
          selectedDealId={selectedDealId}
          onSelectDeal={setSelectedDealId}
          onDealResolved={setResolvedDeal}
          dealItems={resolvedDeal?.items ?? []}
          onUseZohoMaterial={saveZohoMaterial}
          detailContent={isMgt && showDetail ? <DetailTable {...detailProps} bare isMgt /> : undefined}
        />
      )}

      {/* Detail table — item lines (AP/SO; hidden for II/PODP). For MGT/Zoho it's rendered inside
          the Mapping Results card above (via detailContent), so only non-MGT shows it here. */}
      {showDetail && !isMgt && <DetailTable {...detailProps} />}

      {/* Totals */}
      {totalsFields && (
        <div className="card">
          <div className="card-h">
            <h2>Totals</h2>
            {vendorTotals && (
              <>
                <div className="sp" />
                <span className="hint">
                  Vendor {vendorFilter} only · pick "All vendors" to go back to the whole document
                </span>
              </>
            )}
          </div>
          <div className="card-b">{totalsFields}</div>
        </div>
      )}

      {/* MIRO trade groups (AP) */}
      {doc.module === 'AP' && (
        <div className="card">
          <div className="card-h">
            <h2>Supplier Invoice (MIRO)</h2>
            {vendorTotals && (
              <>
                <div className="sp" />
                <span className="hint">Totals for vendor {vendorFilter}</span>
              </>
            )}
          </div>
          <TabbedGroups
            // Amount / tax figures follow the vendor lookup so the MIRO tabs show exactly what
            // this vendor's run is worth; remount on change because the numeric inputs are
            // uncontrolled (see the Totals card).
            key={vendorFilter || 'all'}
            groups={AP_TRADE_GROUPS}
            values={vendorTotals ?? h}
            posted={posted}
            onEdit={editHeader}
            extras={{
              'PO Reference': <DetailTable {...detailProps} bare />,
              Tax: <TaxDataTable {...taxProps} bare />,
              'Withholding Tax': <WhtTable {...whtProps} bare />,
            }}
          />
        </div>
      )}

      {/* Incoming Invoice (SAP FB60 · no PO) */}
      {doc.module === 'II' && (
        <IncomingInvoiceCard
          key={`${vendorFilter || 'all'}-${glRows.items.length}`}
          values={h}
          posted={posted}
          onEdit={editHeader}
          glItems={glRows.items}
          taxProps={taxProps}
          whtProps={whtProps}
        />
      )}

      {/* G/L Account items (AP/II) */}
      {/* Remounted when the vendor filter or the row count changes. The Amount cell is an
          uncontrolled input (it has to be, so a half-typed number is not reformatted under the
          cursor), and with a plain index key React reuses the same DOM node for a different row —
          which is how switching to the customs vendor showed the customs rows' assignment and tax
          code beside the PREVIOUS vendor's amounts. */}
      {showGlItems && (
        <GlItemsTable key={`${vendorFilter || 'all'}-${glRows.items.length}`} {...glProps} />
      )}

      {/* Chat fix */}
      {!posted && (
        <ChatFixCard
          docId={doc.docId}
          history={matchChatLog.length ? [...chatHistory, ...matchChatLog] : chatHistory}
          chatImage={chatImage}
          setChatImage={setChatImage}
          chatProvider={chatProvider}
          setChatProvider={setChatProvider}
          providers={providers}
          onSend={sendChat}
        />
      )}

      {isMgt && map && !isSplit && (
        <ZohoSalesOrderStep
          ref={zohoStepRef}
          doc={doc}
          map={map}
          selectedDealId={selectedDealId}
          resolvedDeal={resolvedDeal}
          selectedShipTo={selectedZohoShipTo}
          providers={providers}
          uomRules={masters?.uoms || []}
          shipTos={masters?.shiptos || []}
          salesOrg={salesOrg}
          posted={posted}
          onUseMaterial={async (lineIndex, item) => {
            await saveZohoMaterial(lineIndex, item);
            showToast('Saved the CustomerMaterial from Zoho — it will be used for this Sales Order');
          }}
          onPosted={setDoc}
        />
      )}

      {!isMgt && doc.module === 'SO' && map && !isSplit && (
        <SapSalesOrderStep
          ref={sapStepRef}
          doc={doc}
          map={map}
          salesOrg={salesOrg}
          posted={posted}
          onPosted={setDoc}
        />
      )}

      {/* Action bar */}
      <div className="card">
        <div className="card-b row">
          <button className="btn primary" onClick={() => runMap(false)} disabled={posted || isSplit}>
            <i className="fa-solid fa-magnifying-glass" /> Step 2 — Data Mapping
          </button>
          {/* MGT/Zoho Step 3: reveal the Sales Order editor above (mirrors how Step 2 reveals the
              mapping results). The actual Send lives inside that editor; this only opens it. */}
          {isMgt && (
            <button
              className="btn success"
              onClick={() => zohoStepRef.current?.open()}
              disabled={!(map && !posted && !isSplit)}
            >
              ⎋ Step 3 · Review Sales Order
            </button>
          )}
          {/* For MGT/Zoho and SAP/SO the Send button lives inside the review card above (next to
              the data it sends); only AP/II keep the old popup-based Send here (Zoho has no AP/II
              equivalent to match against). */}
          {!isMgt && doc.module === 'SO' && (
            <button
              className="btn success"
              onClick={() => sapStepRef.current?.open()}
              disabled={!(map && map.pass && !posted && !isSplit)}
            >
              ⎋ Step 3 · Review and send to SAP
            </button>
          )}
          {!isMgt && doc.module !== 'SO' && (
            <button
              className="btn success"
              onClick={() => setPostOpen(true)}
              disabled={!(map && map.pass && !posted && !isSplit)}
            >
              ⎋ Step 3 · Review and send to SAP
            </button>
          )}
          {!isMgt && doc.module !== 'SO' && (
            <button className="btn" onClick={openPayload} disabled={!(map && map.pass)}>
              {'{}'} View Payload
            </button>
          )}
          <div style={{ flex: 1 }} />
          <span className="hint">
            {isSplit
              ? 'This document has been split into other Sales Orders'
              : posted
                ? isMgt
                  ? 'This document has been sent to Zoho CRM'
                  : 'This document has been sent to SAP'
                : isMgt
                  ? map
                    ? selectedDealId
                      ? 'Press Step 3 to review and send the Sales Order'
                      : 'Match the customer and pick a Deal, then press Step 3'
                    : 'Click Mapping first (Step 2), then pick a Deal and press Step 3'
                  : map
                    ? map.pass
                      ? doc.module === 'SO'
                        ? 'Press Step 3 to review and send the Sales Order'
                        : 'Ready to send to SAP'
                      : 'Fix the items that failed before sending'
                    : 'Click Mapping to validate against Master Data'}
          </span>
        </div>
      </div>

      {/* ---- Modals ---- */}
      <Modal open={reviewOpen} onClose={() => setReviewOpen(false)} wide>
        <ModalHeader
          title={<><i className="fa-solid fa-eye" /> View document — {doc.fileName}</>}
          onClose={() => setReviewOpen(false)}
        />
        <div className="card-b">
          <p className="hint">Compare the original file with the data extracted in the HEADER/DETAIL sections</p>
          {doc.provider === 'demo' ? (
            <p className="hint">This document was generated from sample data (demo) — no original file to view</p>
          ) : fileErr ? (
            <p className="hint">Could not open the original file: {fileErr}</p>
          ) : !fileUrl ? (
            <p className="hint">Loading the original file…</p>
          ) : ['jpg', 'jpeg', 'png', 'tif', 'tiff', 'bmp', 'webp'].includes(
              (doc.fileName || '').split('.').pop()?.toLowerCase() || '',
            ) ? (
            <img
              src={fileUrl}
              style={{ maxWidth: '100%', borderRadius: 'var(--r3)', border: '1px solid var(--line)' }}
              alt=""
            />
          ) : (
            <iframe
              src={fileUrl}
              style={{ width: '100%', height: '78vh', border: '1px solid var(--line)', borderRadius: 'var(--r3)' }}
              title="document"
            />
          )}
        </div>
      </Modal>

      <Modal open={rawText != null} onClose={() => setRawText(null)}>
        <ModalHeader title="Text Extracted from File" onClose={() => setRawText(null)} />
        <div className="card-b">
          <p className="hint">Use this to check what the document reader detected</p>
          <pre className="json">{rawText}</pre>
        </div>
      </Modal>

      <Modal open={payload != null} onClose={() => setPayload(null)}>
        <ModalHeader title={isMgt ? 'Payload to Send to Zoho CRM' : 'Payload to Send to SAP'} onClose={() => setPayload(null)} />
        <div className="card-b">
          <p className="hint">Review the data prepared for submission.</p>
          <pre className="json">{JSON.stringify(payload, null, 2)}</pre>
        </div>
      </Modal>

      <Modal open={postOpen} onClose={() => setPostOpen(false)}>
        <ModalHeader
          title="Confirm Submission to SAP S/4HANA"
          onClose={() => setPostOpen(false)}
        />
        <div className="card-b">
            <>
              <p>
                The system will create a <b>{moduleLabel(doc.module)}</b> document in SAP
              </p>
              <div className="tw">
                <table style={{ minWidth: 'auto' }}>
                  <tbody>
                    {doc.module === 'SO' ? (
                      <>
                        <tr>
                          <th>Sold-to</th>
                          <td>{map?.header.customer.code} — {map?.header.customer.text}</td>
                        </tr>
                        <tr>
                          <th>Ship-to</th>
                          <td>{map?.header.shipTo.code} — {map?.header.shipTo.text}</td>
                        </tr>
                        <tr>
                          <th>Customer PO</th>
                          <td>{h.poNo || ''}</td>
                        </tr>
                      </>
                    ) : (
                      <>
                        <tr>
                          <th>Vendor</th>
                          <td>{map?.header.vendor.code} — {map?.header.vendor.text}</td>
                        </tr>
                        <tr>
                          <th>Invoice No.</th>
                          <td>{h.invoiceNo || ''}</td>
                        </tr>
                      </>
                    )}
                    <tr>
                      <th>Number of Items</th>
                      <td>{doc.lines.length} rows</td>
                    </tr>
                    <tr>
                      <th>Total</th>
                      <td>
                        <b>
                          {fmt(h.totalAmount)} {h.currency || 'THB'}
                        </b>
                      </td>
                    </tr>
                  </tbody>
                </table>
              </div>
              <div className="row" style={{ marginTop: 18 }}>
                <button className="btn success" onClick={confirmPost} disabled={SEND_DISABLED || posting}>
                  {posting ? 'Sending…' : 'Confirm send to SAP'}
                </button>
                <button className="btn" onClick={() => setPostOpen(false)}>
                  Cancel
                </button>
              </div>
            </>
        </div>
      </Modal>

      {lineExtraIdx != null && (
        <LineExtraModal
          line={doc.lines[lineExtraIdx]}
          onClose={() => setLineExtraIdx(null)}
          onEdit={(k, v) => editLineExtra(lineExtraIdx, k, v)}
        />
      )}
      <MasterEditModal
        state={masterEdit}
        masters={masters}
        onClose={() => setMasterEdit(null)}
        afterSave={async () => {
          await loadMasters(true);
        }}
        isMgt={isMgt}
      />
    </>
  );
}
