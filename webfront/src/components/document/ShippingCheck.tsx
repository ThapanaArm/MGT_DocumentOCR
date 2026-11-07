import type { DocModel } from '../../api/documents';
import { fmt, num } from '../../utils/format';

/* Shipping cost check — FORM SHIPPING EXPENSE vs the documents behind it.

   The cost lines of a shipping bundle are read off the FORM SHIPPING EXPENSE sheet, each filed
   under a COSTS heading (Local / Insurance / Fee / Overseas, in line.extra.costType). The reader
   also lists every supporting invoice / receipt in the bundle (header.sourceDocs) with its own
   totals and the heading it belongs to. Both are added up per heading here:

     1. Form      — what the form says the shipment cost, the figure the invoices are built from
     2. Documents — what the invoices and receipts themselves add up to (before VAT, since the form
                    carries costs without VAT)

   A heading where the two do not agree shows its DIFF in red: a cost the form left out, a form
   figure no document stands behind, or a document read under the wrong heading — all of which are
   cheaper to catch here than after the documents are in SAP. A receipt the agent paid on our behalf
   and bills again on its own invoice (includedIn) is listed but not counted twice. */

export const COST_TYPES = ['Local', 'Insurance', 'Fee', 'Overseas'] as const;

export interface SourceDoc {
  docType?: string;
  issuerName?: string;
  docNo?: string;
  docDate?: string;
  costType?: string;
  currency?: string;
  nonVatAmount?: number | string;
  amountBeforeVat?: number | string;
  vatAmount?: number | string;
  totalAmount?: number | string;
  includedIn?: string;
  shipper?: string;
  blNo?: string;
  shipperInvoiceNo?: string;
}

/* "KCC SILICONE CORPORATION" vs the form's SUPPLIER "KCC SILICONE CORP." — same party when the
   first distinctive word matches. Empty on either side = cannot tell, so not flagged. */
const firstWord = (v: unknown) => (String(v ?? '').toUpperCase().match(/[A-Z]{3,}|[\u0E00-\u0E7F]{3,}/) ?? [''])[0];
const otherShipment = (d: SourceDoc, goodsSupplier: string) => {
  const a = firstWord(d.shipper);
  const b = firstWord(goodsSupplier);
  return a !== '' && b !== '' && a !== b;
};

const norm = (v: unknown): string => {
  const s = String(v ?? '').trim().toLowerCase();
  if (s.startsWith('loc')) return 'Local';
  if (s.startsWith('ins')) return 'Insurance';
  if (s.startsWith('fee')) return 'Fee';
  if (s.startsWith('over')) return 'Overseas';
  return '';
};

const TAX_ROW = /^(VAT|WHT|DUTY)$/i;

/* Before VAT of one document = มูลค่าไม่มีหรือยกเว้นภาษี + มูลค่าที่คำนวณภาษี 7% (the whole figure
   before VAT); else total − VAT. Total = Before VAT + VAT. */
const beforeVat = (d: SourceDoc) => {
  const b = num(d.amountBeforeVat);
  if (b) return b;
  return Math.round((num(d.totalAmount) - num(d.vatAmount)) * 100) / 100;
};

export function shippingCheckRows(doc: DocModel) {
  const form: Record<string, number> = {};
  for (const l of doc.lines || []) {
    if (TAX_ROW.test(String(l.extCode ?? '').trim())) continue;
    const t = norm((l.extra as Record<string, unknown> | undefined)?.costType);
    if (!t) continue;
    form[t] = (form[t] ?? 0) + num(l.amount);
  }
  const docs: SourceDoc[] = Array.isArray(doc.header.sourceDocs) ? doc.header.sourceDocs : [];
  const counted: Record<string, { nonVat: number; before: number; vat: number; total: number; n: number }> = {};
  for (const d of docs) {
    if (String(d.includedIn ?? '').trim()) continue;
    const t = norm(d.costType);
    if (!t) continue;
    const c = (counted[t] ??= { nonVat: 0, before: 0, vat: 0, total: 0, n: 0 });
    c.nonVat += num(d.nonVatAmount);
    c.before += beforeVat(d);
    c.vat += num(d.vatAmount);
    c.total += num(d.totalAmount);
    c.n += 1;
  }
  return COST_TYPES
    .filter((t) => form[t] != null || counted[t] != null)
    .map((t) => {
      const f = Math.round((form[t] ?? 0) * 100) / 100;
      const c = counted[t] ?? { nonVat: 0, before: 0, vat: 0, total: 0, n: 0 };
      const before = Math.round(c.before * 100) / 100;
      return { type: t, form: f, docsNonVat: Math.round(c.nonVat * 100) / 100, docsBefore: before, docsVat: c.vat, docsTotal: c.total, docCount: c.n,
        diff: Math.round((f - before) * 100) / 100 };
    });
}

