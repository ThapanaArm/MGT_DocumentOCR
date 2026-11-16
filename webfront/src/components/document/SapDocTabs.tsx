import { useState } from 'react';
import { getPayload } from '../../api/documents';
import type { DocLine } from '../../api/documents';
import { fmtAmt } from '../../utils/format';

/* MGT (AP, 2026-11-16): what each SAP document of this bundle will carry, one TAB per document —
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
}

type Payload = Record<string, any>;
const listOf = (v: unknown): Payload[] =>
  Array.isArray(v) ? (v as Payload[]) : Array.isArray((v as any)?.results) ? (v as any).results : [];

const COND: Record<string, string> = {
  ZFRE: 'Freight', ZLH1: 'Local Handling', ZINS: 'Insurance', ZFE2: 'Fee', ZIM1: 'Import Tax', ZOVS: 'Oversea',
};

/** The documents as the screen sees them: cost lines grouped by supplier and supplier invoice. */
export function screenTabs(args: {
  lines: DocLine[];
  mainVendor: string;
  po: string;
  invoiceNo: string;
  text: string;
}): SapDocTab[] {
  const { lines, mainVendor, po, invoiceNo, text } = args;
  const groups = new Map<string, { vendor: string; inv: string; costs: string[]; amount: number }>();
  const firstInvOf = new Map<string, string>();
  for (const l of lines) {
    const ext = String(l.extCode ?? '').trim().toUpperCase();
    if (['VAT', 'WHT'].includes(ext) || /^\s*(VAT|WITHHOLDING)/i.test(String(l.desc ?? ''))) continue;
    const amount = Number(l.amount) || 0;
    if (!amount) continue;
    const x = (l.extra ?? {}) as Record<string, any>;
    const vendor = String(x.vendorCode ?? '').trim() || mainVendor;
    let inv = String(x.invoiceNo ?? '').trim();
    if (inv && !firstInvOf.has(vendor)) firstInvOf.set(vendor, inv);
    if (!inv) inv = firstInvOf.get(vendor) ?? '';
    const key = `${vendor}|${inv.toUpperCase()}`;
    const g = groups.get(key) ?? { vendor, inv, costs: [], amount: 0 };
    g.costs.push(String(l.desc ?? '').trim());
    g.amount = Math.round((g.amount + amount) * 100) / 100;
    groups.set(key, g);
  }
  return [...groups.values()].map((g, i) => {
    const insurance = g.costs.length > 0 && g.costs.every((c) => /INSURANCE|ประกัน/i.test(c));
    // The supplier invoice the lines name (LOG101185LCH) — not the receipt / tax invoice number
    // (RE2512206) the header carries; that one is only shown when no line names an invoice.
    const reference = insurance ? po : g.inv || invoiceNo || po;
    return {
      key: `s${i}`,
      vendor: g.vendor,
      costs: g.costs,
      amount: g.amount,
      reference,
      vendorAssignment: insurance ? po : reference,
      lineAssignment: po,
      text,
      note: insurance ? 'ใบประกัน: Reference / Assignment = เลข PO'
        : !g.inv ? 'ยังไม่มีเลขใบแจ้งหนี้ของรายการ (คอลัมน์ Supplier Inv. No. ในตาราง DETAIL) — Reference นี้เป็นเลขของเอกสาร' : undefined,
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

export default function SapDocTabs({ docId, fromScreen }: { docId: number; fromScreen: SapDocTab[] }) {
  const [fromSap, setFromSap] = useState<SapDocTab[] | null>(null);
  const [errors, setErrors] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState('');
  const [active, setActive] = useState(0);
  const tabs = fromSap ?? fromScreen;
  const cur = tabs[Math.min(active, Math.max(tabs.length - 1, 0))];

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
                onClick={() => setActive(i)}
                style={{
                  borderRadius: '8px 8px 0 0',
                  borderBottom: i === active ? '2px solid var(--brand)' : '2px solid transparent',
                  fontWeight: i === active ? 600 : 400,
                }}
                title={t.costs.join(', ')}
              >
                {i + 1}. {t.vendor || '—'} · {t.costs.join(' + ') || '—'}
              </button>
            ))}
          </div>
          {cur && (
            <div className="grid" style={{ marginTop: 8 }}>
              {box('ผู้ขาย', cur.vendor)}
              {box('ยอด', `${fmtAmt(cur.amount)}${cur.currency ? ' ' + cur.currency : ''}`)}
              {box('Reference → SAP', cur.reference)}
              {box('Assignment — บรรทัดผู้ขาย', cur.vendorAssignment)}
              {box('Assignment — บรรทัดค่าใช้จ่าย', cur.lineAssignment)}
              {box('Text — ทุกบรรทัด', cur.text)}
            </div>
          )}
          {cur?.note && <div className="hint" style={{ marginTop: 4 }}>{cur.note}</div>}
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
