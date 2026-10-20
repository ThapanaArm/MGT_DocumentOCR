import { api } from './client';

/* Live SAP Business Partner (customer) lookup — used to help match/create a
   Customer master row from real SAP data when local matching fails.
   Backend: GET /api/sap/business-partner (MgtOcr.Sap.SapBusinessPartnerClient).
   Tax ID is an exact match (one company = one Tax ID) and is tried first; the
   customer name is a fuzzy fallback used only when Tax ID is missing or comes
   up empty. */

export interface SapBusinessPartner {
  businessPartnerId: string;
  businessPartnerName: string;
  businessPartnerFullName?: string | null;
  businessPartnerIsBlocked?: boolean;
  /** Head office vs branch distinguisher: TH3 branch-code turned out identical across branches
   *  on this tenant, so address is used instead — head office and its branches are different
   *  physical locations. Not yet confirmed reliable, may be null. */
  addressCity?: string | null;
  addressStreet?: string | null;
  /** Address sub-fields added 2026-09-22 alongside the SAP backend's expanded
   *  A_BusinessPartnerAddress $select (see SapBusinessPartnerClient.BusinessPartner) -- the SAP
   *  equivalents of Zoho's ZohoShipToInfo breakdown, so Ship-to/Sold-to can show more than just
   *  street+city for SAP too. Street2-5/DifferenceCity added same day (2026-09-22, second pass)
   *  once Megachem's own SAP->Zoho Ship_to sync job (ZohoShipToSyncJob, a separate service)
   *  confirmed SAP does have 4 street lines plus a "home city"/Difference City equivalent
   *  (StreetPrefixName/AdditionalStreetPrefixName/StreetSuffixName/AdditionalStreetSuffixName/
   *  HomeCityName) -- this class's first pass had only gone up to Street 3 and guessed the wrong
   *  OData field names for it. */
  addressHouseNumber?: string | null;
  addressDistrict?: string | null;
  addressPostalCode?: string | null;
  addressCountry?: string | null;
  addressStreet2?: string | null;
  addressStreet3?: string | null;
  addressStreet4?: string | null;
  addressStreet5?: string | null;
  addressDifferenceCity?: string | null;
  /** SAP's own Tax ID on file for this partner (A_BusinessPartnerTaxNumber, BPTaxNumber) — always
   *  filled in when SAP has one, regardless of whether the search was by Tax ID or by name, so it
   *  can be shown/used even when the document being matched had none of its own. */
  taxId?: string | null;
}

export interface SapBusinessPartnerSearchResult {
  count: number;
  results: SapBusinessPartner[];
}

export interface SapBusinessPartnerSearchParams {
  name?: string;
  taxId?: string;
  /** SAP customer code (BusinessPartner id) — matched as a substring, so a prefix works. */
  code?: string;
  top?: number;
}

// companyCode is NOT sent here on purpose: the backend derives the AuthorizationGroup scope from
// the signed-in user's own company (see SapBusinessPartnerController.Find), not from anything the
// client passes, so a caller can't ask for another company's data by changing a query param.
export const searchSapBusinessPartner = ({ name, taxId, code, top = 10 }: SapBusinessPartnerSearchParams) => {
  const qs = new URLSearchParams();
  if (taxId) qs.set('taxId', taxId);
  if (code) qs.set('code', code);
  if (name) qs.set('name', name);
  qs.set('top', String(top));
  return api.get<SapBusinessPartnerSearchResult>('/api/sap/business-partner?' + qs.toString());
};

/* Ship-to/Sold-to partner-function links SAP already has on file for a Sold-to's sales area --
   used to help match a document's Ship-to against what SAP knows for the already-matched
   customer, instead of the local shiptos master alone.
   Backend: GET /api/sap/business-partner/{customerId}/partners (MgtOcr.Sap.SapBusinessPartnerClient
   .FindPartnerFunctionsAsync). PartnerFunction "SH" = Ship-to, "SP" = Sold-to. Field names on the
   underlying A_CustSalesPartnerFunc entity are not yet verified against this tenant's real SAP
   metadata -- see the backend NOTE. */

