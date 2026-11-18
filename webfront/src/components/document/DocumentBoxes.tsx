import React, { useEffect, useMemo, useRef, useState } from 'react';
import { api, fetchBlobUrl } from '../../api/client';
import type { DocLine } from '../../api/documents';
import { fmtAmt } from '../../utils/format';

/* "View document" with frames (AP, 2026-11-18): every page of the original file as a picture, and
   over it a frame around each value the read took from it — the header fields from
   header._positions and each line from extra._box, both "<page>:<ymin>,<xmin>,<ymax>,<xmax>" on a
   0-1000 grid, as Gemini reported them. When a supplier changes its form, the frames show at once
   which box each value came from. Positions are recorded only by reads made after this was added. */

const FIELD_LABELS: Record<string, string> = {
  invoiceNo: 'Invoice No.',
  invoiceDate: 'Invoice Date',
  vendorName: 'Vendor',
  vendorTaxId: 'Tax ID',
  poRef: 'PO',
  currency: 'Currency',
  subTotal: 'Amount before tax',
  vatAmount: 'VAT',
  totalAmount: 'Total',
  goodsSupplier: 'Goods Supplier',
  goodsInvoiceNo: 'Goods Inv. No.',
  linesTable: 'ตารางรายการ',
};

interface Frame {
  key: string;
  page: number;
  box: [number, number, number, number]; // ymin, xmin, ymax, xmax (0-1000)
  label: string;
  value: string;
  kind: 'header' | 'line' | 'table';
}

function parseBox(v: unknown): { page: number; box: [number, number, number, number] } | null {
  const m = /^(\d+):(\d+),(\d+),(\d+),(\d+)$/.exec(String(v ?? '').trim());
  if (!m) return null;
  const n = m.slice(2).map(Number) as [number, number, number, number];
  if (n[2] <= n[0] || n[3] <= n[1]) return null;
  return { page: Number(m[1]), box: n };
}

/** Whether this document has any frames to draw. */
export function hasDocumentFrames(header: Record<string, any>, lines: DocLine[]): boolean {
  const pos = header?._positions;
  if (pos && typeof pos === 'object' && Object.keys(pos).length > 0) return true;
  return (lines || []).some((l) => !!parseBox((l.extra as Record<string, unknown> | undefined)?._box));
}

const COLORS = { header: '#1f6feb', line: '#1a7f37', table: '#d97706' };

function PageImage({ docId, page, onSize }: { docId: number; page: number; onSize?: () => void }) {
  const holder = useRef<HTMLDivElement>(null);
  const [url, setUrl] = useState<string | null>(null);
  const [err, setErr] = useState('');
  const [visible, setVisible] = useState(false);
  useEffect(() => {
    const el = holder.current;
    if (!el) return;
    const io = new IntersectionObserver((es) => { if (es.some((e) => e.isIntersecting)) setVisible(true); }, { rootMargin: '600px' });
    io.observe(el);
    return () => io.disconnect();
  }, []);
  useEffect(() => {
    if (!visible) return;
    let alive = true;
    let u: string | null = null;
    fetchBlobUrl(`/api/documents/${docId}/page/${page}`)
      .then((x) => { u = x; if (alive) setUrl(x); else URL.revokeObjectURL(x); })
      .catch((e) => alive && setErr(e?.message || 'load failed'));
    return () => { alive = false; if (u) URL.revokeObjectURL(u); };
  }, [visible, docId, page]);
  return (
    <div ref={holder} style={{ minHeight: url ? undefined : 600 }}>
      {url ? (
        <img src={url} alt={`page ${page}`} onLoad={onSize} style={{ width: '100%', display: 'block' }} />
      ) : (
        <div className="hint" style={{ padding: 16 }}>{err ? `โหลดหน้า ${page} ไม่ได้: ${err}` : `กำลังโหลดหน้า ${page}…`}</div>
      )}
    </div>
  );
}

type Box = [number, number, number, number];
const fmtBox = (page: number, b: Box) => `${page}:${b.map((v) => Math.round(v)).join(',')}`;

