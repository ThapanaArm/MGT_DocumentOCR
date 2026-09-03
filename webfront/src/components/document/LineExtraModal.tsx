import Modal, { ModalHeader } from '../Modal';
import { AA_GUIDE, PO_LINE_EXTRA_FIELDS } from '../../constants/fields';
import type { DocLine } from '../../api/documents';

/* Ports showLineExtra() — the per-line PO detail editor (AP module). */
export default function LineExtraModal({
  line,
  onClose,
  onEdit,
}: {
  line: DocLine;
  onClose: () => void;
  onEdit: (key: string, value: string) => void;
}) {
  const ex = line.extra || {};
  const guide = AA_GUIDE[ex.accountAssignment || ''] || AA_GUIDE[''];

  return (
    <Modal open onClose={onClose} wide>
      <ModalHeader
        title={`📋 Additional PO Details — Item ${line.itemNo} (${line.desc || ''})`}
        onClose={onClose}
      />
      <div className="card-b">
        <p
          className="hint"
          style={{ margin: '-4px 0 16px', padding: '8px 12px', background: 'var(--line-soft)', borderRadius: 'var(--r2)' }}
        >
          💡 {guide.hint}
        </p>
        <div className="grid">
          {PO_LINE_EXTRA_FIELDS.map((f) => {
            const [k, label, type, opts] = f;
            const isRelevant = k === guide.field;
            return (
              <div
                className="f"
                key={k}
                style={isRelevant ? { border: '1.5px solid var(--brand)', borderRadius: 'var(--r3)', padding: 8 } : undefined}
              >
                <label>
                  {label}
                  {isRelevant && <span style={{ color: 'var(--brand)' }}> ★ Recommended</span>}
                </label>
                {type === 'select' ? (
                  <select value={ex[k] || ''} onChange={(e) => onEdit(k, e.target.value)}>
                    {(opts as [string, string][]).map(([v, t]) => (
                      <option key={v} value={v}>
                        {t}
                      </option>
                    ))}
                  </select>
                ) : (
                  <input value={ex[k] || ''} onChange={(e) => onEdit(k, e.target.value)} />
                )}
              </div>
            );
          })}
        </div>
        <div className="row" style={{ marginTop: 16, justifyContent: 'flex-end' }}>
          <button className="btn primary" onClick={onClose}>
            Done
          </button>
        </div>
      </div>
    </Modal>
  );
}
