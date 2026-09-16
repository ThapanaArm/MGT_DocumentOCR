import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
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
  mapDocument,
  postToSap,
  reocrDocument,
  setDocCategory,
  splitDocument,
  type ChatMessage,
  type DocModel,
  type MapResult,
} from '../api/documents';
import {
  AP_TOTALS_H,
  AP_TRADE_GROUPS,
  headerDefFor,
  PODP_TOTALS_H,
  SO_REMARK_H,
  SO_TOTALS_H,
} from '../constants/fields';
import { SEND_DISABLED } from '../constants/flags';
import { dt, fmt, fmtCost, intFmt, moduleLabel, num, statusBadge } from '../utils/format';
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
import SapSalesOrderEditor from '../components/document/SapSalesOrderEditor';
import LineExtraModal from '../components/document/LineExtraModal';
import MasterEditModal, {
  type MasterEditState,
} from '../components/master/MasterEditModal';
import { createMaster } from '../api/masters';
import type { SapBusinessPartner, SapMaterial, SapMaterialDetail, SapPartnerFunctionLink } from '../api/sap';
import { getSapMaterialDetail } from '../api/sap';
import { createZohoSalesOrder, getZohoSalesOrderPreview, getZohoSalesOrderPayload } from '../api/zoho';
import type {
  ZohoAccount,
  ZohoShipToInfo,
  ZohoDeal,
  ZohoDealItem,
  ZohoSalesOrderEdits,
  ZohoSalesOrderPreview,
  ZohoSalesOrderResult,
} from '../api/zoho';
import ZohoSalesOrderEditor from '../components/document/ZohoSalesOrderEditor';
import { compareCandidates, type CompareField, type CompareResult } from '../api/compare';
import type { CustomerMatchProposal } from '../components/document/MappingCards';

// Deprecated. The backend no longer reads any "user" value sent by the client — it stamps the
// identity from the validated Entra ID token instead, so whatever is passed here is discarded.
// Left in place only so the existing call signatures keep compiling; remove it together with the
// `user` parameters in api/documents.ts.
const USER = '(ignored by the server)';
const REOCR_INIT: Record<string, string> = {
  ocr: 'tesseract', text: 'text', azure: 'azure', claude: 'claude',
  claude_text: 'claude_text', typhoon: 'typhoon', gemini: 'gemini', openai: 'openai',
};

// SAP-like withholding-tax prefill: on a Supplier Invoice / liability-recording
// document (module AP/II), if OCR captured a WHT amount but no WHT rows exist yet,
// seed one row the way SAP's Create Supplier Invoice / Journal Entry WHT tab does —
// base defaults to the document amount excluding VAT. A goods invoice (whtAmount 0)
// gets no row, exactly like SAP for a non-WHT vendor.
function seedWhtItems(d: DocModel): DocModel {
  if (d.module !== 'AP' && d.module !== 'II') return d;
  if (d.header.whtItems && d.header.whtItems.length) return d;
  const amt = Number(d.header.whtAmount) || 0;
  if (amt <= 0) return d;
  const base = Number(d.header.subTotal) || 0;
  const row = { wtType: 'WHT Type for Payment Posting', whtCode: '', baseFc: base, amtFc: amt };
  return { ...d, header: { ...d.header, whtItems: [row] } };
}

// SAP-like VAT prefill for the Tax tab, mirroring seedWhtItems: on an AP/II
// supplier invoice, if OCR captured a VAT amount/rate but no tax rows exist yet,
// seed one input-VAT row (D/C = S, amount = vatAmount, rate = vatRate). Tax Code
// defaults to V1 for the standard Thai 7% input VAT; a non-VAT invoice gets no row.
function seedTaxItems(d: DocModel): DocModel {
  if (d.module !== 'AP' && d.module !== 'II') return d;
  if (d.header.taxItems && d.header.taxItems.length) return d;
  const amt = Number(d.header.vatAmount) || 0;
  const rate = Number(d.header.vatRate) || 0;
  if (amt <= 0 && rate <= 0) return d;
  const row = {
    drCr: 'S',
    docCurrencyAmt: amt,
    taxCode: rate === 7 ? 'V1' : '',
    validFrom: '',
    taxRate: rate ? String(rate) : '',
  };
  return { ...d, header: { ...d.header, taxItems: [row] } };
}

