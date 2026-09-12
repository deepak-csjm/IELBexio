using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Validation;

namespace IelBexio.Application.Invoices;

/// <summary>Rounding tolerance configuration for the arithmetic checks.</summary>
public sealed class ValidationTolerances
{
    /// <summary>
    /// Absolute tolerance when comparing computed against stated totals, expressed in the invoice
    /// currency. Source systems round per line; a two-rappen drift on a 20-line invoice is normal and
    /// must not fail the invoice, while a real error is orders of magnitude larger.
    /// </summary>
    public decimal TotalsTolerance { get; init; } = 0.05m;

    /// <summary>Tolerance when recomputing a line's tax from its net amount and rate.</summary>
    public decimal LineTaxTolerance { get; init; } = 0.02m;

    /// <summary>Invoice dates outside this window are implausible and get flagged.</summary>
    public int MaxFutureDays { get; init; } = 7;

    public int MaxPastYears { get; init; } = 10;
}

/// <summary>
/// Deterministic invoice validation (§7, §21). This is ordinary arithmetic and rule checking — no AI
/// is involved and none is permitted: an LLM must never perform authoritative monetary arithmetic.
/// <para>
/// The validator is a pure function of the invoice: given the same invoice it always produces the same
/// report. That makes it exhaustively unit-testable and safe to re-run at any point in the workflow.
/// </para>
/// </summary>
public sealed class InvoiceValidator
{
    private readonly ValidationTolerances _tolerances;
    private readonly TimeProvider _timeProvider;

    public InvoiceValidator(ValidationTolerances? tolerances = null, TimeProvider? timeProvider = null)
    {
        _tolerances = tolerances ?? new ValidationTolerances();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValidationReport Validate(Invoice invoice, IReadOnlyList<InvoiceLine> lines)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(lines);

        var report = new ValidationReport();

        ValidateCurrency(invoice, lines, report);
        ValidateIdentity(invoice, report);
        ValidateDates(invoice, report);
        ValidateLinesPresent(lines, report);
        ValidateLineArithmetic(invoice, lines, report);
        ValidateHeaderAgainstLines(invoice, lines, report);
        ValidateTotalsBalance(invoice, report);
        ValidateCustomer(invoice, report);

        return report;
    }

