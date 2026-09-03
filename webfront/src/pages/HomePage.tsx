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
          <div className="empty">Failed to load overview</div>
        </div>
      </div>
    );
  }

  if (!data) {
    return (
      <div className="card">
        <div className="card-b">
          <div className="empty">Loading…</div>
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
    { icon: '📄', bg: 'var(--info-bg)', fg: 'var(--info)', label: 'Total Documents', value: total, pct: tr.total },
    { icon: '⏳', bg: 'var(--orange-bg)', fg: 'var(--orange)', label: 'Pending Review', value: pendingReview, pct: tr.NEW },
    { icon: '❌', bg: 'var(--red-bg)', fg: 'var(--red)', label: 'Mapping Failed', value: mappingFailed, pct: tr.INCOMPLETE },
    { icon: '📤', bg: 'var(--info-bg)', fg: 'var(--info)', label: 'Ready to Send to SAP', value: readyToSend, pct: tr.MAPPED },
    { icon: '✅', bg: 'var(--green-bg)', fg: 'var(--green)', label: 'Sent to SAP Successfully', value: sentOk, pct: tr.POSTED },
  ];

  const donutSegs: DonutSeg[] = (
    [
      { label: 'Successful', value: sentOk, color: 'var(--green)' },
      { label: 'Pending Review', value: pendingReview + mappingFailed, color: 'var(--orange)' },
      { label: 'Ready to Send', value: readyToSend, color: 'var(--info)' },
    ] as DonutSeg[]
  ).filter((s) => s.value > 0);
  const donutTotal = donutSegs.reduce((a, s) => a + s.value, 0) || 1;
  const donutWithPct = donutSegs.map((s) => ({ ...s, pct: Math.round((s.value / donutTotal) * 100) }));

  const daySelect = (
    <select value={days} onChange={(e) => setDays(parseInt(e.target.value))} style={{ width: 'auto' }}>
      {DAY_OPTIONS.map((n) => (
        <option key={n} value={n}>
          {n} days
        </option>
      ))}
    </select>
  );

  const tasks = [
    { icon: '📋', title: 'Review OCR Data', sub: 'Documents pending review', n: pendingReview },
    { icon: '⚙', title: 'Fix Master Mapping', sub: 'Items needing correction', n: mappingFailed },
    { icon: '☁', title: 'Send Data to SAP', sub: 'Documents ready to send', n: readyToSend },
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
            <h2>Document Volume and OCR Success</h2>
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
                Documents
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
                OCR Successful
              </span>
            </div>
            <DocTrendChart data={data.ocrDaily} />
          </div>
        </div>

        <div className="card" style={{ marginBottom: 0 }}>
          <div className="card-h">
            <h2>Document Status</h2>
          </div>
          <div className="card-b">
            <StatusDonut
              segments={donutWithPct}
              centerValue={intFmt(total)}
              centerLabel="Total Documents"
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
                    {s.pct}% · {intFmt(s.value)} documents
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
            <h2>Documents by Type</h2>
            <div className="sp" />
            {daySelect}
          </div>
          <div className="card-b">
            {data.byModule.length ? (
              <ModuleBarChart data={data.byModule} />
            ) : (
              <div className="empty">No data yet</div>
            )}
            <p className="sec-title" style={{ marginTop: 20 }}>
              OCR Cost by Module
            </p>
            {data.costByModule.length ? (
              <div className="tw">
                <table>
                  <thead>
                    <tr>
                      <th>Module</th>
                      <th style={{ textAlign: 'right' }}>Documents</th>
                      <th style={{ textAlign: 'right' }}>Token</th>
                      <th style={{ textAlign: 'right' }}>Cost</th>
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
                      <td>Total</td>
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
              <div className="empty">No data yet</div>
            )}
          </div>
        </div>

        <div className="card" style={{ marginBottom: 0 }}>
          <div className="card-h">
            <h2>OCR Performance</h2>
          </div>
          <div className="card-b">
            <OcrGauge pct={data.ocrPerf.avgConfidencePct ?? 0} label="OCR Accuracy" />
            <div style={{ marginTop: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div className="row" style={{ gap: 10 }}>
                <span>⏱</span>
                <div>
                  <div className="hint">Average per Document</div>
                  <b>
                    {data.ocrPerf.avgDurationSec != null
                      ? data.ocrPerf.avgDurationSec + ' seconds'
                      : '—'}
                  </b>
                </div>
              </div>
              <div className="row" style={{ gap: 10 }}>
                <span>👤</span>
                <div>
                  <div className="hint">Edited by User</div>
                  <b>{data.ocrPerf.pctEditedByUser}%</b>
                </div>
              </div>
              <div className="row" style={{ gap: 10 }}>
                <span>📋</span>
                <div>
                  <div className="hint">Tokens Today</div>
                  <b>{intFmt(data.ocrPerf.tokensToday)}</b>
                </div>
              </div>
            </div>
          </div>
        </div>

        <div className="card" style={{ marginBottom: 0 }}>
          <div className="card-h">
            <h2>Tasks to Complete</h2>
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
                View all tasks →
              </a>
            </div>
          </div>
        </div>
      </div>

      {/* Recent documents */}
      <div className="card" style={{ marginTop: 20 }}>
        <div className="card-h">
          <h2>Recent Documents</h2>
          <div className="sp" />
          <button className="btn sm" onClick={viewAllInbox}>
            View all →
          </button>
        </div>
        <div className="card-b">
          <div className="tw">
            <table>
              <thead>
                <tr>
                  <th>Document No.</th>
                  <th>Type</th>
                  <th>Partner</th>
                  <th>Status</th>
                  <th>Last Updated</th>
                  <th>Performed By</th>
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
                            title="Open in Document Register (document opening will be ported in Phase C)"
                          >
                            Open
                          </button>
                        </td>
                      </tr>
                    );
                  })
                ) : (
                  <tr>
                    <td colSpan={7} className="empty">
                      No documents in the system yet
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
