/* =====================================================================
   Mock backend — lets the whole UI run with NO backend/DB (UI mockup mode).
   Used by api/client.ts. Turn ON: create webfront/.env.local with
     VITE_USE_MOCK=1
   then restart `npm run dev`. Turn OFF: remove that line (default = only
   falls back to mock when the real backend is unreachable).

   It keeps a small in-memory store so attach → edit → map → post flows feel
   real within a session (state resets on page reload).
   ===================================================================== */

/* eslint-disable @typescript-eslint/no-explicit-any */

const providers = [
  { id: 'auto', label: 'Automatic (Recommended)', desc: 'Reads the text in the file first; for scanned files it automatically uses Tesseract OCR', ready: true },
  { id: 'text', label: 'Text in File Only', desc: 'Fastest, but does not work with scanned files/images', ready: true },
  { id: 'tesseract', label: 'Tesseract OCR (Local)', desc: 'Forces OCR reading even if the file already has a text layer', ready: true },
  { id: 'typhoon', label: 'Typhoon OCR (Thai-specialized)', desc: 'Thai/English OCR model by SCB 10X', ready: false },
  { id: 'azure', label: 'Azure Document Intelligence', desc: 'Much more accurate for forms/tables', ready: false },
  { id: 'claude', label: 'Claude Vision (AI)', desc: 'Most accurate for messy documents', ready: false },
  { id: 'gemini', label: 'Gemini Vision (AI)', desc: "Google's Vision model", ready: false },
  { id: 'openai', label: 'ChatGPT Vision (AI)', desc: "OpenAI's GPT-4o model", ready: false },
  { id: 'demo', label: 'Sample Data (Test)', desc: 'Does not read a real file; used to test the Mapping / SAP submission steps', ready: true },
];

const apDocCategories = [
  { id: 'INVENTORY', label: 'AP Liability Recognition - Inventory' },
  { id: 'EXPENSE', label: 'AP Liability Recognition - Expense' },
  { id: 'FIXED_ASSET_BUDGET', label: 'AP Liability Recognition - Fixed Asset (Budget-Controlled)' },
  { id: 'FIXED_ASSET_NO_BUDGET', label: 'AP Liability Recognition - Fixed Asset (Not Budget-Controlled)' },
  { id: 'SUB_CONTRACT', label: 'AP Liability Recognition - Sub Contract' },
];

const health = {
  ok: true,
  db: { db: 'MGT_Document_OCR (mock)', usr: 'sa', srv: 'MOCK\\SQLEXPRESS' },
  counts: { customers: 3, vendors: 3, materials: 7, documents: 4 },
  ocrProvider: 'auto',
  sapMode: 'simulate',
};

function dashboard(days: number) {
  const ocrDaily = Array.from({ length: days }, (_, i) => {
    const d = new Date();
    d.setDate(d.getDate() - (days - 1 - i));
    const docCount = Math.round(4 + 5 * Math.abs(Math.sin(i * 1.1)));
    return { date: d.toISOString().slice(0, 10), docCount, okCount: Math.max(0, docCount - (i % 3)) };
  });
  return {
    statusCounts: { NEW: 6, INCOMPLETE: 3, MAPPED: 5, POSTED: 12, SPLIT: 1, total: 27 },
    trend: { NEW: 12, INCOMPLETE: -8, MAPPED: 20, POSTED: 15, SPLIT: 0, total: 10 },
    byModule: [
      { module: 'AP', count: 12 }, { module: 'SO', count: 8 },
      { module: 'II', count: 5 }, { module: 'PODP', count: 2 },
    ],
    costByModule: [
      { module: 'AP', count: 12, tokens: 48200, cost: 0.7231, costCurrency: 'USD' },
      { module: 'SO', count: 8, tokens: 31500, cost: 0.472, costCurrency: 'USD' },
      { module: 'II', count: 5, tokens: 15800, cost: 0.237, costCurrency: 'USD' },
    ],
    ocrPerf: { avgConfidencePct: 91.4, avgDurationSec: 3.2, pctEditedByUser: 18.5, tokensToday: 6120 },
    ocrDaily,
    recent: inbox.slice(0, 4).map((r) => ({
      DocId: r.DocId, Module: r.Module, FileName: r.FileName, DocNo: r.DocNo,
      PartnerName: r.PartnerName, Status: r.Status, TotalAmount: r.TotalAmount, SapDocNo: r.SapDocNo,
      CreatedAt: r.CreatedAt, UpdatedAt: r.CreatedAt, CreatedBy: 'it-digital@megachem.co.th',
      PostedBy: r.Status === 'POSTED' ? 'it-digital@megachem.co.th' : null,
    })),
  };
}

