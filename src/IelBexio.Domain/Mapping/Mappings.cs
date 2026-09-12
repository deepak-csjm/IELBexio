using IelBexio.Domain.Common;

namespace IelBexio.Domain.Mapping;

/// <summary>How a mapping row came to exist — drives trust and review routing (§17).</summary>
public enum MappingOrigin
{
    Unknown = 0,
    ExactMatch = 1,
    Configured = 2,
    Manual = 3,
    AiSuggested = 4,
    Seed = 5,
}

public abstract class MappingBase : TenantEntity
{
    public MappingOrigin Origin { get; set; }
    public bool IsActive { get; set; } = true;
    public string? CreatedBy { get; set; }
    public string? Notes { get; set; }

    /// <summary>Workflow signal in [0,1]; AI-suggested mappings start below the auto-accept threshold.</summary>
    public decimal ConfidenceSignal { get; set; } = 1m;
}

/// <summary>Internal customer → Bexio contact.</summary>
public sealed class CustomerMapping : MappingBase
{
    public Guid CustomerId { get; set; }
    public string BexioContactId { get; set; } = string.Empty;
    public string? BexioContactName { get; set; }
}

/// <summary>Internal product (by SKU) → Bexio article.</summary>
public sealed class ProductMapping : MappingBase
{
    public string Sku { get; set; } = string.Empty;
    public string? ProductName { get; set; }
    public string? BexioArticleId { get; set; }

    /// <summary>Revenue account to book this product's lines to, when no article-level default applies.</summary>
    public string? BexioAccountId { get; set; }
}

/// <summary>
/// Internal tax code → Bexio tax id, scoped by country and validity window. Never hardcoded: rows are
/// created from the taxes discovered through the Bexio API (§6, §17).
/// </summary>
public sealed class TaxMapping : MappingBase
{
    public string InternalTaxCode { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "CH";
    public decimal RatePercent { get; set; }
    public string BexioTaxId { get; set; } = string.Empty;
    public string? BexioTaxName { get; set; }
    public decimal? BexioTaxRatePercent { get; set; }

    /// <summary>Mirrors Bexio's own active flag as last discovered. An inactive tax fails preflight (§18).</summary>
    public bool BexioTaxIsActive { get; set; } = true;

    public DateOnly ValidFrom { get; set; } = new(2000, 1, 1);
    public DateOnly? ValidTo { get; set; }

    public bool IsValidOn(DateOnly date) => date >= ValidFrom && (ValidTo is null || date <= ValidTo);
}

/// <summary>Internal revenue-account code → Bexio account id.</summary>
public sealed class AccountMapping : MappingBase
{
    /// <summary>Our own account key, e.g. "REVENUE_GOODS", "REVENUE_SHIPPING".</summary>
    public string InternalAccountCode { get; set; } = string.Empty;

    public string BexioAccountId { get; set; } = string.Empty;
    public string? BexioAccountNumber { get; set; }
    public string? BexioAccountName { get; set; }
}

/// <summary>Well-known internal account codes used by deterministic line classification.</summary>
public static class InternalAccountCodes
{
    public const string RevenueGoods = "REVENUE_GOODS";
    public const string RevenueShipping = "REVENUE_SHIPPING";
    public const string RevenueDiscount = "REVENUE_DISCOUNT";
}
