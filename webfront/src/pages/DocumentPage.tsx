import { useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useAppState } from '../state/AppState';
import { useMeta } from '../state/MetaContext';
import {
  chatFix,
  getChat,
  getDocument,
  getPayload,
  getRawText,
  learnMaterial,
  mapDocument,
  postToSap,
  reocrDocument,
  setDocCategory,
  splitDocument,
  type ChatMessage,
  type DocModel,
  type MapResult,
} from '../api/documents';
import {
  AP_TOTALS_H,
  AP_TRADE_GROUPS,
  headerDefFor,
  PODP_TOTALS_H,
  SO_REMARK_H,
  SO_TOTALS_H,
} from '../constants/fields';
import { dt, fmt, fmtCost, intFmt, moduleLabel, num, statusBadge } from '../utils/format';
import { findDupes } from '../utils/dupes';
import Steps from '../components/Steps';
import Modal, { ModalHeader } from '../components/Modal';
import OcrProviderSelect from '../components/OcrProviderSelect';
import FieldGrid from '../components/document/FieldGrid';
import TabbedGroups from '../components/document/TabbedGroups';
import DetailTable from '../components/document/DetailTable';
import GlItemsTable from '../components/document/GlItemsTable';
import TaxDataTable from '../components/document/TaxDataTable';
import WhtTable from '../components/document/WhtTable';
import IncomingInvoiceCard from '../components/document/IncomingInvoiceCard';
import MappingCards from '../components/document/MappingCards';
import ChatFixCard from '../components/document/ChatFixCard';
import SplitModal from '../components/document/SplitModal';
import LineExtraModal from '../components/document/LineExtraModal';
import MasterEditModal, {
  type MasterEditState,
} from '../components/master/MasterEditModal';

const USER = 'it-digital@megachem.co.th';
const REOCR_INIT: Record<string, string> = {
  ocr: 'tesseract', text: 'text', azure: 'azure', claude: 'claude',
  claude_text: 'claude_text', typhoon: 'typhoon', gemini: 'gemini', openai: 'openai',
};