const inbox = [
  { DocId: 1042, Module: 'AP', FileName: 'invoice_UC_2024.pdf', DocNo: 'INV-2024-0042', DocDate: '2026-08-30', PartnerName: 'Universal Chemical Supply Co., Ltd.', TotalAmount: 128400, Status: 'POSTED', ApDocCategory: 'INVENTORY', OcrConfidence: 0.94, OcrConfidenceNote: null, OcrProvider: 'claude', OcrTokensIn: 3200, OcrTokensOut: 900, OcrCost: 0.021, OcrInputCost: 0.015, OcrOutputCost: 0.006, OcrCostCurrency: 'USD', SapDocNo: '5100004210', CreatedAt: '2026-09-01T09:12:00' },
  { DocId: 1041, Module: 'SO', FileName: 'po_scicorp.pdf', DocNo: 'PO-778120', DocDate: '2026-08-29', PartnerName: 'Siam Chemical Industry Co., Ltd.', TotalAmount: 64200, Status: 'MAPPED', ApDocCategory: null, OcrConfidence: 0.9, OcrConfidenceNote: null, OcrProvider: 'auto', OcrTokensIn: null, OcrTokensOut: null, OcrCost: null, OcrInputCost: null, OcrOutputCost: null, OcrCostCurrency: null, SapDocNo: null, CreatedAt: '2026-09-01T08:40:00' },
  { DocId: 1040, Module: 'AP', FileName: 'freight_nc.jpg', DocNo: 'NC-0091', DocDate: '2026-08-28', PartnerName: 'N.C. Logistics Service Co., Ltd.', TotalAmount: 8500, Status: 'INCOMPLETE', ApDocCategory: 'EXPENSE', OcrConfidence: 0.72, OcrConfidenceNote: 'Image partially blurred', OcrProvider: 'tesseract', OcrTokensIn: null, OcrTokensOut: null, OcrCost: null, OcrInputCost: null, OcrOutputCost: null, OcrCostCurrency: null, SapDocNo: null, CreatedAt: '2026-08-31T16:20:00' },
  { DocId: 1043, Module: 'II', FileName: 'supplier_invoice_no_po.pdf', DocNo: 'INV250400286', DocDate: '2025-04-23', PartnerName: 'Excellent Chemical Co., Ltd.', TotalAmount: 10700, Status: 'NEW', ApDocCategory: null, OcrConfidence: 0.93, OcrConfidenceNote: null, OcrProvider: 'claude', OcrTokensIn: 3120, OcrTokensOut: 840, OcrCost: 0.019, OcrInputCost: 0.013, OcrOutputCost: 0.006, OcrCostCurrency: 'USD', SapDocNo: null, CreatedAt: '2026-09-01T10:05:00' },
  { DocId: 1044, Module: 'PODP', FileName: 'po_downpayment.pdf', DocNo: 'DP-0007', DocDate: '2026-08-30', PartnerName: 'Universal Chemical Supply Co., Ltd.', TotalAmount: 30000, Status: 'NEW', ApDocCategory: null, OcrConfidence: 0.9, OcrConfidenceNote: null, OcrProvider: 'auto', OcrTokensIn: null, OcrTokensOut: null, OcrCost: null, OcrInputCost: null, OcrOutputCost: null, OcrCostCurrency: null, SapDocNo: null, CreatedAt: '2026-08-31T11:00:00' },
];

