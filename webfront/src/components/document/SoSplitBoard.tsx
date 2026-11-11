import { useMemo, useState, type CSSProperties } from 'react';
import Modal, { ModalHeader } from '../Modal';
import { fmt, num } from '../../utils/format';
import type { DocModel } from '../../api/documents';
import { lineSoGroup } from './SapSalesOrderEditor';
import { isFreightLine } from '../../utils/soCharges';
import { formatDate } from '../common/DateInput';
import { shipToValue, type GroupShipToOption } from './SoGroupPanel';

/* "Split Sales Order" board (12 Oct 2026). One card per Sales Order: drag items between cards (or use
   each item's "Move to" menu -- keyboard / touch friendly), add a new card, and pick a Ship-to per
   card. Nothing is created here: Save writes each line's SO Group (extra.soGroup) and the card
   ship-tos (header.soGroupShipTos), and Step 3 sends one Sales Order per card and delivery date
   (SoDeliveryGroups). Every item sits in exactly one card, so no item can be sent twice. */

type ShipTos = Record<string, Record<string, unknown>>;

const NEW_CARD = '__new__';

function shipToText(d: Record<string, unknown>) {
  const code = String(d.code ?? '').trim();
  const rest = String(d.name ?? '').trim() || String(d.address ?? '').trim();
  return [code, rest].filter(Boolean).join(' — ') || '(no code)';
}

