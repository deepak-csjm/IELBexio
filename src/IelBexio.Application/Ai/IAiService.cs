using IelBexio.Domain.Ai;

namespace IelBexio.Application.Ai;

/// <summary>Why a request to the AI gateway was refused before any provider call was made.</summary>
public sealed record AiRefusal(AiFailureReason Reason, string Message);

/// <summary>
/// Result of an AI request. AI failure is always a normal outcome, never an exception that can take
/// down ingestion: acceptance criterion 9 requires deterministic processing to keep working with AI
/// unavailable or disabled.
/// </summary>
public sealed record AiResult<T>(bool Succeeded, T? Value, AiRefusal? Refusal, AiCallMetadata Metadata)
{
    public static AiResult<T> Refused(AiFailureReason reason, string message, AiCallMetadata metadata) =>
        new(false, default, new AiRefusal(reason, message), metadata);

    public static AiResult<T> Ok(T value, AiCallMetadata metadata) => new(true, value, null, metadata);
}

/// <summary>Safe, non-PII metadata about one gateway invocation (§13).</summary>
public sealed record AiCallMetadata
{
    public AiOperation Operation { get; init; }
    public string Model { get; init; } = string.Empty;
    public string PromptVersion { get; init; } = string.Empty;
    public string SchemaVersion { get; init; } = string.Empty;
    public int LatencyMs { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public decimal EstimatedCost { get; init; }
    public string CostCurrency { get; init; } = "USD";
    public bool ServedFromCache { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// A request into the AI gateway. The untrusted content is kept in a dedicated property so the
/// gateway can wrap it in an explicit data envelope and the prompt template cannot accidentally
/// interpolate it as instructions (§14).
/// </summary>
public sealed record AiRequest
{
    public required AiOperation Operation { get; init; }

    /// <summary>Task-specific, developer-authored context. Trusted. Never contains document text.</summary>
    public string TrustedContext { get; init; } = string.Empty;

    /// <summary>
    /// Content originating outside the system (document text, customer names, product descriptions).
    /// Treated strictly as data. Anything instruction-shaped in here is invoice content, not a command.
    /// </summary>
    public string UntrustedContent { get; init; } = string.Empty;

    /// <summary>JSON Schema the response must satisfy. A response that does not validate is rejected.</summary>
    public required string ResponseJsonSchema { get; init; }

    public string SchemaName { get; init; } = "response";

    /// <summary>Invoice this call is attributed to, for the per-invoice call cap and cost reporting.</summary>
    public Guid? InvoiceId { get; init; }

    /// <summary>Stable cache key component; when set, an identical prior result may be reused (§13).</summary>
    public string? CacheKey { get; init; }
}

/// <summary>
/// The single controlled gateway to Azure OpenAI (§12). All AI traffic passes through here; business
/// code never constructs a provider client. The gateway enforces model allow-list, operation
/// allow-list, size and token budgets, temperature ceiling, timeout, retry, schema validation, prompt
/// versioning, caching, cost accounting and correlation.
/// </summary>
public interface IAiService
{
    bool IsEnabled { get; }

    /// <summary>Sends a request and returns the schema-validated JSON payload, or a refusal.</summary>
    Task<AiResult<string>> CompleteJsonAsync(AiRequest request, CancellationToken cancellationToken = default);
}

// ---- Specialised facades. Each routes through IAiService; none talks to a provider directly. ----

public sealed record ExtractedInvoiceCandidate(
    string? InvoiceNumber,
    string? InvoiceDate,
    string? Currency,
    string? CustomerName,
    string? CustomerVatNumber,
    decimal? Subtotal,
    decimal? TaxAmount,
    decimal? Total,
    IReadOnlyList<ExtractedLineCandidate> Lines,
    IReadOnlyDictionary<string, decimal> FieldConfidence);

public sealed record ExtractedLineCandidate(string? Description, decimal? Quantity, decimal? UnitPrice, decimal? NetAmount, decimal? TaxRatePercent);

public interface IAiDocumentExtractor
{
    Task<AiResult<ExtractedInvoiceCandidate>> ExtractAsync(string documentText, Guid? invoiceId, string? cacheKey, CancellationToken cancellationToken = default);
}

public sealed record DocumentClassification(string Kind, decimal Confidence, string? Rationale);

public interface IAiClassificationService
{
    Task<AiResult<DocumentClassification>> ClassifyAsync(string documentText, string? cacheKey, CancellationToken cancellationToken = default);
}

public sealed record MappingSuggestion(string CandidateId, string CandidateLabel, decimal Confidence, string? Evidence);

public interface IAiMappingSuggestionService
{
    Task<AiResult<IReadOnlyList<MappingSuggestion>>> SuggestCustomerMatchAsync(
        string customerDescription,
        IReadOnlyList<(string Id, string Label)> candidates,
        Guid? invoiceId,
        CancellationToken cancellationToken = default);

    Task<AiResult<IReadOnlyList<MappingSuggestion>>> SuggestTaxCodeAsync(
        string lineDescription,
        string countryCode,
        IReadOnlyList<(string Id, string Label)> candidates,
        Guid? invoiceId,
        CancellationToken cancellationToken = default);

    Task<AiResult<IReadOnlyList<MappingSuggestion>>> SuggestAccountAsync(
        string lineDescription,
        IReadOnlyList<(string Id, string Label)> candidates,
        Guid? invoiceId,
        CancellationToken cancellationToken = default);
}

public sealed record AnomalyExplanation(string Summary, IReadOnlyList<string> Observations, decimal Confidence);

public interface IAiAnomalyService
{
    Task<AiResult<AnomalyExplanation>> ExplainAsync(string factsJson, Guid? invoiceId, CancellationToken cancellationToken = default);
}
