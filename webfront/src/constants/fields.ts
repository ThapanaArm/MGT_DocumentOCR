/* =====================================================================
   Header/detail field definitions & master-data schema — ported verbatim
   from app.js so the ported screens render the exact same fields.
   ===================================================================== */

// A header field is one of:
//   [key, label]                              → text input
//   [key, label, 'checkbox']                  → checkbox (value 'X' when checked)
//   [key, label, 'select', [value,label][]]   → dropdown
export type FieldDef =
  | [string, string]
  | [string, string, 'checkbox']
  | [string, string, 'select', [string, string][]];
export interface FieldGroup {
  title: string;
  fields: FieldDef[];
  required?: string[]; // keys shown with a required (*) marker
}

export const SO_H: FieldDef[] = [
  ['docType', 'ประเภทเอกสาร'], ['poNo', 'เลขที่ใบสั่งซื้อลูกค้า'], ['poDate', 'วันที่เอกสาร'],
  ['customerName', 'ชื่อลูกค้า'], ['customerTaxId', 'เลขทะเบียนนิติบุคคล'], ['shipToName', 'สถานที่ส่งของ'],
  ['shipToAddress', 'ที่อยู่จัดส่ง'], ['deliveryDate', 'วันที่ต้องการรับสินค้า'], ['currency', 'สกุลเงิน'],
  ['paymentTerms', 'เงื่อนไขชำระเงิน'], ['incoterms', 'Incoterms'],
];
export const SO_TOTALS_H: FieldDef[] = [
  ['subTotal', 'มูลค่าก่อนภาษี'], ['vatAmount', 'ภาษีมูลค่าเพิ่ม'], ['totalAmount', 'ยอดรวมทั้งสิ้น'],
];
export const SO_REMARK_H: FieldDef[] = [['remark', 'หมายเหตุ']];

export const AP_H: FieldDef[] = [
  ['docType', 'ประเภทเอกสาร'], ['invoiceNo', 'เลขที่ใบแจ้งหนี้'], ['invoiceDate', 'วันที่ใบแจ้งหนี้'],
  ['postingDate', 'วันที่ผ่านรายการ'], ['vendorName', 'ชื่อผู้ขาย'], ['vendorTaxId', 'เลขทะเบียนนิติบุคคล'],
  ['branch', 'สาขา'], ['poRef', 'อ้างอิง PO'], ['currency', 'สกุลเงิน'], ['paymentTerms', 'เงื่อนไขชำระเงิน'],
];
export const AP_TOTALS_H: FieldDef[] = [
  ['subTotal', 'มูลค่าก่อนภาษี'], ['vatRate', 'อัตราภาษี (%)'], ['vatAmount', 'ภาษีมูลค่าเพิ่ม'],
  ['whtAmount', 'ภาษีหัก ณ ที่จ่าย'], ['totalAmount', 'ยอดรวมสุทธิ'],
];

