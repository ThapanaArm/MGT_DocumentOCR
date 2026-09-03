import { useState } from 'react';
import Modal, { ModalHeader } from '../Modal';
import { fmt } from '../../utils/format';
import type { DocModel } from '../../api/documents';

/* Ports openSplitModal()/updateSplitSummary()/confirmSplit(). */
export default function SplitModal({
  doc,
  onClose,
  onConfirm,
}: {
  doc: DocModel;
  onClose: () => void;
  onConfirm: (assign: Record<string, number>) => void;
}) {
  const [assign, setAssign] = useState<Record<string, string>>({});

  const groups: Record<number, string[]> = {};
  Object.entries(assign).forEach(([itemNo, g]) => {
    const gi = parseInt(g);
    if (gi > 0) (groups[gi] = groups[gi] || []).push(itemNo);
  });
  const nGroups = Object.keys(groups).length;

  function confirm() {
    const out: Record<string, number> = {};
    Object.entries(assign).forEach(([itemNo, g]) => {
      const gi = parseInt(g);
      if (gi > 0) out[itemNo] = gi;
    });
    if (new Set(Object.values(out)).size < 2) return;
    onConfirm(out);
  }

  return (
    <Modal open onClose={onClose} wide>
      <ModalHeader title="Split Document into Multiple Sales Orders" onClose={onClose} />
      <div className="card-b">
        <p className="hint">
          Enter a group number (1, 2, 3, ...) for each item to be split into a new Sales Order —
          items left blank will not be split. At least 2 groups are required to Split.
        </p>
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th>Item</th>
                <th>Description</th>
                <th>Quantity</th>
                <th style={{ textAlign: 'right' }}>Amount</th>
                <th>SO Group</th>
              </tr>
            </thead>
            <tbody>
              {doc.lines.map((l) => (
                <tr key={l.itemNo}>
                  <td style={{ textAlign: 'center' }}>{l.itemNo}</td>
                  <td>{l.desc}</td>
                  <td className="num">
                    {fmt(l.qty)} {l.uom}
                  </td>
                  <td className="num">{fmt(l.amount)}</td>
                  <td>
                    <input
                      type="number"
                      min={1}
                      step={1}
                      style={{ width: 70 }}
                      placeholder="—"
                      value={assign[l.itemNo] ?? ''}
                      onChange={(e) =>
                        setAssign((a) => ({ ...a, [l.itemNo]: e.target.value }))
                      }
                    />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="hint" style={{ marginTop: 12 }}>
          {nGroups >= 2 ? (
            <>
              Will create <b>{nGroups}</b> new Sales Orders (
              {Object.entries(groups)
                .map(([g, items]) => `Group ${g}: ${items.length} items`)
                .join(' · ')}
              )
            </>
          ) : (
            <span style={{ color: 'var(--red)' }}>At least 2 groups are required</span>
          )}
        </div>
        <div className="row" style={{ marginTop: 16, justifyContent: 'flex-end' }}>
          <button className="btn primary" onClick={confirm} disabled={nGroups < 2}>
            Confirm Split
          </button>
        </div>
      </div>
    </Modal>
  );
}
