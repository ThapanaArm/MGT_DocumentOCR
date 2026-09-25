import { AUTH_ENABLED as MS_ENABLED, getAccessToken, getAccount, signOut as msalSignOut } from '../auth/msal';

/* =====================================================================
   Password login (OCR-issued token) + the one place that hands out the
   Bearer token for every API call.

   "Remember me" chooses the storage: localStorage survives a browser
   restart; sessionStorage is cleared when the tab closes.
   ===================================================================== */

const KEY = 'mgtocr.local';

interface Stored {
  token: string;
  expiresAt: string; // ISO
}

function safeLocal(): Storage | null {
  try { return window.localStorage; } catch { return null; }
}
function safeSession(): Storage | null {
  try { return window.sessionStorage; } catch { return null; }
}

function parse(raw: string | null): Stored | null {
  if (!raw) return null;
  try {
    const s = JSON.parse(raw) as Stored;
    if (!s?.token || !s?.expiresAt) return null;
    if (new Date(s.expiresAt).getTime() <= Date.now()) return null; // expired locally
    return s;
  } catch {
    return null;
  }
}

function read(): Stored | null {
  for (const store of [safeLocal(), safeSession()]) {
    if (!store) continue;
    try {
      const s = parse(store.getItem(KEY));
      if (s) return s;
      store.removeItem(KEY); // clear expired/garbage
    } catch {
      /* ignore */
    }
  }
  return null;
}

export function getLocalToken(): string | null {
  return read()?.token ?? null;
}

export function hasLocalSession(): boolean {
  return read() !== null;
}

export function clearLocalToken(): void {
  try { safeLocal()?.removeItem(KEY); } catch { /* ignore */ }
  try { safeSession()?.removeItem(KEY); } catch { /* ignore */ }
}

/* ---------------------------------------------------------------------
   One device per user (backend: SingleSession.cs, sql/26_user_session.sql).
   This browser's session id lives in localStorage, so every tab of the same
   browser shares it (many tabs = fine). It is sent on every API call as
   X-Session-Id; signing in claims it as the user's ONLY active device, and
   the previously active device is signed out on its next call.
   --------------------------------------------------------------------- */
const SESSION_KEY = 'mgtocr.sessionId';
const SIGNOUT_REASON_KEY = 'mgtocr.signoutReason';
let memSessionId: string | null = null; // fallback when storage is blocked

function newId(): string {
  try {
    if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) return crypto.randomUUID();
  } catch { /* fall through */ }
  const b = new Uint8Array(16);
  crypto.getRandomValues(b);
  return Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
}

export function getSessionId(): string {
  try {
    const store = safeLocal();
    let id = store?.getItem(SESSION_KEY) ?? null;
    if (!id) {
      id = memSessionId ?? newId();
      store?.setItem(SESSION_KEY, id);
    }
    memSessionId = id;
    return id;
  } catch {
    memSessionId ??= newId();
    return memSessionId;
  }
}

/** Make THIS browser the signed-in user's only active device. Call right after a fresh sign-in. */
export async function claimSession(): Promise<void> {
  const token = await getAuthToken();
  const headers: Record<string, string> = { 'X-Session-Id': getSessionId() };
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const r = await fetch('/api/session/claim', { method: 'POST', headers });
  if (!r.ok) throw new Error(`Could not start session (HTTP ${r.status})`);
}

/** Why the last automatic sign-out happened (shown once on the sign-in screen). */
export function takeSignOutReason(): string | null {
  try {
    const v = safeSession()?.getItem(SIGNOUT_REASON_KEY) ?? null;
    safeSession()?.removeItem(SIGNOUT_REASON_KEY);
    return v;
  } catch {
    return null;
  }
}

/** Sign this browser out because the server rejected its session (e.g. signed in elsewhere).
 *  Local only — does not sign the person out of Microsoft 365 — and blocks silent re-sign-in, so
 *  the two devices don't keep kicking each other. */
export async function forceSignOut(message: string): Promise<void> {
  try { safeSession()?.setItem(SIGNOUT_REASON_KEY, message); } catch { /* ignore */ }
  clearLocalToken();
  if (MS_ENABLED && getAccount()) {
    try { await msalSignOut(); } catch { /* ignore */ }
  }
  window.dispatchEvent(new Event('mgtocr:signedout'));
}

export async function loginWithPassword(username: string, password: string, remember = true): Promise<void> {
  const r = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  });
  if (!r.ok) {
    let detail = 'Sign-in failed';
    try {
      const b = (await r.json()) as { detail?: string };
      if (b?.detail) detail = b.detail;
    } catch {
      /* ignore */
    }
    throw new Error(detail);
  }
  const data = (await r.json()) as { token: string; expiresAt: string };
  const value = JSON.stringify({ token: data.token, expiresAt: data.expiresAt });
  const keep = remember ? safeLocal() : safeSession();
  const drop = remember ? safeSession() : safeLocal();
  try { keep?.setItem(KEY, value); } catch { /* ignore */ }
  try { drop?.removeItem(KEY); } catch { /* ignore */ }
  await claimSession(); // this device becomes the only active one
}

// Bearer for API calls: password session wins; otherwise the Microsoft token.
export async function getAuthToken(): Promise<string | null> {
  return getLocalToken() ?? (await getAccessToken());
}

// One sign-out for both worlds.
export async function signOut(): Promise<void> {
  clearLocalToken();
  if (MS_ENABLED && getAccount()) {
    await msalSignOut();
  }
  window.dispatchEvent(new Event('mgtocr:signedout'));
}