export const II_H: FieldDef[] = [
  ['transaction', 'Transaction'], ['invoiceNo', 'Reference'], ['invoiceDate', 'Invoice Date'],
  ['postingDate', 'Posting Date'], ['vendorName', 'Supplier'], ['vendorTaxId', 'เลขทะเบียนนิติบุคคล'],
  ['sapDocType', 'Document type'], ['currency', 'Currency'],
];
export const II_TOTALS_H: FieldDef[] = [
  ['totalAmount', 'Amount'], ['vatAmount', 'Tax Amount'], ['whtAmount', 'Withholding Tax Amount FC'],
];
// One-time vendor Address & Bank Data (SAP FB60 popup, shown here as a card).
export const II_ONETIME_H: FieldDef[] = [
  ['language', 'Language'], ['vendorName', 'Name'], ['addressStreet', 'Street'],
  ['addressCity', 'City'], ['addressPostalCode', 'Postal Code'], ['addressCountry', 'Ctry/Reg.'],
  ['vendorEmail', 'E-Mail'], ['bankCountry', 'Bank Ctry/Reg.'], ['bankKey', 'Bank Key'],
  ['bankAccountNo', 'Bank Account'], ['vendorTaxId', 'Tax Number 3'],
];
// SAP "Enter Supplier Invoice" (FB60 · no PO) tabs. Transaction sits in the top
// strip (see IncomingInvoiceCard), so Basic Data starts at Supplier.
export const II_GROUPS: FieldGroup[] = [
  {
    title: 'Basic Data',
    required: ['vendorName', 'sapDocType', 'businessPlace'],
    fields: [
      ['vendorName', 'Supplier'],           // 2)
      ['invoiceDate', 'Invoice Date'],      // 3)
      ['invoiceNo', 'Reference'],           // 4)
      ['postingDate', 'Posting Date'],      // 5)
      ['sapDocType', 'Document Type', 'select', [ // 6)
        ['KR', 'KR - Vendor Invoice'],
        ['KG', 'KG - Vendor Credit Memo'],
        ['RE', 'RE - Invoice (Gross)'],
        ['RN', 'RN - Invoice (Net)'],
      ]],
      ['totalAmount', 'Amount'],            // 7)
      ['currency', 'Currency'],             // 8)
      ['calculateTax', 'Calculate Tax', 'checkbox'], // 9)
      ['vatAmount', 'Tax Amount'],          // 10)
      ['taxCode', 'Tax Code', 'select', [   // 11)
        ['', '—'],
        ['V1', 'V1 - Input VAT 7%'],
        ['D1', 'D1 - Input Deferred Tax 7%'],
        ['V0', 'V0 - Input VAT 0%'],
        ['VN', 'VN - No Tax'],
      ]],
      ['businessPlace', 'Business Place'],  // 12)
      ['headerText', 'Text'],               // 13)
    ],
  },
  {
    title: 'Payment',
    fields: [
      ['baselineDate', 'Baseline Date'],
      ['paymentTerms', 'Payment Terms'],
      ['dueDate', 'Due On'],
      ['paymentMethod', 'Payment Method'],
      ['paymentBlock', 'Payment Block', 'select', [
        ['', 'Free for payment'],
        ['A', 'A - Blocked for payment'],
        ['B', 'B - Blocked for payment'],
        ['R', 'R - Invoice verification'],
      ]],
      ['partnerBank', 'Partner Bank'],
      ['houseBank', 'House Bank'],
      ['bankAccountId', 'Account ID'],
    ],
  },
  {
    title: 'Details',
    fields: [
      ['assignmentText', 'Assignment'],
      ['headerText', 'Header Text'],
      ['refKey1', 'Ref. Key 1'],
      ['refKey2', 'Ref. Key 2'],
      ['refKey3', 'Ref. Key 3'],
    ],
  },
  {
    title: 'Tax',
    fields: [
      ['taxReportingDate', 'Tax Reporting Date'],
      ['taxFulfillDate', 'Tax Fulfill Date'],
      ['taxDate', 'Tax Date'],
      ['calculateTax', 'Calculate Tax', 'checkbox'],
    ],
  },
  {
    title: 'Withholding Tax',
    fields: [],
  },
  // {
  //   title: 'Notes',
  //   fields: [['notes', 'Notes']],
  // },
];

export const PODP_H: FieldDef[] = [
  ['docType', 'ประเภทเอกสาร'], ['invoiceNo', 'เลขที่เอกสาร'], ['invoiceDate', 'วันที่เอกสาร'],
  ['postingDate', 'วันที่ผ่านรายการ'], ['vendorName', 'ผู้ขาย'], ['vendorTaxId', 'เลขทะเบียนนิติบุคคล'],
  ['poRef', 'อ้างอิง PO'], ['currency', 'สกุลเงิน'], ['paymentTerms', 'เงื่อนไขชำระเงิน'],
];
export const PODP_TOTALS_H: FieldDef[] = [['totalAmount', 'จำนวนเงินมัดจำ']];

