import {
  PublicClientApplication,
  InteractionRequiredAuthError,
  type AccountInfo,
} from '@azure/msal-browser';

/* =====================================================================
   Microsoft Entra ID sign-in — ONE multi-tenant app registration (MGT-OCR-SSO).

   MGT and GLC sit in separate Entra tenants but use the SAME app registration
   (multi-tenant, admin consent granted in the GLC tenant). The authority is
   "organizations", so a person signs in with whichever work account they pick and
   Entra routes them to their own tenant. There is no company picker any more.

   The backend is the gate: it accepts a token only when its tenant id (tid) is in
   AzureAd:Tenants, maps that tenant to a CompanyID, and requires the e-mail to exist
   in Ms_User AND Ms_UserCompany for that company (otherwise 403).

   Because the authority is "organizations", a silent sign-in needs a hint about WHO to
   sign in, so the last account's username is remembered and passed as loginHint. Token
   requests use the account's own tenant authority so the right cached token is found.

   Deliberately plain @azure/msal-browser (not the React bindings): the fetch wrapper in
   api/client.ts is not a component and still needs a token.
   ===================================================================== */

export interface AuthCompany {
  id: string;
  label: string;
  clientId: string;
  authority: string;
  apiScope: string;
}

const clientId = ((import.meta.env.VITE_AZURE_CLIENT_ID as string) || '').trim();
const authority = ((import.meta.env.VITE_AZURE_AUTHORITY as string) || 'https://login.microsoftonline.com/organizations').trim();
const apiScope = ((import.meta.env.VITE_AZURE_API_SCOPE as string) || `api://${clientId}/access_as_user`).trim();

// Kept as a one-element list so the sign-in screen renders its single "Continue with Microsoft" button.
export const COMPANIES: AuthCompany[] = clientId
  ? [{ id: 'MS', label: 'Microsoft', clientId, authority, apiScope }]
  : [];

// No client id = the app registration isn't configured, so the app runs exactly as it did before
// sign-in existed. The backend has the matching switch (blank AzureAd:Tenants, Development only).
export const AUTH_ENABLED = COMPANIES.length > 0;

// The sign-in screen (AuthGate) asks which company a typed name belongs to before redirecting.
// With the one multi-tenant registration there is nothing to choose: every name goes to it, and
// the backend's AzureAd:Tenants decides from the token's tenant which company the person is in.
export function companyForLogin(_input: string): AuthCompany | null {
  return COMPANIES[0] ?? null;
}

// App-local sign-out must not sign the person out of Microsoft 365 in the browser. Remember the
// explicit choice so a later page refresh does not immediately ssoSilent them back into OCR.
const MANUAL_SIGN_OUT_KEY = 'mgtocr.microsoft.manuallySignedOut';
// Last signed-in account (username), used as the ssoSilent loginHint on the next visit.
const LAST_HINT_KEY = 'mgtocr.microsoft.lastLoginHint';

const readLS = (k: string): string | null => {
  try { return window.localStorage.getItem(k); } catch { return null; }
};
const writeLS = (k: string, v: string | null) => {
  try {
    if (v === null) window.localStorage.removeItem(k);
    else window.localStorage.setItem(k, v);
  } catch { /* private mode/storage disabled: the current page still works */ }
};
const wasManuallySignedOut = () => readLS(MANUAL_SIGN_OUT_KEY) === '1';
const markManuallySignedOut = (value: boolean) => writeLS(MANUAL_SIGN_OUT_KEY, value ? '1' : null);

let instance: PublicClientApplication | null = null;
let initialized = false;

function getInstance(): PublicClientApplication {
  instance ??= new PublicClientApplication({
    auth: {
      clientId,
      authority,
      // Whatever origin the app is served from — must be a SPA redirect URI on the app registration.
      redirectUri: window.location.origin,
      postLogoutRedirectUri: window.location.origin,
      navigateToLoginRequestUrl: false,
    },
    cache: { cacheLocation: 'localStorage', storeAuthStateInCookie: false },
  });
  return instance;
}

async function ensureInit(): Promise<PublicClientApplication> {
  const inst = getInstance();
  if (!initialized) {
    await inst.initialize();
    initialized = true;
  }
  return inst;
}

// The account's own tenant authority — so the token lookup/refresh targets the right tenant when the
// cache holds accounts from more than one (e.g. a MGT and a GLC account on the same browser).
const tenantAuthority = (a: AccountInfo) => `https://login.microsoftonline.com/${a.tenantId}`;

// Remember who signed in, and make them the active account.
function remember(inst: PublicClientApplication, account: AccountInfo) {
  inst.setActiveAccount(account);
  writeLS(LAST_HINT_KEY, account.username);
}

