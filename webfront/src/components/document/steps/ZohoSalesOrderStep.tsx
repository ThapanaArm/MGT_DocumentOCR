import { forwardRef, useEffect, useImperativeHandle, useMemo, useRef, useState } from 'react';
import { getSoPosts, type DocModel, type MapResult, type SoGroupState } from '../../../api/documents';
import type { OcrProvider } from '../../../api/masters';
import {
  createZohoSalesOrder,
  getZohoSalesOrderPayload,
  getZohoSalesOrderPreview,
  type ZohoDeal,
  type ZohoDealItem,
  type ZohoSalesOrderEdits,
  type ZohoSalesOrderPreview,
  type ZohoSalesOrderResult,
  type ZohoShipToInfo,
} from '../../../api/zoho';
import { useAppState } from '../../../state/AppState';
import Modal, { ModalHeader } from '../../Modal';
import ConfirmModal from '../../ConfirmModal';
import { num } from '../../../utils/format';
import ZohoSalesOrderEditor, { type ZohoSalesOrderEditorHeader } from '../ZohoSalesOrderEditor';
import { deliveryDateGroups } from '../SapSalesOrderEditor';
import { groupStatus, type GroupShipToControl } from '../SoGroupPanel';
import { useSoShipToOptions } from '../useSoShipToOptions';
import type { SalesOrderStepHandle } from './SapSalesOrderStep';

type LineEdit = {
  quantity: string;
  unitPrice: string;
  unit: string;
  description: string;
  materialId?: string;
};

interface Props {
  doc: DocModel;
  map: MapResult;
  selectedDealId: string;
  resolvedDeal: ZohoDeal | null;
  selectedShipTo: ZohoShipToInfo | null;
  providers: OcrProvider[];
  uomRules: Record<string, any>[];
  shipTos: Record<string, any>[];
  salesOrg: string;
  posted: boolean;
  onUseMaterial: (lineIndex: number, item: ZohoDealItem) => Promise<void>;
  /** Called after a successful Zoho send with the updated (now POSTED) document, so the page can
   *  flip to the posted state without a manual reload -- mirrors SapSalesOrderStep.onPosted. */
  onPosted: (doc: DocModel) => void;
  /** Save (or clear, with null) the ship-to of one SO Group on the document header. */
  onGroupShipTo: (soGroup: string, data: Record<string, unknown> | null) => void;
}

function convertedValues(
  line: DocModel['lines'][number],
  item: ZohoDealItem,
  rules: Record<string, any>[],
  salesOrg: string,
) {
  const docUnit = (line.uom || '').trim();
  const targetUnit = (item.unit || '').trim();
  const qty = Number(line.qty) || 0;
  const rawPrice = line.price !== '' && line.price != null ? Number(line.price) : NaN;
  if (!docUnit || !targetUnit || docUnit.toLowerCase() === targetUnit.toLowerCase()) {
    return { quantity: qty, unitPrice: Number.isFinite(rawPrice) ? rawPrice : null, unit: targetUnit || docUnit };
  }
  const materialCode = (item.materialCode || '').trim();
  const rule = rules
    .filter((u) => !u.SalesOrg || String(u.SalesOrg) === salesOrg)
    .sort((a, b) => Number(String(b.SalesOrg) === salesOrg) - Number(String(a.SalesOrg) === salesOrg))
    .find(
      (u) =>
        String(u.MaterialCode ?? u.MaterialCodeSAP ?? '') === materialCode &&
        String(u.ExtUom || '').trim().toLowerCase() === docUnit.toLowerCase() &&
        String(u.SapUom || '').trim().toLowerCase() === targetUnit.toLowerCase(),
    );
  // Same order as the backend (ZohoSalesOrderController.ConvertZohoUom): the Deal's own Conversion
  // Ratio wins, the local UoM rule is only the fallback. This is only a first guess shown right after
  // an AI match -- the preview reload replaces it with the backend's own figures.
  const ratio = Number(item.conversionRatio);
  const factor = ratio > 0 ? 1 / ratio : rule && Number(rule.Factor) > 0 ? Number(rule.Factor) : 1;
  return {
    quantity: Math.round(qty * factor * 1000) / 1000,
    unitPrice: Number.isFinite(rawPrice) ? Math.round((rawPrice / factor) * 1e6) / 1e6 : null,
    unit: targetUnit,
  };
}

