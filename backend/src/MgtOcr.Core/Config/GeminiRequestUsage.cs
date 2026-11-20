namespace MgtOcr.Core.Config;

public sealed record GeminiRequestUsage(
    Guid RequestId, long? JobId, int? DocId, string? Module, string Environment,
    string KeyName, string Model, string Operation, int Attempt, int[] PageNumbers,
    DateTime StartedUtc, DateTime FinishedUtc, string Outcome, int? HttpStatus, string? ResponseId,
    int? Input, int? Output, int? Thinking, int? CachedInput,
    decimal? InputRate, decimal? OutputRate, decimal? CachedRate)
{
    public decimal? InputCost => (Input - CachedInput) * InputRate / 1_000_000m
        + CachedInput * CachedRate / 1_000_000m;
    public decimal? OutputCost => ((long?)Output + Thinking) * OutputRate / 1_000_000m;
    public decimal? Cost => InputCost + OutputCost;
}
