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
