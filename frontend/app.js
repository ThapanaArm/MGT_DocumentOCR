/* =====================================================================
   MGT Document OCR -> SAP S/4HANA  (frontend)
   ทุกข้อมูลอ่าน/เขียนผ่าน REST API ที่ต่อกับ SQL Server: MGT_Document_OCR
   ===================================================================== */
// ปกติ frontend/ ถูก serve มาจาก backend เอง (same-origin) — เรียก /api/... แบบ relative ได้ตรงๆ
// ยกเว้นตอนรัน frontend เดี่ยวๆ ผ่าน dev server คนละ port (เช่น :5500 จาก .vscode/tasks.json "Run Frontend")
// ซึ่งต้องยิงข้าม origin ไปหา .NET backend (dotnet run, ค่าเริ่มต้น :8091 ตาม DOTNET_APP_PORT ใน .env)
const API_BASE = location.port === '5500' ? 'http://localhost:8091' : '';
const S = {
  page: 'home', module: null, doc: null, map: null, manual: { header: {}, lines: {} },
  masters: null, masterGroup: 'vendor', masterTab: 'vendors', busy: false, inbox: [], logs: [], health: null,
  user: 'it-digital@megachem.co.th', ocrProviders: null, ocrProvider: 'auto', chatHistory: [], chatImage: null,
  chatProvider: 'claude',
  apDocCategories: null, inboxApDocCategory: '', uploadApDocCategory: '', inboxSearch: '', miroTab: 'Basic Data',
  iiTab: 'Basic Data', usageDays: 7, dashDays: 7,
  masterSearch: '',
  inboxPage: 1, inboxPageSize: 50, inboxDateFrom: '', inboxDateTo: '',
  logPage: 1, logPageSize: 50, logDateFrom: '', logDateTo: '',
  auditLogPage: 1, auditLogPageSize: 50, auditLogDateFrom: '', auditLogDateTo: ''
};

async function ocrProviders() {
  if (!S.ocrProviders) S.ocrProviders = await API.get('/api/ocr/providers');
  return S.ocrProviders;
}
function ocrProviderSelect(id, selected) {
  const list = S.ocrProviders || [];
  return `<select id="${id}" class="ocr-pick">
    ${list.map(p => `<option value="${esc(p.id)}" ${p.id === selected ? 'selected' : ''} ${p.ready ? '' : 'class="hint"'}
        title="${esc(p.desc)}">${esc(p.label)}${p.ready ? '' : ' (ยังไม่ได้ตั้งค่า)'}</option>`).join('')}
  </select>`;
}

async function apDocCategories() {
  if (!S.apDocCategories) S.apDocCategories = await API.get('/api/ap-doc-categories');
  return S.apDocCategories;
}
function apDocCategoryLabel(id) {
  return (S.apDocCategories || []).find(c => c.id === id)?.label || id || '';
}
async function setDocCategory(v) {
  await guard(async () => {
    S.doc = await API.post(`/api/documents/${S.doc.docId}/category`, { apDocCategory: v, user: S.user });
  });
}

/* ---------------------------------------------------------------- utils */
const $ = s => document.querySelector(s);
const esc = s => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const num = v => { const n = parseFloat(String(v == null ? '' : v).replace(/[, ]/g, '')); return isNaN(n) ? 0 : n; };
const fmt = n => num(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const fmtCost = n => num(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 });
const fmtAmt = n => num(n).toLocaleString('en-US', { minimumFractionDigits: 0, maximumFractionDigits: 2 });   // format 0,000.##
const moduleLabel = mod => ({ SO: 'Sales Order', II: 'Incoming Invoice', PODP: 'PO Down Payment' }[mod] || 'Supplier Invoice');
// ชื่อย่อโมเดล/วิธีอ่านเอกสาร — ใช้แสดงในหน้า List ต่าง ๆ ว่าเอกสารนี้อ่านด้วยอะไร
const OCR_PROVIDER_SHORT = { auto: 'อัตโนมัติ', text: 'ข้อความในไฟล์', ocr: 'Tesseract OCR', tesseract: 'Tesseract OCR',
  typhoon: 'Typhoon', azure: 'Azure', claude_text: 'Claude (text)', claude: 'Claude', gemini: 'Gemini',
  openai: 'ChatGPT', demo: 'ตัวอย่าง', failed: 'อ่านไม่สำเร็จ' };
const providerBadge = id => id ? `<span class="hint">${esc(OCR_PROVIDER_SHORT[id] || id)}</span>` : '<span class="hint">—</span>';
// หัวข้อหน้าทะเบียนเอกสาร — ปกติใช้ moduleLabel + ' List' แต่บาง module สั้นกว่านั้น (override เฉพาะจุด)
const inboxTitle = mod => mod ? ({ II: 'Incoming' }[mod] || moduleLabel(mod)) + ' List' : 'ทะเบียนเอกสารทั้งหมด';
const dt = s => s ? String(s).replace('T', ' ').slice(0, 19) : '';

/* ---------------------------------------------------------------- duplicate check (client-side) */
const taxDigits = s => String(s || '').replace(/\D/g, '');
const normName = s => String(s || '').toLowerCase()
  .replace(/บริษัท|จำกัด|มหาชน|หจก\.|ห้างหุ้นส่วนจำกัด|co\.,?\s*ltd\.?|company|limited|public|pcl\.?|corp\.?|inc\.?/g, '')
  .replace(/[^a-z0-9ก-๙]/g, '');
function simJS(a, b) {
  a = normName(a); b = normName(b);
  if (!a || !b) return 0;
  if (a === b) return 1;
  if (a.includes(b) || b.includes(a)) return 0.92;
  const bg = s => { const o = []; for (let i = 0; i < s.length - 1; i++) o.push(s.slice(i, i + 2)); return o; };
  const A = bg(a), B = bg(b);
  if (!A.length || !B.length) return 0;
  const pool = B.slice(); let hit = 0;
  A.forEach(x => { const i = pool.indexOf(x); if (i > -1) { hit++; pool.splice(i, 1); } });
  return 2 * hit / (A.length + B.length);
}
/* หาข้อมูลที่อาจซ้ำใน master ก่อนเพิ่มใหม่ — ตรงเลขทะเบียนนิติบุคคล = คะแนนเต็ม, ไม่งั้นเทียบชื่อ */
function findDupes(kind, nameVal, taxVal, filterFn) {
  const rows = (S.masters && S.masters[kind]) || [];
  const nameField = M_LABEL[kind];
  const taxField = (kind === 'customers' || kind === 'vendors') ? 'TaxId' : null;
  return rows.filter(r => !filterFn || filterFn(r)).map(r => {
    let score = 0, reason = '';
    if (taxField && taxVal && taxDigits(taxVal) && taxDigits(r[taxField]) === taxDigits(taxVal)) {
      score = 1; reason = 'เลขทะเบียนนิติบุคคลตรงกัน';
    } else {
      const s = simJS(nameVal, r[nameField]);
      if (s > score) { score = s; reason = 'ชื่อคล้ายกัน ' + Math.round(s * 100) + '%'; }
    }
    return { row: r, score, reason };
  }).filter(x => x.score >= 0.55).sort((a, b) => b.score - a.score).slice(0, 4);
}

function toast(msg, ms) {
  const t = $('#toast'); t.innerHTML = msg; t.classList.add('on');
  clearTimeout(t._t); t._t = setTimeout(() => t.classList.remove('on'), ms || 3000);
}
function openModal(html, size) { $('#modal').innerHTML = html; $('#modal').className = 'modal' + (size ? ' ' + size : ''); $('#ov').classList.add('on'); }
function closeModal() { $('#ov').classList.remove('on'); }
document.getElementById('ov').addEventListener('click', e => { if (e.target.id === 'ov') closeModal(); });

