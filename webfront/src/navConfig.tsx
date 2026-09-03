import type { ModuleCode } from './api/types';

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

const IMPORT = '⚍'; // ☱
const LIST = '☰'; // ☰
const OVERVIEW = '▣'; // ▣
const MASTER = '⚑'; // ⚑
const LOG = '☷'; // ☷
const AUDIT = '⚑'; // ⚑

export const navSections: NavSection[] = [
  {
    items: [{ label: 'Overview', to: '/', icon: OVERVIEW }],
  },
  {
    title: 'SUPPLIER INVOICE',
    items: [
      { label: 'Import Supplier Invoice', to: '/import/AP', icon: IMPORT, module: 'AP' },
      { label: 'Supplier Invoice List', to: '/list/AP', icon: LIST, module: 'AP' },
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
    title: 'INCOMING INVOICES',
    items: [
      { label: 'Import Incoming Invoice', to: '/import/II', icon: IMPORT, module: 'II' },
      { label: 'Incoming Invoice List', to: '/list/II', icon: LIST, module: 'II' },
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
