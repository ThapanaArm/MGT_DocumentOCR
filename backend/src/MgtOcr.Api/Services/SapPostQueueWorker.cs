using System.Text.Json;
using MgtOcr.Core.Auth;
using MgtOcr.Data;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Services;

/// <summary>Sends queued Step 3 documents to SAP one at a time, oldest first (ocr.SapPostQueue,
/// sql/32). Pending (1) → In process (0) → Completed (2) or Error (9).
///
/// The send itself is the same code the Step 3 button always ran (DocumentsController.PostDocument),
/// so splitting by supplier and tax code, skipping invoices already in SAP and the PostLog / JobLog
/// rows all behave exactly as before. It runs as the person who pressed the button: their user
/// record is stored with the queue row and stamped as PostedBy.
///
/// One at a time on purpose: two invoices against the same purchase order at once is what SAP
/// refuses with ME/006 "PO is being processed", and a shipping bundle can make several of them.</summary>
public class SapPostQueueWorker(SapPostQueueRepository queue, IServiceScopeFactory scopes,
    ILogger<SapPostQueueWorker> log) : BackgroundService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var reset = await queue.ResetOrphansAsync(ct);
            if (reset > 0) log.LogInformation("Requeued {Count} SAP post(s) left In process by a previous run", reset);
        }
        catch (Exception e) { log.LogWarning(e, "Could not reset the SAP post queue (run sql/32_sap_post_queue.sql?)"); }

        while (!ct.IsCancellationRequested)
        {
            SapPostQueueClaim? job = null;
            try { job = await queue.ClaimNextAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Could not read the SAP post queue");
            }

            if (job is null)
            {
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            await SendAsync(job);
        }
    }

    private async Task SendAsync(SapPostQueueClaim job)
    {
        log.LogInformation("SAP post queue {QueueId}: sending document {DocId} for {User}", job.QueueId, job.DocId, job.RequestedBy);
        try
        {
            using var scope = scopes.CreateScope();
            var user = string.IsNullOrWhiteSpace(job.UserJson) ? null
                : JsonSerializer.Deserialize<CurrentUser>(job.UserJson, Web);
            if (user is null) throw new InvalidOperationException("ไม่พบข้อมูลผู้กดส่งในคิว");

            var controller = ActivatorUtilities.CreateInstance<Controllers.DocumentsController>(
                scope.ServiceProvider, (ICurrentUserAccessor)new QueuedUser(user));
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider },
            };

            var action = await controller.PostDocument(job.DocId, null);
            var value = (action as ObjectResult)?.Value;
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, Web));
            var root = json.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            var message = root.TryGetProperty("message", out var m) ? m.ToString() : null;
            var sapDocNo = root.TryGetProperty("sapDocNo", out var n) ? n.ToString() : null;
            // The document itself is not kept with the row — the screen reads it fresh.
            var slim = root.EnumerateObject().Where(p => p.Name != "document")
                .ToDictionary(p => p.Name, p => p.Value.Clone());
            await queue.FinishAsync(job.QueueId, success, sapDocNo, message, JsonSerializer.Serialize(slim, Web));
            log.LogInformation("SAP post queue {QueueId}: document {DocId} {Result}", job.QueueId, job.DocId,
                success ? "completed " + sapDocNo : "error " + message);
        }
        catch (Exception e)
        {
            var message = e is MgtOcr.Core.HttpApiException ? e.Message : $"{e.GetType().Name}: {e.Message}";
            log.LogWarning(e, "SAP post queue {QueueId}: document {DocId} failed", job.QueueId, job.DocId);
            var result = JsonSerializer.Serialize(new { success = false, message }, Web);
            try { await queue.FinishAsync(job.QueueId, false, null, message, result); }
            catch (Exception e2) { log.LogError(e2, "Could not record the result of SAP post queue {QueueId}", job.QueueId); }
        }
    }

    /// <summary>The person who pressed Step 3, as stored with the queue row.</summary>
    private sealed class QueuedUser(CurrentUser user) : ICurrentUserAccessor
    {
        public Task<CurrentUser?> GetAsync(CancellationToken ct = default) => Task.FromResult<CurrentUser?>(user);
        public Task<CurrentUser> RequireAsync(CancellationToken ct = default) => Task.FromResult(user);
    }
}
