/* =====================================================================
   MGT Document OCR -> SAP S/4HANA  (frontend)
   ทุกข้อมูลอ่าน/เขียนผ่าน REST API ที่ต่อกับ SQL Server: MGT_Document_OCR
   ===================================================================== */
const S = {
  page: 'home', module: null, doc: null, map: null, manual: { header: {}, lines: {} },
  masters: null, masterGroup: 'vendor', masterTab: 'vendors', busy: false, inbox: [], logs: [], health: null,
  user: 'it-digital@megachem.co.th', ocrProviders: null, ocrProvider: 'auto', chatHistory: [], chatImage: null,
  apDocCategories: null, inboxApDocCategory: '', uploadApDocCategory: '', inboxSearch: ''
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
    S.doc = await API.post(`/api/documents/${S.doc.docId}/category`, { apDocCategory: v });
  });
}

/* ---------------------------------------------------------------- utils */
const $ = s => document.querySelector(s);
const esc = s => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const num = v => { const n = parseFloat(String(v == null ? '' : v).replace(/[, ]/g, '')); return isNaN(n) ? 0 : n; };
const fmt = n => num(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const fmtCost = n => num(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 });
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

/* ---------------------------------------------------------------- api */
async function api(method, url, body, isForm) {
  const opt = { method, headers: {} };
  if (body !== undefined) {
    if (isForm) opt.body = body;
    else { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
  }
  const r = await fetch(url, opt);
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
['paymentTerms', 'เงื่อนไขชำระเงิน'], ['incoterms', 'Incoterms'], ['subTotal', 'มูลค่าก่อนภาษี'],
['vatAmount', 'ภาษีมูลค่าเพิ่ม'], ['totalAmount', 'ยอดรวมทั้งสิ้น'], ['remark', 'หมายเหตุ']];
const AP_H = [['docType', 'ประเภทเอกสาร'], ['invoiceNo', 'เลขที่ใบแจ้งหนี้'], ['invoiceDate', 'วันที่ใบแจ้งหนี้'],
['postingDate', 'วันที่ผ่านรายการ'], ['vendorName', 'ชื่อผู้ขาย'], ['vendorTaxId', 'เลขทะเบียนนิติบุคคล'],
['branch', 'สาขา'], ['poRef', 'อ้างอิง PO'], ['currency', 'สกุลเงิน'], ['paymentTerms', 'เงื่อนไขชำระเงิน'],
['subTotal', 'มูลค่าก่อนภาษี'], ['vatRate', 'อัตราภาษี (%)'], ['vatAmount', 'ภาษีมูลค่าเพิ่ม'],
['whtAmount', 'ภาษีหัก ณ ที่จ่าย'], ['totalAmount', 'ยอดรวมสุทธิ']];
// ฟิลด์เพิ่มเติมสำหรับเอกสารประเภท Trade (MIRO) เท่านั้น — ผู้ใช้กรอกเอง (ไม่ได้เดาจาก OCR)
const AP_TRADE_H = [['taxCode', 'Tax Code (รหัสภาษีซื้อ)'], ['calculateTax', 'Calculate Tax'],
['baselineDate', 'Baseline Date'], ['paymentMethod', 'Payment Method'], ['assignmentText', 'Assignment/Text']];
// ฟิลด์เพิ่มเติมสำหรับเอกสารประเภท Non-Trade ไม่มี PO เท่านั้น — ผู้ใช้กรอกเอง (ไม่ได้เดาจาก OCR)
// (Vendor/Invoice Date/Posting Date/Reference/Amount/Currency/Payment Terms/Withholding Tax ใช้ฟิลด์เดิมใน
// HEADER อยู่แล้ว ไม่ต้องเพิ่มซ้ำ — taxCode/calculateTax/baselineDate/paymentMethod ใช้ key ร่วมกับ Trade)
const AP_NONTRADE_NOPO_H = [['companyCode', 'Company Code'], ['taxCode', 'Tax Code'],
['calculateTax', 'Calculate Tax'], ['baselineDate', 'Baseline Date'], ['paymentMethod', 'Payment Method'],
['assignmentText', 'Assignment'], ['headerText', 'Header Text']];
// ฟิลด์ระดับรายการสำหรับเอกสารประเภท Non-Trade มี PO (Service/Item) เท่านั้น — แยกกรอกทีละรายการใน DETAIL
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
  if (mod) { S.module = mod; S.uploadApDocCategory = ''; S.inboxSearch = ''; S.inboxApDocCategory = ''; }
  S.page = p;
  document.querySelectorAll('#nav a').forEach(a =>
    a.classList.toggle('active', a.dataset.page === p && (!a.dataset.mod || a.dataset.mod === S.module)));
  $('#pageTitle').textContent = { home: 'ภาพรวมเอกสารในระบบ', work: 'นำเข้าเอกสาร / OCR', inbox: 'ทะเบียนเอกสาร', master: 'Master Mapping', log: 'ประวัติส่ง SAP' }[p];
  render();
}
function render() {
  const t = $('#modTag');
  const showMod = S.module && (S.page === 'work' || S.page === 'inbox');
  if (showMod) { t.style.display = 'inline-flex'; t.innerHTML = S.module === 'AP' ? '&#9632; Module: AP Invoice' : '&#9632; Module: Sales Order'; }
  else t.style.display = 'none';
  ({ home: renderHome, work: renderWork, inbox: renderInbox, master: renderMaster, log: renderLog })[S.page]();
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
// (กลุ่ม "AP INVOICE" / "SALES ORDER") หน้านี้เหลือแค่สรุปสถิติเอกสารรวมทุกโมดูล
async function renderHome() {
  $('#content').innerHTML = '<div class="card"><div class="card-b"><div class="empty">กำลังโหลด…</div></div></div>';
  try {
    const d = await API.get('/api/dashboard');
    const cnt = k => d.byStatus.filter(x => x.Status === k).reduce((a, b) => a + b.Cnt, 0);
    $('#content').innerHTML = `
    <div class="card"><div class="card-h"><h2>ภาพรวมเอกสารในระบบ</h2><div class="sp"></div>
      <button class="btn sm" onclick="viewAllInbox()">ดูทั้งหมด &rarr;</button></div>
    <div class="card-b">
      <div class="modules" style="grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px;margin-bottom:16px">
        ${[['NEW', 'รออ่าน/แก้ไข'], ['INCOMPLETE', 'Mapping ไม่ผ่าน'], ['MAPPED', 'พร้อมส่ง SAP'], ['POSTED', 'ส่ง SAP แล้ว']]
        .map(([k, l]) => `<div class="mod" style="cursor:default;padding:16px"><div class="hint">${l}</div>
             <div style="font-size:30px;font-weight:600;line-height:40px">${cnt(k)}</div></div>`).join('')}
      </div>
      <div class="tw"><table><thead><tr><th>#</th><th>Module</th><th>เลขที่เอกสาร</th><th>คู่ค้า</th>
        <th style="text-align:right">ยอดรวม</th><th>สถานะ</th><th>SAP Doc</th><th>เวลา</th></tr></thead><tbody>
        ${d.recent.map(r => `<tr onclick="openDoc(${r.DocId})" style="cursor:pointer">
          <td>${r.DocId}</td><td><span class="badge ${r.Module === 'AP' ? 'b-warn' : 'b-ok'}">${r.Module}</span></td>
          <td>${esc(r.DocNo || r.FileName)}</td><td>${esc(r.PartnerName || '')}</td>
          <td style="text-align:right">${fmt(r.TotalAmount)}</td><td>${statusBadge(r.Status)}</td>
          <td>${esc(r.SapDocNo || '')}</td><td class="hint">${dt(r.CreatedAt)}</td></tr>`).join('')
        || '<tr><td colspan="8" class="empty">ยังไม่มีเอกสาร</td></tr>'}
      </tbody></table></div>
    </div></div>`;
  } catch (e) { $('#content').innerHTML = '<div class="card"><div class="card-b"><div class="empty">โหลดภาพรวมไม่สำเร็จ</div></div></div>'; }
}
function statusBadge(s) {
  const m = { NEW: ['b-idle', 'รอ Mapping'], INCOMPLETE: ['b-fail', 'Mapping ไม่ผ่าน'], MAPPED: ['b-ok', 'พร้อมส่ง SAP'], POSTED: ['b-ok', 'ส่ง SAP แล้ว'] }[s] || ['b-idle', s];
  return `<span class="badge ${m[0]}">${m[1]}</span>`;
}
function viewAllInbox() { S.module = null; go('inbox'); }

/* ---------------------------------------------------------------- WORK */
async function renderWork() {
  if (!S.doc) { $('#content').innerHTML = stepsHtml(1) + await uploadHtml(); bindDrop(); return; }
  $('#content').innerHTML = stepsHtml(S.doc.status === 'POSTED' ? 3 : 2) + await docHtml();
}

async function uploadHtml() {
  let list = '';
  const needCategory = S.module === 'AP' && !S.uploadApDocCategory;
  try {
    const s = await API.get('/api/samples/' + S.module);
    list = s.map(x => `<tr><td><b>${esc(x.name)}</b></td><td>${esc(x.label)}</td><td>${Math.round(x.confidence * 100)}%</td>
      <td style="text-align:right"><button class="btn sm primary" ${needCategory ? 'disabled' : ''} onclick="useSample(${x.index})">อ่านเอกสาร</button></td></tr>`).join('');
  } catch (e) { }
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
      <div class="hint" style="margin-top:12px">โมดูลปัจจุบัน: <b>${S.module === 'AP' ? 'AP Invoice (ใบแจ้งหนี้ผู้ขาย)' : 'Sales Order (ใบสั่งซื้อลูกค้า)'}</b></div>
      <div id="prog" style="display:none;max-width:440px;margin:18px auto 0">
        <div id="progText" class="hint"></div><div class="bar"><i id="progBar"></i></div></div>
    </div>
    <p class="hint" style="margin:18px 0 10px">หรือเลือกเอกสารตัวอย่างเพื่อทดสอบขั้นตอนทั้งหมด:</p>
    <div class="tw"><table style="min-width:600px"><thead><tr><th>ไฟล์</th><th>ลักษณะเอกสาร</th><th>ความมั่นใจ</th><th></th></tr></thead>
      <tbody>${list}</tbody></table></div>
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
    toast(doc.ocrNote ? '&#9888; ' + esc(doc.ocrNote)
      : '&#10003; อ่านเอกสารสำเร็จ (' + doc.provider + ') — พบ ' + doc.lines.length + ' รายการ', 5000);
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
    const doc = await API.post('/api/documents/' + S.doc.docId + '/reocr', { ocr: engine });
    S.doc = doc; S.map = null; S.manual = { header: {}, lines: {} }; S.chatImage = null;
    // ไม่ล้างประวัติแชท — อ่านเอกสารใหม่ไม่ได้ลบ doc_id จึงยังอยู่บันทึกไว้ที่ server เหมือนเดิม
    await renderWork();
    const failed = doc.ocrNote && doc.provider === 'demo';
    openModal(`<div class="card-h"><h2>${failed ? '&#9888; อ่านเอกสารไม่สำเร็จ' : '&#10003; อ่านเอกสารใหม่เสร็จแล้ว'}</h2>
        <div class="sp"></div><button class="btn sm" onclick="closeModal()">&#10005;</button></div>
      <div class="card-b">
        ${failed
          ? `<p class="hint">${esc(doc.ocrNote)}</p>`
          : `<p>Engine: <b>${esc(doc.provider || '')}</b> &nbsp;·&nbsp; ความมั่นใจ: <b>${Math.round((doc.confidence || 0) * 100)}%</b></p>
             <p>พบรายการ Item Detail: <b>${doc.lines.length}</b> รายการ</p>
             ${doc.tokensIn != null ? `<p>Token ที่ใช้: <b>${num(doc.tokensIn).toLocaleString('en-US')}</b> input / <b>${num(doc.tokensOut).toLocaleString('en-US')}</b> output
               ${doc.cost != null ? ` &nbsp;·&nbsp; ค่าใช้จ่ายโดยประมาณ: <b>${fmtCost(doc.costIn)}</b> input + <b>${fmtCost(doc.costOut)}</b> output = <b>${fmtCost(doc.cost)} ${esc(doc.costCurrency || '')}</b>` : ''}</p>` : ''}
             ${doc.confidenceNote ? `<p class="hint">&#9888; ${esc(doc.confidenceNote)}</p>` : ''}`}
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
  const fileUrl = `/api/documents/${d.docId}/file`;
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
  const def = d.module === 'SO' ? SO_H : AP_H;
  const posted = d.status === 'POSTED';

  const headerFieldGrid = list => list.map(f => `<div class="f"><label>${f[1]}</label>
      <input type="text" value="${esc(h[f[0]] == null ? '' : h[f[0]])}" ${posted ? 'readonly' : ''}
             oninput="editHeader('${f[0]}',this.value)"></div>`).join('');
  const fields = headerFieldGrid(def);
  const tradeFields = (d.module === 'AP' && d.apDocCategory === 'TRADE') ? headerFieldGrid(AP_TRADE_H) : '';
  const nonTradeNoPoFields = (d.module === 'AP' && d.apDocCategory === 'NONTRADE_NOPO') ? headerFieldGrid(AP_NONTRADE_NOPO_H) : '';

  const mapCards = mp ? mappingCards(d, mp, md, posted) : '';

  const matOpts = md.materials.map(m => ({ v: m.MaterialCode, t: m.MaterialCode + ' — ' + m.Description }));
  const showPoExtra = d.module === 'AP' && (d.apDocCategory === 'NONTRADE_PO_SERVICE' || d.apDocCategory === 'NONTRADE_PO_ITEM');
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
      <td class="num"><input value="${fmt(l.qty)}" ${posted ? 'readonly' : ''} onfocus="this.value=num(this.value)" onblur="this.value=fmt(this.value)" oninput="editLine(${i},'qty',this.value)"></td>
      <td><input value="${esc(l.uom)}" ${posted ? 'readonly' : ''} oninput="editLine(${i},'uom',this.value)" style="width:64px"></td>
      <td class="num"><input value="${fmt(l.price)}" ${posted ? 'readonly' : ''} onfocus="this.value=num(this.value)" onblur="this.value=fmt(this.value)" oninput="editLine(${i},'price',this.value)"></td>
      <td class="num"><input value="${fmt(l.amount)}" ${posted ? 'readonly' : ''} onfocus="this.value=num(this.value)" onblur="this.value=fmt(this.value)" oninput="editLine(${i},'amount',this.value)"></td>
      <td class="${mp && mp.lines[i].status === 'fail' ? 'cell-fail' : ''}" style="min-width:270px">${cell}</td>
      <td class="${mp && (mp.lines[i].uom || {}).status === 'fail' ? 'cell-fail' : ''}" style="min-width:170px">${uomCell(mp, i, l, posted)}</td>
      <td style="white-space:nowrap">${st}
        ${mp && mp.lines[i].status === 'manual' && mp.lines[i].code && !posted
        ? `<button class="btn sm" style="margin-left:4px" onclick="learn(${i})">&#43; Master</button>` : ''}</td>
      ${showPoExtra ? `<td style="white-space:nowrap"><button class="btn sm ${lineExtraCount(l) ? '' : 'ghost'}" onclick="showLineExtra(${i})">
          &#128203; PO ${lineExtraCount(l) ? `(${lineExtraCount(l)}/${PO_LINE_EXTRA_FIELDS.length})` : ''}</button></td>` : ''}
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
      <div>เอกสาร SAP: <code>${esc(d.sapDocNo)}</code> &nbsp;|&nbsp; ${d.module === 'SO' ? 'Sales Order' : 'Supplier Invoice'} &nbsp;|&nbsp; ${dt(d.postedAt)}</div></div>` : '';

  return `${panel}${postedBox}
  <div class="card">
    <div class="card-h"><h2>เอกสาร #${d.docId}</h2>
      <span class="badge ${d.confidence >= 0.9 ? 'b-ok' : 'b-warn'}" ${d.confidenceNote ? `title="${esc(d.confidenceNote)}"` : ''}>OCR ${Math.round((d.confidence || 0) * 100)}% · ${esc(d.provider || '')}</span>
      ${d.tokensIn != null ? `<span class="badge" title="Token ที่ใช้อ่านเอกสารนี้">&#9889; ${num(d.tokensIn).toLocaleString('en-US')} in / ${num(d.tokensOut).toLocaleString('en-US')} out</span>` : ''}
      ${d.cost != null ? `<span class="badge" title="Input: ${fmtCost(d.costIn)} ${esc(d.costCurrency || '')} · Output: ${fmtCost(d.costOut)} ${esc(d.costCurrency || '')}">&#128176; ${fmtCost(d.cost)} ${esc(d.costCurrency || '')}</span>` : ''}
      <span class="filechip">&#128196; ${esc(d.fileName)}</span>${statusBadge(d.status)}
      <div class="sp"></div>
      ${!posted ? ocrProviderSelect('docOcrEngine', { ocr: 'tesseract', text: 'text', azure: 'azure', claude: 'claude', claude_text: 'claude_text', typhoon: 'typhoon', gemini: 'gemini', openai: 'openai' }[d.provider] || 'auto') : ''}
      <button class="btn sm primary" onclick="reOcr(this)" ${posted ? 'disabled' : ''}
        title="อ่านไฟล์ต้นฉบับใหม่ด้วย engine ที่เลือก">&#8635; อ่านเอกสารใหม่</button>
      <button class="btn sm ghost" onclick="showRaw()">&#128196; ข้อความที่อ่านได้</button>
      <button class="btn sm ghost" onclick="reviewDocument()" title="เปิดดูไฟล์ต้นฉบับ เทียบกับข้อมูลที่อ่านได้">&#128065; Review Document</button>
      <button class="btn sm ghost" onclick="S.doc=null;S.map=null;S.chatHistory=[];S.chatImage=null;renderWork()">เปลี่ยนเอกสาร</button></div>
    <div class="card-b">
      ${d.module === 'AP' ? `<div class="f" style="max-width:320px;margin-bottom:14px">
          <label>ประเภทเอกสาร</label>
          <select ${posted ? 'disabled' : ''} onchange="setDocCategory(this.value)">
            <option value="">— เลือกประเภทเอกสาร —</option>
            ${(S.apDocCategories || []).map(c => `<option value="${esc(c.id)}" ${c.id === d.apDocCategory ? 'selected' : ''}>${esc(c.label)}</option>`).join('')}
          </select></div>` : ''}
      ${d.confidenceNote ? `<div class="hint" style="margin:-6px 0 14px;padding:8px 12px;background:var(--line-soft);border-radius:var(--r2)">
          &#9888; เหตุผลที่ความแม่นยำไม่ถึง 100%: ${esc(d.confidenceNote)}</div>` : ''}
      <p class="sec-title">HEADER — ข้อมูลส่วนหัว</p>
      <div class="grid">${fields}</div>
    </div>
  </div>
  ${mapCards}

  <div class="card">
    <div class="card-h"><h2>DETAIL — รายการสินค้า (${d.lines.length} บรรทัด)</h2><div class="sp"></div>
      ${posted ? '' : '<button class="btn sm" onclick="addLine()">&#43; เพิ่มบรรทัด</button>'}</div>
    <div class="card-b"><div class="tw"><table>
      <thead><tr><th style="width:54px">Item</th><th style="width:150px">รหัสสินค้า (คู่ค้า)</th><th style="min-width:260px">ชื่อสินค้าตามเอกสาร</th>
        <th style="width:92px">จำนวน</th><th style="width:74px">หน่วย</th><th style="width:104px">ราคา/หน่วย</th>
        <th style="width:116px">จำนวนเงิน</th><th style="min-width:270px">Material (SAP)</th><th style="min-width:170px">หน่วย &rarr; SAP</th><th>สถานะ</th>
        ${showPoExtra ? '<th style="width:120px">PO Detail</th>' : ''}<th style="width:44px"></th></tr></thead>
      <tbody>${rows || `<tr><td colspan="${showPoExtra ? 12 : 11}" class="empty">ไม่มีรายการ</td></tr>`}</tbody>
      <tfoot><tr class="totrow"><td colspan="6" style="text-align:right">รวม</td>
        <td style="text-align:right">${fmt(sum)}</td><td colspan="${showPoExtra ? 5 : 4}"></td></tr></tfoot>
    </table></div></div>
  </div>

  ${tradeFields ? `<div class="card">
    <div class="card-h"><h2>ข้อมูลสำหรับ Trade (MIRO)</h2></div>
    <div class="card-b"><div class="grid">${tradeFields}</div></div>
  </div>` : ''}

  ${nonTradeNoPoFields ? `<div class="card">
    <div class="card-h"><h2>ข้อมูลสำหรับ Non-Trade ไม่มี PO</h2></div>
    <div class="card-b"><div class="grid">${nonTradeNoPoFields}</div></div>
  </div>` : ''}

  ${!posted ? chatFixCard() : ''}

  <div class="card"><div class="card-b row">
    <button class="btn primary" onclick="runMap()" ${posted ? 'disabled' : ''}>&#128269; ขั้นตอนที่ 2 — Mapping ข้อมูล</button>
    <button class="btn success" onclick="postSAP()" ${mp && mp.pass && !posted ? '' : 'disabled'}>&#9099; ขั้นตอนที่ 3 — ส่งเข้า SAP S/4HANA</button>
    <button class="btn" onclick="showPayload()" ${mp && mp.pass ? '' : 'disabled'}>&#123;&#125; ดู Payload</button>
    <div style="flex:1"></div>
    <span class="hint">${posted ? 'เอกสารนี้ส่งเข้า SAP แล้ว' : (mp ? (mp.pass ? 'พร้อมส่งเข้า SAP' : 'แก้ไขข้อมูลที่ไม่ผ่านก่อนส่ง') : 'กด Mapping เพื่อตรวจสอบกับ Master Data')}</span>
  </div></div>`;
}

/* ---------- แชทสั่งแก้ไขข้อมูล (AI) — แก้เฉพาะเอกสารนี้ ไม่บันทึกลง Master Data ---------- */
/* ประวัติเก็บถาวรที่ server (ocr.DocumentChat) — โหลดใหม่ทุกครั้งที่เปิดเอกสาร และหลังส่งข้อความสำเร็จ
   เพื่อให้ chatId/รูปภาพเป็นค่าจริงจาก server เสมอ (ไม่ใช่แค่ state ชั่วคราวในเบราว์เซอร์) */
async function loadChatHistory(docId) {
  try { S.chatHistory = await API.get(`/api/documents/${docId}/chat`); }
  catch (e) { S.chatHistory = []; }
}

function chatFixCard() {
  const ready = (S.ocrProviders || []).find(p => p.id === 'claude')?.ready;
  const msgs = S.chatHistory.map(m => {
    // m.image = data URL ชั่วคราว (ข้อความที่เพิ่งส่ง ยังไม่ได้ค่า chatId จาก server)
    // m.hasImage + m.chatId = ภาพที่บันทึกถาวรแล้ว โหลดผ่าน URL แทนการฝัง data URL ซ้ำ
    const imgSrc = m.image || (m.hasImage && m.chatId ? `/api/documents/${S.doc.docId}/chat/${m.chatId}/image` : '');
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
      ${!ready ? '<span class="hint">ต้องตั้งค่า ANTHROPIC_API_KEY ก่อนใช้งาน</span>' : ''}</div>
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
    const r = await API.post(`/api/documents/${S.doc.docId}/chat-fix`, { message, image, user: S.user });
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
  if (d.module === 'AP') {
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
      { header: S.doc.header, lines: S.doc.lines, manual: S.manual });
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
    <p>ระบบจะสร้างเอกสาร <b>${d.module === 'SO' ? 'Sales Order' : 'Supplier Invoice (AP Invoice)'}</b> ในระบบ SAP</p>
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
  S.inboxList = await API.get('/api/documents?limit=200' + (S.module ? '&module=' + S.module : '') + catQs);
  renderInboxLocal();
}
function renderInboxLocal() {
  const q = (S.inboxSearch || '').trim().toLowerCase();
  const list = !q ? S.inboxList : S.inboxList.filter(r =>
    String(r.DocNo || '').toLowerCase().includes(q) ||
    String(r.PartnerName || '').toLowerCase().includes(q) ||
    String(r.DocDate || '').toLowerCase().includes(q));
  const title = S.module ? `ทะเบียนเอกสาร — ${S.module === 'AP' ? 'AP Invoice' : 'Sales Order'} (${list.length})`
    : `ทะเบียนเอกสารทั้งหมด (${list.length})`;
  const catFilter = S.module === 'AP' ? `<select onchange="S.inboxApDocCategory=this.value;renderInbox()" style="margin-right:8px">
      <option value="">ทุกประเภทเอกสาร</option>
      ${(S.apDocCategories || []).map(c => `<option value="${esc(c.id)}" ${c.id === S.inboxApDocCategory ? 'selected' : ''}>${esc(c.label)}</option>`).join('')}
    </select>` : '';
  $('#content').innerHTML = `
  <div class="card"><div class="card-h"><h2>${title}</h2><div class="sp"></div>
    <input id="inboxSearch" type="text" placeholder="ค้นหา วันที่ / คู่ค้า / เลขที่เอกสาร…" value="${esc(S.inboxSearch)}"
           oninput="S.inboxSearch=this.value;renderInboxLocal()" style="max-width:260px;margin-right:8px">
    ${catFilter}<button class="btn sm" onclick="renderInbox()">&#8635; รีเฟรช</button></div>
  <div class="card-b"><div class="tw"><table>
    <thead><tr><th>#</th><th>Module</th><th>ไฟล์</th><th>เลขที่เอกสาร</th><th>วันที่</th><th>คู่ค้า</th>
      <th style="text-align:right">ยอดรวม</th>${S.module === 'AP' ? '<th>ประเภทเอกสาร</th>' : ''}<th>สถานะ</th><th>SAP Doc</th><th>สร้างเมื่อ</th><th></th></tr></thead>
    <tbody>${list.map(r => `<tr>
      <td>${r.DocId}</td><td><span class="badge ${r.Module === 'AP' ? 'b-warn' : 'b-ok'}">${r.Module}</span></td>
      <td>${esc(r.FileName || '')}</td><td>${esc(r.DocNo || '')}</td><td>${esc(r.DocDate || '')}</td>
      <td>${esc(r.PartnerName || '')}</td><td style="text-align:right">${fmt(r.TotalAmount)}</td>
      ${S.module === 'AP' ? `<td>${r.ApDocCategory ? esc(apDocCategoryLabel(r.ApDocCategory)) : '<span class="hint">—</span>'}</td>` : ''}
      <td ${r.OcrConfidenceNote || r.OcrTokensIn != null ? `title="${esc([r.OcrConfidenceNote,
          r.OcrTokensIn != null ? `Token: ${r.OcrTokensIn} in / ${r.OcrTokensOut} out` : '',
          r.OcrCost != null ? `ค่าใช้จ่าย: ${r.OcrInputCost} in + ${r.OcrOutputCost} out = ${r.OcrCost} ${r.OcrCostCurrency || ''}` : ''].filter(Boolean).join(' — '))}"` : ''}>${statusBadge(r.Status)}
        ${r.OcrConfidence != null ? `<span class="hint">${Math.round(r.OcrConfidence * 100)}%</span>` : ''}</td>
      <td>${esc(r.SapDocNo || '')}</td><td class="hint">${dt(r.CreatedAt)}</td>
      <td style="white-space:nowrap"><button class="btn sm" onclick="openDoc(${r.DocId})">เปิด</button>
        ${r.Status === 'POSTED' ? '' : `<button class="btn sm ghost" onclick="delDoc(${r.DocId})">&#10005;</button>`}</td></tr>`).join('')
      || `<tr><td colspan="${S.module === 'AP' ? 12 : 11}" class="empty">ยังไม่มีเอกสารในระบบ</td></tr>`}
    </tbody></table></div></div></div>`;
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
  await guard(async () => { await API.del('/api/documents/' + id); toast('ลบเอกสารแล้ว'); renderInbox(); });
}

/* ---------------------------------------------------------------- MASTER */
async function renderMaster() {
  const md = await masters(true);
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
      `<button class="btn sm ${S.masterTab === k ? 'primary' : ''}" onclick="S.masterTab='${k}';renderMaster()">
         ${MASTER_DEF[k].label} <span style="opacity:.7">${count(k)}</span></button>`).join('') + `</div>`;

  const def = MASTER_DEF[S.masterTab], rows = md[S.masterTab];
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
        <span class="hint">${MASTER_NOTE[S.masterTab]}</span>
      </div>
      <div class="tw"><table><thead><tr>${def.cols.map(c => `<th>${esc(c.l)}</th>`).join('')}<th style="width:130px"></th></tr></thead>
      <tbody>${body || `<tr><td colspan="${def.cols.length + 1}" class="empty">ยังไม่มีข้อมูล</td></tr>`}</tbody></table></div>
    </div></div>`;
}
function setGroup(k) {
  S.masterGroup = k;
  S.masterTab = (MASTER_GROUPS.find(g => g.key === k) || MASTER_GROUPS[0]).tabs[0];
  renderMaster();
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
  const list = await API.get('/api/logs');
  $('#content').innerHTML = `
  <div class="card"><div class="card-h"><h2>ประวัติการส่งเข้า SAP (${list.length})</h2><div class="sp"></div>
    <button class="btn sm" onclick="renderLog()">&#8635; รีเฟรช</button></div>
  <div class="card-b"><div class="tw"><table>
    <thead><tr><th>เวลา</th><th>Module</th><th>SAP Doc</th><th>เอกสารอ้างอิง</th><th>คู่ค้า</th>
      <th style="text-align:right">ยอดรวม</th><th>รายการ</th><th>ผล</th><th>ไฟล์</th><th></th></tr></thead>
    <tbody>${list.map(l => `<tr>
      <td class="hint">${dt(l.PostedAt)}</td><td><span class="badge ${l.Module === 'AP' ? 'b-warn' : 'b-ok'}">${esc(l.Module)}</span></td>
      <td><b>${esc(l.SapDocNo || '-')}</b></td><td>${esc(l.DocNo || '')}</td><td>${esc(l.PartnerName || '')}</td>
      <td style="text-align:right">${fmt(l.TotalAmount)}</td><td style="text-align:center">${l.Lines || 0}</td>
      <td>${l.Success ? '<span class="badge b-ok">&#10003; สำเร็จ</span>' : '<span class="badge b-fail">&#10007; ไม่สำเร็จ</span>'}</td>
      <td>${esc(l.FileName || '')}</td>
      <td><button class="btn sm" onclick="showLogPayload(${l.LogId})">Payload</button></td></tr>`).join('')
      || '<tr><td colspan="10" class="empty">ยังไม่มีเอกสารที่ส่งเข้า SAP</td></tr>'}
    </tbody></table></div></div></div>`;
}
async function showLogPayload(id) {
  await guard(async () => {
    const p = await API.get('/api/logs/' + id + '/payload');
    openModal(`<div class="card-h"><h2>Payload (Log #${id})</h2><div class="sp"></div>
      <button class="btn sm" onclick="closeModal()">&#10005;</button></div>
      <div class="card-b"><pre class="json">${esc(JSON.stringify(p, null, 2))}</pre></div>`);
  });
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
