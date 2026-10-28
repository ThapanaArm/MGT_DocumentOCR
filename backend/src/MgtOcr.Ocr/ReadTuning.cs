using MgtOcr.Core.Config;

namespace MgtOcr.Ocr;

/// <summary>Thinking cap for Gemini reads (Megachem, 28 Oct 2026: "reduce time, keep accuracy").
///
/// Gemini spends half to three quarters of the tokens it generates "thinking" before it answers, and
/// generating tokens is what the read waits for. thinkingConfig caps that:
/// - Ocr:ThinkingBudget = N (Gemini 2.5 models): at most N thinking tokens (0 = none, -1 = model decides).
/// - Ocr:ThinkingLevel  = "low" / "high" … (Gemini 3 models, which take a level instead of a budget).
/// Neither set = nothing is sent and the model behaves exactly as before (the production default).
///
/// Test on docs 844-847 (28 Oct 2026): no thinking read about twice as fast (≈15 s vs ≈37 s) but one
/// document of four came back with a different invoice number and an extra line. Not enabled until it
/// has been checked against posted documents.</summary>
public static class ReadTuning
{
    /// <summary>The generationConfig.thinkingConfig to send, or null to send none.</summary>
    public static object? ThinkingConfig(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.OcrThinkingLevel))
            return new { thinkingLevel = config.OcrThinkingLevel.Trim().ToLowerInvariant() };
        if (config.OcrThinkingBudget is int b) return new { thinkingBudget = b };
        return null;
    }
}
