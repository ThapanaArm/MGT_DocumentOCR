import { fmtAmt, num } from '../../utils/format';
import { qtyTxt } from './MappingCards';
import type { DocLine, DocModel, MapResult } from '../../api/documents';
import type { MastersData } from '../../api/masters';
import { PO_LINE_EXTRA_FIELDS } from '../../constants/fields';

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
  if (!map) return <span className="badge b-idle">รอ Mapping</span>;
  const r = map.lines[i];
  const u = r.uom || ({} as NonNullable<typeof r.uom>);
  if (!r.code) return <span className="hint">—</span>;
  if (u.status === 'fail')
    return (
      <>
        <span className="badge b-fail">✗ ไม่มีกฎแปลงหน่วย</span>
        {!posted && (
          <div style={{ marginTop: 6 }}>
            <button className="btn sm" onClick={() => onAddUomRule(i)}>
              + เพิ่มกฎ
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
          <span className="badge b-warn">⇄ ×{u.factor}</span>
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
}: Props) {
  const matOpts = masters.materials.map((m) => ({
    v: m.MaterialCode,
    t: m.MaterialCode + ' — ' + m.Description,
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
      + เพิ่มบรรทัด
    </button>
  ) : null;

  return (
    <div className={bare ? undefined : 'card'}>
      {!bare && (
        <div className="card-h">
          <h2>DETAIL — รายการสินค้า ({doc.lines.length} บรรทัด)</h2>
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
                <th style={{ width: 150 }}>รหัสสินค้า (คู่ค้า)</th>
                <th style={{ minWidth: 260 }}>ชื่อสินค้าตามเอกสาร</th>
                <th style={{ minWidth: 110 }}>จำนวน</th>
                <th style={{ width: 74 }}>หน่วย</th>
                <th style={{ minWidth: 130 }}>ราคา/หน่วย</th>
                <th style={{ minWidth: 140 }}>จำนวนเงิน</th>
                <th style={{ minWidth: 270 }}>Material (SAP)</th>
                <th style={{ minWidth: 170 }}>หน่วย → SAP</th>
                <th>สถานะ</th>
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
                          <span className="badge b-idle">รอ Mapping</span>
                        ) : (
                          <select
                            value={r?.code || ''}
                            disabled={posted}
                            onChange={(e) => onManualLine(i, e.target.value)}
                          >
                            <option value="">-- ไม่พบ / กรุณาเลือก --</option>
                            {matOpts.map((o) => (
                              <option key={o.v} value={o.v}>
                                {o.t}
                              </option>
                            ))}
                          </select>
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
                            <span className="badge b-fail">✗ ไม่พบ</span>
                          ) : r.status === 'manual' ? (
                            <span className="badge b-warn">✎ เลือกเอง</span>
                          ) : (
                            <span className="badge b-ok">✓ {r.method}</span>
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
                            📋 PO{' '}
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
                            ✕
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })
              ) : (
                <tr>
                  <td colSpan={11 + extraCols} className="empty">
                    ไม่มีรายการ
                  </td>
                </tr>
              )}
            </tbody>
            <tfoot>
              <tr className="totrow">
                <td colSpan={6} style={{ textAlign: 'right' }}>
                  รวม
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