const masters = {
  customers: [
    { CustomerCode: '0010001', SapCustomerCode: '0000100023', NameTh: 'Siam Chemical Industry Co., Ltd.', NameEn: 'Siam Chemical Industry Co., Ltd.', TaxId: '0105533012345', Branch: '00000', SalesOrg: '1000', DistChannel: '10', Division: '00', Currency: 'THB', PaymentTerms: 'N030' },
    { CustomerCode: '0010002', SapCustomerCode: '0000100047', NameTh: 'Thai Polymer Group PCL.', NameEn: 'Thai Polymer Group PCL.', TaxId: '0107536000123', Branch: '00000', SalesOrg: '1000', DistChannel: '10', Division: '00', Currency: 'THB', PaymentTerms: 'N060' },
    { CustomerCode: '0010003', SapCustomerCode: '', NameTh: 'Asia Coating & Plastic Co., Ltd.', NameEn: 'Asia Coating & Plastic Co., Ltd.', TaxId: '0125548009876', Branch: '00000', SalesOrg: '1000', DistChannel: '10', Division: '00', Currency: 'THB', PaymentTerms: 'N030' },
  ],
  vendors: [
    { VendorCode: 'V-500012', SapVendorCode: '0000200015', VendorName: 'Universal Chemical Supply Co., Ltd.', TaxId: '0105546007788', Branch: '00000', Currency: 'THB', PaymentTerms: 'N030', ReconAcct: '2110100', WhtCode: '-' },
    { VendorCode: 'V-500034', SapVendorCode: '0000200037', VendorName: 'N.C. Logistics Service Co., Ltd.', TaxId: '0115551002233', Branch: '00000', Currency: 'THB', PaymentTerms: 'N015', ReconAcct: '2110100', WhtCode: '53 (3%)' },
    { VendorCode: 'V-500051', SapVendorCode: '', VendorName: 'PTG Petrochemical PCL.', TaxId: '0107545000456', Branch: '00001', Currency: 'THB', PaymentTerms: 'N045', ReconAcct: '2110100', WhtCode: '-' },
  ],
  materials: [
    { MaterialCode: 'FG-100021', SapMaterialCode: '000000000000100021', Description: 'Titanium Dioxide R-902 (25 KG/BAG)', Uom: 'KG', Plant: '1000', MatGroup: 'PIG01' },
    { MaterialCode: 'FG-100045', SapMaterialCode: '000000000000100045', Description: 'Calcium Carbonate CC-800 (25 KG/BAG)', Uom: 'KG', Plant: '1000', MatGroup: 'FIL01' },
    { MaterialCode: 'RM-200011', SapMaterialCode: '000000000000200011', Description: 'Methyl Ethyl Ketone (MEK) 99.5%', Uom: 'L', Plant: '1000', MatGroup: 'SOL01' },
    { MaterialCode: 'SV-900001', SapMaterialCode: '', Description: 'Freight Charge', Uom: 'AU', Plant: '1000', MatGroup: 'SRV01' },
  ],
  shiptos: [
    { ShipToCode: '0010001-01', CustomerCode: '0010001', SapShipToCode: '0000100024', ShipToName: 'Bang Pu Warehouse', Address: 'Bang Pu Industrial Estate, Soi 7, Phraeksa, Mueang, Samut Prakan 10280' },
    { ShipToCode: '0010002-01', CustomerCode: '0010002', SapShipToCode: '0000100048', ShipToName: 'Ayutthaya Plant (Rojana)', Address: 'Rojana Industrial Park, Khan Ham, Uthai, Phra Nakhon Si Ayutthaya 13210' },
  ],
  custmaterials: [{ Id: 1, CustomerCode: '0010001', ExtCode: 'SCI-TIO2-902', ExtDesc: 'TIO2 R902 25 kg bag', MaterialCode: 'FG-100021' }],
  venmaterials: [{ Id: 1, VendorCode: 'V-500012', ExtCode: 'UC-MEK-995', ExtDesc: 'MEK 99.5 PCT', MaterialCode: 'RM-200011' }],
  uoms: [
    { Id: 1, MaterialCode: '', ExtUom: 'kg', SapUom: 'KG', SapUomIso: 'KGM', Factor: 1, Note: 'Global rule' },
    { Id: 2, MaterialCode: 'FG-100021', ExtUom: 'BAG', SapUom: 'KG', SapUomIso: 'KGM', Factor: 25, Note: 'Packed 25 kg/bag' },
  ],
};

