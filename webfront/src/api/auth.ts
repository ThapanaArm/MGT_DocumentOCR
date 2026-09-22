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