export default function DocumentBoxes({ docId, header, lines, onMove, onReadRegion }: {
  docId: number;
  header: Record<string, any>;
  lines: DocLine[];
  /** A frame dragged, resized or moved to another page: key "h:<field>" or "l:<line index>",
   *  value "<page>:<ymin>,<xmin>,<ymax>,<xmax>". Without it the frames are read-only. */
  onMove?: (key: string, value: string) => void;
  /** Read the value again from inside a header field's frame (after it was moved). */
  onReadRegion?: (field: string, page: number, box: [number, number, number, number]) => Promise<void>;
}) {
  const [reading, setReading] = useState('');
  // The frame being dragged: its box follows the pointer here and is handed to onMove on release.
  const [dragBox, setDragBox] = useState<{ key: string; box: Box } | null>(null);
  const drag = useRef<{ key: string; page: number; mode: 'move' | 'size'; x: number; y: number; w: number; h: number; box: Box } | null>(null);
  const [count, setCount] = useState(0);
  const [failed, setFailed] = useState('');
  const [active, setActive] = useState('');
  const [showAll, setShowAll] = useState(false);
  const pageRefs = useRef<Record<number, HTMLDivElement | null>>({});

  useEffect(() => {
    api.get<{ count: number }>(`/api/documents/${docId}/pages`)
      .then((r) => setCount(r.count || 0))
      .catch((e) => setFailed(e?.message || String(e)));
  }, [docId]);

  const frames = useMemo<Frame[]>(() => {
    const out: Frame[] = [];
    const pos = (header?._positions ?? {}) as Record<string, unknown>;
    Object.entries(pos).forEach(([k, v]) => {
      const b = parseBox(v);
      if (!b) return;
      const raw = header?.[k];
      out.push({
        key: `h:${k}`, page: b.page, box: b.box, label: FIELD_LABELS[k] ?? k,
        value: k === 'linesTable' ? `${(lines || []).length} แถว` : typeof raw === 'number' ? fmtAmt(raw) : String(raw ?? ''),
        kind: k === 'linesTable' ? 'table' : 'header',
      });
    });
    (lines || []).forEach((l, i) => {
      const b = parseBox((l.extra as Record<string, unknown> | undefined)?._box);
      if (!b) return;
      out.push({
        key: `l:${i}`, page: b.page, box: b.box, label: `แถว ${i + 1}`,
        value: `${String(l.desc ?? '').trim()} · ${fmtAmt(l.amount)}`, kind: 'line',
      });
    });
    return out;
  }, [header, lines]);

  const pagesWithFrames = useMemo(() => [...new Set(frames.map((f) => f.page))].sort((a, b) => a - b), [frames]);
  const pages = showAll || pagesWithFrames.length === 0
    ? Array.from({ length: count }, (_, i) => i + 1)
    : pagesWithFrames.filter((p) => p <= (count || p));

  const startDrag = (e: React.PointerEvent, f: Frame, mode: 'move' | 'size') => {
    if (!onMove) return;
    e.preventDefault();
    e.stopPropagation();
    const holder = pageRefs.current[f.page];
    if (!holder) return;
    const r = holder.getBoundingClientRect();
    drag.current = { key: f.key, page: f.page, mode, x: e.clientX, y: e.clientY, w: r.width, h: r.height, box: f.box };
    setActive(f.key);
    setDragBox({ key: f.key, box: f.box });
    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
  };
  const onDrag = (e: React.PointerEvent) => {
    const d = drag.current;
    if (!d) return;
    const dy = ((e.clientY - d.y) / d.h) * 1000;
    const dx = ((e.clientX - d.x) / d.w) * 1000;
    const [y1, x1, y2, x2] = d.box;
    let b: Box;
    if (d.mode === 'move') {
      const hh = y2 - y1; const ww = x2 - x1;
      const ny = Math.min(Math.max(y1 + dy, 0), 1000 - hh);
      const nx = Math.min(Math.max(x1 + dx, 0), 1000 - ww);
      b = [ny, nx, ny + hh, nx + ww];
    } else {
      b = [y1, x1, Math.min(Math.max(y2 + dy, y1 + 8), 1000), Math.min(Math.max(x2 + dx, x1 + 8), 1000)];
    }
    setDragBox({ key: d.key, box: b });
  };
  const endDrag = () => {
    const d = drag.current;
    drag.current = null;
    if (!d || !dragBox || !onMove) { setDragBox(null); return; }
    const moved = dragBox.box.some((v, i) => Math.abs(v - d.box[i]) >= 1);
    if (moved) onMove(d.key, fmtBox(d.page, dragBox.box));
    setDragBox(null);
  };
  const toPage = (f: Frame, page: number) => {
    if (!onMove || page === f.page) return;
    onMove(f.key, fmtBox(page, f.box));
    setActive(f.key);
    setTimeout(() => pageRefs.current[page]?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 50);
  };

  const jump = (f: Frame) => {
    setActive(f.key);
    pageRefs.current[f.page]?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  };

  if (failed) return <p className="hint">เปิดรูปหน้าเอกสารไม่ได้: {failed}</p>;

  return (
    <div style={{ display: 'flex', gap: 12, alignItems: 'stretch' }}>
      <div style={{ width: 280, flex: '0 0 280px', maxHeight: '78vh', overflowY: 'auto', fontSize: 12 }}>
        <div className="hint" style={{ marginBottom: 6 }}>
          กรอบ = ตำแหน่งที่ระบบอ่านค่ามา · คลิกเพื่อไปที่กรอบ
          {onMove && <> · <b>ลากกรอบ</b>เพื่อย้าย ลาก<b>มุมขวาล่าง</b>เพื่อปรับขนาด กด <b>อ่านค่าใหม่จากกรอบนี้</b> ให้ค่าเปลี่ยนตามกรอบ แล้วกด Save</>}
        </div>
        {frames.length === 0 && <div className="hint">เอกสารนี้ยังไม่มีตำแหน่ง — กด Re-read เพื่ออ่านใหม่พร้อมตำแหน่ง</div>}
        {frames.map((f) => (
          <button
            key={f.key}
            type="button"
            onClick={() => jump(f)}
            style={{
              display: 'block', width: '100%', textAlign: 'left', padding: '4px 6px', marginBottom: 2,
              border: '1px solid ' + (active === f.key ? COLORS[f.kind] : 'transparent'),
              borderLeft: `3px solid ${COLORS[f.kind]}`, borderRadius: 4, background: active === f.key ? 'var(--bg-subtle, #f5f5f5)' : 'transparent',
              cursor: 'pointer',
            }}
          >
            <b>{f.label}</b> <span className="hint">หน้า {f.page}</span>
            <div style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{f.value || '—'}</div>
            {onReadRegion && active === f.key && f.kind === 'header' && (
              <div onClick={(e) => e.stopPropagation()} style={{ marginTop: 4 }}>
                <span
                  role="button"
                  className="btn sm"
                  style={{ pointerEvents: reading ? 'none' : undefined, opacity: reading ? 0.6 : 1 }}
                  onClick={async () => {
                    setReading(f.key);
                    try { await onReadRegion(f.key.slice(2), f.page, f.box); } finally { setReading(''); }
                  }}
                >
                  <i className="fa-solid fa-wand-magic-sparkles" /> {reading === f.key ? 'กำลังอ่าน…' : 'อ่านค่าใหม่จากกรอบนี้'}
                </span>
              </div>
            )}
            {onMove && active === f.key && count > 1 && (
              <div onClick={(e) => e.stopPropagation()} style={{ marginTop: 4 }}>
                ย้ายไปหน้า{' '}
                <select value={f.page} onChange={(e) => toPage(f, Number(e.target.value))}>
                  {Array.from({ length: count }, (_, i) => i + 1).map((n) => <option key={n} value={n}>{n}</option>)}
                </select>
              </div>
            )}
          </button>
        ))}
        {count > 0 && pagesWithFrames.length > 0 && (
          <label style={{ display: 'block', marginTop: 8 }}>
            <input type="checkbox" checked={showAll} onChange={(e) => setShowAll(e.target.checked)} /> แสดงทุกหน้า ({count})
          </label>
        )}
      </div>
      <div style={{ flex: 1, maxHeight: '78vh', overflowY: 'auto', background: 'var(--bg-subtle, #f0f0f0)', padding: 8, borderRadius: 6 }}>
        {pages.map((p) => (
          <div
            key={p}
            ref={(el) => { pageRefs.current[p] = el; }}
            style={{ position: 'relative', marginBottom: 12, background: '#fff', boxShadow: '0 1px 3px rgba(0,0,0,.2)' }}
          >
            <div className="hint" style={{ position: 'absolute', top: 4, right: 8, zIndex: 3, background: 'rgba(255,255,255,.85)', padding: '0 6px', borderRadius: 4 }}>
              หน้า {p}
            </div>
            <PageImage docId={docId} page={p} />
            {frames.filter((f) => f.page === p).map((f) => {
              const [y1, x1, y2, x2] = dragBox?.key === f.key ? dragBox.box : f.box;
              const on = active === f.key;
              return (
                <div
                  key={f.key}
                  onClick={() => setActive(f.key)}
                  onPointerDown={(e) => startDrag(e, f, 'move')}
                  onPointerMove={onDrag}
                  onPointerUp={endDrag}
                  onPointerCancel={endDrag}
                  title={`${f.label}: ${f.value}`}
                  style={{
                    position: 'absolute', top: `${y1 / 10}%`, left: `${x1 / 10}%`,
                    height: `${(y2 - y1) / 10}%`, width: `${(x2 - x1) / 10}%`,
                    border: `${on ? 3 : 2}px ${f.kind === 'table' ? 'dashed' : 'solid'} ${COLORS[f.kind]}`,
                    background: on ? `${COLORS[f.kind]}22` : 'transparent', borderRadius: 2,
                    cursor: onMove ? 'move' : 'pointer', zIndex: on ? 2 : 1, touchAction: 'none', userSelect: 'none',
                  }}
                >
                  {onMove && on && (
                    <span
                      onPointerDown={(e) => startDrag(e, f, 'size')}
                      onPointerMove={onDrag}
                      onPointerUp={endDrag}
                      title="ลากเพื่อปรับขนาด"
                      style={{
                        position: 'absolute', right: -6, bottom: -6, width: 12, height: 12, background: COLORS[f.kind],
                        border: '2px solid #fff', borderRadius: 2, cursor: 'nwse-resize',
                      }}
                    />
                  )}
                  <span style={{
                    position: 'absolute', top: -16, left: -2, fontSize: 10, lineHeight: '14px', padding: '0 4px',
                    background: COLORS[f.kind], color: '#fff', borderRadius: 2, whiteSpace: 'nowrap',
                    maxWidth: 260, overflow: 'hidden', textOverflow: 'ellipsis',
                  }}>
                    {f.label}{f.kind !== 'table' && f.value ? `: ${f.value}` : ''}
                  </span>
                </div>
              );
            })}
          </div>
        ))}
      </div>
    </div>
  );
}