export default function SoSplitBoard({
  doc,
  headerDate,
  shipToOptions,
  shipToLoading,
  shipToError,
  docShipToLabel,
  canPickShipTo,
  onClose,
  onSave,
  skipCharges = false,
}: {
  doc: DocModel;
  /** GLC: freight lines are not items, so they get no card (their amount is set per Sales Order in Step 3). */
  skipCharges?: boolean;
  headerDate: string;
  shipToOptions: GroupShipToOption[];
  shipToLoading?: boolean;
  shipToError?: string | null;
  /** The document's own ship-to, used by a card without its own pick. */
  docShipToLabel: string;
  /** False until the customer is matched (Step 2) -- there is no ship-to list to pick from yet. */
  canPickShipTo: boolean;
  onClose: () => void;
  /** groups[i] = SO Group of line i ("1", "2", ... contiguous, in card order); shipTos keyed the same way. */
  onSave: (groups: string[], shipTos: ShipTos) => void;
}) {
  const lines = doc.lines || [];
  const initialCards = useMemo(() => {
    const ids = Array.from(new Set(lines.filter((l) => !(skipCharges && isFreightLine(l))).map((l) => lineSoGroup(l))));
    ids.sort((a, b) => Number(a) - Number(b));
    return ids.length ? ids : ['1'];
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const [cards, setCards] = useState<string[]>(initialCards);
  const [assign, setAssign] = useState<string[]>(() => lines.map((l) => lineSoGroup(l)));
  const [shipTos, setShipTos] = useState<ShipTos>(() => {
    const saved = (doc.header.soGroupShipTos as ShipTos) || {};
    const out: ShipTos = {};
    for (const id of initialCards) if (saved[id]) out[id] = saved[id];
    return out;
  });
  const [dragIdx, setDragIdx] = useState<number | null>(null);
  const [overCard, setOverCard] = useState<string | null>(null);

  const nextId = () => String(Math.max(0, ...cards.map(Number)) + 1);
  const addCard = () => {
    const id = nextId();
    setCards((c) => [...c, id]);
    return id;
  };
  const moveLine = (i: number, target: string) => {
    const to = target === NEW_CARD ? addCard() : target;
    setAssign((a) => a.map((g, j) => (j === i ? to : g)));
  };
  const removeCard = (id: string) => {
    setCards((c) => c.filter((x) => x !== id));
    setShipTos((s) => {
      const n = { ...s };
      delete n[id];
      return n;
    });
  };
  const setCardShipTo = (id: string, value: string) =>
    setShipTos((s) => {
      const n = { ...s };
      const opt = shipToOptions.find((o) => o.value === value);
      if (!value || !opt) delete n[id];
      else n[id] = opt.data;
      return n;
    });

  const dateOf = (i: number) =>
    ((lines[i].extra as Record<string, string> | undefined)?.deliveryDate || '').trim() || (headerDate || '').trim();
  const cardLines = (id: string) =>
    lines.map((_, i) => i).filter((i) => assign[i] === id && !(skipCharges && isFreightLine(lines[i])));
  const cardDates = (id: string) => Array.from(new Set(cardLines(id).map(dateOf)));
  const usedCards = cards.filter((id) => cardLines(id).length > 0);
  const totalOrders = usedCards.reduce((n, id) => n + cardDates(id).length, 0);
  const changed =
    assign.some((g, i) => g !== lineSoGroup(lines[i])) ||
    JSON.stringify(shipTos) !== JSON.stringify((doc.header.soGroupShipTos as ShipTos) || {});

  const save = () => {
    // Renumber the cards that still hold items 1, 2, 3 ... in board order.
    const renum = new Map<string, string>();
    usedCards.forEach((id, k) => renum.set(id, String(k + 1)));
    const groups = assign.map((g) => renum.get(g) || '1');
    const outShipTos: ShipTos = {};
    for (const [id, data] of Object.entries(shipTos)) {
      const n = renum.get(id);
      if (n) outShipTos[n] = data;
    }
    onSave(groups, outShipTos);
  };

  const onDrop = (id: string) => {
    if (dragIdx != null) moveLine(dragIdx, id);
    setDragIdx(null);
    setOverCard(null);
  };

  const cardStyle = (id: string): CSSProperties => ({
    flex: '0 0 300px',
    minWidth: 260,
    border: '1px solid var(--line, #d8dde6)',
    borderRadius: 10,
    padding: 12,
    background: overCard === id ? 'var(--hover, #eef4ff)' : 'var(--card, #fff)',
    outline: overCard === id ? '2px dashed var(--blue, #2f6fed)' : 'none',
    display: 'flex',
    flexDirection: 'column',
    gap: 8,
  });

  return (
    <Modal open onClose={onClose} wide>
      <ModalHeader title="Split Sales Order" onClose={onClose} />
      <div className="card-b">
        <p className="hint" style={{ marginTop: 0 }}>
          Drag items between Sales Orders, or use each item's <b>Move to</b> menu. Click <b>+ New Sales Order</b> to
          add one. Each Sales Order can have its own Ship-to. Items with different delivery dates inside one Sales
          Order are still sent as separate orders (one per date).
        </p>
        {shipToError && <div className="hint" style={{ color: 'var(--red)' }}>Could not load the ship-to list: {shipToError}</div>}
        {!canPickShipTo && <div className="hint">Run Step 2 — Data Mapping first to choose a Ship-to per Sales Order.</div>}

        <div style={{ display: 'flex', gap: 12, overflowX: 'auto', padding: '8px 2px 12px', alignItems: 'flex-start' }}>
          {cards.map((id, k) => {
            const idx = cardLines(id);
            const dates = cardDates(id);
            const amount = idx.reduce((s, i) => s + num(lines[i].amount), 0);
            const picked = shipTos[id];
            const pickedValue = shipToValue(picked);
            const pickedKnown = !picked || shipToOptions.some((o) => o.value === pickedValue);
            return (
              <div
                key={id}
                style={cardStyle(id)}
                onDragOver={(e) => { e.preventDefault(); if (overCard !== id) setOverCard(id); }}
                onDragLeave={() => setOverCard((c) => (c === id ? null : c))}
                onDrop={(e) => { e.preventDefault(); onDrop(id); }}
              >
                <div className="row" style={{ alignItems: 'center', gap: 8 }}>
                  <b>Sales Order {k + 1}</b>
                  <span className="hint">· {idx.length} item{idx.length === 1 ? '' : 's'} · {fmt(amount)}</span>
                  <div className="sp" style={{ flex: 1 }} />
                  {idx.length === 0 && cards.length > 1 && (
                    <button className="btn sm ghost" title="Remove this empty Sales Order" onClick={() => removeCard(id)}>
                      <i className="fa-solid fa-xmark" />
                    </button>
                  )}
                </div>
                <label className="hint" style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                  Ship-to
                  <select
                    value={pickedValue}
                    disabled={!canPickShipTo || shipToLoading}
                    onChange={(e) => setCardShipTo(id, e.target.value)}
                    aria-label={`Ship-to for Sales Order ${k + 1}`}
                  >
                    <option value="">{docShipToLabel ? `Same as document (${docShipToLabel})` : 'Same as document'}</option>
                    {!pickedKnown && picked && <option value={pickedValue}>{shipToText(picked)}</option>}
                    {shipToOptions.map((o) => (
                      <option key={o.value} value={o.value}>{o.label}</option>
                    ))}
                  </select>
                </label>
                {dates.length > 1 && (
                  <div className="badge b-warn" style={{ whiteSpace: 'normal' }}>
                    <i className="fa-solid fa-calendar-days" /> {dates.length} delivery dates — will be sent as {dates.length} Sales Orders
                  </div>
                )}
                {idx.length === 0 && (
                  <div className="hint" style={{ border: '1px dashed var(--line, #d8dde6)', borderRadius: 8, padding: 16, textAlign: 'center' }}>
                    Drag items here
                  </div>
                )}
                {idx.map((i) => {
                  const l = lines[i];
                  return (
                    <div
                      key={i}
                      draggable
                      onDragStart={(e) => { setDragIdx(i); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(i)); }}
                      onDragEnd={() => { setDragIdx(null); setOverCard(null); }}
                      style={{
                        border: '1px solid var(--line, #d8dde6)',
                        borderRadius: 8,
                        padding: '8px 10px',
                        background: 'var(--bg, #fafbfc)',
                        cursor: 'grab',
                        opacity: dragIdx === i ? 0.5 : 1,
                      }}
                    >
                      <div className="row" style={{ gap: 6, alignItems: 'flex-start' }}>
                        <i className="fa-solid fa-grip-vertical hint" aria-hidden="true" style={{ marginTop: 3 }} />
                        <div style={{ flex: 1, minWidth: 0 }}>
                          <div style={{ fontWeight: 600, overflowWrap: 'anywhere' }}>
                            {l.itemNo} · {l.desc || l.extCode || '(no description)'}
                          </div>
                          <div className="hint">
                            {fmt(l.qty)} {l.uom} · {dateOf(i) ? formatDate(dateOf(i)) : 'no delivery date'}
                          </div>
                        </div>
                      </div>
                      <select
                        value=""
                        onChange={(e) => { if (e.target.value) moveLine(i, e.target.value); }}
                        aria-label={`Move item ${l.itemNo} to another Sales Order`}
                        style={{ marginTop: 6, width: '100%', fontSize: 12, padding: '4px 8px', minHeight: 0, height: 30 }}
                      >
                        <option value="">Move to…</option>
                        {cards.filter((c) => c !== id).map((c) => (
                          <option key={c} value={c}>Sales Order {cards.indexOf(c) + 1}</option>
                        ))}
                        <option value={NEW_CARD}>+ New Sales Order</option>
                      </select>
                    </div>
                  );
                })}
              </div>
            );
          })}
          <div
            role="button"
            tabIndex={0}
            style={{
              flex: '0 0 180px',
              minHeight: 140,
              border: '2px dashed var(--line, #c9d0db)',
              borderRadius: 10,
              display: 'flex',
              flexDirection: 'column',
              alignItems: 'center',
              justifyContent: 'center',
              gap: 4,
              cursor: 'pointer',
              textAlign: 'center',
              padding: 12,
              background: overCard === NEW_CARD ? 'var(--hover, #eef4ff)' : 'transparent',
            }}
            onClick={() => addCard()}
            onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); addCard(); } }}
            onDragOver={(e) => { e.preventDefault(); if (overCard !== NEW_CARD) setOverCard(NEW_CARD); }}
            onDragLeave={() => setOverCard((c) => (c === NEW_CARD ? null : c))}
            onDrop={(e) => { e.preventDefault(); onDrop(NEW_CARD); }}
          >
            <b><i className="fa-solid fa-plus" /> New Sales Order</b>
            <span className="hint">or drop an item here</span>
          </div>
        </div>

        <div className="row" style={{ marginTop: 8, alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
          <span>
            Will create <b>{totalOrders}</b> Sales Order{totalOrders === 1 ? '' : 's'}
            {totalOrders !== usedCards.length && <span className="hint"> (some cards have more than one delivery date)</span>}
          </span>
          <div className="sp" style={{ flex: 1 }} />
          <button className="btn" onClick={onClose}>Cancel</button>
          <button className="btn primary" onClick={save} disabled={!changed}>Save</button>
        </div>
      </div>
    </Modal>
  );
}
