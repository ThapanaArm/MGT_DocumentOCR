using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MgtOcr.Data;

namespace MgtOcr.Api.Services;

public sealed class SapUsdThbWorker(IConfiguration config, IHttpClientFactory clients, Db db,
    ILogger<SapUsdThbWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = config.GetSection("Ocr:SapExchangeRate");
        if (!settings.GetValue<bool>("Enabled")) return;

        var rateType = settings["RateType"]?.Trim().ToUpperInvariant();
        if (!Uri.TryCreate(settings["ServiceUrl"]?.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var service)
            || service.Scheme != Uri.UriSchemeHttps || service.UserInfo.Length > 0
            || service.Query.Length > 0 || service.Fragment.Length > 0
            || !AuthenticationHeaderValue.TryParse(settings["AuthHeader"], out var auth)
            || string.IsNullOrWhiteSpace(auth.Parameter)
            || rateType is null || rateType.Length is < 1 or > 4 || !rateType.All(char.IsAsciiLetterOrDigit))
        {
            logger.LogWarning("SAP exchange-rate sync requires an HTTPS OData ServiceUrl, AuthHeader and approved RateType.");
            return;
        }

        using var interval = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var today = DateTime.UtcNow.AddHours(7).Date;
                await SyncAsync(service, auth, rateType, today.AddDays(-29), today, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (HttpRequestException error) when (error.StatusCode.HasValue)
            {
                logger.LogWarning("SAP exchange-rate API returned HTTP {StatusCode}.", (int)error.StatusCode.Value);
            }
            catch (Exception error)
            {
                logger.LogWarning("Unable to refresh SAP exchange rates ({ErrorType}).", error.GetType().Name);
            }
        }
        while (!stoppingToken.IsCancellationRequested && await interval.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAsync(Uri service, AuthenticationHeaderValue auth, string rateType,
        DateTime from, DateTime to, CancellationToken ct)
    {
        using var client = clients.CreateClient(nameof(SapUsdThbWorker));
        client.Timeout = TimeSpan.FromSeconds(15);
        var filter = FormattableString.Invariant(
            $"ExchangeRateType eq '{rateType}' and SourceCurrency eq 'USD' and TargetCurrency eq 'THB' and ExchangeRateEffectiveDate ge {from:yyyy-MM-dd} and ExchangeRateEffectiveDate le {to:yyyy-MM-dd}");
        using var listed = await GetAsync(client, new Uri(service,
            $"CurrencyExchangeRate?$filter={Uri.EscapeDataString(filter)}&$select=ExchangeRateEffectiveDate"),
            auth, ct);
        var dates = listed.RootElement.GetProperty("value").EnumerateArray()
            .Select(row => DateTime.ParseExact(row.GetProperty("ExchangeRateEffectiveDate").GetString()!,
                "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Where(date => date >= from && date <= to)
            .Distinct();

        foreach (var date in dates)
        {
            // Ask SAP to apply indirect quotation and currency ratios. A larger USD amount
            // preserves six rate decimals even if SAP rounds the THB result to two decimals.
            var convert = FormattableString.Invariant(
                $"CurrencyExchangeRate/SAP__self.Convert(ExchangeRateType='{rateType}',CurrencyPair='',SourceCurrency='USD',TargetCurrency='THB',AmountInSourceCurrency=10000,AmountInTargetCurrency=0,CrcyExchangeRateValidOnDate={date:yyyy-MM-dd})");
            using var result = await GetAsync(client, new Uri(service, convert), auth, ct);
            var amount = result.RootElement.GetProperty("AmountInTargetCurrency");
            if (!decimal.TryParse(amount.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var thb)
                || thb <= 0) continue;

            await db.ExecuteAsync("""
                MERGE ocr.SapUsdThbRate WITH (HOLDLOCK) AS existing
                USING (SELECT @date AS RateDate, CAST(@rate AS decimal(12,6)) AS ThbPerUsd,
                              @rateType AS RateType) AS sap
                   ON sap.RateDate = existing.RateDate
                WHEN MATCHED AND (existing.ThbPerUsd <> sap.ThbPerUsd OR existing.RateType <> sap.RateType)
                    THEN UPDATE SET ThbPerUsd = sap.ThbPerUsd, RateType = sap.RateType
                WHEN NOT MATCHED THEN
                    INSERT (RateDate, ThbPerUsd, RateType) VALUES (sap.RateDate, sap.ThbPerUsd, sap.RateType);
                """, new { date, rate = thb / 10000m, rateType }, ct);
        }
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, Uri uri,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = auth;
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
