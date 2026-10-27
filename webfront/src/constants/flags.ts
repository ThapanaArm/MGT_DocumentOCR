/* Kill-switches for the "Send" actions that actually POST a document to SAP or to Zoho CRM.
   Everything else (View Payload, Step 2 mapping, editing, the Excel exports) is unaffected --
   these only feed the `disabled` prop of the Send buttons.

   The two Send paths are switched SEPARATELY on purpose: they belong to different developers and
   move at different speeds, so freezing one must never freeze the other.

   SEND_DISABLED    -- the Sales Order flow (SapSalesOrderEditor.tsx, ZohoSalesOrderEditor.tsx).
                       Open. Not ours to change: leave it as the Sales Order work sets it.

   SEND_DISABLED_AP -- the AP/II "Confirm Submission to SAP" modal in DocumentPage.tsx
                       (MIRO / FB60 via API_SUPPLIERINVOICE_PROCESS_SRV). Open (20 Oct 2026).

                       Safe to leave open because this route cannot reach production at all, by
                       configuration rather than by this flag: Sap:SupplierInvoice carries its OWN
                       ActiveEnvironment ("dev"), so switching the rest of the system to Prod does
                       not drag it along, and its BaseUrl_Prod is deliberately EMPTY -- with no URL
                       the backend refuses to post instead of inventing a document number, which is
                       what it used to do (a fake "51" + six digits that existed nowhere).

                       Close it again if that stops being true -- if BaseUrl_Prod is ever filled
                       in, this is the switch that keeps users out until the route is trusted. */
export const SEND_DISABLED = false;
export const SEND_DISABLED_AP = false;

/* Temporary switch for Microsoft (Entra) SSO sign-in (14 Oct 2026). SSO is built and will be used,
   but is not allowed yet, so it is hidden: false = no "Continue with Microsoft" button, no silent
   sign-in on load, no redirect handling -- only username/password login works. Anyone who was
   signed in with Microsoft lands on the password sign-in screen. Set back to true to turn SSO on
   again (VITE_AZURE_CLIENT_ID must still be set). The backend still accepts Microsoft tokens; this
   only hides it in the web app. */
export const SSO_LOGIN_ENABLED = false;
