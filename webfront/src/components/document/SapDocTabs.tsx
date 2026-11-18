import { useState } from 'react';
import { getPayload } from '../../api/documents';
import type { DocLine } from '../../api/documents';
import { fmtAmt } from '../../utils/format';

/* GLC copy of MGT's SapDocTabs (AP, 2026-11-18). MGT (AP, 2026-11-16): what each SAP document of this bundle will carry, one TAB per document —
   a shipping bundle becomes one document per supplier invoice (PRD 5100000595 FLS Freight
   LOG101185LCH, 5100000596 FLS Local Handling LOG101202LCH, 5100000597 Felix Insurance with the PO
   as Reference). Built from the screen straight away; "ตรวจกับ SAP" swaps in the exact documents
   the send would make (amounts at the PO estimate, the TEST "-T" suffix), read from the payload. */

export interface SapDocTab {
  key: string;
  vendor: string;
  costs: string[];
  amount: number | string;
  currency?: string;
  reference: string;
  vendorAssignment: string;
  lineAssignment: string;
  text: string;
  note?: string;
  /** Positions in doc.lines of the rows this document is made of (screen tabs only). */
  lineIdx?: number[];
  /** The Reference was typed by hand (extra.sapRef). */
  manualRef?: boolean;
}

type Payload = Record<string, any>;
const listOf = (v: unknown): Payload[] =>
  Array.isArray(v) ? (v as Payload[]) : Array.isArray((v as any)?.results) ? (v as any).results : [];

const COND: Record<string, string> = {
  ZFRE: 'Freight', ZLH1: 'Local Handling', ZINS: 'Insurance', ZFE2: 'Fee', ZIM1: 'Import Tax', ZOVS: 'Oversea',
};

/** The documents as the screen sees them — GLC (AP, 2026-11-18): ONE SAP document per supplier
 *  (goods keyed against the PO: one document altogether). Reference = what was typed in the tab
 *  (extra.sapRef on the supplier's rows), else that supplier's own tax invoice number on the bundle,
 *  else the document's invoice number, else the PO — as the backend builds it. */
export function screenTabs(args: {
  lines: DocLine[];
  mainVendor: string;
  po: string;
  invoiceNo: string;
  text: string;
  /** Goods keyed against the PO: every row is one document of the main supplier. */
  singleDoc?: boolean;
  /** The Reference a goods document carries (the supplier's invoice, else the PO). */
  goodsReference?: string;
}): SapDocTab[] {
  const { lines, mainVendor, po, invoiceNo, text, singleDoc, goodsReference } = args;
  const groups = new Map<string, { vendor: string; manual: string; costs: string[]; amount: number; idx: number[] }>();
  const taxNoOf = new Map<string, string>();
  lines.forEach((l, li) => {
    const ext = String(l.extCode ?? '').trim().toUpperCase();
    const x = (l.extra ?? {}) as Record<string, any>;
    const vendor = singleDoc ? mainVendor : String(x.vendorCode ?? '').trim() || mainVendor;
    if (['VAT', 'WHT'].includes(ext) || /^\s*(VAT|WITHHOLDING)/i.test(String(l.desc ?? ''))) {
      const no = String(x.taxDocNo ?? '').trim();
      if (ext === 'VAT' && no && !taxNoOf.has(vendor)) taxNoOf.set(vendor, no);
      return;
    }
    const amount = Number(l.amount) || 0;
    if (!amount) return;
    const g = groups.get(vendor) ?? { vendor, manual: '', costs: [], amount: 0, idx: [] };
    g.costs.push(String(l.desc ?? '').trim());
    g.amount = Math.round((g.amount + amount) * 100) / 100;
    g.idx.push(li);
    if (!g.manual) g.manual = String(x.sapRef ?? '').trim();
    groups.set(vendor, g);
  });
  return [...groups.values()].map((g, i) => {
    const auto = singleDoc ? (goodsReference || invoiceNo || po) : (taxNoOf.get(g.vendor) || invoiceNo || po);
    const reference = g.manual || auto;
    return {
      key: `s${i}`,
      vendor: g.vendor,
      costs: singleDoc ? [`${g.costs.length} รายการ`] : g.costs,
      amount: g.amount,
      reference,
      vendorAssignment: singleDoc ? po : reference,
      lineAssignment: po,
      text,
      lineIdx: g.idx,
      manualRef: !!g.manual,
      note: g.manual ? 'Reference แก้เอง — ลบให้ว่างเพื่อกลับไปใช้ค่าที่ระบบหาให้' : undefined,
    };
  });
}

