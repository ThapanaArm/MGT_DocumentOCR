import { useEffect, useState } from 'react';
import type { SapUnitChoice } from '../../hooks/useSapUnits';

/* Unit-of-measure picker: a dropdown of the units SAP has for the material, with an
   "Other…" escape hatch to type a unit by hand (SAP down, material not mapped yet, or a rule
   whose document unit is a Thai label). With no SAP units it is just the old text box, so it
   never makes a screen worse than before.
   The current value is always kept: if it isn't one of SAP's units it shows as
   "<value> (current — not in SAP)" so nothing is silently swapped. */

export default function UomPicker({
  value,
  onChange,
  units,
  loading,
  disabled,
  id,
  maxLength,
  width,
  upper = true,
  emptyLabel = '— Select unit —',
}: {
  value: string;
  onChange: (v: string) => void;
  units: SapUnitChoice[];
  loading?: boolean;
  disabled?: boolean;
  id?: string;
  maxLength?: number;
  width?: number | string;
  /** upper-case what gets picked/typed (SAP codes are upper-case). */
  upper?: boolean;
  emptyLabel?: string;
}) {
  const v = (value ?? '').toString();
  const inList = units.some((u) => u.unit.toUpperCase() === v.trim().toUpperCase());
  const [custom, setCustom] = useState(false);
  // Leave "Other…" mode automatically when the units list (material) changes.
  useEffect(() => { setCustom(false); }, [units]);

  const style = width != null ? { width } : undefined;
  const norm = (s: string) => (upper ? s.toUpperCase() : s);

  if (!units.length || custom) {
    return (
      <span style={{ display: 'inline-flex', gap: 4, alignItems: 'center', ...(style || {}) }}>
        <input
          id={id}
          value={v}
          readOnly={disabled}
          maxLength={maxLength}
          placeholder={loading ? 'Loading SAP units…' : undefined}
          onChange={(e) => onChange(norm(e.target.value))}
          style={{ flex: 1, minWidth: 0 }}
        />
        {custom && units.length > 0 && !disabled && (
          <button type="button" className="btn sm" title="Pick from SAP units" onClick={() => setCustom(false)}>
            <i className="fa-solid fa-list" />
          </button>
        )}
      </span>
    );
  }

  return (
    <select
      id={id}
      value={inList ? units.find((u) => u.unit.toUpperCase() === v.trim().toUpperCase())!.unit : v}
      disabled={disabled}
      style={style}
      title="Units from SAP for this material"
      onChange={(e) => {
        if (e.target.value === '__other__') { setCustom(true); return; }
        onChange(e.target.value);
      }}
    >
      {!v && <option value="">{emptyLabel}</option>}
      {v && !inList && <option value={v}>{v} (current — not in SAP)</option>}
      {units.map((u) => (
        <option key={u.unit} value={u.unit}>
          {u.unit}{u.isBase ? ' (base)' : ''}{u.hint ? ` · ${u.hint}` : ''}
        </option>
      ))}
      <option value="__other__">Other… (type a unit)</option>
    </select>
  );
}
