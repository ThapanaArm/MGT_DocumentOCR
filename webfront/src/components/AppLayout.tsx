import { useEffect, useState } from 'react';
import { Link, NavLink, Outlet, useLocation } from 'react-router-dom';
import { navSections, MODULE_LABEL } from '../navConfig';
import { useAppState } from '../state/AppState';
import { api } from '../api/client';
import type { HealthStatus, ModuleCode } from '../api/types';
import GlobalToast from './GlobalToast';

/* =====================================================================
   App shell — faithful port of the old index.html layout (.app / .sidebar
   / .topbar) rebuilt as a React component, styled by styles/legacy.css.
   ===================================================================== */

function currentModule(pathname: string): ModuleCode | null {
  const m = pathname.match(/^\/(?:import|list)\/(AP|PODP|II|SO)/);
  return (m?.[1] as ModuleCode) ?? null;
}

function pageTitle(pathname: string): string {
  if (pathname === '/') return 'Overview';
  if (pathname.startsWith('/import')) return 'Document Import / OCR';
  if (pathname.startsWith('/list')) return 'Document Register';
  if (pathname.startsWith('/master')) return 'Master Mapping';
  if (pathname.startsWith('/audit-log')) return 'Log';
  if (pathname.startsWith('/log')) return 'SAP Submission History';
  return '';
}

const USER = 'it-digital@megachem.co.th';

function SidebarFoot() {
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
  if (error || (health && !health.ok))
    return (
      <div className="sidebar-foot lbl" id="foot">
        <span style={{ color: 'var(--red)' }}>● Unable to connect to database</span>
      </div>
    );
  if (!health)
    return (
      <div className="sidebar-foot lbl" id="foot">
        Connecting to database…
      </div>
    );
  return (
    <div className="sidebar-foot lbl" id="foot">
      DB: <b style={{ color: 'var(--brand)' }}>● {health.db?.db}</b>
      <br />
      {health.db?.srv}
      <br />
      SAP: {health.sapMode === 'live' ? 'Live' : 'Simulation Mode'} · OCR: {health.ocrProvider}
      <br />
      <span className="hint">{USER}</span>
    </div>
  );
}

export default function AppLayout() {
  const { themeMode, toggleTheme, navCollapsed, toggleNav } = useAppState();
  const location = useLocation();

  const activeModule = currentModule(location.pathname);
  const showModTag =
    activeModule && (location.pathname.startsWith('/import') || location.pathname.startsWith('/list'));

  return (
    <div className="app">
      <aside className={'sidebar' + (navCollapsed ? ' collapsed' : '')} id="sidebar">
        <div className="brand">
          <Link to="/" style={{ textDecoration: 'none' }}>
            <img src="/assets/logo.png" alt="MGT" className="brand-logo" />
            <b className="lbl">MGT Document OCR</b>
            <span className="lbl">Document Intake → SAP S/4HANA</span>
          </Link>
        </div>

        <nav className="nav" id="nav">
          {navSections.map((section, i) => (
            <div key={i}>
              {section.title && <div className="nav-title lbl">{section.title}</div>}
              {section.items.map((item) => (
                <NavLink key={item.to} to={item.to} end={item.to === '/'} title={item.label}>
                  <span className="ic">{item.icon}</span>
                  <span className="lbl">{item.label}</span>
                </NavLink>
              ))}
            </div>
          ))}
        </nav>

        <SidebarFoot />
      </aside>

      <div className="main">
        <div className="topbar">
          <button
            className="sidebar-toggle"
            onClick={toggleNav}
            title={navCollapsed ? 'Expand menu' : 'Collapse menu'}
            aria-label={navCollapsed ? 'Expand menu' : 'Collapse menu'}
          >
            <svg
              viewBox="0 0 24 24"
              width="19"
              height="19"
              fill="none"
              stroke="currentColor"
              strokeWidth={2}
              strokeLinecap="round"
              strokeLinejoin="round"
            >
              <rect x="3" y="4" width="18" height="16" rx="2" />
              <line x1="9" y1="4" x2="9" y2="20" />
            </svg>
          </button>
          <h1 id="pageTitle">{pageTitle(location.pathname)}</h1>
          {showModTag && (
            <span className="modtag">▪ Module: {MODULE_LABEL[activeModule]}</span>
          )}
          <div className="sp" />
          <button
            className="btn sm ghost"
            onClick={toggleTheme}
            title="Toggle light/dark mode"
            dangerouslySetInnerHTML={{ __html: themeMode === 'dark' ? '&#9788;' : '&#9789;' }}
          />
          <Link className="btn sm" to="/">
            ↻ Start New Document
          </Link>
        </div>

        <div className="content" id="content">
          <Outlet />
        </div>
      </div>

      <GlobalToast />
      <div className="busy-indicator" id="busyIndicator">
        <span className="hg">⏳</span> Processing…
      </div>
    </div>
  );
}