export const AP_TRADE_GROUPS: FieldGroup[] = [
  {
    title: 'Basic Data',
    fields: [
      // 1) Transaction
      ['transaction', 'Transaction', 'select', [
        ['Invoice', '1 Invoice'],
        ['CreditMemo', '2 Credit Memo'],
        ['SubsequentDebit', '3 Subsequent Debit'],
        ['SubsequentCredit', '4 Subsequent Credit'],
      ]],
      ['invoiceDate', 'Invoice Date'],     // 2)
      ['invoiceNo', 'Reference'],          // 3)
      ['postingDate', 'Posting Date'],     // 4)
      ['totalAmount', 'Amount'],           // 5)
      ['currency', 'Currency'],            // 6)
      ['calculateTax', 'Calculate Tax', 'checkbox'], // 7)
      ['taxCode', 'Tax Code'],             // 8)
      ['businessPlace', 'Business Place'], // 9)
      ['headerText', 'Text'],              // 10)
    ],
  },
  {
    title: 'PO Reference',
    fields: [
      ['refDocument', 'Reference Document', 'select', [
        ['PO', 'Purchase Order / Scheduling Agreement'],
        ['DELIVERY_NOTE', 'Delivery Note (Goods Only)'],
        ['BILL_OF_LADING', 'Bill of Lading (Planned Delivery Costs Only)'],
        ['SUPPLIER', 'Supplier'],
        ['OUTBOUND_DELIVERY', 'Outbound Delivery'],
        ['SERVICE_ENTRY', 'Service Entry Sheet - Lean Services'],
      ]],
      ['PurchasingOrder','Purchasing Order'],
      ['refDocType', 'Reference Document Category', 'select', [
        ['1', 'Goods/service items'],
        ['2', 'Planned delivery costs'],
        ['3', 'Goods/service items + planned delivery costs'],
      ]],
      ['layout', 'Layout', 'select', [
        ['ALL', 'All information'],
        ['ACCT_COST_CENTER', 'Acct Assignment - Cost Center'],
        ['ACCT_ASSET_ORDER', 'Acct assignment - asset, order'],
        ['INVOICE_REDUCTION', 'Invoice reduction'],
        ['PO_JURISDICTION', 'PO - Jurisdiction Code'],
        ['PO_ORDER_PRICE_QTY', 'PO - Order Price Quantity'],
        ['PO_HISTORY', 'PO history'],
        ['PO_DELIVERY_NOTE', 'Purchase order - delivery note'],
        ['PO_ENTRY_SHEET', 'Purchase order - entry sheet'],
        ['SERVICES', 'Services'],
        ['TRANSFERS', 'Transfers'],
      ]],
    ],
  },
  {
    title: 'Payment',
    fields: [
      ['baselineDate', 'Baseline Date'],  
      ['dueDate', 'Due Date'], 
      ['paymentTerms', 'Payment Terms'],
      ['paymentMethod', 'Payment Method'], 
      ['paymentBlock', 'Payment Block', 'select', [ 
        ['', 'Free for payment'],
        ['A', 'A - Blocked for payment'],
        ['B', 'B - Blocked for payment'],
        ['R', 'R - Invoice verification'],
        ['V', 'V - Payment clearing'],
      ]],
      ['partnerBank', 'Partner Bank'],     
      ['houseBank', 'House Bank'],        
      ['bankAccountId', 'Account ID'],    
    ],
  },
  {
    title: 'Details',
    // Matches the SAP "Enter Incoming Invoice · Details" tab (numbered fields 2-7; 1 is the tab).
    fields: [
      ['unplannedDeliveryCost', 'Unplanned Delivery Costs'], // 2)
      ['sapDocType', 'Document Type', 'select', [            // 3)
        ['RE', 'RE - Invoice - Gross'],
        ['RN', 'RN - Invoice - Net'],
        ['KR', 'KR - Vendor Invoice'],
        ['KG', 'KG - Vendor Credit Memo'],
      ]],
      ['invoicingParty', 'Invoicing Party'],  // 4)
      ['assignmentText', 'Assignment'],        // 5)
      ['glAccountHeader', 'G/L Account'],      // 6)
      ['headerText', 'Header Text'],           // 7)
    ],
  },
  {
    title: 'Tax',
    fields: [
      ['taxReportingDate', 'Tax Reporting Date'], // 5)
      ['taxFulfillDate', 'Tax Fulfill Date'],     // 6)
      ['taxDate', 'Tax Date'],                     // 7)
      ['calculateTax', 'Calculate Tax', 'checkbox'], // 8)
    ],
  },
  {
    title: 'Withholding Tax',
    fields: [],
  },
];

