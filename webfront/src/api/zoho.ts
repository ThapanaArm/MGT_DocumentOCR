import { api } from './client';
import type { DocModel } from './documents';

/* Live Zoho CRM "Accounts" (customer) lookup — the Zoho-side counterpart to sap.ts, used for
   the MGT-side Sales Order flow (Green Leaf keeps using sap.ts / SAP Business Partner).
   Backend: GET /api/zoho/account (MgtOcr.Zoho.ZohoAccountClient).
   Field names are confirmed against Megachem's Zoho CRM user manual (AO-CRM-UM-2026-003,
   section 5.2.8), not guessed. Tax ID is tried first; the customer name is a "contains" fallback
   used only when Tax ID is missing or comes up empty — same shape as the SAP lookup. */

export interface ZohoAccount {
  accountId: string;
  accountName: string;
  accountCode?: string | null;
  taxId?: string | null;
  /** Head office vs branch, e.g. "HEAD OFFICE" — a real Zoho field (Branch_Name), unlike SAP's
   *  Business Partner API which turned out not to provide this reliably. */
  branchName?: string | null;
  accountType?: string | null;
}

export interface ZohoAccountSearchResult {
  count: number;
  results: ZohoAccount[];
}

export interface ZohoAccountSearchParams {
  name?: string;
  taxId?: string;
  top?: number;
}

export const searchZohoAccount = ({ name, taxId, top = 10 }: ZohoAccountSearchParams) => {
  const qs = new URLSearchParams();
  if (taxId) qs.set('taxId', taxId);
  if (name) qs.set('name', name);
  qs.set('top', String(top));
  return api.get<ZohoAccountSearchResult>('/api/zoho/account?' + qs.toString());
};

/* Every populated field Zoho has for one Account (not just the curated search columns above) —
   used by CompareModal to show the full record instead of only Name/Tax ID/Branch/Account Type
   once the person opens the "Compare" popup. Backend: GET /api/zoho/account/{id}/full, a plain
   record-by-id fetch (no field allow-list), so it reflects whatever's on Megachem's actual Zoho
   layout rather than a hand-picked subset that would need re-confirming from the manual. */
export interface ZohoFullField {
  label: string;
  value?: string | null;
}

export const getZohoAccountFull = (accountId: string) =>
  api.get<{ fields: ZohoFullField[] }>('/api/zoho/account/' + encodeURIComponent(accountId) + '/full');

// Master rows store Account Code; resolve the record ID only when calling Zoho's record APIs.
const resolveAccount = (code: string) => api.get<ZohoAccount>('/api/zoho/account/by-code/' + encodeURIComponent(code));
export const getZohoAccountFullByCode = async (code: string) => getZohoAccountFull((await resolveAccount(code)).accountId);
export const getZohoAccountShipTosByCode = async (code: string) => getZohoAccountShipTos((await resolveAccount(code)).accountId);
export const getZohoAccountSoldToByCode = async (code: string) => getZohoAccountSoldTo((await resolveAccount(code)).accountId);

/* Sold-to/Ship-to address off a Zoho Account -- every confirmed sub-field, not just a collapsed
   joined string, so the UI can show exactly what's on file. Field names confirmed against
   Megachem's Zoho CRM user manual, section 5.2.8 "Accounts Module" (inline Sold-to/Ship-to
   Information) and section 5.4.3 "Ship-to Module" (the separate module used when an Account has
   more than one delivery address). `source` says which: "account" (the inline fields) or
   "shipto-module" (a record from the separate module). */
export interface ZohoShipToInfo {
  address?: string | null;
  code?: string | null;
  street?: string | null;
  street2?: string | null;
  street3?: string | null;
  street4?: string | null;
  street5?: string | null;
  houseNumber?: string | null;
  district?: string | null;
  city?: string | null;
  differenceCity?: string | null;
  postCode?: string | null;
  countryReg?: string | null;
  source?: string | null;
  /** Zoho record id when this address comes from the separate Ship-to module. */
  recordId?: string | null;
}

/* Single best Ship-to for this Account -- the inline one if present, else the first record from
   the separate Ship-to Module. Backend: GET /api/zoho/account/{id}/shipto. */
