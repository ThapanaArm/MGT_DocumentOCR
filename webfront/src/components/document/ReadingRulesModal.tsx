import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import Modal, { ModalHeader } from '../Modal';
import {
  READ_FIELD_LABELS, deleteReadRule, fetchPageImage, getReadHints, saveReadRule, setReadRuleActive,
  type Box, type ReadHintsInfo, type ReadRule,
} from '../../api/readHints';

/* Sales Order "Reading rules" (sql/39, Megachem 17 Nov 2026).
   Shows the document's pages with a box per field: the customer's rule (set by CS, or learned after a
   Zoho post) or, when there is no rule, where the AI found the value on this read. CS picks a field,
   draws / moves / resizes its box and saves it as a rule for this customer's next POs.
   Coordinates are 0-1000 of the page image: [ymin, xmin, ymax, xmax]. */

type Drag =
  | { kind: 'draw'; startX: number; startY: number }
  | { kind: 'move'; startX: number; startY: number; orig: Box }
  | { kind: 'resize'; orig: Box };

const MIN_SIZE = 8;
const clamp = (v: number) => Math.max(0, Math.min(1000, Math.round(v)));
const sameBox = (a: Box | null, b: Box | null) =>
  (a == null && b == null) || (a != null && b != null && a.every((v, i) => v === b[i]));
const label = (f: string) => READ_FIELD_LABELS[f] ?? f;