const logs = [
  { LogId: 5001, PostedAt: '2026-09-01T10:03:00', Module: 'AP', SapDocNo: '5100004210', DocNo: 'INV-2024-0042', PartnerName: 'Universal Chemical Supply Co., Ltd.', TotalAmount: 128400, Lines: 3, Success: true, OcrProvider: 'claude', FileName: 'invoice_UC_2024.pdf' },
  { LogId: 5000, PostedAt: '2026-08-30T14:20:00', Module: 'SO', SapDocNo: '0000012345', DocNo: 'PO-770001', PartnerName: 'Thai Polymer Group PCL.', TotalAmount: 250000, Lines: 5, Success: true, OcrProvider: 'auto', FileName: 'po_tpg.pdf' },
];

const auditLogs = [
  { Action: 'POSTED', CreatedAt: '2026-09-01T10:03:00', Module: 'AP', DocId: 1042, DocNo: 'INV-2024-0042', FileName: 'invoice_UC_2024.pdf', Detail: 'Sent to SAP successfully 5100004210', OcrProvider: 'claude', PerformedBy: 'it-digital@megachem.co.th' },
  { Action: 'UPDATE', CreatedAt: '2026-09-01T09:50:00', Module: 'AP', DocId: 1042, DocNo: 'INV-2024-0042', FileName: 'invoice_UC_2024.pdf', Detail: 'Edited totals', OcrProvider: 'claude', PerformedBy: 'it-digital@megachem.co.th' },
  { Action: 'CREATE', CreatedAt: '2026-09-01T09:12:00', Module: 'AP', DocId: 1042, DocNo: 'INV-2024-0042', FileName: 'invoice_UC_2024.pdf', Detail: 'Read document with Claude', OcrProvider: 'claude', PerformedBy: 'it-digital@megachem.co.th' },
];

// ---- stateful document store (attach → edit → map → post) ----
const docStore = new Map<number, any>();
let nextId = 9001;