export default function DocumentPage() {
  const { docId } = useParams<{ docId: string }>();
  const id = Number(docId);
  const navigate = useNavigate();
  const { guard, showToast } = useAppState();
  const { ocrProviders, loadOcrProviders, apDocCategories, loadApDocCategories, masters, loadMasters } =
    useMeta();

  const [doc, setDoc] = useState<DocModel | null>(null);
  const [map, setMap] = useState<MapResult | null>(null);
  const [failed, setFailed] = useState(false);
  const manual = useRef<{ header: Record<string, string>; lines: Record<number, string> }>({
    header: {},
    lines: {},
  });

  const [chatHistory, setChatHistory] = useState<ChatMessage[]>([]);
  const [chatImage, setChatImage] = useState<string | null>(null);
  const [chatProvider, setChatProvider] = useState('claude');
  const [reocrEngine, setReocrEngine] = useState('auto');

  // modals
  const [reviewOpen, setReviewOpen] = useState(false);
  const [rawText, setRawText] = useState<string | null>(null);
  const [payload, setPayload] = useState<Record<string, any> | null>(null);
  const [splitOpen, setSplitOpen] = useState(false);
  const [lineExtraIdx, setLineExtraIdx] = useState<number | null>(null);
  const [postOpen, setPostOpen] = useState(false);
  const [posting, setPosting] = useState(false);
  const [masterEdit, setMasterEdit] = useState<MasterEditState | null>(null);

  const runMap = useCallback(
    async (silent: boolean, forDoc?: DocModel) => {
      const d = forDoc || doc;
      if (!d) return;
      const res = await guard(() =>
        mapDocument(d.docId, {
          header: d.header,
          lines: d.lines,
          manual: manual.current,
          user: USER,
        }),
      );
      if (res) {
        setDoc(res.document);
        setMap(res);
        if (!silent) {
          showToast(
            res.pass
              ? '✓ Mapping ผ่าน — บันทึกลงฐานข้อมูลแล้ว'
              : '✗ Mapping ไม่ผ่าน — ไม่พบข้อมูล ' + res.errors.length + ' จุด',
          );
          window.scrollTo({ top: 0, behavior: 'smooth' });
        }
      }
    },
    [doc, guard, showToast],
  );

  // Load document on mount / id change.
  useEffect(() => {
    let alive = true;
    setDoc(null);
    setMap(null);
    setFailed(false);
    manual.current = { header: {}, lines: {} };
    loadOcrProviders();
    loadMasters();
    (async () => {
      const d = await guard(() => getDocument(id));
      if (!alive) return;
      if (!d) {
        setFailed(true);
        return;
      }
      setReocrEngine(REOCR_INIT[d.provider || ''] || 'auto');
      setDoc(d);
      if (d.module === 'AP') loadApDocCategories();
      try {
        const chat = await getChat(d.docId);
        if (alive) setChatHistory(chat);
      } catch {
        /* ignore */
      }
      if (d.mapStatus) runMap(true, d);
    })();
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  if (failed) return <div className="card"><div className="empty">โหลดเอกสารไม่สำเร็จ</div></div>;
  if (!doc || !masters) return <div className="card"><div className="empty">กำลังโหลด…</div></div>;

  const h = doc.header;
  const posted = doc.status === 'POSTED';
  const isSplit = doc.status === 'SPLIT';
  const canSplit =
    doc.module === 'SO' && doc.lines.length > 1 && !posted && !isSplit && !doc.sourceDocId;
  const glItems = h.glItems || [];
  const showDetail = doc.module !== 'II' && doc.module !== 'PODP';
  const showGlItems = doc.module === 'AP' || doc.module === 'II';

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
      items[i] = { ...items[i], [k]: v };
      return { ...d, header: { ...d.header, whtItems: items } };
    });
  const addWhtItem = () =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.whtItems || []).slice();
      items.push({ wtType: '', whtCode: '', baseFc: 0, amtFc: 0 });
      return { ...d, header: { ...d.header, whtItems: items } };
    });
  const delWhtItem = (i: number) =>
    setDoc((d) => {
      if (!d) return d;
      const items = (d.header.whtItems || []).filter((_w: unknown, j: number) => j !== i);
      return { ...d, header: { ...d.header, whtItems: items } };
    });

  const setManualHeader = (k: string, v: string) => {
    manual.current.header[k] = v;
    if (k === 'customer') manual.current.header.shipTo = '';
    runMap(true);
  };
  const setManualLine = (i: number, v: string) => {
    manual.current.lines[i] = v;
    runMap(true);
  };

  const learn = (i: number) =>
    guard(async () => {
      const code = map?.lines[i].code;
      const l = doc.lines[i];
      if (!code) return;
      await learnMaterial(doc.docId, {
        partnerCode: doc.partnerCode,
        extCode: l.extCode,
        extDesc: l.desc,
        materialCode: code,
      });
      await loadMasters(true);
      showToast('✓ บันทึกลง Master Mapping แล้ว — ครั้งถัดไประบบจะจับคู่อัตโนมัติ');
      await runMap(true);
    });

  const doReocr = () =>
    guard(async () => {
      const d = await reocrDocument(doc.docId, reocrEngine, USER);
      setDoc(d);
      setMap(null);
      manual.current = { header: {}, lines: {} };
      if (d.provider === 'failed')
        showToast('⚠ อ่านเอกสารไม่สำเร็จ — ลองแนบภาพในแชท AI ด้านล่าง');
      else
        showToast(
          '✓ อ่านเอกสารใหม่เสร็จ — engine ' +
            (d.provider || '') +
            ' · พบ ' +
            d.lines.length +
            ' รายการ',
        );
    });

  const openRaw = () =>
    guard(async () => {
      const r = await getRawText(doc.docId);
      setRawText(r.text || '(ไม่มีข้อความ — เป็นไฟล์สแกนหรือสร้างจากชุดตัวอย่าง)');
    });
  const openPayload = () =>
    guard(async () => {
      const r = await getPayload(doc.docId);
      setPayload(r.payload);
    });

  const confirmPost = () =>
    guard(async () => {
      setPosting(true);
      try {
        const r = await postToSap(doc.docId, USER);
        setDoc(r.document);
        setPostOpen(false);
        showToast(
          (r.simulated ? '✓ (โหมดจำลอง) ' : '✓ ') + 'สร้างเอกสารใน SAP สำเร็จ — เลขที่ ' + r.sapDocNo,
        );
        window.scrollTo({ top: 0, behavior: 'smooth' });
      } finally {
        setPosting(false);
      }
    });

  const doSplit = (assign: Record<string, number>) =>
    guard(async () => {
      const res = await splitDocument(doc.docId, assign, USER);
      setSplitOpen(false);
      setDoc(res.source);
      setMap(null);
      showToast(`✓ Split สำเร็จ — สร้าง ${res.created.length} Sales Order ใหม่`);
    });

  const changeCategory = (v: string) =>
    guard(async () => {
      const d = await setDocCategory(doc.docId, v, USER);
      setDoc(d);
    });

  // ---- quick-add master flows ----
  const quickAddVendor = () =>
    setMasterEdit({
      tab: 'vendors',
      rowKey: null,
      prefill: { VendorName: h.vendorName || '', TaxId: h.vendorTaxId || '', Currency: h.currency || 'THB' },
      dupes: findDupes(masters, 'vendors', h.vendorName, h.vendorTaxId),
      onSaved: async (code) => setManualHeader('vendor', code),
      onUseDupe: (code) => setManualHeader('vendor', code),
    });
  const quickAddCustomer = () =>
    setMasterEdit({
      tab: 'customers',
      rowKey: null,
      prefill: { NameTh: h.customerName || '', TaxId: h.customerTaxId || '', Currency: h.currency || 'THB' },
      dupes: findDupes(masters, 'customers', h.customerName, h.customerTaxId),
      onSaved: async (code) => setManualHeader('customer', code),
      onUseDupe: (code) => setManualHeader('customer', code),
    });
  const quickAddShipTo = () => {
    const custCode = map?.header.customer.code;
    if (!custCode) {
      showToast('⚠ กรุณาระบุลูกค้าให้ได้ก่อน');
      return;
    }
    setMasterEdit({
      tab: 'shiptos',
      rowKey: null,
      prefill: { CustomerCode: custCode, ShipToName: h.shipToName || '', Address: h.shipToAddress || '' },
      dupes: findDupes(masters, 'shiptos', h.shipToName, null, (x) => x.CustomerCode === custCode),
      onSaved: async (code) => setManualHeader('shipTo', code),
      onUseDupe: (code) => setManualHeader('shipTo', code),
    });
  };
  const quickAddMaterial = (i: number) => {
    const l = doc.lines[i];
    setMasterEdit({
      tab: 'materials',
      rowKey: null,
      prefill: { Description: l.desc || '', Uom: l.uom || '', Plant: '1000' },
      dupes: findDupes(masters, 'materials', l.desc, null),
      onSaved: async (code) => {
        manual.current.lines[i] = code;
        await learnMaterial(doc.docId, {
          partnerCode: doc.partnerCode,
          extCode: l.extCode,
          extDesc: l.desc,
          materialCode: code,
        });
        await loadMasters(true);
        await runMap(true);
      },
      onUseDupe: (code) => setManualLine(i, code),
    });
  };
  const addUomRule = (i: number) => {
    const l = doc.lines[i];
    const code = map?.lines[i].code;
    const mat = masters.materials.find((m) => m.MaterialCode === code) || {};
    setMasterEdit({
      tab: 'uoms',
      rowKey: null,
      prefill: {
        MaterialCode: code || '',
        ExtUom: l.uom || '',
        SapUom: mat.Uom || '',
        Note: 'เพิ่มจากเอกสาร #' + doc.docId,
      },
      onSaved: async () => {
        showToast('✓ เพิ่มกฎแปลงหน่วยแล้ว — กำลัง Mapping ใหม่');
        await runMap(true);
      },
    });
  };

  const sendChat = (message: string) => {
    const image = chatImage;
    setChatHistory((hist) => [...hist, { role: 'user', text: message, image: image || undefined }]);
    setChatImage(null);
    (async () => {
      try {
        const r = await chatFix(doc.docId, { message, image, user: USER, provider: chatProvider });
        setDoc(r.document);
        setMap(null);
      } catch (e) {
        showToast('⚠ ' + (e instanceof Error ? e.message : String(e)));
      } finally {
        try {
          setChatHistory(await getChat(doc.docId));
        } catch {
          /* ignore */
        }
      }
    })();
  };

  // ---- derived render pieces ----
  const totalsFields =
    doc.module === 'AP' ? (
      <FieldGrid fields={AP_TOTALS_H} values={h} posted={posted} numeric onEdit={editHeader} />
    ) : doc.module === 'SO' ? (
      <>
        <FieldGrid fields={SO_TOTALS_H} values={h} posted={posted} numeric onEdit={editHeader} />
        <FieldGrid fields={SO_REMARK_H} values={h} posted={posted} onEdit={editHeader} />
      </>
    ) : doc.module === 'PODP' ? (
      <FieldGrid fields={PODP_TOTALS_H} values={h} posted={posted} numeric onEdit={editHeader} />
    ) : null;

  const sb = statusBadge(doc.status);
  const providers = ocrProviders ?? [];

  // Shared props for the item tables (used standalone for SO/II and inside the AP item tabs).
  const detailProps = {
    doc,
    map,
    masters,
    posted,
    onEditLine: editLine,
    onEditLineExtra: editLineExtra,
    onManualLine: setManualLine,
    onDelLine: delLine,
    onAddLine: addLine,
    onLearn: learn,
    onShowLineExtra: (i: number) => setLineExtraIdx(i),
    onAddUomRule: addUomRule,
  };
  const glProps = {
    module: doc.module,
    items: glItems,
    posted,
    onEdit: editGlItem,
    onAdd: addGlItem,
    onDelete: delGlItem,
  };
  const taxProps = {
    items: h.taxItems || [],
    posted,
    onEdit: editTaxItem,
    onAdd: addTaxItem,
    onDelete: delTaxItem,
  };
  const whtProps = {
    items: h.whtItems || [],
    posted,
    onEdit: editWhtItem,
    onAdd: addWhtItem,
    onDelete: delWhtItem,
  };

  return (
    <>
      <Steps current={posted ? 3 : 2} />

      {/* Map result panel */}
      {map &&
        (map.pass ? (
          <div className="result ok">
            <h3>
              <span className="badge b-ok">✓ ผ่าน</span> Mapping ข้อมูลครบถ้วน — พร้อมส่งเข้า SAP
            </h3>
            {map.warns.length > 0 && (
              <ul>
                {map.warns.map((w, i) => (
                  <li key={i}>⚠ {w}</li>
                ))}
              </ul>
            )}
          </div>
        ) : (
          <div className="result bad">
            <h3>
              <span className="badge b-fail">✗ ไม่ผ่าน</span> ไม่พบข้อมูล {map.errors.length} จุด
            </h3>
            <ul>
              {map.errors.map((e, i) => (
                <li key={i}>
                  <b>{e.field}:</b> {e.msg}
                  <br />
                  <span className="hint">↳ วิธีแก้: {e.fix}</span>
                </li>
              ))}
            </ul>
            <div className="row" style={{ marginTop: 14 }}>
              <button className="btn" onClick={() => navigate('/master')}>
                ⚙ ไปหน้า Master Mapping
              </button>
              <span className="hint">หรือเลือกค่าที่ถูกต้องจาก dropdown ด้านล่าง</span>
            </div>
          </div>
        ))}

      {posted && (
        <div className="result ok">
          <h3>✓ ส่งเข้า SAP S/4HANA สำเร็จ</h3>
          <div>
            เอกสาร SAP: <code>{doc.sapDocNo}</code> | {moduleLabel(doc.module)} | {dt(doc.postedAt)}
          </div>
        </div>
      )}

      {/* Document header card */}
      <div className="card">
        <div className="card-h">
          <h2>เอกสาร #{doc.docId}</h2>
          <span
            className={
              'badge ' +
              (doc.provider === 'failed' ? 'b-fail' : (doc.confidence ?? 0) >= 0.9 ? 'b-ok' : 'b-warn')
            }
            title={doc.confidenceNote || undefined}
          >
            OCR {Math.round((doc.confidence || 0) * 100)}% · {doc.provider || ''}
          </span>
          {doc.tokensIn != null && (
            <span className="badge" title="Token ที่ใช้อ่านเอกสารนี้">
              ⚡ {intFmt(doc.tokensIn)} in / {intFmt(doc.tokensOut)} out
            </span>
          )}
          {doc.cost != null && (
            <span className="badge">
              💰 {fmtCost(doc.cost)} {doc.costCurrency || ''}
            </span>
          )}
          <span className="filechip">📄 {doc.fileName}</span>
          <span className={'badge ' + sb.cls}>{sb.label}</span>
          <div className="sp" />
          {!posted && !isSplit && !doc.sourceDocId && (
            <OcrProviderSelect
              providers={providers}
              value={reocrEngine}
              onChange={setReocrEngine}
            />
          )}
          <button
            className="btn sm primary"
            onClick={doReocr}
            disabled={posted || isSplit || !!doc.sourceDocId}
          >
            ↻ อ่านเอกสารใหม่
          </button>
          <button className="btn sm ghost" onClick={openRaw}>
            📄 ข้อความที่อ่านได้
          </button>
          <button className="btn sm ghost" onClick={() => setReviewOpen(true)}>
            👁 Review Document
          </button>
          {canSplit && (
            <button className="btn sm ghost" onClick={() => setSplitOpen(true)}>
              ⑃ แยกเป็นหลาย SO
            </button>
          )}
          <button className="btn sm ghost" onClick={() => navigate('/import/' + doc.module)}>
            เปลี่ยนเอกสาร
          </button>
        </div>
        <div className="card-b">
          {doc.module === 'AP' && (
            <div className="f" style={{ maxWidth: 320, marginBottom: 14 }}>
              <label>ประเภทเอกสาร</label>
              <select
                disabled={posted}
                value={doc.apDocCategory || ''}
                onChange={(e) => changeCategory(e.target.value)}
              >
                <option value="">— เลือกประเภทเอกสาร —</option>
                {(apDocCategories ?? []).map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.label}
                  </option>
                ))}
              </select>
            </div>
          )}
          {doc.sourceDocId && (
            <div
              className="hint"
              style={{ margin: '-6px 0 14px', padding: '8px 12px', background: 'var(--line-soft)', borderRadius: 'var(--r2)' }}
            >
              ↩ แยกมาจากเอกสาร{' '}
              <a href="#" onClick={(e) => { e.preventDefault(); navigate('/doc/' + doc.sourceDocId); }}>
                #{doc.sourceDocId}
              </a>
            </div>
          )}
          {doc.confidenceNote && (
            <div
              className="hint"
              style={{ margin: '-6px 0 14px', padding: '8px 12px', background: 'var(--line-soft)', borderRadius: 'var(--r2)' }}
            >
              ⚠ เหตุผลที่ความแม่นยำไม่ถึง 100%: {doc.confidenceNote}
            </div>
          )}
          {doc.module !== 'II' && (
            <>
              <p className="sec-title">HEADER — ข้อมูลส่วนหัว</p>
              <FieldGrid fields={headerDefFor(doc.module)} values={h} posted={posted} onEdit={editHeader} />
            </>
          )}
        </div>
      </div>

      {/* Mapping cards */}
      {map && (
        <MappingCards
          doc={doc}
          map={map}
          masters={masters}
          posted={posted}
          onManualHeader={setManualHeader}
          onManualLine={setManualLine}
          onQuickAddVendor={quickAddVendor}
          onQuickAddCustomer={quickAddCustomer}
          onQuickAddShipTo={quickAddShipTo}
          onQuickAddMaterial={quickAddMaterial}
          onAddUomRule={addUomRule}
        />
      )}

      {/* Detail table — item lines (AP/SO; hidden for II/PODP) */}
      {showDetail && <DetailTable {...detailProps} />}

      {/* Totals */}
      {totalsFields && (
        <div className="card">
          <div className="card-h">
            <h2>ยอดรวม</h2>
          </div>
          <div className="card-b">{totalsFields}</div>
        </div>
      )}

      {/* MIRO trade groups (AP) */}
      {doc.module === 'AP' && (
        <div className="card">
          <div className="card-h">
            <h2>Supplier Invoice (MIRO)</h2>
          </div>
          <TabbedGroups
            groups={AP_TRADE_GROUPS}
            values={h}
            posted={posted}
            onEdit={editHeader}
            extras={{
              'PO Reference': <DetailTable {...detailProps} bare />,
              Tax: <TaxDataTable {...taxProps} bare />,
              'Withholding Tax': <WhtTable {...whtProps} bare />,
            }}
          />
        </div>
      )}

      {/* Incoming Invoice (SAP FB60 · no PO) */}
      {doc.module === 'II' && (
        <IncomingInvoiceCard
          values={h}
          posted={posted}
          onEdit={editHeader}
          glItems={glItems}
          taxProps={taxProps}
          whtProps={whtProps}
        />
      )}

      {/* G/L Account items (AP/II) */}
      {showGlItems && <GlItemsTable {...glProps} />}

      {/* Chat fix */}
      {!posted && (
        <ChatFixCard
          docId={doc.docId}
          history={chatHistory}
          chatImage={chatImage}
          setChatImage={setChatImage}
          chatProvider={chatProvider}
          setChatProvider={setChatProvider}
          providers={providers}
          onSend={sendChat}
        />
      )}

      {/* Action bar */}
      <div className="card">
        <div className="card-b row">
          <button className="btn primary" onClick={() => runMap(false)} disabled={posted || isSplit}>
            🔍 ขั้นตอนที่ 2 — Mapping ข้อมูล
          </button>
          <button
            className="btn success"
            onClick={() => setPostOpen(true)}
            disabled={!(map && map.pass && !posted && !isSplit)}
          >
            ⎋ ขั้นตอนที่ 3 — ส่งเข้า SAP S/4HANA
          </button>
          <button className="btn" onClick={openPayload} disabled={!(map && map.pass)}>
            {'{}'} ดู Payload
          </button>
          <div style={{ flex: 1 }} />
          <span className="hint">
            {isSplit
              ? 'เอกสารนี้ถูกแยกไปเป็น Sales Order อื่นแล้ว'
              : posted
                ? 'เอกสารนี้ส่งเข้า SAP แล้ว'
                : map
                  ? map.pass
                    ? 'พร้อมส่งเข้า SAP'
                    : 'แก้ไขข้อมูลที่ไม่ผ่านก่อนส่ง'
                  : 'กด Mapping เพื่อตรวจสอบกับ Master Data'}
          </span>
        </div>
      </div>

      {/* ---- Modals ---- */}
      <Modal open={reviewOpen} onClose={() => setReviewOpen(false)} wide>
        <ModalHeader title={`👁 Review Document — ${doc.fileName}`} onClose={() => setReviewOpen(false)} />
        <div className="card-b">
          <p className="hint">เปรียบเทียบไฟล์ต้นฉบับกับข้อมูลที่อ่านได้ในหน้า HEADER/DETAIL</p>
          {doc.provider === 'demo' ? (
            <p className="hint">เอกสารนี้สร้างจากชุดตัวอย่าง (demo) ไม่มีไฟล์ต้นฉบับให้เปิดดู</p>
          ) : ['jpg', 'jpeg', 'png', 'tif', 'tiff', 'bmp', 'webp'].includes(
              (doc.fileName || '').split('.').pop()?.toLowerCase() || '',
            ) ? (
            <img
              src={`/api/documents/${doc.docId}/file`}
              style={{ maxWidth: '100%', borderRadius: 'var(--r3)', border: '1px solid var(--line)' }}
              alt=""
            />
          ) : (
            <iframe
              src={`/api/documents/${doc.docId}/file`}
              style={{ width: '100%', height: '78vh', border: '1px solid var(--line)', borderRadius: 'var(--r3)' }}
              title="document"
            />
          )}
        </div>
      </Modal>

      <Modal open={rawText != null} onClose={() => setRawText(null)}>
        <ModalHeader title="ข้อความที่อ่านได้จากไฟล์" onClose={() => setRawText(null)} />
        <div className="card-b">
          <p className="hint">ใช้ตรวจว่าตัวอ่านเอกสารเห็นอะไรบ้าง</p>
          <pre className="json">{rawText}</pre>
        </div>
      </Modal>

      <Modal open={payload != null} onClose={() => setPayload(null)}>
        <ModalHeader title="Payload ที่จะส่งเข้า SAP" onClose={() => setPayload(null)} />
        <div className="card-b">
          <p className="hint">
            Endpoint: <code>{payload?._target}</code>
          </p>
          <pre className="json">{JSON.stringify(payload, null, 2)}</pre>
        </div>
      </Modal>

      <Modal open={postOpen} onClose={() => setPostOpen(false)}>
        <ModalHeader title="ยืนยันการส่งเข้า SAP S/4HANA" onClose={() => setPostOpen(false)} />
        <div className="card-b">
          <p>
            ระบบจะสร้างเอกสาร <b>{moduleLabel(doc.module)}</b> ในระบบ SAP
          </p>
          <div className="tw">
            <table style={{ minWidth: 'auto' }}>
              <tbody>
                {doc.module === 'SO' ? (
                  <>
                    <tr>
                      <th>Sold-to</th>
                      <td>{map?.header.customer.code} — {map?.header.customer.text}</td>
                    </tr>
                    <tr>
                      <th>Ship-to</th>
                      <td>{map?.header.shipTo.code} — {map?.header.shipTo.text}</td>
                    </tr>
                    <tr>
                      <th>PO ลูกค้า</th>
                      <td>{h.poNo || ''}</td>
                    </tr>
                  </>
                ) : (
                  <>
                    <tr>
                      <th>Vendor</th>
                      <td>{map?.header.vendor.code} — {map?.header.vendor.text}</td>
                    </tr>
                    <tr>
                      <th>เลขที่ใบแจ้งหนี้</th>
                      <td>{h.invoiceNo || ''}</td>
                    </tr>
                  </>
                )}
                <tr>
                  <th>จำนวนรายการ</th>
                  <td>{doc.lines.length} บรรทัด</td>
                </tr>
                <tr>
                  <th>ยอดรวม</th>
                  <td>
                    <b>
                      {fmt(h.totalAmount)} {h.currency || 'THB'}
                    </b>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <div className="row" style={{ marginTop: 18 }}>
            <button className="btn success" onClick={confirmPost} disabled={posting}>
              {posting ? 'กำลังส่ง…' : 'ยืนยันส่งเข้า SAP'}
            </button>
            <button className="btn" onClick={() => setPostOpen(false)}>
              ยกเลิก
            </button>
          </div>
        </div>
      </Modal>

      {splitOpen && (
        <SplitModal doc={doc} onClose={() => setSplitOpen(false)} onConfirm={doSplit} />
      )}
      {lineExtraIdx != null && (
        <LineExtraModal
          line={doc.lines[lineExtraIdx]}
          onClose={() => setLineExtraIdx(null)}
          onEdit={(k, v) => editLineExtra(lineExtraIdx, k, v)}
        />
      )}
      <MasterEditModal
        state={masterEdit}
        masters={masters}
        onClose={() => setMasterEdit(null)}
        afterSave={async () => {
          await loadMasters(true);
        }}
      />
    </>
  );
}
