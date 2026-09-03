import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { getDashboard, type DashboardData } from '../api/dashboard';
import { useAppState } from '../state/AppState';
import { dt, fmtCost, intFmt, moduleLabel, num, statusBadge } from '../utils/format';
import StatTile from '../components/dashboard/StatTile';
import {
  DocTrendChart,
  ModuleBarChart,
  OcrGauge,
  StatusDonut,
  type DonutSeg,
} from '../components/dashboard/DashCharts';

/* Overview / Dashboard — port of renderHome() in app.js. */

const DAY_OPTIONS = [7, 15, 30];

export default function HomePage() {
  const { guard } = useAppState();
  const navigate = useNavigate();
  const [days, setDays] = useState(7);
  const [data, setData] = useState<DashboardData | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let alive = true;
    setData(null);
    setFailed(false);
    guard(() => getDashboard(days)).then((d) => {
      if (!alive) return;
      if (d) setData(d);
      else setFailed(true);
    });
    return () => {
      alive = false;
    };
  }, [days, guard]);

  if (failed) {
    return (
      <div className="card">
        <div className="card-b">
          <div className="empty">โหลดภาพรวมไม่สำเร็จ</div>
        </div>
      </div>
    );
  }

  if (!data) {
    return (
      <div className="card">
        <div className="card-b">
          <div className="empty">กำลังโหลด…</div>
        </div>
      </div>
    );
  }

  const sc = data.statusCounts;
  const pendingReview = sc.NEW || 0;
  const mappingFailed = sc.INCOMPLETE || 0;
  const readyToSend = sc.MAPPED || 0;
  const sentOk = sc.POSTED || 0;
  const total = sc.total || 0;
  const tr = data.trend;

  const tiles = [
    { icon: '📄', bg: 'var(--info-bg)', fg: 'var(--info)', label: 'เอกสารทั้งหมด', value: total, pct: tr.total },
    { icon: '⏳', bg: 'var(--orange-bg)', fg: 'var(--orange)', label: 'รอตรวจสอบ', value: pendingReview, pct: tr.NEW },
    { icon: '❌', bg: 'var(--red-bg)', fg: 'var(--red)', label: 'Mapping ไม่ผ่าน', value: mappingFailed, pct: tr.INCOMPLETE },
    { icon: '📤', bg: 'var(--info-bg)', fg: 'var(--info)', label: 'พร้อมส่ง SAP', value: readyToSend, pct: tr.MAPPED },
    { icon: '✅', bg: 'var(--green-bg)', fg: 'var(--green)', label: 'ส่ง SAP สำเร็จ', value: sentOk, pct: tr.POSTED },
  ];

  const donutSegs: DonutSeg[] = (
    [
      { label: 'สำเร็จ', value: sentOk, color: 'var(--green)' },
      { label: 'รอตรวจสอบ', value: pendingReview + mappingFailed, color: 'var(--orange)' },
      { label: 'พร้อมส่ง', value: readyToSend, color: 'var(--info)' },
    ] as DonutSeg[]
  ).filter((s) => s.value > 0);
  const donutTotal = donutSegs.reduce((a, s) => a + s.value, 0) || 1;
  const donutWithPct = donutSegs.map((s) => ({ ...s, pct: Math.round((s.value / donutTotal) * 100) }));

  const daySelect = (
    <select value={days} onChange={(e) => setDays(parseInt(e.target.value))} style={{ width: 'auto' }}>
      {DAY_OPTIONS.map((n) => (
        <option key={n} value={n}>
          {n} วัน
        </option>
      ))}
    </select>
  );

  const tasks = [
    { icon: '📋', title: 'ตรวจสอบข้อมูล OCR', sub: 'เอกสารรอตรวจสอบ', n: pendingReview },
    { icon: '⚙', title: 'แก้ไข Master Mapping', sub: 'รายการที่ต้องแก้ไข', n: mappingFailed },
    { icon: '☁', title: 'ส่งข้อมูลไป SAP', sub: 'เอกสารพร้อมส่ง', n: readyToSend },
  ];

  const viewAllInbox = () => navigate('/list');
  const costTotal = data.costByModule.reduce((a, r) => a + num(r.cost), 0);
  const costCurrency = data.costByModule.find((r) => r.costCurrency)?.costCurrency || 'USD';

  return (
    <>
      {/* Stat tiles */}
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fit,minmax(190px,1fr))',
          gap: 16,
          marginBottom: 20,
        }}
      >
        {tiles.map((t) => (
          <StatTile key={t.label} {...t} />
        ))}
      </div>

      {/* Trend + status donut */}
      <div
        style={{ display: 'grid', gridTemplateColumns: '1.6fr 1fr', gap: 20, alignItems: 'start' }}
        className="dash-row"
      >
        <div className="card" style={{ marginBottom: 0 }}>
          <div className="card-h">
            <h2>ปริมาณเอกสารและความสำเร็จ OCR</h2>
            <div className="sp" />
            {daySelect}
          </div>
          <div className="card-b">
            <div className="row" style={{ gap: 18, marginBottom: 8, fontSize: 13 }}>
              <span>
                <span
                  style={{
                    display: 'inline-block',
                    width: 14,
                    height: 3,
                    background: 'var(--brand)',
                    verticalAlign: 'middle',
                    marginRight: 6,
                  }}
                />
                เอกสาร
              </span>
              <span>
                <span
                  style={{
                    display: 'inline-block',
                    width: 14,
                    height: 0,
                    borderTop: '3px dashed var(--info)',
                    verticalAlign: 'middle',
                    marginRight: 6,
                  }}
                />
                OCR สำเร็จ
              </span>
            </div>
            <DocTrendChart data={data.ocrDaily} />
          </div>
        </div>

        <div className="card" style={{ marginBottom: 0 }}>
          <div className="card-h">
            <h2>สถานะเอกสาร</h2>
          </div>
          <div className="card-b">
            <StatusDonut
              segments={donutWithPct}
              centerValue={intFmt(total)}
              centerLabel="รวมทั้งหมด เอกสาร"
            />
            <div style={{ marginTop: 14 }}>
              {donutWithPct.map((s) => (
                <div
                  key={s.label}
                  className="row"
                  style={{ justifyContent: 'space-between', marginBottom: 8 }}
                >
                  <span>
                    <span
                      style={{
                        display: 'inline-block',
                        width: 9,
                        height: 9,
                        borderRadius: '50%',
                        background: s.color,
                        marginRight: 8,
                      }}
                    />
                    {s.label}
                  </span>
                  <span className="hint">
                    {s.pct}% · {intFmt(s.value)} เอกสาร
                  </span>
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>

      {/* By module + OCR perf + tasks */}
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: '1fr 1fr 1fr',
          gap: 20,
          alignItems: 'start',
          marginTop: 20,
        }}
        className="dash-row"
      >
        <div className="card" style={{ marginBottom: 0 }}>
          <div className="card-h">
            <h2>เอกสารตามประเภท</h2>
            <div className="sp" />
            {daySelect}
          </div>
          <div className="card-b">
            {data.byModule.length ? (
              <ModuleBarChart data={data.byModule} />
            ) : (
              <div className="empty">ยังไม่มีข้อมูล</div>
            )}
            <p className="sec-title" style={{ marginTop: 20 }}>
              ค่าใช้จ่าย OCR แต่ละ Module
            </p>
            {data.costByModule.length ? (
              <div className="tw">
                <table>
                  <thead>
                    <tr>
                      <th>Module</th>
                      <th style={{ textAlign: 'right' }}>เอกสาร</th>
                      <th style={{ textAlign: 'right' }}>Token</th>
                      <th style={{ textAlign: 'right' }}>ค่าใช้จ่าย</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.costByModule.map((r) => (
                      <tr key={r.module}>
                        <td>
                          <span
                            className={
                              'badge ' +
                              (r.module === 'AP' ? 'b-warn' : r.module === 'II' ? 'b-idle' : 'b-ok')
                            }
                          >
                            {moduleLabel(r.module)}
                          </span>
                        </td>
                        <td style={{ textAlign: 'right' }}>{intFmt(r.count)}</td>
                        <td style={{ textAlign: 'right' }}>{intFmt(r.tokens)}</td>
                        <td style={{ textAlign: 'right' }}>
                          {fmtCost(r.cost)} {r.costCurrency || ''}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                  <tfoot>
                    <tr className="totrow">
                      <td>รวม</td>
                      <td />
                      <td />
                      <td style={{ textAlign: 'right' }}>
                        {fmtCost(costTotal)} {costCurrency}
                      </td>
                    </tr>
                  </tfoot>
                </table>
              </div>
            ) : (
              <div className="empty">ยังไม่มีข้อมูล</div>
            )}
          </div>
        </div>

        <div className="card" style={{ marginBottom: 0 }}>
          <div className="card-h">
            <h2>ประสิทธิภาพ OCR</h2>
          </div>
          <div className="card-b">
            <OcrGauge pct={data.ocrPerf.avgConfidencePct ?? 0} label="ความแม่นยำ OCR" />
            <div style={{ marginTop: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div className="row" style={{ gap: 10 }}>
                <span>⏱</span>
                <div>
                  <div className="hint">เฉลี่ยต่อเอกสาร</div>
                  <b>
                    {data.ocrPerf.avgDurationSec != null
                      ? data.ocrPerf.avgDurationSec + ' วินาที'
                      : '—'}
                  </b>
                </div>
              </div>
              <div className="row" style={{ gap: 10 }}>
                <span>👤</span>
                <div>
                  <div className="hint">แก้ไขโดยผู้ใช้</div>
                  <b>{data.ocrPerf.pctEditedByUser}%</b>
                </div>
              </div>
              <div className="row" style={{ gap: 10 }}>
                <span>📋</span>
                <div>
                  <div className="hint">Token วันนี้</div>
                  <b>{intFmt(data.ocrPerf.tokensToday)}</b>
                </div>
              </div>
            </div>
          </div>
        </div>

        <div className="card" style={{ marginBottom: 0 }}>
          <div className="card-h">
            <h2>งานที่ต้องดำเนินการ</h2>
          </div>
          <div className="card-b">
            {tasks.map((t) => (
              <div
                key={t.title}
                className="row"
                style={{
                  justifyContent: 'space-between',
                  padding: '10px 0',
                  borderBottom: '1px solid var(--line)',
                  cursor: 'pointer',
                }}
                onClick={viewAllInbox}
              >
                <div className="row" style={{ gap: 10 }}>
                  <span style={{ fontSize: 18 }}>{t.icon}</span>
                  <div>
                    <div style={{ fontWeight: 600 }}>{t.title}</div>
                    <div className="hint">{t.sub}</div>
                  </div>
                </div>
                <div className="row" style={{ gap: 8 }}>
                  <span className="badge b-warn">{t.n}</span>
                  <span className="hint">›</span>
                </div>
              </div>
            ))}
            <div style={{ textAlign: 'right', marginTop: 10 }}>
              <a
                href="#"
                onClick={(e) => {
                  e.preventDefault();
                  viewAllInbox();
                }}
              >
                ดูงานทั้งหมด →
              </a>
            </div>
          </div>
        </div>
      </div>

      {/* Recent documents */}
      <div className="card" style={{ marginTop: 20 }}>
        <div className="card-h">
          <h2>รายการล่าสุด</h2>
          <div className="sp" />
          <button className="btn sm" onClick={viewAllInbox}>
            ดูทั้งหมด →
          </button>
        </div>
        <div className="card-b">
          <div className="tw">
            <table>
              <thead>
                <tr>
                  <th>เลขที่เอกสาร</th>
                  <th>ประเภท</th>
                  <th>คู่ค้า</th>
                  <th>สถานะ</th>
                  <th>อัปเดตล่าสุด</th>
                  <th>ผู้ดำเนินการ</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {data.recent.length ? (
                  data.recent.map((r) => {
                    const sb = statusBadge(r.Status);
                    return (
                      <tr key={r.DocId}>
                        <td>
                          <b>{r.DocNo || r.FileName || '#' + r.DocId}</b>
                        </td>
                        <td>{moduleLabel(r.Module)}</td>
                        <td>{r.PartnerName || ''}</td>
                        <td>
                          <span className={'badge ' + sb.cls}>{sb.label}</span>
                        </td>
                        <td className="hint">{dt(r.UpdatedAt || r.CreatedAt)}</td>
                        <td>{r.PostedBy || r.CreatedBy || ''}</td>
                        <td>
                          <button
                            className="btn sm"
                            onClick={() => navigate(`/list/${r.Module}`)}
                            title="เปิดในทะเบียนเอกสาร (การเปิดเอกสารจะพอร์ตใน Phase C)"
                          >
                            เปิด
                          </button>
                        </td>
                      </tr>
                    );
                  })
                ) : (
                  <tr>
                    <td colSpan={7} className="empty">
                      ยังไม่มีเอกสารในระบบ
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      </div>
    </>
  );
}