// Merges person-confirmed AI material matches into a GET .../preview response for display only
// -- moves a skipped line into `lines` (built from the chosen Deal Item + the document's own
// qty/price/unit, the same sourcing rule the backend's TryBuildMatchedLine uses) so the Material
// cards and the Send-to-Zoho confirm modal show it as matched immediately, without waiting on a
// re-fetch. Never changes what's actually sent to Zoho by itself -- that only happens once the
// same override is included as a line's materialId in the POST .../create request (see
// confirmPostZoho), which the backend independently re-validates against the Deal's own Items.
function applyMaterialOverrides(
  preview: ZohoSalesOrderPreview | null,
  overrides: Record<string, ZohoDealItem>,
  docLines: DocModel['lines'],
  uomRules: Record<string, any>[],
  salesOrg: string,
): ZohoSalesOrderPreview | null {
  if (!preview || !Object.keys(overrides).length) return preview;
  const lines = [...preview.lines];
  const skipped: typeof preview.skipped = [];
  for (const sk of preview.skipped) {
    const key = String(sk.itemNo);
    const ov = overrides[key];
    if (ov && ov.materialId) {
      const docLine = docLines.find((l) => String(l.itemNo) === key);
      const converted = docLine ? zohoConvertedValues(docLine, ov, uomRules, salesOrg) : null;
      lines.push({
        itemNo: sk.itemNo,
        desc: docLine?.desc ?? sk.desc ?? null,
        extCode: docLine?.extCode ?? sk.extCode ?? null,
        materialName: ov.materialName ?? null,
        materialCode: ov.materialCode ?? null,
        quantity: converted?.quantity ?? 0,
        unitPrice: converted?.unitPrice ?? null,
        unit: converted?.unit ?? ov.unit ?? null,
      });
    } else {
      skipped.push(sk);
    }
  }
  return { ...preview, lines, skipped };
}

function zohoConvertedValues(
  line: DocModel['lines'][number], item: ZohoDealItem,
  rules: Record<string, any>[], salesOrg: string,
) {
  const docUnit = (line.uom || '').trim();
  const targetUnit = (item.unit || '').trim();
  const qty = Number(line.qty) || 0;
  const rawPrice = line.price !== '' && line.price != null ? Number(line.price) : NaN;
  if (!docUnit || !targetUnit || docUnit.toLowerCase() === targetUnit.toLowerCase())
    return { quantity: qty, unitPrice: Number.isFinite(rawPrice) ? rawPrice : null, unit: targetUnit || docUnit };

  const materialCode = (item.materialCode || '').trim();
  const rule = rules
    .filter((u) => !u.SalesOrg || String(u.SalesOrg) === salesOrg)
    .sort((a, b) => Number(String(b.SalesOrg) === salesOrg) - Number(String(a.SalesOrg) === salesOrg))
    .find((u) => String(u.MaterialCode ?? u.MaterialCodeSAP ?? '') === materialCode
      && String(u.ExtUom || '').trim().toLowerCase() === docUnit.toLowerCase()
      && String(u.SapUom || '').trim().toLowerCase() === targetUnit.toLowerCase());
  const ratio = Number(item.conversionRatio);
  // Zoho's Conversion_Ratio means sub-units contained in one Zoho selling unit
  // (e.g. 10 KG per BAG). Our stored factor is the reverse direction because mapping computes
  // document quantity * factor, therefore the default is 1/10 BAG per KG.
  const factor = rule && Number(rule.Factor) > 0 ? Number(rule.Factor)
    : ratio > 0 ? 1 / ratio : 1;
  return {
    quantity: Math.round(qty * factor * 1000) / 1000,
    unitPrice: Number.isFinite(rawPrice) ? Math.round((rawPrice / factor) * 1e6) / 1e6 : null,
    unit: targetUnit,
  };
}

