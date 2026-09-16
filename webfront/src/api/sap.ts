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
  top?: number;
}

export const searchSapBusinessPartner = ({ name, taxId, top = 10 }: SapBusinessPartnerSearchParams) => {
  const qs = new URLSearchParams();
  if (taxId) qs.set('taxId', taxId);
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

export const findSapPartnerFunctions = (soldToSapCode: string, partnerFunction?: string, top = 50) => {
  const qs = new URLSearchParams();
  if (partnerFunction) qs.set('function', partnerFunction);
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

export const searchSapMaterials = (description: string, top = 30) => {
  const qs = new URLSearchParams({ description, top: String(top) });
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
