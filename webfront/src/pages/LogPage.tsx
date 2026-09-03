import { useEffect, useMemo, useState } from 'react';
import { useAppState } from '../state/AppState';
import { getLogPayload, getLogs, type LogRow } from '../api/masters';
import { dt, fmt } from '../utils/format';
import { OCR_PROVIDER_SHORT } from '../constants/fields';
import Modal, { ModalHeader } from '../components/Modal';
import Pager, { DateRange, inDateRange, paginate } from '../components/Pager';

/* Ports renderLog()/renderLogLocal()/showLogPayload(). */
export default function LogPage() {
  const { guard } = useAppState();
  const [rows, setRows] = useState<LogRow[] | null>(null);
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(50);
  const [payload, setPayload] = useState<{ id: number; data: Record<string, any> } | null>(null);

  const reload = () => {
    setRows(null);
    guard(() => getLogs()).then((r) => setRows(r ?? []));
  };
  useEffect(reload, [guard]);

  const filtered = useMemo(
    () => (rows ?? []).filter((l) => inDateRange(l.PostedAt, from, to)),
    [rows, from, to],
  );
  const pageRows = paginate(filtered, page, pageSize);

  const showPayload = (id: number) =>
    guard(async () => {
      const data = await getLogPayload(id);
      setPayload({ id, data });
    });

  return (
    <div className="card">
      <div className="card-h">
        <h2>ประวัติการส่งเข้า SAP ({filtered.length})</h2>
        <div className="sp" />
        <span className="hint" style={{ marginRight: 4 }}>
          วันที่:
        </span>
        <DateRange from={from} to={to} setFrom={setFrom} setTo={setTo} />
        <div style={{ marginRight: 8 }} />
        <button className="btn sm" onClick={reload}>
          ↻ รีเฟรช
        </button>
      </div>
      <div className="card-b">
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th>เวลา</th>
                <th>Module</th>
                <th>SAP Doc</th>
                <th>เอกสารอ้างอิง</th>
                <th>คู่ค้า</th>
                <th style={{ textAlign: 'right' }}>ยอดรวม</th>
                <th>รายการ</th>
                <th>ผล</th>
                <th>Model</th>
                <th>ไฟล์</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {!rows ? (
                <tr>
                  <td colSpan={11} className="empty">
                    กำลังโหลด…
                  </td>
                </tr>
              ) : pageRows.length ? (
                pageRows.map((l) => (
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
                        <span className="badge b-ok">✓ SAP Connected Successfully</span>
                      ) : (
                        <span className="badge b-fail">✗ Unable to Connect to SAP</span>
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
                    ยังไม่มีเอกสารที่ส่งเข้า SAP
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <Pager page={page} setPage={setPage} pageSize={pageSize} setPageSize={setPageSize} total={filtered.length} />
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
