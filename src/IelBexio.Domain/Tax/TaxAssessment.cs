using IelBexio.Domain.Common;

namespace IelBexio.Domain.Tax;

/// <summary>How a tax conclusion was reached. Recorded so a reviewer can see whether to trust it.</summary>
public enum TaxDeterminationMethod
{
    Unknown = 0,

    /// <summary>Taken directly from the source system's own tax lines.</summary>
    SourceProvided = 1,

    /// <summary>Derived by the deterministic rule table from jurisdiction + tax type.</summary>
    RuleTable = 2,

    /// <summary>Back-computed deterministically from amounts present on the document.</summary>
    DerivedFromAmounts = 3,

    /// <summary>Proposed by AI. Never sufficient on its own — requires human verification (§6).</summary>
    AiProposed = 4,

    /// <summary>Entered or corrected by a human.</summary>
    HumanEntered = 5,

    /// <summary>No conclusion could be reached.</summary>
    Undetermined = 6,
}

/// <summary>Coarse tax categories the POC distinguishes. Not a complete tax taxonomy — see §39.</summary>
public enum TaxType
{
    Unknown = 0,
    StandardRate = 1,
    ReducedRate = 2,
    SpecialRate = 3,
    ZeroRated = 4,
    Exempt = 5,
    ReverseCharge = 6,
    OutOfScope = 7,
}

/// <summary>
/// The internal tax representation (§6). Deliberately separate from both the source's tax data and
/// Bexio's tax ids: it is the interchange point between "what the source said", "what our rules
/// concluded", and "what we intend to send to Bexio" — each recorded independently so a reviewer can
/// see all three.
/// </summary>
public sealed class TaxAssessment : TenantEntity
{
    public Guid InvoiceId { get; set; }
    public Guid? InvoiceLineId { get; set; }

    // ---- Jurisdiction ----------------------------------------------------------
    /// <summary>Free-form jurisdiction label, e.g. "CH" or "CH-ZH". Country is stored separately.</summary>
    public string? Jurisdiction { get; set; }

    /// <summary>ISO-3166-1 alpha-2 of the taxing country.</summary>
    public string? CountryCode { get; set; }

    public TaxType TaxType { get; set; }

    /// <summary>Rate as a percentage (8.1 = 8.1%).</summary>
    public decimal RatePercent { get; set; }

    public decimal TaxableAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public string Currency { get; set; } = "CHF";

    // ---- What the source said (verbatim) ---------------------------------------
    public string? SourceTaxCode { get; set; }
    public string? SourceTaxName { get; set; }
    public decimal? SourceTaxRatePercent { get; set; }

    // ---- What we concluded ------------------------------------------------------
    /// <summary>Our own stable tax code, e.g. "CH-VAT-STD-8.1". Maps to Bexio via <c>tax_mappings</c>.</summary>
    public string? InternalTaxCode { get; set; }

    /// <summary>Proposed Bexio tax id. Always resolved dynamically; never hardcoded (§6).</summary>
    public string? ProposedBexioTaxId { get; set; }

    /// <summary>Bexio tax id actually used at synchronisation time.</summary>
    public string? AppliedBexioTaxId { get; set; }

    public TaxDeterminationMethod DeterminationMethod { get; set; }

    /// <summary>Version of the determination logic, so old conclusions remain interpretable.</summary>
    public string DeterminationVersion { get; set; } = "1.0.0";

    /// <summary>
    /// Workflow signal in [0,1]. NOT a legal or tax-advice confidence: it only drives whether the
    /// record is routed to review. Explicitly documented as such in the UI.
    /// </summary>
    public decimal ConfidenceSignal { get; set; }

    public bool HumanVerified { get; set; }
    public string? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>Human-readable explanation of how the conclusion was reached.</summary>
    public string? Rationale { get; set; }

    public Money Taxable => new(TaxableAmount, Currency);
    public Money Tax => new(TaxAmount, Currency);
}
