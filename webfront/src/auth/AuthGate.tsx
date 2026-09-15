import { useEffect, useState } from 'react';
import { AUTH_ENABLED as MS_ENABLED, attemptSilentSignIn, getAccount, initAuth, signIn } from './msal';
import { hasLocalSession, loginWithPassword } from '../api/auth';
import { MOCK_ALWAYS } from '../api/mocks';

/* =====================================================================
   Sign-in screen. Two panels: a branded illustration on the left (brand,
   headline, and a document-flow graphic that fans multiple document types
   into SAP S/4HANA and Zoho CRM) and the login card on the right. The left
   panel collapses on narrow screens. Nothing past this renders until there
   is a signed-in session — by password or by a Microsoft account. The API
   enforces auth on its own; this is just a friendly gate.
   ===================================================================== */

type Phase = 'starting' | 'signed-out' | 'ready';
const IT_MAIL = 'mailto:it-digital@megachem.co.th';

// Microsoft's official four-square mark, required on a "Sign in with Microsoft" button.
function MicrosoftMark() {
  return (
    <svg width="16" height="16" viewBox="0 0 21 21" aria-hidden="true" style={{ flex: '0 0 auto' }}>
      <rect x="1" y="1" width="9" height="9" fill="#f25022" />
      <rect x="11" y="1" width="9" height="9" fill="#7fba00" />
      <rect x="1" y="11" width="9" height="9" fill="#00a4ef" />
      <rect x="11" y="11" width="9" height="9" fill="#ffb900" />
    </svg>
  );
}

// The document-flow graphic: a fanned stack of document types (PO, Sales Order,
// Invoice) read by OCR, branching into the two downstream systems.
function LoginIllustration() {
  return (
    <div className="login-ill">
      <div className="login-stage">
        <div className="login-mini po">
          <span className="mck"><i className="fa-solid fa-check" /></span>
          <span className="mbadge">PO</span>
          <span className="mln" /><span className="mln s" />
        </div>
        <div className="login-mini so">
          <span className="mck"><i className="fa-solid fa-check" /></span>
          <span className="mbadge">Sales Order</span>
          <span className="mln" /><span className="mln s" />
        </div>
        <div className="login-doc">
          <div className="ldh">
            <span className="lfi"><i className="fa-solid fa-file-lines" /></span>
            <span className="ltt">Invoice</span>
            <span className="lbadge">OCR</span>
          </div>
          <div className="lsrow"><span className="lsl w1" /><span className="lck"><i className="fa-solid fa-check" /></span></div>
          <div className="lsrow"><span className="lsl w2" /><span className="lck"><i className="fa-solid fa-check" /></span></div>
          <div className="lsrow"><span className="lsl w3" /><span className="lck"><i className="fa-solid fa-check" /></span></div>
        </div>
      </div>

      <div className="login-flowc">
        <svg
          className="login-conn"
          viewBox="0 0 470 52"
          preserveAspectRatio="none"
          fill="none"
          stroke="#00994d"
          strokeWidth={2}
          strokeDasharray="5 5"
          strokeLinecap="round"
          aria-hidden="true"
        >
          <path d="M235 2 V16" />
          <path d="M118 50 V38 Q118 26 150 26 H235 H320 Q352 26 352 38 V50" />
          <circle cx="235" cy="2" r="3.5" fill="#00994d" stroke="none" />
          <circle cx="118" cy="50" r="3.5" fill="#00994d" stroke="none" />
          <circle cx="352" cy="50" r="3.5" fill="#00994d" stroke="none" />
        </svg>
        <div className="login-dests">
          <div className="login-dcard sap">
            <span className="dlg">S/4</span>
            <span className="dnm">SAP S/4HANA<small>Supplier Invoice</small></span>
          </div>
          <div className="login-dcard zoho">
            <span className="dlg">Z</span>
            <span className="dnm">Zoho CRM<small>Sales Order</small></span>
          </div>
        </div>
      </div>
    </div>
  );
}

