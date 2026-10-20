import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

/* One date field for the whole app (15 Oct 2026): always shown the same way, in English and the
   Gregorian calendar -- dd/MM/yyyy, e.g. 20/09/2026 -- whatever language or calendar the browser
   or Windows is set to (the browser's own <input type="date"> follows the PC's locale, so on a
   Thai PC it showed Thai month names / Buddhist years). The VALUE stays ISO YYYY-MM-DD, which is
   what OCR returns, what is stored, and what the backend turns into SAP's date format.
   Type a date (20/09/2026, 20-9-2026, 2026-09-20; a Buddhist year like 2569 is converted) or pick
   it from the calendar. */

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const WEEKDAYS = ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su'];
const pad = (n: number) => String(n).padStart(2, '0');

function valid(y: number, m: number, d: number) {
  if (y > 2400) y -= 543; // Buddhist year
  const dt = new Date(Date.UTC(y, m - 1, d));
  return dt.getUTCFullYear() === y && dt.getUTCMonth() === m - 1 && dt.getUTCDate() === d ? `${y}-${pad(m)}-${pad(d)}` : null;
}

/** Any date the documents / people write -> ISO YYYY-MM-DD; '' for empty; null when unreadable. */
export function toIsoDate(raw: unknown): string | null {
  const s = (raw ?? '').toString().trim();
  if (!s) return '';
  let m = s.match(/^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})/);
  if (m) return valid(+m[1], +m[2], +m[3]);
  m = s.match(/^(\d{1,2})[-/. ](\d{1,2})[-/. ](\d{4})$/);
  if (m) return valid(+m[3], +m[2], +m[1]);
  return null;
}

/** ISO YYYY-MM-DD -> dd/MM/yyyy for display ('' stays ''; anything else is shown as it is). */
export function formatDate(raw: unknown): string {
  const iso = toIsoDate(raw);
  if (iso === null) return String(raw ?? '');
  if (!iso) return '';
  const [y, m, d] = iso.split('-');
  return `${d}/${m}/${y}`;
}