export const getZohoAccountShipTo = (accountId: string) =>
  api.get<ZohoShipToInfo | null>('/api/zoho/account/' + encodeURIComponent(accountId) + '/shipto');

/* Every Ship-to on file for this Account, plus whether the "Ship to มากกว่า 1" (Ship_to_1)
   checkbox is ticked on the Account -- when it is, this also includes every record from
   Megachem's separate Ship-to Module. Backend: GET /api/zoho/account/{id}/shiptos. */
export interface ZohoShipToListResult {
  count: number;
  hasMultipleShipTos: boolean;
  shipTos: ZohoShipToInfo[];
}

export const getZohoAccountShipTos = (accountId: string) =>
  api.get<ZohoShipToListResult>('/api/zoho/account/' + encodeURIComponent(accountId) + '/shiptos');

/* Sold-to (the customer's own billing) address off this same Account -- shown next to a search
   result so the person can eyeball which candidate is right without opening a full compare.
   Backend: GET /api/zoho/account/{id}/soldto. Same shape as ZohoShipToInfo above. */
export const getZohoAccountSoldTo = (accountId: string) =>
  api.get<ZohoShipToInfo | null>('/api/zoho/account/' + encodeURIComponent(accountId) + '/soldto');

/* Open (not Closed Won/Lost) Deals linked to this Zoho Account, each with its Deal Items
   subform -- used by the "which Deal does this document belong to" step of the OCR->Zoho Sales
   Order flow. Backend: GET /api/zoho/account/{id}/deals (MgtOcr.Zoho.ZohoDealClient). Field
   names confirmed against the Zoho CRM user manual, section 5.6.9. Returns every open Deal,
   not just "the" best match -- Megachem wants the person to pick, with the OCR AI-compare tool
   if they want it. */
export interface ZohoDealItem {
  materialName?: string | null;
  /** Zoho record id of the linked Materials-module record (the Material_Name lookup's own id) --
   *  used as Product_Name when this Deal Item is reused to build a Zoho Sales Order line, since
   *  there is no separate Zoho Material search feature. */
  materialId?: string | null;
  materialCode?: string | null;
  materialDescription?: string | null;
  materialGroup?: string | null;
  quantity?: number | null;
  unit?: string | null;
  conversionRatio?: number | null;
  subUnit?: string | null;
  unitPrice?: number | null;
  priceSubUnit?: number | null;
  lastPrice?: number | null;
  totalAmount?: number | null;
  shipVia?: string | null;
  stock?: string | null;
  leadTimeDays?: string | null;
  soCompleted?: number | null;
  soCancelled?: number | null;
}

export interface ZohoDeal {
  id: string;
  dealName: string;
  stage: string;
  /** Zoho record id of the linked Account (the Account_Name lookup's own id) -- used as
   *  Account_Name when creating a Zoho Sales Order from this Deal. */
  accountId?: string | null;
  accountCode?: string | null;
  customerRef?: string | null;
  deliveryDate?: string | null;
  closingDate?: string | null;
  items: ZohoDealItem[];
}

// Looks up open Deals by the Deal's own Account_Code field (same customer code stored locally
// as CustomerCode) rather than a Zoho Account record id -- see ZohoDealClient.
// FindOpenDealsByAccountCodeAsync for why: this needs no Zoho account link to exist at all.
export const getZohoDealsByAccountCode = (accountCode: string) =>
  api.get<{ count: number; deals: ZohoDeal[] }>(
    '/api/zoho/deals/by-account-code/' + encodeURIComponent(accountCode),
  );

/* Manual fallback search shown by the Deal card when the Account_Code lookup above finds no
   open Deal -- searches by Deal Name (partial match), with no account-code or stage filter, so
   a Deal that was missed because its Zoho Account link (and so its Account_Code) is wrong/blank,
   or because it's already in a Closed stage, still shows up. Backend: GET /api/zoho/deals/search
   (MgtOcr.Zoho.ZohoDealClient.SearchByNameAsync). */
export const searchZohoDeals = (query: string, top = 20) =>
  api.get<{ count: number; deals: ZohoDeal[] }>(
    '/api/zoho/deals/search?q=' + encodeURIComponent(query) + '&top=' + top,
  );