function mergeOverrides(
  preview: ZohoSalesOrderPreview | null,
  overrides: Record<string, ZohoDealItem>,
  doc: DocModel,
  uomRules: Record<string, any>[],
  salesOrg: string,
) {
  if (!preview || !Object.keys(overrides).length) return preview;
  const lines = [...preview.lines];
  const skipped: typeof preview.skipped = [];
  for (const item of preview.skipped) {
    const key = String(item.itemNo);
    const override = overrides[key];
    if (!override?.materialId) {
      skipped.push(item);
      continue;
    }
    const docLine = doc.lines.find((line) => String(line.itemNo) === key);
    const converted = docLine ? convertedValues(docLine, override, uomRules, salesOrg) : null;
    lines.push({
      itemNo: item.itemNo,
      desc: docLine?.desc ?? item.desc ?? null,
      extCode: docLine?.extCode ?? item.extCode ?? null,
      materialName: override.materialName ?? null,
      materialCode: override.materialCode ?? null,
      quantity: converted?.quantity ?? 0,
      unitPrice: converted?.unitPrice ?? null,
      unit: converted?.unit ?? override.unit ?? null,
    });
  }
  return { ...preview, lines, skipped };
}

const EMPTY_HEADER: ZohoSalesOrderEditorHeader = {
  subject: '', customerRef: '', deliveryDate: '', paymentTerms: '', paymentCurrency: '', incoterms: '', taxId: '', remarks: '',
};

