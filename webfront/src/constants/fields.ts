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
  ['docType', 'Document Type'], ['poNo', 'Customer PO No.'], ['poDate', 'Document Date'],
  ['customerName', 'Customer Name'], ['customerTaxId', 'Tax ID'], ['shipToName', 'Ship-to Location'],
  ['shipToAddress', 'Delivery Address'], ['deliveryDate', 'Requested Delivery Date'], ['currency', 'Currency'],
  ['paymentTerms', 'Payment Terms'], ['incoterms', 'Incoterms'],
];
export const SO_TOTALS_H: FieldDef[] = [
  ['subTotal', 'Amount before Tax'], ['vatAmount', 'VAT'], ['totalAmount', 'Grand Total'],
];
export const SO_REMARK_H: FieldDef[] = [['remark', 'Remark']];

export const AP_H: FieldDef[] = [
  ['docType', 'Document Type'], ['invoiceNo', 'Invoice No.'], ['invoiceDate', 'Invoice Date'],
  ['postingDate', 'Posting Date'], ['vendorName', 'Vendor Name'], ['vendorTaxId', 'Tax ID'],
  ['branch', 'Branch'], ['poRef', 'PO Reference'], ['currency', 'Currency'], ['paymentTerms', 'Payment Terms'],
];
export const AP_TOTALS_H: FieldDef[] = [
  ['subTotal', 'Amount before Tax'], ['vatRate', 'Tax Rate (%)'], ['vatAmount', 'VAT'],
  ['whtAmount', 'Withholding Tax'], ['totalAmount', 'Net Total'],
];

export const II_H: FieldDef[] = [
  ['transaction', 'Transaction'], ['invoiceNo', 'Reference'], ['invoiceDate', 'Invoice Date'],
  ['postingDate', 'Posting Date'], ['vendorName', 'Supplier'], ['vendorTaxId', 'Tax ID'],
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
  ['docType', 'Document Type'], ['invoiceNo', 'Document No.'], ['invoiceDate', 'Document Date'],
  ['postingDate', 'Posting Date'], ['vendorName', 'Vendor'], ['vendorTaxId', 'Tax ID'],
  ['poRef', 'PO Reference'], ['currency', 'Currency'], ['paymentTerms', 'Payment Terms'],
];
export const PODP_TOTALS_H: FieldDef[] = [['totalAmount', 'Down Payment Amount']];

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
  ['accountAssignment', 'Account Assignment Category', 'select', [['', '— Select —'], ['K', 'K — Cost Center'], ['A', 'A — Asset'], ['P', 'P — Project']]],
  ['itemCategory', 'Item Category', 'select', [['', '— Select —'], ['STANDARD', 'Standard'], ['SERVICE', 'Service']]],
  ['plant', 'Plant'], ['glAccount', 'G/L Account'], ['costCenter', 'Cost Center'],
  ['internalOrder', 'Internal Order'], ['wbsElement', 'WBS Element'], ['assetNumber', 'Asset Number'],
  ['taxCode', 'Tax Code'], ['deliveryDate', 'Delivery Date'],
  ['grIndicator', 'GR Indicator', 'select', [['', '— Select —'], ['YES', 'Yes — Goods/service receipt required'], ['NO', 'No']]],
  ['irIndicator', 'IR Indicator', 'select', [['', '— Select —'], ['YES', 'Yes — Invoice receipt allowed'], ['NO', 'No']]],
  ['grBasedIv', 'GR-Based IV', 'select', [['', '— Select —'], ['YES', 'Yes — Must reference goods already received'], ['NO', 'No']]],
];

export const AA_GUIDE: Record<string, { hint: string; field: string }> = {
  '': { hint: 'Goods received into normal stock do not use Account Assignment (can be left blank unless required by company configuration)', field: '' },
  K: { hint: 'Departmental expense → enter Cost Center (or add Internal Order for activity-specific expenses)', field: 'costCenter' },
  A: { hint: 'Fixed asset purchase → enter Asset Number', field: 'assetNumber' },
  P: { hint: 'Project expense → enter WBS Element', field: 'wbsElement' },
};

