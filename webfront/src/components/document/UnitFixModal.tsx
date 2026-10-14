import { useMemo, useState } from 'react';
import Modal, { ModalHeader } from '../Modal';
import { fmt, num } from '../../utils/format';
import type { SapUnitChoice } from '../../hooks/useSapUnits';

/* "Change unit" popup (GLC Sales Order, 12 Oct 2026). Opened when SAP doesn't know the unit on a
   line for its material (SAP rejects the order with "Sales unit KG is not defined for item 000010").
   The person picks one of the material's own SAP units and says how many of the line's current
   unit make one of the new unit ("1 DR = 50 KG"); quantity and unit price are converted together
   (350 KG x 500 -> 7 DR x 25,000), so the line amount stays the same. */

export interface UnitFixTarget {
  index: number;
  itemNo: unknown;
  desc: string;
  materialCode: string;
  uom: string;
  qty: number;
  price: number;
  amount: number;
  units: SapUnitChoice[];
}

const round = (n: number, d: number) => Math.round(n * 10 ** d) / 10 ** d;

export default function UnitFixModal({
  target,
  remaining,
  suggest,
  onApply,
  onClose,
}: {
  target: UnitFixTarget;
  /** How many more lines need the same fix after this one (shown as a hint). */
  remaining: number;
  /** Known "1 <new unit> = n <current unit>" from the Unit Conversion master, if any. */
  suggest: (newUnit: string) => number | undefined;
  onApply: (fix: { uom: string; qty: number; price: number; amount: number }) => void;
  onClose: () => void;
}) {
  const first = target.units.find((u) => u.isBase) ?? target.units[0];
  const [unit, setUnit] = useState(first?.unit ?? '');
  const [factorText, setFactorText] = useState(() => {
    const s = first ? suggest(first.unit) : undefined;
    return s ? String(s) : '';
  });
  const pick = (u: string) => {
    setUnit(u);
    const s = suggest(u);
    setFactorText(s ? String(s) : '');
  };
  const factor = num(factorText);
  const valid = !!unit && factor > 0;
  const preview = useMemo(() => {
    if (!valid) return null;
    return { qty: round(target.qty / factor, 3), price: round(target.price * factor, 4) };
  }, [valid, target.qty, target.price, factor]);

  return (
    <Modal open onClose={onClose}>
      <ModalHeader title={`Change unit — item ${String(target.itemNo)}`} onClose={onClose} />
      <div className="card-b">
        <p style={{ marginTop: 0 }}>
          SAP doesn&apos;t know the unit <b>{target.uom || '(none)'}</b> for material <b>{target.materialCode}</b>
          {target.desc ? <> ({target.desc})</> : null}. Pick the unit SAP sells it in:
        </p>
        <div className="row" style={{ gap: 8, flexWrap: 'wrap', marginBottom: 12 }}>
          {target.units.map((u) => (
            <button
              key={u.unit}
              type="button"
              className={'btn sm ' + (u.unit === unit ? 'primary' : '')}
              onClick={() => pick(u.unit)}
              title={u.hint || (u.isBase ? 'Base unit' : '')}
            >
              {u.unit}
              {/* Same colour as the button text (white on the selected green button, dark on the
                  others) -- the grey .hint style was unreadable on the selected button. */}
              {u.isBase && <span style={{ marginLeft: 6, fontWeight: 500, opacity: 0.9 }}>(base)</span>}
              {u.hint && <span style={{ marginLeft: 6, fontWeight: 500, opacity: 0.9 }}>{u.hint}</span>}
            </button>
          ))}
        </div>
        <label className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
          <span>1 <b>{unit || '?'}</b> =</span>
          <input
            type="text"
            inputMode="decimal"
            value={factorText}
            onChange={(e) => setFactorText(e.target.value)}
            placeholder="e.g. 50"
            style={{ width: 110 }}
            aria-label={`How many ${target.uom} in one ${unit}`}
          />
          <span><b>{target.uom || '?'}</b></span>
        </label>
        <div className="hint" style={{ marginTop: 6 }}>
          Quantity is divided and the unit price multiplied by this number, so the line amount stays the same.
          Use 1 if only the unit name differs.
        </div>
        <div className="so-group-notice" style={{ marginTop: 12 }}>
          {preview ? (
            <span>
              {fmt(target.qty)} {target.uom} × {fmt(target.price)} = {fmt(target.amount)}
              {'  →  '}
              <b>{preview.qty} {unit} × {fmt(preview.price)}</b> = {fmt(target.amount)}
            </span>
          ) : (
            <span>Enter how many {target.uom || 'units'} make one {unit || 'new unit'}.</span>
          )}
        </div>
        <div className="row" style={{ marginTop: 16, gap: 8, alignItems: 'center' }}>
          {remaining > 0 && <span className="hint">{remaining} more item{remaining === 1 ? '' : 's'} after this one</span>}
          <div className="sp" style={{ flex: 1 }} />
          <button className="btn" onClick={onClose}>Later</button>
          <button
            className="btn primary"
            disabled={!preview}
            onClick={() => preview && onApply({ uom: unit, qty: preview.qty, price: preview.price, amount: target.amount })}
          >
            Change unit
          </button>
        </div>
      </div>
    </Modal>
  );
}