function buildDoc(module: string, id: number, fileName: string): any {
  const base: any = {
    docId: id, module, status: 'NEW', provider: 'demo', confidence: 0.92,
    fileName, apDocCategory: module === 'AP' ? 'INVENTORY' : '', partnerCode: '',
    tokensIn: 3120, tokensOut: 840, cost: 0.019, costIn: 0.013, costOut: 0.006, costCurrency: 'USD',
    header: {}, lines: [],
  };
  if (module === 'SO') {
    base.partnerCode = '0010001';
    base.header = {
      docType: 'Purchase Order', poNo: 'PO-778120', poDate: '2026-08-29', customerName: 'Siam Chemical Industry Co., Ltd.',
      customerTaxId: '0105533012345', shipToName: 'Bang Pu Warehouse', shipToAddress: 'Bang Pu Industrial Estate, Phraeksa, Samut Prakan',
      deliveryDate: '2026-09-10', currency: 'THB', paymentTerms: 'N030', incoterms: 'DAP',
      subTotal: 60000, vatAmount: 4200, totalAmount: 64200, remark: '',
    };
    base.lines = [
      { itemNo: 10, extCode: 'SCI-TIO2-902', desc: 'TIO2 R902 25 kg bag', qty: 40, uom: 'BAG', price: 1500, amount: 60000, materialCode: '', extra: {} },
    ];
  } else if (module === 'II') {
    // Data mirrors the "Enter Supplier Invoice" (FB60, no PO) training doc.
    base.partnerCode = 'OT053';
    base.header = {
      transaction: 'Invoice', invoiceNo: 'INV250400286', invoiceDate: '2025-04-23', postingDate: '2025-04-23',
      sapDocType: 'KR', currency: 'THB',
      // Supplier (one-time vendor OT053)
      vendorCode: 'OT053', vendorName: 'Excellent Chemical Co., Ltd.', vendorTaxId: '0105562100384',
      totalAmount: 10700, vatAmount: '', whtAmount: 0,
      calculateTax: '', taxCode: 'D1', businessPlace: '0000',
      headerText: 'AP Invoice without Purchase Order',
      notes: 'AP liability for licence fee — not referencing a purchase order',
      // Payment
      baselineDate: '2025-04-23', paymentTerms: '0001', dueDate: '2025-04-23', paymentMethod: 'T',
      // Address & bank (one-time vendor)
      language: 'EN', addressStreet: '124 Phahonyothin 48, Phahonyothin', addressCity: 'Bangkhen Bangkok',
      addressPostalCode: '10110', addressCountry: 'TH', vendorEmail: 'customerservice@excellentchemi.co.th',
      bankCountry: 'TH', bankKey: '0140202', bankAccountNo: '2020003486',
      // G/L Account line items
      glItems: [
        { glAccount: '74240000', drCr: 'D', amount: 10700, taxCode: 'D1', assignment: '', itemText: 'Licence Fee', costCenter: '10200' },
      ],
      taxItems: [],
      // WHT type rows are always shown (from BP master); amounts blank on this invoice.
      whtItems: [
        { wtType: 'WHT Type for Payment Posting 1', whtCode: '01', baseFc: '', amtFc: '' },
        { wtType: 'WHT Type for Payment Posting 2', whtCode: '02', baseFc: '', amtFc: '' },
        { wtType: 'WHT Type for Payment Posting 3', whtCode: '03', baseFc: '', amtFc: '' },
        { wtType: 'WHT Type for Invoice Posting 1', whtCode: '', baseFc: '', amtFc: '' },
        { wtType: 'WHT Type for Invoice Posting 2', whtCode: '', baseFc: '', amtFc: '' },
        { wtType: 'WHT Type for Invoice Posting 3', whtCode: '', baseFc: '', amtFc: '' },
      ],
      oneTimeVendor: true,
    };
  } else if (module === 'PODP') {
    base.partnerCode = 'V-500012';
    base.header = {
      docType: 'Down Payment Slip', invoiceNo: 'DP-0007', invoiceDate: '2026-08-30', postingDate: '2026-08-30',
      vendorName: 'Universal Chemical Supply Co., Ltd.', vendorTaxId: '0105546007788', poRef: 'PO-9001',
      currency: 'THB', paymentTerms: 'N030', totalAmount: 30000,
    };
  } else {
    // AP — Supplier Invoice (MIRO, with PO). Data mirrors the supplierInvoice training doc.
    base.partnerCode = '0001000001';
    base.header = {
      docType: 'Tax Invoice', transaction: 'Invoice', invoiceNo: 'IV2504210024',
      invoiceDate: '2025-04-21', postingDate: '2025-04-21',
      vendorName: 'Comfort Company', vendorTaxId: '', branch: '00000',
      poRef: '2130000016', currency: 'THB',
      // Basic Data (MIRO)
      calculateTax: 'X', taxCode: 'V1', businessPlace: '0000', headerText: '',
      // PO Reference
      refDocument: 'PO', refDocType: '3', layout: 'ALL',
      // Payment
      baselineDate: '2025-04-21', paymentTerms: '4003', dueDate: '2025-05-21', paymentBlock: '',
      // Details
      sapDocType: 'RE', invoicingParty: '1000001', glAccountHeader: '21211001',
      // Tax tab
      taxReportingDate: '2025-04-21', taxFulfillDate: '2025-04-21', taxDate: '2025-04-21',
      taxItems: [
        { drCr: 'S', docCurrencyAmt: 1506.40, taxCode: 'V1 (Input VAT 7%)', validFrom: '01.01.1900', taxRate: '7.000%(VST)' },
      ],
      // WHT type rows are always shown (from BP master); amounts blank on this invoice.
      whtItems: [
       { wtType: 'WHT Type for Payment Posting 1', whtCode: '', baseFc: '', amtFc: '' },
        { wtType: 'WHT Type for Payment Posting 2', whtCode: '', baseFc: '', amtFc: '' },
        { wtType: 'WHT Type for Payment Posting 3', whtCode: '', baseFc: '', amtFc: '' },
        { wtType: 'WHT Type for Invoice Posting 1', whtCode: '', baseFc: '', amtFc: '' }
      ],
      // Totals
      subTotal: 21520, vatRate: 7, vatAmount: 1506.40, whtAmount: 0, totalAmount: 23026.40,
      // G/L Account Items
      glItems: [
        { glAccount: '76430000', drCr: 'D', amount: 500, taxCode: 'V1', assignment: '', itemText: 'Office Supply', costCenter: '10200' },
      ],
    };
    base.lines = [
      { itemNo: 10, extCode: '', desc: 'ADEKANOL L-31 200KG DR.', qty: 10, uom: 'DR', price: 2102, amount: 21020, materialCode: '', extra: {} },
    ];
  }
  return base;
}