export default function ShippingCheck({ doc, onDocsChange, posted }: {
  doc: DocModel;
  onDocsChange?: (docs: SourceDoc[]) => void;
  posted?: boolean;
}) {
  const docs: SourceDoc[] = Array.isArray(doc.header.sourceDocs) ? doc.header.sourceDocs : [];
  const rows = shippingCheckRows(doc);
  const hasForm = (doc.lines || []).some((l) => norm((l.extra as Record<string, unknown> | undefined)?.costType));
  if (!hasForm && docs.length === 0) return null;

  const red = { color: 'var(--danger, #e5484d)', fontWeight: 700 } as const;
  const ok = { color: 'var(--ok, #1f9d55)', fontWeight: 600 } as const;
  const anyDiff = rows.some((r) => Math.abs(r.diff) >= 0.01);
  const setDoc = (i: number, k: keyof SourceDoc, v: string) =>
    onDocsChange?.(docs.map((d, j) => (j === i ? { ...d, [k]: v } : d)));

  return (
    <div className="card">
      <div className="card-h">
        <h2>Shipping Check — Form vs Documents</h2>
        <div className="sp" />
        {docs.length > 0 && (
          anyDiff
            ? <span style={red}><i className="fa-solid fa-triangle-exclamation" /> ยอดไม่ตรง</span>
            : <span style={ok}><i className="fa-solid fa-circle-check" /> ยอดตรงกันทุกหมวด</span>
        )}
      </div>
      <div className="card-b">
        {docs.length === 0 && (
          <p className="hint" style={{ marginTop: 0 }}>
            ยังไม่มีรายการเอกสารประกอบ (sourceDocs) ของชุดนี้ — กด Re-read Document เพื่อให้ระบบอ่านยอดของแต่ละใบมาเทียบกับฟอร์ม
          </p>
        )}
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th>COSTS</th>
                <th style={{ textAlign: 'right' }}>1. Form (FORM SHIPPING EXPENSE)</th>
                <th style={{ textAlign: 'right' }}>Non VAT</th>
                <th style={{ textAlign: 'right' }}>2. Documents (ก่อน VAT)</th>
                <th style={{ textAlign: 'right' }}>VAT</th>
                <th style={{ textAlign: 'right' }}>Documents Total</th>
                <th style={{ textAlign: 'right' }}>DIFF (1 − 2)</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => {
                const bad = Math.abs(r.diff) >= 0.01;
                return (
                  <tr key={r.type}>
                    <td><b>{r.type}</b>{r.docCount > 0 && <span className="hint"> · {r.docCount} ใบ</span>}</td>
                    <td style={{ textAlign: 'right' }}>{fmt(r.form)}</td>
                    <td style={{ textAlign: 'right' }}>{fmt(r.docsNonVat)}</td>
                    <td style={{ textAlign: 'right' }}>{fmt(r.docsBefore)}</td>
                    <td style={{ textAlign: 'right' }}>{fmt(r.docsVat)}</td>
                    <td style={{ textAlign: 'right' }}>{fmt(r.docsTotal)}</td>
                    <td style={{ textAlign: 'right', ...(bad ? red : ok) }}>
                      {bad ? fmt(r.diff) : '0.00 ✓'}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        {docs.length > 0 && (
          <>
            <p className="sec-title" style={{ marginTop: 16 }}>เอกสารประกอบในชุด ({docs.length})</p>
            <div className="tw">
              <table>
                <thead>
                  <tr>
                    <th>Issuer</th>
                    <th>Doc No.</th>
                    <th>Type</th>
                    <th>Date</th>
                    <th>COSTS</th>
                    <th style={{ textAlign: 'right' }}>Non VAT</th>
                    <th style={{ textAlign: 'right' }}>ก่อน VAT</th>
                    <th style={{ textAlign: 'right' }}>VAT</th>
                    <th style={{ textAlign: 'right' }}>Total</th>
                    <th>รวมอยู่ในใบ (ไม่นับซ้ำ)</th>
                    <th>Shipper</th>
                    <th>B/L</th>
                    <th>Shipper INV.</th>
                  </tr>
                </thead>
                <tbody>
                  {docs.map((d, i) => (
                    <tr key={i} style={String(d.includedIn ?? '').trim() ? { opacity: 0.6 } : undefined}>
                      <td>{d.issuerName}</td>
                      <td>{d.docNo}</td>
                      <td>{d.docType}</td>
                      <td>{d.docDate}</td>
                      <td>
                        {onDocsChange && !posted ? (
                          <select value={norm(d.costType)} onChange={(e) => setDoc(i, 'costType', e.target.value)}>
                            <option value="">—</option>
                            {COST_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
                          </select>
                        ) : norm(d.costType) || '—'}
                      </td>
                      <td style={{ textAlign: 'right' }}>{fmt(d.nonVatAmount)}</td>
                      <td style={{ textAlign: 'right' }}>{fmt(beforeVat(d))}</td>
                      <td style={{ textAlign: 'right' }}>{fmt(d.vatAmount)}</td>
                      <td style={{ textAlign: 'right' }}>{fmt(d.totalAmount)}</td>
                      <td>
                        {onDocsChange && !posted ? (
                          <input
                            style={{ width: 140 }}
                            defaultValue={String(d.includedIn ?? '')}
                            placeholder="—"
                            onBlur={(e) => e.target.value !== String(d.includedIn ?? '') && setDoc(i, 'includedIn', e.target.value.trim())}
                          />
                        ) : d.includedIn || '—'}
                      </td>
                      <td style={otherShipment(d, String(doc.header.goodsSupplier ?? '')) ? red : undefined}>
                        {d.shipper || '—'}
                        {otherShipment(d, String(doc.header.goodsSupplier ?? '')) && (
                          <div style={{ fontSize: 11 }}>ไม่ตรงกับ SUPPLIER ในฟอร์ม ({String(doc.header.goodsSupplier ?? '')})</div>
                        )}
                      </td>
                      <td>{d.blNo || '—'}</td>
                      <td>{d.shipperInvoiceNo || '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="hint">
              แถวที่ใส่ "รวมอยู่ในใบ" คือใบเสร็จที่ตัวแทนจ่ายแทนแล้วเรียกเก็บรวมในใบแจ้งหนี้ของตัวแทนอีกที — แสดงไว้แต่ไม่นับในยอดข้อ 2
              · แก้หมวดหรือช่องนี้แล้วกด Save
            </p>
          </>
        )}
      </div>
    </div>
  );
}
