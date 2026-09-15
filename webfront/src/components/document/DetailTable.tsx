import { fmtAmt, num } from '../../utils/format';
import { qtyTxt } from './MappingCards';
import type { DocLine, DocModel, MapResult } from '../../api/documents';
import type { MastersData } from '../../api/masters';
import { PO_LINE_EXTRA_FIELDS } from '../../constants/fields';
import MaterialSearchSelect from './MaterialSearchSelect';

/* Ports the DETAIL lines table from docHtml() incl. uomCell()/lineExtraCount(). */

function lineExtraCount(l: DocLine) {
  const ex = l.extra || {};
  return PO_LINE_EXTRA_FIELDS.filter((f) => (ex[f[0]] || '') !== '').length;
}

function UomCell({
  map,
  i,
  posted,
  onAddUomRule,
}: {
  map: MapResult | null;
  i: number;
  posted: boolean;
  onAddUomRule: (i: number) => void;
}) {
  if (!map) return <span className="badge b-idle">Pending Mapping</span>;
  const r = map.lines[i];
  const u = r.uom || ({} as NonNullable<typeof r.uom>);
  if (!r.code) return <span className="hint">—</span>;
  if (u.status === 'fail')
    return (
      <>
        <span className="badge b-fail"><i className="fa-solid fa-xmark" /> No unit conversion rule</span>
        {!posted && (
          <div style={{ marginTop: 6 }}>
            <button className="btn sm" onClick={() => onAddUomRule(i)}>
              + Add Rule
            </button>
          </div>
        )}
      </>
    );
  if (u.status === 'convert')
    return (
      <>
        <b>
          {qtyTxt(u.sapQty)} {u.sapUom}
        </b>
        <div className="sub">
          <span className="badge b-warn"><i className="fa-solid fa-right-left" /> ×{u.factor}</span>
        </div>
        <div className="hint" style={{ marginTop: 2 }}>
          {u.method}
        </div>
      </>
    );
  return (
    <>
      <b>
        {qtyTxt(u.sapQty)} {u.sapUom}
      </b>
      <div className="hint">{u.method}</div>
    </>
  );
}

interface Props {
  doc: DocModel;
  map: MapResult | null;
  masters: MastersData;
  posted: boolean;
  onEditLine: (i: number, key: string, value: string) => void;
  onEditLineExtra: (i: number, key: string, value: string) => void;
  onManualLine: (i: number, value: string) => void;
  onDelLine: (i: number) => void;
  onAddLine: () => void;
  onLearn: (i: number) => void;
  onShowLineExtra: (i: number) => void;
  onAddUomRule: (i: number) => void;
  bare?: boolean; // render without the outer .card wrapper (for use inside a tabbed card)
  /** F01: this table checks OCR lines against shared Master Data (materials/UoM rules) — for the
   *  SAP/GLC path that master data IS the SAP material master, but for MGT the actual Sales Order
   *  is matched and sent separately in Step 3 (Zoho CRM), so labeling these columns "(SAP)" here
   *  implies a destination this check doesn't decide. Defaults to the SAP wording (unchanged for
   *  GLC and for the AP/PO Reference tab, which do post to SAP) — pass true only for the MGT/Zoho
   *  document view. */
  isMgt?: boolean;
}