export default function DocumentPage() {
  const { docId } = useParams<{ docId: string }>();
  const id = Number(docId);
  const navigate = useNavigate();
  const { guard, showToast } = useAppState();
  const { ocrProviders, loadOcrProviders, apDocCategories, loadApDocCategories, masters, loadMasters } =
    useMeta();
  // Which company opened this document decides where Sales Order Customer matching looks:
  // MGT -> Zoho CRM, Green Leaf (and anyone else) -> SAP, per AppLayout's Outlet context.
  const { me } = useOutletContext<{ me: Me | null }>();
  const isMgt = me?.primaryCompany?.companyCode === 'MGT';

  const [doc, setDoc] = useState<DocModel | null>(null);
  const currentSalesOrg = doc?.header.salesOrg || me?.salesOrganization || (isMgt ? '1000' : '2000');
  const [map, setMap] = useState<MapResult | null>(null);
  const [failed, setFailed] = useState(false);
  const manual = useRef<{ header: Record<string, string>; lines: Record<number, string> }>({
    header: {},
    lines: {},
  });

  const [chatHistory, setChatHistory] = useState<ChatMessage[]>([]);
  const [chatImage, setChatImage] = useState<string | null>(null);
  const [chatProvider, setChatProvider] = useState('claude');
  const [reocrEngine, setReocrEngine] = useState('auto');

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
  const [lineExtraIdx, setLineExtraIdx] = useState<number | null>(null);
  const [postOpen, setPostOpen] = useState(false);
  const [posting, setPosting] = useState(false);
  // MGT/Zoho Step 3: the Sales Order editor stays hidden until the person presses the Step 3
  // button (mirroring how Step 2 reveals the mapping results), then this stays true.
  const [showZohoEditor, setShowZohoEditor] = useState(false);
  const zohoEditorRef = useRef<HTMLDivElement | null>(null);
  // SAP Step 3 (SO module only) -- same reveal pattern as showZohoEditor/zohoEditorRef above, so
  // the "check before you send" screen looks and behaves the same on both platforms. AP/II
  // documents keep the old postOpen confirm popup (Zoho has no equivalent to compare against).
  const [showSapEditor, setShowSapEditor] = useState(false);
  const sapEditorRef = useRef<HTMLDivElement | null>(null);
  // MGT/Zoho only -- which Deal is picked on the Deal card (lifted up from ZohoDealCard) and the
  // in-flight state for creating the Zoho Sales Order from the inline editor. Kept entirely
  // separate from postOpen/posting/confirmPost above, the SAP-only path for non-MGT documents.
  const [selectedDealId, setSelectedDealId] = useState('');
  // The full resolved Deal (with its Deal Items), lifted up from MappingCards' Deal card -- the
  // candidate pool for the inline Sales Order editor's AI-suggested-match tool.
  const [resolvedDeal, setResolvedDeal] = useState<ZohoDeal | null>(null);
  const [zohoPosting, setZohoPosting] = useState(false);
  const [zohoResult, setZohoResult] = useState<ZohoSalesOrderResult | null>(null);
  const [selectedZohoShipTo, setSelectedZohoShipTo] = useState<ZohoShipToInfo | null>(null);
  const [zohoHeader, setZohoHeader] = useState({
    subject: '',
    customerRef: '',
    deliveryDate: '',
    paymentTerms: '',
    paymentCurrency: '',
    incoterms: '',
    taxId: '',
  });
  const [zohoLineEdits, setZohoLineEdits] = useState<
    Record<string, { quantity: string; unitPrice: string; unit: string; description: string; materialId?: string }>
  >({});
  const editZohoLine = (
    itemNo: unknown,
    patch: Partial<{ quantity: string; unitPrice: string; unit: string; description: string; materialId?: string }>,
  ) =>
    setZohoLineEdits((prev) => {
      const key = String(itemNo);
      const current = prev[key] || { quantity: '', unitPrice: '', unit: '', description: '' };
      return { ...prev, [key]: { ...current, ...patch } };
    });

  // Fetches the Sales Order preview (GET .../preview) as soon as a Deal is picked, so the inline
  // Sales Order editor is populated and visible without the person pressing Send first. Silent on
  // failure (e.g. the Deal has no linked Account yet) -- this runs automatically on every Deal
  // switch, so it must not pop an error toast the way a person-initiated action would; the editor
  // shows the reason inline via zohoMaterialError instead.
  const [zohoMaterialPreview, setZohoMaterialPreview] = useState<ZohoSalesOrderPreview | null>(null);
  const [zohoMaterialLoading, setZohoMaterialLoading] = useState(false);
  const [zohoMaterialError, setZohoMaterialError] = useState<string | null>(null);
  // Deal Items a person has confirmed via the AI-suggested-match tool on a "Material — Row N"
  // card, keyed by the document line's itemNo -- Deal-specific, so it's dropped whenever the
  // selected Deal (or the document) changes rather than silently carrying a stale match over.
  const [zohoMaterialOverrides, setZohoMaterialOverrides] = useState<Record<string, ZohoDealItem>>({});
  // A confirmed AI match is tied to one Deal on one document -- drop it (not on every doc
  // content edit, only when either of those actually changes) rather than silently carry a
  // stale choice over to a different Deal or document.
  useEffect(() => {
    setZohoMaterialOverrides({});
  }, [doc?.docId, selectedDealId]);
  useEffect(() => {
    if (!doc || !isMgt || !selectedDealId) {
      setZohoMaterialPreview(null);
      setZohoMaterialError(null);
      return;
    }
    let cancelled = false;
    setZohoMaterialLoading(true);
    setZohoMaterialError(null);
    getZohoSalesOrderPreview(doc.docId, selectedDealId)
      .then((p) => {
        if (cancelled) return;
        setZohoMaterialPreview(p);
      })
      .catch((err) => {
        if (cancelled) return;
        setZohoMaterialPreview(null);
        setZohoMaterialError(err instanceof Error ? err.message : 'Could not check this Deal against Zoho CRM');
      })
      .finally(() => {
        if (!cancelled) setZohoMaterialLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [doc, isMgt, selectedDealId]);

  // What the inline Sales Order editor shows -- the backend's own preview with any AI-confirmed
  // matches folded in client-side, so applying one updates the page immediately without a re-fetch.
  const zohoMaterialPreviewMerged = useMemo(
    () => applyMaterialOverrides(zohoMaterialPreview, zohoMaterialOverrides, doc?.lines || [], masters?.uoms || [], currentSalesOrg),
    [zohoMaterialPreview, zohoMaterialOverrides, doc, masters?.uoms, currentSalesOrg],
  );

  // Seed the editable header + per-line fields from a freshly loaded preview. Keyed on the raw
  // preview object identity, which only changes on an actual re-fetch (new Deal / document), so
  // it never clobbers what the person has typed, nor the AI-match edits seeded below.
  useEffect(() => {
    const p = zohoMaterialPreview;
    if (!p) return;
    setZohoHeader({
      subject: p.subject || '',
      customerRef: p.customerRef || '',
      deliveryDate: p.deliveryDate || '',
      paymentTerms: p.paymentTerms || '',
      paymentCurrency: p.paymentCurrency || '',
      incoterms: p.incoterms || '',
      taxId: p.taxId || '',
    });
    const le: Record<string, { quantity: string; unitPrice: string; unit: string; description: string; materialId?: string }> = {};
    p.lines.forEach((l) => {
      le[String(l.itemNo)] = {
        quantity: l.quantity != null ? String(l.quantity) : '',
        unitPrice: l.unitPrice != null ? String(l.unitPrice) : '',
        unit: l.unit || '',
        description: l.desc || '',
      };
    });
    setZohoLineEdits(le);
  }, [zohoMaterialPreview]);

  // Called from the inline Sales Order editor's "Ask AI to match" tool once the person clicks
  // "Use this record" on a suggested Deal Item -- keyed by the document line's itemNo. Records the
  // override (so the merged preview shows the line as matched) and seeds that line's editable
  // fields incl. its materialId, which is what gets sent (and backend-revalidated) on create.
  const useAiMaterialMatch = async (itemNo: unknown, item: ZohoDealItem) => {
    if (!doc || !item.materialId) return;
    const key = String(itemNo);
    const lineIndex = doc.lines.findIndex((l) => String(l.itemNo) === key);
    if (lineIndex < 0) return;

    // Persist the same CustomerMaterial mapping as the normal Zoho "Use & Save" action.
    // Previously AI matching only added a one-off MaterialId override to this Sales Order, so
    // the next document had to be matched again, unlike the SAP flow.
    await useZohoMaterial(lineIndex, item);
    setZohoMaterialOverrides((prev) => ({ ...prev, [key]: item }));
    const dl = doc.lines[lineIndex];
    const converted = zohoConvertedValues(dl, item, masters?.uoms || [], currentSalesOrg);
    editZohoLine(key, {
      materialId: item.materialId,
      quantity: String(converted.quantity),
      unitPrice: converted.unitPrice != null ? String(converted.unitPrice) : '',
      unit: converted.unit,
      description: dl?.desc || '',
    });
    showToast('บันทึก CustomerMaterial จาก Zoho แล้ว และจะใช้กับ Sales Order นี้');
  };

  const [masterEdit, setMasterEdit] = useState<MasterEditState | null>(null);

  const runMap = useCallback(
    async (silent: boolean, forDoc?: DocModel) => {
      const d = forDoc || doc;
      if (!d) return;
      const res = await guard(() =>
        mapDocument(d.docId, {
          header: d.header,
          lines: d.lines,
          manual: manual.current,
          user: USER,
        }),
      );
      if (res) {
        setDoc(res.document);
        setMap(res);
        if (!silent) {
          showToast(
            res.pass
              ? 'จับคู่ข้อมูลสำเร็จและบันทึกแล้ว'
              : 'ยังจับคู่ไม่ครบ ' + res.errors.length + ' รายการ กรุณาตรวจและเลือกข้อมูล',
          );
          window.scrollTo({ top: 0, behavior: 'smooth' });
        }
      }
    },
    [doc, guard, showToast],
  );

  // Load document on mount / id change.
  useEffect(() => {
    let alive = true;
    setDoc(null);
    setMap(null);
    setFailed(false);
    setShowZohoEditor(false);
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
      setReocrEngine(REOCR_INIT[d.provider || ''] || 'auto');
      setDoc(seedTaxItems(seedWhtItems(d)));
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

  if (failed) return <div className="card"><div className="empty">Failed to load document</div></div>;
  if (!doc || !masters) return <div className="card"><div className="empty">Loading…</div></div>;

  const h = doc.header;
  const salesOrg = h.salesOrg || me?.salesOrganization || (isMgt ? '1000' : '2000');
  const posted = doc.status === 'POSTED';
  const isSplit = doc.status === 'SPLIT';
  const canSplit =
    doc.module === 'SO' && doc.lines.length > 1 && !posted && !isSplit && !doc.sourceDocId;
  const glItems = h.glItems || [];
  const showDetail = doc.module !== 'II' && doc.module !== 'PODP';
  const showGlItems = doc.module === 'AP' || doc.module === 'II';

  // ---- editing handlers ----
  const patchDoc = (fn: (d: DocModel) => DocModel) => {
    setDoc((prev) => (prev ? fn(prev) : prev));
    setMap(null);
  };
  const editHeader = (k: string, v: string) =>
    patchDoc((d) => ({ ...d, header: { ...d.header, [k]: v } }));
  const editLine = (i: number, k: string, v: string) =>
    patchDoc((d) => {
      const lines = d.lines.slice();
      const l = { ...lines[i], [k]: v };
      if (k === 'qty' || k === 'price') l.amount = (num(l.qty) * num(l.price)).toFixed(2);
      lines[i] = l;
      return { ...d, lines };
    });
  const editLineExtra = (i: number, k: string, v: string) =>
    setDoc((d) => {
      if (!d) return d;
      const lines = d.lines.slice();
      lines[i] = { ...lines[i], extra: { ...(lines[i].extra || {}), [k]: v } };
      return { ...d, lines };
    });
  const addLine = () =>
    patchDoc((d) => ({
      ...d,
      lines: [
        ...d.lines,
        { itemNo: (d.lines.length + 1) * 10, extCode: '', desc: '', qty: 0, uom: 'EA', price: 0, amount: 0, materialCode: '' },
      ],
    }));
  const delLine = (i: number) =>
    patchDoc((d) => ({ ...d, lines: d.lines.filter((_l, j) => j !== i) }));

  const editGlItem = (i: number, k: string, v: string) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.glItems || []).slice();
      items[i] = { ...items[i], [k]: v };
      return { ...d, header: { ...d.header, glItems: items } };
    });
  const addGlItem = () =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.glItems || []).slice();
      items.push({ glAccount: '', drCr: '', amount: 0, taxCode: '', assignment: '', itemText: '', costCenter: '' });
      return { ...d, header: { ...d.header, glItems: items } };
    });
  const delGlItem = (i: number) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.glItems || []).filter((_g: unknown, j: number) => j !== i);
      return { ...d, header: { ...d.header, glItems: items } };
    });

  const editTaxItem = (i: number, k: string, v: string) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.taxItems || []).slice();
      items[i] = { ...items[i], [k]: v };
      return { ...d, header: { ...d.header, taxItems: items } };
    });
  const addTaxItem = () =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.taxItems || []).slice();
      items.push({ drCr: 'S', docCurrencyAmt: 0, taxCode: '', validFrom: '', taxRate: '' });
      return { ...d, header: { ...d.header, taxItems: items } };
    });
  const delTaxItem = (i: number) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.taxItems || []).filter((_t: unknown, j: number) => j !== i);
      return { ...d, header: { ...d.header, taxItems: items } };
    });

  const editWhtItem = (i: number, k: string, v: string) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.whtItems || []).slice();
      items[i] = { ...items[i], [k]: v };
      return { ...d, header: { ...d.header, whtItems: items } };
    });
  const addWhtItem = () =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.whtItems || []).slice();
      items.push({ wtType: '', whtCode: '', baseFc: Number(d.header.subTotal) || 0, amtFc: 0 });
      return { ...d, header: { ...d.header, whtItems: items } };
    });
  const delWhtItem = (i: number) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.whtItems || []).filter((_w: unknown, j: number) => j !== i);
      return { ...d, header: { ...d.header, whtItems: items } };
    });

  const setManualHeader = (k: string, v: string) => {
    manual.current.header[k] = v;
    if (k === 'customer') {
      manual.current.header.shipTo = '';
      setSelectedZohoShipTo(null);
    }
    if (k === 'shipTo') setSelectedZohoShipTo(null);
    runMap(true);
  };
  const setManualLine = (i: number, v: string) => {
    manual.current.lines[i] = v;
    runMap(true);
  };

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
      setDoc(seedTaxItems(seedWhtItems(d)));
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
        window.scrollTo({ top: 0, behavior: 'smooth' });
      } finally {
        setPosting(false);
      }
    });

  // Creates the Zoho Sales Order straight from the inline editor's current values -- no confirm
  // popup (the editable table on the page IS the confirmation). The result, success or failure,
  // is shown inline in the editor via zohoResult.
  // The request body sent to both create and payload -- built from the inline editor's current
  // header + line edits, so "View Payload" shows exactly what "Send" would submit.
  const buildZohoEdits = (): ZohoSalesOrderEdits => {
    // A previously saved master-row selection has no structured Zoho fields in component state;
    // still send its saved code/address instead of silently omitting Ship-to altogether.
    const mappedShipTo = (masters.shiptos || []).find(
      (x) => String(x.SapShipToCode ?? '') === String(map?.header.shipTo.code ?? ''),
    );
    const shipTo = selectedZohoShipTo || (mappedShipTo ? {
      code: String(mappedShipTo.ShipToCode ?? mappedShipTo.SapShipToCode ?? ''),
      address: String(mappedShipTo.ShipToAddress ?? mappedShipTo.Address ?? ''),
    } : undefined);
    return {
      subject: zohoHeader.subject || undefined,
      customerRef: zohoHeader.customerRef || undefined,
      deliveryDate: zohoHeader.deliveryDate || undefined,
      paymentTerms: zohoHeader.paymentTerms || undefined,
      paymentCurrency: zohoHeader.paymentCurrency || undefined,
      incoterms: zohoHeader.incoterms || undefined,
      taxId: zohoHeader.taxId || undefined,
      shipTo,
      lines: Object.entries(zohoLineEdits).map(([itemNo, e]) => ({
        itemNo,
        quantity: e.quantity !== '' ? Number(e.quantity) : undefined,
        unitPrice: e.unitPrice !== '' ? Number(e.unitPrice) : undefined,
        unit: e.unit || undefined,
        description: e.description || undefined,
        materialId: e.materialId || undefined,
      })),
    };
  };

  const confirmPostZoho = () =>
    guard(async () => {
      if (!selectedDealId) return;
      setZohoPosting(true);
      setZohoResult(null);
      try {
        const r = await createZohoSalesOrder(doc.docId, selectedDealId, buildZohoEdits());
        setZohoResult(r);
        if (r.success) {
          showToast(`Created Sales Order in Zoho CRM successfully — linked to Deal "${r.dealName}" (${r.linesSent} line(s) sent)`);
          window.scrollTo({ top: 0, behavior: 'smooth' });
        }
      } finally {
        setZohoPosting(false);
      }
    });

  // "View Payload" for Zoho -- fetches the exact JSON that Send would POST (from the current
  // edits) and shows it in the same payload modal the SAP path uses.
  const viewZohoPayload = () =>
    guard(async () => {
      if (!selectedDealId) return;
      const p = await getZohoSalesOrderPayload(doc.docId, selectedDealId, buildZohoEdits());
      setPayload(p as Record<string, any>);
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
      prefill: { CompanyName: h.customerName || '', TaxId: h.customerTaxId || '', Branch: h.branch || '', SalesOrg: salesOrg },
      dupes: findDupes(masters, 'customers', h.customerName, h.customerTaxId, (x) => String(x.SalesOrg) === salesOrg),
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
        SalesOrg: salesOrg,
        Branch: h.branch || '',
        IsActive: 1,
        // SAP's own Tax ID wins when it has one — same rationale as useZohoCustomer below: SAP is
        // the source of truth here, and should show up even when the document had no Tax ID (or a
        // stale/wrong one) of its own to go on.
        TaxId: bp.taxId || h.customerTaxId || '',
      });
      await loadMasters(true);
      setManualHeader('customer', code);
      if (pendingMatch) {
        setMatchChatLog((log) => [...log, { role: 'assistant', text: `เลือก "${code}" จากตารางโดยตรงแล้วครับ` }]);
        setPendingMatch(null);
      }
      showToast('Customer added from SAP and matched');
    });

  // MGT uses the real Zoho Account Code stored in Customer.ComcompyCodeSAP.
  const useZohoCustomer = (acc: ZohoAccount) =>
    guard(async () => {
      const code = acc.accountCode?.trim();
      if (!code) { showToast('Zoho record นี้ไม่มี Account Code'); return; }
      await createMaster('customers', {
        ComcompyCodeSAP: code,
        CompanyName: h.customerName || '',
        CompanyNameSAP: acc.accountName || '',
        SalesOrg: salesOrg,
        IsActive: 1,
        TaxId: acc.taxId || h.customerTaxId || '',
        Branch: h.branch || '',
      });
      await loadMasters(true);
      setManualHeader('customer', code);
      if (pendingMatch) {
        setMatchChatLog((log) => [...log, { role: 'assistant', text: `เลือก "${code}" จากตารางโดยตรงแล้วครับ` }]);
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
        prefill: { ShipToCode: info.code || h.shipToCode || '', CustomerCode: custCode,
          SapShipToCode: info.code || custCode, ShipToName: h.shipToName || '',
          ShipToAddress: info.address || h.shipToAddress || '' },
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
      setMasterEdit({
        tab: 'shiptos', rowKey: null,
        prefill: { ShipToCode: h.shipToCode || '', CustomerCode: custCode, SapShipToCode: code,
          ShipToName: h.shipToName || '', ShipToAddress: h.shipToAddress || '' },
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
      prefill: { CustomerCode: custCode, ShipToCode: h.shipToCode || '', ShipToName: h.shipToName || '', ShipToAddress: h.shipToAddress || '' },
      dupes: findDupes(masters, 'shiptos', h.shipToName, null, (x) => x.CustomerCode === custCode),
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
      prefill: so ? { SalesOrg: salesOrg, CustomerCode: customerCode, MaterialCodeCode: l.extCode || '', MaterialCodeName: l.desc || '' } : { Description: l.desc || '', Uom: l.uom || '', Plant: '1000' },
      dupes: so ? findDupes(masters, 'custmaterials', l.desc, null, (x) => x.CustomerCode === customerCode && String(x.SalesOrg) === salesOrg) : findDupes(masters, 'apmaterials', l.desc, null),
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
      gram: 'G', g: 'G',
      ton: 'TON', tonne: 'TON', metricton: 'TON',
      liter: 'L', litre: 'L', l: 'L',
      milliliter: 'ML', millilitre: 'ML', ml: 'ML',
      meter: 'M', metre: 'M', m: 'M',
      piece: 'EA', pc: 'EA', each: 'EA', ea: 'EA',
      box: 'BOX', bag: 'BAG', drum: 'DRUM',
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
        showToast(`เลือก Material ${material.materialCode} จาก SAP แล้ว`);
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
        // If the document's unit corresponds by name to a SAP unit (e.g. "Kilogram" -> "KG"), send
        // the order in that unit with factor 1 and let SAP convert to its base unit internally.
        // Only a unit SAP doesn't recognise needs a real pack-size factor to the base unit.
        const docCode = normUom(docUnit);
        const matchedAlt = alts.find((a) => normUom(a.unit) === docCode);
        const matchesBaseByName = baseUnit.length > 0 && normUom(baseUnit) === docCode;
        const alt = alts[0];
        const isDirect = !!matchedAlt || matchesBaseByName;
        const sapUom = matchedAlt ? matchedAlt.unit : matchesBaseByName ? baseUnit : (baseUnit || alt?.unit || '');
        const factor = isDirect
          ? 1
          : (alt && alt.numerator > 0 ? Math.round((alt.denominator / alt.numerator) * 1e6) / 1e6 : 1);
        setMasterEdit({
          tab: 'uoms',
          rowKey: null,
          prefill: {
            MaterialCode: material.materialCode,
            ExtUom: docUnit,
            SapUom: sapUom,
            Factor: factor,
            Note: isDirect
              ? `Document unit "${docUnit}" = SAP unit ${sapUom} (from SAP, document #${doc.docId})`
              : alt
                ? `Converted to base unit ${sapUom} using SAP pack size — please verify Factor (document #${doc.docId})`
                : `SAP did not return a pack size — please check SAP Unit and Factor before saving (document #${doc.docId})`,
          },
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
            && String(m.SalesOrg) === salesOrg,
        );
        if (!existingCm) {
          await createMaster('custmaterials', {
            SalesOrg: salesOrg,
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
  const useZohoMaterial = (i: number, item: ZohoDealItem) =>
    guard(async () => {
      const customerCode = map?.header.customer?.code || doc.partnerCode;
      if (!customerCode) {
        showToast('Please identify the customer first');
        return;
      }
      const matCode = (item.materialCode || '').trim();
      if (!matCode) {
        showToast('รายการนี้ไม่มี Material Code ใน Zoho');
        return;
      }
      const line = doc.lines[i];
      const existingCm = masters.custmaterials.find((m) =>
        m.MaterialCodeSAP === matCode
          && m.CustomerCode === customerCode
          && String(m.SalesOrg) === salesOrg,
      );
      if (!existingCm) {
        await createMaster('custmaterials', {
          SalesOrg: salesOrg,
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
          && (!u.SalesOrg || String(u.SalesOrg) === salesOrg),
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
        showToast(`บันทึก Material ${matCode} จาก Deal แล้ว`);
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
      `พบข้อมูลลูกค้าที่เป็นไปได้ ${p.candidates.length} รายการ กรุณาตรวจสอบ:`,
      '',
      ...p.candidates.map((c, i) => {
        const bits = c.fields.filter((f) => f.value).map((f) => `${f.label}: ${f.value}`).join(' · ');
        return `${i + 1}. ${c.label}${bits ? ' — ' + bits : ''}`;
      }),
      '',
      'พิมพ์บอกได้เลยว่าอยากใช้ตัวไหน (เช่น "ใช้ 1000698") หรือพิมพ์ "ช่วยเทียบให้หน่อย" ให้ผมช่วยวิเคราะห์',
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
      { role: 'assistant', text: `เลือก "${c?.label ?? candidateId}" ให้แล้วครับ${reason ? ' — ' + reason : ''}` },
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
          { role: 'assistant', text: 'ขอโทษครับ วิเคราะห์ไม่สำเร็จ: ' + (e instanceof Error ? e.message : String(e)) },
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
            text: `ผมว่าน่าจะเป็น "${c?.label ?? candidateId}" ครับ — ${reason}\n\nพิมพ์ "ใช่" เพื่อยืนยัน หรือบอกผมว่าจะใช้ตัวไหนแทน`,
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
        : 'ยังไม่สามารถวิเคราะห์ได้ครับ ลองพิมพ์บอกให้ชัดเจนขึ้น เช่น "ใช้ตัวแรก" ได้ไหมครับ';
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
              const key = String(dl.itemNo);
              setZohoMaterialOverrides((prev) => ({ ...prev, [key]: dealItem }));
              const converted = zohoConvertedValues(dl, dealItem, masters?.uoms || [], currentSalesOrg);
              editZohoLine(key, {
                materialId: dealItem.materialId || undefined,
                quantity: String(converted.quantity),
                unitPrice: converted.unitPrice != null ? String(converted.unitPrice) : '',
                unit: converted.unit || '',
                description: dl.desc || '',
              });
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
  const totalsFields =
    doc.module === 'AP' ? (
      <FieldGrid fields={AP_TOTALS_H} values={h} posted={posted} numeric onEdit={editHeader} />
    ) : doc.module === 'SO' ? (
      <>
        <FieldGrid fields={SO_TOTALS_H} values={h} posted={posted} numeric onEdit={editHeader} />
        <FieldGrid fields={SO_REMARK_H} values={h} posted={posted} onEdit={editHeader} />
      </>
    ) : doc.module === 'PODP' ? (
      <FieldGrid fields={PODP_TOTALS_H} values={h} posted={posted} numeric onEdit={editHeader} />
    ) : null;

  const sb = statusBadge(doc.status);
  const providers = ocrProviders ?? [];

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
  };
  const glProps = {
    module: doc.module,
    items: glItems,
    posted,
    onEdit: editGlItem,
    onAdd: addGlItem,
    onDelete: delGlItem,
  };
  const taxProps = {
    items: h.taxItems || [],
    posted,
    onEdit: editTaxItem,
    onAdd: addTaxItem,
    onDelete: delTaxItem,
  };
  const whtProps = {
    items: h.whtItems || [],
    posted,
    onEdit: editWhtItem,
    onAdd: addWhtItem,
    onDelete: delWhtItem,
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
                <i className="fa-solid fa-gear" /> ไปที่ข้อมูล Mapping
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
            <i className="fa-solid fa-eye" /> ดูเอกสาร
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
          posted={posted}
          onManualHeader={setManualHeader}
          onManualLine={setManualLine}
          onQuickAddVendor={quickAddVendor}
          onQuickAddCustomer={quickAddCustomer}
          onQuickAddShipTo={quickAddShipTo}
          onQuickAddMaterial={quickAddMaterial}
          onUseSapMaterial={useSapMaterial}
          onAddUomRule={addUomRule}
          onUseSapCustomer={useSapCustomer}
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
          onUseZohoMaterial={useZohoMaterial}
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
          </div>
          <div className="card-b">{totalsFields}</div>
        </div>
      )}

      {/* MIRO trade groups (AP) */}
      {doc.module === 'AP' && (
        <div className="card">
          <div className="card-h">
            <h2>Supplier Invoice (MIRO)</h2>
          </div>
          <TabbedGroups
            groups={AP_TRADE_GROUPS}
            values={h}
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

      {/* MGT/Zoho — Step 3: the Sales Order lifted whole onto the page as one editable table,
          placed here at the send step (it replaces the per-line Material cards and the old confirm
          popup). Shown once mapping has run (so the Customer/Deal cards exist); deliberately NOT
          gated on map.pass, because that reflects the SAP material-master check an MGT document
          doesn't use -- Zoho material matching happens in this editor. The person edits inline and
          sends from the button inside it. */}
      {isMgt && showZohoEditor && map && !isSplit && (
        <div ref={zohoEditorRef}>
        <ZohoSalesOrderEditor
          preview={zohoMaterialPreviewMerged}
          loading={zohoMaterialLoading}
          error={zohoMaterialError}
          selectedDealId={selectedDealId}
          docLines={doc.lines}
          resolvedDeal={resolvedDeal}
          providers={providers}
          header={zohoHeader}
          onHeaderChange={(patch) => setZohoHeader((p) => ({ ...p, ...patch }))}
          lineEdits={zohoLineEdits}
          onLineChange={editZohoLine}
          onUseAiMatch={useAiMaterialMatch}
          sending={zohoPosting}
          result={zohoResult}
          onSend={confirmPostZoho}
          onViewPayload={viewZohoPayload}
          posted={posted}
        />
        </div>
      )}

      {/* SAP Step 3 (SO module only) -- the read-only counterpart of the Zoho editor above, so
          both "check before you send" screens share the same card layout. AP/II keep the old
          postOpen popup below (Zoho has no AP/II equivalent to match against). */}
      {!isMgt && doc.module === 'SO' && showSapEditor && map && !isSplit && (
        <div ref={sapEditorRef}>
          <SapSalesOrderEditor
            doc={doc}
            map={map}
            header={h}
            salesOrg={salesOrg}
            sending={posting}
            posted={posted}
            onSend={confirmPost}
            onViewPayload={openPayload}
          />
        </div>
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
              onClick={() => {
                setShowZohoEditor(true);
                setTimeout(() => zohoEditorRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 60);
              }}
              disabled={!(map && !posted && !isSplit)}
            >
              ⎋ ขั้นตอน 3 · ตรวจ Sales Order
            </button>
          )}
          {/* For MGT/Zoho and SAP/SO the Send button lives inside the review card above (next to
              the data it sends); only AP/II keep the old popup-based Send here (Zoho has no AP/II
              equivalent to match against). */}
          {!isMgt && doc.module === 'SO' && (
            <button
              className="btn success"
              onClick={() => {
                setShowSapEditor(true);
                setTimeout(() => sapEditorRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 60);
              }}
              disabled={!(map && map.pass && !posted && !isSplit)}
            >
              ⎋ ขั้นตอน 3 · ตรวจและส่ง SAP
            </button>
          )}
          {!isMgt && doc.module !== 'SO' && (
            <button
              className="btn success"
              onClick={() => setPostOpen(true)}
              disabled={!(map && map.pass && !posted && !isSplit)}
            >
              ⎋ ขั้นตอน 3 · ตรวจและส่ง SAP
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
                      ? showZohoEditor
                        ? 'Review the Sales Order above and press Send to Zoho CRM'
                        : 'Press Step 3 to build the Sales Order'
                      : 'Match the customer and pick a Deal, then press Step 3'
                    : 'Click Mapping first (Step 2), then pick a Deal and press Step 3'
                  : map
                    ? map.pass
                      ? doc.module === 'SO'
                        ? showSapEditor
                          ? 'Review the Sales Order above and press Send to SAP'
                          : 'Press Step 3 to review the Sales Order'
                        : 'Ready to send to SAP'
                      : 'Fix the items that failed before sending'
                    : 'Click Mapping to validate against Master Data'}
          </span>
        </div>
      </div>

      {/* ---- Modals ---- */}
      <Modal open={reviewOpen} onClose={() => setReviewOpen(false)} wide>
        <ModalHeader title={`<i className="fa-solid fa-eye" /> ดูเอกสาร — ${doc.fileName}`} onClose={() => setReviewOpen(false)} />
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
                  {posting ? 'Sending…' : 'ยืนยันส่งไป SAP'}
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
