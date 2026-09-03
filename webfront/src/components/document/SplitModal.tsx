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
      <ModalHeader title="แยกเอกสารเป็นหลาย Sales Order" onClose={onClose} />
      <div className="card-b">
        <p className="hint">
          ใส่หมายเลขกลุ่ม (1, 2, 3, ...) ให้แต่ละรายการที่จะแยกออกไปเป็น Sales Order ใหม่ —
          รายการที่เว้นว่างจะไม่ถูกแยก ต้องมีอย่างน้อย 2 กลุ่มจึงจะ Split ได้
        </p>
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th>Item</th>
                <th>รายการ</th>
                <th>จำนวน</th>
                <th style={{ textAlign: 'right' }}>จำนวนเงิน</th>
                <th>กลุ่ม SO</th>
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
              จะสร้าง <b>{nGroups}</b> Sales Order ใหม่ (
              {Object.entries(groups)
                .map(([g, items]) => `กลุ่ม ${g}: ${items.length} รายการ`)
                .join(' · ')}
              )
            </>
          ) : (
            <span style={{ color: 'var(--red)' }}>ต้องแบ่งอย่างน้อย 2 กลุ่ม</span>
          )}
        </div>
        <div className="row" style={{ marginTop: 16, justifyContent: 'flex-end' }}>
          <button className="btn primary" onClick={confirm} disabled={nGroups < 2}>
            ยืนยัน Split
          </button>
        </div>
      </div>
    </Modal>
  );
}
