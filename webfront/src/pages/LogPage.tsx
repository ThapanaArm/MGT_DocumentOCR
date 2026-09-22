import { useState } from 'react';
import { useAppState } from '../state/AppState';
import { getLogPayload, getLogs, type LogRow } from '../api/masters';
import { dt, fmt } from '../utils/format';
import { OCR_PROVIDER_SHORT } from '../constants/fields';
import Modal, { ModalHeader } from '../components/Modal';
import Pager, { DateRange } from '../components/Pager';
import { usePagedList } from '../hooks/usePagedList';

/* SAP submission history — server-side paged (see usePagedList); only the current page is loaded. */
export default function LogPage() {
  const { guard } = useAppState();
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [payload, setPayload] = useState<{ id: number; data: Record<string, any> } | null>(null);

  const { rows, total, page, pageSize, setPage, setPageSize, reload } = usePagedList<LogRow>(
    (pg, ps) => guard(() => getLogs({ dateFrom: from, dateTo: to, page: pg, pageSize: ps })),
    [from, to],
  );

  const showPayload = (id: number) =>
    guard(async () => {
      const data = await getLogPayload(id);
      setPayload({ id, data });
    });

  return (
    <div className="card">
      <div className="card-h">
        <h2>SAP Submission History ({total})</h2>
        <div className="sp" />
        <span className="hint" style={{ marginRight: 4 }}>
          Date:
        </span>
        <DateRange from={from} to={to} setFrom={setFrom} setTo={setTo} />
        <div style={{ marginRight: 8 }} />
        <button className="btn sm" onClick={reload}>
          <i className="fa-solid fa-arrow-rotate-right" /> Refresh
        </button>
      </div>
      <div className="card-b">
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th>Time</th>
                <th>Module</th>
                <th>SAP Doc</th>
                <th>Reference Document</th>
                <th>Partner</th>
                <th style={{ textAlign: 'right' }}>Total</th>
                <th>Lines</th>
                <th>Result</th>
                <th>Model</th>
                <th>File</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {!rows ? (
                <tr>
                  <td colSpan={11} className="empty">
                    Loading…
                  </td>
                </tr>
              ) : rows.length ? (
                rows.map((l) => (
                  <tr key={l.LogId}>
                    <td className="hint">{dt(l.PostedAt)}</td>
                    <td>
                      <span
                        className={
                          'badge ' + (l.Module === 'AP' ? 'b-warn' : l.Module === 'II' ? 'b-idle' : 'b-ok')
                        }
                      >
                        {l.Module}
                      </span>
                    </td>
                    <td>
                      <b>{l.SapDocNo || '-'}</b>
                    </td>
                    <td>{l.DocNo || ''}</td>
                    <td>{l.PartnerName || ''}</td>
                    <td style={{ textAlign: 'right' }}>{fmt(l.TotalAmount)}</td>
                    <td style={{ textAlign: 'center' }}>{l.Lines || 0}</td>
                    <td>
                      {l.Success ? (
                        <span className="badge b-ok"><i className="fa-solid fa-check" /> SAP Connected Successfully</span>
                      ) : (
                        <span className="badge b-fail"><i className="fa-solid fa-xmark" /> Unable to Connect to SAP</span>
                      )}
                    </td>
                    <td>
                      <span className="hint">
                        {OCR_PROVIDER_SHORT[l.OcrProvider ?? ''] || l.OcrProvider || '—'}
                      </span>
                    </td>
                    <td>{l.FileName || ''}</td>
                    <td>
                      <button className="btn sm" onClick={() => showPayload(l.LogId)}>
                        Payload
                      </button>
                    </td>
                  </tr>
                ))
              ) : (
                <tr>
                  <td colSpan={11} className="empty">
                    No documents submitted to SAP yet
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <Pager page={page} setPage={setPage} pageSize={pageSize} setPageSize={setPageSize} total={total} />
      </div>

      <Modal open={payload != null} onClose={() => setPayload(null)}>
        <ModalHeader title={`Payload (Log #${payload?.id})`} onClose={() => setPayload(null)} />
        <div className="card-b">
          <pre className="json">{JSON.stringify(payload?.data, null, 2)}</pre>
        </div>
      </Modal>
    </div>
  );
}
