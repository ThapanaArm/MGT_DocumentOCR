<<<<<<< HEAD
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
export const SSO_LOGIN_ENABLED = false;
=======
/* Kill-switches for the "Send" actions that actually POST a document to SAP or to Zoho CRM.
   Everything else (View Payload, Step 2 mapping, editing, the Excel exports) is unaffected --
   these only feed the `disabled` prop of the Send buttons.

   SEND_DISABLED    -- the Sales Order flow (SapSalesOrderEditor.tsx, ZohoSalesOrderEditor.tsx).
                       Still frozen: that flow belongs to another developer and its write URL can
                       point at the live tenant.
   SEND_DISABLED_AP -- the AP/II "Confirm Submission to SAP" modal in DocumentPage.tsx (MIRO /
                       FB60 via API_SUPPLIERINVOICE_PROCESS_SRV). Open, because that route has its
                       own environment key (Sap:SupplierInvoice:ActiveEnvironment = dev) and its
                       BaseUrl_Prod is deliberately left empty in appsettings.json, so it cannot
                       reach production even if the rest of the system is switched over. With no
                       BaseUrl at all the backend answers in simulation mode instead of posting. */
export const SEND_DISABLED = true;
export const SEND_DISABLED_AP = false;
>>>>>>> feature/ap-invoice
