import { useState } from 'react';
import type { DocLine } from '../../api/documents';
import type { OcrProvider } from '../../api/masters';
import type {
  ZohoDeal,
  ZohoDealItem,
  ZohoSalesOrderPreview,
  ZohoSalesOrderResult,
} from '../../api/zoho';
import CompareModal, { type CompareCandidateItem } from './CompareModal';
import type { CompareField } from '../../api/compare';
import { SEND_DISABLED } from '../../constants/flags';

/* The Zoho Sales Order, lifted whole onto the document page as one editable table -- what
   replaced the old per-line "Material - Row N" comparison cards + the "Confirm Submission to
   Zoho CRM" popup. It shows exactly the fields that get sent to Zoho (header + Ordered Items),
   every one editable inline, plus any lines that couldn't be matched to a Deal Item (each with
   an "Ask AI to suggest a match" button). The person edits here directly and presses Send at the
   bottom -- no separate confirm popup. The heavy lifting (fetching the preview, holding the edit
   state, and actually creating the record) all stays in DocumentPage; this is the presentation. */

type LineEdit = { quantity: string; unitPrice: string; unit: string; description: string; materialId?: string };

export default function ZohoSalesOrderEditor({
  preview,
  loading,
  error,
  selectedDealId,
  docLines,
  resolvedDeal,
  providers,
  header,
  onHeaderChange,
  lineEdits,
  onLineChange,
  onUseAiMatch,
  sending,
  result,
  onSend,
  onViewPayload,
  posted,
}: {
  /** The merged preview (backend defaults + any AI-confirmed matches) -- fetched by DocumentPage
   *  as soon as a Deal is picked, so this table is visible without pressing Send first. */
  preview: ZohoSalesOrderPreview | null;
  loading: boolean;
  error: string | null;
  selectedDealId: string;
  docLines: DocLine[];
  /** The full resolved Deal (with its Deal Items) -- the candidate pool for the AI-suggest tool
   *  on a line the automatic match skipped. */
  resolvedDeal: ZohoDeal | null;
  providers: OcrProvider[];
  header: { subject: string; customerRef: string; deliveryDate: string; paymentTerms: string; paymentCurrency: string; incoterms: string; taxId: string };
  onHeaderChange: (patch: Partial<ZohoSalesOrderEditorHeader>) => void;
  lineEdits: Record<string, LineEdit>;
  onLineChange: (itemNo: unknown, patch: Partial<LineEdit>) => void;
  onUseAiMatch: (itemNo: unknown, item: ZohoDealItem) => void;
  sending: boolean;
  result: ZohoSalesOrderResult | null;
  onSend: () => void;
  /** Opens the "View Payload" modal showing the exact JSON that will be POSTed to Zoho. */
  onViewPayload: () => void;
  posted: boolean;
}) {
  // Which skipped line (by itemNo) currently has the "Ask AI to suggest a match" popup open.
  const [aiMatchItemNo, setAiMatchItemNo] = useState<string | null>(null);

  const dealHasMaterials = !!resolvedDeal && resolvedDeal.items.some((it) => it.materialId);

  // Document fields shown to the AI when suggesting a match for a skipped line -- the same info a
  // person reads off the card (partner code + material name), enriched with qty/unit/price from
  // the document line where available.
  const docFieldsFor = (itemNo: string): CompareField[] => {
    const dl = docLines.find((l) => String(l.itemNo) === itemNo);
    const sk = preview?.skipped.find((s) => String(s.itemNo) === itemNo);
    return [
      { label: 'Partner Material Code', value: dl?.extCode || sk?.extCode || '' },
      { label: 'Material Name (from document)', value: dl?.desc || sk?.desc || '' },
      { label: 'Quantity', value: dl ? String(dl.qty ?? '') : '' },
      { label: 'Unit', value: dl?.uom || '' },
      { label: 'Unit Price', value: dl ? String(dl.price ?? '') : '' },
    ];
  };

  const aiCandidates: CompareCandidateItem<ZohoDealItem>[] = (resolvedDeal?.items || [])
    .filter((it): it is ZohoDealItem & { materialId: string } => !!it.materialId)
    .map((it) => ({
      id: it.materialId,
      label: it.materialName || it.materialCode || it.materialId,
      fields: [
        { label: 'Material Name', value: it.materialName },
        { label: 'Material Code', value: it.materialCode },
        { label: 'Material Description', value: it.materialDescription },
        { label: 'Material Group', value: it.materialGroup },
        { label: 'Unit', value: it.unit },
      ],
      raw: it,
    }));

  const body = () => {
    if (!selectedDealId) return <p className="hint">Pick a Deal on the Deal (Zoho) card above — the Sales Order to send will appear here.</p>;
    if (loading) return <p className="hint">Reading the Sales Order from Zoho CRM…</p>;
    if (error) return <p className="hint" style={{ color: 'var(--red, #c0392b)' }}>Could not build the Sales Order: {error}</p>;
    if (!preview) return <p className="hint">No Sales Order data yet.</p>;

    return (
      <>
        <p className="hint">
          This is exactly what will be created in Zoho CRM as a <b>Sales Order</b>, linked to Deal <b>{preview.dealName}</b>. Every
          field is editable — change anything before you send.
        </p>

        <div className="so-counts">
          <div>
            <span>DOCUMENT LINES</span>
            <b>{preview.lines.length + preview.skipped.length}</b>
          </div>
          <div className={preview.lines.length ? 'ok' : ''}>
            <span>WILL SEND</span>
            <b>{preview.lines.length}</b>
          </div>
          <div className={preview.skipped.length ? 'bad' : 'ok'}>
            <span>NOT MATCHED</span>
            <b>{preview.skipped.length}</b>
          </div>
        </div>

        <div className="grid">
          <div className="f">
            <label>Subject</label>
            <input type="text" value={header.subject} disabled={posted} maxLength={50} onChange={(e) => onHeaderChange({ subject: e.target.value })} />
          </div>
          <div className="f">
            <label>Customer PO / Customer Ref.</label>
            <input type="text" value={header.customerRef} disabled={posted} onChange={(e) => onHeaderChange({ customerRef: e.target.value })} />
          </div>
          <div className="f">
            <label>Delivery Date</label>
            <input type="text" value={header.deliveryDate} disabled={posted} onChange={(e) => onHeaderChange({ deliveryDate: e.target.value })} />
          </div>
          <div className="f">
            <label>Payment Terms</label>
            <select value={header.paymentTerms} disabled={posted} onChange={(e) => onHeaderChange({ paymentTerms: e.target.value })}>
              <option value="">— not set —</option>
              {preview.paymentTermsOptions.map((o) => (
                <option key={o} value={o}>
                  {o}
                </option>
              ))}
            </select>
          </div>
          <div className="f">
            <label>Payment Currency</label>
            <select value={header.paymentCurrency} disabled={posted} onChange={(e) => onHeaderChange({ paymentCurrency: e.target.value })}>
              <option value="">— not set —</option>
              {preview.paymentCurrencyOptions.map((o) => (
                <option key={o} value={o}>
                  {o}
                </option>
              ))}
            </select>
          </div>
          <div className="f">
            <label>Incoterms</label>
            <input type="text" value={header.incoterms} disabled={posted} onChange={(e) => onHeaderChange({ incoterms: e.target.value })} />
          </div>
          <div className="f">
            <label>Tax ID</label>
            <input type="text" value={header.taxId} disabled={posted} onChange={(e) => onHeaderChange({ taxId: e.target.value })} />
          </div>
          {preview.accountCode && (
            <div className="f">
              <label>Account Code (linked)</label>
              <input type="text" value={preview.accountCode} disabled readOnly />
            </div>
          )}
        </div>

        <p className="sec-title" style={{ marginTop: 20 }}>
          ORDERED ITEMS ({preview.lines.length})
        </p>
        {preview.lines.length === 0 ? (
          <p className="hint">No document line is matched to a Deal Item yet — resolve the skipped lines below first.</p>
        ) : (
          <div className="tw" style={{ overflowX: 'auto' }}>
            <table>
              <thead>
                <tr>
                  <th>Line</th>
                  <th>Material (Zoho)</th>
                  <th>Description</th>
                  <th>Qty</th>
                  <th>Unit</th>
                  <th>Unit Price</th>
                </tr>
              </thead>
              <tbody>
                {preview.lines.map((l) => {
                  const key = String(l.itemNo);
                  const e = lineEdits[key] || { quantity: '', unitPrice: '', unit: '', description: '' };
                  return (
                    <tr key={key}>
                      <td>{key}</td>
                      <td>
                        <b>{l.materialCode || l.materialName || '—'}</b>
                        {l.materialName && l.materialCode && <div className="hint">{l.materialName}</div>}
                      </td>
                      <td>
                        <input type="text" value={e.description} disabled={posted} onChange={(ev) => onLineChange(l.itemNo, { description: ev.target.value })} style={{ width: '100%', minWidth: 220 }} />
                      </td>
                      <td>
                        <input type="text" value={e.quantity} disabled={posted} onChange={(ev) => onLineChange(l.itemNo, { quantity: ev.target.value })} style={{ width: 90 }} />
                      </td>
                      <td>
                        <input type="text" value={e.unit} disabled={posted} onChange={(ev) => onLineChange(l.itemNo, { unit: ev.target.value })} style={{ width: 80 }} />
                      </td>
                      <td>
                        <input type="text" value={e.unitPrice} disabled={posted} onChange={(ev) => onLineChange(l.itemNo, { unitPrice: ev.target.value })} style={{ width: 110 }} />
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}

        {preview.skipped.length > 0 && (
          <div style={{ marginTop: 16 }}>
            <p className="sec-title">NOT MATCHED ({preview.skipped.length}) — won't be sent unless you match them</p>
            <div className="tw" style={{ overflowX: 'auto' }}>
              <table>
                <thead>
                  <tr>
                    <th>Line</th>
                    <th>Partner Code</th>
                    <th>Material Name (from document)</th>
                    <th>Reason</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {preview.skipped.map((sk, i) => {
                    const key = String(sk.itemNo ?? i + 1);
                    return (
                      <tr key={key}>
                        <td>{key}</td>
                        <td>{sk.extCode || '—'}</td>
                        <td>{sk.desc || '—'}</td>
                        <td className="hint">{sk.reason}</td>
                        <td>
                          {dealHasMaterials && !posted && (
                            <button className="btn sm" onClick={() => setAiMatchItemNo(key)}>
                              <i className="fa-solid fa-wand-magic-sparkles" /> Ask AI to match
                            </button>
                          )}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {result && !result.success && (
          <div className="hint" style={{ color: 'var(--red, #c0392b)', marginTop: 14 }}>
            Zoho did not accept the record: {result.message || 'Unknown error'}
          </div>
        )}
        {result && result.success && (
          <div className="badge b-ok" style={{ marginTop: 14 }}>
            <i className="fa-solid fa-check" /> Sent — Sales Order created in Zoho CRM ({result.linesSent} line(s))
          </div>
        )}

        <div className="so-send">
          <div className="row" style={{ gap: 10 }}>
            <button
              className="btn success"
              onClick={onSend}
              disabled={SEND_DISABLED || sending || posted || preview.lines.length === 0 || !!result?.success}
            >
              {sending
                ? 'กำลังส่ง…'
                : result?.success
                  ? 'ส่งสำเร็จ ✓'
                  : `⎋ Send ${preview.lines.length} line${preview.lines.length === 1 ? '' : 's'} to Zoho CRM`}
            </button>
            <button className="btn" onClick={onViewPayload} disabled={preview.lines.length === 0}>
              {'{}'} ดูข้อมูลที่จะส่ง
            </button>
          </div>
          <div style={{ textAlign: 'right' }}>
            <b>
              {result?.success
                ? 'Sent to Zoho CRM'
                : preview.lines.length === 0
                  ? 'Nothing to send yet'
                  : `${preview.lines.length} line${preview.lines.length === 1 ? '' : 's'} ready`}
            </b>
            <div className="hint">
              {preview.lines.length === 0
                ? 'Match at least one line before sending'
                : preview.skipped.length
                  ? `Not sent: line ${preview.skipped.map((sk, i) => String(sk.itemNo ?? i + 1)).join(', ')}`
                  : 'All document lines matched'}
            </div>
          </div>
        </div>
      </>
    );
  };

  return (
    <>
      <div className="card">
        <div className="card-h">
          <h2>สรุป Sales Order ที่จะส่งไป Zoho CRM</h2>
          <div className="sp" />
          {preview && (
            <span className="badge b-idle">
              {preview.lines.length + preview.skipped.length} line(s) · will send {preview.lines.length}
              {preview.skipped.length ? ` · not sent ${preview.skipped.length}` : ''}
            </span>
          )}
        </div>
        <div className="card-b">{body()}</div>
      </div>

      <CompareModal<ZohoDealItem>
        open={aiMatchItemNo != null}
        onClose={() => setAiMatchItemNo(null)}
        title={`AI Suggested Match — Line ${aiMatchItemNo ?? ''}`}
        docFields={aiMatchItemNo ? docFieldsFor(aiMatchItemNo) : []}
        candidates={aiCandidates}
        providers={providers}
        onUse={(item) => {
          if (aiMatchItemNo) onUseAiMatch(aiMatchItemNo, item);
          setAiMatchItemNo(null);
        }}
      />
    </>
  );
}

export type ZohoSalesOrderEditorHeader = {
  subject: string;
  customerRef: string;
  deliveryDate: string;
  paymentTerms: string;
  paymentCurrency: string;
  incoterms: string;
  taxId: string;
};