export default function DateInput({
  value,
  onChange,
  disabled,
  width,
  placeholder = 'dd/mm/yyyy',
  clearable = true,
  ariaLabel,
}: {
  value: string | null | undefined;
  /** Always called with ISO YYYY-MM-DD, or '' when cleared. */
  onChange: (iso: string) => void;
  disabled?: boolean;
  width?: number | string;
  placeholder?: string;
  clearable?: boolean;
  ariaLabel?: string;
}) {
  const iso = toIsoDate(value);
  const shown = iso === null ? String(value ?? '') : formatDate(iso);
  const [text, setText] = useState(shown);
  const [editing, setEditing] = useState(false);
  const [open, setOpen] = useState(false);
  const today = new Date();
  const [view, setView] = useState(() => {
    const base = iso ? new Date(iso + 'T00:00:00') : today;
    return { y: base.getFullYear(), m: base.getMonth() };
  });
  const boxRef = useRef<HTMLSpanElement>(null);
  const popRef = useRef<HTMLDivElement>(null);
  // The calendar is drawn on document.body (fixed position), not inside the field: cards and tables
  // clip their overflow, which cut the calendar off. Placed under the field, or above it when there
  // isn't room below, and kept inside the window horizontally.
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);
  const POP_W = 252;
  const POP_H = 300;
  const place = () => {
    const r = boxRef.current?.getBoundingClientRect();
    if (!r) return;
    const below = window.innerHeight - r.bottom;
    const top = below >= POP_H + 8 || r.top < POP_H + 8 ? r.bottom + 4 : r.top - POP_H - 4;
    const left = Math.min(Math.max(8, r.right - POP_W), window.innerWidth - POP_W - 8);
    setPos({ top: Math.max(8, top), left });
  };
  useLayoutEffect(() => {
    if (!open) return;
    place();
    window.addEventListener('scroll', place, true);
    window.addEventListener('resize', place);
    return () => {
      window.removeEventListener('scroll', place, true);
      window.removeEventListener('resize', place);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  useEffect(() => { if (!editing) setText(shown); }, [shown, editing]);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => {
      const t = e.target as Node;
      if (boxRef.current?.contains(t) || popRef.current?.contains(t)) return;
      setOpen(false);
    };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  const commitText = () => {
    setEditing(false);
    const t = text.trim();
    if (!t) { if (iso) onChange(''); return; }
    const next = toIsoDate(t);
    if (next && next !== iso) onChange(next);
    else setText(shown); // unreadable -> back to the stored value
  };
  const pick = (y: number, m: number, d: number) => {
    onChange(`${y}-${pad(m + 1)}-${pad(d)}`);
    setOpen(false);
  };
  const openCalendar = () => {
    const base = iso ? new Date(iso + 'T00:00:00') : today;
    setView({ y: base.getFullYear(), m: base.getMonth() });
    setOpen((o) => !o);
  };
  const shift = (delta: number) => setView((v) => {
    const m = v.m + delta;
    return { y: v.y + Math.floor(m / 12), m: ((m % 12) + 12) % 12 };
  });

  // Monday-first month grid.
  const first = new Date(view.y, view.m, 1);
  const lead = (first.getDay() + 6) % 7;
  const days = new Date(view.y, view.m + 1, 0).getDate();
  const cells: (number | null)[] = [...Array(lead).fill(null), ...Array.from({ length: days }, (_, i) => i + 1)];
  while (cells.length % 7) cells.push(null);
  const isSel = (d: number) => iso === `${view.y}-${pad(view.m + 1)}-${pad(d)}`;
  const isToday = (d: number) => today.getFullYear() === view.y && today.getMonth() === view.m && today.getDate() === d;

  return (
    <span ref={boxRef} style={{ position: 'relative', display: 'inline-flex', gap: 4, alignItems: 'center', width: width ?? '100%' }}>
      <input
        type="text"
        value={text}
        disabled={disabled}
        placeholder={placeholder}
        aria-label={ariaLabel}
        onFocus={() => setEditing(true)}
        onChange={(e) => setText(e.target.value)}
        onBlur={commitText}
        onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); if (e.key === 'Escape') setOpen(false); }}
        style={{ flex: 1, minWidth: 0 }}
      />
      {!disabled && (
        <button type="button" className="btn sm" title="Pick a date" aria-label="Pick a date" onClick={openCalendar}>
          <i className="fa-regular fa-calendar" />
        </button>
      )}
      {!disabled && clearable && !!iso && (
        <button type="button" className="btn sm" title="Clear date" aria-label="Clear date" onClick={() => onChange('')}>
          <i className="fa-solid fa-xmark" />
        </button>
      )}
      {open && pos && createPortal(
        <div
          ref={popRef}
          role="dialog"
          aria-label="Calendar"
          onKeyDown={(e) => { if (e.key === 'Escape') setOpen(false); }}
          style={{
            position: 'fixed', top: pos.top, left: pos.left, zIndex: 2000, padding: 10, width: POP_W, boxSizing: 'border-box',
            background: 'var(--card, #fff)', border: '1px solid var(--line, #d9dfdb)', borderRadius: 10,
            boxShadow: '0 8px 24px rgba(0,0,0,.12)',
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 6 }}>
            <button type="button" className="btn sm" aria-label="Previous month" onClick={() => shift(-1)}>‹</button>
            <b style={{ fontSize: 13 }}>{MONTHS[view.m]} {view.y}</b>
            <button type="button" className="btn sm" aria-label="Next month" onClick={() => shift(1)}>›</button>
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, 1fr)', gap: 2, textAlign: 'center', fontSize: 12 }}>
            {WEEKDAYS.map((w) => <div key={w} style={{ color: 'var(--muted, #6b7a70)', padding: '2px 0' }}>{w}</div>)}
            {cells.map((d, i) => d == null ? <div key={i} /> : (
              <button
                key={i}
                type="button"
                onClick={() => pick(view.y, view.m, d)}
                style={{
                  padding: '5px 0', borderRadius: 6, cursor: 'pointer', fontSize: 12,
                  border: isToday(d) ? '1px solid var(--brand, #00994d)' : '1px solid transparent',
                  background: isSel(d) ? 'var(--brand, #00994d)' : 'transparent',
                  color: isSel(d) ? '#fff' : 'inherit',
                }}
              >
                {d}
              </button>
            ))}
          </div>
          <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 8 }}>
            <button type="button" className="btn sm" onClick={() => pick(today.getFullYear(), today.getMonth(), today.getDate())}>Today</button>
            {clearable && <button type="button" className="btn sm" onClick={() => { onChange(''); setOpen(false); }}>Clear</button>}
          </div>
        </div>,
        document.body,
      )}
    </span>
  );
}
