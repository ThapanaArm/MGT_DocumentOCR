import { useMemo, useState } from 'react';
import Modal, { ModalHeader } from '../Modal';
import { GL_ACCOUNT_NAMES } from '../../constants/glAccounts';

/* Value help for G/L acct, laid out like SAP's "General Ledger Account" dialog in MIRO
   (Search and Select: G/L Account / Chart of Accounts, then G/L Acct · Long Text).
   Several conditions combine: account number, long text, account group and a free
   "Find expression" over both. */

const GROUPS: Array<[string, string]> = [
  ['', 'All'],
  ['1', '1 · Assets'],
  ['2', '2 · Liabilities'],
  ['3', '3 · Equity'],
  ['4', '4 · Revenue'],
  ['5', '5 · Cost of sales / variance'],
  ['6', '6 · Selling & admin expenses'],
  ['7', '7 · Other expenses'],
  ['8', '8 · Finance cost & tax'],
  ['9', '9 · Group reporting'],
];

const ALL = Object.entries(GL_ACCOUNT_NAMES);

export default function GlAccountHelp({
  open,
  initial,
  onPick,
  onClose,
}: {
  open: boolean;
  initial?: string;
  onPick: (acct: string) => void;
  onClose: () => void;
}) {
  const [acct, setAcct] = useState('');
  const [text, setText] = useState('');
  const [group, setGroup] = useState('');
  const [find, setFind] = useState('');
  const [sel, setSel] = useState<string>(String(initial ?? '').trim());

  const rows = useMemo(() => {
    const a = acct.trim().replace(/\*/g, '');
    const t = text.trim().toLowerCase();
    const f = find.trim().toLowerCase();
    return ALL.filter(([no, name]) =>
      (!a || no.includes(a)) &&
      (!t || name.toLowerCase().includes(t)) &&
      (!group || no.startsWith(group)) &&
      (!f || no.includes(f) || name.toLowerCase().includes(f)));
  }, [acct, text, group, find]);

  const choose = (no: string) => {
    if (!no) return;
    onPick(no);
    onClose();
  };

  return (
    <Modal open={open} onClose={onClose} wide>
      <ModalHeader title={`General Ledger Account (${rows.length})`} onClose={onClose} />
      <div className="card-b">
        <div style={{ fontWeight: 600, marginBottom: 10 }}>Search and Select · Chart of Accounts YCOA</div>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: 12, marginBottom: 14 }}>
          <label>
            <div className="muted" style={{ fontSize: 12 }}>G/L Account</div>
            <input value={acct} onChange={(e) => setAcct(e.target.value)} placeholder="เช่น 1171" autoFocus />
          </label>
          <label>
            <div className="muted" style={{ fontSize: 12 }}>Long Text</div>
            <input value={text} onChange={(e) => setText(e.target.value)} placeholder="เช่น Prepayment" />
          </label>
          <label>
            <div className="muted" style={{ fontSize: 12 }}>Account Group</div>
            <select value={group} onChange={(e) => setGroup(e.target.value)}>
              {GROUPS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
            </select>
          </label>
          <label>
            <div className="muted" style={{ fontSize: 12 }}>Find expression</div>
            <input value={find} onChange={(e) => setFind(e.target.value)} placeholder="เลขหรือชื่อ" />
          </label>
        </div>
        <div className="row" style={{ marginBottom: 8 }}>
          <b>Items ({rows.length})</b>
          <div className="sp" />
          {(acct || text || group || find) && (
            <button className="btn sm ghost" onClick={() => { setAcct(''); setText(''); setGroup(''); setFind(''); }}>
              ล้างเงื่อนไข
            </button>
          )}
        </div>
        <div className="tw" style={{ maxHeight: 380, overflow: 'auto' }}>
          <table>
            <thead>
              <tr>
                <th style={{ width: 140 }}>G/L Acct</th>
                <th>Long Text</th>
              </tr>
            </thead>
            <tbody>
              {rows.length ? rows.map(([no, name]) => (
                <tr
                  key={no}
                  onClick={() => setSel(no)}
                  onDoubleClick={() => choose(no)}
                  style={{ cursor: 'pointer', background: sel === no ? 'var(--brand-soft)' : undefined }}
                >
                  <td>{no}</td>
                  <td>{name}</td>
                </tr>
              )) : (
                <tr><td colSpan={2} className="empty">ไม่พบบัญชีตามเงื่อนไข</td></tr>
              )}
            </tbody>
          </table>
        </div>
        <div className="row" style={{ justifyContent: 'flex-end', gap: 8, marginTop: 14 }}>
          <button className="btn" disabled={!sel} onClick={() => choose(sel)}>OK</button>
          <button className="btn ghost" onClick={onClose}>Cancel</button>
        </div>
      </div>
    </Modal>
  );
}
