import { api, fetchBlobUrl } from './client';

/* Sales Order "Reading rules" (sql/39): where each field is printed on a customer's PO.
   Boxes are [ymin, xmin, ymax, xmax] on a 0-1000 scale of the page image. */

export type Box = [number, number, number, number];

export interface ReadRule {
  field: string;
  pageNo: number | null;
  box: Box | null;
  note: string | null;
  /** 'AUTO' = learned from posted POs; anything else = set by a person, stored as their role
   *  (Admin / Leader / Manager / user). Older rows may say 'CS'. */
  source: string;
  status: 'ACTIVE' | 'DISABLED';
  confirmCount: number;
  useCount: number;
  updatedBy: string | null;
  updatedAt: string;
}

export interface ReadHintsInfo {
  fields: string[];
  customerKey: string | null;
  customerTaxId: string | null;
  customerName: string;
  textCustomerKey: string | null;
  pageCount: number;
  fileAvailable: boolean;
  positions: Record<string, { image: number; box: Box }> | null;
  hintsUsed: string[];
  rules: ReadRule[];
}

export const READ_FIELD_LABELS: Record<string, string> = {
  docType: 'Document Type',
  poNo: 'PO No.',
  poDate: 'PO Date',
  quoteRef: 'Quote No.',
  customerName: 'Customer Name',
  customerTaxId: 'Customer Tax ID',
  customerAddress: 'Sold-to Address',
  shipToCode: 'Ship-to Code',
  shipToName: 'Ship-to Name',
  shipToAddress: 'Ship-to Address',
  deliveryDate: 'Delivery Date',
  currency: 'Currency',
  paymentTerms: 'Payment Terms',
  incoterms: 'Incoterms',
  remark: 'Remark',
  subTotal: 'Amount before Tax',
  vatAmount: 'VAT',
  totalAmount: 'Grand Total',
  linesTable: 'Item table',
};

const base = (docId: number) => `/api/documents/${docId}/read-hints`;

export const getReadHints = (docId: number) => api.get<ReadHintsInfo>(base(docId));

export const saveReadRule = (docId: number, field: string, body: { pageNo: number | null; box: Box | null; note: string }) =>
  api.put<ReadHintsInfo>(`${base(docId)}/${encodeURIComponent(field)}`, body);

export const setReadRuleActive = (docId: number, field: string, active: boolean) =>
  api.post<ReadHintsInfo>(`${base(docId)}/${encodeURIComponent(field)}/status`, { active });

export const deleteReadRule = (docId: number, field: string) =>
  api.del<ReadHintsInfo>(`${base(docId)}/${encodeURIComponent(field)}`);

/** Object URL of one rendered page (1-based). Caller revokes it. */
export const fetchPageImage = (docId: number, page: number) =>
  fetchBlobUrl(`/api/documents/${docId}/page-image/${page}`);
