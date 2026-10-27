import type { SoGroupState } from '../../api/documents';
import type { DeliveryDateGroup } from './SapSalesOrderEditor';

/* One document -> one Sales Order per SO Group + delivery date (see deliveryDateGroups), all sent
   from this page (replaces splitting it into child documents). Shows each group = one SAP / Zoho
   Sales Order with its own ship-to and the result of its last send: created (with the SO number),
   failed (with the reason, retried by the next Send), or not sent yet. A group that already went
   through is never sent again. */

export function groupStatus(g: DeliveryDateGroup, posts: SoGroupState[]) {
  const st = posts.find((p) => (p.key ?? '') === g.key);
  return { ok: st?.posted ? st : undefined, last: st && !st.posted && st.error ? st : undefined };
}

/** One ship-to the person can pick for an SO Group. `data` is what gets stored in
 *  header.soGroupShipTos[soGroup]: GLC = { code (SAP ship-to party), name, address },
 *  MGT = the Zoho ship-to (ZohoShipToInfo fields). */
export interface GroupShipToOption {
  value: string;
  label: string;
  data: Record<string, unknown>;
}

/** Per-SO-Group ship-to picker, supplied by the SAP / Zoho step. */
export interface GroupShipToControl {
  options: GroupShipToOption[];
  loading?: boolean;
  error?: string | null;
  /** header.soGroupShipTos -- the picks already saved on the document. */
  current: Record<string, Record<string, unknown>>;
  /** Label for "no pick" -- the document's own ship-to. */
  docShipToLabel?: string;
  editable: boolean;
  onChange: (soGroup: string, data: Record<string, unknown> | null) => void;
}

/** Stable identity of a ship-to across reloads: Zoho record id, else code + address. */
export function shipToValue(d: Record<string, unknown> | null | undefined): string {
  if (!d) return '';
  const rid = String(d.recordId ?? '').trim();
  if (rid) return 'rid:' + rid;
  return 'c:' + String(d.code ?? '').trim() + '|' + String(d.address ?? '').trim();
}

function shipToText(d: Record<string, unknown>) {
  const code = String(d.code ?? '').trim();
  const name = String(d.name ?? '').trim();
  const addr = String(d.address ?? '').trim();
  return [code, name || addr].filter(Boolean).join(' — ') || '(no code)';
}

export default function SoGroupPanel({
  groups,
  posts,
  target,
  shipTo,
  itemLabel,
}: {
  groups: DeliveryDateGroup[];
  posts: SoGroupState[];
  target: 'SAP' | 'Zoho CRM';
  shipTo?: GroupShipToControl;
  /** What to show for a line in the Items column (e.g. its material code and name); the bare
   *  line number when absent. */
  itemLabel?: (itemNo: number) => string;
}) {
  const usesSoGroups = groups.some((g) => g.soGroup !== '1');
  if (groups.length < 2 && !usesSoGroups && !posts.some((p) => p.posted || p.error)) return null;
  const done = groups.filter((g) => groupStatus(g, posts).ok).length;
  const pending = groups.length - done;
  // A SO Group already (partly) created keeps its ship-to: changing it now would send the rest of
  // the same group somewhere else.
  const groupStarted = (soGroup: string) => groups.some((g) => g.soGroup === soGroup && groupStatus(g, posts).ok);

  const shipToCell = (g: DeliveryDateGroup) => {
    const picked = shipTo?.current?.[g.soGroup] || null;
    const label = picked ? shipToText(picked) : shipTo?.docShipToLabel || 'Same as document';
    if (!shipTo || !shipTo.editable || groupStarted(g.soGroup)) {
      return <span className={picked ? '' : 'hint'}>{label}</span>;
    }
    const value = shipToValue(picked);
    const known = !picked || shipTo.options.some((o) => o.value === value);
    return (
      <select
        value={value}
        disabled={shipTo.loading}
        aria-label={`Ship-to for SO Group ${g.soGroup}`}
        onChange={(e) => {
          const v = e.target.value;
          if (!v) return shipTo.onChange(g.soGroup, null);
          const opt = shipTo.options.find((o) => o.value === v);
          if (opt) shipTo.onChange(g.soGroup, opt.data);
        }}
        style={{ maxWidth: 320 }}
      >
        <option value="">{shipTo.docShipToLabel ? `Same as document (${shipTo.docShipToLabel})` : 'Same as document'}</option>
        {!known && picked && <option value={value}>{shipToText(picked)}</option>}
        {shipTo.options.map((o) => (
          <option key={o.value} value={o.value}>{o.label}</option>
        ))}
      </select>
    );
  };

  return (
    <section className="so-group-panel" aria-labelledby="so-group-title">
      <div className="so-group-header">
        <div className="so-group-heading">
          <span className="so-group-icon" aria-hidden="true"><i className="fa-solid fa-calendar-days" /></span>
          <div>
            <h3 id="so-group-title">Sales Orders to create</h3>
            {usesSoGroups ? (
              <p>
                Each split group (SO 1, SO 2, …) and delivery date becomes its own Sales Order in {target}. Use
                <b> Split Sales Order</b> above the Detail table to move items between groups; each group can have its
                own Ship-to.
              </p>
            ) : (
              <p>
                Split by delivery date: one Sales Order in {target} per delivery date. To also put products on separate
                Sales Orders (each with its own Ship-to), use <b>Split Sales Order</b> above the Detail table.
              </p>
            )}
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
      {shipTo?.error && (
        <div className="so-group-notice">
          <i className="fa-solid fa-triangle-exclamation" aria-hidden="true" />
          Could not load the ship-to list: {shipTo.error}
        </div>
      )}
      <div className="tw so-group-table-wrap">
        <table className="so-group-table">
          <thead>
            <tr>
              <th>Sales Order</th>
              {usesSoGroups && <th>Split group</th>}
              <th>Delivery Date</th>
              <th>Ship-to</th>
              <th>Items</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {groups.map((g, gi) => {
              const { ok, last } = groupStatus(g, posts);
              return (
                <tr key={g.key}>
                  <td><span className="so-group-number">{gi + 1}</span></td>
                  {usesSoGroups && (
                    <td><span className="badge b-idle" style={{ color: '#000', fontWeight: 700 }}>SO {g.soGroup}</span></td>
                  )}
                  <td>{g.date}</td>
                  <td>{shipToCell(g)}</td>
                  <td>
                    {itemLabel ? (
                      <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
                        {g.itemNos.map((no) => (
                          <span key={no}>{itemLabel(no) || `Line ${no}`}</span>
                        ))}
                      </div>
                    ) : (
                      <>{g.itemNos.join(', ')} <span className="hint">· {g.count} item{g.count === 1 ? '' : 's'}</span></>
                    )}
                  </td>
                  <td>
                    {ok ? (
                      <span className="badge b-ok">
                        <i className="fa-solid fa-check" /> Created{ok.docNo ? ` · ${ok.docNo}` : ''}
                      </span>
                    ) : last ? (
                      <>
                        <span className="badge b-fail"><i className="fa-solid fa-xmark" /> Action required</span>
                        {last.error && <div className="hint" style={{ color: 'var(--red)', marginTop: 4 }}>{last.error}</div>}
                      </>
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