export const PO_LINE_EXTRA_FIELDS: FieldDef[] = [
  ['accountAssignment', 'Account Assignment Category', 'select', [['', '— เลือก —'], ['K', 'K — Cost Center'], ['A', 'A — Asset'], ['P', 'P — Project']]],
  ['itemCategory', 'Item Category', 'select', [['', '— เลือก —'], ['STANDARD', 'Standard'], ['SERVICE', 'Service']]],
  ['plant', 'Plant'], ['glAccount', 'G/L Account'], ['costCenter', 'Cost Center'],
  ['internalOrder', 'Internal Order'], ['wbsElement', 'WBS Element'], ['assetNumber', 'Asset Number'],
  ['taxCode', 'Tax Code'], ['deliveryDate', 'Delivery Date'],
  ['grIndicator', 'GR Indicator', 'select', [['', '— เลือก —'], ['YES', 'ใช่ — ต้องรับของ/บริการ'], ['NO', 'ไม่ใช่']]],
  ['irIndicator', 'IR Indicator', 'select', [['', '— เลือก —'], ['YES', 'ใช่ — รับ Invoice ได้'], ['NO', 'ไม่ใช่']]],
  ['grBasedIv', 'GR-Based IV', 'select', [['', '— เลือก —'], ['YES', 'ใช่ — ต้องอ้างอิงรายการที่รับแล้ว'], ['NO', 'ไม่ใช่']]],
];

export const AA_GUIDE: Record<string, { hint: string; field: string }> = {
  '': { hint: 'ของเข้า Stock ปกติไม่ใช้ Account Assignment (เว้นว่างไว้ได้ เว้นแต่ Configuration บริษัทกำหนดไว้)', field: '' },
  K: { hint: 'ค่าใช้จ่ายของแผนก → กรอก Cost Center (หรือ Internal Order เพิ่มถ้าเป็นค่าใช้จ่ายเฉพาะกิจกรรม)', field: 'costCenter' },
  A: { hint: 'ซื้อทรัพย์สินถาวร → กรอก Asset Number', field: 'assetNumber' },
  P: { hint: 'ค่าใช้จ่ายโครงการ → กรอก WBS Element', field: 'wbsElement' },
};

export const OCR_PROVIDER_SHORT: Record<string, string> = {
  auto: 'อัตโนมัติ', text: 'ข้อความในไฟล์', ocr: 'Tesseract OCR', tesseract: 'Tesseract OCR',
  typhoon: 'Typhoon', azure: 'Azure', claude_text: 'Claude (text)', claude: 'Claude', gemini: 'Gemini',
  openai: 'ChatGPT', demo: 'ตัวอย่าง', failed: 'อ่านไม่สำเร็จ',
};

// ---- Master-data schema ----
export interface MasterCol {
  k: string;
  l: string;
  sap?: boolean;
  ref?: string;
  blank?: boolean;
}
export interface MasterDef {
  label: string;
  mod: string;
  key: string;
  cols: MasterCol[];
}

