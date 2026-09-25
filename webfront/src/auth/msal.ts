import {
  PublicClientApplication,
  InteractionRequiredAuthError,
  type AccountInfo,
} from '@azure/msal-browser';

/* =====================================================================
   Microsoft Entra ID sign-in — multi-registration.

   MGT and GLC are in SEPARATE Entra tenants, each with its OWN single-tenant
   app registration (not one shared multi-tenant app). Both companies use the
   SAME app URL, so this module holds one MSAL PublicClientApplication PER
   company (keyed by clientId — MSAL namespaces its localStorage cache by
   clientId, so the two never collide), and the person picks their company on
   the sign-in screen. The chosen company is remembered so silent sign-in and
   token refresh keep using the right registration.

   The backend already accepts either registration: AzureAd:Tenants lists each
   tenant's TenantId + ClientId, and AuthExtensions validates the issuer and
   audience per tenant. Ms_User is still the real gate (unknown e-mail = 403).

   Backward compatible: with only VITE_AZURE_CLIENT_ID set (no GLC vars), there
   is exactly one company and the flow is identical to the single-registration
   version — one "Continue with Microsoft" button, one instance.

   Deliberately plain @azure/msal-browser (not the React bindings): the fetch
   wrapper in api/client.ts is not a component and still needs a token, so the
   instances live in a module both layers can reach.
   ===================================================================== */

export interface AuthCompany {
  id: string;
  label: string;
  clientId: string;
  authority: string;
  apiScope: string;
}

// Build a company from its env vars, or null when its client id isn't set (so it's simply absent).
// authority: a single-tenant app needs its OWN tenant authority (…/login.microsoftonline.com/<tenantId>);
// "organizations" is the multi-tenant default and only works for a multi-tenant registration.
function envCompany(
  id: string,
  label: string,
  clientId: string | undefined,
  authorityEnv: string | undefined,
  scopeEnv: string | undefined,
): AuthCompany | null {
  const cid = (clientId ?? '').trim();
  if (!cid) return null;
  const authority = (authorityEnv || 'https://login.microsoftonline.com/organizations').trim();
  const apiScope = (scopeEnv || `api://${cid}/access_as_user`).trim();
  return { id, label, clientId: cid, authority, apiScope };
}

// The configured companies, in display order. MGT uses the original VITE_AZURE_* vars (unchanged);
// GLC uses the _GLC-suffixed vars. Add more here if the group ever grows.
export const COMPANIES: AuthCompany[] = [
  envCompany(
    'MGT',
    (import.meta.env.VITE_AZURE_COMPANY_LABEL as string) || 'MGT',
    import.meta.env.VITE_AZURE_CLIENT_ID as string,
    import.meta.env.VITE_AZURE_AUTHORITY as string,
    import.meta.env.VITE_AZURE_API_SCOPE as string,
  ),
  envCompany(
    'GLC',
    (import.meta.env.VITE_AZURE_COMPANY_LABEL_GLC as string) || 'GLC',
    import.meta.env.VITE_AZURE_CLIENT_ID_GLC as string,
    import.meta.env.VITE_AZURE_AUTHORITY_GLC as string,
    import.meta.env.VITE_AZURE_API_SCOPE_GLC as string,
  ),
].filter((c): c is AuthCompany => c !== null);

// No company configured = the Entra app registrations don't exist yet, so the app runs exactly as it
// did before sign-in was added (no login screen, no Authorization header). The backend has the
// matching switch (blank AzureAd:Tenants, Development only), so the two halves cannot disagree.
export const AUTH_ENABLED = COMPANIES.length > 0;

// App-local sign-out must not sign the person out of Microsoft 365 in the browser. Remember the
// explicit choice so a later page refresh does not immediately ssoSilent them back into OCR.
const MANUAL_SIGN_OUT_KEY = 'mgtocr.microsoft.manuallySignedOut';
// Which company the person signed in with — so a refresh restores the RIGHT registration.
const ACTIVE_COMPANY_KEY = 'mgtocr.microsoft.activeCompany';

const wasManuallySignedOut = () => {
  try { return window.localStorage.getItem(MANUAL_SIGN_OUT_KEY) === '1'; } catch { return false; }
};
const markManuallySignedOut = (value: boolean) => {
  try {
    if (value) window.localStorage.setItem(MANUAL_SIGN_OUT_KEY, '1');
    else window.localStorage.removeItem(MANUAL_SIGN_OUT_KEY);
  } catch { /* private mode/storage disabled: sign-out still works for the current page */ }
};

function readActiveCompany(): AuthCompany | null {
  if (COMPANIES.length === 0) return null;
  if (COMPANIES.length === 1) return COMPANIES[0];
  try {
    const id = window.localStorage.getItem(ACTIVE_COMPANY_KEY);
    const found = COMPANIES.find((c) => c.id === id);
    if (found) return found;
  } catch { /* fall through */ }
  // No explicit choice yet: default to the first company so an already-signed-in MGT user is still
  // restored after this multi-company change was deployed. A fresh user of another company just has
  // no account under this default and lands on the picker.
  return COMPANIES[0];
}
function writeActiveCompany(id: string | null) {
  try {
    if (id) window.localStorage.setItem(ACTIVE_COMPANY_KEY, id);
    else window.localStorage.removeItem(ACTIVE_COMPANY_KEY);
  } catch { /* ignore */ }
}

