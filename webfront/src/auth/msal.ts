import {
  PublicClientApplication,
  InteractionRequiredAuthError,
  type AccountInfo,
  type Configuration,
} from '@azure/msal-browser';

/* =====================================================================
   Microsoft Entra ID sign-in.

   Deliberately plain @azure/msal-browser rather than the React bindings: the
   fetch wrapper in api/client.ts is not a component and still needs a token,
   so the instance lives in a module both layers can reach.
   ===================================================================== */

const clientId = (import.meta.env.VITE_AZURE_CLIENT_ID ?? '').trim();

// No client id = the Entra app registration does not exist yet, so the app runs exactly as it did
// before sign-in was added: no login screen, no Authorization header. The backend has the matching
// switch (blank AzureAd:Tenants, Development only), so the two halves cannot disagree about
// whether sign-in is on — and the backend refuses to start in that state outside Development.
export const AUTH_ENABLED = clientId.length > 0;

// "organizations" accepts a work account from ANY Microsoft tenant. That is not as loose as it
// reads: the backend only honours tokens from the tenants listed in AzureAd:Tenants, and then only
// for addresses with an active row in Ms_User. Keeping it generic here means the group's second
// company — expected to have its own tenant — needs no frontend change at all.
const authority = (import.meta.env.VITE_AZURE_AUTHORITY ||
  'https://login.microsoftonline.com/organizations').trim();

// Must match the scope exposed by the app registration ("Expose an API" -> access_as_user).
const apiScope = (import.meta.env.VITE_AZURE_API_SCOPE ||
  (clientId ? `api://${clientId}/access_as_user` : '')).trim();

const config: Configuration = {
  auth: {
    clientId,
    authority,
    // Whatever origin the app is served from — :5173 in dev, the real host in production. Both must
    // be listed as SPA redirect URIs in the app registration or Entra refuses to return the token.
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,
  },
  cache: {
    // localStorage (not sessionStorage): the signed-in account survives closing the browser, so a
    // returning user is signed in silently — no button, no typing. That is the "convenience" a
    // username/password door was floated for, delivered by SSO instead and without a second, weaker
    // way in. Trade-off: tokens sit in localStorage, so an XSS bug could read them — acceptable for
    // an internal line-of-business app with no third-party script surface; revisit if that changes.
    cacheLocation: 'localStorage',
    storeAuthStateInCookie: false,
  },
};

export const msalInstance = new PublicClientApplication(config);

let ready: Promise<void> | null = null;

// MSAL v3+ must be initialised before use, and the redirect coming back from Microsoft has to be
// handled before any account is readable. Both happen once, here.
export function initAuth(): Promise<void> {
  if (!AUTH_ENABLED) return Promise.resolve();
  // Fast and bounded: initialise, finish any redirect coming back from Microsoft, and adopt a
  // remembered account. Deliberately does NOT attempt ssoSilent here — that talks to Microsoft
  // through a hidden iframe and, when the app registration or session is not ready, only settles
  // after MSAL's ~10s iframe timeout, which would freeze the sign-in screen on "Checking sign-in…".
  // The silent attempt is a separate, background call (attemptSilentSignIn) the gate never waits on.
  ready ??= msalInstance.initialize().then(async () => {
    await msalInstance.handleRedirectPromise();
    const accounts = msalInstance.getAllAccounts();
    if (accounts.length > 0 && !msalInstance.getActiveAccount()) {
      msalInstance.setActiveAccount(accounts[0]);
    }
  });
  return ready;
}

// Best-effort silent sign-in using an existing Microsoft 365 session (Outlook/Teams open). Run in
// the BACKGROUND after the form is already visible — never block on it. Returns true if it adopted
// an account. When SSO is off, or no session exists, it just returns false.
export async function attemptSilentSignIn(): Promise<boolean> {
  if (!AUTH_ENABLED) return false;
  if (msalInstance.getActiveAccount()) return true;
  try {
    const result = await msalInstance.ssoSilent({ scopes: [apiScope] });
    if (result.account) {
      msalInstance.setActiveAccount(result.account);
      return true;
    }
  } catch {
    /* no silent session — the sign-in screen handles it */
  }
  return false;
}

export function getAccount(): AccountInfo | null {
  return AUTH_ENABLED ? msalInstance.getActiveAccount() : null;
}

// Returns null when sign-in is switched off, or when a redirect had to be started (in which case
// the page is on its way to Microsoft and this request no longer matters).
export async function getAccessToken(): Promise<string | null> {
  if (!AUTH_ENABLED) return null;
  const account = msalInstance.getActiveAccount();
  if (!account) return null;
  try {
    const result = await msalInstance.acquireTokenSilent({ account, scopes: [apiScope] });
    return result.accessToken;
  } catch (err) {
    // Consent revoked, MFA required, refresh token expired — anything that genuinely needs the
    // person. Everything else is a real error and is left to bubble up rather than swallowed.
    if (err instanceof InteractionRequiredAuthError) {
      await msalInstance.acquireTokenRedirect({ account, scopes: [apiScope] });
      return null;
    }
    throw err;
  }
}

export async function signIn(): Promise<void> {
  if (AUTH_ENABLED) await msalInstance.loginRedirect({ scopes: [apiScope] });
}

export async function signOut(): Promise<void> {
  if (AUTH_ENABLED) await msalInstance.logoutRedirect();
}