function payloadTabs(docs: Payload[]): SapDocTab[] {
  return docs.map((p, i) => {
    const po = listOf(p.to_SuplrInvcItemPurOrdRef);
    const gl = listOf(p.to_SupplierInvoiceItemGLAcct);
    const conds = [...new Set(po.map((x) => String(x.SuplrInvcDeliveryCostCndnType ?? '').trim()).filter(Boolean))];
    const lineAsg = String(po[0]?.AssignmentReference ?? gl[0]?.AssignmentReference ?? p._lineAssignment ?? '');
    const estimates = po.map((x) => String(x._mgtEstimate ?? '')).filter(Boolean);
    return {
      key: `p${i}`,
      vendor: String(p.InvoicingParty ?? ''),
      costs: conds.length ? conds.map((c) => COND[c] ?? c) : po.length ? ['Goods'] : [],
      amount: p.InvoiceGrossAmount,
      currency: String(p.DocumentCurrency ?? ''),
      reference: String(p.SupplierInvoiceIDByInvcgParty ?? ''),
      vendorAssignment: String(p.AssignmentReference ?? ''),
      lineAssignment: lineAsg,
      text: String(p.SupplierPostingLineItemText ?? p.DocumentHeaderText ?? ''),
      note: estimates.length ? `ยอดตามประมาณการใน PO: ${estimates.join(', ')}` : undefined,
    };
  });
}