// Infer a document's module from the inbox list so opening it renders the right
// page (Incoming/II, Supplier/AP, …); default to AP for unknown ids.
function moduleForId(id: number): string {
  return inbox.find((r) => r.DocId === id)?.Module ?? 'AP';
}

function getDoc(id: number): any {
  if (!docStore.has(id)) {
    const mod = moduleForId(id);
    const row = inbox.find((r) => r.DocId === id);
    docStore.set(id, buildDoc(mod, id, row?.FileName || 'sample_invoice.pdf'));
  }
  return docStore.get(id);
}

// Build a passing MapResult for a stored doc so the mapping cards render.
function buildMap(doc: any): any {
  const f = (label: string, value: any, match = true) => ({ label, value: String(value ?? ''), match });
  const header: any = {};
  if (doc.module === 'SO') {
    header.customer = {
      status: 'ok', code: '0010001', method: 'Matched by Tax ID', sapCode: '0000100023', text: 'Siam Chemical Industry Co., Ltd.',
      doc: [f('Customer Name', doc.header.customerName), f('Tax ID', doc.header.customerTaxId)],
      sap: [f('Customer Code', '0010001'), f('Name', 'Siam Chemical Industry Co., Ltd.'), f('Sold-to SAP', '0000100023')],
    };
    header.shipTo = {
      status: 'ok', code: '0010001-01', method: 'Matched by location name', sapCode: '0000100024', text: 'Bang Pu Warehouse',
      doc: [f('Ship-to Location', doc.header.shipToName)], sap: [f('Ship-to Code', '0010001-01'), f('Name', 'Bang Pu Warehouse')],
    };
  } else {
    header.vendor = {
      status: 'ok', code: 'V-500012', method: 'Matched by Tax ID', sapCode: '0000200015', text: 'Universal Chemical Supply Co., Ltd.',
      doc: [f('Vendor Name', doc.header.vendorName), f('Tax ID', doc.header.vendorTaxId)],
      sap: [f('Vendor Code', 'V-500012'), f('Name', 'Universal Chemical Supply Co., Ltd.'), f('Supplier SAP', '0000200015')],
    };
  }
  const lines = (doc.lines || []).map((l: any) => ({
    status: 'ok', code: 'RM-200011', method: 'Matched by partner code', sapCode: '000000000000200011',
    doc: [f('Item', l.desc), f('Quantity', l.qty + ' ' + l.uom)],
    sap: [f('Material', 'RM-200011'), f('Description', 'Methyl Ethyl Ketone (MEK) 99.5%')],
    uom: { status: 'ok', sapQty: Number(l.qty), sapUom: l.uom, factor: 1, method: 'Units match' },
    unit: { status: 'ok', doc: [f('Document Unit', l.uom)], sap: [f('SAP Unit', l.uom)] },
  }));
  return { document: doc, pass: true, errors: [], warns: [], header, lines };
}

const MOCKS: Record<string, unknown> = {
  'GET /api/ocr/providers': providers,
  'GET /api/ap-doc-categories': apDocCategories,
  'GET /api/health': health,
  'GET /api/masters': masters,
  'GET /api/logs': logs,
  'GET /api/audit-logs': auditLogs,
};

