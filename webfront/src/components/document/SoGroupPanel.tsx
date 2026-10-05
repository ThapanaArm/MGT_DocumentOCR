import type { SoGroupState } from '../../api/documents';
import type { DeliveryDateGroup } from './SapSalesOrderEditor';

/* One Sales Order per delivery date, all sent from this document (replaces splitting it into child
   documents). Shows each delivery-date group = one SAP / Zoho Sales Order, with the result of its
   last send: created (with the SO number), failed (with the reason, retried by the next Send), or
   not sent yet. A group that already went through is never sent again. */

export function groupStatus(g: DeliveryDateGroup, posts: SoGroupState[]) {
  const st = posts.find((p) => (p.key ?? '') === g.key);
  return { ok: st?.posted ? st : undefined, last: st && !st.posted && st.error ? st : undefined };
}

export default function SoGroupPanel({
  groups,
  posts,
  target,
}: {
  groups: DeliveryDateGroup[];
  posts: SoGroupState[];
  target: 'SAP' | 'Zoho CRM';
}) {
  if (groups.length < 2 && !posts.some((p) => p.posted || p.error)) return null;
  const done = groups.filter((g) => groupStatus(g, posts).ok).length;
  const pending = groups.length - done;
  return (
    <section className="so-group-panel" aria-labelledby="so-group-title">
      <div className="so-group-header">
        <div className="so-group-heading">
          <span className="so-group-icon" aria-hidden="true"><i className="fa-solid fa-calendar-days" /></span>
          <div>
            <h3 id="so-group-title">Delivery schedule</h3>
            <p>Items with different delivery dates are submitted as separate Sales Orders to {target}.</p>
          </div>
        </div>
        <div className="so-group-summary" aria-label={`${done} of ${groups.length} Sales Orders created`}>
          <strong>{done}/{groups.length}</strong>
          <span>{pending === 0 ? 'Complete' : 'Created'}</span>
        </div>
      </div>
      {done > 0 && pending > 0 && (
        <div className="so-group-notice">
          <i className="fa-solid fa-circle-info" aria-hidden="true" />
          Submit again to create the remaining orders. Completed orders will be skipped.
        </div>
      )}
      <div className="tw so-group-table-wrap">
        <table className="so-group-table">
          <thead>
            <tr>
              <th>Sales Order</th>
              <th>Delivery Date</th>
              <th>Items</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {groups.map((g, gi) => {
              const { ok, last } = groupStatus(g, posts);
              return (
                <tr key={g.key || '_'}>
                  <td><span className="so-group-number">{gi + 1}</span></td>
                  <td>{g.date}</td>
                  <td>{g.itemNos.join(', ')} <span className="hint">· {g.count} item{g.count === 1 ? '' : 's'}</span></td>
                  <td>
                    {ok ? (
                      <span className="badge b-ok">
                        <i className="fa-solid fa-check" /> Created{ok.docNo ? ` · ${ok.docNo}` : ''}
                      </span>
                    ) : last ? (
                      <span className="badge b-fail"><i className="fa-solid fa-xmark" /> Action required</span>
                    ) : (
                      <span className="badge b-idle">Pending</span>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}
