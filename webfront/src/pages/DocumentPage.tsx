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
  splitDocument,
  type ChatMessage,
  type DocModel,
} from '../api/documents';
import type { DocLine } from '../api/documents';
import {
  AP_TOTALS_H,
  AP_TRADE_GROUPS,
  headerDefFor,
  PODP_TOTALS_H,
  SO_REMARK_H,
  SO_TOTALS_H,
  WHT_CODE_RATE,
} from '../constants/fields';
import { SEND_DISABLED } from '../constants/flags';
import { dt, fmt, fmtCost, intFmt, moduleLabel, statusBadge } from '../utils/format';
import { findDupes } from '../utils/dupes';
import Steps from '../components/Steps';
import Modal, { ModalHeader } from '../components/Modal';
import OcrProviderSelect from '../components/OcrProviderSelect';
import FieldGrid from '../components/document/FieldGrid';
import TabbedGroups from '../components/document/TabbedGroups';
import DetailTable from '../components/document/DetailTable';
import GlItemsTable from '../components/document/GlItemsTable';
import TaxDataTable from '../components/document/TaxDataTable';
import WhtTable from '../components/document/WhtTable';
import IncomingInvoiceCard from '../components/document/IncomingInvoiceCard';
import MappingCards from '../components/document/MappingCards';
import ChatFixCard from '../components/document/ChatFixCard';
import SplitModal from '../components/document/SplitModal';
import LineExtraModal from '../components/document/LineExtraModal';
import MasterEditModal, {
  type MasterEditState,
} from '../components/master/MasterEditModal';
import { createMaster } from '../api/masters';
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
          baseFc: rate && amtFc > 0 ? Math.round((amtFc / rate) * 100) / 100 : base,
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
function seedGlItems(d: DocModel): DocModel {
  if (d.module !== 'II') return d;
  if (d.header.glItems && d.header.glItems.length) return d;
  const items = (d.lines || [])
    // "VAT" rows are the per-invoice VAT the read appended; they belong to the Tax tab only.
    .filter((l) => String(l.extCode || '').toUpperCase() !== 'VAT')
    .filter((l) => (Number(l.amount) || 0) !== 0)
    .map((l) => ({
      glAccount: '',
      // Withholding-tax rows the OCR appended to a shipping bundle (extCode "WHT") are credits;
      // the cost rows from the FORM SHIPPING EXPENSE table are debits.
      drCr: l.extCode === 'WHT' ? 'C' : 'D', // GlItemsTable's values: D = S-Debit, C = H-Credit
      amount: Number(l.amount) || 0,
      taxCode: '',
      assignment: '',
      itemText: String(l.desc || '').slice(0, 50), // SAP item text is 50 chars
      costCenter: '',
    }));
  if (!items.length) return d;
  return { ...d, header: { ...d.header, glItems: items } };
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
  if (d.header.taxCode === HEADER_TAX_CODE && d.header.businessPlace === businessPlace) return d;
  return { ...d, header: { ...d.header, taxCode: HEADER_TAX_CODE, businessPlace } };
}

function padBranch(value: unknown): string {
  const digits = String(value ?? '').replace(/\D/g, '');
  return digits.length ? digits.slice(-5).padStart(5, '0') : '00000';
}

