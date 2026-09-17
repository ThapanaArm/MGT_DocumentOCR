import { forwardRef, useEffect, useImperativeHandle, useMemo, useRef, useState } from 'react';
import type { DocModel, MapResult } from '../../../api/documents';
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
import ZohoSalesOrderEditor, { type ZohoSalesOrderEditorHeader } from '../ZohoSalesOrderEditor';
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
  const ratio = Number(item.conversionRatio);
  const factor = rule && Number(rule.Factor) > 0 ? Number(rule.Factor) : ratio > 0 ? 1 / ratio : 1;
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
  subject: '', customerRef: '', deliveryDate: '', paymentTerms: '', paymentCurrency: '', incoterms: '', taxId: '',
};

const ZohoSalesOrderStep = forwardRef<SalesOrderStepHandle, Props>(function ZohoSalesOrderStep(
  { doc, map, selectedDealId, resolvedDeal, selectedShipTo, providers, uomRules, shipTos, salesOrg, posted, onUseMaterial },
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

  useEffect(() => {
    setOverrides({});
    setResult(null);
  }, [doc.docId, selectedDealId]);

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
  }, [doc.docId, selectedDealId]);

  useEffect(() => {
    if (!preview) return;
    setHeader({
      subject: preview.subject || '', customerRef: preview.customerRef || '', deliveryDate: preview.deliveryDate || '',
      paymentTerms: preview.paymentTerms || '', paymentCurrency: preview.paymentCurrency || '',
      incoterms: preview.incoterms || '', taxId: preview.taxId || '',
    });
    const next: Record<string, LineEdit> = {};
    preview.lines.forEach((line) => {
      next[String(line.itemNo)] = {
        quantity: line.quantity != null ? String(line.quantity) : '',
        unitPrice: line.unitPrice != null ? String(line.unitPrice) : '',
        unit: line.unit || '', description: line.desc || '',
      };
    });
    setLineEdits(next);
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
      lines: Object.entries(lineEdits).map(([itemNo, edit]) => ({
        itemNo, quantity: edit.quantity !== '' ? Number(edit.quantity) : undefined,
        unitPrice: edit.unitPrice !== '' ? Number(edit.unitPrice) : undefined,
        unit: edit.unit || undefined, description: edit.description || undefined, materialId: edit.materialId || undefined,
      })),
    };
  };

  const send = () => guard(async () => {
    if (!selectedDealId) return;
    setSending(true);
    setResult(null);
    try {
      const response = await createZohoSalesOrder(doc.docId, selectedDealId, buildEdits());
      setResult(response);
      if (response.success) {
        showToast(`Created Sales Order in Zoho CRM successfully — linked to Deal "${response.dealName}" (${response.linesSent} line(s) sent)`);
        window.scrollTo({ top: 0, behavior: 'smooth' });
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
            onHeaderChange={(patch) => setHeader((current) => ({ ...current, ...patch }))}
            lineEdits={lineEdits} onLineChange={editLine} onUseAiMatch={useAiMatch}
            sending={sending} result={result} onSend={send} onViewPayload={viewPayload} posted={posted}
          />
        </div>
      )}
      <Modal open={payload != null} onClose={() => setPayload(null)}>
        <ModalHeader title="Payload to Send to Zoho CRM" onClose={() => setPayload(null)} />
        <div className="card-b">
          <p className="hint">Endpoint: <code>{String(payload?._target ?? '')}</code></p>
          <pre className="json">{JSON.stringify(payload, null, 2)}</pre>
        </div>
      </Modal>
    </>
  );
});

export default ZohoSalesOrderStep;
