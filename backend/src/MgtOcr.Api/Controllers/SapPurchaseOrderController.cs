using MgtOcr.Core;
using MgtOcr.Core.Auth;
using MgtOcr.Sap;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// The purchase order behind a supplier invoice, read straight from SAP.
//
// A MIRO posted against a PO names the PO ITEM it bills — 10, 40, 50, 60 on PO 2230000020 — and
// those numbers exist nowhere but in SAP: the printed PO renumbers its rows 1, 2, 3 and the
// shipping form does not carry them at all. So before the invoice is sent, the PO is read here
// and its items are matched against what the document says.
// GET /api/sap/purchase-order/2230000020
[ApiController]
[Route("api/sap")]
public class SapPurchaseOrderController(SapPurchaseOrderClient client, ICurrentUserAccessor currentUser)
    : ControllerBase
{
    [HttpGet("purchase-order/{purchaseOrder}")]
    public async Task<IActionResult> Items(string purchaseOrder)
    {
        await currentUser.RequireAsync();

        var po = (purchaseOrder ?? "").Trim();
        if (po.Length == 0) return BadRequest(new { detail = "Provide a purchase order number" });
        if (!client.Configured)
            return Ok(new { purchaseOrder = po, configured = false, count = 0, items = Array.Empty<object>() });

        try
        {
            var items = await client.ItemsAsync(po);
            // Supplier and company code come back with the items so the screen can say "this PO
            // belongs to Kimex 2000014 / company 2000" before anything is assigned.
            var head = await client.HeadAsync(po);
            return Ok(new { purchaseOrder = po, configured = true, count = items.Count, items, head });
        }
        catch (Exception ex)
        {
            // A lookup that fails must not look like "this PO has no items" — the person would
            // then post an invoice against item numbers nobody checked.
            throw new HttpApiException(502, $"Could not read purchase order {po} from SAP: {ex.Message}");
        }
    }
}