export interface SapPartnerFunctionLink {
  customer: string;
  partnerFunction: string;
  partnerCustomer: string;
  partner?: SapBusinessPartner | null;
}

export interface SapPartnerFunctionSearchResult {
  count: number;
  results: SapPartnerFunctionLink[];
}

export const findSapPartnerFunctions = (soldToSapCode: string, salesOrganization: string, partnerFunction?: string, top = 50) => {
  const qs = new URLSearchParams();
  if (partnerFunction) qs.set('function', partnerFunction);
  qs.set('salesOrganization', salesOrganization);
  qs.set('top', String(top));
  return api.get<SapPartnerFunctionSearchResult>(
    `/api/sap/business-partner/${encodeURIComponent(soldToSapCode)}/partners?` + qs.toString(),
  );
};

export interface SapMaterial {
  materialCode: string;
  materialDescription: string;
  language?: string | null;
}

export const searchSapMaterials = (description: string, plant: string, top = 30) => {
  const qs = new URLSearchParams({ description, plant, top: String(top) });
  return api.get<{ count: number; results: SapMaterial[] }>('/api/sap/materials?' + qs.toString());
};

/* Base Unit / Material Group / pack-size (alternative unit of measure) lookup for one material —
   used to default the "confirm material details" popup shown when a SAP search result is first
   saved, so Base Unit and bag/box size etc. don't have to be typed in from scratch. Always
   best-effort: `detail` comes back null when SAP isn't configured, the material isn't found, or
   the lookup fails — callers must treat it as an optional default, never a blocker, and let the
   person confirm or correct it before saving.
   Backend: GET /api/sap/material-detail (MgtOcr.Sap.SapProductClient.GetMaterialDetailAsync). */

export interface SapUnitOfMeasure {
  unit: string;
  numerator: number;
  denominator: number;
}

export interface SapMaterialDetail {
  materialCode: string;
  baseUnit: string;
  materialGroup?: string | null;
  altUnits: SapUnitOfMeasure[];
}

export const getSapMaterialDetail = (materialCode: string) =>
  api.get<{ detail: SapMaterialDetail | null }>(
    '/api/sap/material-detail?product=' + encodeURIComponent(materialCode),
  );

/* Last Price — GLC Sales Order flow only: the last actual price SAP billed THIS customer for THIS
   material, shown when a Material Code is confirmed in the material-confirm popup so the person
   picking a unit has a live reference price (never used to fill in / override the line's own
   price). Always best-effort: `price` comes back null when SAP Billing isn't configured, there's
   no prior billing line for this exact customer+material pair, or the lookup fails — callers must
   treat it as optional context, never a blocker to confirming the material.
   Plant/company scope is derived server-side from the signed-in user, same as the Business
   Partner search above — customer/material are the only inputs this endpoint needs.
   Backend: GET /api/sap/last-price (MgtOcr.Sap.SapBillingClient.GetLastPriceAsync). */

export interface SapLastPrice {
  pricePerUnit: number;
  unit: string;
  billingDocument?: string | null;
  creationDate?: string | null;
}

export const getSapLastPrice = (customerCode: string, materialCode: string) => {
  const qs = new URLSearchParams({ customer: customerCode, material: materialCode });
  return api.get<{ customer: string; material: string; price: SapLastPrice | null }>(
    '/api/sap/last-price?' + qs.toString(),
  );
};

/* What this customer has bought before (GLC Material card, 15 Oct 2026): one row per material,
   merged from SAP billing for this Sold-to (last 12 months) and Sales Orders posted through OCR.
   Best-effort: an empty list when SAP is not configured, there is no history, or a source fails.
   Backend: GET /api/sap/customer-materials (SapBillingClient.GetSoldMaterialsAsync +
   SalesOrderPostRepository.SoldMaterialsAsync). */
