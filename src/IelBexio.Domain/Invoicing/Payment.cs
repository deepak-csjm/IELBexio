using IelBexio.Domain.Common;

namespace IelBexio.Domain.Invoicing;

/// <summary>A payment recorded against a canonical invoice, as reported by the source system.</summary>
public sealed class Payment : TenantEntity
{
    public Guid InvoiceId { get; set; }
    public string? SourcePaymentId { get; set; }
    public string? Method { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "CHF";
    public string? Status { get; set; }
    public string? TransactionReference { get; set; }

    public Money Value => new(Amount, Currency);
}
