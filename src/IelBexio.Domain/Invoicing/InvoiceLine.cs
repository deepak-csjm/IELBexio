using IelBexio.Domain.Common;

namespace IelBexio.Domain.Invoicing;

/// <summary>Canonical invoice line. Amounts are stored as decimals with the parent invoice's currency.</summary>
public sealed class InvoiceLine : TenantEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    /// <summary>Position on the document, 1-based. Determines display and Bexio position order.</summary>
    public int LineNumber { get; set; }

    public string? SourceLineId { get; set; }
    public string? Sku { get; set; }
    public string? ProductCode { get; set; }
    public string Description { get; set; } = string.Empty;

    public decimal Quantity { get; set; } = 1m;
    public string? Unit { get; set; }

    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal NetAmount { get; set; }

    /// <summary>Effective tax rate as a percentage (8.1 means 8.1%), not a fraction.</summary>
    public decimal TaxRatePercent { get; set; }

    public decimal TaxAmount { get; set; }
    public decimal GrossAmount { get; set; }

    public string Currency { get; set; } = "CHF";

    /// <summary>True for shipping/handling charge lines, which map to a different Bexio account.</summary>
    public LineKind Kind { get; set; } = LineKind.Product;

    public Guid? TaxAssessmentId { get; set; }
    public Guid? ProductMappingId { get; set; }

    public string? BexioArticleId { get; set; }
    public string? BexioAccountId { get; set; }
    public string? BexioTaxId { get; set; }

    public Money Net => new(NetAmount, Currency);
    public Money Tax => new(TaxAmount, Currency);
    public Money Gross => new(GrossAmount, Currency);
}

public enum LineKind { Product = 0, Shipping = 1, Discount = 2, Surcharge = 3, Rounding = 4 }