export const MASTER_DEF: Record<string, MasterDef> = {
  customers: {
    label: 'ลูกค้า (Customer)', mod: 'SO', key: 'CustomerCode', cols: [
      { k: 'CustomerCode', l: 'รหัสลูกค้า (ภายใน)' }, { k: 'SapCustomerCode', l: 'รหัสใน SAP (Sold-to)', sap: true },
      { k: 'NameTh', l: 'ชื่อ (TH)' }, { k: 'NameEn', l: 'ชื่อ (EN)' },
      { k: 'TaxId', l: 'เลขทะเบียน / ผู้เสียภาษี' }, { k: 'Branch', l: 'สาขา' }, { k: 'SalesOrg', l: 'Sales Org' },
      { k: 'DistChannel', l: 'Distr.Ch' }, { k: 'Division', l: 'Div' }, { k: 'Currency', l: 'สกุลเงิน' },
      { k: 'PaymentTerms', l: 'Payment Terms' }],
  },
  shiptos: {
    label: 'สถานที่ส่งของ (Ship-to)', mod: 'SO', key: 'ShipToCode', cols: [
      { k: 'CustomerCode', l: 'รหัสลูกค้า', ref: 'customers' }, { k: 'ShipToCode', l: 'รหัส Ship-to (ภายใน)' },
      { k: 'SapShipToCode', l: 'รหัสใน SAP (Ship-to)', sap: true }, { k: 'ShipToName', l: 'ชื่อสถานที่' }, { k: 'Address', l: 'ที่อยู่' }],
  },
  custmaterials: {
    label: 'สินค้าฝั่งลูกค้า', mod: 'SO', key: 'Id', cols: [
      { k: 'CustomerCode', l: 'รหัสลูกค้า', ref: 'customers' }, { k: 'ExtCode', l: 'รหัสสินค้าของลูกค้า' },
      { k: 'ExtDesc', l: 'ชื่อสินค้าของลูกค้า' }, { k: 'MaterialCode', l: 'Material (SAP)', ref: 'materials' }],
  },
  vendors: {
    label: 'ผู้ขาย (Vendor)', mod: 'AP', key: 'VendorCode', cols: [
      { k: 'VendorCode', l: 'รหัสผู้ขาย (ภายใน)' }, { k: 'SapVendorCode', l: 'รหัสใน SAP (Supplier)', sap: true },
      { k: 'VendorName', l: 'ชื่อผู้ขาย' },
      { k: 'TaxId', l: 'เลขทะเบียน / ผู้เสียภาษี' }, { k: 'Branch', l: 'สาขา' }, { k: 'Currency', l: 'สกุลเงิน' },
      { k: 'PaymentTerms', l: 'Payment Terms' }, { k: 'ReconAcct', l: 'Recon. Account' }, { k: 'WhtCode', l: 'ภาษีหัก ณ ที่จ่าย' }],
  },
  venmaterials: {
    label: 'สินค้าฝั่งผู้ขาย', mod: 'AP', key: 'Id', cols: [
      { k: 'VendorCode', l: 'รหัสผู้ขาย', ref: 'vendors' }, { k: 'ExtCode', l: 'รหัสสินค้าของผู้ขาย' },
      { k: 'ExtDesc', l: 'ชื่อสินค้าของผู้ขาย' }, { k: 'MaterialCode', l: 'Material (SAP)', ref: 'materials' }],
  },
  uoms: {
    label: 'การแปลงหน่วย (UoM)', mod: 'ALL', key: 'Id', cols: [
      { k: 'MaterialCode', l: 'Material (เว้นว่าง = ทุกสินค้า)', ref: 'materials', blank: true },
      { k: 'ExtUom', l: 'หน่วยตามเอกสาร' }, { k: 'SapUom', l: 'หน่วยใน SAP' },
      { k: 'SapUomIso', l: 'ISO code', sap: true },
      { k: 'Factor', l: 'ตัวคูณ (1 หน่วยเอกสาร = ? หน่วย SAP)' }, { k: 'Note', l: 'หมายเหตุ' }],
  },
  materials: {
    label: 'สินค้า/บริการ (Material)', mod: 'ALL', key: 'MaterialCode', cols: [
      { k: 'MaterialCode', l: 'รหัส Material (ภายใน)' }, { k: 'SapMaterialCode', l: 'รหัสใน SAP (Material)', sap: true },
      { k: 'Description', l: 'รายละเอียด' }, { k: 'Uom', l: 'หน่วยฐาน' },
      { k: 'Plant', l: 'Plant' }, { k: 'MatGroup', l: 'Material Group' }],
  },
};

