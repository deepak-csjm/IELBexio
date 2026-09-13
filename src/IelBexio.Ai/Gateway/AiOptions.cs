using IelBexio.Domain.Ai;

namespace IelBexio.Ai.Gateway;

/// <summary>
/// Every control the specification requires on AI usage (§13), in one place so an operator can see and
/// change the whole safety envelope without reading code.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>
    /// Master switch. When false the gateway refuses every request and deterministic processing
    /// continues unaffected — acceptance criterion 9. This is the default: AI is opt-in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Azure OpenAI resource endpoint. Azure OpenAI only; no other provider is supported (§12).</summary>
    public string? Endpoint { get; set; }

    /// <summary>API key. Omit to use managed identity via DefaultAzureCredential. Secret.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Deployment name to call.</summary>
    public string? Deployment { get; set; }

    /// <summary>
    /// Models this application may call. A request naming anything else is refused before any network
    /// call — an allow-list, not a block-list, because the failure mode of guessing wrong is expensive.
    /// </summary>
    public IList<string> AllowedModels { get; } = ["gpt-4o-mini", "gpt-4o", "gpt-4.1-mini", "gpt-4.1"];

    /// <summary>
    /// Operations this application may request. Anything outside the set is refused. Note what is
    /// absent: there is no "approve", "post", "delete" or "change permissions" operation, by design.
    /// </summary>
    public IList<AiOperation> AllowedOperations { get; } =
    [
        AiOperation.DocumentClassification,
        AiOperation.DocumentExtraction,
        AiOperation.FieldNormalisation,
        AiOperation.CustomerMatchSuggestion,
        AiOperation.ProductNormalisation,
        AiOperation.TaxCodeSuggestion,
        AiOperation.AccountSuggestion,
        AiOperation.AnomalyExplanation,
        AiOperation.ReviewSummary,
    ];

    /// <summary>Upper bound on untrusted content sent in one request, in characters.</summary>
    public int MaxUntrustedContentChars { get; set; } = 60_000;

    /// <summary>Upper bound on a document accepted for AI extraction, in bytes.</summary>
    public long MaxDocumentBytes { get; set; } = 10 * 1024 * 1024;

    public int MaxOutputTokens { get; set; } = 2_000;

    /// <summary>
    /// Temperature ceiling. Extraction and classification want determinism; a high temperature on a
    /// financial extraction is a defect, so the gateway clamps rather than trusting the call site.
    /// </summary>
    public float MaxTemperature { get; set; } = 0.2f;

    public float Temperature { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(45);

    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>Cap on AI calls attributable to one invoice, so a retry loop cannot run up a bill.</summary>
    public int MaxCallsPerInvoice { get; set; } = 8;

    public int MaxExtractionAttemptsPerDocument { get; set; } = 3;

    /// <summary>Monthly spend ceiling. When exceeded the gateway refuses and routes to human review (§13).</summary>
    public decimal MonthlyBudget { get; set; } = 50m;

    public string BudgetCurrency { get; set; } = "USD";

    /// <summary>Estimated cost per 1,000 prompt tokens, for the cost meter. An estimate, not billing.</summary>
    public decimal CostPer1kPromptTokens { get; set; } = 0.00015m;

    public decimal CostPer1kCompletionTokens { get; set; } = 0.0006m;

    /// <summary>Whether identical requests may reuse a cached result (§13 extraction caching).</summary>
    public bool EnableCaching { get; set; } = true;

    public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromDays(30);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Deployment);
}
