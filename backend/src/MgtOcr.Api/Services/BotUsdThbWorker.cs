using System.Globalization;
using System.Text.Json;
using MgtOcr.Data;

namespace MgtOcr.Api.Services;

public sealed class BotUsdThbWorker(IConfiguration config, IHttpClientFactory clients, Db db,
    ILogger<BotUsdThbWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var key = config["Ocr:BotApiKey"]?.Trim();
        if (string.IsNullOrEmpty(key)) return;

        using var interval = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var today = DateTime.UtcNow.AddHours(7).Date;
                await SyncAsync(key, today.AddDays(-29), today, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("Unable to refresh BOT reference rates ({ErrorType}).", error.GetType().Name);
            }
        }
        while (await interval.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAsync(string key, DateTime from, DateTime to, CancellationToken ct)
    {
        using var client = clients.CreateClient(nameof(BotUsdThbWorker));
        client.Timeout = TimeSpan.FromSeconds(15);
        using var request = new HttpRequestMessage(HttpMethod.Get, FormattableString.Invariant(
            $"https://gateway.api.bot.or.th/Stat-ReferenceRate/v2/DAILY_REF_RATE/?start_period={from:yyyy-MM-dd}&end_period={to:yyyy-MM-dd}"));
        request.Headers.Add("Authorization", key);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var data = json.RootElement.GetProperty("result").GetProperty("data").GetProperty("data_detail");
        var rates = new Dictionary<DateTime, decimal>();
        foreach (var row in data.EnumerateArray())
        {
            if (DateTime.TryParseExact(row.GetProperty("period").GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) && date >= from && date <= to &&
                decimal.TryParse(row.GetProperty("rate").ToString(), NumberStyles.Number, CultureInfo.InvariantCulture,
                    out var rate) && rate > 0)
                rates[date] = rate;
        }
        if (rates.Count == 0) return;

        var rows = JsonSerializer.Serialize(rates.Select(pair => new { date = pair.Key, rate = pair.Value }));
        await db.ExecuteAsync("""
            INSERT ocr.BotUsdThbReferenceRate (RateDate, ThbPerUsd)
            SELECT r.RateDate, r.ThbPerUsd
            FROM OPENJSON(@rows) WITH (RateDate date '$.date', ThbPerUsd decimal(12,6) '$.rate') r
            WHERE NOT EXISTS (
                SELECT 1 FROM ocr.BotUsdThbReferenceRate WITH (UPDLOCK, HOLDLOCK) WHERE RateDate = r.RateDate
            )
            """, new { rows }, ct);
    }
}
