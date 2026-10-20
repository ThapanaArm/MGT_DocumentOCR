import { forwardRef, useEffect, useImperativeHandle, useRef, useState } from 'react';
import { getSoPayloads, getSoPosts, postSalesOrderGroups, type DocModel, type MapResult, type SoGroupState } from '../../../api/documents';
import type { GroupShipToControl } from '../SoGroupPanel';
import type { SapSalesEmployee } from '../../../api/sap';
import { useSoShipToOptions } from '../useSoShipToOptions';
import UnitFixModal, { type UnitFixTarget } from '../UnitFixModal';
import { loadSapUnits } from '../../../hooks/useSapUnits';
import { num } from '../../../utils/format';
import { useAppState } from '../../../state/AppState';
import Modal, { ModalHeader } from '../../Modal';
import SapSalesOrderEditor from '../SapSalesOrderEditor';

export interface SalesOrderStepHandle {
  open: () => void;
  stageMaterialMatch?: (lineIndex: number, item: unknown) => void;
}

interface Props {
  doc: DocModel;
  map: MapResult;
  salesOrg: string;
  posted: boolean;
  onPosted: (doc: DocModel) => void;
  /** Save (or clear, with null) the ship-to of one SO Group on the document header. */
  onGroupShipTo: (soGroup: string, data: Record<string, unknown> | null) => void;
  /** Save the VAT mode on the document header (header.vatMode). */
  onVatModeChange: (mode: string) => void;
  /** Save how the freight is shared between the Sales Orders (header.soGroupFreight); null = all on the first. */
  onFreightSplitChange?: (split: Record<string, number> | null) => void;
  salesEmployees: SapSalesEmployee[];
  /** Unit Conversion master rows -- only used to pre-fill the "1 DR = ? KG" box. */
  uomRules: Record<string, any>[];
  /** Change one line's unit, converting quantity and price (see UnitFixModal). */
  onFixLineUnit: (index: number, fix: { uom: string; qty: number; price: number; amount: number }) => void;
}

