import { num } from '../../utils/format';
import type { DocModel, MapEntry, MapField, MapResult } from '../../api/documents';
import type { MastersData } from '../../api/masters';

/* Ports mappingCards()/cmpCard()/sideList()/statusChip()/uomCell(). */

function SideList({ items, side }: { items?: MapField[]; side: 'doc' | 'sap' }) {
  if (!items || !items.length)
    return (
      <div className="hint" style={{ padding: '6px 0' }}>
        {side === 'sap' ? 'No data found in SAP yet' : '—'}
      </div>
    );
  return (
    <table className="cmp">
      <tbody>
        {items.map((f, i) => (
          <tr key={i}>
            <th>{f.label}</th>
            <td>
              {f.value || <span className="hint">—</span>}
              {f.match === true && (
                <span className="badge b-ok" style={{ padding: '1px 7px', marginLeft: 4 }}>
                  ✓
                </span>
              )}
              {f.match === false && (
                <span className="badge b-warn" style={{ padding: '1px 7px', marginLeft: 4 }}>
                  ≠
                </span>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function StatusChip({ st }: { st: string }) {
  if (st === 'ok') return <span className="badge b-ok">✓ Auto-matched</span>;
  if (st === 'manual') return <span className="badge b-warn">✎ Manually selected</span>;
  if (st === 'convert') return <span className="badge b-ok">⇄ Unit converted</span>;
  if (st === 'fail') return <span className="badge b-fail">✗ Not found</span>;
  if (st === 'unitfail') return <span className="badge b-fail">✗ No unit conversion rule</span>;
  return <span className="badge b-idle">Pending Mapping</span>;
}

function CmpCard({
  no,
  title,
  r,
  picker,
  children,
}: {
  no: string | number;
  title: string;
  r: MapEntry;
  picker?: React.ReactNode;
  children?: React.ReactNode;
}) {
  const st = r.status || 'idle';
  return (
    <div className={'cmp-card ' + (st === 'fail' ? 'bad' : '')}>
      <div className="cmp-head">
        <span className="cmp-no">{no}</span>
        <b>{title}</b>
        <StatusChip st={st} />
        {r.sapCode && (
          <span className="badge b-ok" title="Code posted to SAP">
            SAP: {r.sapCode}
          </span>
        )}
        <span className="hint">{r.method || ''}</span>
        <div className="sp" />
        {picker}
      </div>
      <div className="cmp-body">
        <div className="cmp-col">
          <div className="cmp-label">📄 Data from Document</div>
          <SideList items={r.doc} side="doc" />
        </div>
        <div className="cmp-arrow">→</div>
        <div className="cmp-col sap">
          <div className="cmp-label">🏦 Data from SAP</div>
          <SideList items={r.sap} side="sap" />
        </div>
      </div>
      {children}
    </div>
  );
}

function qtyTxt(n: unknown) {
  return num(n).toLocaleString('en-US', { maximumFractionDigits: 3 });
}

interface Props {
  doc: DocModel;
  map: MapResult;
  masters: MastersData;
  posted: boolean;
  onManualHeader: (key: string, value: string) => void;
  onManualLine: (i: number, value: string) => void;
  onQuickAddVendor: () => void;
  onQuickAddCustomer: () => void;
  onQuickAddShipTo: () => void;
  onQuickAddMaterial: (i: number) => void;
  onAddUomRule: (i: number) => void;
}

export default function MappingCards({
  doc,
  map,
  masters,
  posted,
  onManualHeader,
  onManualLine,
  onQuickAddVendor,
  onQuickAddCustomer,
  onQuickAddShipTo,
  onQuickAddMaterial,
  onAddUomRule,
}: Props) {
  const headerSel = (kind: string, key: string, code?: string) => {
    let opts: { v: string; t: string }[] = [];
    if (kind === 'customers')
      opts = masters.customers.map((c) => ({ v: c.CustomerCode, t: c.CustomerCode + ' — ' + c.NameTh }));
    else if (kind === 'vendors')
      opts = masters.vendors.map((c) => ({ v: c.VendorCode, t: c.VendorCode + ' — ' + c.VendorName }));
    else if (kind === 'shiptos') {
      const cc = map.header.customer?.code || '';
      opts = masters.shiptos
        .filter((s) => !cc || s.CustomerCode === cc)
        .map((s) => ({ v: s.ShipToCode, t: s.ShipToCode + ' — ' + s.ShipToName }));
    }
    return (
      <select
        className="cmp-pick"
        value={code || ''}
        disabled={posted}
        onChange={(e) => onManualHeader(key, e.target.value)}
      >
        <option value="">-- Select manually --</option>
        {opts.map((o) => (
          <option key={o.v} value={o.v}>
            {o.t}
          </option>
        ))}
      </select>
    );
  };

  const addBtn = (label: string, fn: () => void) =>
    posted ? null : (
      <button className="btn sm" style={{ marginLeft: 6 }} onClick={fn}>
        + {label}
      </button>
    );

  const matOpts = masters.materials.map((m) => ({
    v: m.MaterialCode,
    t: m.MaterialCode + ' — ' + m.Description,
  }));

  const headerCards: React.ReactNode[] = [];
  let n = 0;
  if (doc.module !== 'SO') {
    const r = map.header.vendor;
    n++;
    headerCards.push(
      <CmpCard
        key="vendor"
        no={n}
        title="Vendor / Supplier"
        r={r}
        picker={
          <>
            {headerSel('vendors', 'vendor', r.code)}
            {r.status === 'fail' && addBtn('Add New Vendor', onQuickAddVendor)}
          </>
        }
      />,
    );
  } else {
    const c = map.header.customer;
    const sh = map.header.shipTo;
    n++;
    headerCards.push(
      <CmpCard
        key="customer"
        no={n}
        title="Customer"
        r={c}
        picker={
          <>
            {headerSel('customers', 'customer', c.code)}
            {c.status === 'fail' && addBtn('Add New Customer', onQuickAddCustomer)}
          </>
        }
      />,
    );
    n++;
    headerCards.push(
      <CmpCard
        key="shipto"
        no={n}
        title="Ship-to"
        r={sh}
        picker={
          <>
            {headerSel('shiptos', 'shipTo', sh.code)}
            {sh.status === 'fail' &&
              (c.code ? (
                addBtn('Add New Ship-to', onQuickAddShipTo)
              ) : (
                <span className="hint" style={{ marginLeft: 6 }}>
                  A customer must be specified first
                </span>
              ))}
          </>
        }
      />,
    );
  }

  const nMat = ++n;
  const matCards = doc.lines.map((_l, i) => {
    const r = map.lines[i];
    const u = r.unit || { doc: [], sap: [], status: 'idle' };
    return (
      <CmpCard
        key={i}
        no={`${nMat}.${i + 1}`}
        title={`Material — Row ${i + 1}`}
        r={r}
        picker={
          <>
            <select
              className="cmp-pick"
              value={r.code || ''}
              disabled={posted}
              onChange={(e) => onManualLine(i, e.target.value)}
            >
              <option value="">-- Select manually --</option>
              {matOpts.map((o) => (
                <option key={o.v} value={o.v}>
                  {o.t}
                </option>
              ))}
            </select>
            {r.status === 'fail' && addBtn('Add New Material', () => onQuickAddMaterial(i))}
          </>
        }
      >
        {r.code && (
          <div className={'cmp-sub ' + (u.status === 'fail' ? 'bad' : '')}>
            <div className="cmp-head">
              <span className="cmp-no sub">{nMat + 1}</span>
              <b>Relate Unit — Unit Conversion</b>
              <StatusChip st={u.status === 'fail' ? 'unitfail' : u.status} />
              <div className="sp" />
              {u.status === 'fail' && !posted && (
                <button className="btn sm" onClick={() => onAddUomRule(i)}>
                  + Add Unit Conversion Rule
                </button>
              )}
            </div>
            <div className="cmp-body">
              <div className="cmp-col">
                <SideList items={u.doc} side="doc" />
              </div>
              <div className="cmp-arrow">→</div>
              <div className="cmp-col sap">
                <SideList items={u.sap} side="sap" />
              </div>
            </div>
          </div>
        )}
      </CmpCard>
    );
  });

  return (
    <div className="card">
      <div className="card-h">
        <h2>Mapping Results — Document vs SAP Comparison</h2>
        <div className="sp" />
        {map.pass ? (
          <span className="badge b-ok">✓ All matched</span>
        ) : (
          <span className="badge b-fail">✗ {map.errors.length} items not found</span>
        )}
      </div>
      <div className="card-b">
        {headerCards}
        <p className="sec-title" style={{ marginTop: 20 }}>
          {nMat}. MATERIAL &amp; {nMat + 1}. RELATE UNIT (per line)
        </p>
        {matCards}
      </div>
    </div>
  );
}

export { qtyTxt };
