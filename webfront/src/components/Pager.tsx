import { useEffect, useRef, useState } from 'react';

/* Ports pagerHtml() — page-size select + prev/next. */
export default function Pager({
  page,
  setPage,
  pageSize,
  setPageSize,
  total,
}: {
  page: number;
  setPage: (n: number) => void;
  pageSize: number;
  setPageSize: (n: number) => void;
  total: number;
}) {
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const cur = Math.min(Math.max(1, page), totalPages);
  return (
    <div className="pager">
      <div className="row pager-size" style={{ gap: 6, alignItems: 'center' }}>
        <span className="hint">Show</span>
        <select
          value={pageSize}
          onChange={(e) => {
            setPageSize(parseInt(e.target.value));
            setPage(1);
          }}
        >
          {[10, 25, 50, 100].map((n) => (
            <option key={n} value={n}>
              {n}
            </option>
          ))}
        </select>
        <span className="hint pager-per-page"><span className="pager-per-page-desktop">per page</span><span className="pager-per-page-mobile">rows</span></span>
      </div>
      <div className="row pager-nav" style={{ gap: 6, alignItems: 'center' }}>
        <button className="btn sm ghost" onClick={() => setPage(Math.max(1, cur - 1))} disabled={cur <= 1}>
          <i className="fa-solid fa-angle-left" /> <span className="pager-nav-text">Previous</span>
        </button>
        <span className="hint pager-page">
          <span className="pager-page-label">Page </span>{cur} / {totalPages}
        </span>
        <button
          className="btn sm ghost"
          onClick={() => setPage(Math.min(totalPages, cur + 1))}
          disabled={cur >= totalPages}
        >
          <span className="pager-nav-text">Next</span> <i className="fa-solid fa-angle-right" />
        </button>
      </div>
    </div>
  );
}

export function paginate<T>(list: T[], page: number, pageSize: number): T[] {
  const totalPages = Math.max(1, Math.ceil(list.length / pageSize));
  const cur = Math.min(Math.max(1, page), totalPages);
  const start = (cur - 1) * pageSize;
  return list.slice(start, start + pageSize);
}

/* Ports dateRangeHtml() — from/to date inputs with clear button. */
export function DateRange({
  from,
  to,
  setFrom,
  setTo,
}: {
  from: string;
  to: string;
  setFrom: (v: string) => void;
  setTo: (v: string) => void;
}) {
  return (
    <>
      <EnglishDateInput value={from} onChange={setFrom} label="Start date" />
      <span className="hint date-range-separator">to</span>
      <EnglishDateInput value={to} onChange={setTo} label="End date" />
      {(from || to) && (
        <button
          className="btn sm ghost"
          onClick={() => {
            setFrom('');
            setTo('');
          }}
          title="Clear date range"
        >
          <i className="fa-solid fa-xmark" />
        </button>
      )}
    </>
  );
}

function formatDate(value: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  return match ? `${match[2]}/${match[3]}/${match[1]}` : '';
}