const SapSalesOrderStep = forwardRef<SalesOrderStepHandle, Props>(function SapSalesOrderStep(
  { doc, map, salesOrg, posted, onPosted, onGroupShipTo, onVatModeChange, onFreightSplitChange, salesEmployees, uomRules, onFixLineUnit },
  ref,
) {
  const { guard, showToast } = useAppState();
  const [visible, setVisible] = useState(false);
  const [posting, setPosting] = useState(false);
  const [payload, setPayload] = useState<unknown>(null);
  // Ship-to choices for each SO Group: the SAP ship-to parties (partner function SH) of the matched
  // sold-to -- the same lookup as the Ship-to card's "search SAP". Loaded once the step is opened.
  const soldTo = String(map.header.customer?.sapCode || map.header.customer?.code || '').trim();
  const { options: shipToOptions, loading: shipToLoading, error: shipToError } = useSoShipToOptions({
    enabled: visible, isMgt: false, customerCode: '', soldToSapCode: soldTo, salesOrg,
  });
  const groupShipTo: GroupShipToControl = {
    options: shipToOptions,
    loading: shipToLoading,
    error: shipToError,
    current: (doc.header.soGroupShipTos as Record<string, Record<string, unknown>>) || {},
    docShipToLabel: [map.header.shipTo?.code, map.header.shipTo?.text].filter((x) => String(x || '').trim()).join(' — ') || 'Sold-to',
    editable: !posted && doc.status !== 'SPLIT' && doc.status !== 'PARTIAL',
    onChange: onGroupShipTo,
  };

  // Send results per group (one SAP Sales Order per SO Group + delivery date, all from this document).
  const [posts, setPosts] = useState<SoGroupState[]>([]);
  const reloadPosts = () => getSoPosts(doc.docId).then((r) => setPosts(r.groups)).catch(() => setPosts([]));
  useEffect(() => { void reloadPosts(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [doc.docId, doc.status]);
  const editorRef = useRef<HTMLDivElement | null>(null);

  useImperativeHandle(ref, () => ({
    open() {
      setVisible(true);
      setTimeout(() => editorRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 60);
    },
  }), []);

  // Units SAP doesn't know for the line's material (12 Oct 2026). SAP rejects the whole order with
  // "Sales unit KG is not defined for item 000010", so they are found here first -- from the
  // material's own SAP units (base + alternatives, cached) -- and the person is asked to change the
  // unit in a popup that converts quantity and price together. A material SAP can't be asked about
  // is skipped (best effort, same as the unit dropdown).
  const [unitIssues, setUnitIssues] = useState<UnitFixTarget[]>([]);
  const [fixing, setFixing] = useState<UnitFixTarget | null>(null);
  useEffect(() => {
    if (!visible || posted) { setUnitIssues([]); return; }
    let cancelled = false;
    (async () => {
      const out: UnitFixTarget[] = [];
      for (let i = 0; i < doc.lines.length; i++) {
        const l = doc.lines[i];
        if (['WHT', 'VAT'].includes(String(l.extCode || '').toUpperCase())) continue;
        const m = map.lines?.[i];
        if (!m || m.status === 'skip' || m.status === 'fail' || m.status === 'charge') continue;
        const code = String(m.sapCode || m.code || '').trim();
        if (!code) continue;
        const units = await loadSapUnits(code);
        if (!units.length) continue;
        const uom = String(l.uom || '').trim().toUpperCase();
        if (units.some((u) => u.unit === uom)) continue;
        out.push({
          index: i, itemNo: l.itemNo, desc: String(l.desc || ''), materialCode: code, uom: String(l.uom || '').trim(),
          qty: num(l.qty), price: num(l.price), amount: num(l.amount) || num(l.qty) * num(l.price), units,
        });
      }
      if (!cancelled) setUnitIssues(out);
    })();
    return () => { cancelled = true; };
  }, [visible, posted, doc.lines, map]);

  // "1 <new unit> = n <current unit>" from the Unit Conversion master (either direction), if known.
  const suggestFactor = (t: UnitFixTarget) => (newUnit: string): number | undefined => {
    const eq = (a: unknown, b: string) => String(a ?? '').trim().toUpperCase() === b.trim().toUpperCase();
    const forMat = (r: Record<string, any>) =>
      !String(r.MaterialCode ?? r.MaterialCodeSAP ?? '').trim()
      || eq(r.MaterialCode, t.materialCode) || eq(r.MaterialCodeSAP, t.materialCode);
    const rules = (uomRules || []).filter(forMat);
    const fwd = rules.find((r) => eq(r.ExtUom, t.uom) && eq(r.SapUom, newUnit) && num(r.Factor) > 0);
    if (fwd) return Math.round((1 / num(fwd.Factor)) * 1e6) / 1e6; // rule: 1 old = f new
    const back = rules.find((r) => eq(r.ExtUom, newUnit) && eq(r.SapUom, t.uom) && num(r.Factor) > 0);
    if (back) return num(back.Factor); // rule: 1 new = f old
    return undefined;
  };

  const applyUnitFix = (fix: { uom: string; qty: number; price: number; amount: number }) => {
    if (!fixing) return;
    onFixLineUnit(fixing.index, fix);
    const rest = unitIssues.filter((x) => x.index !== fixing.index);
    setUnitIssues(rest);
    setFixing(rest[0] ?? null);
    if (rest.length === 0) showToast('Units changed — check the lines, then send again', 'success');
  };

  const send = () => {
    if (unitIssues.length > 0) { setFixing(unitIssues[0]); return; }
    void doSend();
  };

  const doSend = () =>
    guard(async () => {
      setPosting(true);
      try {
        const result = await postSalesOrderGroups(doc.docId);
        onPosted(result.document);
        await reloadPosts();
        const sent = result.results.filter((g) => g.success && !g.skipped);
        const failed = result.results.filter((g) => !g.success);
        const nos = sent.map((g) => g.docNo).filter(Boolean).join(', ');
        if (failed.length === 0) {
          showToast(
            `${result.simulated ? 'Simulation completed' : `Created ${sent.length} Sales Order${sent.length === 1 ? '' : 's'} in SAP`}${nos ? ` — ${nos}` : ''}`,
            'success',
          );
        } else {
          showToast(
            `${sent.length} Sales Order${sent.length === 1 ? '' : 's'} created. ${failed.length} require attention. Submit again to retry the remaining orders.`,
            'error',
          );
        }
        document.querySelector('.content')?.scrollTo({ top: 0, behavior: 'smooth' }); window.scrollTo({ top: 0, behavior: 'smooth' });
      } finally {
        setPosting(false);
      }
    });

  const viewPayload = () =>
    guard(async () => {
      const result = await getSoPayloads(doc.docId);
      // One group -> the single payload as before; several -> one payload per Sales Order.
      setPayload(result.payloads.length === 1
        ? result.payloads[0].payload
        : result.payloads.map((p) => ({ soGroup: p.soGroup ?? null, deliveryDate: p.deliveryDate || null, items: p.itemNos, payload: p.payload })));
    });

  return (
    <>
      {visible && (
        <div ref={editorRef}>
          <SapSalesOrderEditor
            doc={doc}
            map={map}
            header={doc.header}
            salesOrg={salesOrg}
            sending={posting}
            posted={posted}
            onSend={send}
            onViewPayload={viewPayload}
            posts={posts}
            partial={doc.status === 'PARTIAL'}
            groupShipTo={groupShipTo}
            onVatModeChange={onVatModeChange}
            onFreightSplitChange={onFreightSplitChange}
            salesEmployees={salesEmployees}
            unitIssues={unitIssues.map((x) => ({ itemNo: x.itemNo, uom: x.uom }))}
            onFixUnits={() => unitIssues[0] && setFixing(unitIssues[0])}
          />
        </div>
      )}

      {fixing && (
        <UnitFixModal
          key={fixing.index}
          target={fixing}
          remaining={Math.max(0, unitIssues.length - 1)}
          suggest={suggestFactor(fixing)}
          onApply={applyUnitFix}
          onClose={() => setFixing(null)}
        />
      )}
      <Modal open={payload != null} onClose={() => setPayload(null)}>
        <ModalHeader title="Payload to Send to SAP" onClose={() => setPayload(null)} />
        <div className="card-b">
          <p className="hint">
            Review the data prepared for submission
            {Array.isArray(payload) && <> · {payload.length} Sales Orders (one per SO Group and delivery date)</>}
          </p>
          <pre className="json">{JSON.stringify(payload, null, 2)}</pre>
        </div>
      </Modal>
    </>
  );
});

export default SapSalesOrderStep;
