/* =====================================================================
   API client — typed fetch wrapper around the MgtOcr.Api backend.
   Ported from app.js's api()/API/guard(). In this React app the Vite dev
   server proxies /api -> http://localhost:8091 (see vite.config.ts), and in
   production MgtOcr.Api serves the built SPA same-origin, so paths are always
   relative ("/api/...") — no API_BASE juggling needed.
   ===================================================================== */

import { getMock, MOCK_ALWAYS } from './mocks';
import { clearLocalToken, forceSignOut, getAuthToken, getSessionId } from './auth';

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

  // Every call carries a Bearer token — a password session if there is one, otherwise the Microsoft
  // token (renewed silently near expiry). Null only when truly signed out.
  const token = await getAuthToken();
  if (token) (opt.headers as Record<string, string>)['Authorization'] = `Bearer ${token}`;
  (opt.headers as Record<string, string>)['X-Session-Id'] = getSessionId();

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

  // 401 = token missing/expired/rejected (either kind) — the session is gone, not the request wrong.
  // Drop any password session and tell the gate to show sign-in again. 403 is different (signed in,
  // but not in Ms_User) and is left to surface as text for the person to read.
  if (r.status === 401) {
    const code = (data as { code?: string })?.code;
    if (code === 'SESSION_REPLACED' || code === 'SESSION_MISSING') {
      // Signed in on another device (one device per user) — sign this browser out for real.
      await forceSignOut((data as { detail?: string })?.detail || 'You have been signed out.');
    } else {
      clearLocalToken();
      window.dispatchEvent(new Event('mgtocr:signedout'));
    }
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

/** Fetch a protected binary endpoint (image/PDF) WITH the Bearer token and return an object URL.
 *  A plain <img src="/api/..."> can't send the Authorization header, so it gets 401 from the
 *  backend's auth fallback policy. Caller must URL.revokeObjectURL() the result when done. */
export async function fetchBlobUrl(url: string): Promise<string> {
  const token = await getAuthToken();
  const headers: Record<string, string> = { 'X-Session-Id': getSessionId() };
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const r = await fetch(url, { headers });
  if (!r.ok) throw new ApiError(`HTTP ${r.status}`, r.status);
  return URL.createObjectURL(await r.blob());
}