function applyTheme(t) {
  document.documentElement.setAttribute('data-theme', t);
  const b = $('#themeBtn'); if (b) b.innerHTML = t === 'dark' ? '&#9788;' : '&#9789;';
  try { localStorage.setItem('ocr_sap_theme', t); } catch (e) { }
}
function toggleTheme() { applyTheme(document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark'); }
(function () { let t = 'light'; try { t = localStorage.getItem('ocr_sap_theme') || 'light'; } catch (e) { } applyTheme(t); })();

function applyNavCollapsed(on) {
  $('#sidebar')?.classList.toggle('collapsed', on);
  const b = $('#navToggle'); if (b) b.title = on ? 'ขยายเมนู' : 'ย่อเมนู';
  try { localStorage.setItem('ocr_sap_nav_collapsed', on ? '1' : '0'); } catch (e) { }
}
function toggleNav() { applyNavCollapsed(!$('#sidebar')?.classList.contains('collapsed')); }
(function () { let c = false; try { c = localStorage.getItem('ocr_sap_nav_collapsed') === '1'; } catch (e) { } if (c) applyNavCollapsed(true); })();

/* ---------------------------------------------------------------- api */
async function api(method, url, body, isForm) {
  const opt = { method, headers: {} };
  if (body !== undefined) {
    if (isForm) opt.body = body;
    else { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
  }
  const r = await fetch(API_BASE + url, opt);
  const txt = await r.text();
  let data = null; try { data = txt ? JSON.parse(txt) : null; } catch (e) { data = { detail: txt }; }
  if (!r.ok) throw new Error((data && (data.detail || data.error)) || ('HTTP ' + r.status));
  return data;
}
const API = {
  get: u => api('GET', u),
  post: (u, b) => api('POST', u, b || {}),
  put: (u, b) => api('PUT', u, b),
  del: u => api('DELETE', u),
  upload: (u, fd) => api('POST', u, fd, true)
};
async function guard(fn) {
  document.body.classList.add('busy');
  try { S.busy = true; return await fn(); }
  catch (e) { toast('&#9888; ' + esc(e.message)); throw e; }
  finally { S.busy = false; document.body.classList.remove('busy'); }
}

/* ---------------------------------------------------------------- master defs */
const MASTER_DEF = {
  customers: {
    label: 'ลูกค้า (Customer)', mod: 'SO', key: 'CustomerCode', cols: [
      { k: 'CustomerCode', l: 'รหัสลูกค้า (ภายใน)' }, { k: 'SapCustomerCode', l: 'รหัสใน SAP (Sold-to)', sap: true },
      { k: 'NameTh', l: 'ชื่อ (TH)' }, { k: 'NameEn', l: 'ชื่อ (EN)' },
      { k: 'TaxId', l: 'เลขทะเบียน / ผู้เสียภาษี' }, { k: 'Branch', l: 'สาขา' }, { k: 'SalesOrg', l: 'Sales Org' },
      { k: 'DistChannel', l: 'Distr.Ch' }, { k: 'Division', l: 'Div' }, { k: 'Currency', l: 'สกุลเงิน' },
      { k: 'PaymentTerms', l: 'Payment Terms' }]
  },
  shiptos: {
    label: 'สถานที่ส่งของ (Ship-to)', mod: 'SO', key: 'ShipToCode', cols: [
      { k: 'CustomerCode', l: 'รหัสลูกค้า', ref: 'customers' }, { k: 'ShipToCode', l: 'รหัส Ship-to (ภายใน)' },
      { k: 'SapShipToCode', l: 'รหัสใน SAP (Ship-to)', sap: true }, { k: 'ShipToName', l: 'ชื่อสถานที่' }, { k: 'Address', l: 'ที่อยู่' }]
  },
  custmaterials: {
    label: 'สินค้าฝั่งลูกค้า', mod: 'SO', key: 'Id', cols: [
      { k: 'CustomerCode', l: 'รหัสลูกค้า', ref: 'customers' }, { k: 'ExtCode', l: 'รหัสสินค้าของลูกค้า' },
      { k: 'ExtDesc', l: 'ชื่อสินค้าของลูกค้า' }, { k: 'MaterialCode', l: 'Material (SAP)', ref: 'materials' }]
  },
  vendors: {
    label: 'ผู้ขาย (Vendor)', mod: 'AP', key: 'VendorCode', cols: [
      { k: 'VendorCode', l: 'รหัสผู้ขาย (ภายใน)' }, { k: 'SapVendorCode', l: 'รหัสใน SAP (Supplier)', sap: true },
      { k: 'VendorName', l: 'ชื่อผู้ขาย' },
      { k: 'TaxId', l: 'เลขทะเบียน / ผู้เสียภาษี' }, { k: 'Branch', l: 'สาขา' }, { k: 'Currency', l: 'สกุลเงิน' },
      { k: 'PaymentTerms', l: 'Payment Terms' }, { k: 'ReconAcct', l: 'Recon. Account' }, { k: 'WhtCode', l: 'ภาษีหัก ณ ที่จ่าย' }]
  },
  venmaterials: {
    label: 'สินค้าฝั่งผู้ขาย', mod: 'AP', key: 'Id', cols: [
      { k: 'VendorCode', l: 'รหัสผู้ขาย', ref: 'vendors' }, { k: 'ExtCode', l: 'รหัสสินค้าของผู้ขาย' },
      { k: 'ExtDesc', l: 'ชื่อสินค้าของผู้ขาย' }, { k: 'MaterialCode', l: 'Material (SAP)', ref: 'materials' }]
  },
  uoms: {
    label: 'การแปลงหน่วย (UoM)', mod: 'ALL', key: 'Id', cols: [
      { k: 'MaterialCode', l: 'Material (เว้นว่าง = ทุกสินค้า)', ref: 'materials', blank: true },
      { k: 'ExtUom', l: 'หน่วยตามเอกสาร' }, { k: 'SapUom', l: 'หน่วยใน SAP' },
      { k: 'SapUomIso', l: 'ISO code', sap: true },
      { k: 'Factor', l: 'ตัวคูณ (1 หน่วยเอกสาร = ? หน่วย SAP)' }, { k: 'Note', l: 'หมายเหตุ' }]
  },
  materials: {
    label: 'สินค้า/บริการ (Material)', mod: 'ALL', key: 'MaterialCode', cols: [
      { k: 'MaterialCode', l: 'รหัส Material (ภายใน)' }, { k: 'SapMaterialCode', l: 'รหัสใน SAP (Material)', sap: true },
      { k: 'Description', l: 'รายละเอียด' }, { k: 'Uom', l: 'หน่วยฐาน' },
      { k: 'Plant', l: 'Plant' }, { k: 'MatGroup', l: 'Material Group' }]
  }
};
const MASTER_NOTE = {
  customers: 'ใช้จับคู่ Sold-to: ตรวจจาก <b>เลขทะเบียนนิติบุคคล</b> ก่อน ถ้าไม่พบจึงเทียบ <b>ชื่อ</b> (ความคล้าย &ge; 82%)',
  shiptos: 'ใช้จับคู่ Ship-to จาก <b>ชื่อสถานที่/ที่อยู่</b> ภายใต้ลูกค้าเดียวกัน',
  custmaterials: 'ใช้แปลง <b>รหัส/ชื่อสินค้าของลูกค้า</b> เป็น Material ของ SAP',
  vendors: 'ใช้จับคู่ Vendor: ตรวจจาก <b>เลขทะเบียนนิติบุคคล</b> ก่อน ถ้าไม่พบจึงเทียบ <b>ชื่อ</b>',
  venmaterials: 'ใช้แปลง <b>รหัส/ชื่อสินค้าของผู้ขาย</b> เป็น Material ของ SAP',
  materials: 'ข้อมูล Material ของ SAP (ควร replicate จาก S/4HANA)',
  uoms: 'แปลงหน่วยตามเอกสารของคู่ค้าเป็นหน่วยของ SAP เช่น <b>1 BAG = 25 KG</b> — ' +
        'ระบบจะหา<b>กฎเฉพาะสินค้า</b>ก่อน ถ้าไม่พบจึงใช้<b>กฎกลาง</b> (แถวที่ไม่ระบุ Material)'
};

/* กลุ่มการ Mapping ตามหัวข้อ: 1 Vendor · 2 Customer · 3 Ship-to · 4 Material (+ การแปลงหน่วย) */
const MASTER_GROUPS = [
  { key: 'vendor', label: '1. Vendor / Supplier', mod: 'AP', tabs: ['vendors'],
    note: 'ตรวจจาก <b>เลขทะเบียนนิติบุคคล 13 หลัก</b> ก่อน ถ้าไม่พบจึงเทียบ <b>ชื่อผู้ขาย</b> (ความคล้าย &ge; 82%)' },
  { key: 'customer', label: '2. Customer', mod: 'SO', tabs: ['customers'],
    note: 'ตรวจจาก <b>เลขทะเบียนนิติบุคคล 13 หลัก</b> ก่อน ถ้าไม่พบจึงเทียบ <b>ชื่อลูกค้า (ไทย/อังกฤษ)</b> (ความคล้าย &ge; 82%)' },
  { key: 'shipto', label: '3. Ship-to', mod: 'SO', tabs: ['shiptos'],
    note: 'เทียบ <b>ชื่อสถานที่ + ที่อยู่จัดส่ง</b> เฉพาะภายใต้ลูกค้าที่จับคู่ได้แล้ว (ความคล้าย &ge; 70%)' },
  { key: 'material', label: '4. Material', mod: 'ALL', tabs: ['materials', 'custmaterials', 'venmaterials', 'uoms'],
    note: 'ตรวจ <b>รหัสสินค้าของคู่ค้า</b> ตรงตัวก่อน &rarr; <b>ชื่อสินค้าของคู่ค้า</b> &ge; 85% &rarr; <b>Material master</b> &ge; 93% ' +
          'จากนั้นจึง <b>แปลงหน่วย</b> ให้ตรงกับหน่วยของ Material' }
];
const M_LABEL = { customers: 'NameTh', vendors: 'VendorName', materials: 'Description', shiptos: 'ShipToName' };

async function masters(force) {
  if (!S.masters || force) S.masters = await API.get('/api/masters');
  return S.masters;
}

/* ---------------------------------------------------------------- header fields */
const SO_H = [['docType', 'ประเภทเอกสาร'], ['poNo', 'เลขที่ใบสั่งซื้อลูกค้า'], ['poDate', 'วันที่เอกสาร'],
['customerName', 'ชื่อลูกค้า'], ['customerTaxId', 'เลขทะเบียนนิติบุคคล'], ['shipToName', 'สถานที่ส่งของ'],
['shipToAddress', 'ที่อยู่จัดส่ง'], ['deliveryDate', 'วันที่ต้องการรับสินค้า'], ['currency', 'สกุลเงิน'],
['paymentTerms', 'เงื่อนไขชำระเงิน'], ['incoterms', 'Incoterms']];
// ฟิลด์ยอดเงิน + หมายเหตุ — ย้ายมาแสดงเป็นการ์ดแยกด้านล่าง DETAIL (ใกล้รายการสินค้าที่รวมยอดมาจาก)
const SO_TOTALS_H = [['subTotal', 'มูลค่าก่อนภาษี'], ['vatAmount', 'ภาษีมูลค่าเพิ่ม'], ['totalAmount', 'ยอดรวมทั้งสิ้น']];
const SO_REMARK_H = [['remark', 'หมายเหตุ']];
const AP_H = [['docType', 'ประเภทเอกสาร'], ['invoiceNo', 'เลขที่ใบแจ้งหนี้'], ['invoiceDate', 'วันที่ใบแจ้งหนี้'],
['postingDate', 'วันที่ผ่านรายการ'], ['vendorName', 'ชื่อผู้ขาย'], ['vendorTaxId', 'เลขทะเบียนนิติบุคคล'],
['branch', 'สาขา'], ['poRef', 'อ้างอิง PO'], ['currency', 'สกุลเงิน'], ['paymentTerms', 'เงื่อนไขชำระเงิน']];
// Incoming Invoices (Fiori F0859) — เอกสารตั้งหนี้เจ้าหนี้ไม่มี PO อ้างอิง แยก module 'II' จาก Supplier Invoice โดยสมบูรณ์
const II_H = [['transaction', 'Transaction'], ['invoiceNo', 'Reference'], ['invoiceDate', 'Invoice Date'],
['postingDate', 'Posting Date'], ['vendorName', 'Supplier'], ['vendorTaxId', 'เลขทะเบียนนิติบุคคล'],
['sapDocType', 'Document type'], ['currency', 'Currency']];
const II_TOTALS_H = [['totalAmount', 'Amount'], ['vatAmount', 'Tax Amount'], ['whtAmount', 'Withholding Tax Amount FC']];
const II_GROUPS = [
  { title: 'Basic Data', fields: [['calculateTax', 'Calculate Tax'], ['taxCode', 'Tax Code'],
    ['businessPlace', 'Business Place'], ['headerText', 'Text']] },
  { title: 'Address and Bank Data', fields: [['language', 'Language'], ['vendorName2', 'Name2'],
    ['vendorName3', 'Name3'], ['vendorName4', 'Name4'], ['addressStreet', 'Street'], ['addressCity', 'City'],
    ['addressPostalCode', 'Postal Code'], ['addressCountry', 'Ctry/Reg.'], ['vendorEmail', 'E-Mail'],
    ['bankCountry', 'Bank Ctry/Reg.'], ['bankKey', 'Bank Key'], ['bankAccountNo', 'Bank Account'],
    ['taxNumber3', 'Tax Number 3']] },
  { title: 'Payment', fields: [['baselineDate', 'Baseline Date'], ['paymentTerms', 'Payment Terms'],
    ['paymentMethod', 'Payment Method'], ['paymentBlock', 'Payment Block'], ['partnerBank', 'Partner Bank'],
    ['houseBank', 'House Bank'], ['bankAccountId', 'Account ID']] },
  { title: 'Details', fields: [['assignmentText', 'Assignment'], ['refKey1', 'Ref. Key 1'],
    ['refKey2', 'Ref. Key 2'], ['refKey3', 'Ref. Key 3']] },
  { title: 'Withholding Tax', fields: [['whtCode', 'Withholding Tax Code'],
    ['whtBaseAmount', 'Withholding Tax Base Amount FC']] },
];
// Purchase Order Down Payments — module แยกอีกอัน ฟิลด์เริ่มต้นแบบพื้นฐาน รอรายละเอียดฉบับเต็มจากผู้ใช้
const PODP_H = [['docType', 'ประเภทเอกสาร'], ['invoiceNo', 'เลขที่เอกสาร'], ['invoiceDate', 'วันที่เอกสาร'],
['postingDate', 'วันที่ผ่านรายการ'], ['vendorName', 'ผู้ขาย'], ['vendorTaxId', 'เลขทะเบียนนิติบุคคล'],
['poRef', 'อ้างอิง PO'], ['currency', 'สกุลเงิน'], ['paymentTerms', 'เงื่อนไขชำระเงิน']];
const PODP_TOTALS_H = [['totalAmount', 'จำนวนเงินมัดจำ']];
// ฟิลด์ยอดเงิน — ย้ายมาแสดงเป็นการ์ดแยกด้านล่าง DETAIL (ใกล้รายการสินค้าที่รวมยอดมาจาก) พร้อม format เลข 0,000.##
const AP_TOTALS_H = [['subTotal', 'มูลค่าก่อนภาษี'], ['vatRate', 'อัตราภาษี (%)'], ['vatAmount', 'ภาษีมูลค่าเพิ่ม'],
['whtAmount', 'ภาษีหัก ณ ที่จ่าย'], ['totalAmount', 'ยอดรวมสุทธิ']];
// ฟิลด์เพิ่มเติมสำหรับ Supplier Invoice / MIRO เต็มรูปแบบ — ใช้กับทุกประเภทเอกสาร AP (ผู้ใช้กรอกเอง ไม่ได้เดาจาก OCR)
// จัดกลุ่มตามหน้าจอ MIRO จริง — Invoice Date/Reference/Posting Date/Amount/Currency/PO Reference อยู่ใน
// HEADER หลักอยู่แล้ว (invoiceDate/invoiceNo/postingDate/totalAmount/currency/poRef/branch) ไม่ต้องเพิ่มซ้ำ
const AP_TRADE_GROUPS = [
  { title: 'Basic Data', fields: [['taxCode', 'Tax Code'], ['calculateTax', 'Calculate Tax'],
    ['businessPlace', 'Business Place'], ['companyCode', 'Company Code'], ['headerText', 'Text']] },
  { title: 'PO Reference', fields: [['refDocType', 'เอกสารอ้างอิง', 'select', [
    ['', '— เลือก —'], ['1', '1. Goods/service item'], ['2', '2. Planned delivery costs'],
    ['3', '3. Goods/service item + Planned delivery costs']]]] },
  { title: 'Payment', fields: [['baselineDate', 'Baseline Date'], ['paymentMethod', 'Payment Method'],
    ['paymentBlock', 'Payment Block'], ['partnerBank', 'Partner Bank'], ['houseBank', 'House Bank'],
    ['bankAccountId', 'Account ID']] },
  { title: 'Details', fields: [['assignmentText', 'Assignment'], ['unplannedDeliveryCost', 'Unplanned Delivery Costs']] },
  { title: 'Tax', fields: [['taxDate', 'Tax Date'], ['taxReportingDate', 'Tax Reporting Date'],
    ['taxFulfillDate', 'Tax Fulfill Date']] },
  { title: 'Withholding Tax', fields: [['whtCode', 'Withholding Tax Code'],
    ['whtBaseAmount', 'Withholding Tax Base Amount FC']] },
];
// ฟิลด์ระดับรายการ (Account Assignment/GL/Cost Center ฯลฯ) — ใช้กับทุกประเภทเอกสาร AP แยกกรอกทีละรายการใน DETAIL
// (Short Text/Material, Quantity, Net Price ใช้ desc/qty/price ที่มีอยู่แล้ว ไม่ต้องเพิ่มซ้ำ)
const PO_LINE_EXTRA_FIELDS = [
  ['accountAssignment', 'Account Assignment Category', 'select', [['', '— เลือก —'], ['K', 'K — Cost Center'], ['A', 'A — Asset'], ['P', 'P — Project']]],
  ['itemCategory', 'Item Category', 'select', [['', '— เลือก —'], ['STANDARD', 'Standard'], ['SERVICE', 'Service']]],
  ['plant', 'Plant', 'text'],
  ['glAccount', 'G/L Account', 'text'],
  ['costCenter', 'Cost Center', 'text'],
  ['internalOrder', 'Internal Order', 'text'],
  ['wbsElement', 'WBS Element', 'text'],
  ['assetNumber', 'Asset Number', 'text'],
  ['taxCode', 'Tax Code', 'text'],
  ['deliveryDate', 'Delivery Date', 'text'],
  ['grIndicator', 'GR Indicator', 'select', [['', '— เลือก —'], ['YES', 'ใช่ — ต้องรับของ/บริการ'], ['NO', 'ไม่ใช่']]],
  ['irIndicator', 'IR Indicator', 'select', [['', '— เลือก —'], ['YES', 'ใช่ — รับ Invoice ได้'], ['NO', 'ไม่ใช่']]],
  ['grBasedIv', 'GR-Based IV', 'select', [['', '— เลือก —'], ['YES', 'ใช่ — ต้องอ้างอิงรายการที่รับแล้ว'], ['NO', 'ไม่ใช่']]],
];
// เลือก Account Assignment ให้ตรงวัตถุประสงค์ — ใช้ไฮไลต์ฟิลด์ที่ควรกรอกใน showLineExtra() ให้ผู้ใช้ไม่ต้องจำเอง
const AA_GUIDE = {
  '': { hint: 'ของเข้า Stock ปกติไม่ใช้ Account Assignment (เว้นว่างไว้ได้ เว้นแต่ Configuration บริษัทกำหนดไว้)', field: '' },
  K: { hint: 'ค่าใช้จ่ายของแผนก → กรอก Cost Center (หรือ Internal Order เพิ่มถ้าเป็นค่าใช้จ่ายเฉพาะกิจกรรม)', field: 'costCenter' },
  A: { hint: 'ซื้อทรัพย์สินถาวร → กรอก Asset Number', field: 'assetNumber' },
  P: { hint: 'ค่าใช้จ่ายโครงการ → กรอก WBS Element', field: 'wbsElement' },
};

/* ---------------------------------------------------------------- router */
// เมนูซ้ายแยก Process ของแต่ละโมดูล (AP Invoice / Sales Order) ไว้คนละกลุ่มชัดเจน — ลิงก์แต่ละอันมี
// data-mod กำกับอยู่แล้วว่าเป็นของโมดูลไหน กดแล้วสลับ S.module ให้อัตโนมัติ ไม่ต้องมีหน้า "เลือกโมดูล" คั่นกลาง
document.querySelectorAll('#nav a').forEach(a => { a.onclick = e => { e.preventDefault(); go(a.dataset.page, a.dataset.mod); }; });
function go(p, mod) {
  if (mod && mod !== S.module) {
    // สลับโมดูลจากเมนู — ล้างเอกสารที่ค้างอยู่ (ถ้ามี) กันหน้าจอค้างแสดงเอกสารของโมดูลเก่าทับโมดูลใหม่
    S.module = mod; S.uploadApDocCategory = ''; S.inboxSearch = ''; S.inboxApDocCategory = '';
    S.inboxPage = 1; S.inboxDateFrom = ''; S.inboxDateTo = '';
    S.doc = null; S.map = null; S.manual = { header: {}, lines: {} }; S.chatHistory = []; S.chatImage = null;
  } else if (mod) {
    S.module = mod; S.uploadApDocCategory = ''; S.inboxSearch = ''; S.inboxApDocCategory = '';
  }
  S.page = p;
  document.querySelectorAll('#nav a').forEach(a =>
    a.classList.toggle('active', a.dataset.page === p && (!a.dataset.mod || a.dataset.mod === S.module)));
  $('#pageTitle').textContent = { home: 'Overview', work: 'นำเข้าเอกสาร / OCR', inbox: inboxTitle(S.module), master: 'Master Mapping', log: 'ประวัติส่ง SAP', auditlog: 'Log' }[p];
  render();
}
function render() {
  const t = $('#modTag');
  const showMod = S.module && (S.page === 'work' || S.page === 'inbox');
  if (showMod) { t.style.display = 'inline-flex'; t.innerHTML = '&#9632; Module: ' + moduleLabel(S.module); }
  else t.style.display = 'none';
  ({ home: renderHome, work: renderWork, inbox: renderInbox, master: renderMaster, log: renderLog, auditlog: renderAuditLog })[S.page]();
}
function stepsHtml(cur) {
  const st = [['นำเข้า &amp; อ่านเอกสาร', 'OCR → Header / Detail'],
  ['Mapping ข้อมูล', 'ตรวจกับ Master Data'], ['ส่งเข้า SAP', 'สร้างเอกสารใน S/4HANA']];
  return '<div class="steps">' + st.map((s, i) => {
    const n = i + 1, cls = n < cur ? 'done' : (n === cur ? 'on' : '');
    return `<div class="step ${cls}"><div class="n">${n < cur ? '&#10003;' : n}</div><div class="t"><b>${s[0]}</b>${s[1]}</div></div>`;
  }).join('') + '</div>';
}

/* ---------------------------------------------------------------- HOME */
// หน้าภาพรวม — ไม่ต้องเลือกโมดูลก่อนอีกต่อไป Process ของแต่ละโมดูลแยกเข้าถึงตรงจากเมนูซ้าย
async function renderHome() {
  $('#content').innerHTML = '<div class="card"><div class="card-b"><div class="empty">กำลังโหลด…</div></div></div>';
  try {
    const days = S.dashDays || 7;
    const d = await API.get('/api/dashboard?days=' + days);
    const sc = d.statusCounts, tr = d.trend;
    const pendingReview = sc.NEW || 0, mappingFailed = sc.INCOMPLETE || 0, readyToSend = sc.MAPPED || 0,
          sentOk = sc.POSTED || 0, total = sc.total || 0;

    const tiles = [
      ['&#128196;', 'var(--info-bg)', 'var(--info)', 'เอกสารทั้งหมด', total, tr.total],
      ['&#8987;', 'var(--orange-bg)', 'var(--orange)', 'รอตรวจสอบ', pendingReview, tr.NEW],
      ['&#10060;', 'var(--red-bg)', 'var(--red)', 'Mapping ไม่ผ่าน', mappingFailed, tr.INCOMPLETE],
      ['&#128228;', 'var(--info-bg)', 'var(--info)', 'พร้อมส่ง SAP', readyToSend, tr.MAPPED],
      ['&#9989;', 'var(--green-bg)', 'var(--green)', 'ส่ง SAP สำเร็จ', sentOk, tr.POSTED],
    ].map(([icon, bg, fg, label, val, pct]) => statTile(icon, bg, fg, label, val, pct)).join('');

    const dayOpts = [7, 15, 30].map(n => `<option value="${n}" ${n === days ? 'selected' : ''}>${n} วัน</option>`).join('');
    const daySelect = `<select onchange="S.dashDays=parseInt(this.value);renderHome()" style="width:auto">${dayOpts}</select>`;

    const statusDonutSegs = [
      { label: 'สำเร็จ', value: sentOk, color: 'var(--green)' },
      { label: 'รอตรวจสอบ', value: pendingReview + mappingFailed, color: 'var(--orange)' },
      { label: 'พร้อมส่ง', value: readyToSend, color: 'var(--info)' },
    ].filter(s => s.value > 0);
    const donutTotal = statusDonutSegs.reduce((a, s) => a + s.value, 0) || 1;
    statusDonutSegs.forEach(s => s.pct = Math.round(s.value / donutTotal * 100));

    const byModuleData = d.byModule.map(m => ({ label: moduleLabel(m.module), value: m.count }));

    const tasks = [
      ['&#128203;', 'ตรวจสอบข้อมูล OCR', 'เอกสารรอตรวจสอบ', pendingReview],
      ['&#9881;', 'แก้ไข Master Mapping', 'รายการที่ต้องแก้ไข', mappingFailed],
      ['&#9729;', 'ส่งข้อมูลไป SAP', 'เอกสารพร้อมส่ง', readyToSend],
    ];

    $('#content').innerHTML = `
    <div class="tiles" style="display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:16px;margin-bottom:20px">${tiles}</div>

    <div style="display:grid;grid-template-columns:1.6fr 1fr;gap:20px;align-items:start" class="dash-row">
      <div class="card" style="margin-bottom:0"><div class="card-h"><h2>ปริมาณเอกสารและความสำเร็จ OCR</h2><div class="sp"></div>${daySelect}</div>
        <div class="card-b">
          <div class="row" style="gap:18px;margin-bottom:8px;font-size:13px">
            <span><span style="display:inline-block;width:14px;height:3px;background:var(--brand);vertical-align:middle;margin-right:6px"></span>เอกสาร</span>
            <span><span style="display:inline-block;width:14px;height:0;border-top:3px dashed var(--info);vertical-align:middle;margin-right:6px"></span>OCR สำเร็จ</span>
          </div>
          ${lineChart2(d.ocrDaily, [{ key: 'docCount', label: 'เอกสาร', color: 'var(--brand)' },
                                    { key: 'okCount', label: 'OCR สำเร็จ', color: 'var(--info)', dash: true }])}
        </div></div>
      <div class="card" style="margin-bottom:0"><div class="card-h"><h2>สถานะเอกสาร</h2></div>
        <div class="card-b">
          ${donutChart(statusDonutSegs, num(total).toLocaleString('en-US'), 'รวมทั้งหมด เอกสาร')}
          <div style="margin-top:14px">${statusDonutSegs.map(s => `<div class="row" style="justify-content:space-between;margin-bottom:8px">
              <span><span style="display:inline-block;width:9px;height:9px;border-radius:50%;background:${s.color};margin-right:8px"></span>${esc(s.label)}</span>
              <span class="hint">${s.pct}% &middot; ${num(s.value).toLocaleString('en-US')} เอกสาร</span></div>`).join('')}</div>
        </div></div>
    </div>

    <div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:20px;align-items:start;margin-top:20px" class="dash-row">
      <div class="card" style="margin-bottom:0"><div class="card-h"><h2>เอกสารตามประเภท</h2><div class="sp"></div>${daySelect}</div>
        <div class="card-b">${byModuleData.length ? hBarChart(byModuleData) : '<div class="empty">ยังไม่มีข้อมูล</div>'}
          <p class="sec-title" style="margin-top:20px">ค่าใช้จ่าย OCR แต่ละ Module</p>
          ${costByModuleTable(d.costByModule)}
        </div></div>
      <div class="card" style="margin-bottom:0"><div class="card-h"><h2>ประสิทธิภาพ OCR</h2></div>
        <div class="card-b">
          ${radialGauge(d.ocrPerf.avgConfidencePct ?? 0, 'ความแม่นยำ OCR')}
          <div style="margin-top:16px;display:flex;flex-direction:column;gap:12px">
            <div class="row" style="gap:10px"><span>&#9201;</span><div><div class="hint">เฉลี่ยต่อเอกสาร</div>
              <b>${d.ocrPerf.avgDurationSec != null ? d.ocrPerf.avgDurationSec + ' วินาที' : '—'}</b></div></div>
            <div class="row" style="gap:10px"><span>&#128100;</span><div><div class="hint">แก้ไขโดยผู้ใช้</div>
              <b>${d.ocrPerf.pctEditedByUser}%</b></div></div>
            <div class="row" style="gap:10px"><span>&#128203;</span><div><div class="hint">Token วันนี้</div>
              <b>${num(d.ocrPerf.tokensToday).toLocaleString('en-US')}</b></div></div>
          </div>
        </div></div>
      <div class="card" style="margin-bottom:0"><div class="card-h"><h2>งานที่ต้องดำเนินการ</h2></div>
        <div class="card-b">
          ${tasks.map(([icon, title, sub, n]) => `<div class="row" style="justify-content:space-between;padding:10px 0;border-bottom:1px solid var(--line);cursor:pointer" onclick="viewAllInbox()">
              <div class="row" style="gap:10px"><span style="font-size:18px">${icon}</span>
                <div><div style="font-weight:600">${title}</div><div class="hint">${sub}</div></div></div>
              <div class="row" style="gap:8px"><span class="badge b-warn">${n}</span><span class="hint">&rsaquo;</span></div>
            </div>`).join('')}
          <div style="text-align:right;margin-top:10px"><a href="#" onclick="event.preventDefault();viewAllInbox()">ดูงานทั้งหมด &rarr;</a></div>
        </div></div>
    </div>

    <div class="card" style="margin-top:20px"><div class="card-h"><h2>รายการล่าสุด</h2><div class="sp"></div>
      <button class="btn sm" onclick="viewAllInbox()">ดูทั้งหมด &rarr;</button></div>
    <div class="card-b"><div class="tw"><table>
      <thead><tr><th>เลขที่เอกสาร</th><th>ประเภท</th><th>คู่ค้า</th><th>สถานะ</th><th>อัปเดตล่าสุด</th><th>ผู้ดำเนินการ</th><th></th></tr></thead>
      <tbody>${d.recent.map(r => `<tr>
          <td><b>${esc(r.DocNo || r.FileName || ('#' + r.DocId))}</b></td>
          <td>${esc(moduleLabel(r.Module))}</td>
          <td>${esc(r.PartnerName || '')}</td>
          <td>${statusBadge(r.Status)}</td>
          <td class="hint">${dt(r.UpdatedAt || r.CreatedAt)}</td>
          <td>${esc(r.PostedBy || r.CreatedBy || '')}</td>
          <td><button class="btn sm" onclick="openDoc(${r.DocId})">เปิด</button></td></tr>`).join('')
        || '<tr><td colspan="7" class="empty">ยังไม่มีเอกสารในระบบ</td></tr>'}
      </tbody></table></div></div></div>`;
  } catch (e) { $('#content').innerHTML = '<div class="card"><div class="card-b"><div class="empty">โหลดภาพรวมไม่สำเร็จ</div></div></div>'; }
}
function statTile(icon, bg, fg, label, val, pct) {
  const up = (pct || 0) >= 0;
  return `<div class="card" style="margin-bottom:0"><div class="card-b" style="padding:18px">
      <div style="width:38px;height:38px;border-radius:var(--r3);background:${bg};color:${fg};
           display:flex;align-items:center;justify-content:center;font-size:16px;margin-bottom:12px">${icon}</div>
      <div class="hint" style="margin-bottom:2px">${esc(label)}</div>
      <div style="font-size:26px;font-weight:700;line-height:34px;margin-bottom:6px">${num(val).toLocaleString('en-US')}</div>
      <div style="font-size:12px;color:${up ? 'var(--green)' : 'var(--red)'}">${up ? '&#9650;' : '&#9660;'} ${Math.abs(pct || 0)}% จากสัปดาห์ที่แล้ว</div>
    </div></div>`;
}
/* กราฟเส้น 2 เส้น (เอกสาร / OCR สำเร็จ) — ไม่พึ่ง library ภายนอก ใช้ CSS var ให้เข้ากับธีมสว่าง/มืดอัตโนมัติ */
function lineChart2(data, series) {
  const w = 960, h = 220, padL = 6, padR = 10, padB = 22, padT = 14;
  const n = data.length || 1;
  const allVals = data.flatMap(row => series.map(s => num(row[s.key])));
  const max = Math.max(...allVals, 1);
  const slot = n > 1 ? (w - padL - padR) / (n - 1) : 0;
  const X = i => padL + i * slot;
  const Y = v => h - padB - (v / max) * (h - padT - padB);
  const grid = [0, .25, .5, .75, 1].map(f => {
    const y = (padT + f * (h - padT - padB)).toFixed(1);
    return `<line x1="${padL}" y1="${y}" x2="${w - padR}" y2="${y}" stroke="var(--line)" stroke-width="1"/>`;
  }).join('');
  const lines = series.map(s => {
    const pts = data.map((row, i) => `${X(i).toFixed(1)},${Y(num(row[s.key])).toFixed(1)}`).join(' ');
    const dots = data.map((row, i) => `<circle cx="${X(i).toFixed(1)}" cy="${Y(num(row[s.key])).toFixed(1)}" r="3" fill="${s.color}">
        <title>${esc(row.date)} — ${esc(s.label)}: ${num(row[s.key]).toLocaleString('en-US')}</title></circle>`).join('');
    return `<polyline points="${pts}" fill="none" stroke="${s.color}" stroke-width="2.5" ${s.dash ? 'stroke-dasharray="6 4"' : ''}/>${dots}`;
  }).join('');
  const labelEvery = Math.max(1, Math.ceil(n / 8));
  const labels = data.map((row, i) => i % labelEvery !== 0 ? '' : `<text x="${X(i).toFixed(1)}" y="${h - 4}"
      font-size="10" fill="var(--muted)" text-anchor="middle">${esc((row.date || '').slice(5))}</text>`).join('');
  return `<svg viewBox="0 0 ${w} ${h}" style="width:100%;height:220px;display:block">${grid}${lines}${labels}</svg>`;
}
/* กราฟแท่งแนวนอน (เอกสารตามประเภท) */
function hBarChart(data) {
  const max = Math.max(...data.map(x => x.value), 1);
  return `<div style="display:flex;flex-direction:column;gap:16px">${data.map(x => {
    const pct = Math.max(4, x.value / max * 100);
    return `<div class="row" style="gap:10px">
      <div style="width:120px;flex-shrink:0;font-size:13px">${esc(x.label)}</div>
      <div style="flex:1;background:var(--line-soft);border-radius:var(--pill);height:18px">
        <div style="width:${pct.toFixed(1)}%;background:var(--brand);height:100%;border-radius:var(--pill)"></div>
      </div>
      <div style="width:50px;text-align:right;font-weight:600;font-size:13px">${num(x.value).toLocaleString('en-US')}</div>
    </div>`;
  }).join('')}</div>`;
}
/* ตารางค่าใช้จ่าย OCR แยกตาม Module — คู่กับกราฟ "เอกสารตามประเภท" ด้านบน (ช่วงวันเดียวกัน) */
function costByModuleTable(rows) {
  if (!rows || !rows.length) return '<div class="empty">ยังไม่มีข้อมูล</div>';
  const totalCost = rows.reduce((a, r) => a + num(r.cost), 0);
  const currency = rows.find(r => r.costCurrency)?.costCurrency || 'USD';
  return `<div class="tw"><table>
    <thead><tr><th>Module</th><th style="text-align:right">เอกสาร</th><th style="text-align:right">Token</th><th style="text-align:right">ค่าใช้จ่าย</th></tr></thead>
    <tbody>${rows.map(r => `<tr>
        <td><span class="badge ${r.module === 'AP' ? 'b-warn' : r.module === 'II' ? 'b-idle' : 'b-ok'}">${esc(moduleLabel(r.module))}</span></td>
        <td style="text-align:right">${num(r.count).toLocaleString('en-US')}</td>
        <td style="text-align:right">${num(r.tokens).toLocaleString('en-US')}</td>
        <td style="text-align:right">${fmtCost(r.cost)} ${esc(r.costCurrency || '')}</td></tr>`).join('')}
    </tbody>
    <tfoot><tr class="totrow"><td>รวม</td><td></td><td></td>
        <td style="text-align:right">${fmtCost(totalCost)} ${esc(currency)}</td></tr></tfoot>
    </table></div>`;
}
/* โดนัทหลายสี (สถานะเอกสาร) — วาดด้วย stroke-dasharray ต่อเนื่องกันเป็นวง */
function donutChart(segments, centerValue, centerLabel) {
  const size = 180, r = 68, cx = size / 2, cy = size / 2, sw = 22;
  const circ = 2 * Math.PI * r;
  let offset = 0;
  const arcs = segments.map(s => {
    const len = s.pct / 100 * circ;
    const rotate = offset / circ * 360 - 90;
    offset += len;
    return `<circle cx="${cx}" cy="${cy}" r="${r}" fill="none" stroke="${s.color}" stroke-width="${sw}"
        stroke-dasharray="${len.toFixed(2)} ${(circ - len).toFixed(2)}"
        transform="rotate(${rotate.toFixed(2)} ${cx} ${cy})"><title>${esc(s.label)}: ${s.pct}%</title></circle>`;
  }).join('');
  return `<svg viewBox="0 0 ${size} ${size}" style="width:180px;height:180px;display:block;margin:0 auto">
      <circle cx="${cx}" cy="${cy}" r="${r}" fill="none" stroke="var(--line)" stroke-width="${sw}"/>${arcs}
      <text x="${cx}" y="${cy - 4}" text-anchor="middle" font-size="22" font-weight="700" fill="var(--text)">${esc(centerValue)}</text>
      <text x="${cx}" y="${cy + 16}" text-anchor="middle" font-size="11" fill="var(--muted)">${esc(centerLabel)}</text>
    </svg>`;
}
/* วงแหวนแสดงค่าเดียว (ความแม่นยำ OCR) */
function radialGauge(pct, label) {
  const size = 150, r = 60, cx = size / 2, cy = size / 2, sw = 15;
  const circ = 2 * Math.PI * r;
  const len = Math.max(0, Math.min(100, pct)) / 100 * circ;
  return `<svg viewBox="0 0 ${size} ${size}" style="width:150px;height:150px;display:block;margin:0 auto">
      <circle cx="${cx}" cy="${cy}" r="${r}" fill="none" stroke="var(--line)" stroke-width="${sw}"/>
      <circle cx="${cx}" cy="${cy}" r="${r}" fill="none" stroke="var(--brand)" stroke-width="${sw}" stroke-linecap="round"
          stroke-dasharray="${len.toFixed(2)} ${(circ - len).toFixed(2)}" transform="rotate(-90 ${cx} ${cy})"/>
      <text x="${cx}" y="${cy - 2}" text-anchor="middle" font-size="24" font-weight="700" fill="var(--text)">${pct}%</text>
      <text x="${cx}" y="${cy + 18}" text-anchor="middle" font-size="10" fill="var(--muted)">${esc(label)}</text>
    </svg>`;
}
function statusBadge(s) {
  const m = { NEW: ['b-idle', 'Pending Mapping'], INCOMPLETE: ['b-fail', 'Mapping Incomplete'], MAPPED: ['b-ok', 'Pending SAP Connection'], POSTED: ['b-ok', 'SAP Connected Successfully'], SPLIT: ['b-warn', 'แยกเป็นหลาย SO แล้ว'] }[s] || ['b-idle', s];
  return `<span class="badge ${m[0]}">${m[1]}</span>`;
}
function viewAllInbox() { S.module = null; S.inboxPage = 1; S.inboxDateFrom = ''; S.inboxDateTo = ''; go('inbox'); }

/* ---------------------------------------------------------------- pagination (ใช้ร่วมกันทุกหน้า List) */
// ตัดหน้าฝั่ง client จาก list ที่กรอง/ค้นหามาแล้ว — คืนแค่แถวของหน้าปัจจุบัน + ข้อมูลไว้วาด pager
function paginate(list, pageKey, sizeKey) {
  const pageSize = S[sizeKey] || 50;
  const totalPages = Math.max(1, Math.ceil(list.length / pageSize));
  S[pageKey] = Math.min(Math.max(1, S[pageKey] || 1), totalPages);
  const start = (S[pageKey] - 1) * pageSize;
  return { pageRows: list.slice(start, start + pageSize), totalPages, pageSize, total: list.length };
}
function pagerHtml(pageKey, sizeKey, totalPages, total, pageSize, rerenderFn) {
  const page = S[pageKey];
  return `<div class="row" style="justify-content:space-between;align-items:center;margin-top:12px;flex-wrap:wrap;gap:10px">
    <div class="row" style="gap:6px;align-items:center">
      <span class="hint">แสดง</span>
      <select onchange="S.${sizeKey}=parseInt(this.value);S.${pageKey}=1;${rerenderFn}()">
        ${[10, 50, 100].map(n => `<option value="${n}" ${n === pageSize ? 'selected' : ''}>${n}</option>`).join('')}
      </select>
      <span class="hint">รายการ/หน้า &middot; ทั้งหมด ${total} รายการ</span>
    </div>
    <div class="row" style="gap:6px;align-items:center">
      <button class="btn sm ghost" onclick="S.${pageKey}=Math.max(1,S.${pageKey}-1);${rerenderFn}()" ${page <= 1 ? 'disabled' : ''}>&lsaquo; ก่อนหน้า</button>
      <span class="hint">หน้า ${page} / ${totalPages}</span>
      <button class="btn sm ghost" onclick="S.${pageKey}=Math.min(${totalPages},S.${pageKey}+1);${rerenderFn}()" ${page >= totalPages ? 'disabled' : ''}>ถัดไป &rsaquo;</button>
    </div>
  </div>`;
}
// ช่วงวันที่ (from/to แบบ YYYY-MM-DD) — เทียบกับ field ที่เป็น date หรือ datetime string ก็ได้ (ตัดมาแค่ 10 ตัวแรก)
function dateRangeHtml(fromKey, toKey, onchangeFn) {
  return `<input type="date" value="${esc(S[fromKey] || '')}" onchange="S.${fromKey}=this.value;${onchangeFn}()" title="วันที่เริ่มต้น">
    <span class="hint">ถึง</span>
    <input type="date" value="${esc(S[toKey] || '')}" onchange="S.${toKey}=this.value;${onchangeFn}()" title="วันที่สิ้นสุด">
    ${S[fromKey] || S[toKey] ? `<button class="btn sm ghost" onclick="S.${fromKey}='';S.${toKey}='';${onchangeFn}()" title="ล้างช่วงวันที่">&#10005;</button>` : ''}`;
}
function inDateRange(dateStr, fromKey, toKey) {
  const d = String(dateStr || '').slice(0, 10);
  if (S[fromKey] && (!d || d < S[fromKey])) return false;
  if (S[toKey] && (!d || d > S[toKey])) return false;
  return true;
}

/* ---------------------------------------------------------------- WORK */
async function renderWork() {
  if (!S.doc) { $('#content').innerHTML = stepsHtml(1) + await uploadHtml(); bindDrop(); return; }
  $('#content').innerHTML = stepsHtml(S.doc.status === 'POSTED' ? 3 : 2) + await docHtml();
}

async function uploadHtml() {
  const needCategory = S.module === 'AP' && !S.uploadApDocCategory;
  const providers = await ocrProviders();
  const active = providers.find(p => p.id === S.ocrProvider) || providers[0];
  if (S.module === 'AP') await apDocCategories();
  const categoryBlock = S.module === 'AP' ? `
    <div class="row" style="margin-bottom:16px">
      <label class="hint" style="font-weight:600">&#128203; ประเภทเอกสาร</label>
      <select id="uploadApDocCategory" onchange="S.uploadApDocCategory=this.value;renderWork()">
        <option value="">— เลือกประเภทเอกสาร —</option>
        ${(S.apDocCategories || []).map(c => `<option value="${esc(c.id)}" ${c.id === S.uploadApDocCategory ? 'selected' : ''}>${esc(c.label)}</option>`).join('')}
      </select>
    </div>
    ${needCategory ? '<p class="hint" style="margin:-8px 0 16px">&#9888; กรุณาเลือกประเภทเอกสารก่อน จึงจะเลือกวิธีอ่านเอกสาร/นำเข้าไฟล์ได้</p>' : ''}` : '';
  return `
  <div class="card"><div class="card-h"><h2>ขั้นตอนที่ 1 — นำเข้าเอกสาร</h2><div class="sp"></div>
    <span class="hint">รองรับ PDF / JPG / PNG / TIFF</span></div>
  <div class="card-b">
    ${categoryBlock}
    <div class="row" style="margin-bottom:16px;${needCategory ? 'opacity:.45;pointer-events:none' : ''}">
      <label class="hint" style="font-weight:600">&#129504; วิธีอ่านเอกสาร (OCR Engine)</label>
      ${ocrProviderSelect('ocrEngine', S.ocrProvider)}
    </div>
    <p class="hint" style="margin:-8px 0 16px" id="ocrEngineDesc">${esc(active ? active.desc : '')}</p>
    <div class="drop" id="drop" style="${needCategory ? 'opacity:.45;pointer-events:none' : ''}">
      <div class="big">&#128228;</div>
      <div style="margin:12px 0 4px;font-weight:600">ลากไฟล์มาวางที่นี่ หรือ</div>
      <input type="file" id="fileIn" accept=".pdf,.jpg,.jpeg,.png,.tif,.tiff" hidden>
      <button class="btn primary" onclick="document.getElementById('fileIn').click()">เลือกไฟล์เอกสาร</button>
      <div class="hint" style="margin-top:12px">โมดูลปัจจุบัน: <b>${esc(moduleLabel(S.module))}</b></div>
      <div id="prog" style="display:none;max-width:440px;margin:18px auto 0">
        <div id="progText" class="hint"></div><div class="bar"><i id="progBar"></i></div></div>
    </div>
  </div></div>`;
}

function bindDrop() {
  const d = $('#drop'), f = $('#fileIn'); if (!d) return;
  ['dragenter', 'dragover'].forEach(e => d.addEventListener(e, ev => { ev.preventDefault(); d.classList.add('over'); }));
  ['dragleave', 'drop'].forEach(e => d.addEventListener(e, ev => { ev.preventDefault(); d.classList.remove('over'); }));
  d.addEventListener('drop', ev => { const file = ev.dataTransfer.files[0]; if (file) uploadFile(file); });
  f.addEventListener('change', () => { if (f.files[0]) uploadFile(f.files[0]); });
  const eng = $('#ocrEngine');
  if (eng) eng.addEventListener('change', () => {
    S.ocrProvider = eng.value;
    const p = (S.ocrProviders || []).find(x => x.id === eng.value);
    if ($('#ocrEngineDesc')) $('#ocrEngineDesc').textContent = p ? p.desc : '';
  });
}

function progress(on, text, pct) {
  const p = $('#prog'); if (!p) return;
  p.style.display = on ? 'block' : 'none';
  if ($('#progText')) $('#progText').textContent = text || '';
  if ($('#progBar')) $('#progBar').style.width = (pct || 0) + '%';
}

/* ห้ามใช้ engine อื่นมาแทนแบบเงียบ ๆ เมื่ออ่านเอกสารไม่สำเร็จ — ต้อง popup แจ้งเตือนชัดเจน แล้วให้ผู้ใช้
   แนบภาพเอกสารไปที่แชท AI (ด้านล่างของหน้าเอกสาร) เพื่อให้อ่านให้แทน */
function showReadFailedPopup(doc) {
  openModal(`<div class="card-h"><h2>&#9888; อ่านเอกสารไม่สำเร็จ</h2>
      <div class="sp"></div><button class="btn sm" onclick="closeModal()">&#10005;</button></div>
    <div class="card-b">
      <p class="hint">${esc(doc.ocrNote || 'ระบบอ่านข้อมูลจากไฟล์นี้ไม่สำเร็จ')}</p>
      <p>กรุณาแนบภาพเอกสารในกล่องแชท AI ด้านล่าง แล้วพิมพ์บอกข้อมูลที่ต้องการให้ AI ช่วยกรอกให้แทน</p>
      <div class="row" style="margin-top:16px;justify-content:flex-end">
        <button class="btn primary" onclick="focusChatAttach()">&#128206; ไปที่แชท AI</button>
      </div>
    </div>`);
}

async function uploadFile(file) {
  if (S.module === 'AP' && !S.uploadApDocCategory) { toast('&#9888; กรุณาเลือกประเภทเอกสารก่อน'); return; }
  const fd = new FormData();
  fd.append('module', S.module); fd.append('user', S.user); fd.append('ocr', S.ocrProvider); fd.append('file', file);
  fd.append('apDocCategory', S.uploadApDocCategory || '');
  progress(true, 'กำลังอัปโหลด ' + file.name + ' …', 35);
  await guard(async () => {
    progress(true, 'กำลังอ่านเอกสาร (' + S.ocrProvider + ') …', 70);
    const doc = await API.upload('/api/documents/upload', fd);
    progress(true, 'เสร็จสิ้น', 100);
    S.doc = doc; S.map = null; S.manual = { header: {}, lines: {} }; S.chatImage = null;
    await loadChatHistory(doc.docId);
    render();
    if (doc.provider === 'failed') showReadFailedPopup(doc);
    else toast('&#10003; อ่านเอกสารสำเร็จ (' + doc.provider + ') — พบ ' + doc.lines.length + ' รายการ', 5000);
  }).catch(() => progress(false));
}

async function useSample(i) {
  if (S.module === 'AP' && !S.uploadApDocCategory) { toast('&#9888; กรุณาเลือกประเภทเอกสารก่อน'); return; }
  await guard(async () => {
    const doc = await API.post('/api/documents/sample', { module: S.module, index: i, user: S.user, apDocCategory: S.uploadApDocCategory });
    S.doc = doc; S.map = null; S.manual = { header: {}, lines: {} }; S.chatImage = null;
    await loadChatHistory(doc.docId);
    render(); toast('&#10003; สร้างเอกสารในระบบแล้ว (DocId ' + doc.docId + ')');
  });
}

async function reOcr(btn) {
  const sel = $('#docOcrEngine');
  const engine = sel ? sel.value : 'auto';
  const label = btn ? btn.innerHTML : '';
  if (btn) { btn.disabled = true; btn.innerHTML = '<span class="hg" style="display:inline-block">&#8987;</span> กำลังอ่านเอกสาร…'; }
  await guard(async () => {
    const doc = await API.post('/api/documents/' + S.doc.docId + '/reocr', { ocr: engine, user: S.user });
    S.doc = doc; S.map = null; S.manual = { header: {}, lines: {} }; S.chatImage = null;
    // ไม่ล้างประวัติแชท — อ่านเอกสารใหม่ไม่ได้ลบ doc_id จึงยังอยู่บันทึกไว้ที่ server เหมือนเดิม
    await renderWork();
    if (doc.provider === 'failed') { showReadFailedPopup(doc); return; }
    openModal(`<div class="card-h"><h2>&#10003; อ่านเอกสารใหม่เสร็จแล้ว</h2>
        <div class="sp"></div><button class="btn sm" onclick="closeModal()">&#10005;</button></div>
      <div class="card-b">
        <p>Engine: <b>${esc(doc.provider || '')}</b> &nbsp;·&nbsp; ความมั่นใจ: <b>${Math.round((doc.confidence || 0) * 100)}%</b></p>
        <p>พบรายการ Item Detail: <b>${doc.lines.length}</b> รายการ</p>
        ${doc.tokensIn != null ? `<p>Token ที่ใช้: <b>${num(doc.tokensIn).toLocaleString('en-US')}</b> input / <b>${num(doc.tokensOut).toLocaleString('en-US')}</b> output
          ${doc.cost != null ? ` &nbsp;·&nbsp; ค่าใช้จ่ายโดยประมาณ: <b>${fmtCost(doc.costIn)}</b> input + <b>${fmtCost(doc.costOut)}</b> output = <b>${fmtCost(doc.cost)} ${esc(doc.costCurrency || '')}</b>` : ''}</p>` : ''}
        ${doc.confidenceNote ? `<p class="hint">&#9888; ${esc(doc.confidenceNote)}</p>` : ''}
        <div class="row" style="margin-top:16px;justify-content:flex-end">
          <button class="btn primary" onclick="closeModal()">ตกลง</button>
        </div>
      </div>`);
  }).catch(() => { if (btn) { btn.disabled = false; btn.innerHTML = label; } });
}

async function showRaw() {
  await guard(async () => {
    const r = await API.get('/api/documents/' + S.doc.docId + '/rawtext');
    openModal(`<div class="card-h"><h2>ข้อความที่อ่านได้จากไฟล์</h2><div class="sp"></div>
      <button class="btn sm" onclick="closeModal()">&#10005;</button></div>
      <div class="card-b"><p class="hint">ใช้ตรวจว่าตัวอ่านเอกสารเห็นอะไรบ้าง</p>
      <pre class="json">${esc(r.text || '(ไม่มีข้อความ — เป็นไฟล์สแกนหรือสร้างจากชุดตัวอย่าง)')}</pre></div>`);
  });
}

function reviewDocument() {
  const d = S.doc;
  const fileUrl = `${API_BASE}/api/documents/${d.docId}/file`;
  const ext = (d.fileName || '').split('.').pop().toLowerCase();
  const isImg = ['jpg', 'jpeg', 'png', 'tif', 'tiff', 'bmp', 'webp'].includes(ext);
  const viewer = d.provider === 'demo'
    ? '<p class="hint">เอกสารนี้สร้างจากชุดตัวอย่าง (demo) ไม่มีไฟล์ต้นฉบับให้เปิดดู</p>'
    : isImg
      ? `<img src="${esc(fileUrl)}" style="max-width:100%;border-radius:var(--r3);border:1px solid var(--line)">`
      : `<iframe src="${esc(fileUrl)}" style="width:100%;height:78vh;border:1px solid var(--line);border-radius:var(--r3)"></iframe>`;
  openModal(`<div class="card-h"><h2>&#128065; Review Document — ${esc(d.fileName)}</h2><div class="sp"></div>
      ${d.provider !== 'demo' ? `<a class="btn sm ghost" href="${esc(fileUrl)}" target="_blank" rel="noopener">เปิดแท็บใหม่</a>` : ''}
      <button class="btn sm" onclick="closeModal()">&#10005;</button></div>
    <div class="card-b"><p class="hint">เปรียบเทียบไฟล์ต้นฉบับกับข้อมูลที่อ่านได้ในหน้า HEADER/DETAIL</p>${viewer}</div>`, 'wide');
}

async function openDoc(id) {
  await guard(async () => {
    const doc = await API.get('/api/documents/' + id);
    S.doc = doc; S.module = doc.module; S.map = null; S.manual = { header: {}, lines: {} }; S.chatImage = null;
    await loadChatHistory(doc.docId);
    if (doc.mapStatus) await runMap(true);
    go('work');
  });
}

/* ---------------------------------------------------------------- document view */
async function docHtml() {
  const d = S.doc, h = d.header, mp = S.map, md = await masters();
  const providers = await ocrProviders();
  if (d.module === 'AP') await apDocCategories();
  const def = d.module === 'SO' ? SO_H : d.module === 'II' ? II_H : d.module === 'PODP' ? PODP_H : AP_H;
  const posted = d.status === 'POSTED';
  const isSplit = d.status === 'SPLIT';
  const canSplit = d.module === 'SO' && d.lines.length > 1 && !posted && !isSplit && !d.sourceDocId;

  const headerFieldGrid = list => list.map(([k, label, type, opts]) => `<div class="f"><label>${esc(label)}</label>
      ${type === 'select'
        ? `<select ${posted ? 'disabled' : ''} onchange="editHeader('${k}',this.value)">
             ${opts.map(([v, t]) => `<option value="${esc(v)}" ${v === (h[k] || '') ? 'selected' : ''}>${esc(t)}</option>`).join('')}
           </select>`
        : `<input type="text" value="${esc(h[k] == null ? '' : h[k])}" ${posted ? 'readonly' : ''}
             oninput="editHeader('${k}',this.value)">`}
    </div>`).join('');
  // แสดงเป็น Tab แยกตามกลุ่ม เหมือนหน้าจอ MIRO/Incoming Invoices จริง — ใช้ helper เดียวกัน คนละชุด field ตาม module
  const tabbedGroupsHtml = (groups, tabStateKey) => {
    const active = groups.find(g => g.title === S[tabStateKey]) || groups[0];
    const tabsHtml = groups.map(g =>
      `<button class="${g.title === active.title ? 'on' : ''}" onclick="S.${tabStateKey}='${g.title}';renderWork()">${esc(g.title)}</button>`).join('');
    return `<div class="tabs">${tabsHtml}</div><div class="card-b"><div class="grid">${headerFieldGrid(active.fields)}</div></div>`;
  };
  const tradeGroupsHtml = d.module === 'AP' ? tabbedGroupsHtml(AP_TRADE_GROUPS, 'miroTab') : '';
  const iiGroupsHtml = d.module === 'II' ? tabbedGroupsHtml(II_GROUPS, 'iiTab') : '';
  // ฟิลด์ยอดเงิน (subTotal/vatRate/vatAmount/whtAmount/totalAmount) — format 0,000.## (คั่นหลักพัน ไม่บังคับทศนิยม)
  // สลับ raw/format ตอน focus/blur เหมือนช่องตัวเลขใน DETAIL
  const totalsFieldGrid = list => list.map(f => `<div class="f"><label>${f[1]}</label>
      <input type="text" value="${fmtAmt(h[f[0]])}" ${posted ? 'readonly' : ''}
             onfocus="this.value=num(this.value)" onblur="this.value=fmtAmt(this.value)"
             oninput="editHeader('${f[0]}',this.value)"></div>`).join('');
  const fields = headerFieldGrid(def);
  const totalsFields = d.module === 'AP' ? totalsFieldGrid(AP_TOTALS_H)
    : d.module === 'SO' ? totalsFieldGrid(SO_TOTALS_H) + headerFieldGrid(SO_REMARK_H)
    : d.module === 'II' ? totalsFieldGrid(II_TOTALS_H)
    : d.module === 'PODP' ? totalsFieldGrid(PODP_TOTALS_H) : '';
  const glItems = h.glItems || [];
  const showGlItems = d.module === 'AP' || d.module === 'II';
  const showDetail = d.module !== 'II' && d.module !== 'PODP';   // ไม่มีรายการสินค้า/บริการแบบมี Material
  const glItemsRows = glItems.map((g, i) => `<tr>
      <td><input value="${esc(g.glAccount || '')}" ${posted ? 'readonly' : ''} oninput="editGlItem(${i},'glAccount',this.value)"></td>
      <td><select ${posted ? 'disabled' : ''} onchange="editGlItem(${i},'drCr',this.value)">
        <option value="">— เลือก —</option>
        <option value="D" ${g.drCr === 'D' ? 'selected' : ''}>Debit</option>
        <option value="C" ${g.drCr === 'C' ? 'selected' : ''}>Credit</option>
      </select></td>
      <td class="num"><input value="${fmt(g.amount)}" ${posted ? 'readonly' : ''} onfocus="this.value=num(this.value)" onblur="this.value=fmt(this.value)" oninput="editGlItem(${i},'amount',this.value)"></td>
      <td><input value="${esc(g.taxCode || '')}" ${posted ? 'readonly' : ''} oninput="editGlItem(${i},'taxCode',this.value)"></td>
      <td><input value="${esc(g.assignment || '')}" ${posted ? 'readonly' : ''} oninput="editGlItem(${i},'assignment',this.value)"></td>
      <td><input value="${esc(g.itemText || '')}" ${posted ? 'readonly' : ''} oninput="editGlItem(${i},'itemText',this.value)"></td>
      <td><input value="${esc(g.costCenter || '')}" ${posted ? 'readonly' : ''} oninput="editGlItem(${i},'costCenter',this.value)"></td>
      <td>${posted ? '' : `<button class="btn sm ghost" onclick="delGlItem(${i})">&#10005;</button>`}</td></tr>`).join('');

  const mapCards = mp ? mappingCards(d, mp, md, posted) : '';

  const matOpts = md.materials.map(m => ({ v: m.MaterialCode, t: m.MaterialCode + ' — ' + m.Description }));
  const showPoExtra = d.module === 'AP';
  const showSoExtra = d.module === 'SO';
  const extraCols = (showPoExtra ? 1 : 0) + (showSoExtra ? 2 : 0);
  const rows = d.lines.map((l, i) => {
    const r = mp ? mp.lines[i] : null;
    const cell = !mp ? '<span class="badge b-idle">รอ Mapping</span>'
      : `<select onchange="setManualLine(${i},this.value)" ${posted ? 'disabled' : ''}>
           <option value="">-- ไม่พบ / กรุณาเลือก --</option>
           ${matOpts.map(o => `<option value="${esc(o.v)}" ${r.code === o.v ? 'selected' : ''}>${esc(o.t)}</option>`).join('')}
         </select>`;
    const st = !mp ? '' : (r.status === 'fail' ? '<span class="badge b-fail">&#10007; ไม่พบ</span>'
      : (r.status === 'manual' ? '<span class="badge b-warn">&#9998; เลือกเอง</span>'
        : `<span class="badge b-ok">&#10003; ${esc(r.method)}</span>`));
    return `<tr>
      <td style="text-align:center">${l.itemNo}</td>
      <td><input value="${esc(l.extCode)}" ${posted ? 'readonly' : ''} oninput="editLine(${i},'extCode',this.value)"></td>
      <td><input value="${esc(l.desc)}" ${posted ? 'readonly' : ''} oninput="editLine(${i},'desc',this.value)"></td>
      <td class="num"><input value="${fmtAmt(l.qty)}" ${posted ? 'readonly' : ''} onfocus="this.value=num(this.value)" onblur="this.value=fmtAmt(this.value)" oninput="editLine(${i},'qty',this.value)"></td>
      <td><input value="${esc(l.uom)}" ${posted ? 'readonly' : ''} oninput="editLine(${i},'uom',this.value)" style="width:64px"></td>
      <td class="num"><input value="${fmtAmt(l.price)}" ${posted ? 'readonly' : ''} onfocus="this.value=num(this.value)" onblur="this.value=fmtAmt(this.value)" oninput="editLine(${i},'price',this.value)"></td>
      <td class="num"><input value="${fmtAmt(l.amount)}" ${posted ? 'readonly' : ''} onfocus="this.value=num(this.value)" onblur="this.value=fmtAmt(this.value)" oninput="editLine(${i},'amount',this.value)"></td>
      <td class="${mp && mp.lines[i].status === 'fail' ? 'cell-fail' : ''}" style="min-width:270px">${cell}</td>
      <td class="${mp && (mp.lines[i].uom || {}).status === 'fail' ? 'cell-fail' : ''}" style="min-width:170px">${uomCell(mp, i, l, posted)}</td>
      <td style="white-space:nowrap">${st}
        ${mp && mp.lines[i].status === 'manual' && mp.lines[i].code && !posted
        ? `<button class="btn sm" style="margin-left:4px" onclick="learn(${i})">&#43; Master</button>` : ''}</td>
      ${showPoExtra ? `<td style="white-space:nowrap"><button class="btn sm ${lineExtraCount(l) ? '' : 'ghost'}" onclick="showLineExtra(${i})">
          &#128203; PO ${lineExtraCount(l) ? `(${lineExtraCount(l)}/${PO_LINE_EXTRA_FIELDS.length})` : ''}</button></td>` : ''}
      ${showSoExtra ? `<td style="min-width:160px"><input value="${esc((l.extra || {}).salesEmployeeName || '')}" ${posted ? 'readonly' : ''} oninput="editLineExtra(${i},'salesEmployeeName',this.value)"></td>
      <td style="min-width:140px"><input value="${esc((l.extra || {}).deliveryDate || '')}" ${posted ? 'readonly' : ''} oninput="editLineExtra(${i},'deliveryDate',this.value)"></td>` : ''}
      <td>${posted ? '' : `<button class="btn sm ghost" onclick="delLine(${i})">&#10005;</button>`}</td></tr>`;
  }).join('');
  const sum = d.lines.reduce((a, l) => a + num(l.amount), 0);

  let panel = '';
  if (mp) {
    panel = mp.pass
      ? `<div class="result ok"><h3><span class="badge b-ok">&#10003; ผ่าน</span> Mapping ข้อมูลครบถ้วน — พร้อมส่งเข้า SAP</h3>
         <div class="hint">ตรวจสอบครบ ${(d.module === 'SO' ? 2 : 1) + d.lines.length} จุด: ${d.module === 'SO' ? 'Customer, Ship-to' : 'Vendor'} และ Material ${d.lines.length} รายการ</div>
         ${mp.warns.length ? '<ul>' + mp.warns.map(w => '<li>&#9888; ' + esc(w) + '</li>').join('') + '</ul>' : ''}</div>`
      : `<div class="result bad"><h3><span class="badge b-fail">&#10007; ไม่ผ่าน</span> ไม่พบข้อมูล ${mp.errors.length} จุด</h3>
         <ul>${mp.errors.map(e => `<li><b>${esc(e.field)}:</b> ${esc(e.msg)}<br><span class="hint">&#8627; วิธีแก้: ${esc(e.fix)}</span></li>`).join('')}</ul>
         ${mp.warns.length ? '<div style="margin-top:10px"><b>คำเตือน</b><ul>' + mp.warns.map(w => '<li>&#9888; ' + esc(w) + '</li>').join('') + '</ul></div>' : ''}
         <div class="row" style="margin-top:14px"><button class="btn" onclick="go('master')">&#9881; ไปหน้า Master Mapping</button>
         <span class="hint">หรือเลือกค่าที่ถูกต้องจาก dropdown ด้านล่าง</span></div></div>`;
  }
  const postedBox = posted ? `<div class="result ok"><h3>&#10003; ส่งเข้า SAP S/4HANA สำเร็จ</h3>
      <div>เอกสาร SAP: <code>${esc(d.sapDocNo)}</code> &nbsp;|&nbsp; ${esc(moduleLabel(d.module))} &nbsp;|&nbsp; ${dt(d.postedAt)}</div></div>` : '';
  const splitBanner = isSplit ? `<div class="result ok"><h3>&#10003; เอกสารนี้ถูกแยกออกเป็น ${d.splitChildren.length} Sales Order แล้ว</h3>
      <div class="hint" style="margin-bottom:10px">เอกสารนี้เก็บไว้เป็นเอกสารอ้างอิงเท่านั้น — ไปทำ Mapping/ส่งเข้า SAP ที่เอกสารที่แยกไว้ด้านล่างแทน</div>
      <div class="tw"><table><thead><tr><th>เอกสาร</th><th>เลขที่</th><th>สถานะ</th><th style="text-align:right">ยอดรวม</th></tr></thead>
      <tbody>${d.splitChildren.map(c => `<tr><td><a href="#" onclick="event.preventDefault();openDoc(${c.DocId})">#${c.DocId}</a></td>
        <td>${esc(c.DocNo || '')}</td><td>${statusBadge(c.Status)}</td><td style="text-align:right">${fmt(c.TotalAmount)}</td></tr>`).join('')}</tbody>
      </table></div></div>` : '';
  const splitFromNote = d.sourceDocId ? `<div class="hint" style="margin:-6px 0 14px;padding:8px 12px;background:var(--line-soft);border-radius:var(--r2)">
      &#8617; แยกมาจากเอกสาร <a href="#" onclick="event.preventDefault();openDoc(${d.sourceDocId})">#${d.sourceDocId}</a></div>` : '';

  return `${panel}${postedBox}${splitBanner}
  <div class="card">
    <div class="card-h"><h2>เอกสาร #${d.docId}</h2>
      <span class="badge ${d.provider === 'failed' ? 'b-fail' : d.confidence >= 0.9 ? 'b-ok' : 'b-warn'}" ${d.confidenceNote ? `title="${esc(d.confidenceNote)}"` : ''}>OCR ${Math.round((d.confidence || 0) * 100)}% · ${esc(d.provider || '')}</span>
      ${d.tokensIn != null ? `<span class="badge" title="Token ที่ใช้อ่านเอกสารนี้">&#9889; ${num(d.tokensIn).toLocaleString('en-US')} in / ${num(d.tokensOut).toLocaleString('en-US')} out</span>` : ''}
      ${d.cost != null ? `<span class="badge" title="Input: ${fmtCost(d.costIn)} ${esc(d.costCurrency || '')} · Output: ${fmtCost(d.costOut)} ${esc(d.costCurrency || '')}">&#128176; ${fmtCost(d.cost)} ${esc(d.costCurrency || '')}</span>` : ''}
      <span class="filechip">&#128196; ${esc(d.fileName)}</span>${statusBadge(d.status)}
      <div class="sp"></div>
      ${!posted && !isSplit && !d.sourceDocId ? ocrProviderSelect('docOcrEngine', { ocr: 'tesseract', text: 'text', azure: 'azure', claude: 'claude', claude_text: 'claude_text', typhoon: 'typhoon', gemini: 'gemini', openai: 'openai' }[d.provider] || 'auto') : ''}
      <button class="btn sm primary" onclick="reOcr(this)" ${posted || isSplit || d.sourceDocId ? 'disabled' : ''}
        title="อ่านไฟล์ต้นฉบับใหม่ด้วย engine ที่เลือก">&#8635; อ่านเอกสารใหม่</button>
      <button class="btn sm ghost" onclick="showRaw()">&#128196; ข้อความที่อ่านได้</button>
      <button class="btn sm ghost" onclick="reviewDocument()" title="เปิดดูไฟล์ต้นฉบับ เทียบกับข้อมูลที่อ่านได้">&#128065; Review Document</button>
      ${canSplit ? `<button class="btn sm ghost" onclick="openSplitModal()" title="แยกรายการสินค้าออกเป็นหลาย Sales Order">&#10740; แยกเป็นหลาย SO</button>` : ''}
      <button class="btn sm ghost" onclick="S.doc=null;S.map=null;S.chatHistory=[];S.chatImage=null;renderWork()">เปลี่ยนเอกสาร</button></div>
    <div class="card-b">
      ${d.module === 'AP' ? `<div class="f" style="max-width:320px;margin-bottom:14px">
          <label>ประเภทเอกสาร</label>
          <select ${posted ? 'disabled' : ''} onchange="setDocCategory(this.value)">
            <option value="">— เลือกประเภทเอกสาร —</option>
            ${(S.apDocCategories || []).map(c => `<option value="${esc(c.id)}" ${c.id === d.apDocCategory ? 'selected' : ''}>${esc(c.label)}</option>`).join('')}
          </select></div>` : ''}
      ${splitFromNote}
      ${d.confidenceNote ? `<div class="hint" style="margin:-6px 0 14px;padding:8px 12px;background:var(--line-soft);border-radius:var(--r2)">
          &#9888; เหตุผลที่ความแม่นยำไม่ถึง 100%: ${esc(d.confidenceNote)}</div>` : ''}
      <p class="sec-title">HEADER — ข้อมูลส่วนหัว</p>
      <div class="grid">${fields}</div>
    </div>
  </div>
  ${mapCards}

  ${showDetail ? `<div class="card">
    <div class="card-h"><h2>DETAIL — รายการสินค้า (${d.lines.length} บรรทัด)</h2><div class="sp"></div>
      ${posted ? '' : '<button class="btn sm" onclick="addLine()">&#43; เพิ่มบรรทัด</button>'}</div>
    <div class="card-b"><div class="tw"><table>
      <thead><tr><th style="width:54px">Item</th><th style="width:150px">รหัสสินค้า (คู่ค้า)</th><th style="min-width:260px">ชื่อสินค้าตามเอกสาร</th>
        <th style="min-width:110px">จำนวน</th><th style="width:74px">หน่วย</th><th style="min-width:130px">ราคา/หน่วย</th>
        <th style="min-width:140px">จำนวนเงิน</th><th style="min-width:270px">Material (SAP)</th><th style="min-width:170px">หน่วย &rarr; SAP</th><th>สถานะ</th>
        ${showPoExtra ? '<th style="width:120px">PO Detail</th>' : ''}
        ${showSoExtra ? '<th style="min-width:160px">Sales Employee Name</th><th style="min-width:140px">Delivery Date</th>' : ''}
        <th style="width:44px"></th></tr></thead>
      <tbody>${rows || `<tr><td colspan="${11 + extraCols}" class="empty">ไม่มีรายการ</td></tr>`}</tbody>
      <tfoot><tr class="totrow"><td colspan="6" style="text-align:right">รวม</td>
        <td style="text-align:right">${fmtAmt(sum)}</td><td colspan="${4 + extraCols}"></td></tr></tfoot>
    </table></div></div>
  </div>` : ''}

  ${totalsFields ? `<div class="card">
    <div class="card-h"><h2>ยอดรวม</h2></div>
    <div class="card-b"><div class="grid">${totalsFields}</div></div>
  </div>` : ''}

  ${tradeGroupsHtml ? `<div class="card">
    <div class="card-h"><h2>Supplier Invoice (MIRO)</h2></div>
    ${tradeGroupsHtml}
  </div>` : ''}

  ${iiGroupsHtml ? `<div class="card">
    <div class="card-h"><h2>Incoming Invoice</h2></div>
    ${iiGroupsHtml}
  </div>` : ''}

  ${showGlItems ? `<div class="card">
    <div class="card-h"><h2>${d.module === 'II' ? 'Line Items' : 'G/L Account Items'} (${glItems.length})</h2><div class="sp"></div>
      ${posted ? '' : '<button class="btn sm" onclick="addGlItem()">&#43; เพิ่มรายการ</button>'}</div>
    <div class="card-b"><div class="tw"><table>
      <thead><tr><th style="min-width:140px">G/L Account</th><th style="width:100px">Dr/Cr</th>
        <th style="min-width:130px">Amount</th><th style="min-width:100px">Tax Code</th>
        <th style="min-width:140px">Assignment</th><th style="min-width:180px">Item Text</th>
        <th style="min-width:130px">Cost Center</th><th style="width:44px"></th></tr></thead>
      <tbody>${glItemsRows || '<tr><td colspan="8" class="empty">ไม่มีรายการ</td></tr>'}</tbody>
    </table></div></div>
  </div>` : ''}

  ${!posted ? chatFixCard() : ''}

  <div class="card"><div class="card-b row">
    <button class="btn primary" onclick="runMap()" ${posted || isSplit ? 'disabled' : ''}>&#128269; ขั้นตอนที่ 2 — Mapping ข้อมูล</button>
    <button class="btn success" onclick="postSAP()" ${mp && mp.pass && !posted && !isSplit ? '' : 'disabled'}>&#9099; ขั้นตอนที่ 3 — ส่งเข้า SAP S/4HANA</button>
    <button class="btn" onclick="showPayload()" ${mp && mp.pass ? '' : 'disabled'}>&#123;&#125; ดู Payload</button>
    <div style="flex:1"></div>
    <span class="hint">${isSplit ? 'เอกสารนี้ถูกแยกไปเป็น Sales Order อื่นแล้ว' : posted ? 'เอกสารนี้ส่งเข้า SAP แล้ว' : (mp ? (mp.pass ? 'พร้อมส่งเข้า SAP' : 'แก้ไขข้อมูลที่ไม่ผ่านก่อนส่ง') : 'กด Mapping เพื่อตรวจสอบกับ Master Data')}</span>
  </div></div>`;
}

/* ---------- แยกเอกสาร PO เป็นหลาย Sales Order — ผู้ใช้เลือกเองว่ารายการไหนไปกลุ่มไหน ---------- */
function openSplitModal() {
  const d = S.doc;
  S._splitAssign = {};
  const rows = d.lines.map(l => `<tr>
      <td style="text-align:center">${l.itemNo}</td>
      <td>${esc(l.desc)}</td>
      <td class="num">${fmt(l.qty)} ${esc(l.uom)}</td>
      <td class="num">${fmt(l.amount)}</td>
      <td><input type="number" min="1" step="1" style="width:70px" placeholder="—"
          oninput="S._splitAssign[${l.itemNo}]=this.value;updateSplitSummary()"></td>
    </tr>`).join('');
  openModal(`<div class="card-h"><h2>แยกเอกสารเป็นหลาย Sales Order</h2><div class="sp"></div>
      <button class="btn sm" onclick="closeModal()">&#10005;</button></div>
    <div class="card-b">
      <p class="hint">ใส่หมายเลขกลุ่ม (1, 2, 3, ...) ให้แต่ละรายการที่จะแยกออกไปเป็น Sales Order ใหม่ —
        รายการที่เว้นว่างจะไม่ถูกแยก (ยังอยู่ในเอกสารต้นฉบับเท่านั้น) ต้องมีอย่างน้อย 2 กลุ่มจึงจะ Split ได้</p>
      <div class="tw"><table><thead><tr><th>Item</th><th>รายการ</th><th>จำนวน</th><th style="text-align:right">จำนวนเงิน</th><th>กลุ่ม SO</th></tr></thead>
      <tbody>${rows}</tbody></table></div>
      <div id="splitSummary" class="hint" style="margin-top:12px"></div>
      <div class="row" style="margin-top:16px;justify-content:flex-end">
        <button class="btn primary" onclick="confirmSplit()">ยืนยัน Split</button>
      </div>
    </div>`, 'wide');
  updateSplitSummary();
}
function updateSplitSummary() {
  const groups = {};
  Object.entries(S._splitAssign || {}).forEach(([itemNo, g]) => {
    g = parseInt(g); if (g > 0) (groups[g] = groups[g] || []).push(itemNo);
  });
  const n = Object.keys(groups).length;
  const el = $('#splitSummary');
  if (!el) return;
  el.innerHTML = n >= 2 ? `จะสร้าง <b>${n}</b> Sales Order ใหม่ (${Object.entries(groups).map(([g, items]) => `กลุ่ม ${g}: ${items.length} รายการ`).join(' · ')})`
    : '<span style="color:var(--red)">ต้องแบ่งอย่างน้อย 2 กลุ่ม</span>';
}
async function confirmSplit() {
  const assign = {};
  Object.entries(S._splitAssign || {}).forEach(([itemNo, g]) => { g = parseInt(g); if (g > 0) assign[itemNo] = g; });
  if (new Set(Object.values(assign)).size < 2) { toast('&#9888; ต้องแบ่งอย่างน้อย 2 กลุ่ม'); return; }
  await guard(async () => {
    const res = await API.post(`/api/documents/${S.doc.docId}/split`, { assign, user: S.user });
    S._splitAssign = {};
    closeModal();
    S.doc = res.source; S.map = null;
    toast(`&#10003; Split สำเร็จ — สร้าง ${res.created.length} Sales Order ใหม่`);
    await renderWork();
  });
}

/* ---------- แชทสั่งแก้ไขข้อมูล (AI) — แก้เฉพาะเอกสารนี้ ไม่บันทึกลง Master Data ---------- */
/* ประวัติเก็บถาวรที่ server (ocr.DocumentChat) — โหลดใหม่ทุกครั้งที่เปิดเอกสาร และหลังส่งข้อความสำเร็จ
   เพื่อให้ chatId/รูปภาพเป็นค่าจริงจาก server เสมอ (ไม่ใช่แค่ state ชั่วคราวในเบราว์เซอร์) */
async function loadChatHistory(docId) {
  try { S.chatHistory = await API.get(`/api/documents/${docId}/chat`); }
  catch (e) { S.chatHistory = []; }
}

/* เมื่ออ่านเอกสารไม่สำเร็จ — พาผู้ใช้ไปที่กล่องแชท AI ด้านล่างเพื่อแนบภาพเอกสารให้ AI อ่านแทน */
function focusChatAttach() {
  closeModal();
  const card = $('#chatInput')?.closest('.card');
  if (card) card.scrollIntoView({ behavior: 'smooth', block: 'center' });
  $('#chatInput')?.focus();
}

const CHAT_MODEL_IDS = ['claude', 'gemini', 'openai'];
function chatFixCard() {
  const chatModels = (S.ocrProviders || []).filter(p => CHAT_MODEL_IDS.includes(p.id));
  // ถ้าโมเดลที่เลือกไว้ยังไม่พร้อมใช้งาน (ไม่ได้ตั้งค่า key) แต่มีโมเดลอื่นพร้อม — สลับไปตัวที่พร้อมให้อัตโนมัติ
  if (!chatModels.find(p => p.id === S.chatProvider)?.ready) {
    const firstReady = chatModels.find(p => p.ready);
    if (firstReady) S.chatProvider = firstReady.id;
  }
  const ready = chatModels.some(p => p.ready);
  const modelSelect = `<select id="chatModelSelect" class="ocr-pick" onchange="S.chatProvider=this.value"
      title="เลือกโมเดล Vision ที่จะใช้อ่านภาพที่แนบมา — เผื่อโมเดลหลักอ่านเอกสารไม่สำเร็จ">
      ${chatModels.map(p => `<option value="${esc(p.id)}" ${p.id === S.chatProvider ? 'selected' : ''} ${p.ready ? '' : 'class="hint"'}
          title="${esc(p.desc)}">${esc(p.label)}${p.ready ? '' : ' (ยังไม่ได้ตั้งค่า)'}</option>`).join('')}
    </select>`;
  const msgs = S.chatHistory.map(m => {
    // m.image = data URL ชั่วคราว (ข้อความที่เพิ่งส่ง ยังไม่ได้ค่า chatId จาก server)
    // m.hasImage + m.chatId = ภาพที่บันทึกถาวรแล้ว โหลดผ่าน URL แทนการฝัง data URL ซ้ำ
    const imgSrc = m.image || (m.hasImage && m.chatId ? `${API_BASE}/api/documents/${S.doc.docId}/chat/${m.chatId}/image` : '');
    return `
    <div class="chat-msg ${m.role}">
      <b>${m.role === 'user' ? 'คุณ' : 'AI'}</b>
      ${imgSrc ? `<img src="${esc(imgSrc)}" class="chat-img">` : ''}
      ${m.text ? `<div>${esc(m.text)}</div>` : ''}
    </div>`;
  }).join('') ||
    '<p class="hint">พิมพ์หรือแนบภาพ (capture จุดที่ผิดจาก Review Document ก็ได้) บอกจุดที่ OCR อ่านผิดด้วยภาษาธรรมดา เช่น "ชื่อผู้ขายที่ถูกคือ บริษัท เอบีซี จำกัด ไม่ใช่ เอ็กซ์วายแซด" หรือถามคำถามเกี่ยวกับเอกสารนี้ก็ได้ — AI จะแก้เฉพาะเอกสารนี้ให้ ไม่กระทบเอกสารอื่น</p>';
  return `<div class="card">
    <div class="card-h"><h2>&#129302; แชทสั่งแก้ไขข้อมูล (AI)</h2><div class="sp"></div>
      ${modelSelect}
      ${!ready ? '<span class="hint">ต้องตั้งค่า API key ของโมเดล Vision อย่างน้อย 1 ตัวก่อนใช้งาน</span>' : ''}</div>
    <div class="card-b">
      <div class="chat-history" id="chatHistory">${msgs}</div>
      ${S.chatImage ? `<div class="chat-attach-preview">
          <img src="${esc(S.chatImage)}"><span class="hint">แนบภาพแล้ว</span>
          <button class="btn sm ghost" onclick="S.chatImage=null;renderWork()">&#10005; เอาภาพออก</button>
        </div>` : ''}
      <div class="row" style="margin-top:10px">
        <input id="chatFileIn" type="file" accept="image/*" hidden onchange="onChatFileChosen(event)">
        <button class="btn sm ghost" onclick="$('#chatFileIn').click()" title="แนบภาพ (capture จุดที่ผิด)" ${ready ? '' : 'disabled'}>&#128206;</button>
        <input id="chatInput" type="text" placeholder="เช่น ยอดรวมที่ถูกคือ 25,680 บาท (หรือวางภาพด้วย Ctrl+V)" style="flex:1"
          onkeydown="if(event.key==='Enter')sendChatFix()" onpaste="onChatPaste(event)" ${ready ? '' : 'disabled'}>
        <button class="btn primary" onclick="sendChatFix()" ${ready ? '' : 'disabled'}>&#10148; ส่ง</button>
      </div>
    </div>
  </div>`;
}

function readImageFile(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result);
    reader.onerror = reject;
    reader.readAsDataURL(file);
  });
}

async function onChatFileChosen(e) {
  const file = e.target.files && e.target.files[0];
  if (!file) return;
  S.chatImage = await readImageFile(file);
  await renderWork();
  $('#chatInput')?.focus();
}

async function onChatPaste(e) {
  const item = [...(e.clipboardData?.items || [])].find(i => i.type.startsWith('image/'));
  if (!item) return;
  e.preventDefault();
  S.chatImage = await readImageFile(item.getAsFile());
  await renderWork();
  $('#chatInput')?.focus();
}

async function sendChatFix() {
  const input = $('#chatInput');
  const message = (input?.value || '').trim();
  const image = S.chatImage;
  if (!message && !image) return;
  S.chatHistory.push({ role: 'user', text: message, image });    // แสดงทันทีก่อนรอ server (optimistic)
  if (input) input.value = '';
  S.chatImage = null;
  await renderWork();
  const hist = $('#chatHistory'); if (hist) hist.scrollTop = hist.scrollHeight;

  await guard(async () => {
    const r = await API.post(`/api/documents/${S.doc.docId}/chat-fix`, { message, image, user: S.user, provider: S.chatProvider });
    S.doc = r.document; S.map = null;
  }).catch(() => { /* ข้อความผู้ใช้ถูกบันทึกที่ server แล้วแม้ Claude ตอบไม่สำเร็จ — โหลดประวัติจริงด้านล่างอยู่ดี */ })
    .finally(async () => {
      await loadChatHistory(S.doc.docId);   // แทนที่ state ชั่วคราวด้วยประวัติจริงจาก server (chatId/รูปภาพถาวร)
      await renderWork();
      const h2 = $('#chatHistory'); if (h2) h2.scrollTop = h2.scrollHeight;
    });
}

/* ---------- การ์ดเทียบข้อมูล: เอกสาร (OCR) ↔ SAP ---------- */
function sideList(items, side) {
  if (!items || !items.length)
    return `<div class="hint" style="padding:6px 0">${side === 'sap' ? 'ยังไม่พบข้อมูลใน SAP' : '—'}</div>`;
  return '<table class="cmp"><tbody>' + items.map(f => {
    const tick = f.match === true ? ' <span class="badge b-ok" style="padding:1px 7px">&#10003;</span>'
      : (f.match === false ? ' <span class="badge b-warn" style="padding:1px 7px">&#8800;</span>' : '');
    return `<tr><th>${esc(f.label)}</th><td>${esc(f.value) || '<span class="hint">—</span>'}${tick}</td></tr>`;
  }).join('') + '</tbody></table>';
}

function statusChip(st) {
  if (st === 'ok') return '<span class="badge b-ok">&#10003; จับคู่อัตโนมัติ</span>';
  if (st === 'manual') return '<span class="badge b-warn">&#9998; เลือกด้วยตนเอง</span>';
  if (st === 'convert') return '<span class="badge b-ok">&#8646; แปลงหน่วยแล้ว</span>';
  if (st === 'fail') return '<span class="badge b-fail">&#10007; ไม่พบข้อมูล</span>';
  if (st === 'unitfail') return '<span class="badge b-fail">&#10007; ไม่พบกฎแปลงหน่วย</span>';
  return '<span class="badge b-idle">รอ Mapping</span>';
}

function cmpCard(no, title, r, picker, extra) {
  const st = r.status || 'idle';
  return `<div class="cmp-card ${st === 'fail' ? 'bad' : ''}">
    <div class="cmp-head"><span class="cmp-no">${no}</span><b>${title}</b>
      ${statusChip(st)}${r.sapCode ? `<span class="badge b-ok" title="รหัสที่ส่งเข้า SAP">SAP: ${esc(r.sapCode)}</span>` : ''}
      <span class="hint">${esc(r.method || '')}</span>
      <div class="sp"></div>${picker || ''}</div>
    <div class="cmp-body">
      <div class="cmp-col"><div class="cmp-label">&#128196; ข้อมูลจากเอกสาร</div>${sideList(r.doc, 'doc')}</div>
      <div class="cmp-arrow">&rarr;</div>
      <div class="cmp-col sap"><div class="cmp-label">&#127974; ข้อมูลจาก SAP</div>${sideList(r.sap, 'sap')}</div>
    </div>${extra || ''}</div>`;
}

function mappingCards(d, mp, md, posted) {
  const sel = (kind, key, code) => {
    const opts = optionsFor(kind, md);
    return `<select class="cmp-pick" onchange="setManualHeader('${key}',this.value)" ${posted ? 'disabled' : ''}>
      <option value="">-- เลือกเอง --</option>
      ${opts.map(o => `<option value="${esc(o.v)}" ${o.v === code ? 'selected' : ''}>${esc(o.t)}</option>`).join('')}</select>`;
  };
  const addBtn = (label, fn) => posted ? '' : ` <button class="btn sm" onclick="${fn}">&#43; ${label}</button>`;
  let cards = '', n = 0;
  if (d.module !== 'SO') {
    const r = mp.header.vendor;
    const picker = sel('vendors', 'vendor', r.code) + (r.status === 'fail' ? addBtn('เพิ่มผู้ขายใหม่', 'quickAddVendor()') : '');
    cards += cmpCard(++n, 'Vendor / Supplier', r, picker);
  } else {
    const c = mp.header.customer, sh = mp.header.shipTo;
    const custPicker = sel('customers', 'customer', c.code) + (c.status === 'fail' ? addBtn('เพิ่มลูกค้าใหม่', 'quickAddCustomer()') : '');
    const shipPicker = sel('shiptos', 'shipTo', sh.code) + (sh.status === 'fail'
      ? (c.code ? addBtn('เพิ่ม Ship-to ใหม่', 'quickAddShipTo()') : ' <span class="hint">ต้องระบุลูกค้าก่อน</span>') : '');
    cards += cmpCard(++n, 'Customer', c, custPicker);
    cards += cmpCard(++n, 'Ship-to', sh, shipPicker);
  }
  const matOpts = md.materials.map(m => ({ v: m.MaterialCode, t: m.MaterialCode + ' — ' + m.Description }));
  const nMat = ++n;
  const matCards = d.lines.map((l, i) => {
    const r = mp.lines[i], u = r.unit || { doc: [], sap: [], status: 'idle' };
    const picker = `<select class="cmp-pick" onchange="setManualLine(${i},this.value)" ${posted ? 'disabled' : ''}>
      <option value="">-- เลือกเอง --</option>
      ${matOpts.map(o => `<option value="${esc(o.v)}" ${r.code === o.v ? 'selected' : ''}>${esc(o.t)}</option>`).join('')}</select>`
      + (r.status === 'fail' ? addBtn('เพิ่ม Material ใหม่', 'quickAddMaterial(' + i + ')') : '');
    const unitBlock = `<div class="cmp-sub ${u.status === 'fail' ? 'bad' : ''}">
      <div class="cmp-head"><span class="cmp-no sub">${nMat + 1}</span><b>Relate Unit — การแปลงหน่วย</b>
        ${statusChip(u.status === 'fail' ? 'unitfail' : u.status)}
        <div class="sp"></div>
        ${u.status === 'fail' && !posted ? `<button class="btn sm" onclick="addUomRule(${i})">&#43; เพิ่มกฎแปลงหน่วย</button>` : ''}</div>
      <div class="cmp-body">
        <div class="cmp-col">${sideList(u.doc, 'doc')}</div>
        <div class="cmp-arrow">&rarr;</div>
        <div class="cmp-col sap">${sideList(u.sap, 'sap')}</div>
      </div></div>`;
    return cmpCard(`${nMat}.${i + 1}`, `Material — บรรทัดที่ ${i + 1}`, r, picker, r.code ? unitBlock : '');
  }).join('');

  return `<div class="card"><div class="card-h"><h2>ผลการ Mapping — เทียบข้อมูลเอกสารกับ SAP</h2>
      <div class="sp"></div>${mp.pass ? '<span class="badge b-ok">&#10003; ครบทุกจุด</span>'
      : `<span class="badge b-fail">&#10007; ไม่พบข้อมูล ${mp.errors.length} จุด</span>`}</div>
    <div class="card-b">${cards}
      <p class="sec-title" style="margin-top:20px">${nMat}. MATERIAL &amp; ${nMat + 1}. RELATE UNIT (รายบรรทัด)</p>
      ${matCards}</div></div>`;
}

function qtyTxt(n) { return num(n).toLocaleString('en-US', { maximumFractionDigits: 3 }); }

function uomCell(mp, i, l, posted) {
  if (!mp) return '<span class="badge b-idle">รอ Mapping</span>';
  const r = mp.lines[i], u = r.uom || {};
  if (!r.code) return '<span class="hint">—</span>';
  if (u.status === 'fail')
    return `<span class="badge b-fail">&#10007; ไม่มีกฎแปลงหน่วย</span>
      ${posted ? '' : `<div style="margin-top:6px"><button class="btn sm" onclick="addUomRule(${i})">&#43; เพิ่มกฎ</button></div>`}`;
  if (u.status === 'convert')
    return `<b>${qtyTxt(u.sapQty)} ${esc(u.sapUom)}</b>
      <div class="sub"><span class="badge b-warn">&#8646; &times;${u.factor}</span></div>
      <div class="hint" style="margin-top:2px">${esc(u.method)}</div>`;
  return `<b>${qtyTxt(u.sapQty)} ${esc(u.sapUom)}</b><div class="hint">${esc(u.method)}</div>`;
}

/* เพิ่มกฎแปลงหน่วยจากหน้าเอกสารได้ทันที */
async function addUomRule(i) {
  const l = S.doc.lines[i], code = S.map.lines[i].code, md = await masters();
  const mat = md.materials.find(m => m.MaterialCode === code) || {};
  S.masterGroup = 'material'; S.masterTab = 'uoms';
  editRow(null, { MaterialCode: code, ExtUom: l.uom || '', SapUom: mat.Uom || '',
                  Note: 'เพิ่มจากเอกสาร #' + S.doc.docId });
  window._afterUomSave = true;
}

/* ---------- เพิ่มข้อมูลหลักใหม่จากการ์ด Mapping ที่ "ไม่พบข้อมูล" ---------- */
/* prefill ค่าจากเอกสารให้อัตโนมัติ + ตรวจข้อมูลซ้ำก่อนบันทึกเสมอ (findDupes) */
async function quickAddCustomer() {
  await masters();
  const h = S.doc.header;
  const dupes = findDupes('customers', h.customerName, h.customerTaxId);
  S.masterGroup = 'customer'; S.masterTab = 'customers';
  window._quickAddResolve = code => setManualHeader('customer', code);
  editRow(null, { NameTh: h.customerName || '', TaxId: h.customerTaxId || '', Currency: h.currency || 'THB' }, dupes);
}

async function quickAddVendor() {
  await masters();
  const h = S.doc.header;
  const dupes = findDupes('vendors', h.vendorName, h.vendorTaxId);
  S.masterGroup = 'vendor'; S.masterTab = 'vendors';
  window._quickAddResolve = code => setManualHeader('vendor', code);
  editRow(null, { VendorName: h.vendorName || '', TaxId: h.vendorTaxId || '', Currency: h.currency || 'THB' }, dupes);
}

async function quickAddShipTo() {
  const custCode = S.map && S.map.header.customer.code;
  if (!custCode) { toast('&#9888; กรุณาระบุลูกค้าให้ได้ก่อน'); return; }
  await masters();
  const h = S.doc.header;
  const dupes = findDupes('shiptos', h.shipToName, null, x => x.CustomerCode === custCode);
  S.masterGroup = 'shipto'; S.masterTab = 'shiptos';
  window._quickAddResolve = code => setManualHeader('shipTo', code);
  editRow(null, { CustomerCode: custCode, ShipToName: h.shipToName || '', Address: h.shipToAddress || '' }, dupes);
}

async function quickAddMaterial(i) {
  await masters();
  const l = S.doc.lines[i];
  const dupes = findDupes('materials', l.desc, null);
  S.masterGroup = 'material'; S.masterTab = 'materials';
  window._quickAddResolve = async code => {
    S.manual.lines[i] = code;
    await API.post('/api/documents/' + S.doc.docId + '/learn',
      { partnerCode: S.doc.partnerCode, extCode: l.extCode, extDesc: l.desc, materialCode: code });
    await masters(true); await runMap(true);
  };
  editRow(null, { Description: l.desc || '', Uom: l.uom || '', Plant: '1000' }, dupes);
}

function optionsFor(kind, md) {
  if (kind === 'customers') return md.customers.map(c => ({ v: c.CustomerCode, t: c.CustomerCode + ' — ' + c.NameTh }));
  if (kind === 'vendors') return md.vendors.map(c => ({ v: c.VendorCode, t: c.VendorCode + ' — ' + c.VendorName }));
  if (kind === 'shiptos') {
    const cc = S.map && S.map.header.customer ? S.map.header.customer.code : '';
    return md.shiptos.filter(s => !cc || s.CustomerCode === cc).map(s => ({ v: s.ShipToCode, t: s.ShipToCode + ' — ' + s.ShipToName }));
  }
  return [];
}

/* ------- local edits (จะถูกส่งขึ้น server ตอนกด Mapping / ส่ง SAP) ------- */
function editHeader(k, v) { S.doc.header[k] = v; S.map = null; markDirty(); }
function editLine(i, k, v) {
  S.doc.lines[i][k] = v;
  if (k === 'qty' || k === 'price') S.doc.lines[i].amount = (num(S.doc.lines[i].qty) * num(S.doc.lines[i].price)).toFixed(2);
  S.map = null; markDirty();
}

/* ---------- ข้อมูล PO ระดับรายการ (Non-Trade มี PO) — ผู้ใช้กรอกเอง ไม่ได้เดาจาก OCR ---------- */
function lineExtraCount(l) {
  const ex = l.extra || {};
  return PO_LINE_EXTRA_FIELDS.filter(f => (ex[f[0]] || '') !== '').length;
}
function editLineExtra(i, k, v) {
  const l = S.doc.lines[i];
  l.extra = { ...(l.extra || {}), [k]: v };
  markDirty();
}
function showLineExtra(i) {
  const l = S.doc.lines[i];
  const ex = l.extra || {};
  const aaGuide = AA_GUIDE[ex.accountAssignment || ''] || AA_GUIDE[''];
  const fields = PO_LINE_EXTRA_FIELDS.map(([k, label, type, opts]) => {
    const isRelevant = k === aaGuide.field;
    const wrapStyle = isRelevant ? 'border:1.5px solid var(--brand);border-radius:var(--r3);padding:8px' : '';
    return `<div class="f" style="${wrapStyle}"><label>${esc(label)}${isRelevant ? ' <span style="color:var(--brand)">&#9733; แนะนำให้กรอก</span>' : ''}</label>
      ${type === 'select'
        ? `<select ${k === 'accountAssignment' ? `onchange="editLineExtra(${i},'${k}',this.value);showLineExtra(${i})"` : `onchange="editLineExtra(${i},'${k}',this.value)"`}>
             ${opts.map(([v, t]) => `<option value="${esc(v)}" ${v === (ex[k] || '') ? 'selected' : ''}>${esc(t)}</option>`).join('')}
           </select>`
        : `<input type="text" value="${esc(ex[k] || '')}" oninput="editLineExtra(${i},'${k}',this.value)">`}
    </div>`;
  }).join('');
  openModal(`<div class="card-h"><h2>&#128203; ข้อมูล PO เพิ่มเติม — รายการ ${l.itemNo} (${esc(l.desc || '')})</h2>
      <div class="sp"></div><button class="btn sm" onclick="closeModal();renderWork()">&#10005;</button></div>
    <div class="card-b">
      <p class="hint" style="margin:-4px 0 16px;padding:8px 12px;background:var(--line-soft);border-radius:var(--r2)">&#128161; ${esc(aaGuide.hint)}</p>
      <div class="grid">${fields}</div>
      <div class="row" style="margin-top:16px;justify-content:flex-end">
        <button class="btn primary" onclick="closeModal();renderWork()">เสร็จสิ้น</button>
      </div>
    </div>`, 'wide');
}
function addLine() {
  S.doc.lines.push({ itemNo: (S.doc.lines.length + 1) * 10, extCode: '', desc: '', qty: 0, uom: 'EA', price: 0, amount: 0, materialCode: '' });
  S.map = null; renderWork();
}
function delLine(i) { S.doc.lines.splice(i, 1); S.map = null; renderWork(); }

/* ---------- G/L Account Items — เฉพาะเอกสารประเภท Trade (Supplier Invoice/MIRO) เก็บใน header.glItems ---------- */
function addGlItem() {
  if (!S.doc.header.glItems) S.doc.header.glItems = [];
  S.doc.header.glItems.push({ glAccount: '', drCr: '', amount: 0, taxCode: '', assignment: '', itemText: '', costCenter: '' });
  markDirty(); renderWork();
}
function delGlItem(i) { S.doc.header.glItems.splice(i, 1); markDirty(); renderWork(); }
function editGlItem(i, k, v) { S.doc.header.glItems[i][k] = v; markDirty(); }
let _dirtyTimer = null;
function markDirty() {
  clearTimeout(_dirtyTimer);
  _dirtyTimer = setTimeout(() => {
    const btn = document.querySelector('.btn.success'); if (btn) btn.disabled = true;
  }, 10);
}

async function runMap(silent) {
  await guard(async () => {
    const res = await API.post('/api/documents/' + S.doc.docId + '/map',
      { header: S.doc.header, lines: S.doc.lines, manual: S.manual, user: S.user });
    S.doc = res.document; S.map = res;
    await renderWork();
    if (!silent) {
      toast(res.pass ? '&#10003; Mapping ผ่าน — บันทึกลงฐานข้อมูลแล้ว'
        : '&#10007; Mapping ไม่ผ่าน — ไม่พบข้อมูล ' + res.errors.length + ' จุด');
      window.scrollTo({ top: 0, behavior: 'smooth' });
    }
  });
}
function setManualHeader(k, v) { S.manual.header[k] = v; if (k === 'customer') S.manual.header.shipTo = ''; runMap(true); }
function setManualLine(i, v) { S.manual.lines[i] = v; runMap(true); }

async function learn(i) {
  const l = S.doc.lines[i], code = S.map.lines[i].code;
  await guard(async () => {
    await API.post('/api/documents/' + S.doc.docId + '/learn',
      { partnerCode: S.doc.partnerCode, extCode: l.extCode, extDesc: l.desc, materialCode: code });
    await masters(true);
    toast('&#10003; บันทึกลง Master Mapping แล้ว — ครั้งถัดไประบบจะจับคู่อัตโนมัติ');
    await runMap(true);
  });
}

async function showPayload() {
  await guard(async () => {
    const r = await API.get('/api/documents/' + S.doc.docId + '/payload');
    openModal(`<div class="card-h"><h2>Payload ที่จะส่งเข้า SAP</h2><div class="sp"></div>
      <button class="btn sm" onclick="closeModal()">&#10005;</button></div>
      <div class="card-b"><p class="hint">Endpoint: <code>${esc(r.payload._target)}</code></p>
      <pre class="json">${esc(JSON.stringify(r.payload, null, 2))}</pre></div>`);
  });
}

function postSAP() {
  const d = S.doc, mp = S.map;
  openModal(`<div class="card-h"><h2>ยืนยันการส่งเข้า SAP S/4HANA</h2></div><div class="card-b">
    <p>ระบบจะสร้างเอกสาร <b>${esc(moduleLabel(d.module))}</b> ในระบบ SAP</p>
    <div class="tw"><table style="min-width:auto"><tbody>
      ${d.module === 'SO'
      ? `<tr><th>Sold-to</th><td>${esc(mp.header.customer.code)} — ${esc(mp.header.customer.text)}</td></tr>
           <tr><th>Ship-to</th><td>${esc(mp.header.shipTo.code)} — ${esc(mp.header.shipTo.text)}</td></tr>
           <tr><th>PO ลูกค้า</th><td>${esc(d.header.poNo || '')}</td></tr>`
      : `<tr><th>Vendor</th><td>${esc(mp.header.vendor.code)} — ${esc(mp.header.vendor.text)}</td></tr>
           <tr><th>เลขที่ใบแจ้งหนี้</th><td>${esc(d.header.invoiceNo || '')}</td></tr>`}
      <tr><th>จำนวนรายการ</th><td>${d.lines.length} บรรทัด</td></tr>
      <tr><th>ยอดรวม</th><td><b>${fmt(d.header.totalAmount)} ${esc(d.header.currency || 'THB')}</b></td></tr>
    </tbody></table></div>
    <div class="row" style="margin-top:18px"><button class="btn success" onclick="confirmPost(this)">ยืนยันส่งเข้า SAP</button>
    <button class="btn" onclick="closeModal()">ยกเลิก</button></div></div>`);
}
async function confirmPost(btn) {
  btn.disabled = true; btn.textContent = 'กำลังส่ง…';
  try {
    const r = await API.post('/api/documents/' + S.doc.docId + '/post', { user: S.user });
    S.doc = r.document; closeModal(); await renderWork();
    toast((r.simulated ? '&#10003; (โหมดจำลอง) ' : '&#10003; ') + 'สร้างเอกสารใน SAP สำเร็จ — เลขที่ ' + r.sapDocNo, 5000);
    window.scrollTo({ top: 0, behavior: 'smooth' });
  } catch (e) { toast('&#9888; ' + esc(e.message)); btn.disabled = false; btn.textContent = 'ยืนยันส่งเข้า SAP'; }
}
function resetDoc() { S.doc = null; S.map = null; S.manual = { header: {}, lines: {} }; S.chatHistory = []; S.chatImage = null; S.uploadApDocCategory = ''; go(S.module ? 'work' : 'home'); }

/* ---------------------------------------------------------------- INBOX */
async function renderInbox() {
  $('#content').innerHTML = '<div class="card"><div class="card-b"><div class="empty">กำลังโหลด…</div></div></div>';
  if (S.module === 'AP') await apDocCategories(); else S.inboxApDocCategory = '';
  // ทะเบียนเอกสารแยกตามโมดูลที่เข้าจากเมนูซ้าย (AP INVOICE / SALES ORDER) — กรองด้วย S.module
  // ถ้าเข้าจากปุ่ม "ดูทั้งหมด" บนหน้าภาพรวม (S.module ว่าง) จะแสดงทุกโมดูลรวมกัน
  const catQs = (S.module === 'AP' && S.inboxApDocCategory) ? '&apDocCategory=' + S.inboxApDocCategory : '';
  S.inboxList = await API.get('/api/documents?limit=5000' + (S.module ? '&module=' + S.module : '') + catQs);
  renderInboxLocal();
}
function renderInboxLocal() {
  const q = (S.inboxSearch || '').trim().toLowerCase();
  let list = !q ? S.inboxList : S.inboxList.filter(r =>
    String(r.DocNo || '').toLowerCase().includes(q) ||
    String(r.PartnerName || '').toLowerCase().includes(q));
  list = list.filter(r => inDateRange(r.DocDate, 'inboxDateFrom', 'inboxDateTo'));
  const title = `${esc(inboxTitle(S.module))} (${list.length})`;
  const catFilter = S.module === 'AP' ? `<select onchange="S.inboxApDocCategory=this.value;S.inboxPage=1;renderInbox()" style="margin-right:8px">
      <option value="">ทุกประเภทเอกสาร</option>
      ${(S.apDocCategories || []).map(c => `<option value="${esc(c.id)}" ${c.id === S.inboxApDocCategory ? 'selected' : ''}>${esc(c.label)}</option>`).join('')}
    </select>` : '';
  const { pageRows, totalPages, pageSize, total } = paginate(list, 'inboxPage', 'inboxPageSize');
  $('#content').innerHTML = `
  <div class="card"><div class="card-h"><h2>${title}</h2><div class="sp"></div>
    <input id="inboxSearch" type="text" placeholder="ค้นหา คู่ค้า / เลขที่เอกสาร…" value="${esc(S.inboxSearch)}"
           oninput="S.inboxSearch=this.value;S.inboxPage=1;renderInboxLocal()" style="max-width:220px;margin-right:8px">
    <span class="hint" style="margin-right:4px">วันที่:</span>${dateRangeHtml('inboxDateFrom', 'inboxDateTo', "S.inboxPage=1;renderInboxLocal")}
    <div style="margin-right:8px"></div>
    ${catFilter}<button class="btn sm" onclick="renderInbox()">&#8635; รีเฟรช</button></div>
  <div class="card-b"><div class="tw"><table>
    <thead><tr><th>#</th>${!S.module ? '<th>Module</th>' : ''}<th>File</th><th>${['AP', 'II', 'PODP'].includes(S.module) ? 'Invoice Number' : 'PO Number'}</th><th>PO Date</th><th>Supplier</th>
      <th style="text-align:right">Total</th>${S.module === 'AP' ? '<th>Document Type</th>' : ''}<th>Status</th><th>Model OCR</th><th>SAP Doc</th><th>Create Date</th><th></th></tr></thead>
    <tbody>${pageRows.map(r => `<tr>
      <td>${r.DocId}</td>${!S.module ? `<td><span class="badge ${r.Module === 'AP' ? 'b-warn' : r.Module === 'II' ? 'b-idle' : 'b-ok'}">${r.Module}</span></td>` : ''}
      <td>${esc(r.FileName || '')}</td><td>${esc(r.DocNo || '')}</td><td>${esc(r.DocDate || '')}</td>
      <td>${esc(r.PartnerName || '')}</td><td style="text-align:right">${fmt(r.TotalAmount)}</td>
      ${S.module === 'AP' ? `<td>${r.ApDocCategory ? esc(apDocCategoryLabel(r.ApDocCategory)) : '<span class="hint">—</span>'}</td>` : ''}
      <td ${r.OcrConfidenceNote || r.OcrTokensIn != null ? `title="${esc([r.OcrConfidenceNote,
          r.OcrTokensIn != null ? `Token: ${r.OcrTokensIn} in / ${r.OcrTokensOut} out` : '',
          r.OcrCost != null ? `ค่าใช้จ่าย: ${r.OcrInputCost} in + ${r.OcrOutputCost} out = ${r.OcrCost} ${r.OcrCostCurrency || ''}` : ''].filter(Boolean).join(' — '))}"` : ''}>${statusBadge(r.Status)}
        ${r.OcrConfidence != null ? `<span class="hint">${Math.round(r.OcrConfidence * 100)}%</span>` : ''}</td>
      <td>${providerBadge(r.OcrProvider)}</td>
      <td>${esc(r.SapDocNo || '')}</td><td class="hint">${dt(r.CreatedAt)}</td>
      <td style="white-space:nowrap"><button class="btn sm" onclick="openDoc(${r.DocId})">เปิด</button>
        ${r.Status === 'POSTED' ? '' : `<button class="btn sm ghost" onclick="delDoc(${r.DocId})">&#10005;</button>`}</td></tr>`).join('')
      || `<tr><td colspan="${11 + (!S.module ? 1 : 0) + (S.module === 'AP' ? 1 : 0)}" class="empty">ยังไม่มีเอกสารในระบบ</td></tr>`}
    </tbody></table></div>
    ${pagerHtml('inboxPage', 'inboxPageSize', totalPages, total, pageSize, 'renderInboxLocal')}
  </div></div>`;
  // ค้นหาแบบ re-render ทั้ง card ทุกครั้งที่พิมพ์ ทำให้ input เดิมถูกแทนที่ด้วย element ใหม่และเสีย focus —
  // ต้องคืน focus + ตำแหน่ง cursor ให้กล่องค้นหาเองหลัง render เสร็จ
  const si = $('#inboxSearch');
  if (si && document.activeElement !== si) {
    si.focus();
    si.setSelectionRange(si.value.length, si.value.length);
  }
}
async function delDoc(id) {
  if (!confirm('ลบเอกสาร #' + id + ' ?')) return;
  await guard(async () => { await API.del('/api/documents/' + id + '?user=' + encodeURIComponent(S.user)); toast('ลบเอกสารแล้ว'); renderInbox(); });
}

/* ---------------------------------------------------------------- MASTER */
async function renderMaster() {
  await masters(true);
  renderMasterLocal();
}
function renderMasterLocal() {
  const md = S.masters;
  const grp = MASTER_GROUPS.find(g => g.key === S.masterGroup) || MASTER_GROUPS[0];
  if (!grp.tabs.includes(S.masterTab)) S.masterTab = grp.tabs[0];

  const count = k => (md[k] || []).length;
  const groupTabs = MASTER_GROUPS.map(g => {
    const n = g.tabs.reduce((a, k) => a + count(k), 0);
    const tag = g.mod === 'ALL' ? '' : ` <span class="badge b-idle">${g.mod}</span>`;
    return `<button class="${g.key === grp.key ? 'on' : ''}" onclick="setGroup('${g.key}')">${g.label}${tag}
      <span class="muted">(${n})</span></button>`;
  }).join('');

  const subTabs = grp.tabs.length < 2 ? '' :
    `<div class="row" style="gap:8px;margin-bottom:16px">` + grp.tabs.map(k =>
      `<button class="btn sm ${S.masterTab === k ? 'primary' : ''}" onclick="S.masterTab='${k}';S.masterSearch='';renderMasterLocal()">
         ${MASTER_DEF[k].label} <span style="opacity:.7">${count(k)}</span></button>`).join('') + `</div>`;

  const def = MASTER_DEF[S.masterTab];
  const q = (S.masterSearch || '').trim().toLowerCase();
  const rows = !q ? md[S.masterTab] : md[S.masterTab].filter(r =>
    def.cols.some(c => String(r[c.k] ?? '').toLowerCase().includes(q)));
  const cell = (r, c) => {
    if (c.sap) return r[c.k]
      ? `<b class="sapcode">${esc(r[c.k])}</b>`
      : '<span class="badge b-fail">&#10007; ยังไม่ระบุ</span>';
    if (S.masterTab === 'uoms' && c.k === 'MaterialCode' && !r[c.k])
      return '<span class="badge b-idle">ทุกสินค้า (กฎกลาง)</span>';
    if (S.masterTab === 'uoms' && c.k === 'Factor')
      return `<b>${num(r[c.k]).toLocaleString('en-US', { maximumFractionDigits: 6 })}</b>`;
    return esc(r[c.k]);
  };
  const body = rows.map(r => '<tr>' + def.cols.map(c => `<td>${cell(r, c)}</td>`).join('') +
    `<td style="white-space:nowrap"><button class="btn sm" onclick='editRow(${JSON.stringify(r[def.key])})'>แก้ไข</button>
     <button class="btn sm ghost" onclick='delRow(${JSON.stringify(r[def.key])})'>ลบ</button></td></tr>`).join('');

  $('#content').innerHTML = `
  <div class="card"><div class="card-h"><h2>Master Mapping — แยกตามจุดที่ต้องจับคู่</h2><div class="sp"></div>
    <span class="hint">SQL Server · schema <code>ocr</code></span></div>
    <div class="tabs">${groupTabs}</div>
    <div class="card-b">
      <div class="result ok" style="margin-bottom:16px;padding:12px 16px">
        <b>${grp.label}</b> — <span style="font-weight:400">${grp.note}</span></div>
      ${subTabs}
      <div class="hint" style="margin-bottom:12px">&#128273; คอลัมน์ <b class="sapcode">รหัสใน SAP</b>
        คือค่าที่ระบบใช้ยิงเข้า S/4HANA จริง — ถ้าเว้นว่างจะ Mapping ไม่ผ่านและส่งเอกสารไม่ได้</div>
      <div class="row" style="margin-bottom:14px">
        <button class="btn primary sm" onclick="editRow(null)">&#43; เพิ่มข้อมูล</button>
        <input id="masterSearch" type="text" placeholder="ค้นหา..." value="${esc(S.masterSearch)}"
               oninput="S.masterSearch=this.value;renderMasterLocal()" style="max-width:220px">
        <span class="hint">${MASTER_NOTE[S.masterTab]}</span>
      </div>
      <div class="tw"><table><thead><tr>${def.cols.map(c => `<th>${esc(c.l)}</th>`).join('')}<th style="width:130px"></th></tr></thead>
      <tbody>${body || `<tr><td colspan="${def.cols.length + 1}" class="empty">${q ? 'ไม่พบข้อมูลที่ค้นหา' : 'ยังไม่มีข้อมูล'}</td></tr>`}</tbody></table></div>
    </div></div>`;
  // ค้นหาแบบ re-render ทั้ง card ทุกครั้งที่พิมพ์ ทำให้ input เดิมถูกแทนที่ด้วย element ใหม่และเสีย focus —
  // ต้องคืน focus + ตำแหน่ง cursor ให้กล่องค้นหาเองหลัง render เสร็จ (เหมือน inboxSearch)
  const ms = $('#masterSearch');
  if (ms && document.activeElement !== ms) {
    ms.focus();
    ms.setSelectionRange(ms.value.length, ms.value.length);
  }
}
function setGroup(k) {
  S.masterGroup = k;
  S.masterTab = (MASTER_GROUPS.find(g => g.key === k) || MASTER_GROUPS[0]).tabs[0];
  S.masterSearch = '';
  renderMasterLocal();
}

function editRow(key, prefill, dupes) {
  const def = MASTER_DEF[S.masterTab], md = S.masters;
  const r = key == null ? (prefill || {}) : md[S.masterTab].find(x => String(x[def.key]) === String(key)) || {};
  const fields = def.cols.map(c => {
    if (c.ref) {
      const src = md[c.ref], vk = MASTER_DEF[c.ref].key, lk = M_LABEL[c.ref];
      return `<div class="f"><label>${esc(c.l)}</label><select id="fld_${c.k}">
        ${c.blank ? `<option value="" ${!r[c.k] ? 'selected' : ''}>— ทุกสินค้า (กฎกลาง) —</option>` : ''}
        ${src.map(o => `<option value="${esc(o[vk])}" ${r[c.k] === o[vk] ? 'selected' : ''}>${esc(o[vk])} — ${esc(o[lk])}</option>`).join('')}
      </select></div>`;
    }
    return `<div class="f"><label>${esc(c.l)}</label><input id="fld_${c.k}" value="${esc(r[c.k] == null ? '' : r[c.k])}"></div>`;
  }).join('');
  const dupeBox = (dupes && dupes.length) ? `<div class="result bad" style="margin-bottom:16px">
      <h3>&#9888; พบข้อมูลที่อาจซ้ำกัน ${dupes.length} รายการ</h3>
      <p class="hint" style="margin:0 0 10px">ตรวจสอบก่อนเพิ่มใหม่ — ถ้าเป็นรายการเดียวกัน ให้กด "ใช้รายการนี้แทน" แทนการสร้างซ้ำ</p>
      <div class="tw"><table style="min-width:auto"><tbody>
      ${dupes.map(x => `<tr><td><b>${esc(x.row[def.key])}</b> — ${esc(x.row[M_LABEL[S.masterTab]] || '')}</td>
        <td><span class="badge b-warn">${esc(x.reason)}</span></td>
        <td><button class="btn sm primary" onclick='useDupe(${JSON.stringify(String(x.row[def.key]))})'>ใช้รายการนี้แทน</button></td></tr>`).join('')}
      </tbody></table></div></div>` : '';
  openModal(`<div class="card-h"><h2>${key == null ? 'เพิ่ม' : 'แก้ไข'} — ${def.label}</h2><div class="sp"></div>
    <button class="btn sm" onclick="closeModal()">&#10005;</button></div>
    <div class="card-b">${dupeBox}<div class="grid">${fields}</div>
    <div class="row" style="margin-top:18px">
    <button class="btn primary" onclick='saveRow(${JSON.stringify(key)})'>${dupes && dupes.length ? 'ยืนยัน — บันทึกเป็นรายการใหม่' : 'บันทึก'}</button>
    <button class="btn" onclick="closeModal()">ยกเลิก</button></div></div>`);
}
/* เลือกใช้รายการที่มีอยู่แล้วแทนการสร้างใหม่ (จากกล่องเตือนข้อมูลซ้ำ) */
function useDupe(code) {
  closeModal();
  if (window._quickAddResolve) { const fn = window._quickAddResolve; window._quickAddResolve = null; fn(code); }
}
async function saveRow(key) {
  const def = MASTER_DEF[S.masterTab], o = {};
  def.cols.forEach(c => { const el = document.getElementById('fld_' + c.k); if (el) o[c.k] = el.value.trim(); });
  if (key == null && def.key !== 'Id' && !o[def.key]) { toast('กรุณากรอก ' + def.cols.find(c => c.k === def.key).l); return; }
  await guard(async () => {
    if (key == null) await API.post('/api/masters/' + S.masterTab, o);
    else await API.put('/api/masters/' + S.masterTab + '/' + encodeURIComponent(key), o);
    closeModal(); await masters(true);
    if (window._quickAddResolve) {                 // เพิ่มข้อมูลจากการ์ด Mapping -> ผูกกับเอกสารทันที
      const fn = window._quickAddResolve; window._quickAddResolve = null;
      await fn(o[def.key]);
      toast('&#10003; เพิ่มข้อมูลใหม่แล้ว — กำลัง Mapping ใหม่'); return;
    }
    if (window._afterUomSave && S.doc) {          // บันทึกกฎจากหน้าเอกสาร -> กลับไป map ต่อ
      window._afterUomSave = false;
      toast('&#10003; เพิ่มกฎแปลงหน่วยแล้ว — กำลัง Mapping ใหม่');
      await runMap(true); return;
    }
    await renderMaster(); toast('&#10003; บันทึกข้อมูลหลักเรียบร้อย');
  });
}
async function delRow(key) {
  if (!confirm('ลบข้อมูลนี้?')) return;
  await guard(async () => {
    await API.del('/api/masters/' + S.masterTab + '/' + encodeURIComponent(key));
    await renderMaster(); toast('ลบข้อมูลแล้ว');
  });
}

/* ---------------------------------------------------------------- LOG */
async function renderLog() {
  $('#content').innerHTML = '<div class="card"><div class="card-b"><div class="empty">กำลังโหลด…</div></div></div>';
  S.logsList = await API.get('/api/logs?limit=5000');
  renderLogLocal();
}
function renderLogLocal() {
  const list = S.logsList.filter(l => inDateRange(l.PostedAt, 'logDateFrom', 'logDateTo'));
  const { pageRows, totalPages, pageSize, total } = paginate(list, 'logPage', 'logPageSize');
  $('#content').innerHTML = `
  <div class="card"><div class="card-h"><h2>ประวัติการส่งเข้า SAP (${list.length})</h2><div class="sp"></div>
    <span class="hint" style="margin-right:4px">วันที่:</span>${dateRangeHtml('logDateFrom', 'logDateTo', "S.logPage=1;renderLogLocal")}
    <div style="margin-right:8px"></div>
    <button class="btn sm" onclick="renderLog()">&#8635; รีเฟรช</button></div>
  <div class="card-b"><div class="tw"><table>
    <thead><tr><th>เวลา</th><th>Module</th><th>SAP Doc</th><th>เอกสารอ้างอิง</th><th>คู่ค้า</th>
      <th style="text-align:right">ยอดรวม</th><th>รายการ</th><th>ผล</th><th>Model</th><th>ไฟล์</th><th></th></tr></thead>
    <tbody>${pageRows.map(l => `<tr>
      <td class="hint">${dt(l.PostedAt)}</td><td><span class="badge ${l.Module === 'AP' ? 'b-warn' : l.Module === 'II' ? 'b-idle' : 'b-ok'}">${esc(l.Module)}</span></td>
      <td><b>${esc(l.SapDocNo || '-')}</b></td><td>${esc(l.DocNo || '')}</td><td>${esc(l.PartnerName || '')}</td>
      <td style="text-align:right">${fmt(l.TotalAmount)}</td><td style="text-align:center">${l.Lines || 0}</td>
      <td>${l.Success ? '<span class="badge b-ok">&#10003; SAP Connected Successfully</span>' : '<span class="badge b-fail">&#10007; Unable to Connect to SAP</span>'}</td>
      <td>${providerBadge(l.OcrProvider)}</td>
      <td>${esc(l.FileName || '')}</td>
      <td><button class="btn sm" onclick="showLogPayload(${l.LogId})">Payload</button></td></tr>`).join('')
      || '<tr><td colspan="11" class="empty">ยังไม่มีเอกสารที่ส่งเข้า SAP</td></tr>'}
    </tbody></table></div>
    ${pagerHtml('logPage', 'logPageSize', totalPages, total, pageSize, 'renderLogLocal')}
  </div></div>`;
}
async function showLogPayload(id) {
  await guard(async () => {
    const p = await API.get('/api/logs/' + id + '/payload');
    openModal(`<div class="card-h"><h2>Payload (Log #${id})</h2><div class="sp"></div>
      <button class="btn sm" onclick="closeModal()">&#10005;</button></div>
      <div class="card-b"><pre class="json">${esc(JSON.stringify(p, null, 2))}</pre></div>`);
  });
}

/* ---------------------------------------------------------------- audit log (รวมทุก module — filter ในหน้าเดียว) */
const AUDIT_ACTION_BADGE = {
  CREATE: ['b-ok', 'เพิ่มเอกสาร'], UPDATE: ['b-warn', 'แก้ไข'], DELETE: ['b-fail', 'ลบเอกสาร'], REOCR: ['b-idle', 'อ่าน OCR ใหม่']
};
const AUDIT_MODULE_FILTERS = [['', 'ทั้งหมด'], ['AP', 'Supplier Invoice'], ['PODP', 'PO Down Payment'], ['II', 'Incoming Invoice'], ['SO', 'Sales Order']];
async function renderAuditLog() {
  $('#content').innerHTML = '<div class="card"><div class="card-b"><div class="empty">กำลังโหลด…</div></div></div>';
  S.auditLogList = await API.get('/api/audit-logs?limit=5000');
  renderAuditLogLocal();
}
function renderAuditLogLocal() {
  const mod = S.auditLogModule || '';
  let list = !mod ? S.auditLogList : S.auditLogList.filter(l => l.Module === mod);
  list = list.filter(l => inDateRange(l.CreatedAt, 'auditLogDateFrom', 'auditLogDateTo'));
  const filterBtns = AUDIT_MODULE_FILTERS.map(([v, l]) =>
    `<button class="btn sm ${v === mod ? 'primary' : 'ghost'}" onclick="S.auditLogModule='${v}';S.auditLogPage=1;renderAuditLogLocal()">${l}</button>`).join('');
  const { pageRows, totalPages, pageSize, total } = paginate(list, 'auditLogPage', 'auditLogPageSize');
  $('#content').innerHTML = `
  <div class="card"><div class="card-h"><h2>Log กิจกรรม (${list.length})</h2><div class="sp"></div>
    <span class="hint" style="margin-right:4px">วันที่:</span>${dateRangeHtml('auditLogDateFrom', 'auditLogDateTo', "S.auditLogPage=1;renderAuditLogLocal")}
    <div style="margin-right:8px"></div>
    <button class="btn sm" onclick="renderAuditLog()">&#8635; รีเฟรช</button></div>
  <div class="card-b">
    <div class="row" style="gap:6px;margin-bottom:16px;flex-wrap:wrap">${filterBtns}</div>
    <div class="tw"><table>
    <thead><tr><th>เวลา</th><th>Module</th><th>Process</th><th>เอกสาร</th><th>รายละเอียด</th><th>Model</th><th>ทำโดย</th></tr></thead>
    <tbody>${pageRows.map(l => {
    const b = AUDIT_ACTION_BADGE[l.Action] || ['b-idle', l.Action];
    return `<tr><td class="hint">${dt(l.CreatedAt)}</td>
      <td><span class="badge ${l.Module === 'AP' ? 'b-warn' : l.Module === 'II' ? 'b-idle' : 'b-ok'}">${esc(moduleLabel(l.Module))}</span></td>
      <td><span class="badge ${b[0]}">${b[1]}</span></td>
      <td>#${l.DocId ?? '-'}${l.DocNo ? ' &middot; ' + esc(l.DocNo) : ''}${l.FileName ? '<div class="hint">' + esc(l.FileName) + '</div>' : ''}</td>
      <td>${esc(l.Detail || '')}</td>
      <td>${providerBadge(l.OcrProvider)}</td>
      <td>${esc(l.PerformedBy || '')}</td></tr>`;
  }).join('') || '<tr><td colspan="7" class="empty">ยังไม่มีประวัติ</td></tr>'}
    </tbody></table></div>
    ${pagerHtml('auditLogPage', 'auditLogPageSize', totalPages, total, pageSize, 'renderAuditLogLocal')}
  </div></div>`;
}

/* ---------------------------------------------------------------- boot */
(async function boot() {
  try {
    const h = await API.get('/api/health');
    S.health = h;
    $('#foot').innerHTML = `DB: <b style="color:var(--brand)">● ${esc(h.db.db)}</b><br>
      ${esc(h.db.srv)}<br>SAP: ${h.sapMode === 'live' ? 'เชื่อมต่อจริง' : 'โหมดจำลอง'} · OCR: ${esc(h.ocrProvider)}<br>
      <span class="hint">${esc(S.user)}</span>`;
  } catch (e) {
    $('#foot').innerHTML = '<span style="color:var(--red)">● เชื่อมต่อฐานข้อมูลไม่ได้</span>';
  }
  go('home');
})();