function seedTaxItems(d: DocModel): DocModel {
  if (d.module !== 'AP' && d.module !== 'II') return d;
  // Rows the user already has stay as they are — only a row with no D/C at all gets the S default.
  if (d.header.taxItems && d.header.taxItems.length) {
    const items = d.header.taxItems.map((t: Record<string, unknown>) =>
      t.drCr ? t : { ...t, drCr: 'S' },
    );
    return { ...d, header: { ...d.header, taxItems: items } };
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
    issuerName: String(src?.extra?.issuerName ?? ''),
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
    .filter((l) => String(l.extCode || '').toUpperCase() === 'DUTY')
    .map((l) =>
      mkRow(Number(l.amount) || 0, '', String(l.extra?.vendorCode ?? ''), String(l.desc ?? '')),
    )
    .filter((r) => r.docCurrencyAmt > 0)
    // Import duty / excise carry no input VAT of their own — booked with VX (Input VAT Exempt).
    .map((r) => ({ ...r, taxCode: 'VX', taxRate: '0' }));
  const rows = [...vatRows, ...dutyRows];
  if (!rows.length) return d;
  return { ...d, header: { ...d.header, taxItems: rows } };
}

// Merges person-confirmed AI material matches into a GET .../preview response for display only
// -- moves a skipped line into `lines` (built from the chosen Deal Item + the document's own
// qty/price/unit, the same sourcing rule the backend's TryBuildMatchedLine uses) so the Material
// cards and the Send-to-Zoho confirm modal show it as matched immediately, without waiting on a
// re-fetch. Never changes what's actually sent to Zoho by itself -- that only happens once the
// same override is included as a line's materialId in the POST .../create request (see
// the Zoho step), which the backend independently re-validates against the Deal's own Items.
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

  // modals
  const [reviewOpen, setReviewOpen] = useState(false);
  const [rawText, setRawText] = useState<string | null>(null);
  const [payload, setPayload] = useState<Record<string, any> | null>(null);
  const [splitOpen, setSplitOpen] = useState(false);
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
      const d = await guard(() => getDocument(id));
      if (!alive) return;
      if (!d) {
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

  if (failed) return <div className="card"><div className="empty">Failed to load document</div></div>;
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
  const canSplit =
    doc.module === 'SO' && doc.lines.length > 1 && !posted && !isSplit && !doc.sourceDocId;
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
      const code = map?.lines[i].code;
      const l = doc.lines[i];
      if (!code) return;
      await learnMaterial(doc.docId, {
        partnerCode: doc.partnerCode,
        extCode: l.extCode,
        extDesc: l.desc,
        materialCode: code,
      });
      await loadMasters(true);
      showToast('Saved to Master Mapping — the system will match it automatically next time');
      await runMap(true);
    });

  const doReocr = () =>
    guard(async () => {
      const d = await reocrDocument(doc.docId, reocrEngine, USER);
      setDoc(seedHeaderTaxCode(seedGlItems(seedTaxItems(seedWhtItems(d)))));
      setMap(null);
      manual.current = { header: {}, lines: {} };
      if (d.provider === 'failed')
        showToast('Failed to read document — try attaching an image in the AI chat below');
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
        showToast(
          (r.simulated ? '(Simulation Mode) ' : '') + 'Document created in SAP successfully — No. ' + r.sapDocNo,
        );
        document.querySelector('.content')?.scrollTo({ top: 0, behavior: 'smooth' }); window.scrollTo({ top: 0, behavior: 'smooth' });
      } finally {
        setPosting(false);
      }
    });

  const doSplit = (assign: Record<string, number>) =>
    guard(async () => {
      const res = await splitDocument(doc.docId, assign, USER);
      setSplitOpen(false);
      setDoc(res.source);
      setMap(null);
      showToast(`<i className="fa-solid fa-check" /> Split successful — created ${res.created.length} new Sales Orders`);
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
  const useSapVendor = (bp: SapBusinessPartner) =>
    guard(async () => {
      const code = bp.businessPartnerId;
      await createMaster('vendors', {
        VendorCode: code,
        SapVendorCode: code,
        VendorName: bp.businessPartnerFullName || bp.businessPartnerName || h.vendorName || '',
        // SAP's own Tax ID wins when it has one; the document's is the fallback, same rule as
        // useSapCustomer — SAP is the source of truth and the document may carry a stale one.
        TaxId: bp.taxId || h.vendorTaxId || '',
        Branch: h.branch || '',
        Currency: h.currency || 'THB',
        IsActive: 1,
      });
      await loadMasters(true);
      setManualHeader('vendor', code);
      showToast('Vendor added from SAP and matched');
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
        prefill: { SalesOrg: companyCode, ShipToCode: info.code || h.shipToCode || '', CustomerCode: custCode,
          SapShipToCode: info.code || custCode, ShipToName: h.shipToName || '',
          ShipToAddress: info.address || h.shipToAddress || '',
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
          // Previously fell back straight to the document's own OCR'd address text and never
          // used SAP's own data at all, even though it was right there on `link.partner` --
          // fixed 2026-09-22 alongside the SAP address field expansion (see
          // SapBusinessPartnerClient.BusinessPartner). SAP's real address wins when we have it;
          // the OCR text is now only a fallback for when SAP returned nothing.
          ShipToAddress: [bp?.addressHouseNumber, bp?.addressStreet, bp?.addressStreet2, bp?.addressStreet3,
            bp?.addressStreet4, bp?.addressStreet5, bp?.addressDistrict, bp?.addressCity,
            bp?.addressPostalCode, bp?.addressCountry]
            .filter((v) => v && v.trim() !== '').join(', ') || h.shipToAddress || '',
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
            && String(m.SalesOrg) === companyCode,
        );
        if (!existingCm) {
          await createMaster('custmaterials', {
            SalesOrg: companyCode,
            CustomerCode: customerCode,
            MaterialCodeCode: line.extCode || material.materialCode,
            // Prefer the SAP (English) description so CustomerMaterial names stay English; the OCR
            // line text (often Thai) is only a fallback when SAP returns no description.
            MaterialCodeName: material.materialDescription || line.desc,
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
          && String(m.SalesOrg) === companyCode,
      );
      if (!existingCm) {
        await createMaster('custmaterials', {
          SalesOrg: companyCode,
          CustomerCode: customerCode,
          MaterialCodeCode: line.extCode || matCode,
          MaterialCodeName: item.materialName || item.materialDescription || line.desc,
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
    const code = map?.lines[i].code;
    const mat = masters.materials.find((m) => m.MaterialCode === code) || {};
    setMasterEdit({
      tab: 'uoms',
      rowKey: null,
      prefill: {
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
    const mainVendor = [...costByVendor.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? '';
    const subTotal = costByVendor.get(vendorFilter) ?? 0;
    // Tax rows carry their own vendor code when the read could match them to a form row; only
    // when none of them do does the whole tax fall back to the main vendor.
    const taxSum = (code: string) => {
      const rows = doc.lines.filter((l) => l.extCode === code);
      const tagged = rows.filter((l) => codeOf(l).length > 0);
      if (tagged.length > 0) {
        return tagged
          .filter((l) => codeOf(l) === vendorFilter)
          .reduce((a, l) => a + (Number(l.amount) || 0), 0);
      }
      return vendorFilter === mainVendor
        ? rows.reduce((a, l) => a + (Number(l.amount) || 0), 0)
        : 0;
    };
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
        posted={posted || !!vendorTotals}
        numeric
        onEdit={editHeader}
      />
    ) : doc.module === 'SO' ? (
      <>
        <FieldGrid fields={SO_TOTALS_H} values={h} posted={posted} numeric onEdit={editHeader} />
        <FieldGrid fields={SO_REMARK_H} values={h} posted={posted} onEdit={editHeader} />
      </>
    ) : doc.module === 'PODP' ? (
      <FieldGrid fields={PODP_TOTALS_H} values={h} posted={posted} numeric onEdit={editHeader} />
    ) : null;

  const sb = statusBadge(doc.status);
  // Locked to Gemini everywhere (per Megachem): every AI/engine dropdown fed by `providers`
  // (re-OCR engine, Chat-fix AI, Zoho step) shows only Gemini. Falls back to the full list only if
  // no Gemini engine is present, so a control is never empty.
  const allProviders = ocrProviders ?? [];
  const geminiProviders = allProviders.filter((p) => p.id.toLowerCase().includes('gemini'));
  const providers = geminiProviders.length ? geminiProviders : allProviders;

  // Shared props for the item tables (used standalone for SO/II and inside the AP item tabs).
  const detailProps = {
    doc,
    map,
    masters,
    posted,
    onEditLine: editLine,
    onEditLineExtra: editLineExtra,
    onManualLine: setManualLine,
    onDelLine: delLine,
    onAddLine: addLine,
    onLearn: learn,
    onShowLineExtra: (i: number) => setLineExtraIdx(i),
    onAddUomRule: addUomRule,
    vendorFilter,
    onVendorFilter: setVendorFilter,
  };
  const glProps = {
    module: doc.module,
    items: glItems,
    posted,
    onEdit: editGlItem,
    onAdd: addGlItem,
    onDelete: delGlItem,
  };
  // The MIRO tabs are read per vendor, so the Tax / Withholding rows show only that vendor's
  // invoices. Rows keep their real position in the header so editing and deleting still hit the
  // right one; when no row carries a vendor code (older documents) nothing is filtered out.
  const forVendor = <T extends { vendorCode?: string }>(rows: T[]) => {
    if (!vendorFilter) return { items: rows, at: rows.map((_, i) => i) };
    const at: number[] = [];
    rows.forEach((r, i) => {
      if (String(r.vendorCode ?? '') === vendorFilter) at.push(i);
    });
    if (at.length === 0 && !rows.some((r) => String(r.vendorCode ?? ''))) {
      return { items: rows, at: rows.map((_, i) => i) };
    }
    return { items: at.map((i) => rows[i]), at };
  };
  const taxRows = forVendor<Record<string, any>>(h.taxItems || []);
  const whtRows = forVendor<Record<string, any>>(h.whtItems || []);
  const taxProps = {
    items: taxRows.items,
    posted,
    onEdit: (i: number, k: string, v: string) => editTaxItem(taxRows.at[i], k, v),
    onAdd: addTaxItem,
    onDelete: (i: number) => delTaxItem(taxRows.at[i]),
  };
  const whtProps = {
    items: whtRows.items,
    posted,
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
        <div className="result ok">
          <h3><i className="fa-solid fa-check" /> Sent to SAP S/4HANA successfully</h3>
          <div>
            SAP Document: <code>{doc.sapDocNo}</code> | {moduleLabel(doc.module)} | {dt(doc.postedAt)}
          </div>
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
          <span className={'badge ' + sb.cls}>{sb.label}</span>
          <div className="sp" />
          {!posted && !isSplit && !doc.sourceDocId && (
            <OcrProviderSelect
              providers={providers}
              value={reocrEngine}
              onChange={setReocrEngine}
            />
          )}
          <button
            className="btn sm primary"
            onClick={doReocr}
            disabled={posted || isSplit || !!doc.sourceDocId}
          >
            <i className="fa-solid fa-arrow-rotate-right" /> Re-read Document
          </button>
          <button className="btn sm ghost" onClick={openRaw}>
            <i className="fa-solid fa-file-lines" /> Extracted Text
          </button>
          <button className="btn sm ghost" onClick={() => setReviewOpen(true)}>
            <i className="fa-solid fa-eye" /> View document
          </button>
          {canSplit && (
            <button className="btn sm ghost" onClick={() => setSplitOpen(true)}>
              <i className="fa-solid fa-code-branch" /> Split into Multiple SOs
            </button>
          )}
          <button className="btn sm ghost" onClick={() => navigate('/import/' + doc.module)}>
            Change Document
          </button>
        </div>
        <div className="card-b">
          {doc.module === 'AP' && (
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
              <FieldGrid fields={headerDefFor(doc.module)} values={h} posted={posted} onEdit={editHeader} />
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
          posted={posted}
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
          values={h}
          posted={posted}
          onEdit={editHeader}
          glItems={glItems}
          taxProps={taxProps}
          whtProps={whtProps}
        />
      )}

      {/* G/L Account items (AP/II) */}
      {showGlItems && <GlItemsTable {...glProps} />}

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
        <ModalHeader title={`<i className="fa-solid fa-eye" /> View document — ${doc.fileName}`} onClose={() => setReviewOpen(false)} />
        <div className="card-b">
          <p className="hint">Compare the original file with the data extracted in the HEADER/DETAIL sections</p>
          {doc.provider === 'demo' ? (
            <p className="hint">This document was generated from sample data (demo) — no original file to view</p>
          ) : ['jpg', 'jpeg', 'png', 'tif', 'tiff', 'bmp', 'webp'].includes(
              (doc.fileName || '').split('.').pop()?.toLowerCase() || '',
            ) ? (
            <img
              src={`/api/documents/${doc.docId}/file`}
              style={{ maxWidth: '100%', borderRadius: 'var(--r3)', border: '1px solid var(--line)' }}
              alt=""
            />
          ) : (
            <iframe
              src={`/api/documents/${doc.docId}/file`}
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
          <p className="hint">
            Endpoint: <code>{payload?._target}</code>
          </p>
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

      {splitOpen && (
        <SplitModal doc={doc} onClose={() => setSplitOpen(false)} onConfirm={doSplit} />
      )}
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