/* Creates a Zoho CRM "Sales Orders" record from a document, linked by lookup to the given Deal.
   Backend: GET .../preview + POST .../create/{docId} (MgtOcr.Zoho.ZohoSalesOrderClient +
   ZohoSalesOrderController) -- deliberately separate from postToSap/DocumentsController.
   Server-side matches document lines to the Deal's own Deal Items (by Material code, else a
   fuzzy description match) and reports any line it could not match rather than guessing.

   Every header/line value that would otherwise be silently auto-derived is returned by the
   preview call as an editable default -- nothing is sent un-reviewable. paymentTermsOptions/
   paymentCurrencyOptions come straight from Zoho's own live picklist metadata, not a hand-typed
   list, so they always match whatever Megachem's Zoho admin has configured. */
export type ZohoSalesOrderSkippedLine = { itemNo?: unknown; desc?: string | null; extCode?: string | null; reason: string };

export interface ZohoSalesOrderPreviewLine {
  itemNo: unknown;
  desc?: string | null;
  extCode?: string | null;
  materialName?: string | null;
  materialCode?: string | null;
  quantity: number;
  unitPrice?: number | null;
  unit?: string | null;
  /** UoM conversion the mapping applied for this MGT line, surfaced in the preview (F10):
   *  1 order unit = conversionRatio subUnit. Null when no conversion applies. */
  conversionRatio?: number | null;
  subUnit?: string | null;
}

export interface ZohoSalesOrderPreview {
  dealName: string;
  accountCode?: string | null;
  subject: string;
  customerRef?: string | null;
  deliveryDate?: string | null;
  paymentTerms?: string | null;
  paymentCurrency?: string | null;
  incoterms?: string | null;
  taxId?: string | null;
  paymentTermsOptions: string[];
  paymentCurrencyOptions: string[];
  lines: ZohoSalesOrderPreviewLine[];
  skipped: ZohoSalesOrderSkippedLine[];
}

export const getZohoSalesOrderPreview = (docId: number, dealId: string) =>
  api.get<ZohoSalesOrderPreview>(
    '/api/zoho/sales-order/preview/' + docId + '?dealId=' + encodeURIComponent(dealId),
  );

export interface ZohoSalesOrderLineEdit {
  itemNo: unknown;
  quantity?: number | null;
  unitPrice?: number | null;
  unit?: string | null;
  description?: string | null;
  /** Zoho Material record id of a Deal Item the person confirmed (via the AI-suggested-match
   *  tool on a line the automatic code/description match skipped) as the right one for this
   *  line -- see ZohoSalesOrderController.Create, which only honors it when it's actually one
   *  of the selected Deal's own Items. */
  materialId?: string | null;
}

export interface ZohoSalesOrderEdits {
  subject?: string;
  customerRef?: string;
  deliveryDate?: string;
  paymentTerms?: string;
  paymentCurrency?: string;
  incoterms?: string;
  taxId?: string;
  /** Delivery address explicitly selected on the Ship-to mapping card. */
  shipTo?: ZohoShipToInfo;
  lines?: ZohoSalesOrderLineEdit[];
}

export interface ZohoSalesOrderResult {
  success: boolean;
  zohoId?: string | null;
  message?: string | null;
  dealName: string;
  linesSent: number;
  skipped: ZohoSalesOrderSkippedLine[];
  /** The updated document after the send (status flips to POSTED on success), so the page can
   *  reflect the posted state without a manual refresh -- mirrors the SAP /post response. */
  document?: DocModel;
}

export const createZohoSalesOrder = (docId: number, dealId: string, edits?: ZohoSalesOrderEdits) =>
  api.post<ZohoSalesOrderResult>('/api/zoho/sales-order/create/' + docId, { dealId, ...edits });

/* The exact JSON that createZohoSalesOrder would POST to Zoho, without creating anything -- the
   Zoho counterpart to the SAP "View Payload". Returns the on-the-wire envelope ({ _target, data:
   [record] }); same request body as createZohoSalesOrder so it reflects the current edits. */
export const getZohoSalesOrderPayload = (docId: number, dealId: string, edits?: ZohoSalesOrderEdits) =>
  api.post<Record<string, unknown>>('/api/zoho/sales-order/payload/' + docId, { dealId, ...edits });
