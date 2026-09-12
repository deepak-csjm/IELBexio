using IelBexio.Domain.Common;

namespace IelBexio.Domain.Invoicing;

/// <summary>
/// An explicit human approval. Separate from the invoice row so the approval history survives
/// re-approval after a rejection, and so dual control can be added without schema change (§16).
/// </summary>
public sealed class Approval : TenantEntity
{
    public Guid EntityId { get; set; }
    public string EntityType { get; set; } = "Invoice";
    public ApprovalDecision Decision { get; set; }

    public string? PreparedBy { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTimeOffset ApprovedAt { get; set; }

    /// <summary>
    /// The version of the entity that was approved. If the record changes afterwards, the approval no
    /// longer matches and must be re-obtained — this prevents approve-then-edit-then-post.
    /// </summary>
    public string ApprovalVersion { get; set; } = string.Empty;

    public string? Comment { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}

public enum ApprovalDecision { Approved = 0, Rejected = 1 }

/// <summary>A tenant of the platform.</summary>
public sealed class Tenant : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? CountryCode { get; set; }
    public string DefaultCurrency { get; set; } = "CHF";
    public bool IsActive { get; set; } = true;
}