function parseDate(value: string): string | null {
  const text = value.trim();
  if (!text) return '';
  const match = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(text);
  if (!match) return null;
  const month = Number(match[1]);
  const day = Number(match[2]);
  const year = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day) return null;
  return `${String(year).padStart(4, '0')}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
}

function EnglishDateInput({ value, onChange, label }: { value: string; onChange: (value: string) => void; label: string }) {
  const [draft, setDraft] = useState('');
  const [editing, setEditing] = useState(false);
  const [open, setOpen] = useState(false);
  const initialMonth = value ? new Date(`${value}T00:00:00`) : new Date();
  const [viewMonth, setViewMonth] = useState(() => new Date(initialMonth.getFullYear(), initialMonth.getMonth(), 1));
  const rootRef = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (event: MouseEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false);
    };
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', escape);
    return () => {
      document.removeEventListener('mousedown', close);
      document.removeEventListener('keydown', escape);
    };
  }, [open]);

  const commit = () => {
    const parsed = parseDate(draft);
    setEditing(false);
    if (parsed == null) {
      return;
    }
    onChange(parsed);
  };

  return (
    <span className="english-date-input" ref={rootRef}>
      <input
        type="text"
        inputMode="numeric"
        value={editing ? draft : formatDate(value)}
        placeholder="MM/DD/YYYY"
        aria-label={label}
        title={`${label} (MM/DD/YYYY)`}
        maxLength={10}
        onFocus={() => { setDraft(formatDate(value)); setEditing(true); }}
        onChange={(event) => setDraft(event.target.value.replace(/[^0-9/]/g, ''))}
        onBlur={commit}
        onKeyDown={(event) => { if (event.key === 'Enter') event.currentTarget.blur(); }}
      />
      <button
        type="button"
        className="english-date-picker-button"
        aria-label={`Open ${label.toLowerCase()} calendar`}
        title={`Choose ${label.toLowerCase()}`}
        aria-expanded={open}
        onClick={() => {
          const selected = value ? new Date(`${value}T00:00:00`) : new Date();
          setViewMonth(new Date(selected.getFullYear(), selected.getMonth(), 1));
          setOpen((current) => !current);
        }}
      >
        <i className="fa-regular fa-calendar" aria-hidden="true" />
      </button>
      {open && (
        <EnglishCalendar
          value={value}
          viewMonth={viewMonth}
          setViewMonth={setViewMonth}
          onSelect={(next) => { onChange(next); setOpen(false); }}
          onClose={() => setOpen(false)}
        />
      )}
    </span>
  );
}

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const WEEKDAYS = ['Su', 'Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa'];

function localIso(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

function EnglishCalendar({
  value,
  viewMonth,
  setViewMonth,
  onSelect,
  onClose,
}: {
  value: string;
  viewMonth: Date;
  setViewMonth: (value: Date) => void;
  onSelect: (value: string) => void;
  onClose: () => void;
}) {
  const year = viewMonth.getFullYear();
  const month = viewMonth.getMonth();
  const firstDay = new Date(year, month, 1).getDay();
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const previousMonthDays = new Date(year, month, 0).getDate();
  const today = localIso(new Date());
  const cells = Array.from({ length: 42 }, (_, index) => {
    const day = index - firstDay + 1;
    if (day < 1) return new Date(year, month - 1, previousMonthDays + day);
    if (day > daysInMonth) return new Date(year, month + 1, day - daysInMonth);
    return new Date(year, month, day);
  });

  return (
    <div className="english-calendar" role="dialog" aria-label="Choose date">
      <div className="english-calendar-header">
        <button type="button" aria-label="Previous month" onClick={() => setViewMonth(new Date(year, month - 1, 1))}>
          <i className="fa-solid fa-chevron-left" />
        </button>
        <strong>{MONTHS[month]} {year}</strong>
        <button type="button" aria-label="Next month" onClick={() => setViewMonth(new Date(year, month + 1, 1))}>
          <i className="fa-solid fa-chevron-right" />
        </button>
      </div>
      <div className="english-calendar-weekdays">
        {WEEKDAYS.map((day) => <span key={day}>{day}</span>)}
      </div>
      <div className="english-calendar-days">
        {cells.map((date) => {
          const iso = localIso(date);
          const outside = date.getMonth() !== month;
          return (
            <button
              type="button"
              key={iso}
              className={`${outside ? 'outside ' : ''}${iso === value ? 'selected ' : ''}${iso === today ? 'today' : ''}`.trim()}
              aria-label={date.toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' })}
              aria-pressed={iso === value}
              onClick={() => onSelect(iso)}
            >
              {date.getDate()}
            </button>
          );
        })}
      </div>
      <div className="english-calendar-footer">
        <button type="button" onClick={() => { onSelect(''); onClose(); }}>Clear</button>
        <button type="button" onClick={() => onSelect(today)}>Today</button>
      </div>
    </div>
  );
}

export function inDateRange(dateStr: unknown, from: string, to: string): boolean {
  const d = String(dateStr || '').slice(0, 10);
  if (from && (!d || d < from)) return false;
  if (to && (!d || d > to)) return false;
  return true;
}
