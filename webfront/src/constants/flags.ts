/* Temporary kill-switch for the "Send" actions that actually POST a document to SAP or to Zoho
   CRM. Flip this back to false to re-enable both Send buttons -- everything else (View Payload,
   Step 2 mapping, editing) is unaffected; this only feeds into the `disabled` prop of the Send
   buttons in SapSalesOrderEditor.tsx, ZohoSalesOrderEditor.tsx, and DocumentPage.tsx's AP/II
   "Confirm Submission to SAP" modal. */
export const SEND_DISABLED = false;

/* Temporary switch for Microsoft (Entra) SSO sign-in (14 Oct 2026). SSO is built and will be used,
   but is not allowed yet, so it is hidden: false = no "Continue with Microsoft" button, no silent
   sign-in on load, no redirect handling -- only username/password login works. Anyone who was
   signed in with Microsoft lands on the password sign-in screen. Set back to true to turn SSO on
   again (VITE_AZURE_CLIENT_ID must still be set). The backend still accepts Microsoft tokens; this
   only hides it in the web app. */
export const SSO_LOGIN_ENABLED = true;
