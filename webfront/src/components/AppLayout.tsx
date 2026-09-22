import { useEffect, useState } from 'react';
import { Link, NavLink, Navigate, Outlet, useLocation } from 'react-router-dom';
import { visibleNavSections, allowedModules } from '../navConfig';
import { useAppState } from '../state/AppState';
import { api } from '../api/client';
import { getMe, type Me } from '../api/me';
import { signOut } from '../api/auth';
import type { HealthStatus, ModuleCode } from '../api/types';
import GlobalToast from './GlobalToast';

/* =====================================================================
   App shell — faithful port of the old index.html layout (.app / .sidebar
   / .topbar) rebuilt as a React component, styled by styles/legacy.css.
   ===================================================================== */

function pageTitle(pathname: string): string {
  if (pathname === '/') return 'Overview';
  if (pathname.startsWith('/import')) return 'Document Import / OCR';
  if (pathname.startsWith('/list')) return 'Document Register';
  if (pathname.startsWith('/master')) return 'Master Mapping';
  if (pathname.startsWith('/audit-log')) return 'Log';
  if (pathname.startsWith('/log')) return 'SAP Submission History';
  return '';
}

// The document module a route belongs to (/import/:m, /list/:m), or null for other pages.
function moduleOfPath(pathname: string): ModuleCode | null {
  const m = pathname.match(/^\/(?:import|list)\/(AP|PODP|II|SO)\b/i);
  return m ? (m[1].toUpperCase() as ModuleCode) : null;
}

// Initials for the avatar: first letters of the first two words, else the first two characters.
function initialsOf(name: string | null | undefined): string {
  const parts = (name ?? '').trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[1][0]).toUpperCase();
}

// The signed-in person, shown at the foot of the sidebar. Replaces the old hard-coded address —
// name, role, company and a sign-out button, resolved from the token via /api/me.
function UserCard({ me, denied }: { me: Me | null; denied: string | null }) {
  if (denied)
    return (
      <div className="user-card">
        <div className="avatar" style={{ background: 'var(--red-bg)', color: 'var(--red)', borderColor: 'transparent' }}>
          <i className="fa-solid fa-triangle-exclamation" />
        </div>
        <div className="u-info lbl">
          <div className="u-name" style={{ color: 'var(--red)' }}>Sign-in blocked</div>
          <div className="u-sub" title={denied}>{denied}</div>
        </div>
      </div>
    );

  if (!me)
    return (
      <div className="user-card">
        <div className="avatar" style={{ opacity: 0.5 }}>…</div>
        <div className="u-info lbl">
          <div className="u-sub">Loading…</div>
        </div>
      </div>
    );

  const name = me.fullName || me.username || me.email || 'User';
  const company = me.primaryCompany?.companyName ?? '';
  const sub = [me.role, company].filter(Boolean).join(' · ');

  return (
    <div className="user-card">
      <div className="avatar" title={name}>{initialsOf(name)}</div>
      <div className="u-info lbl">
        <div className="u-name" title={name}>{name}</div>
        {sub && <div className="u-sub" title={sub}>{sub}</div>}
        {me.email && <div className="u-mail" title={me.email}>{me.email}</div>}
      </div>
      <button
        type="button"
        className="signout lbl"
        title="Sign out"
        aria-label="Sign out"
        onClick={() => void signOut()}
      >
        <i className="fa-solid fa-arrow-right-from-bracket" />
      </button>
    </div>
  );
}

// System status — small, secondary line above the user card.
function StatusLine() {
  const [health, setHealth] = useState<HealthStatus | null>(null);
  const [error, setError] = useState(false);
  useEffect(() => {
    let alive = true;
    api
      .get<HealthStatus>('/api/health')
      .then((h) => alive && setHealth(h))
      .catch(() => alive && setError(true));
    return () => {
      alive = false;
    };
  }, []);

  const ok = !error && !!health?.ok;
  const bad = error || (health != null && !health.ok);
  let text = 'Connecting to database…';
  if (bad) text = 'Database unavailable';
  else if (health)
    text = `${health.db?.db ?? 'DB'} · SAP ${health.sapMode === 'live' ? 'Live' : 'Sim'} · ${health.ocrProvider ?? 'OCR'}`;

  return (
    <div className="status-line lbl" title={health?.db?.srv ?? text}>
      <span className="dot" style={{ background: bad ? 'var(--red)' : ok ? 'var(--brand)' : 'var(--g500)' }} />
      <span className="status-text">{text}</span>
    </div>
  );
}

// Light/dark toggle, moved out of the top bar into the sidebar foot (sun · switch · moon).
function ThemeToggle() {
  const { themeMode, toggleTheme } = useAppState();
  const dark = themeMode === 'dark';
  return (
    <div className="theme-row">
      <span className="lbl theme-label">Theme</span>
      <button
        type="button"
        className={'theme-toggle' + (dark ? ' dark' : '')}
        onClick={toggleTheme}
        role="switch"
        aria-checked={dark}
        aria-label="Toggle light/dark theme"
      >
        <i className="fa-solid fa-sun tt-sun" aria-hidden="true" />
        <i className="fa-solid fa-moon tt-moon" aria-hidden="true" />
        <span className="tt-knob" />
      </button>
    </div>
  );
}