// Prefer the account matching the last hint, then the active one, then the first.
function pickAccount(inst: PublicClientApplication): AccountInfo | null {
  const accounts = inst.getAllAccounts();
  if (accounts.length === 0) return null;
  const hint = readLS(LAST_HINT_KEY)?.toLowerCase();
  return (hint ? accounts.find((a) => a.username.toLowerCase() === hint) : undefined)
    ?? inst.getActiveAccount() ?? accounts[0];
}

let ready: Promise<void> | null = null;

// True once per fresh Microsoft sign-in (redirect back from Microsoft, or silent SSO on a device that
// had no session) — AuthGate uses it to claim this device as the user's only active one.
let freshSignIn = false;
export function consumeFreshSignIn(): boolean {
  const v = freshSignIn;
  freshSignIn = false;
  return v;
}

// MSAL v3+ must be initialised before use, and the redirect coming back from Microsoft has to be handled.
export function initAuth(): Promise<void> {
  if (!AUTH_ENABLED) return Promise.resolve();
  ready ??= (async () => {
    const inst = await ensureInit();
    const redirectResult = await inst.handleRedirectPromise();
    if (redirectResult?.account) {
      remember(inst, redirectResult.account);
      markManuallySignedOut(false);
      freshSignIn = true;
    } else {
      const account = pickAccount(inst);
      if (account && !inst.getActiveAccount()) inst.setActiveAccount(account);
    }
  })();
  return ready;
}

// Best-effort silent sign-in. Runs in the BACKGROUND after the form is visible — never blocked on.
// Any failure (no session, third-party cookies blocked, iframe timeout) just leaves the sign-in screen.
export async function attemptSilentSignIn(): Promise<boolean> {
  if (!AUTH_ENABLED) return false;
  if (wasManuallySignedOut()) return false;
  try {
    const inst = await ensureInit();
    if (inst.getActiveAccount()) return true;

    // 1) an account is already cached: refresh a token for it
    const cached = pickAccount(inst);
    if (cached) {
      try {
        await inst.acquireTokenSilent({ account: cached, scopes: [apiScope], authority: tenantAuthority(cached) });
        remember(inst, cached);
        return true;
      } catch (err) {
        if (!(err instanceof InteractionRequiredAuthError)) console.warn('acquireTokenSilent failed', err);
      }
    }

    // 2) nothing cached, but we know who signed in last time: ssoSilent needs that hint on /organizations
    const hint = readLS(LAST_HINT_KEY);
    if (hint) {
      const result = await inst.ssoSilent({ scopes: [apiScope], loginHint: hint });
      if (result.account) {
        remember(inst, result.account);
        freshSignIn = true;
        return true;
      }
    }
  } catch { /* no silent session — the sign-in screen handles it */ }
  return false;
}

export function getAccount(): AccountInfo | null {
  if (!AUTH_ENABLED || !instance) return null;
  return instance.getActiveAccount();
}

// Returns null when sign-in is off, no active account, or a redirect had to be started (page is on
// its way to Microsoft).
export async function getAccessToken(): Promise<string | null> {
  if (!AUTH_ENABLED) return null;
  const inst = await ensureInit();
  const account = inst.getActiveAccount();
  if (!account) return null;
  const request = { account, scopes: [apiScope], authority: tenantAuthority(account) };
  try {
    const result = await inst.acquireTokenSilent(request);
    return result.accessToken;
  } catch (err) {
    if (err instanceof InteractionRequiredAuthError) {
      await inst.acquireTokenRedirect(request);
      return null;
    }
    throw err;
  }
}

// Interactive sign-in. The account picker is forced: one browser may hold both an MGT and a GLC account.
// (The companyId argument is kept only so the existing sign-in screen compiles; it is ignored.)
// loginHint: the e-mail typed on the sign-in screen, used only to pre-fill the Microsoft page; the
// account picker is still shown.
export async function signIn(_companyId?: string, loginHint?: string): Promise<void> {
  if (!AUTH_ENABLED) return;
  markManuallySignedOut(false);
  const inst = await ensureInit();
  const hint = (loginHint ?? '').trim();
  await inst.loginRedirect(
    hint.includes('@')
      ? { scopes: [apiScope], prompt: 'select_account', loginHint: hint }
      : { scopes: [apiScope], prompt: 'select_account' });
}

export async function signOut(): Promise<void> {
  if (!AUTH_ENABLED) return;
  markManuallySignedOut(true);
  // Forget the hint too, otherwise the next visit would silently sign straight back in.
  writeLS(LAST_HINT_KEY, null);
  const inst = await ensureInit();
  const account = inst.getActiveAccount() ?? undefined;
  await inst.clearCache({ account });
  inst.setActiveAccount(null);
}
