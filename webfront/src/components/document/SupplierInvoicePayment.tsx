import { useEffect, useState } from 'react';
import { getSupplierInvoicePayment, type SupplierInvoicePaymentReadback } from '../../api/documents';
import { getSapPurchaseOrder } from '../../api/sap';
import { expectedPaymentDueDate, paymentDateForDisplay, type PaymentTerm } from '../../utils/supplierInvoicePayment';

export default function SupplierInvoicePayment({
  docId, terms, code, description, baseline, purchaseOrder, locked, sent, onTerm, onBaseline,
}: {
  docId: number;
  terms: PaymentTerm[];
  code: string;
  description: string;
  baseline: string;
  purchaseOrder: string;
  locked: boolean;
  sent: boolean;
  onTerm: (term: PaymentTerm) => void;
  onBaseline: (date: string) => void;
}) {
  const [query, setQuery] = useState('');
  const [poTerm, setPoTerm] = useState('');
  const [readback, setReadback] = useState<SupplierInvoicePaymentReadback[] | null>(null);

  useEffect(() => {
    let current = true;
    setPoTerm('');
    if (purchaseOrder) {
      void getSapPurchaseOrder(purchaseOrder).then(result => {
        if (current) setPoTerm(result.head?.paymentTerms?.trim() ?? '');
      }).catch(() => { /* PO terms are shown only when the SAP lookup returns them. */ });
    }
    return () => { current = false; };
  }, [purchaseOrder]);

  useEffect(() => {
    let current = true;
    setReadback(null);
    if (sent) {
      void getSupplierInvoicePayment(docId).then(({ payments }) => {
        if (current) setReadback(payments);
      }).catch(() => {
        if (current) setReadback([]);
      });
    }
    return () => { current = false; };
  }, [docId, sent]);

  const search = query.trim().toLowerCase();
  const sapPayment = readback?.length === 1 ? readback[0] : undefined;
  const shownCode = sapPayment ? sapPayment.paymentTerms.trim().toUpperCase() : code;
  const shownBaseline = sapPayment ? sapPayment.baselineDate : baseline;
  const selected = terms.find(term => term.code === shownCode);
  const shownDescription = selected?.description ?? (shownCode === code ? description : '');
  const choices = terms.filter(term => term.code === shownCode || `${term.code} ${term.description}`.toLowerCase().includes(search));
  const dueDate = expectedPaymentDueDate(shownBaseline, shownDescription);
  const shownDue = sapPayment
    ? (sapPayment.dueDates.map(paymentDateForDisplay).join(', ') || 'ยังไม่มีวันที่ยืนยันจาก SAP')
    : dueDate ? paymentDateForDisplay(dueDate) : 'รอ SAP คำนวณ';

  return (
    <div className="card-b">
      {sent && readback === null && <p className="hint" role="status">กำลังอ่าน Payment จาก SAP…</p>}
      {sent && readback?.length === 0 && <p className="hint" role="status">ยังอ่าน Payment จาก SAP ไม่ได้ กำลังแสดงข้อมูลใน OCR</p>}
      {sapPayment && <p className="hint">ข้อมูลจาก SAP · Supplier Invoice {sapPayment.supplierInvoice}</p>}
      {(!readback || readback.length <= 1 || !locked) && (
        <div className="grid">
          <div className="f">
            <label htmlFor="supplier-invoice-payment-term">Payment Terms</label>
            {!locked && (
              <input type="search" aria-label="ค้นหา Payment Terms" placeholder="ค้นหาด้วยรหัสหรือคำอธิบาย"
                value={query} onChange={event => setQuery(event.target.value)} />
            )}
            <select id="supplier-invoice-payment-term" value={shownCode} disabled={locked} onChange={event => {
              const term = terms.find(row => row.code === event.target.value);
              if (term) { onTerm(term); setQuery(''); }
            }}>
              <option value="" disabled>{locked ? '—' : 'เลือก Payment Terms'}</option>
              {shownCode && !selected && <option value={shownCode}>{shownCode}{shownDescription ? ` — ${shownDescription}` : ''}</option>}
              {choices.map(term => (
                <option value={term.code} key={term.code}>{term.code}{term.description ? ` — ${term.description}` : ''}</option>
              ))}
            </select>
            {!locked && terms.length === 0 && <span className="hint">ไม่พบรายการ Payment Terms ใน Master</span>}
            {!locked && terms.length > 0 && search && !choices.some(term =>
              `${term.code} ${term.description}`.toLowerCase().includes(search)) && <span className="hint">ไม่พบเงื่อนไขที่ค้นหา</span>}
            {poTerm && <span className="hint">PO {purchaseOrder} ใช้ {poTerm} (อ้างอิง)</span>}
          </div>
          <div className="f">
            <label htmlFor="supplier-invoice-baseline">Baseline Date</label>
            <input id="supplier-invoice-baseline" type="date" value={shownBaseline} readOnly={locked}
              onChange={event => onBaseline(event.target.value)} />
          </div>
          <div className="f">
            <label htmlFor="supplier-invoice-due">Due On</label>
            <input id="supplier-invoice-due" readOnly value={shownDue} />
            {!sapPayment && dueDate && <span className="hint">วันที่คาดการณ์</span>}
          </div>
        </div>
      )}
      {readback && readback.length > 1 && (
        <div className="tw" style={{ marginTop: 8 }}>
          <table>
            <thead><tr><th>Supplier Invoice</th><th>Payment Terms</th><th>Baseline Date</th><th>Due On จาก SAP</th></tr></thead>
            <tbody>{readback.map(payment => (
              <tr key={payment.supplierInvoice}>
                <td>{payment.supplierInvoice}</td><td>{payment.paymentTerms || '—'}</td>
                <td>{paymentDateForDisplay(payment.baselineDate)}</td>
                <td>{payment.dueDates.length ? payment.dueDates.map(paymentDateForDisplay).join(', ') : 'ยังไม่มีวันที่ยืนยันจาก SAP'}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
    </div>
  );
}
