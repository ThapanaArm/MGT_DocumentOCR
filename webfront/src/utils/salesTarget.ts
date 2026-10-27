import type { Me } from '../api/me';
import type { DocModel } from '../api/documents';
import { DEPLOYMENT_COMPANY } from '../constants/deployment';

/* =====================================================================
   Where a document is posted.

   A Sales Order opened under company MGT is reviewed and sent to Zoho CRM;
   every other case — the liability modules (II / PODP), and Sales Orders under
   any other company (Green Leaf, etc.) — is created in SAP.

   This is the single source of truth for that decision, replacing the
   scattered `companyCode === 'MGT'` checks. Unlike the old flag it is
   module-aware: an MGT user who opens a non-SO document (e.g. an Incoming
   Invoice) correctly routes to SAP, not the Zoho Sales Order path.
   ===================================================================== */
export type SalesTarget = 'sap' | 'zoho';

export function resolveDocTarget(
  doc: Pick<DocModel, 'module'> | null | undefined,
  me: Pick<Me, 'primaryCompany'> | null | undefined,
): SalesTarget {
  // Separate MGT / GLC deployments (27 Oct 2026): a Sales Order follows this site's company,
  // not the signed-in user's (see constants/deployment.ts). `me` is no longer consulted.
  void me;
  const isMgt = DEPLOYMENT_COMPANY === 'MGT';
  const isSalesOrder = doc?.module === 'SO';
  return isMgt && isSalesOrder ? 'zoho' : 'sap';
}