export default function SapDocTabs({ docId, fromScreen, onReference }: {
  docId: number;
  fromScreen: SapDocTab[];
  /** Writes a typed Reference onto the rows of one document (extra.sapRef); '' clears it. */
  onReference?: (lineIdx: number[], value: string) => void;
}) {
  const [fromSap, setFromSap] = useState<SapDocTab[] | null>(null);
  const [errors, setErrors] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState('');
  const [active, setActive] = useState(0);
  // What the Reference box shows while it is being typed in: emptying it clears the hand-typed
  // Reference, and without this the box would jump straight back to the derived one mid-edit.
  const [refDraft, setRefDraft] = useState<string | null>(null);
  const tabs = fromSap ?? fromScreen;
  const cur = tabs[Math.min(active, Math.max(tabs.length - 1, 0))];
  // Two documents of one supplier with the same Reference: SAP refuses the second (M8/804), and
  // it means the rows have no supplier invoice number yet (AP, 2026-11-16).
  // The TEST tag is dropped but its part letter kept: "-T843" and "-T843B" are two References.
  const refKey = (t: SapDocTab) => `${t.vendor}|${t.reference.replace(/-T\d+(?=[A-Z]?$)/, '').toUpperCase()}`;
  const dupKeys = new Set(tabs.map(refKey).filter((k, i, a) => a.indexOf(k) !== i));

  const check = async () => {
    setLoading(true);
    setFailed('');
    try {
      const r = (await getPayload(docId)) as { payload: Payload | Payload[]; errors?: unknown };
      const docs = Array.isArray(r.payload) ? r.payload : r.payload ? [r.payload] : [];
      setFromSap(payloadTabs(docs));
      setErrors(Array.isArray(r.errors) ? (r.errors as unknown[]).map(String) : []);
      setActive(0);
    } catch (e) {
      setFailed(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  };

  // The screen tab a shown tab stands for: itself, or — after "ตรวจกับ SAP" — the screen tab at the
  // same position when the two lists line up (same count). The supplier is not compared: the
  // payload carries the TEST code the vendor map sends (4000021) and the screen the document's own
  // (4000675), so the two never matched and the box stayed read-only (#843). Both lists follow the
  // order of the rows; a list SAP split further (one document per tax code) does not line up and
  // the edit is made on the screen's tabs instead.
  const screenFor = (i: number): SapDocTab | undefined => {
    if (!fromSap) return fromScreen[i];
    return fromScreen.length === fromSap.length ? fromScreen[i] : undefined;
  };
  const curIdx = Math.min(active, Math.max(tabs.length - 1, 0));
  const editable = !!onReference && !!screenFor(curIdx)?.lineIdx?.length;
  const setReference = (value: string) => {
    const target = screenFor(curIdx);
    if (!onReference || !target?.lineIdx?.length) return;
    onReference(target.lineIdx, value);
    // Back to the screen's values, which now carry the edit; "ตรวจกับ SAP" shows SAP's again.
    if (fromSap) setFromSap(null);
  };
  // TEST adds "-T{docId}" when it sends; the box edits the Reference itself.
  const refShown = (t: SapDocTab) => (fromSap ? t.reference.replace(/-T\d+[A-Z]?$/, '') : t.reference);

  const box = (label: string, value: string) => (
    <div className="f" key={label}>
      <label>{label}</label>
      <input type="text" value={value} readOnly style={{ background: 'var(--bg-subtle, #f5f5f5)' }} />
    </div>
  );

  return (
    <div style={{ marginTop: 12 }}>
      <div className="row" style={{ alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <span className="hint">
          เอกสารที่จะสร้างใน SAP ({tabs.length} ใบ){fromSap ? ' — จาก SAP' : ' — จากหน้าจอ'}
        </span>
        <button className="btn sm" onClick={check} disabled={loading} type="button">
          <i className="fa-solid fa-magnifying-glass" /> {loading ? 'กำลังตรวจ…' : 'ตรวจกับ SAP'}
        </button>
        {fromSap && (
          <button className="btn sm" type="button" onClick={() => { setFromSap(null); setActive(0); }}>
            กลับไปค่าจากหน้าจอ
          </button>
        )}
      </div>
      {failed && <div className="hint" style={{ color: 'var(--danger, #c00000)' }}>ตรวจไม่ได้: {failed}</div>}
      {tabs.length > 0 && (
        <>
          <div className="row" style={{ gap: 4, marginTop: 8, borderBottom: '1px solid var(--line, #e5e5e5)', flexWrap: 'wrap' }}>
            {tabs.map((t, i) => (
              <button
                key={t.key}
                type="button"
                className="btn sm"
                onClick={() => { setActive(i); setRefDraft(null); }}
                style={{
                  borderRadius: '8px 8px 0 0',
                  borderBottom: i === active ? '2px solid var(--brand)' : '2px solid transparent',
                  fontWeight: i === active ? 600 : 400,
                }}
                title={t.costs.join(', ')}
              >
                {i + 1}. {t.vendor || '—'} · {t.costs.join(' + ') || '—'}{dupKeys.has(refKey(t)) ? ' ⚠' : ''}
              </button>
            ))}
          </div>
          {cur && (
            <div className="grid" style={{ marginTop: 8 }}>
              {box('ผู้ขาย', cur.vendor)}
              {box('ยอด', `${fmtAmt(cur.amount)}${cur.currency ? ' ' + cur.currency : ''}`)}
              {editable ? (
                <div className="f" key="ref">
                  <label>Reference → SAP (แก้ได้)</label>
                  <input
                    type="text"
                    maxLength={16}
                    value={refDraft ?? refShown(cur)}
                    onFocus={() => setRefDraft(refShown(cur))}
                    onBlur={() => setRefDraft(null)}
                    onChange={(e) => {
                      setRefDraft(e.target.value);
                      setReference(e.target.value.trim());
                    }}
                    style={cur.manualRef ? { borderColor: 'var(--warn, #c08000)' } : undefined}
                  />
                </div>
              ) : (
                <div className="f" key="ref">
                  <label>Reference → SAP</label>
                  <input type="text" value={cur.reference} readOnly style={{ background: 'var(--bg-subtle, #f5f5f5)' }} />
                  {fromSap && onReference && (
                    <span className="hint">แก้ได้ที่ค่าจากหน้าจอ — กด "กลับไปค่าจากหน้าจอ"</span>
                  )}
                </div>
              )}
              {box('Assignment — บรรทัดผู้ขาย', cur.vendorAssignment)}
              {box('Assignment — บรรทัดค่าใช้จ่าย', cur.lineAssignment)}
              {box('Text — ทุกบรรทัด', cur.text)}
            </div>
          )}
          {cur?.note && <div className="hint" style={{ marginTop: 4 }}>{cur.note}</div>}
          {cur && dupKeys.has(refKey(cur)) && (
            <div className="hint" style={{ marginTop: 4, color: 'var(--danger, #c00000)' }}>
              Reference ซ้ำกับอีกใบของผู้ขายเดียวกัน — กรอกเลขใบแจ้งหนี้ของแต่ละแถวในคอลัมน์ Supplier Inv. No. (ตาราง DETAIL)
              เช่น Freight = LOG101185LCH, Local Handling = LOG101202LCH แล้วกด Save และตรวจอีกครั้ง
            </div>
          )}
        </>
      )}
      {fromSap && errors.length > 0 && (
        <ul className="hint" style={{ margin: '6px 0 0', paddingLeft: 18, color: 'var(--danger, #c00000)' }}>
          {errors.map((e) => <li key={e}>{e}</li>)}
        </ul>
      )}
    </div>
  );
}