// One MSAL instance per company, created (and initialized) lazily.
const instances = new Map<string, PublicClientApplication>();
const initialized = new Set<string>();

function instanceFor(c: AuthCompany): PublicClientApplication {
  let inst = instances.get(c.id);
  if (!inst) {
    inst = new PublicClientApplication({
      auth: {
        clientId: c.clientId,
        authority: c.authority,
        // Whatever origin the app is served from — must be a SPA redirect URI in EACH company's app
        // registration or Entra refuses to return the token.
        redirectUri: window.location.origin,
        postLogoutRedirectUri: window.location.origin,
      },
      cache: { cacheLocation: 'localStorage', storeAuthStateInCookie: false },
    });
    instances.set(c.id, inst);
  }
  return inst;
}

async function ensureInit(c: AuthCompany): Promise<PublicClientApplication> {
  const inst = instanceFor(c);
  if (!initialized.has(c.id)) {
    await inst.initialize();
    initialized.add(c.id);
  }
  return inst;
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

// MSAL v3+ must be initialised before use, and the redirect coming back from Microsoft has to be
// handled on the SAME instance that started it — which is the remembered active company (signIn saves
// it before redirecting). Only that instance is touched here; others init lazily when chosen.
export function initAuth(): Promise<void> {
  if (!AUTH_ENABLED) return Promise.resolve();
  ready ??= (async () => {
    const c = readActiveCompany();
    if (!c) return;
    const inst = await ensureInit(c);
    const redirectResult = await inst.handleRedirectPromise();
    if (redirectResult?.account) {
      markManuallySignedOut(false);
      freshSignIn = true;
    }
    const accounts = inst.getAllAccounts();
    if (accounts.length > 0 && !inst.getActiveAccount()) inst.setActiveAccount(accounts[0]);
  })();
  return ready;
}

// Best-effort silent sign-in against the remembered company's Microsoft 365 session. Runs in the
// BACKGROUND after the form is visible — never blocked on.
export async function attemptSilentSignIn(): Promise<boolean> {
  if (!AUTH_ENABLED) return false;
  if (wasManuallySignedOut()) return false;
  const c = readActiveCompany();
  if (!c) return false;
  const inst = await ensureInit(c);
  if (inst.getActiveAccount()) return true;
  try {
    const result = await inst.ssoSilent({ scopes: [c.apiScope] });
    if (result.account) {
      inst.setActiveAccount(result.account);
      freshSignIn = true;
      return true;
    }
  } catch { /* no silent session — the sign-in screen handles it */ }
  return false;
}

export function getAccount(): AccountInfo | null {
  if (!AUTH_ENABLED) return null;
  const c = readActiveCompany();
  if (!c) return null;
  return instanceFor(c).getActiveAccount();
}

// Returns null when sign-in is off, no active account, or a redirect had to be started (page is on
// its way to Microsoft). Uses the active company's instance + its own api scope.
export async function getAccessToken(): Promise<string | null> {
  if (!AUTH_ENABLED) return null;
  const c = readActiveCompany();
  if (!c) return null;
  const inst = await ensureInit(c);
  const account = inst.getActiveAccount();
  if (!account) return null;
  try {
    const result = await inst.acquireTokenSilent({ account, scopes: [c.apiScope] });
    return result.accessToken;
  } catch (err) {
    if (err instanceof InteractionRequiredAuthError) {
      await inst.acquireTokenRedirect({ account, scopes: [c.apiScope] });
      return null;
    }
    throw err;
  }
}

// Start an interactive sign-in for a specific company. companyId is required when more than one
// company is configured (the picker passes it); with a single company it may be omitted.
export async function signIn(companyId?: string): Promise<void> {
  if (!AUTH_ENABLED) return;
  const c = companyId
    ? COMPANIES.find((x) => x.id === companyId)
    : (COMPANIES.length === 1 ? COMPANIES[0] : readActiveCompany());
  if (!c) return; // multi-company and none chosen — the caller must pass a companyId
  writeActiveCompany(c.id);
  markManuallySignedOut(false);
  const inst = await ensureInit(c);
  await inst.loginRedirect({ scopes: [c.apiScope] });
}

export async function signOut(): Promise<void> {
  if (!AUTH_ENABLED) return;
  markManuallySignedOut(true);
  const c = readActiveCompany();
  if (c) {
    const inst = instanceFor(c);
    const account = inst.getActiveAccount() ?? undefined;
    await inst.clearCache({ account });
    inst.setActiveAccount(null);
  }
  writeActiveCompany(null);
}
