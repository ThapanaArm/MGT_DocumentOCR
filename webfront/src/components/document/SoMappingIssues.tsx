import type { MapResult } from '../../api/documents';

/* Step 2 result banner for a Sales Order when mapping is incomplete. The other modules keep the
   generic "Failed" banner in DocumentPage. What is different here:
   - Ship-to and all-items errors that exist only because the Customer is still unknown are not
     counted as separate "not found" items -- they are shown as waiting on the Customer.
   - Red for both companies. GLC (SAP): Send needs a full match. MGT (Zoho CRM): Step 3 can still
     be opened, but Send needs a Ship-to, and item lines are matched again against the Deal there
     (lines not matched to a Deal Item are left out).
   - Picking a value in the cards below re-runs the mapping by itself, so there is no
     "run Mapping again" step to tell the user about. */
interface Props {
  map: MapResult;
  isMgt: boolean;
  onOpenMaster: () => void;
}

const WAITS_ON_CUSTOMER = new Set(['Ship-to', 'Item (all lines)']);

const count = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;

export default function SoMappingIssues({ map, isMgt, onOpenMaster }: Props) {
  const customerMissing = !map.header?.customer?.code;
  const waits = (field: string) => customerMissing && WAITS_ON_CUSTOMER.has(field);
  const issues = map.errors.filter((e) => !waits(e.field));
  const waiting = map.errors.filter((e) => waits(e.field));
  const target = isMgt ? 'Zoho CRM' : 'SAP';

  const others = customerMissing ? issues.length - 1 : issues.length;
  const title = customerMissing
    ? 'Customer not matched' + (others > 0 ? ` · ${count(others, 'other item')} to fix` : '')
    : issues.length > 0
      ? `${count(issues.length, 'item')} still need${issues.length === 1 ? 's' : ''} a match`
      : 'Mapping incomplete';

  const waitingWhat = [
    waiting.some((e) => e.field === 'Ship-to') && 'Ship-to',
    waiting.some((e) => e.field === 'Item (all lines)') && 'items',
  ].filter(Boolean).join(' and ');

  return (
    <div className="result bad">
      <h3>
        <span className="badge b-fail"><i className="fa-solid fa-xmark" /> Not ready</span> {title}
      </h3>
      <p className="hint">
        {isMgt
          ? `A Ship-to is required before the Sales Order can be sent to ${target}. You can open Step 3 to review it; item lines are matched against the Deal there, and lines not matched are left out.`
          : `The Sales Order cannot be sent to ${target} until these are matched.`}
      </p>
      <ul>
        {issues.map((e, i) => (
          <li key={i}>
            <b>{e.field}:</b> {e.msg}
            <br />
            <span className="hint"><i className="fa-solid fa-arrow-turn-down" /> How to fix: {e.fix}</span>
          </li>
        ))}
        {waitingWhat && (
          <li>
            <span className="hint">
              <i className="fa-solid fa-hourglass-half" /> {waitingWhat.charAt(0).toUpperCase() + waitingWhat.slice(1)} will
              be matched automatically once the customer is set.
            </span>
          </li>
        )}
      </ul>
      <div className="row" style={{ marginTop: 14 }}>
        <button className="btn" onClick={onOpenMaster}>
          <i className="fa-solid fa-gear" /> Open Master Mapping
        </button>
        <span className="hint">or pick the right value in the cards below</span>
      </div>
    </div>
  );
}
