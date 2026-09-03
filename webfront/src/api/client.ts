/* =====================================================================
   API client — typed fetch wrapper around the MgtOcr.Api backend.
   Ported from app.js's api()/API/guard(). In this React app the Vite dev
   server proxies /api -> http://localhost:8091 (see vite.config.ts), and in
   production MgtOcr.Api serves the built SPA same-origin, so paths are always
   relative ("/api/...") — no API_BASE juggling needed.
   ===================================================================== */

import { getMock, MOCK_ALWAYS } from './mocks';

export class ApiError extends Error {
  status: number;
  constructor(message: string, status: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

type Method = 'GET' | 'POST' | 'PUT' | 'DELETE';

async function request<T>(
  method: Method,
  url: string,
  body?: unknown,
  isForm = false,
): Promise<T> {
  // Mock mode (VITE_USE_MOCK=1): serve canned data, never touch the network.
  // Lets the whole UI run with no backend/DB. Falls through to a real request
  // only if no mock is defined for this endpoint.
  if (MOCK_ALWAYS) {
    const m = getMock(method, url, body);
    if (m !== undefined) return m as T;
  }

  const opt: RequestInit = { method, headers: {} };
  if (body !== undefined) {
    if (isForm) {
      opt.body = body as BodyInit;
    } else {
      (opt.headers as Record<string, string>)['Content-Type'] = 'application/json';
      opt.body = JSON.stringify(body);
    }
  }

  let r: Response;
  try {
    r = await fetch(url, opt);
  } catch (netErr) {
    // Backend unreachable — fall back to mock data when available.
    const m = getMock(method, url, body);
    if (m !== undefined) return m as T;
    throw netErr;
  }
  const txt = await r.text();
  let data: unknown = null;
  try {
    data = txt ? JSON.parse(txt) : null;
  } catch {
    // Backend mirrors FastAPI: unhandled errors come back as plain-text bodies.
    data = { detail: txt };
  }

  if (!r.ok) {
    const detail =
      (data as { detail?: string; error?: string })?.detail ||
      (data as { error?: string })?.error ||
      `HTTP ${r.status}`;
    throw new ApiError(detail, r.status);
  }
  return data as T;
}

export const api = {
  get: <T>(u: string) => request<T>('GET', u),
  post: <T>(u: string, b?: unknown) => request<T>('POST', u, b ?? {}),
  put: <T>(u: string, b?: unknown) => request<T>('PUT', u, b),
  del: <T>(u: string) => request<T>('DELETE', u),
  upload: <T>(u: string, fd: FormData) => request<T>('POST', u, fd, true),
};
