/* Kill-switches for the "Send" actions that actually POST a document to SAP or to Zoho CRM.
   Everything else (View Payload, Step 2 mapping, editing, the Excel exports) is unaffected --
   these only feed the `disabled` prop of the Send buttons.

   The two Send paths are switched SEPARATELY on purpose: they belong to different developers and
   are at different stages, so freezing one must never freeze the other.

   SEND_DISABLED    -- the Sales Order flow (SapSalesOrderEditor.tsx, ZohoSalesOrderEditor.tsx).
                       Open. Not ours to change: leave it as the Sales Order work sets it.
   SEND_DISABLED_AP -- the AP/II "Confirm Submission to SAP" modal in DocumentPage.tsx (MIRO /
                       FB60 via API_SUPPLIERINVOICE_PROCESS_SRV). CLOSED while the delivery-cost
                       flow is still being tested (20 Oct 2026): the route works -- supplier
                       invoice 5105600153 was posted through it -- but PO item numbers are still
                       being filled in by hand while API_PURCHASEORDER_PROCESS_SRV answers 403, so
                       a wrong number posts a wrong document. Testing goes through the SAP Fiori
                       app meanwhile. Set back to false once the PO lookup is open and checked. */
export const SEND_DISABLED = false;
export const SEND_DISABLED_AP = true;

/* Temporary switch for Microsoft (Entra) SSO sign-in (14 Oct 2026). SSO is built and will be used,
   but is not allowed yet, so it is hidden: false = no "Continue with Microsoft" button, no silent
   sign-in on load, no redirect handling -- only username/password login works. Anyone who was
   signed in with Microsoft lands on the password sign-in screen. Set back to true to turn SSO on
   again (VITE_AZURE_CLIENT_ID must still be set). The backend still accepts Microsoft tokens; this
   only hides it in the web app. */
export const SSO_LOGIN_ENABLED = false;