    private static void ValidateCurrency(Invoice invoice, IReadOnlyList<InvoiceLine> lines, ValidationReport report)
    {
        if (string.IsNullOrWhiteSpace(invoice.Currency))
        {
            report.Error(ValidationCodes.CurrencyMissing, "The invoice has no currency.", nameof(Invoice.Currency));
            return;
        }

        // A mixed-currency invoice is not something we round our way out of — it is a hard error.
        var mismatched = lines
            .Where(l => !string.Equals(l.Currency, invoice.Currency, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.LineNumber)
            .ToList();

        if (mismatched.Count > 0)
        {
            report.Error(
                ValidationCodes.CurrencyMismatch,
                $"Line(s) {string.Join(", ", mismatched)} use a different currency from the invoice ({invoice.Currency}).",
                nameof(Invoice.Currency));
        }
    }

    private static void ValidateIdentity(Invoice invoice, ValidationReport report)
    {
        if (string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
        {
            report.Warning(ValidationCodes.InvoiceNumberMissing, "The invoice has no invoice number.", nameof(Invoice.InvoiceNumber));
        }
    }

    private void ValidateDates(Invoice invoice, ValidationReport report)
    {
        if (invoice.InvoiceDate is null)
        {
            report.Error(ValidationCodes.InvoiceDateMissing, "The invoice has no invoice date.", nameof(Invoice.InvoiceDate));
            return;
        }

        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var date = invoice.InvoiceDate.Value;

        if (date > today.AddDays(_tolerances.MaxFutureDays))
        {
            report.Warning(
                ValidationCodes.InvoiceDateImplausible,
                $"The invoice date {date:yyyy-MM-dd} is more than {_tolerances.MaxFutureDays} days in the future.",
                nameof(Invoice.InvoiceDate));
        }
        else if (date < today.AddYears(-_tolerances.MaxPastYears))
        {
            report.Warning(
                ValidationCodes.InvoiceDateImplausible,
                $"The invoice date {date:yyyy-MM-dd} is more than {_tolerances.MaxPastYears} years in the past.",
                nameof(Invoice.InvoiceDate));
        }

        if (invoice.DueDate is { } due && due < date)
        {
            report.Warning(
                ValidationCodes.DueDateBeforeInvoiceDate,
                $"The due date {due:yyyy-MM-dd} precedes the invoice date {date:yyyy-MM-dd}.",
                nameof(Invoice.DueDate));
        }
    }

    private static void ValidateLinesPresent(IReadOnlyList<InvoiceLine> lines, ValidationReport report)
    {
        if (lines.Count == 0)
        {
            report.Error(ValidationCodes.NoLines, "The invoice has no line items.", nameof(Invoice.Lines));
        }
    }

    private void ValidateLineArithmetic(Invoice invoice, IReadOnlyList<InvoiceLine> lines, ValidationReport report)
    {
        foreach (var line in lines)
        {
            var path = $"lines[{line.LineNumber}]";

            // net = quantity * unitPrice - discount
            var expectedNet = decimal.Round((line.Quantity * line.UnitPrice) - line.DiscountAmount, Money.StorageScale, MidpointRounding.ToEven);
            if (Math.Abs(expectedNet - line.NetAmount) > _tolerances.LineTaxTolerance)
            {
                report.Error(
                    ValidationCodes.LineArithmeticInvalid,
                    $"Line {line.LineNumber}: net {line.NetAmount} does not equal quantity × unit price − discount ({expectedNet}).",
                    $"{path}.netAmount",
                    $"quantity={line.Quantity}, unitPrice={line.UnitPrice}, discount={line.DiscountAmount}");
            }

            // tax = net * rate / 100
            var expectedTax = decimal.Round(line.NetAmount * line.TaxRatePercent / 100m, Money.StorageScale, MidpointRounding.ToEven);
            if (Math.Abs(expectedTax - line.TaxAmount) > _tolerances.LineTaxTolerance)
            {
                report.Error(
                    ValidationCodes.TaxAmountInconsistent,
                    $"Line {line.LineNumber}: tax {line.TaxAmount} does not match {line.TaxRatePercent}% of net {line.NetAmount} ({expectedTax}).",
                    $"{path}.taxAmount");
            }

            // gross = net + tax
            var expectedGross = decimal.Round(line.NetAmount + line.TaxAmount, Money.StorageScale, MidpointRounding.ToEven);
            if (Math.Abs(expectedGross - line.GrossAmount) > _tolerances.LineTaxTolerance)
            {
                report.Error(
                    ValidationCodes.LineArithmeticInvalid,
                    $"Line {line.LineNumber}: gross {line.GrossAmount} does not equal net + tax ({expectedGross}).",
                    $"{path}.grossAmount");
            }

            if (line.Quantity == 0m && line.Kind == LineKind.Product)
            {
                report.Warning(ValidationCodes.LineArithmeticInvalid, $"Line {line.LineNumber} has zero quantity.", $"{path}.quantity");
            }
        }
    }

    private void ValidateHeaderAgainstLines(Invoice invoice, IReadOnlyList<InvoiceLine> lines, ValidationReport report)
    {
        if (lines.Count == 0)
        {
            return;
        }

        // Shipping may be carried either as a dedicated line or as a header amount. Accept both, but
        // never count it twice: header shipping is only added when no shipping line exists.
        var shippingLines = lines.Where(l => l.Kind == LineKind.Shipping).ToList();
        var lineNetTotal = lines.Sum(l => l.NetAmount);
        var lineTaxTotal = lines.Sum(l => l.TaxAmount);

        var expectedNetFromHeader = invoice.SubtotalAmount - invoice.DiscountAmount
            + (shippingLines.Count > 0 ? 0m : invoice.ShippingAmount);

        if (Math.Abs(lineNetTotal - expectedNetFromHeader) > _tolerances.TotalsTolerance)
        {
            report.Error(
                ValidationCodes.LineTotalsDoNotMatchHeader,
                $"Sum of line net amounts ({lineNetTotal}) does not match the header net " +
                $"(subtotal {invoice.SubtotalAmount} − discount {invoice.DiscountAmount}" +
                (shippingLines.Count > 0 ? "" : $" + shipping {invoice.ShippingAmount}") + $" = {expectedNetFromHeader}).",
                nameof(Invoice.SubtotalAmount));
        }

        if (Math.Abs(lineTaxTotal - invoice.TaxAmount) > _tolerances.TotalsTolerance)
        {
            report.Error(
                ValidationCodes.TaxAmountInconsistent,
                $"Sum of line tax amounts ({lineTaxTotal}) does not match the header tax amount ({invoice.TaxAmount}).",
                nameof(Invoice.TaxAmount));
        }
    }

    private void ValidateTotalsBalance(Invoice invoice, ValidationReport report)
    {
        // The §21 identity: subtotal + tax + shipping - discount = total
        var expected = decimal.Round(
            invoice.SubtotalAmount + invoice.TaxAmount + invoice.ShippingAmount - invoice.DiscountAmount,
            Money.StorageScale,
            MidpointRounding.ToEven);

        if (Math.Abs(expected - invoice.TotalAmount) > _tolerances.TotalsTolerance)
        {
            report.Error(
                ValidationCodes.TotalsDoNotBalance,
                $"subtotal ({invoice.SubtotalAmount}) + tax ({invoice.TaxAmount}) + shipping ({invoice.ShippingAmount}) " +
                $"− discount ({invoice.DiscountAmount}) = {expected}, but the stated total is {invoice.TotalAmount}.",
                nameof(Invoice.TotalAmount),
                $"currency={invoice.Currency}; tolerance={_tolerances.TotalsTolerance}");
        }

        // A credit note legitimately has a negative total; a plain invoice does not.
        if (invoice.TotalAmount < 0m && invoice.DocumentStatus != DocumentStatus.CreditNote)
        {
            report.Warning(
                ValidationCodes.NegativeTotal,
                $"The total {invoice.TotalAmount} is negative but the document is not marked as a credit note.",
                nameof(Invoice.TotalAmount));
        }
    }

    private static void ValidateCustomer(Invoice invoice, ValidationReport report)
    {
        if (invoice.CustomerId is null)
        {
            report.Error(ValidationCodes.CustomerMissing, "The invoice has no resolved customer.", nameof(Invoice.CustomerId));
            return;
        }

        var customer = invoice.Customer;
        if (customer is null)
        {
            return;
        }

        if (customer.IsAnonymised)
        {
            report.Warning(
                ValidationCodes.CustomerAmbiguous,
                "Buyer identity was not available from the source; a placeholder customer is in use.",
                nameof(Invoice.CustomerId));
        }

        if (string.IsNullOrWhiteSpace(customer.VatNumber) && !string.IsNullOrWhiteSpace(customer.CompanyName))
        {
            report.Warning(
                ValidationCodes.VatNumberMissing,
                $"No VAT/UID number is recorded for company '{customer.CompanyName}'.",
                "customer.vatNumber");
        }
        else if (!string.IsNullOrWhiteSpace(customer.VatNumber) && !SwissVatNumber.LooksValid(customer.VatNumber))
        {
            // Deliberately a warning: we validate shape only and do not claim to verify registration.
            report.Warning(
                ValidationCodes.VatNumberMalformed,
                $"The VAT/UID number '{customer.VatNumber}' does not match the expected format for its country.",
                "customer.vatNumber");
        }
    }
}

/// <summary>
/// Shape-only check of a Swiss UID / VAT number (CHE-123.456.789 [MWST|TVA|IVA]) including the
/// modulus-11 check digit. This verifies format, not registration status — we do not query a registry.
/// Non-CH numbers are accepted without a shape claim.
/// </summary>
public static class SwissVatNumber
{
    public static bool LooksValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim().ToUpperInvariant();
        if (!trimmed.StartsWith("CHE", StringComparison.Ordinal))
        {
            // Only Swiss numbers have a shape we claim to know. Anything else passes unchecked.
            return true;
        }

        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 9)
        {
            return false;
        }

        // Official UID check digit: weights 5,4,3,2,7,6,5,4 over the first eight digits, modulus 11.
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4];
        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            sum += (digits[i] - '0') * weights[i];
        }

        var remainder = sum % 11;
        var check = 11 - remainder;
        if (check == 10)
        {
            return false; // 10 is not a valid check digit; such a UID is never issued
        }

        if (check == 11)
        {
            check = 0;
        }

        return check == digits[8] - '0';
    }
}