export default function DetailTable({
  doc,
  map,
  masters,
  posted,
  onEditLine,
  onEditLineExtra,
  onManualLine,
  onDelLine,
  onAddLine,
  onLearn,
  onShowLineExtra,
  onAddUomRule,
  bare,
  isMgt,
}: Props) {
  // SO documents match Material through ocr.CustomerMaterial, not the (deprecated for SO) generic
  // ocr.Material master -- same source, same cross-customer-borrow ordering and label format the
  // "Material — Row N" card in MappingCards.tsx uses. Before this fix, this table pulled its
  // options from masters.materials while MappingCards pulled from masters.custmaterials -- the
  // same underlying SAP code could carry a different (often stale/wrong) description in each
  // master, so picking a code here and looking at "Material — Row N" above could show two
  // different product names for what's actually the same selection. AP/II keep the original
  // generic Material master unchanged (no CustomerMaterial concept there).
  const currentCustomerCode = map?.header.customer?.code;
  const matOpts = doc.module === 'SO'
    ? masters.custmaterials
        .filter((cm) => String(cm.SalesOrg) === (doc.header.salesOrg || (isMgt ? '1000' : '2000')))
        .sort((a, b) => {
          const aMine = a.CustomerCode === currentCustomerCode ? 0 : 1;
          const bMine = b.CustomerCode === currentCustomerCode ? 0 : 1;
          if (aMine !== bMine) return aMine - bMine;
          return String(a.MaterialCodeName || '').localeCompare(String(b.MaterialCodeName || ''));
        })
        .map((m) => ({
          value: m.MaterialCodeSAP,
          description: m.MaterialCodeName || '',
          label: m.CustomerCode === currentCustomerCode
            ? `${m.MaterialCodeSAP} — ${m.MaterialCodeName || ''} (CustomerMaterial)`
            : `${m.MaterialCodeSAP} — ${m.MaterialCodeName || ''} (CustomerMaterial · ${m.CustomerCode})`,
        }))
    : masters.materials.map((m) => ({
        value: m.MaterialCode,
        description: m.Description || '',
        label: m.MaterialCode + ' — ' + m.Description,
      }));
  const showPoExtra = doc.module === 'AP';
  const showSoExtra = doc.module === 'SO';
  const extraCols = (showPoExtra ? 1 : 0) + (showSoExtra ? 2 : 0);
  const sum = doc.lines.reduce((a, l) => a + num(l.amount), 0);

  const numInput = (
    value: unknown,
    onInput: (v: string) => void,
  ) => (
    <input
      className=""
      defaultValue={fmtAmt(value)}
      readOnly={posted}
      onFocus={(e) => (e.target.value = String(num(e.target.value)))}
      onBlur={(e) => (e.target.value = fmtAmt(e.target.value))}
      onInput={(e) => onInput((e.target as HTMLInputElement).value)}
    />
  );

  const addBtn = !posted ? (
    <button className="btn sm" onClick={onAddLine}>
      + Add Row
    </button>
  ) : null;

  return (
    <div className={bare ? undefined : 'card'}>
      {!bare && (
        <div className="card-h">
          <h2>DETAIL — Line Items ({doc.lines.length} rows)</h2>
          <div className="sp" />
          {addBtn}
        </div>
      )}
      <div className="card-b">
        {bare && addBtn && (
          <div className="row" style={{ justifyContent: 'flex-end', marginBottom: 12 }}>{addBtn}</div>
        )}
        <div className="tw">
          <table>
            <thead>
              <tr>
                <th style={{ width: 54 }}>Item</th>
                <th style={{ width: 150 }}>Item Code (Partner)</th>
                <th style={{ minWidth: 260 }}>Item Description (per Document)</th>
                <th style={{ minWidth: 110 }}>Quantity</th>
                <th style={{ width: 74 }}>Unit</th>
                <th style={{ minWidth: 130 }}>Unit Price</th>
                <th style={{ minWidth: 140 }}>Amount</th>
                <th style={{ minWidth: 270 }}>{isMgt ? 'Material (Master Data)' : 'Material (SAP)'}</th>
                <th style={{ minWidth: 170 }}>{isMgt ? 'Unit → Master Data' : 'Unit → SAP'}</th>
                <th>Status</th>
                {showPoExtra && <th style={{ width: 120 }}>PO Detail</th>}
                {showSoExtra && (
                  <>
                    <th style={{ minWidth: 160 }}>Sales Employee Name</th>
                    <th style={{ minWidth: 140 }}>Delivery Date</th>
                  </>
                )}
                <th style={{ width: 44 }} />
              </tr>
            </thead>
            <tbody>
              {doc.lines.length ? (
                doc.lines.map((l, i) => {
                  const r = map ? map.lines[i] : null;
                  return (
                    <tr key={i}>
                      <td style={{ textAlign: 'center' }}>{l.itemNo}</td>
                      <td>
                        <input
                          value={l.extCode ?? ''}
                          readOnly={posted}
                          onChange={(e) => onEditLine(i, 'extCode', e.target.value)}
                        />
                      </td>
                      <td>
                        <input
                          value={l.desc ?? ''}
                          readOnly={posted}
                          onChange={(e) => onEditLine(i, 'desc', e.target.value)}
                        />
                      </td>
                      <td className="num">{numInput(l.qty, (v) => onEditLine(i, 'qty', v))}</td>
                      <td>
                        <input
                          value={l.uom ?? ''}
                          readOnly={posted}
                          onChange={(e) => onEditLine(i, 'uom', e.target.value)}
                          style={{ width: 64 }}
                        />
                      </td>
                      <td className="num">{numInput(l.price, (v) => onEditLine(i, 'price', v))}</td>
                      <td className="num">{numInput(l.amount, (v) => onEditLine(i, 'amount', v))}</td>
                      <td
                        className={r && r.status === 'fail' ? 'cell-fail' : ''}
                        style={{ minWidth: 270 }}
                      >
                        {!map ? (
                          <span className="badge b-idle">Pending Mapping</span>
                        ) : (
                          <MaterialSearchSelect
                            options={matOpts}
                            value={r?.code || ''}
                            disabled={posted}
                            onChange={(value) => onManualLine(i, value)}
                          />
                        )}
                      </td>
                      <td
                        className={r && r.uom?.status === 'fail' ? 'cell-fail' : ''}
                        style={{ minWidth: 170 }}
                      >
                        <UomCell map={map} i={i} posted={posted} onAddUomRule={onAddUomRule} />
                      </td>
                      <td style={{ whiteSpace: 'nowrap' }}>
                        {r &&
                          (r.status === 'fail' ? (
                            <span className="badge b-fail"><i className="fa-solid fa-xmark" /> Not found</span>
                          ) : r.status === 'manual' ? (
                            <span className="badge b-warn"><i className="fa-solid fa-pen" /> Manual</span>
                          ) : (
                            <span className="badge b-ok"><i className="fa-solid fa-check" /> {r.method}</span>
                          ))}
                        {r && r.status === 'manual' && r.code && !posted && (
                          <button
                            className="btn sm"
                            style={{ marginLeft: 4 }}
                            onClick={() => onLearn(i)}
                          >
                            + Master
                          </button>
                        )}
                      </td>
                      {showPoExtra && (
                        <td style={{ whiteSpace: 'nowrap' }}>
                          <button
                            className={'btn sm ' + (lineExtraCount(l) ? '' : 'ghost')}
                            onClick={() => onShowLineExtra(i)}
                          >
                            <i className="fa-solid fa-clipboard-list" /> PO{' '}
                            {lineExtraCount(l)
                              ? `(${lineExtraCount(l)}/${PO_LINE_EXTRA_FIELDS.length})`
                              : ''}
                          </button>
                        </td>
                      )}
                      {showSoExtra && (
                        <>
                          <td style={{ minWidth: 160 }}>
                            <input
                              value={(l.extra || {}).salesEmployeeName || ''}
                              readOnly={posted}
                              onChange={(e) => onEditLineExtra(i, 'salesEmployeeName', e.target.value)}
                            />
                          </td>
                          <td style={{ minWidth: 140 }}>
                            <input
                              value={(l.extra || {}).deliveryDate || ''}
                              readOnly={posted}
                              onChange={(e) => onEditLineExtra(i, 'deliveryDate', e.target.value)}
                            />
                          </td>
                        </>
                      )}
                      <td>
                        {!posted && (
                          <button className="btn sm ghost" onClick={() => onDelLine(i)}>
                            <i className="fa-solid fa-xmark" />
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })
              ) : (
                <tr>
                  <td colSpan={11 + extraCols} className="empty">
                    No items
                  </td>
                </tr>
              )}
            </tbody>
            <tfoot>
              <tr className="totrow">
                <td colSpan={6} style={{ textAlign: 'right' }}>
                  Total
                </td>
                <td style={{ textAlign: 'right' }}>{fmtAmt(sum)}</td>
                <td colSpan={4 + extraCols} />
              </tr>
            </tfoot>
          </table>
        </div>
      </div>
    </div>
  );
}