export default function AuthGate({ children }: { children: React.ReactNode }) {
  // UI mockup mode (VITE_USE_MOCK=1): there is no backend to sign in against, so skip the gate
  // entirely and render the app. Strictly dev-only — this flag is 0/absent in production.
  const [phase, setPhase] = useState<Phase>(MOCK_ALWAYS ? 'ready' : 'starting');

  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [showPw, setShowPw] = useState(false);
  const [remember, setRemember] = useState(true);
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  useEffect(() => {
    if (MOCK_ALWAYS) return; // mock mode: already 'ready', no auth to initialise
    let alive = true;
    (async () => {
      try { await initAuth(); } catch { /* fall through to the sign-in screen */ }
      if (!alive) return;
      if (getAccount() || hasLocalSession()) {
        setPhase('ready');
        return;
      }
      setPhase('signed-out');
      const ok = await attemptSilentSignIn();
      if (alive && ok) setPhase('ready');
    })();
    const onSignedOut = () => alive && setPhase('signed-out');
    window.addEventListener('mgtocr:signedout', onSignedOut);
    return () => {
      alive = false;
      window.removeEventListener('mgtocr:signedout', onSignedOut);
    };
  }, []);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setFormError(null);
    try {
      await loginWithPassword(username.trim(), password, remember);
      setPassword('');
      setPhase('ready');
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Sign-in failed');
    } finally {
      setBusy(false);
    }
  }

  if (phase === 'ready') return <>{children}</>;

  return (
    <div className="login-wrap">
      <div className="login-left">
        <div className="login-blob b1" />
        <div className="login-blob b2" />
        <div className="login-dots d1" />
        <div className="login-dots d2" />

        <div className="login-brand">
          <img src="/assets/logo.png" alt="MGT" className="login-logo" />
          <span className="login-sep" />
          <span className="login-appname">Document OCR</span>
        </div>

        <div className="login-head">
          <h1>Automate your<br /><span>document workflow</span></h1>
        </div>

        <LoginIllustration />
      </div>

      <div className="login-right">
        <div className="login-card">
          <div className="login-mobile-brand">
            <img src="/assets/logo.png" alt="MGT" className="login-mobile-logo" />
            <span className="login-mobile-name">Document OCR</span>
          </div>
          {phase === 'starting' ? (
            <p style={{ textAlign: 'center', color: '#5f6e64', fontSize: 14, margin: '8px 0' }}>Checking sign-in…</p>
          ) : (
            <>
              <h2 className="login-title">Welcome</h2>
              <p className="login-subtitle">Sign in to MGT Document OCR</p>

              <form onSubmit={submit}>
                <label className="login-label" htmlFor="lg-user">Username or Email</label>
                <div className="login-field">
                  <span className="ic"><i className="fa-solid fa-user" /></span>
                  <input
                    id="lg-user"
                    type="text"
                    autoComplete="username"
                    placeholder="Enter your username or email"
                    value={username}
                    onChange={(e) => setUsername(e.target.value)}
                    required
                  />
                </div>

                <label className="login-label" htmlFor="lg-pass">Password</label>
                <div className="login-field">
                  <span className="ic"><i className="fa-solid fa-lock" /></span>
                  <input
                    id="lg-pass"
                    type={showPw ? 'text' : 'password'}
                    autoComplete="current-password"
                    placeholder="Enter your password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    required
                  />
                  <button
                    type="button"
                    className="login-eye"
                    onClick={() => setShowPw((v) => !v)}
                    aria-label={showPw ? 'Hide password' : 'Show password'}
                  >
                    <i className={showPw ? 'fa-solid fa-eye-slash' : 'fa-solid fa-eye'} />
                  </button>
                </div>

                <div className="login-row">
                  <label className="login-remember">
                    <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
                    Remember me
                  </label>
                  <a className="login-forgot" href={IT_MAIL}>Forgot password?</a>
                </div>

                {formError && <div className="login-error">{formError}</div>}

                <button type="submit" className="login-signin" disabled={busy}>
                  {busy ? 'Signing in…' : (<>Sign in <i className="fa-solid fa-arrow-right" style={{ fontSize: 13 }} /></>)}
                </button>
              </form>

              {MS_ENABLED && (
                <>
                  <div className="login-divider">OR</div>
                  <button type="button" className="login-ms" onClick={() => void signIn()}>
                    <MicrosoftMark /> Continue with Microsoft
                  </button>
                </>
              )}

              <p className="login-help">Need help? Contact <a href={IT_MAIL}>IT Support</a></p>
              <p className="login-foot">© {new Date().getFullYear()} MGT Document OCR. All rights reserved.</p>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