export const MASTER_NOTE: Record<string, string> = {
  customers: 'ใช้จับคู่ Sold-to: ตรวจจาก เลขทะเบียนนิติบุคคล ก่อน ถ้าไม่พบจึงเทียบ ชื่อ (ความคล้าย ≥ 82%)',
  shiptos: 'ใช้จับคู่ Ship-to จาก ชื่อสถานที่/ที่อยู่ ภายใต้ลูกค้าเดียวกัน',
  custmaterials: 'ใช้แปลง รหัส/ชื่อสินค้าของลูกค้า เป็น Material ของ SAP',
  vendors: 'ใช้จับคู่ Vendor: ตรวจจาก เลขทะเบียนนิติบุคคล ก่อน ถ้าไม่พบจึงเทียบ ชื่อ',
  venmaterials: 'ใช้แปลง รหัส/ชื่อสินค้าของผู้ขาย เป็น Material ของ SAP',
  materials: 'ข้อมูล Material ของ SAP (ควร replicate จาก S/4HANA)',
  uoms: 'แปลงหน่วยตามเอกสารของคู่ค้าเป็นหน่วยของ SAP เช่น 1 BAG = 25 KG — ระบบจะหากฎเฉพาะสินค้าก่อน ถ้าไม่พบจึงใช้กฎกลาง (แถวที่ไม่ระบุ Material)',
};

export interface MasterGroup {
  key: string;
  label: string;
  mod: string;
  tabs: string[];
  note: string;
}
export const MASTER_GROUPS: MasterGroup[] = [
  { key: 'vendor', label: '1. Vendor / Supplier', mod: 'AP', tabs: ['vendors'], note: 'ตรวจจาก เลขทะเบียนนิติบุคคล 13 หลัก ก่อน ถ้าไม่พบจึงเทียบ ชื่อผู้ขาย (ความคล้าย ≥ 82%)' },
  { key: 'customer', label: '2. Customer', mod: 'SO', tabs: ['customers'], note: 'ตรวจจาก เลขทะเบียนนิติบุคคล 13 หลัก ก่อน ถ้าไม่พบจึงเทียบ ชื่อลูกค้า (ไทย/อังกฤษ) (ความคล้าย ≥ 82%)' },
  { key: 'shipto', label: '3. Ship-to', mod: 'SO', tabs: ['shiptos'], note: 'เทียบ ชื่อสถานที่ + ที่อยู่จัดส่ง เฉพาะภายใต้ลูกค้าที่จับคู่ได้แล้ว (ความคล้าย ≥ 70%)' },
  { key: 'material', label: '4. Material', mod: 'ALL', tabs: ['materials', 'custmaterials', 'venmaterials', 'uoms'], note: 'ตรวจ รหัสสินค้าของคู่ค้า ตรงตัวก่อน → ชื่อสินค้าของคู่ค้า ≥ 85% → Material master ≥ 93% จากนั้นจึง แปลงหน่วย ให้ตรงกับหน่วยของ Material' },
];

export const M_LABEL: Record<string, string> = {
  customers: 'NameTh', vendors: 'VendorName', materials: 'Description', shiptos: 'ShipToName',
};

export const headerDefFor = (module: string): FieldDef[] =>
  module === 'SO' ? SO_H : module === 'II' ? II_H : module === 'PODP' ? PODP_H : AP_H;
