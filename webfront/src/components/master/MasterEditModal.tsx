import { useState } from 'react';
import Modal, { ModalHeader } from '../Modal';
import { MASTER_DEF, M_LABEL } from '../../constants/fields';
import { createMaster, updateMaster, type MasterRow, type MastersData } from '../../api/masters';
import { useAppState } from '../../state/AppState';
import type { Dupe } from '../../utils/dupes';

/* Ports editRow()/saveRow()/useDupe() — the add/edit master row modal, shared
   by MasterPage and the document quick-add flow. */

export interface MasterEditState {
  tab: string;
  rowKey: string | number | null;
  prefill?: MasterRow;
  dupes?: Dupe[];
  /** called with the saved row's key after a successful save */
  onSaved?: (savedKey: string) => void | Promise<void>;
  /** called when the user picks an existing duplicate instead */
  onUseDupe?: (code: string) => void;
}

export default function MasterEditModal({
  state,
  masters,
  onClose,
  afterSave,
}: {
  state: MasterEditState | null;
  masters: MastersData;
  onClose: () => void;
  afterSave: () => void | Promise<void>;
}) {
  const { guard, showToast } = useAppState();
  const [form, setForm] = useState<MasterRow>({});
  const [initedFor, setInitedFor] = useState<string | null>(null);

  if (!state) return null;
  const def = MASTER_DEF[state.tab];
  const editing = state.rowKey != null;
  const existing = editing
    ? masters[state.tab].find((x) => String(x[def.key]) === String(state.rowKey)) || {}
    : state.prefill || {};

  // Initialise form once per opened state.
  const stateSig = state.tab + ':' + String(state.rowKey);
  if (initedFor !== stateSig) {
    setForm({ ...existing });
    setInitedFor(stateSig);
  }

  const setField = (k: string, v: string) => setForm((f) => ({ ...f, [k]: v }));

  async function save() {
    const o: MasterRow = {};
    def.cols.forEach((c) => {
      o[c.k] = (form[c.k] ?? '').toString().trim();
    });
    if (state!.rowKey == null && def.key !== 'Id' && !o[def.key]) {
      showToast('Please enter ' + def.cols.find((c) => c.k === def.key)?.l);
      return;
    }
    const ok = await guard(async () => {
      if (state!.rowKey == null) await createMaster(state!.tab, o);
      else await updateMaster(state!.tab, String(state!.rowKey), o);
      return true;
    });
    if (!ok) return;
    if (state!.onSaved) {
      await state!.onSaved(o[def.key]);
    } else {
      showToast('✓ Master data saved successfully');
    }
    await afterSave();
    onClose();
  }

  function useDupe(code: string) {
    onClose();
    state!.onUseDupe?.(code);
  }

  const hasDupes = !!state.dupes && state.dupes.length > 0;

  return (
    <Modal open onClose={onClose}>
      <ModalHeader
        title={`${state.rowKey == null ? 'Add' : 'Edit'} — ${def.label}`}
        onClose={onClose}
      />
      <div className="card-b">
        {hasDupes && (
          <div className="result bad" style={{ marginBottom: 16 }}>
            <h3>⚠ Found {state.dupes!.length} possibly duplicate records</h3>
            <p className="hint" style={{ margin: '0 0 10px' }}>
              Please review before adding a new record — if it is the same record, click "Use this record instead" rather than creating a duplicate
            </p>
            <div className="tw">
              <table style={{ minWidth: 'auto' }}>
                <tbody>
                  {state.dupes!.map((x, i) => (
                    <tr key={i}>
                      <td>
                        <b>{x.row[def.key]}</b> — {x.row[M_LABEL[state.tab]] || ''}
                      </td>
                      <td>
                        <span className="badge b-warn">{x.reason}</span>
                      </td>
                      <td>
                        <button
                          className="btn sm primary"
                          onClick={() => useDupe(String(x.row[def.key]))}
                        >
                          Use this record instead
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
        <div className="grid">
          {def.cols.map((c) => (
            <div className="f" key={c.k}>
              <label>{c.l}</label>
              {c.ref ? (
                <select value={form[c.k] ?? ''} onChange={(e) => setField(c.k, e.target.value)}>
                  {c.blank && <option value="">— All materials (global rule) —</option>}
                  {masters[c.ref].map((o) => {
                    const vk = MASTER_DEF[c.ref!].key;
                    const lk = M_LABEL[c.ref!];
                    return (
                      <option key={o[vk]} value={o[vk]}>
                        {o[vk]} — {o[lk]}
                      </option>
                    );
                  })}
                </select>
              ) : (
                <input value={form[c.k] ?? ''} onChange={(e) => setField(c.k, e.target.value)} />
              )}
            </div>
          ))}
        </div>
        <div className="row" style={{ marginTop: 18 }}>
          <button className="btn primary" onClick={save}>
            {hasDupes ? 'Confirm — Save as New Record' : 'Save'}
          </button>
          <button className="btn" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </Modal>
  );
}