export interface SapCustomerMaterial {
  materialCode: string;
  /** SAP billing item text, or the document's own text when only OCR history has it. */
  materialDescription: string;
  /** How the customer's document described it on the last OCR-posted order (when there was one). */
  docDescription?: string | null;
  lastPrice?: number | null;
  unit?: string | null;
  /** dd/MM/yyyy */
  lastDate?: string | null;
  lastDocNo?: string | null;
  times: number;
  sources: ('SAP' | 'OCR')[];
}

export const getSapCustomerMaterials = (customerCode: string) =>
  api.get<{ customer: string; count: number; results: SapCustomerMaterial[] }>(
    '/api/sap/customer-materials?' + new URLSearchParams({ customer: customerCode }).toString(),
  );

/* Customer Payment Terms — GLC Sales Order flow: the payment terms SAP has on the customer master
   for the already-matched Sold-to, shown live in the Customer card so the person always sees SAP's
   own value instead of a blank "—". Source/priority mirrors the ZohoAccountPushJob account sync:
   company-code level (A_CustomerCompany.PaymentTerms) first, sales-area level
   (A_CustomerSalesArea.CustomerPaymentTerms) as fallback. Always best-effort: `paymentTerms` (and
   its `.paymentTerms` code) come back null when SAP isn't configured, has none on file, or the
   lookup fails — callers must treat it as optional context.
   salesOrganization/companyCode scope which company's/sales area's terms to read (GLC = 2000/2000)
   and are the document's own sales org (which follows the company being worked as), not derived
   server-side, so an admin simulating GLC reads GLC's terms.
   Backend: GET /api/sap/business-partner/{id}/payment-terms
   (MgtOcr.Sap.SapBusinessPartnerClient.GetPaymentTermsAsync). */

export interface SapCustomerPaymentTerms {
  customer: string;
  /** SAP's payment-terms code (e.g. "NT30", "0001"), or null when SAP has none on file. */
  paymentTerms?: string | null;
  /** Which master level the value came from: "company" or "salesArea". */
  source?: string | null;
  companyCode?: string | null;
  salesOrganization?: string | null;
}

export const getSapCustomerPaymentTerms = (
  soldToSapCode: string,
  salesOrganization?: string,
  companyCode?: string,
) => {
  const qs = new URLSearchParams();
  if (salesOrganization) qs.set('salesOrganization', salesOrganization);
  if (companyCode) qs.set('companyCode', companyCode);
  const suffix = qs.toString();
  return api.get<{ customerId: string; paymentTerms: SapCustomerPaymentTerms | null }>(
    `/api/sap/business-partner/${encodeURIComponent(soldToSapCode)}/payment-terms` + (suffix ? '?' + suffix : ''),
  );
};

/* Customer Sales Areas — GLC Sales Order flow: the customer's sales areas from the SAP customer
   master (A_CustomerSalesArea) for a sales org. A customer can have several (differing by
   DistributionChannel/Division), each with its own Sales Group / payment terms. The Customer card
   uses the single area automatically, or lets the person pick when there are more than one — and the
   chosen area's Channel/Division/Sales Group are what get sent to SAP. Always best-effort: `areas`
   comes back empty when SAP isn't configured, the customer has none, or the lookup fails.
   Backend: GET /api/sap/business-partner/{id}/sales-areas
   (MgtOcr.Sap.SapBusinessPartnerClient.GetSalesAreasAsync). */

export interface SapCustomerSalesArea {
  salesOrganization: string;
  distributionChannel: string;
  division: string;
  salesGroup?: string | null;
  salesOffice?: string | null;
  customerPaymentTerms?: string | null;
  currency?: string | null;
}

export const getSapCustomerSalesAreas = (soldToSapCode: string, salesOrganization?: string) => {
  const qs = new URLSearchParams();
  if (salesOrganization) qs.set('salesOrganization', salesOrganization);
  const suffix = qs.toString();
  return api.get<{ customerId: string; count: number; areas: SapCustomerSalesArea[] }>(
    `/api/sap/business-partner/${encodeURIComponent(soldToSapCode)}/sales-areas` + (suffix ? '?' + suffix : ''),
  );
};