const ZohoSalesOrderStep = forwardRef<SalesOrderStepHandle, Props>(function ZohoSalesOrderStep(
  { doc, map, selectedDealId, resolvedDeal, selectedShipTo, providers, uomRules, shipTos, salesOrg, posted, onUseMaterial, onPosted, onGroupShipTo },
  ref,
) {
  const { guard, showToast } = useAppState();
  const [visible, setVisible] = useState(false);
  const [preview, setPreview] = useState<ZohoSalesOrderPreview | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [overrides, setOverrides] = useState<Record<string, ZohoDealItem>>({});
  const [header, setHeader] = useState<ZohoSalesOrderEditorHeader>(EMPTY_HEADER);
  const [lineEdits, setLineEdits] = useState<Record<string, LineEdit>>({});
  const [sending, setSending] = useState(false);
  const [result, setResult] = useState<ZohoSalesOrderResult | null>(null);
  const [payload, setPayload] = useState<Record<string, unknown> | null>(null);
  const editorRef = useRef<HTMLDivElement | null>(null);

  useImperativeHandle(ref, () => ({
    open() {
      setVisible(true);
      setTimeout(() => editorRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 60);
    },
    stageMaterialMatch(lineIndex: number, rawItem: unknown) {
      const item = rawItem as ZohoDealItem;
      const line = doc.lines[lineIndex];
      if (!line || !item.materialId) return;
      const key = String(line.itemNo);
      const converted = convertedValues(line, item, uomRules, salesOrg);
      setOverrides((current) => ({ ...current, [key]: item }));
      setLineEdits((current) => ({
        ...current,
        [key]: {
          ...(current[key] || { quantity: '', unitPrice: '', unit: '', description: '' }),
          materialId: item.materialId || undefined,
          quantity: String(converted.quantity),
          unitPrice: converted.unitPrice != null ? String(converted.unitPrice) : '',
          unit: converted.unit,
          description: line.desc || '',
        },
      }));
    },
  }), [doc.lines, uomRules, salesOrg]);

  // What the person typed in this step (12 Oct 2026). A preview reload -- e.g. after "Ask AI to
  // match" saved a material, or a re-map -- used to reset every field to the backend defaults,
  // silently dropping edited Payment Terms / Remarks / quantities. Typed fields are kept now, and
  // only typed line fields are sent as overrides (the rest come from the backend's own figures).
  const dirtyHeader = useRef<Set<string>>(new Set());
  const dirtyLines = useRef<Record<string, Set<string>>>({});

  useEffect(() => {
    setOverrides({});
    setResult(null);
    dirtyHeader.current = new Set();
    dirtyLines.current = {};
  }, [doc.docId, selectedDealId]);

  // The preview is computed server-side from the SAVED document lines (their mapped materialCode,
  // qty, unit, price). It used to load only when the document or Deal changed, so after a re-map
  // (e.g. the AI chat split one line into two, then Mapping matched both) it kept showing the old
  // answer: "Not matched" even though Step 2 shows both lines auto-matched. Reload whenever the
  // saved lines change too.
  // Send results per group (one Zoho Sales Order per SO Group + delivery date, all from this document).
  const [posts, setPosts] = useState<SoGroupState[]>([]);
  const reloadPosts = () => getSoPosts(doc.docId).then((r) => setPosts(r.groups)).catch(() => setPosts([]));
  useEffect(() => { void reloadPosts(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [doc.docId, doc.status]);

  const linesKey = useMemo(
    () => doc.lines.map((l) => [l.itemNo, l.materialCode ?? '', l.qty, l.uom, l.price, l.desc].join('|')).join(';'),
    [doc.lines],
  );

  useEffect(() => {
    if (!selectedDealId) {
      setPreview(null);
      setError(null);
      return;
    }
    let cancelled = false;
    setLoading(true);
    setError(null);
    getZohoSalesOrderPreview(doc.docId, selectedDealId)
      .then((value) => {
        if (!cancelled) setPreview(value);
      })
      .catch((reason) => {
        if (!cancelled) {
          setPreview(null);
          setError(reason instanceof Error ? reason.message : 'Could not check this Deal against Zoho CRM');
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => { cancelled = true; };
  }, [doc.docId, selectedDealId, linesKey]);

  useEffect(() => {
    if (!preview) return;
    const fromPreview: ZohoSalesOrderEditorHeader = {
      subject: preview.subject || '', customerRef: preview.customerRef || '', deliveryDate: preview.deliveryDate || '',
      paymentTerms: preview.paymentTerms || '', paymentCurrency: preview.paymentCurrency || '',
      incoterms: preview.incoterms || '', taxId: preview.taxId || '', remarks: preview.remarks || '',
    };
    setHeader((prev) => {
      const merged = { ...fromPreview };
      dirtyHeader.current.forEach((k) => {
        (merged as Record<string, string>)[k] = (prev as Record<string, string>)[k];
      });
      return merged;
    });
    setLineEdits((prev) => {
      const next: Record<string, LineEdit> = {};
      preview.lines.forEach((line) => {
        const key = String(line.itemNo);
        const base: LineEdit = {
          quantity: line.quantity != null ? String(line.quantity) : '',
          unitPrice: line.unitPrice != null ? String(line.unitPrice) : '',
          unit: line.unit || '', description: line.desc || '',
        };
        const old = prev[key];
        if (old) {
          (dirtyLines.current[key] || new Set()).forEach((f) => {
            (base as Record<string, string | undefined>)[f] = (old as Record<string, string | undefined>)[f];
          });
          if (old.materialId) base.materialId = old.materialId;
        }
        next[key] = base;
      });
      // Lines matched only through an AI pick (not in the preview yet) keep their staged values.
      Object.entries(prev).forEach(([key, value]) => {
        if (!next[key] && value.materialId) next[key] = value;
      });
      return next;
    });
  }, [preview]);

  const mergedPreview = useMemo(
    () => mergeOverrides(preview, overrides, doc, uomRules, salesOrg),
    [preview, overrides, doc, uomRules, salesOrg],
  );

  const editLine = (itemNo: unknown, patch: Partial<LineEdit>) =>
    setLineEdits((current) => {
      const key = String(itemNo);
      return { ...current, [key]: { ...(current[key] || { quantity: '', unitPrice: '', unit: '', description: '' }), ...patch } };
    });
  // The person's own typing (marked dirty, so a preview reload keeps it and it is sent).
  const userEditLine = (itemNo: unknown, patch: Partial<LineEdit>) => {
    const key = String(itemNo);
    const set = dirtyLines.current[key] || (dirtyLines.current[key] = new Set());
    Object.keys(patch).forEach((f) => { if (f !== 'materialId') set.add(f); });
    editLine(itemNo, patch);
  };
  const userEditHeader = (patch: Partial<ZohoSalesOrderEditorHeader>) => {
    Object.keys(patch).forEach((k) => dirtyHeader.current.add(k));
    setHeader((current) => ({ ...current, ...patch }));
  };

  const useAiMatch = async (itemNo: unknown, item: ZohoDealItem) => {
    if (!item.materialId) return;
    const key = String(itemNo);
    const lineIndex = doc.lines.findIndex((line) => String(line.itemNo) === key);
    if (lineIndex < 0) return;
    await onUseMaterial(lineIndex, item);
    setOverrides((current) => ({ ...current, [key]: item }));
    const converted = convertedValues(doc.lines[lineIndex], item, uomRules, salesOrg);
    editLine(key, {
      materialId: item.materialId, quantity: String(converted.quantity),
      unitPrice: converted.unitPrice != null ? String(converted.unitPrice) : '',
      unit: converted.unit, description: doc.lines[lineIndex].desc || '',
    });
  };

  const buildEdits = (): ZohoSalesOrderEdits => {
    const mappedShipTo = shipTos.find((item) => String(item.SapShipToCode ?? '') === String(map.header.shipTo.code ?? ''));
    const shipTo = selectedShipTo || (mappedShipTo ? {
      code: String(mappedShipTo.ShipToCode ?? mappedShipTo.SapShipToCode ?? ''),
      address: String(mappedShipTo.ShipToAddress ?? mappedShipTo.Address ?? ''),
    } : undefined);
    return {
      ...Object.fromEntries(Object.entries(header).map(([key, value]) => [key, value || undefined])),
      shipTo,
      // Only what the person typed (plus an AI-confirmed material) goes as an override; untouched
      // quantities / prices come from the backend's own conversion. Numbers accept "1,250.50".
      lines: Object.entries(lineEdits)
        .map(([itemNo, edit]) => {
          const dirty = dirtyLines.current[itemNo] || new Set<string>();
          return {
            itemNo,
            quantity: dirty.has('quantity') && edit.quantity.trim() !== '' ? num(edit.quantity) : undefined,
            unitPrice: dirty.has('unitPrice') && edit.unitPrice.trim() !== '' ? num(edit.unitPrice) : undefined,
            unit: dirty.has('unit') ? edit.unit || undefined : undefined,
            description: dirty.has('description') ? edit.description || undefined : undefined,
            materialId: edit.materialId || undefined,
          };
        })
        .filter((l) => l.quantity !== undefined || l.unitPrice !== undefined || l.unit !== undefined
          || l.description !== undefined || l.materialId !== undefined),
    };
  };

  // Typed numbers that can't be sent: a quantity that isn't a positive number, or a price that
  // isn't a number at all (they used to be silently ignored, while the screen kept showing them).
  const isNumber = (s: string) => /^-?[\d,]*\.?\d+$/.test(s.replace(/\s/g, ''));
  const badNumbers = Object.entries(lineEdits)
    .filter(([itemNo, edit]) => {
      const dirty = dirtyLines.current[itemNo];
      if (!dirty) return false;
      const q = edit.quantity.trim(); const p = edit.unitPrice.trim();
      return (dirty.has('quantity') && q !== '' && (!isNumber(q) || num(q) <= 0))
        || (dirty.has('unitPrice') && p !== '' && !isNumber(p));
    })
    .map(([itemNo]) => itemNo);

  // Ship-to per SO Group: every Zoho ship-to on file for the matched Account (the same list the
  // Ship-to card offers). A group without its own pick uses the document's Ship-to.
  const accountCode = String(map.header.customer?.code || '').trim();
  const { options: groupShipToOptions, loading: groupShipToLoading, error: groupShipToError } = useSoShipToOptions({
    enabled: visible, isMgt: true, customerCode: accountCode, soldToSapCode: '', salesOrg,
  });

  // Megachem rule (6 Oct 2026): an MGT Sales Order must have a Ship-to before it goes to Zoho --
  // checked per Sales Order: each group's own SO Group ship-to, else the document's (same source as
  // buildEdits: the Ship-to picked from Zoho, else the mapped Ship-to master row).
  const groups = deliveryDateGroups(doc, String(doc.header.deliveryDate || ''));
  const groupShipTos = (doc.header.soGroupShipTos as Record<string, Record<string, unknown>>) || {};
  const shipToForSend = buildEdits().shipTo;
  const docHasShipTo = !!shipToForSend
    && !!(String(shipToForSend.code ?? '').trim() || String(shipToForSend.address ?? '').trim());
  const hasShipTo = groups
    .filter((g) => !groupStatus(g, posts).ok)
    .every((g) => {
      const own = groupShipTos[g.soGroup];
      return own ? !!(String(own.code ?? '').trim() || String(own.address ?? '').trim()) : docHasShipTo;
    });
  const groupShipTo: GroupShipToControl = {
    options: groupShipToOptions,
    loading: groupShipToLoading,
    error: groupShipToError,
    current: groupShipTos,
    docShipToLabel: docHasShipTo
      ? [shipToForSend?.code, shipToForSend?.address].filter((x) => String(x || '').trim()).join(' — ')
      : undefined,
    editable: !posted && doc.status !== 'SPLIT' && doc.status !== 'PARTIAL',
    onChange: onGroupShipTo,
  };

  // A Deal of another customer (picked through the manual Deal search) needs a deliberate yes;
  // the backend refuses it otherwise (AcceptOtherAccount).
  const docCustomer = String(map.header.customer?.code || '').trim();
  const dealAccount = String(resolvedDeal?.accountCode || '').trim();
  const dealOfOtherCustomer = !!docCustomer && !!dealAccount && docCustomer.toUpperCase() !== dealAccount.toUpperCase();
  const [confirmOtherDeal, setConfirmOtherDeal] = useState(false);

  const send = () => {
    if (!selectedDealId || !hasShipTo) return;
    if (badNumbers.length > 0) {
      showToast(`Check the quantity / price of item ${badNumbers.join(', ')} — not a valid number`, 'error');
      return;
    }
    if (dealOfOtherCustomer) { setConfirmOtherDeal(true); return; }
    void doSend(false);
  };

  const doSend = (acceptOtherAccount: boolean) => guard(async () => {
    setConfirmOtherDeal(false);
    setSending(true);
    setResult(null);
    try {
      const response = await createZohoSalesOrder(doc.docId, selectedDealId, { ...buildEdits(), acceptOtherAccount });
      setResult(response);
      // POSTED or PARTIAL (some delivery-date Sales Orders created): reflect it right away and
      // refresh the per-group results so a retry only sends the rest.
      if (response.document) onPosted(response.document);
      await reloadPosts();
      const created = (response.groups || []).filter((g) => g.success && !g.skipped).length;
      if (response.success) {
        const dealReference = response.dealName ? ` — linked to Deal “${response.dealName}”` : '';
        const what = (response.groups?.length ?? 1) > 1 ? `${response.groups!.length} Sales Orders` : 'Sales Order';
        showToast(`${what} created in Zoho CRM${dealReference} (${response.linesSent} line(s) sent)`, 'success');
        document.querySelector('.content')?.scrollTo({ top: 0, behavior: 'smooth' }); window.scrollTo({ top: 0, behavior: 'smooth' });
      } else if (created > 0) {
        showToast(`${created} Sales Order${created === 1 ? '' : 's'} created. Some orders require attention. Submit again to retry the remaining orders.`, 'error');
      }
    } finally {
      setSending(false);
    }
  });

  const viewPayload = () => guard(async () => {
    if (!selectedDealId) return;
    setPayload(await getZohoSalesOrderPayload(doc.docId, selectedDealId, buildEdits()) as Record<string, unknown>);
  });

  return (
    <>
      {visible && (
        <div ref={editorRef}>
          <ZohoSalesOrderEditor
            preview={mergedPreview} loading={loading} error={error} selectedDealId={selectedDealId}
            docLines={doc.lines} resolvedDeal={resolvedDeal} providers={providers} header={header}
            onHeaderChange={userEditHeader}
            lineEdits={lineEdits} onLineChange={userEditLine} onUseAiMatch={useAiMatch}
            sending={sending} result={result} onSend={send} onViewPayload={viewPayload} posted={posted}
            shipToMissing={!hasShipTo}
            dateGroups={groups}
            posts={posts}
            groupShipTo={groupShipTo}
          />
        </div>
      )}
      <ConfirmModal
        open={confirmOtherDeal}
        title="Deal of another customer"
        message={
          <>
            Deal <b>{resolvedDeal?.dealName}</b> belongs to customer <b>{dealAccount}</b>, but this document's customer
            is <b>{docCustomer}</b>. Send the Sales Order on this Deal anyway?
          </>
        }
        confirmLabel="Send anyway"
        danger={false}
        onConfirm={() => void doSend(true)}
        onCancel={() => setConfirmOtherDeal(false)}
      />
      <Modal open={payload != null} onClose={() => setPayload(null)}>
        <ModalHeader title="Payload to Send to Zoho CRM" onClose={() => setPayload(null)} />
        <div className="card-b">
          <p className="hint">Review the data prepared for submission.</p>
          <pre className="json">{JSON.stringify(payload, null, 2)}</pre>
        </div>
      </Modal>
    </>
  );
});

export default ZohoSalesOrderStep;
