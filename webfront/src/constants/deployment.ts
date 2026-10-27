/* The ONE company this build's Sales Order flow serves (27 Oct 2026) — branch develop-mgt = 'MGT',
   develop-glc = 'GLC'. Mirrors backend MgtOcr.Core/Auth/DeploymentCompany.cs. SO only: an SO on this
   site is always this company (MGT -> Zoho CRM, GLC -> SAP); AP / II / PODP are not affected.
   When cherry-picking between the two branches, keep this file as it is on the target branch. */
export const DEPLOYMENT_COMPANY: 'MGT' | 'GLC' = 'MGT';
