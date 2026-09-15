import type { ModuleCode } from './api/types';
import type { Me } from './api/me';

/* =====================================================================
   Sidebar navigation — mirrors the grouped nav in the old index.html
   exactly, including the unicode glyph icons (.ic) used there.
   Routes are declared in App.tsx and must stay in sync with `to` here.
   ===================================================================== */

export interface NavItem {
  label: string;
  to: string;
  icon: string; // unicode glyph, matches the old .ic spans
  module?: ModuleCode;
}

export interface NavSection {
  title?: string;
  items: NavItem[];
}

export const MODULE_LABEL: Record<ModuleCode, string> = {
  AP: 'Supplier Invoice',
  PODP: 'PO Down Payment',
  II: 'Incoming Invoice',
  SO: 'Sales Order',
};

const IMPORT = 'fa-solid fa-file-import';
const LIST = 'fa-solid fa-list';
const OVERVIEW = 'fa-solid fa-gauge-high';
const MASTER = 'fa-solid fa-flag';
const LOG = 'fa-solid fa-clock-rotate-left';
const AUDIT = 'fa-solid fa-clipboard-check';

export const navSections: NavSection[] = [
  {
    items: [{ label: 'Overview', to: '/', icon: OVERVIEW }],
  },
  {
    // Single Liability-Recording (AP) reading flow. On read, a PO number routes to
    // Supplier Invoice (MIRO / with PO); no PO routes to Incoming Invoice (FB60 / no PO).
    title: 'INVOICE (LIABILITY RECORDING)',
    items: [
      { label: 'Import Invoice', to: '/import/AP', icon: IMPORT, module: 'AP' },
      { label: 'Invoice List', to: '/list/AP', icon: LIST, module: 'AP' },
    ],
  },
  {
    title: 'PURCHASE ORDER DOWN PAYMENTS',
    items: [
      { label: 'Import PO Down Payment', to: '/import/PODP', icon: IMPORT, module: 'PODP' },
      { label: 'PO Down Payment List', to: '/list/PODP', icon: LIST, module: 'PODP' },
    ],
  },
  {
    title: 'SALES ORDER',
    items: [
      { label: 'Import Sales Order', to: '/import/SO', icon: IMPORT, module: 'SO' },
      { label: 'Sales Order List', to: '/list/SO', icon: LIST, module: 'SO' },
    ],
  },
  {
    title: 'MASTER DATA',
    items: [{ label: 'Master Mapping', to: '/master', icon: MASTER }],
  },
  {
    title: 'LOG',
    items: [
      { label: 'SAP Submission History', to: '/log', icon: LOG },
      { label: 'Activity Log', to: '/audit-log', icon: AUDIT },
    ],
  },
];

/* =====================================================================
   Department-based visibility — mirrors the server policy in
   DepartmentAccess.cs. The nav keys off the *tab* modules (AP, PODP, SO);
   II is folded into the AP / Invoice tab, so it needs no nav entry.
   ===================================================================== */

const DEPARTMENT_MODULES: Record<string, ModuleCode[]> = {
  finance: ['AP', 'II'],
  purchase: ['PODP'],
  csr: ['SO'],
};

type Access = Pick<Me, 'role' | 'department'>;

// Modules this user may access. Admin => all; a mapped department => its set; anyone else => none.
export function allowedModules(me: Access): ModuleCode[] {
  if ((me.role || '').trim().toLowerCase() === 'admin') return ['AP', 'PODP', 'II', 'SO'];
  return DEPARTMENT_MODULES[(me.department || '').trim().toLowerCase()] ?? [];
}

// Nav sections this user should see: sections whose items carry a module are kept only when that
// module is allowed; module-less sections (Overview, Master Data, Log) always show.
export function visibleNavSections(me: Access | null): NavSection[] {
  if (!me) return navSections; // still resolving — routes stay API-guarded regardless
  const allow = new Set<ModuleCode>(allowedModules(me));
  return navSections
    .map((s) => ({ ...s, items: s.items.filter((it) => !it.module || allow.has(it.module)) }))
    .filter((s) => s.items.length > 0);
}
