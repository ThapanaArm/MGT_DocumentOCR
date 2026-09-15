import type { DocHeader, DocLine, DocModel, MapResult } from '../../api/documents';
import { SEND_DISABLED } from '../../constants/flags';

/* The SAP Sales Order review step, lifted onto the page as one card -- the SAP-side counterpart
   of ZohoSalesOrderEditor, built so the two "check before you send" screens look and behave the
   same even though they post to different platforms. Unlike the Zoho editor this one is
   deliberately READ-ONLY: SAP documents are already fully edited/matched back in Step 2 (Data
   Mapping / MappingCards), so this card only shows exactly what Step 2 produced and lets the
   person send it -- it does not duplicate the editing surface. Values come straight from `map`
   (the last successful mapping result) and the document header/lines. */

export default function SapSalesOrderEditor({
  doc,
  map,
  header,
  salesOrg,
  sending,
  posted,
  onSend,
  onViewPayload,
}: {
  doc: DocModel;
  map: MapResult | null;
  header: DocHeader;
  salesOrg: string;
  sending: boolean;
  posted: boolean;
  onSend: () => void;
  /** Opens the "View Payload" modal showing the exact JSON that will be POSTed to SAP. */
  onViewPayload: () => void;
}) {
  const lines: DocLine[] = doc.lines || [];
  const lineStatus = (i: number) => map?.lines?.[i]?.status;
  const willSendCount = map ? lines.filter((_, i) => lineStatus(i) !== 'fail').length : 0;
  const notMatchedCount = map ? lines.filter((_, i) => lineStatus(i) === 'fail').length : 0;

  const body = () => {
    if (!map) return <p className="hint">Run Step 2 — Data Mapping first — the Sales Order to send will appear here.</p>;

    return (
      <>
        <p className="hint">
          This is exactly what will be created in SAP as a <b>Sales Order</b>. Values come from Step 2 (Data Mapping) — go
          back there to change anything.
        </p>

        <div className="so-counts">
          <div>
            <span>DOCUMENT LINES</span>
            <b>{lines.length}</b>
          </div>
          <div className={willSendCount ? 'ok' : ''}>
            <span>WILL SEND</span>
            <b>{willSendCount}</b>
          </div>
          <div className={notMatchedCount ? 'bad' : 'ok'}>
            <span>NOT MATCHED</span>
            <b>{notMatchedCount}</b>
          </div>
        </div>

        <div className="grid">
          <div className="f">
            <label>Sold-to</label>
            <input type="text" value={map.header.customer ? `${map.header.customer.code || ''} — ${map.header.customer.text || ''}` : ''} disabled readOnly />
          </div>
          <div className="f">
            <label>Ship-to</label>
            <input type="text" value={map.header.shipTo ? `${map.header.shipTo.code || ''} — ${map.header.shipTo.text || ''}` : ''} disabled readOnly />
          </div>
          <div className="f">
            <label>Customer PO</label>
            <input type="text" value={header.poNo || ''} disabled readOnly />
          </div>
          <div className="f">
            <label>PO Date</label>
            <input type="text" value={header.poDate || ''} disabled readOnly />
          </div>
          <div className="f">
            <label>Delivery Date</label>
            <input type="text" value={header.deliveryDate || ''} disabled readOnly />
          </div>
          <div className="f">
            <label>Currency</label>
            <input type="text" value={header.currency || 'THB'} disabled readOnly />
          </div>
          <div className="f">
            <label>Sales Organization</label>
            <input type="text" value={salesOrg} disabled readOnly />
          </div>
        </div>

        <p className="sec-title" style={{ marginTop: 20 }}>
          ORDERED ITEMS ({lines.length})
        </p>
        <div className="tw" style={{ overflowX: 'auto' }}>
          <table>
            <thead>
              <tr>
                <th>Line</th>
                <th>Material (SAP)</th>
                <th>Description</th>
                <th>Qty</th>
                <th>Unit</th>
                <th>Price</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((l, i) => {
                const m = map.lines?.[i];
                const failed = m?.status === 'fail';
                return (
                  <tr key={i}>
                    <td>{l.itemNo ?? i + 1}</td>
                    <td>
                      <b>{m?.code || '—'}</b>
                      {m?.text && <div className="hint">{m.text}</div>}
                    </td>
                    <td>{l.desc}</td>
                    <td>{l.qty}</td>
                    <td>{l.uom}</td>
                    <td>{l.price}</td>
                    <td>
                      {failed ? (
                        <span className="badge b-fail"><i className="fa-solid fa-xmark" /> Not found</span>
                      ) : (
                        <span className="badge b-ok"><i className="fa-solid fa-check" /> Matched</span>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        {notMatchedCount > 0 && (
          <p className="hint" style={{ marginTop: 10, color: 'var(--red, #c0392b)' }}>
            {notMatchedCount} line(s) above are not matched — see the Failed panel higher on this page, fix them, then
            re-run Step 2.
          </p>
        )}

        <div className="so-send" style={{ justifyContent: 'flex-end' }}>
          <div className="row" style={{ gap: 10 }}>
            <button className="btn success" onClick={onSend} disabled={SEND_DISABLED || sending || posted || !map.pass}>
              {sending ? 'Sending…' : posted ? 'ส่งสำเร็จ ✓' : 'Send'}
            </button>
            <button className="btn" onClick={onViewPayload}>
              {'{}'} View Payload
            </button>
          </div>
        </div>
      </>
    );
  };

  return (
    <div className="card">
      <div className="card-h">
        <h2>สรุป Sales Order ที่จะส่งไป SAP</h2>
        <div className="sp" />
        {map && (
          <span className="badge b-idle">
            {lines.length} line(s) · will send {willSendCount}
            {notMatchedCount ? ` · not sent ${notMatchedCount}` : ''}
          </span>
        )}
      </div>
      <div className="card-b">{body()}</div>
    </div>
  );
}
