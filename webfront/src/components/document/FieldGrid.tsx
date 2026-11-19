import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { fmtAmt, num } from '../../utils/format';
import type { FieldDef } from '../../constants/fields';
import DateInput, { toIsoDate } from '../common/DateInput';

/* Renders a grid of header fields (text, select, checkbox or date). `numeric` switches to the
   0,000.## amount formatting used by the totals blocks (raw on focus). */

/* A one-line text field that opens to show its whole value (Megachem, 17 Nov 2026). Every box in the
   grid stays the same one-line height; a value too long for the box ends in "…" and shows in full on
   hover (title). Click / focus it and a box with the whole value opens over the fields below it
   (layout does not move); leaving it closes it again. Still a single-line value: Enter does not add a
   line break, a pasted line break becomes a space, Esc closes. */
function ExpandText({ value, readOnly, onChange }: { value: string; readOnly?: boolean; onChange: (v: string) => void }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLTextAreaElement>(null);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!open || !el) return;
    el.style.height = 'auto';
    el.style.height = `${el.scrollHeight + 2}px`; // + top/bottom border
  }, [open, value]);
  useEffect(() => {
    const el = ref.current;
    if (!open || !el) return;
    el.focus();
    el.setSelectionRange(el.value.length, el.value.length);
  }, [open]);
  const clean = (v: string) => v.replace(/\r?\n/g, ' ');
  return (
    <div className="f-expand">
      <input
        type="text"
        value={value}
        readOnly={readOnly}
        title={value || undefined}
        tabIndex={open ? -1 : 0}
        onFocus={() => setOpen(true)}
        onChange={(e) => onChange(e.target.value)}
      />
      {open && (
        <textarea
          ref={ref}
          rows={1}
          className="f-expand-pop"
          value={value}
          readOnly={readOnly}
          onChange={(e) => onChange(clean(e.target.value))}
          onBlur={() => setOpen(false)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') e.preventDefault();
            if (e.key === 'Escape') e.currentTarget.blur();
          }}
        />
      )}
    </div>
  );
}

export default function FieldGrid({
  fields,
  values,
  posted,
  numeric,
  required,
  onEdit,
}: {
  fields: FieldDef[];
  values: Record<string, any>;
  posted?: boolean;
  numeric?: boolean;
  required?: string[]; // keys shown with a required (*) marker
  onEdit: (key: string, value: string) => void;
}) {
  return (
    <div className="grid">
      {fields.map((f) => {
        const [k, label, type, opts] = f;
        const raw = values[k];
        const isReq = required?.includes(k);
        return (
          <div className="f" key={k}>
            <label>
              {label}
              {isReq && <span style={{ color: 'var(--danger, #e5484d)', marginLeft: 4 }}>*</span>}
            </label>
            {type === 'select' ? (
              <select
                disabled={posted}
                value={raw == null ? '' : String(raw)}
                onChange={(e) => onEdit(k, e.target.value)}
              >
                {(opts as [string, string][]).map(([v, t]) => (
                  <option key={v} value={v}>
                    {t}
                  </option>
                ))}
              </select>
            ) : type === 'checkbox' ? (
              <div style={{ display: 'flex', alignItems: 'center', minHeight: 43 }}>
                <input
                  type="checkbox"
                  disabled={posted}
                  checked={raw === 'X' || raw === true || raw === 'true'}
                  onChange={(e) => onEdit(k, e.target.checked ? 'X' : '')}
                  style={{ width: 18, height: 18, accentColor: 'var(--brand)', cursor: posted ? 'default' : 'pointer' }}
                />
              </div>
            ) : type === 'date' ? (
              // The app's one date field: shown dd/MM/yyyy in English, stored/sent as YYYY-MM-DD.
              <>
                <DateInput value={raw == null ? '' : String(raw)} disabled={posted} onChange={(v) => onEdit(k, v)} ariaLabel={label} />
                {toIsoDate(raw) === null && (
                  <small className="hint">Read from document: "{String(raw)}" — pick the date</small>
                )}
              </>
            ) : numeric ? (
              <input
                type="text"
                defaultValue={fmtAmt(raw)}
                readOnly={posted}
                onFocus={(e) => (e.target.value = String(num(e.target.value)))}
                onBlur={(e) => (e.target.value = fmtAmt(e.target.value))}
                onInput={(e) => onEdit(k, (e.target as HTMLInputElement).value)}
              />
            ) : (
              <ExpandText
                value={raw == null ? '' : String(raw)}
                readOnly={posted}
                onChange={(v) => onEdit(k, v)}
              />
            )}
          </div>
        );
      })}
    </div>
  );
}
