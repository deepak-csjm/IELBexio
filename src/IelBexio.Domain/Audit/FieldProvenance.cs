using IelBexio.Domain.Common;

namespace IelBexio.Domain.Audit;

/// <summary>
/// Traces one canonical field back to where its value came from (§22). Written whenever a field is
/// populated by normalisation, corrected by a human, or set from an accepted AI proposal.
/// </summary>
public sealed class FieldProvenance : TenantEntity
{
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }

    /// <summary>Dotted path, e.g. "totalAmount" or "lines[2].taxRatePercent".</summary>
    public string FieldPath { get; set; } = string.Empty;

    public ValueOrigin Origin { get; set; }
    public SourceSystem SourceSystem { get; set; }

    /// <summary>Source document/order id the value came from.</summary>
    public string? SourceDocumentId { get; set; }

    /// <summary>The field path in the source payload, e.g. "currentTotalPriceSet.shopMoney.amount".</summary>
    public string? SourceFieldPath { get; set; }

    /// <summary>Description of any transformation applied, e.g. "sum(lines.netAmount)".</summary>
    public string? Transformation { get; set; }

    /// <summary>Extraction method when the value came from a document.</summary>
    public string? ExtractionMethod { get; set; }

    public Guid? AiProposalId { get; set; }
    public string? AiModel { get; set; }
    public string? AiPromptVersion { get; set; }

    public string? ModifiedBy { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>The value recorded at this point, as JSON, so the chain is readable after later edits.</summary>
    public string? ValueJson { get; set; }

    public string CorrelationId { get; set; } = string.Empty;
}