export const OCR_PROVIDER_SHORT: Record<string, string> = {
  auto: 'Automatic', text: 'Text in File', ocr: 'Tesseract OCR', tesseract: 'Tesseract OCR',
  typhoon: 'Typhoon', azure: 'Azure', claude_text: 'Claude (text)', claude: 'Claude', gemini: 'Gemini',
  openai: 'ChatGPT', demo: 'Demo', failed: 'Read Failed',
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
    label: 'Customer', mod: 'SO', key: 'CustomerCode', cols: [
      { k: 'CustomerCode', l: 'Customer Code (Internal)' }, { k: 'SapCustomerCode', l: 'SAP Code (Sold-to)', sap: true },
      { k: 'NameTh', l: 'Name (TH)' }, { k: 'NameEn', l: 'Name (EN)' },
      { k: 'TaxId', l: 'Tax ID' }, { k: 'Branch', l: 'Branch' }, { k: 'SalesOrg', l: 'Sales Org' },
      { k: 'DistChannel', l: 'Distr.Ch' }, { k: 'Division', l: 'Div' }, { k: 'Currency', l: 'Currency' },
      { k: 'PaymentTerms', l: 'Payment Terms' }],
  },
  shiptos: {
    label: 'Ship-to Location', mod: 'SO', key: 'ShipToCode', cols: [
      { k: 'CustomerCode', l: 'Customer Code', ref: 'customers' }, { k: 'ShipToCode', l: 'Ship-to Code (Internal)' },
      { k: 'SapShipToCode', l: 'SAP Code (Ship-to)', sap: true }, { k: 'ShipToName', l: 'Location Name' }, { k: 'Address', l: 'Address' }],
  },
  custmaterials: {
    label: 'Customer Materials', mod: 'SO', key: 'Id', cols: [
      { k: 'CustomerCode', l: 'Customer Code', ref: 'customers' }, { k: 'ExtCode', l: "Customer's Item Code" },
      { k: 'ExtDesc', l: "Customer's Item Description" }, { k: 'MaterialCode', l: 'Material (SAP)', ref: 'materials' }],
  },
  vendors: {
    label: 'Vendor', mod: 'AP', key: 'VendorCode', cols: [
      { k: 'VendorCode', l: 'Vendor Code (Internal)' }, { k: 'SapVendorCode', l: 'SAP Code (Supplier)', sap: true },
      { k: 'VendorName', l: 'Vendor Name' },
      { k: 'TaxId', l: 'Tax ID' }, { k: 'Branch', l: 'Branch' }, { k: 'Currency', l: 'Currency' },
      { k: 'PaymentTerms', l: 'Payment Terms' }, { k: 'ReconAcct', l: 'Recon. Account' }, { k: 'WhtCode', l: 'Withholding Tax' }],
  },
  venmaterials: {
    label: 'Vendor Materials', mod: 'AP', key: 'Id', cols: [
      { k: 'VendorCode', l: 'Vendor Code', ref: 'vendors' }, { k: 'ExtCode', l: "Vendor's Item Code" },
      { k: 'ExtDesc', l: "Vendor's Item Description" }, { k: 'MaterialCode', l: 'Material (SAP)', ref: 'materials' }],
  },
  uoms: {
    label: 'Unit Conversion (UoM)', mod: 'ALL', key: 'Id', cols: [
      { k: 'MaterialCode', l: 'Material (blank = all materials)', ref: 'materials', blank: true },
      { k: 'ExtUom', l: 'Document Unit' }, { k: 'SapUom', l: 'SAP Unit' },
      { k: 'SapUomIso', l: 'ISO code', sap: true },
      { k: 'Factor', l: 'Factor (1 document unit = ? SAP units)' }, { k: 'Note', l: 'Note' }],
  },
  materials: {
    label: 'Material / Service', mod: 'ALL', key: 'MaterialCode', cols: [
      { k: 'MaterialCode', l: 'Material Code (Internal)' }, { k: 'SapMaterialCode', l: 'SAP Code (Material)', sap: true },
      { k: 'Description', l: 'Description' }, { k: 'Uom', l: 'Base Unit' },
      { k: 'Plant', l: 'Plant' }, { k: 'MatGroup', l: 'Material Group' }],
  },
};

export const MASTER_NOTE: Record<string, string> = {
  customers: 'Used to match Sold-to: checks the Tax ID first, then compares the name if not found (similarity ≥ 82%)',
  shiptos: 'Used to match Ship-to by location name/address under the same customer',
  custmaterials: "Used to convert the customer's item code/name to a SAP Material",
  vendors: 'Used to match Vendor: checks the Tax ID first, then compares the name if not found',
  venmaterials: "Used to convert the vendor's item code/name to a SAP Material",
  materials: 'SAP Material data (should be replicated from S/4HANA)',
  uoms: 'Converts the partner document unit to a SAP unit, e.g. 1 BAG = 25 KG — the system looks for a material-specific rule first, then falls back to the global rule (rows without a Material)',
};

export interface MasterGroup {
  key: string;
  label: string;
  mod: string;
  tabs: string[];
  note: string;
}
export const MASTER_GROUPS: MasterGroup[] = [
  { key: 'vendor', label: '1. Vendor / Supplier', mod: 'AP', tabs: ['vendors'], note: 'Checks the 13-digit Tax ID first, then compares the vendor name if not found (similarity ≥ 82%)' },
  { key: 'customer', label: '2. Customer', mod: 'SO', tabs: ['customers'], note: 'Checks the 13-digit Tax ID first, then compares the customer name (Thai/English) if not found (similarity ≥ 82%)' },
  { key: 'shipto', label: '3. Ship-to', mod: 'SO', tabs: ['shiptos'], note: 'Compares location name + delivery address, only under a customer that has already been matched (similarity ≥ 70%)' },
  { key: 'material', label: '4. Material', mod: 'ALL', tabs: ['materials', 'custmaterials', 'venmaterials', 'uoms'], note: "Checks the partner's item code for an exact match first → partner's item name ≥ 85% → Material master ≥ 93%, then converts the unit to match the Material's unit" },
];

export const M_LABEL: Record<string, string> = {
  customers: 'NameTh', vendors: 'VendorName', materials: 'Description', shiptos: 'ShipToName',
};

export const headerDefFor = (module: string): FieldDef[] =>
  module === 'SO' ? SO_H : module === 'II' ? II_H : module === 'PODP' ? PODP_H : AP_H;
