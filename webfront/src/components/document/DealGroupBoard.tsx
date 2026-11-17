import { useMemo, useState, type CSSProperties, type DragEvent } from 'react';
import Modal, { ModalHeader } from '../Modal';
import { fmt } from '../../utils/format';
import type { DocModel } from '../../api/documents';
import type { ZohoDeal } from '../../api/zoho';
import { formatDate } from '../common/DateInput';
import {
  dealCodeChecks,
  dealItemCode,
  dealKeyLabel,
  dealOfKey,
  deliveryDateFit,
  distinctKeys,
  isNewDealKey,
  lineCodes,
  lineDeliveryDate,
  newDealKey,
  newDealNo,
} from '../../utils/zohoDealGroups';

/* "Group items into Deals" (MGT / Zoho, 16 Nov 2026). One PO may hold items of several Deals, and some
   items may have no Deal yet. A list only (Megachem dropped the card board): one section per Deal with
   its materials under it, under one column header; drag a material onto another Deal, onto "+ New Deal",
   or add another open Deal of the customer. Nothing is created here: Save writes each line's Deal
   (line.extra.zohoDeal) and Send then runs Deal -> Quote -> Sales Order(s) once per Deal. */

type Line = DocModel['lines'][number];
const ADD_NEW = '__add_new__';