/* Sales Employee (per Sales Order line) — GLC flow. Two endpoints:
   1) last-sales-employee: who was the Sales Employee the LAST time THIS customer bought THIS
      material, read from the most recent Sales Order item custom field YY1_SDSalesEmployeeI_SDI.
      Used to pre-fill each OCR line with a suggestion. `suggestion` is null when there's no prior
      order for the pair / SAP not configured / lookup failed — treat as optional, then fall back to
      the full picker.
   2) sales-employees: the full pick list, merged from the DB master
      (dbo.SysDataMapping Subject='SalesEmployee') AND live SAP (distinct Person IDs used on recent
      orders). Each entry carries a name (from DB when known) and which sources it came from.
   Backend: GET /api/sap/sales-order/last-sales-employee, GET /api/sap/sales-employees
   (MgtOcr.Sap.SapSalesOrderClient + MasterRepository). */

export interface SapSalesEmployeeSuggestion {
  /** SAP custom Person ID, e.g. "9980000002". */
  personId: string;
  /** Display name from the DB master, or null when the DB has no row for this Person ID. */
  name?: string | null;
  /** The Sales Order the suggestion was read from (for "as used on SO ####"). */
  salesOrder?: string | null;
  creationDate?: string | null;
}

export const getSapLastSalesEmployee = (customerCode: string, materialCode: string) => {
  const qs = new URLSearchParams({ customer: customerCode, material: materialCode });
  return api.get<{ customer: string; material: string; suggestion: SapSalesEmployeeSuggestion | null }>(
    '/api/sap/sales-order/last-sales-employee?' + qs.toString(),
  );
};

export interface SapSalesEmployee {
  personId: string;
  name?: string | null;
  /** Ms_User.Division = this person's SAP Sales Group; null when not on file. */
  salesGroup?: string | null;
  /** "db", "sap", or both — where this Person ID was found. */
  sources: string[];
  lastSalesOrder?: string | null;
  lastCreationDate?: string | null;
}

export const getSapSalesEmployees = () =>
  api.get<{ count: number; results: SapSalesEmployee[] }>('/api/sap/sales-employees');

/* The purchase order behind a supplier invoice, read from SAP — the only place its ITEM numbers
   exist. SAP numbers PO items with gaps (10, 40, 50, 60 on PO 2230000020), so a MIRO posted
   against the PO has to name those exact numbers; the printed PO renumbers its rows 1, 2, 3 and
   the shipping form does not carry them at all.
   `configured: false` means Sap:PurchaseOrder:BaseUrl is not set — show that rather than an empty
   purchase order.
   Backend: GET /api/sap/purchase-order/{po} (MgtOcr.Sap.SapPurchaseOrderClient). */

export interface SapPurchaseOrderItem {
  purchaseOrder: string;
  item: string;
  material: string;
  text: string;
  quantity: number;
  unit: string;
  netPrice: number;
  currency: string;
  plant: string;
  taxCode: string;
}

/** The purchase order's own header — who it is for and under which company. Used to catch an
 *  invoice pointed at the wrong PO before it is sent: PO 2230000020 is Kimex 2000014 / company
 *  2000. Null when the service does not return it. */
export interface SapPurchaseOrderHead {
  purchaseOrder: string;
  supplier: string;
  supplierName: string;
  companyCode: string;
  purchasingOrganization: string;
  currency: string;
}

export const getSapPurchaseOrder = (purchaseOrder: string) =>
  api.get<{
    purchaseOrder: string;
    configured: boolean;
    count: number;
    items: SapPurchaseOrderItem[];
    head?: SapPurchaseOrderHead | null;
  }>('/api/sap/purchase-order/' + encodeURIComponent(purchaseOrder.trim()));
