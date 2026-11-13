import { useState } from 'react';
import { getPayload } from '../../api/documents';
import { fmtAmt } from '../../utils/format';

/* "What goes to SAP" on the Basic Data tab: one row per supplier invoice the document becomes.
   A shipping bundle splits into one SAP document per supplier (PILOT's Local, the Customs
   Department's Fee / Import Tax, Bangkok Insurance, DSM's Overseas per PO), and each carries its
   own Reference — the agent's invoice, the customs receipt's เลขที่ชำระอากร, the insurer's invoice,
   the PO. The single Reference box above only ever shows the document's own, so this lists them
   all to be checked before sending. Built from the same preview the "View Payload" button reads,
   so it is exactly what Step 3 would send. */

const COND_LABEL: Record<string, string> = {
  ZLH1: 'Local Handling',
  ZFE2: 'Fee',
  ZINS: 'Insurance',
  ZIM1: 'Import Tax',
  ZOVS: 'Oversea Charge',
  ZFRE: 'Freight',
  ZED1: 'Excise Duty',
  ZOTH: 'Other Charges',
};

type Payload = Record<string, any>;

const listOf = (v: unknown): Payload[] =>
  Array.isArray(v) ? (v as Payload[]) : Array.isArray((v as any)?.results) ? (v as any).results : [];

export default function SapDocsPreview({ docId }: { docId: number }) {
  const [docs, setDocs] = useState<Payload[] | null>(null);
  const [errors, setErrors] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState('');

  const load = async () => {
    setLoading(true);
    setFailed('');
    try {
      const r = (await getPayload(docId)) as { payload: Payload | Payload[]; errors?: unknown };
      setDocs(Array.isArray(r.payload) ? r.payload : r.payload ? [r.payload] : []);
      setErrors(Array.isArray(r.errors) ? (r.errors as unknown[]).map(String) : []);
    } catch (e) {
      setDocs(null);
      setFailed(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  };

  const rows = (docs ?? []).map((p) => {
    const po = listOf(p.to_SuplrInvcItemPurOrdRef);
    const gl = listOf(p.to_SupplierInvoiceItemGLAcct);
    const conds = [...new Set(po.map((x) => String(x.SuplrInvcDeliveryCostCndnType ?? '').trim()).filter(Boolean))];
    const kinds = conds.length
      ? conds.map((c) => COND_LABEL[c] ? `${c} · ${COND_LABEL[c]}` : c)
      : po.length ? ['Goods'] : [];
    if (gl.some((g) => ['11720000', '11730000'].includes(String(g.GLAccount ?? '').trim()))) kinds.push('VAT');
    const pos = [...new Set(po.map((x) => String(x.PurchaseOrder ?? '').trim()).filter(Boolean))];
    return {
      party: String(p.InvoicingParty ?? ''),
      reference: String(p.SupplierInvoiceIDByInvcgParty ?? ''),
      kinds,
      pos,
      amount: p.InvoiceGrossAmount,
      currency: String(p.DocumentCurrency ?? ''),
      text: String(p.SupplierPostingLineItemText ?? p.DocumentHeaderText ?? ''),
      assignment: String(p.AssignmentReference ?? ''),
    };
  });
  // The same Reference on two documents of the same supplier is refused by SAP (M8/804).
  const dupRefs = new Set(
    rows.map((r) => `${r.party}|${r.reference}`).filter((k, i, a) => a.indexOf(k) !== i),
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 14 }}>
      <div className="row" style={{ justifyContent: 'space-between', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <b>เอกสารที่จะสร้างใน SAP{docs ? ` (${rows.length})` : ''}</b>
        <button className="btn sm" onClick={load} disabled={loading}>
          <i className="fa-solid fa-magnifying-glass" /> {loading ? 'กำลังตรวจ…' : docs ? 'ตรวจอีกครั้ง' : 'ตรวจ Reference แต่ละใบ'}
        </button>
      </div>
      {!docs && !failed && (
        <span className="hint">
          เอกสารชุดนี้อาจแยกเป็นหลายใบตามผู้ขาย (Local / Fee / Insurance / Overseas) แต่ละใบมี Reference ของตัวเอง
          — กดตรวจเพื่อดูก่อนส่ง (อ่าน PO จาก SAP เหมือนตอนส่งจริง)
        </span>
      )}
      {failed && <span className="hint" style={{ color: 'var(--danger, #c00000)' }}>ตรวจไม่ได้: {failed}</span>}
      {docs && (
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th style={{ width: 36 }}>#</th>
                <th>ผู้ขาย</th>
                <th>Reference</th>
                <th>รายการ</th>
                <th>PO</th>
                <th style={{ textAlign: 'right' }}>ยอด</th>
                <th>Text</th>
                <th>Assignment</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={i}>
                  <td>{i + 1}</td>
                  <td>{r.party}</td>
                  <td>
                    <b>{r.reference || <span className="badge b-fail">ว่าง</span>}</b>
                    {dupRefs.has(`${r.party}|${r.reference}`) && (
                      <span className="badge b-fail" style={{ marginLeft: 6 }} title="ผู้ขายเดียวกัน Reference ซ้ำ SAP จะไม่รับ (M8/804)">ซ้ำ</span>
                    )}
                  </td>
                  <td>{r.kinds.join(', ') || <span className="hint">—</span>}</td>
                  <td>{r.pos.join(', ') || <span className="hint">—</span>}</td>
                  <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>{fmtAmt(r.amount)} {r.currency}</td>
                  <td style={{ maxWidth: 280, overflow: 'hidden', textOverflow: 'ellipsis' }} title={r.text}>{r.text}</td>
                  <td>{r.assignment}</td>
                </tr>
              ))}
              {rows.length === 0 && (
                <tr><td colSpan={8} className="hint" style={{ textAlign: 'center' }}>ไม่มีเอกสารที่จะส่ง</td></tr>
              )}
            </tbody>
          </table>
        </div>
      )}
      {docs && errors.length > 0 && (
        <ul className="hint" style={{ margin: 0, paddingLeft: 18, color: 'var(--danger, #c00000)' }}>
          {errors.map((e) => <li key={e}>{e}</li>)}
        </ul>
      )}
    </div>
  );
}