export default function DealGroupBoard({
  lines,
  deals,
  assignment,
  headerDate,
  onShowDeal,
  onClose,
  onSave,
}: {
  lines: Line[];
  /** Open Deals of the customer (plus any picked through the Deal search). */
  deals: ZohoDeal[];
  /** Current Deal key of each line. */
  assignment: string[];
  /** The document's delivery date (a line without its own date uses it). */
  headerDate?: string | null;
  onShowDeal: (dealId: string) => void;
  onClose: () => void;
  onSave: (assignment: string[]) => void;
}) {
  const [assign, setAssign] = useState<string[]>(() => lines.map((_, i) => assignment[i] || newDealKey(1)));
  // Cards: the Deals in use, plus every other open Deal that carries one of this PO's materials -- so
  // two Deals of the same material (e.g. one per delivery month) sit side by side to sort rows into.
  const [cards, setCards] = useState<string[]>(() => {
    const keys = distinctKeys(lines.map((_, i) => assignment[i] || newDealKey(1)));
    const codes = new Set(lines.flatMap(lineCodes));
    const others = deals.filter((d) => !keys.includes(d.id) && d.items.some((it) => codes.has(dealItemCode(it)))).map((d) => d.id);
    const all = [...keys, ...others];
    return all.length ? all : [newDealKey(1)];
  });
  const [dragIdx, setDragIdx] = useState<number | null>(null);

  const [over, setOver] = useState<string | null>(null);

  const created = (d: ZohoDeal) => Date.parse(String(d.createdTime || '')) || 0;
  // Deals that can still be added as a card: those carrying more of this PO's materials first.
  const poCodes = useMemo(() => new Set(lines.flatMap(lineCodes)), [lines]);
  const carried = (d: ZohoDeal) => new Set(d.items.map(dealItemCode).filter((c) => poCodes.has(c))).size;
  const addable = deals
    .filter((d) => !cards.includes(d.id))
    .sort((a, b) => carried(b) - carried(a) || created(b) - created(a));

  const addNewCard = () => {
    const n = Math.max(0, ...cards.filter(isNewDealKey).map(newDealNo)) + 1;
    const key = newDealKey(n);
    setCards((c) => [...c, key]);
    return key;
  };
  const addDealCard = (id: string) => {
    if (id && !cards.includes(id)) setCards((c) => [...c, id]);
    return id;
  };
  const moveLine = (i: number, target: string) => {
    const to = target === ADD_NEW ? addNewCard() : addDealCard(target);
    if (to) setAssign((a) => a.map((k, j) => (j === i ? to : k)));
  };
  const removeCard = (key: string) => setCards((c) => c.filter((x) => x !== key));
  const cardLines = (key: string) => lines.map((_, i) => i).filter((i) => assign[i] === key);
  const used = cards.filter((k) => cardLines(k).length > 0);
  const changed = assign.some((k, i) => k !== (assignment[i] || newDealKey(1)));

  const save = () => {
    // New Deals renumbered 1, 2, ... in board order ("__new__", "__new__2", ...).
    const renum = new Map<string, string>();
    used.filter(isNewDealKey).forEach((k, n) => renum.set(k, newDealKey(n + 1)));
    onSave(assign.map((k) => renum.get(k) || k));
  };
  // The dragged row comes from the drag data (set on dragstart), so a drop never uses a stale index.
  const onDrop = (target: string, raw?: string) => {
    const fromData = raw != null && raw !== '' && Number.isInteger(Number(raw)) ? Number(raw) : null;
    const i = fromData ?? dragIdx;
    if (i != null && i >= 0 && i < lines.length) moveLine(i, target);
    setDragIdx(null);
    setOver(null);
  };

  const dropProps = (key: string) => ({
    onDragOver: (e: DragEvent) => { e.preventDefault(); if (over !== key) setOver(key); },
    onDragLeave: () => setOver((o) => (o === key ? null : o)),
    onDrop: (e: DragEvent) => { e.preventDefault(); onDrop(key, e.dataTransfer.getData('text/plain')); },
  });

  // Why an item does / doesn't fit the Deal of its card.
  const lineStatus = (i: number, key: string) => {
    const l = lines[i];
    const deal = dealOfKey(key, deals);
    if (!deal) {
      return lineCodes(l).length
        ? { tone: 'idle', text: 'New Deal — the item is looked up in Zoho Materials; Forecast is chosen on Send' }
        : { tone: 'bad', text: 'No material code on this item — map it first' };
    }
    const code = lineCodes(l).find((c) => deal.items.some((it) => dealItemCode(it) === c));
    if (!code) {
      return deal.isWcd
        ? { tone: 'bad', text: 'Not on this Deal — a Forecast (WCD) Deal only takes its own items' }
        : { tone: 'warn', text: 'Not on this Deal — sent from Zoho Materials' };
    }
    const c = dealCodeChecks(deal, cardLines(key).map((j) => lines[j])).get(code);
    if (c?.qtyOk) return { tone: 'ok', text: 'On the Deal — Material Code and Qty match' };
    const q = (v: number | null | undefined) => (v == null ? '?' : v.toLocaleString('en-US', { maximumFractionDigits: 3 }));
    return { tone: deal.isWcd ? 'warn' : 'bad', text: `Qty mismatch — PO ${q(c?.poQty)} / Deal ${q(c?.dealQty)} ${c?.unit || ''}` };
  };
  const toneStyle = (tone: string): CSSProperties =>
    tone === 'ok' ? { color: 'var(--brand)' }
    : tone === 'bad' ? { color: '#b91c1c', fontWeight: 700 }
    : tone === 'warn' ? { color: 'var(--orange, #c2410c)' }
    : {};

  const label = (key: string) => dealKeyLabel(key, deals, cards);
  const newCount = used.filter(isNewDealKey).length;

  // ── List view (simplified, 16 Nov 2026: "too much text") ──
  // Deal title: Deal Number · delivery date (tells twin Deals apart); the full name is the tooltip.
  const dealTitle = (key: string) => {
    const d = dealOfKey(key, deals);
    if (!d) return label(key);
    return [d.dealNumber, d.deliveryDate && formatDate(d.deliveryDate)].filter(Boolean).join(' · ') || d.dealName;
  };
  // One short word per item; the full reason is the tooltip.
  const shortStatus = (i: number, key: string) => {
    const st = lineStatus(i, key);
    const deal = dealOfKey(key, deals);
    const l = lines[i];
    const date = lineDeliveryDate(l, headerDate);
    const offMonth = !!deal?.deliveryDate && !!date && deliveryDateFit(l, deal, headerDate) === 0;
    const word = st.tone === 'ok' ? '' : st.text.startsWith('Qty mismatch') ? 'Qty mismatch'
      : st.text.startsWith('No material') ? 'No code' : st.text.startsWith('Not on this Deal') ? 'Not on Deal' : '';
    return { tone: word ? st.tone : offMonth ? 'warn' : st.tone, word: word || (offMonth ? 'Other month' : ''), title: st.text + (offMonth ? ' · not the Deal\'s delivery month' : '') };
  };
  // Columns (Megachem, 16 Nov 2026): Material Code, Material Name, Delivery Date, Qty, Check. Items are
  // moved by dragging only (the small per-row move menu was removed).
  const rowGrid = '16px minmax(130px, 170px) minmax(160px, 1fr) 104px 120px 110px';
  const colHead: CSSProperties = { fontSize: 14, fontWeight: 700, color: 'var(--muted)', textTransform: 'uppercase', letterSpacing: '.04em' };

  const listView = (
    <div style={{ maxHeight: '58vh', overflow: 'auto', display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div style={{
        display: 'grid', gridTemplateColumns: rowGrid, gap: 12, padding: '8px 12px', position: 'sticky', top: 0, zIndex: 1,
        background: 'var(--line-soft, #f2f4f7)', borderRadius: 8,
      }}>
        <span />
        <span style={colHead}>Material Code</span>
        <span style={colHead}>Material Name</span>
        <span style={colHead}>Delivery Date</span>
        <span style={{ ...colHead, textAlign: 'right' }}>Qty</span>
        <span style={colHead}>Check</span>
      </div>
      {cards.map((key) => {
        const deal = dealOfKey(key, deals);
        const idx = cardLines(key);
        return (
          <section key={key} {...dropProps(key)} aria-label={`Deal ${label(key)}`}
            style={{
              border: `1px ${deal ? 'solid' : 'dashed'} ${over === key ? 'var(--blue, #2f6fed)' : 'var(--line, #d8dde6)'}`,
              borderRadius: 10, background: over === key ? 'var(--hover, #eef4ff)' : 'var(--card, #fff)',
            }}>
            <header title={deal?.dealName} style={{
              display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px',
              background: deal ? 'var(--brand-soft)' : 'transparent', borderRadius: '10px 10px 0 0',
            }}>
              <b>{isNewDealKey(key) && <i className="fa-solid fa-plus" />} {dealTitle(key)}</b>
              {deal?.isWcd && <span className="badge b-ok">WCD</span>}
              <span style={{ flex: 1 }} />
              <span className="hint">{idx.length}</span>
              {deal && (
                <button type="button" className="btn sm ghost" title="Deal details" aria-label="Deal details" onClick={() => onShowDeal(deal.id)}>
                  <i className="fa-solid fa-circle-info" />
                </button>
              )}
              {idx.length === 0 && cards.length > 1 && (
                <button type="button" className="btn sm ghost" title="Remove" aria-label="Remove this empty Deal" onClick={() => removeCard(key)}>
                  <i className="fa-solid fa-xmark" />
                </button>
              )}
            </header>
            {idx.length === 0 && (
              <div className="hint" style={{ padding: '8px 12px', textAlign: 'center' }}>Drop here</div>
            )}
            {idx.map((i) => {
              const l = lines[i];
              const st = shortStatus(i, key);
              const date = lineDeliveryDate(l, headerDate);
              return (
                <div key={i} draggable title={st.title}
                  onDragStart={(e) => { setDragIdx(i); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(i)); }}
                  onDragEnd={() => { setDragIdx(null); setOver(null); }}
                  style={{
                    display: 'grid', gridTemplateColumns: rowGrid, alignItems: 'center', gap: 12, padding: '8px 12px',
                    borderTop: '1px solid var(--line-soft, #eef0f4)', cursor: 'grab', opacity: dragIdx === i ? 0.5 : 1,
                  }}>
                  <i className="fa-solid fa-grip-vertical hint" aria-hidden="true" />
                  <b style={{ minWidth: 0, overflowWrap: 'anywhere' }}>{l.materialCode || l.extCode || '—'}</b>
                  <span style={{ minWidth: 0, overflowWrap: 'anywhere' }}>{l.desc || '—'}</span>
                  <span style={{ whiteSpace: 'nowrap' }}>{date ? formatDate(date) : '—'}</span>
                  <span style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>{fmt(l.qty)} {l.uom}</span>
                  <span style={{ ...toneStyle(st.tone), fontSize: 14, whiteSpace: 'nowrap' }}>
                    {st.tone === 'ok' ? <i className="fa-solid fa-check" aria-label="OK" /> : st.word}
                  </span>
                </div>
              );
            })}
          </section>
        );
      })}
      <div style={{ display: 'flex', gap: 8, alignItems: 'stretch', flexWrap: 'wrap' }}>
        <div {...dropProps(ADD_NEW)} role="button" tabIndex={0} onClick={() => addNewCard()}
          onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); addNewCard(); } }}
          style={{
            flex: '1 1 260px', padding: '12px 12px', borderRadius: 10, textAlign: 'center', cursor: 'pointer',
            border: `2px dashed ${over === ADD_NEW ? 'var(--blue, #2f6fed)' : 'var(--line, #c9d0db)'}`,
            background: over === ADD_NEW ? 'var(--hover, #eef4ff)' : 'transparent',
          }}>
          <i className="fa-solid fa-plus" /> New Deal <span className="hint">(or drop here)</span>
        </div>
        {/* Another open Deal of the customer (e.g. one picked through "Find another Deal…") as an empty section. */}
        {addable.length > 0 && (
          <select value="" onChange={(e) => addDealCard(e.target.value)} aria-label="Add another open Deal"
            style={{ flex: '0 1 300px', fontSize: 14, padding: '8px 12px', minHeight: 44 }}>
            <option value="">+ Add an open Deal…</option>
            {addable.map((d) => <option key={d.id} value={d.id}>{dealTitle(d.id)} · {d.dealName}</option>)}
          </select>
        )}
      </div>
    </div>
  );

  return (
    <Modal open onClose={onClose} wide>
      <ModalHeader title="Group items into Deals" onClose={onClose} />
      <div className="card-b">
        <p className="hint" style={{ margin: '0 0 12px' }}>Drag each item to its Deal.</p>

        {listView}

        <div className="row" style={{ marginTop: 8, alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
          <span>
            <b>{used.length}</b> Deal{used.length === 1 ? '' : 's'}{newCount > 0 && <> · <b>{newCount}</b> new</>}
          </span>
          <div className="sp" style={{ flex: 1 }} />
          <button className="btn" onClick={onClose}>Cancel</button>
          <button className="btn primary" onClick={save} disabled={!changed}>Save</button>
        </div>
      </div>
    </Modal>
  );
}
