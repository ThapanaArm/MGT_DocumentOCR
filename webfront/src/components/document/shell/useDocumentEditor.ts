import { useCallback, useRef, useState } from 'react';
import { useAppState } from '../../../state/AppState';
import { mapDocument, type DocModel, type MapResult } from '../../../api/documents';
import { num } from '../../../utils/format';
import { WHT_CODE_RATE } from '../../../constants/fields';

/* =====================================================================
   useDocumentEditor — the provider-agnostic core of the document editor.

   Owns the working document + mapping result and every edit that is the same
   no matter where the document is ultimately posted (SAP or Zoho): header and
   line edits, the GL / Tax / WHT item tables, a manual match override, and the
   re-map call. Both the SAP and Zoho document flows build on this hook so the
   shared editing behaviour lives in exactly one place.

   NOT here (they belong to the provider flow or the page shell, and consume the
   returned state/handlers): document loading, re-OCR, chat, split, master-edit,
   and anything that talks to SAP or Zoho.
   ===================================================================== */
export function useDocumentEditor(user: string) {
  const { guard, showToast } = useAppState();

  const [doc, setDoc] = useState<DocModel | null>(null);
  const [map, setMap] = useState<MapResult | null>(null);
  const [failed, setFailed] = useState(false);
  const manual = useRef<{ header: Record<string, string>; lines: Record<number, string> }>({
    header: {},
    lines: {},
  });

  const runMap = useCallback(
    async (silent: boolean, forDoc?: DocModel) => {
      const d = forDoc || doc;
      if (!d) return;
      const res = await guard(() =>
        mapDocument(d.docId, {
          header: d.header,
          lines: d.lines,
          manual: manual.current,
          user,
        }),
      );
      if (res) {
        setDoc(res.document);
        setMap(res);
        if (!silent) {
          showToast(
            res.pass
              ? 'Data matched and saved successfully'
              : 'Still incomplete: ' + res.errors.length + ' item(s) — please review and select data',
          );
          document.querySelector('.content')?.scrollTo({ top: 0, behavior: 'smooth' }); window.scrollTo({ top: 0, behavior: 'smooth' });
        }
      }
    },
    [doc, guard, showToast, user],
  );

  // ---- editing handlers ----
  const patchDoc = (fn: (d: DocModel) => DocModel) => {
    setDoc((prev) => (prev ? fn(prev) : prev));
    setMap(null);
  };
  const editHeader = (k: string, v: string) =>
    patchDoc((d) => ({ ...d, header: { ...d.header, [k]: v } }));
  const editLine = (i: number, k: string, v: string) =>
    patchDoc((d) => {
      const lines = d.lines.slice();
      const l = { ...lines[i], [k]: v };
      if (k === 'qty' || k === 'price') l.amount = (num(l.qty) * num(l.price)).toFixed(2);
      lines[i] = l;
      return { ...d, lines };
    });
  const editLineExtra = (i: number, k: string, v: string) =>
    setDoc((d) => {
      if (!d) return d;
      const lines = d.lines.slice();
      lines[i] = { ...lines[i], extra: { ...(lines[i].extra || {}), [k]: v } };
      return { ...d, lines };
    });
  const addLine = () =>
    patchDoc((d) => ({
      ...d,
      lines: [
        ...d.lines,
        { itemNo: (d.lines.length + 1) * 10, extCode: '', desc: '', qty: 0, uom: 'EA', price: 0, amount: 0, materialCode: '' },
      ],
    }));
  const delLine = (i: number) =>
    patchDoc((d) => ({ ...d, lines: d.lines.filter((_l, j) => j !== i) }));

  const editGlItem = (i: number, k: string, v: string) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.glItems || []).slice();
      items[i] = { ...items[i], [k]: v };
      return { ...d, header: { ...d.header, glItems: items } };
    });
  const addGlItem = () =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.glItems || []).slice();
      items.push({ glAccount: '', drCr: '', amount: 0, taxCode: '', assignment: '', itemText: '', costCenter: '' });
      return { ...d, header: { ...d.header, glItems: items } };
    });
  const delGlItem = (i: number) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.glItems || []).filter((_g: unknown, j: number) => j !== i);
      return { ...d, header: { ...d.header, glItems: items } };
    });

  const editTaxItem = (i: number, k: string, v: string) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.taxItems || []).slice();
      items[i] = { ...items[i], [k]: v };
      return { ...d, header: { ...d.header, taxItems: items } };
    });
  const addTaxItem = () =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.taxItems || []).slice();
      items.push({ drCr: 'S', docCurrencyAmt: 0, taxCode: '', validFrom: '', taxRate: '' });
      return { ...d, header: { ...d.header, taxItems: items } };
    });
  const delTaxItem = (i: number) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.taxItems || []).filter((_t: unknown, j: number) => j !== i);
      return { ...d, header: { ...d.header, taxItems: items } };
    });

  const editWhtItem = (i: number, k: string, v: string) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.whtItems || []).slice();
      const row = { ...items[i], [k]: v };
      // The W/Tax Code carries its own rate (01 = 1%, 04 = 2%, 05..10 = 3%), so picking a code
      // is enough to work the base back out of the amount that was withheld. Only done when the
      // user changes the code — the base stays typed-over-able afterwards.
      if (k === 'whtCode') {
        const rate = WHT_CODE_RATE[v];
        const amt = Number(row.amtFc) || 0;
        if (rate && amt > 0) row.baseFc = Math.round((amt / rate) * 100) / 100;
      }
      items[i] = row;
      return { ...d, header: { ...d.header, whtItems: items } };
    });
  const addWhtItem = () =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.whtItems || []).slice();
      items.push({ wtType: '', whtCode: '', recipientType: '', baseFc: Number(d.header.subTotal) || 0, amtFc: 0 });
      return { ...d, header: { ...d.header, whtItems: items } };
    });
  const delWhtItem = (i: number) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.whtItems || []).filter((_w: unknown, j: number) => j !== i);
      return { ...d, header: { ...d.header, whtItems: items } };
    });

  const setManualLine = (i: number, v: string) => {
    manual.current.lines[i] = v;
    runMap(true);
  };

  return {
    doc,
    setDoc,
    map,
    setMap,
    failed,
    setFailed,
    manual,
    runMap,
    patchDoc,
    editHeader,
    editLine,
    editLineExtra,
    addLine,
    delLine,
    editGlItem,
    addGlItem,
    delGlItem,
    editTaxItem,
    addTaxItem,
    delTaxItem,
    editWhtItem,
    addWhtItem,
    delWhtItem,
    setManualLine,
  };
}
