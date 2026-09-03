import { fmtAmt, num } from '../../utils/format';
import type { FieldDef } from '../../constants/fields';

/* Renders a grid of header fields (text or select). `numeric` switches to the
   0,000.## amount formatting used by the totals blocks (raw on focus). */
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
              <input
                type="text"
                value={raw == null ? '' : String(raw)}
                readOnly={posted}
                onChange={(e) => onEdit(k, e.target.value)}
              />
            )}
          </div>
        );
      })}
    </div>
  );
}
