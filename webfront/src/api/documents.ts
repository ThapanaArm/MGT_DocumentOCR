import { api } from './client';

/* Documents API — ports the /api/documents/* and related endpoints used by
   the work/document/inbox screens. The document object is large and dynamic
   (mirrors the Python/JS dict), so header/line are loosely typed. */

export interface DocLine {
  itemNo: number;
  extCode: string;
  desc: string;
  qty: number | string;
  uom: string;
  price: number | string;
  amount: number | string;
  materialCode?: string;
  extra?: Record<string, string>;
  [k: string]: unknown;
}

export type DocHeader = Record<string, any>;

export interface DocModel {
  docId: number;
  module: string;
  header: DocHeader;
  lines: DocLine[];
  status: string;
  provider?: string;
  confidence?: number;
  confidenceNote?: string;
  ocrNote?: string;
  fileName?: string;
  tokensIn?: number | null;
  tokensOut?: number | null;
  cost?: number | null;
  costIn?: number | null;
  costOut?: number | null;
  costCurrency?: string;
  apDocCategory?: string;
  partnerCode?: string;
  sapDocNo?: string;
  postedAt?: string;
  sourceDocId?: number;
  splitChildren?: Array<Record<string, any>>;
  mapStatus?: string;
  [k: string]: unknown;
}

export interface MapField {
  label: string;
  value: string;
  match?: boolean | null;
}
export interface MapEntry {
  status: string;
  code?: string;
  method?: string;
  sapCode?: string;
  text?: string;
  doc?: MapField[];
  sap?: MapField[];
  unit?: { doc: MapField[]; sap: MapField[]; status: string };
  uom?: {
    status: string;
    sapQty?: number;
    sapUom?: string;
    factor?: number;
    method?: string;
  };
  [k: string]: unknown;
}
export interface MapResult {
  document: DocModel;
  pass: boolean;
  errors: Array<{ field: string; msg: string; fix: string }>;
  warns: string[];
  header: Record<string, MapEntry>;
  lines: MapEntry[];
}

export interface ChatMessage {
  role: 'user' | 'assistant';
  text?: string;
  image?: string;
  hasImage?: boolean;
  chatId?: number;
}

export const uploadDocument = (fd: FormData) =>
  api.upload<DocModel>('/api/documents/upload', fd);

export const sampleDocument = (body: {
  module: string;
  index: number;
  user: string;
  apDocCategory?: string;
}) => api.post<DocModel>('/api/documents/sample', body);

export const getDocument = (id: number) => api.get<DocModel>('/api/documents/' + id);

export const reocrDocument = (id: number, ocr: string, user: string) =>
  api.post<DocModel>('/api/documents/' + id + '/reocr', { ocr, user });

export const getRawText = (id: number) =>
  api.get<{ text: string }>('/api/documents/' + id + '/rawtext');

export const mapDocument = (
  id: number,
  body: { header: DocHeader; lines: DocLine[]; manual: unknown; user: string },
) => api.post<MapResult>('/api/documents/' + id + '/map', body);

export const learnMaterial = (
  id: number,
  body: { partnerCode?: string; extCode: string; extDesc: string; materialCode: string },
) => api.post('/api/documents/' + id + '/learn', body);

export const getPayload = (id: number) =>
  api.get<{ payload: Record<string, any> }>('/api/documents/' + id + '/payload');

export const postToSap = (id: number, user: string) =>
  api.post<{ document: DocModel; simulated: boolean; sapDocNo: string }>(
    '/api/documents/' + id + '/post',
    { user },
  );

export const splitDocument = (id: number, assign: Record<string, number>, user: string) =>
  api.post<{ source: DocModel; created: unknown[] }>('/api/documents/' + id + '/split', {
    assign,
    user,
  });

export const setDocCategory = (id: number, apDocCategory: string, user: string) =>
  api.post<DocModel>('/api/documents/' + id + '/category', { apDocCategory, user });

export const getChat = (id: number) =>
  api.get<ChatMessage[]>('/api/documents/' + id + '/chat');

export const chatFix = (
  id: number,
  body: { message: string; image: string | null; user: string; provider: string },
) => api.post<{ document: DocModel }>('/api/documents/' + id + '/chat-fix', body);

export interface InboxRow {
  DocId: number;
  Module: string;
  FileName: string | null;
  DocNo: string | null;
  DocDate: string | null;
  PartnerName: string | null;
  TotalAmount: number | null;
  Status: string;
  ApDocCategory: string | null;
  OcrConfidence: number | null;
  OcrConfidenceNote: string | null;
  OcrProvider: string | null;
  OcrTokensIn: number | null;
  OcrTokensOut: number | null;
  OcrCost: number | null;
  OcrInputCost: number | null;
  OcrOutputCost: number | null;
  OcrCostCurrency: string | null;
  SapDocNo: string | null;
  CreatedAt: string | null;
  [k: string]: unknown;
}

export const listDocuments = (module?: string | null, apDocCategory?: string) => {
  let url = '/api/documents?limit=5000';
  if (module) url += '&module=' + module;
  if (module === 'AP' && apDocCategory) url += '&apDocCategory=' + apDocCategory;
  return api.get<InboxRow[]>(url);
};

export const deleteDocument = (id: number, user: string) =>
  api.del('/api/documents/' + id + '?user=' + encodeURIComponent(user));
