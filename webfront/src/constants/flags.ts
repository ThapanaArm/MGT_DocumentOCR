/* Temporary kill-switch for the "Send" actions that actually POST a document to SAP or to Zoho
   CRM. Flip this back to false to re-enable both Send buttons -- everything else (View Payload,
   Step 2 mapping, editing) is unaffected; this only feeds into the `disabled` prop of the Send
   buttons in SapSalesOrderEditor.tsx, ZohoSalesOrderEditor.tsx, and DocumentPage.tsx's AP/II
   "Confirm Submission to SAP" modal. */
export const SEND_DISABLED = false;
