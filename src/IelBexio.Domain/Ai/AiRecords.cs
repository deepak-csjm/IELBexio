using IelBexio.Domain.Common;

namespace IelBexio.Domain.Ai;

public enum AiProposalStatus { Pending = 0, Accepted = 1, Rejected = 2, Superseded = 3, Expired = 4 }

/// <summary>The bounded set of things AI is permitted to be asked for. Anything else is rejected by the gateway.</summary>
public enum AiOperation
{
    Unknown = 0,
    DocumentClassification = 1,
    DocumentExtraction = 2,
    FieldNormalisation = 3,
    CustomerMatchSuggestion = 4,
    ProductNormalisation = 5,
    TaxCodeSuggestion = 6,
    AccountSuggestion = 7,
    AnomalyExplanation = 8,
    ReviewSummary = 9,
}

/// <summary>
/// An AI suggestion. This is the <em>only</em> place AI output is ever written (§12). Nothing reads a
/// proposal into a canonical field except an explicit human acceptance, which is itself audited.
/// </summary>
public sealed class AiProposal : TenantEntity
{
    /// <summary>Entity the proposal is about (usually an invoice or invoice line).</summary>
    public Guid EntityId { get; set; }

    public string EntityType { get; set; } = string.Empty;

    /// <summary>Dotted path of the field being proposed, e.g. "lines[0].taxRatePercent".</summary>
    public string FieldPath { get; set; } = string.Empty;

    public AiOperation Operation { get; set; }

    /// <summary>Proposed value, serialised as JSON so any shape can be represented.</summary>
    public string ProposedValueJson { get; set; } = "null";

    /// <summary>The value at the time the proposal was made, for reviewer comparison.</summary>
    public string? CurrentValueJson { get; set; }

    /// <summary>Model self-reported signal in [0,1]. Explicitly not a correctness guarantee.</summary>
    public decimal ConfidenceSignal { get; set; }

    public string Model { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;

    /// <summary>Evidence the model cited (quoted source text, matched candidate ids). Reviewer-visible.</summary>
    public string? EvidenceJson { get; set; }

    public AiProposalStatus Status { get; set; } = AiProposalStatus.Pending;
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewComment { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}

/// <summary>
/// Safe, non-PII telemetry for one AI call (§13). Prompts and responses are deliberately not stored
/// here; only the metadata needed for cost control, budgeting and debugging.
/// </summary>
public sealed class AiUsageRecord : TenantEntity
{
    public AiOperation Operation { get; set; }
    public string Model { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public int LatencyMs { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }

    /// <summary>Estimated cost in the configured accounting currency. Estimate only — provider billing is authoritative.</summary>
    public decimal EstimatedCost { get; set; }

    public string CostCurrency { get; set; } = "USD";
    public bool Succeeded { get; set; }
    public AiFailureReason FailureReason { get; set; }
    public string? FailureDetail { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public Guid? InvoiceId { get; set; }

    /// <summary>True when the result came from the extraction cache and no provider call was made.</summary>
    public bool ServedFromCache { get; set; }
}

public enum AiFailureReason
{
    None = 0,
    Disabled = 1,
    BudgetExceeded = 2,
    CallLimitExceeded = 3,
    InputTooLarge = 4,
    Timeout = 5,
    ProviderUnavailable = 6,
    MalformedOutput = 7,
    SchemaValidationFailed = 8,
    ModelNotAllowed = 9,
    OperationNotAllowed = 10,
    Unknown = 99,
}