/** Return mock data for a request, or undefined if none is defined. */
export function getMock(method: string, url: string, body?: unknown): unknown {
  const clean = url.split('?')[0];
  const key = method + ' ' + clean;
  if (key in MOCKS) return MOCKS[key];

  // GET
  if (method === 'GET' && clean === '/api/dashboard') {
    const m = url.match(/days=(\d+)/);
    return dashboard(m ? parseInt(m[1]) : 7);
  }
  if (method === 'GET' && clean === '/api/documents') {
    const m = url.match(/[?&]module=([A-Za-z]+)/);
    return m ? inbox.filter((r) => r.Module === m[1]) : inbox;
  }
  if (method === 'GET' && /^\/api\/documents\/\d+$/.test(clean)) return getDoc(idOf(clean));
  if (method === 'GET' && /^\/api\/documents\/\d+\/chat$/.test(clean)) return [];
  if (method === 'GET' && /^\/api\/documents\/\d+\/rawtext$/.test(clean))
    return { text: '(mock) Sample text extracted from the file\nINVOICE No. INV-2024-0042\nMEK 99.5 PCT  200 L  600  120,000' };
  if (method === 'GET' && /^\/api\/documents\/\d+\/payload$/.test(clean))
    return { payload: { _target: 'API_SUPPLIERINVOICE_PROCESS_SRV', CompanyCode: '1000', DocumentDate: '2026-08-30', _mock: true } };

  // POST — attach / actions
  if (method === 'POST' && clean === '/api/documents/upload') {
    const module = formVal(body, 'module') || 'AP';
    const file = formVal(body, 'file') || 'attached.pdf';
    const id = nextId++;
    const doc = buildDoc(module, id, file);
    docStore.set(id, doc);
    return doc;
  }
  if (method === 'POST' && clean === '/api/documents/sample') {
    const b: any = body || {};
    const id = nextId++;
    const doc = buildDoc(b.module || 'AP', id, 'sample_' + (b.module || 'AP') + '.pdf');
    docStore.set(id, doc);
    return doc;
  }
  if (method === 'POST' && /^\/api\/documents\/\d+\/map$/.test(clean)) {
    const doc = getDoc(idOf(clean));
    const b: any = body || {};
    if (b.header) doc.header = { ...doc.header, ...b.header };
    if (b.lines) doc.lines = b.lines;
    doc.status = 'MAPPED';
    return buildMap(doc);
  }
  if (method === 'POST' && /^\/api\/documents\/\d+\/reocr$/.test(clean)) {
    const doc = getDoc(idOf(clean));
    doc.provider = 'demo';
    return doc;
  }
  if (method === 'POST' && /^\/api\/documents\/\d+\/category$/.test(clean)) {
    const doc = getDoc(idOf(clean));
    doc.apDocCategory = (body as any)?.apDocCategory || '';
    return doc;
  }
  if (method === 'POST' && /^\/api\/documents\/\d+\/chat-fix$/.test(clean)) {
    return { document: getDoc(idOf(clean)) };
  }
  if (method === 'POST' && /^\/api\/documents\/\d+\/post$/.test(clean)) {
    const doc = getDoc(idOf(clean));
    doc.status = 'POSTED';
    doc.sapDocNo = (doc.module === 'SO' ? '00' : '51') + Math.floor(100000 + Math.random() * 900000);
    doc.postedAt = new Date().toISOString();
    return { document: doc, simulated: true, sapDocNo: doc.sapDocNo };
  }
  if (method === 'POST' && /^\/api\/documents\/\d+\/split$/.test(clean)) {
    const doc = getDoc(idOf(clean));
    doc.status = 'SPLIT';
    return { source: doc, created: [{ DocId: nextId++ }, { DocId: nextId++ }] };
  }
  if (method === 'POST' && /^\/api\/documents\/\d+\/learn$/.test(clean)) return {};

  // Masters CRUD — no-op success
  if (method === 'POST' && /^\/api\/masters\//.test(clean)) return {};
  if (method === 'PUT' && /^\/api\/masters\//.test(clean)) return {};
  if (method === 'DELETE' && /^\/api\/masters\//.test(clean)) return {};
  if (method === 'DELETE' && /^\/api\/documents\//.test(clean)) return {};

  return undefined;
}

function idOf(path: string): number {
  const m = path.match(/\/documents\/(\d+)/);
  return m ? parseInt(m[1]) : 0;
}
function formVal(body: unknown, key: string): string {
  if (body instanceof FormData) {
    const v = body.get(key);
    if (typeof v === 'string') return v;
    if (v && 'name' in (v as any)) return (v as any).name;
    return '';
  }
  if (body && typeof body === 'object') return String((body as any)[key] ?? '');
  return '';
}

export const MOCK_ALWAYS = import.meta.env.VITE_USE_MOCK === '1';