export default function ReadingRulesModal({
  docId, open, onClose, onToast,
}: {
  docId: number;
  open: boolean;
  onClose: () => void;
  onToast: (message: string, type?: 'success' | 'error' | 'info') => void;
}) {
  const [info, setInfo] = useState<ReadHintsInfo | null>(null);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const [page, setPage] = useState(1);
  const [images, setImages] = useState<Record<number, string>>({});
  const [imgErr, setImgErr] = useState('');
  const [field, setField] = useState<string | null>(null);
  const [draft, setDraft] = useState<{ page: number; box: Box } | null>(null);
  const [note, setNote] = useState('');
  const overlayRef = useRef<HTMLDivElement | null>(null);
  const dragRef = useRef<Drag | null>(null);
  const imagesRef = useRef(images);
  useEffect(() => { imagesRef.current = images; }, [images]);

  // Load the rules when the modal opens; drop page images when it closes.
  useEffect(() => {
    if (!open) return;
    let alive = true;
    setErr(''); setInfo(null); setField(null); setDraft(null); setNote(''); setPage(1);
    getReadHints(docId)
      .then((d) => { if (alive) setInfo(d); })
      .catch((e) => { if (alive) setErr(e instanceof Error ? e.message : String(e)); });
    return () => {
      alive = false;
      Object.values(imagesRef.current).forEach((u) => URL.revokeObjectURL(u));
      setImages({});
    };
  }, [open, docId]);

  // Fetch the current page image once.
  useEffect(() => {
    if (!open || !info?.fileAvailable || images[page]) return;
    let alive = true;
    setImgErr('');
    fetchPageImage(docId, page)
      .then((u) => { if (alive) setImages((m) => ({ ...m, [page]: u })); else URL.revokeObjectURL(u); })
      .catch((e) => { if (alive) setImgErr(e instanceof Error ? e.message : String(e)); });
    return () => { alive = false; };
  }, [open, info?.fileAvailable, page, docId, images]);

  const ruleOf = useCallback((f: string): ReadRule | undefined => info?.rules.find((r) => r.field === f), [info]);
  const detectedOf = useCallback((f: string) => info?.positions?.[f] ?? null, [info]);

  // Box shown for a field: its active rule's box, else where the AI found it on this read.
  const shownBox = useCallback((f: string): { page: number; box: Box; kind: 'rule' | 'detected' } | null => {
    const r = ruleOf(f);
    if (r && r.status === 'ACTIVE' && r.box && r.pageNo) return { page: r.pageNo, box: r.box, kind: 'rule' };
    if (r && r.status === 'DISABLED') return null;
    const d = detectedOf(f);
    return d ? { page: d.image, box: d.box, kind: 'detected' } : null;
  }, [ruleOf, detectedOf]);

  const pick = (f: string) => {
    setField(f);
    const r = ruleOf(f);
    const s = shownBox(f);
    setDraft(s ? { page: s.page, box: s.box } : null);
    setNote(r?.note ?? '');
    if (s && info && s.page <= Math.max(1, info.pageCount)) setPage(s.page);
  };

  const rule = field ? ruleOf(field) : undefined;
  const savedBox = rule && rule.box && rule.pageNo ? { page: rule.pageNo, box: rule.box } : null;
  const dirty = field != null && (
    !rule
      ? draft != null || note.trim().length > 0
      : !sameBox(draft?.box ?? null, savedBox?.box ?? null) || (draft?.page ?? null) !== (savedBox?.page ?? null)
        || note.trim() !== (rule.note ?? '').trim() || rule.source === 'AUTO'
  );

  // ---- drawing on the page ----
  const toUnits = (e: { clientX: number; clientY: number }) => {
    const r = overlayRef.current!.getBoundingClientRect();
    return { x: clamp(((e.clientX - r.left) / r.width) * 1000), y: clamp(((e.clientY - r.top) / r.height) * 1000) };
  };

  const onDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (!field || e.button !== 0) return;
    const t = e.target as HTMLElement;
    const p = toUnits(e);
    const mine = draft && draft.page === page ? draft.box : null;
    if (t.dataset.handle === 'resize' && mine) dragRef.current = { kind: 'resize', orig: mine };
    else if (t.dataset.mine === '1' && mine) dragRef.current = { kind: 'move', startX: p.x, startY: p.y, orig: mine };
    else {
      dragRef.current = { kind: 'draw', startX: p.x, startY: p.y };
      setDraft({ page, box: [p.y, p.x, p.y, p.x] });
    }
    overlayRef.current?.setPointerCapture(e.pointerId);
    e.preventDefault();
  };

  const onMove = (e: ReactPointerEvent<HTMLDivElement>) => {
    const d = dragRef.current;
    if (!d) return;
    const p = toUnits(e);
    if (d.kind === 'draw') {
      setDraft({ page, box: [Math.min(d.startY, p.y), Math.min(d.startX, p.x), Math.max(d.startY, p.y), Math.max(d.startX, p.x)] });
    } else if (d.kind === 'move') {
      const [y1, x1, y2, x2] = d.orig;
      const dy = Math.max(-y1, Math.min(1000 - y2, p.y - d.startY));
      const dx = Math.max(-x1, Math.min(1000 - x2, p.x - d.startX));
      setDraft({ page, box: [y1 + dy, x1 + dx, y2 + dy, x2 + dx] });
    } else {
      const [y1, x1] = d.orig;
      setDraft({ page, box: [y1, x1, Math.max(y1 + MIN_SIZE, p.y), Math.max(x1 + MIN_SIZE, p.x)] });
    }
  };

  const onUp = (e: ReactPointerEvent<HTMLDivElement>) => {
    const d = dragRef.current;
    dragRef.current = null;
    overlayRef.current?.releasePointerCapture?.(e.pointerId);
    // A click without a drag is not a box.
    if (d?.kind === 'draw' && draft && (draft.box[2] - draft.box[0] < MIN_SIZE || draft.box[3] - draft.box[1] < MIN_SIZE)) {
      setDraft(savedBox ?? (detectedOf(field!) ? { page: detectedOf(field!)!.image, box: detectedOf(field!)!.box } : null));
    }
  };

  // ---- actions ----
  const run = async (fn: () => Promise<ReadHintsInfo>, ok: string) => {
    setBusy(true);
    try {
      const d = await fn();
      setInfo(d);
      onToast(ok, 'success');
      return d;
    } catch (e) {
      onToast(e instanceof Error ? e.message : String(e), 'error');
      return null;
    } finally {
      setBusy(false);
    }
  };

  const save = async () => {
    if (!field) return;
    await run(() => saveReadRule(docId, field, { pageNo: draft?.page ?? null, box: draft?.box ?? null, note }), `${label(field)} saved for this customer`);
  };
  const toggle = async (active: boolean) => {
    if (!field) return;
    await run(() => setReadRuleActive(docId, field, active), active ? `${label(field)} is on` : `${label(field)} is off`);
  };
  const forget = async () => {
    if (!field) return;
    const d = await run(() => deleteReadRule(docId, field), `${label(field)} cleared`);
    if (d) {
      const det = d.positions?.[field];
      setDraft(det ? { page: det.image, box: det.box } : null);
      setNote('');
    }
  };

  // ---- view ----
  const fields = useMemo(() => info?.fields ?? [], [info]);
  const boxesOnPage = useMemo(() => fields
    .filter((f) => f !== field)
    .map((f) => ({ f, s: shownBox(f) }))
    .filter((x): x is { f: string; s: NonNullable<ReturnType<typeof shownBox>> } => x.s != null && x.s.page === page),
  [fields, field, shownBox, page]);

  const statusOf = (f: string) => {
    const r = ruleOf(f);
    if (r?.status === 'DISABLED') return { text: 'Off', cls: 'rr-off' };
    if (r && r.source !== 'AUTO') return { text: `Set by ${r.source}`, cls: 'rr-cs' };
    if (r) return { text: 'Learned', cls: 'rr-auto' };
    if (detectedOf(f)) return { text: 'Found on this PO', cls: 'rr-found' };
    return { text: '—', cls: 'rr-none' };
  };

  const boxStyle = (b: Box) => ({
    top: `${b[0] / 10}%`, left: `${b[1] / 10}%`, height: `${(b[2] - b[0]) / 10}%`, width: `${(b[3] - b[1]) / 10}%`,
  });

  const pageCount = Math.max(1, info?.pageCount ?? 1);

  return (
    <Modal open={open} onClose={onClose} wide>
      <ModalHeader
        title={<><i className="fa-solid fa-crosshairs" /> Reading rules{info?.customerName ? ` — ${info.customerName}` : ''}</>}
        onClose={onClose}
      />
      <div className="card-b rr">
        <style>{RR_CSS}</style>
        {err ? (
          <p className="hint">{err}</p>
        ) : !info ? (
          <p className="hint">Loading…</p>
        ) : !info.customerKey ? (
          <p className="hint">Fill the customer Tax ID or name first.</p>
        ) : (
          <>
            <p className="hint rr-sub">
              {info.customerTaxId ? `Tax ID ${info.customerTaxId} · ` : ''}Used when the next PO of this customer is read.
              {info.hintsUsed.length > 0 ? ` This PO was read with ${info.hintsUsed.length} rule(s).` : ''}
            </p>
            <div className="rr-grid">
              <div className="rr-left">
                {pageCount > 1 && (
                  <div className="rr-pages">
                    {Array.from({ length: pageCount }, (_, i) => i + 1).map((n) => (
                      <button key={n} className={'btn sm' + (n === page ? ' on' : '')} aria-pressed={n === page} onClick={() => setPage(n)}>
                        Page {n}
                      </button>
                    ))}
                  </div>
                )}
                {!info.fileAvailable ? (
                  <p className="hint">The original file is no longer on the server. Notes can still be set.</p>
                ) : imgErr ? (
                  <p className="hint">Could not load the page: {imgErr}</p>
                ) : !images[page] ? (
                  <p className="hint">Loading page {page}…</p>
                ) : (
                  <div className="rr-page"><div className="rr-canvas">
                    <img src={images[page]} alt={`Page ${page}`} draggable={false} />
                    <div
                      ref={overlayRef}
                      className={'rr-overlay' + (field ? ' drawing' : '')}
                      onPointerDown={onDown}
                      onPointerMove={onMove}
                      onPointerUp={onUp}
                    >
                      {boxesOnPage.map(({ f, s }) => (
                        <div
                          key={f}
                          className={'rr-box ' + (s.kind === 'rule' ? 'rule' : 'detected')}
                          style={boxStyle(s.box)}
                          onPointerDown={(e) => { if (!field) { e.stopPropagation(); pick(f); } }}
                          title={label(f)}
                        >
                          <span className="rr-tag">{label(f)}</span>
                        </div>
                      ))}
                      {field && draft && draft.page === page && (
                        <div className="rr-box mine" data-mine="1" style={boxStyle(draft.box)}>
                          <span className="rr-tag">{label(field)}</span>
                          <span className="rr-handle" data-handle="resize" />
                        </div>
                      )}
                    </div>
                  </div></div>
                )}
              </div>

              <div className="rr-right">
                <table className="rr-list">
                  <thead><tr><th>Field</th><th>Status</th></tr></thead>
                  <tbody>
                    {fields.map((f) => {
                      const st = statusOf(f);
                      return (
                        <tr key={f} className={f === field ? 'sel' : ''} onClick={() => pick(f)}>
                          <td>{label(f)}</td>
                          <td><span className={'rr-chip ' + st.cls}>{st.text}</span></td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>

                {field ? (
                  <div className="rr-edit">
                    <div className="rr-edit-h">{label(field)}</div>
                    <p className="hint">
                      {draft ? `Page ${draft.page}. Drag the box to move it, the corner to resize, or draw a new one.` : 'Draw a box on the page.'}
                    </p>
                    <div className="rr-row">
                      {detectedOf(field) && !sameBox(draft?.box ?? null, detectedOf(field)!.box) && (
                        <button className="btn sm" onClick={() => { const d = detectedOf(field)!; setDraft({ page: d.image, box: d.box }); setPage(d.image); }}>
                          Use where it was found
                        </button>
                      )}
                      {draft && <button className="btn sm ghost" onClick={() => setDraft(null)}>Remove box</button>}
                    </div>
                    <textarea
                      rows={2}
                      value={note}
                      maxLength={500}
                      placeholder="Note (optional), e.g. the date after “Required Date”"
                      onChange={(e) => setNote(e.target.value)}
                    />
                    <div className="rr-row">
                      <button className="btn sm primary" onClick={save} disabled={busy || !dirty || (!draft && !note.trim())}>
                        Save
                      </button>
                      {rule && (rule.status === 'ACTIVE'
                        ? <button className="btn sm" onClick={() => toggle(false)} disabled={busy}>Turn off</button>
                        : <button className="btn sm" onClick={() => toggle(true)} disabled={busy}>Turn on</button>)}
                      {!rule && (
                        <button className="btn sm" onClick={() => toggle(false)} disabled={busy} title="Never use or learn this field for this customer">
                          Turn off
                        </button>
                      )}
                      {rule && <button className="btn sm danger" onClick={forget} disabled={busy}>Clear</button>}
                    </div>
                    {rule && (
                      <p className="hint">
                        {rule.source !== 'AUTO' ? `Set by ${rule.updatedBy ?? rule.source} (${rule.source})` : 'Learned from posted POs'}
                        {rule.confirmCount > 0 ? ` · confirmed ${rule.confirmCount}×` : ''}
                        {rule.useCount > 0 ? ` · used ${rule.useCount}×` : ''}
                      </p>
                    )}
                  </div>
                ) : (
                  <p className="hint">Pick a field, then draw its box on the page.</p>
                )}
              </div>
            </div>
          </>
        )}
      </div>
    </Modal>
  );
}

const RR_CSS = `
.rr .rr-sub{margin:0 0 var(--sp-3)}
.rr .rr-grid{display:grid;grid-template-columns:minmax(0,1fr) 300px;gap:var(--sp-4);align-items:start}
.rr .rr-pages{display:flex;gap:var(--sp-2);flex-wrap:wrap;margin-bottom:var(--sp-2)}
.rr .rr-page{border:1px solid var(--line);border-radius:var(--r3);max-height:72vh;overflow:auto}
.rr .rr-canvas{position:relative;padding-top:20px;user-select:none;-webkit-user-select:none}
.rr .rr-canvas img{display:block;width:100%;user-select:none}
.rr .rr-overlay{position:absolute;left:0;right:0;bottom:0;top:20px;touch-action:none}
.rr .rr-overlay.drawing{cursor:crosshair}
.rr .rr-box{position:absolute;border:2px solid var(--brand);background:color-mix(in srgb,var(--brand) 10%,transparent)}
.rr .rr-box.detected{border-style:dashed;border-color:var(--text-3);background:transparent}
.rr .rr-box.mine{border-width:3px;border-color:var(--danger);background:color-mix(in srgb,var(--danger) 10%,transparent);cursor:move}
.rr .rr-tag{position:absolute;left:-2px;top:-20px;font-size:var(--fs-5);line-height:18px;padding:0 var(--sp-1);white-space:nowrap;
  background:var(--brand);color:#fff;border-radius:var(--r2) var(--r2) 0 0;pointer-events:none}
.rr .rr-box.detected .rr-tag{background:var(--text-3)}
.rr .rr-box.mine .rr-tag{background:var(--danger)}
.rr .rr-handle{position:absolute;right:-6px;bottom:-6px;width:12px;height:12px;background:var(--danger);border-radius:var(--r1);cursor:nwse-resize}
.rr .rr-right{min-width:0}
.rr .rr-list{width:100%;min-width:0;border-collapse:collapse}
.rr .rr-list th{text-align:left;font-size:var(--fs-5);color:var(--muted);font-weight:600;padding:var(--sp-1) var(--sp-2);border-bottom:1px solid var(--line)}
.rr .rr-list td{padding:var(--sp-2);border-bottom:1px solid var(--line-soft);cursor:pointer}
.rr .rr-list tr.sel td{background:var(--brand-soft)}
.rr .rr-chip{font-size:var(--fs-5);white-space:nowrap}
.rr .rr-cs{color:var(--brand);font-weight:600}
.rr .rr-auto{color:var(--brand)}
.rr .rr-found{color:var(--text-3)}
.rr .rr-off{color:var(--danger)}
.rr .rr-none{color:var(--muted)}
.rr .rr-edit{margin-top:var(--sp-4);display:flex;flex-direction:column;gap:var(--sp-2)}
.rr .rr-edit-h{font-weight:600;font-size:var(--fs-4)}
.rr .rr-edit .hint{margin:0}
.rr .rr-edit textarea{width:100%;resize:vertical}
.rr .rr-row{display:flex;gap:var(--sp-2);flex-wrap:wrap}
@media (max-width:900px){.rr .rr-grid{grid-template-columns:1fr}}
`;