function SidebarFoot({ me, denied }: { me: Me | null; denied: string | null }) {
  return (
    <div className="sidebar-foot" id="foot">
      <StatusLine />
      <ThemeToggle />
      <UserCard me={me} denied={denied} />
    </div>
  );
}

export default function AppLayout() {
  const { navCollapsed, toggleNav } = useAppState();
  const location = useLocation();
  const [mobileNavOpen, setMobileNavOpen] = useState(false);

  useEffect(() => { setMobileNavOpen(false); }, [location.pathname]);
  useEffect(() => {
    if (!mobileNavOpen) return;
    const onEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setMobileNavOpen(false);
    };
    window.addEventListener('keydown', onEscape);
    return () => window.removeEventListener('keydown', onEscape);
  }, [mobileNavOpen]);

  // Resolve the signed-in user once for the whole shell: the sidebar identity card and the
  // department-based nav both read it, so it should not be fetched twice.
  const [me, setMe] = useState<Me | null>(null);
  const [denied, setDenied] = useState<string | null>(null);
  const [meLoaded, setMeLoaded] = useState(false);

  useEffect(() => {
    let alive = true;
    getMe()
      .then((m) => { if (alive) setMe(m); })
      // 403 here means the account signed in fine but has no active row in Ms_User.
      .catch((e: unknown) => { if (alive) setDenied(e instanceof Error ? e.message : 'No access'); })
      .finally(() => { if (alive) setMeLoaded(true); });
    return () => { alive = false; };
  }, []);

  const sections = visibleNavSections(me);


  // Backstop for the hidden nav: opening a module route the department may not see (typed URL,
  // old bookmark) sends the user home. The API is guarded regardless. Wait for /api/me first so
  // we do not redirect during the initial load.
  const routeModule = moduleOfPath(location.pathname);
  if (meLoaded && me != null && routeModule != null && !allowedModules(me).includes(routeModule))
    return <Navigate to="/" replace />;

  return (
    <div className="app">
      {mobileNavOpen && (
        <button className="mobile-nav-backdrop" type="button" aria-label="Close menu" onClick={() => setMobileNavOpen(false)} />
      )}
      <aside className={'sidebar' + (navCollapsed ? ' collapsed' : '') + (mobileNavOpen ? ' mobile-open' : '')} id="sidebar">
        <button className="mobile-nav-close" type="button" aria-label="Close menu" onClick={() => setMobileNavOpen(false)}>
          <i className="fa-solid fa-xmark" />
        </button>
        <div className="brand">
          <Link to="/" style={{ textDecoration: 'none' }}>
            <img src="/assets/logo.png" alt="MGT" className="brand-logo" />
            <b className="lbl">MGT Document OCR</b>
            <span className="lbl">Document Intake → SAP S/4HANA</span>
          </Link>
        </div>

        <nav className="nav" id="nav">
          {sections.map((section, i) => (
            <div key={i}>
              {section.title && <div className="nav-title lbl">{section.title}</div>}
              {section.items.map((item) => (
                <NavLink key={item.to} to={item.to} end={item.to === '/'} title={item.label}>
                  <span className="ic"><i className={item.icon} /></span>
                  <span className="lbl">{item.label}</span>
                </NavLink>
              ))}
            </div>
          ))}
        </nav>

        <button
          className="nav-toggle"
          onClick={toggleNav}
          title={navCollapsed ? 'Expand menu' : 'Collapse menu'}
          aria-label={navCollapsed ? 'Expand menu' : 'Collapse menu'}
        >
          <span className="lbl">{navCollapsed ? 'Expand' : 'Collapse'}</span>
          <span className="chev">
            <i className={`fa-solid ${navCollapsed ? 'fa-chevron-right' : 'fa-chevron-left'}`} style={{ fontSize: 14 }} />
          </span>
        </button>

        <SidebarFoot me={me} denied={denied} />
      </aside>

      <div className="main">
        <div className="topbar">
          <button
            className="mobile-nav-trigger"
            type="button"
            aria-label="Open menu"
            aria-expanded={mobileNavOpen}
            aria-controls="sidebar"
            onClick={() => setMobileNavOpen(true)}
          >
            <i className="fa-solid fa-bars" />
          </button>
          <h1 id="pageTitle">{pageTitle(location.pathname)}</h1>
          <div className="sp" />
          <Link className="btn sm start-document" to="/">
            <i className="fa-solid fa-arrow-rotate-right" /> Start New Document
          </Link>
        </div>

        <div className="content" id="content">
          {/* .content is the full-width scroll pane (so its scrollbar sits at the window edge, not
              floating mid-screen); .content-inner caps the readable width so tables/cards don't
              stretch edge-to-edge. me is threaded to child routes via context so the Sales Order
              page can branch Customer matching by company: MGT -> Zoho CRM, others -> SAP. */}
          <div className="content-inner">
            <Outlet context={{ me }} />
          </div>
        </div>
      </div>

      <GlobalToast />
      <div className="busy-indicator" id="busyIndicator">
        <span className="hg"><i className="fa-solid fa-hourglass-half" /></span> Processing…
      </div>
    </div>
  );
}
