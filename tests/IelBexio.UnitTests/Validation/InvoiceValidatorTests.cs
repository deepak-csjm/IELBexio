using IelBexio.Application.Invoices;
using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Validation;
using Microsoft.Extensions.Time.Testing;

namespace IelBexio.UnitTests.Validation;

/// <summary>
/// Deterministic validation is the control that stands between bad data and the accounting system. It
/// runs with AI disabled and must catch arithmetic problems on its own (§7, §21).
/// </summary>
public sealed class InvoiceValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private static InvoiceValidator CreateValidator(ValidationTolerances? tolerances = null) =>
        new(tolerances, new FakeTimeProvider(Now));

    /// <summary>The §31 reference invoice: net 1000 CHF, VAT 8.1% = 81, total 1081.</summary>
    private static (Invoice Invoice, List<InvoiceLine> Lines) ReferenceInvoice()
    {
        var tenantId = Guid.CreateVersion7();
        var customer = new Customer
        {
            TenantId = tenantId,
            CompanyName = "ABC Swiss GmbH",
            CountryCode = "CH",
            VatNumber = "CHE-116.281.710",
        };

        var invoice = new Invoice
        {
            TenantId = tenantId,
            SourceSystem = SourceSystem.Shopify,
            SourceDocumentId = "gid://shopify/Order/10001",
            InvoiceNumber = "INV-10001",
            InvoiceDate = new DateOnly(2026, 3, 1),
            DueDate = new DateOnly(2026, 3, 31),
            Currency = "CHF",
            CustomerId = customer.Id,
            Customer = customer,
            SubtotalAmount = 1000m,
            DiscountAmount = 0m,
            ShippingAmount = 0m,
            TaxAmount = 81m,
            TotalAmount = 1081m,
        };

        invoice.BillingAddress.CountryCode = "CH";

        var lines = new List<InvoiceLine>
        {
            new()
            {
                TenantId = tenantId,
                InvoiceId = invoice.Id,
                LineNumber = 1,
                Description = "Alpha Widget",
                Quantity = 4m,
                UnitPrice = 250m,
                NetAmount = 1000m,
                TaxRatePercent = 8.1m,
                TaxAmount = 81m,
                GrossAmount = 1081m,
                Currency = "CHF",
            },
        };

        return (invoice, lines);
    }

    [Fact]
    public void The_reference_swiss_invoice_validates_cleanly()
    {
        var (invoice, lines) = ReferenceInvoice();

        var report = CreateValidator().Validate(invoice, lines);

        report.HasErrors.Should().BeFalse(because: string.Join("; ", report.Issues));
        report.Issues.Should().BeEmpty();
    }

    [Fact]
    public void A_total_that_does_not_balance_is_an_error_naming_the_arithmetic()
    {
        var (invoice, lines) = ReferenceInvoice();
        invoice.TotalAmount = 1000m; // tax silently dropped from the total

        var report = CreateValidator().Validate(invoice, lines);

        report.HasErrors.Should().BeTrue();
        report.Contains(ValidationCodes.TotalsDoNotBalance).Should().BeTrue();
        report.Issues.Single(i => i.Code == ValidationCodes.TotalsDoNotBalance)
            .Message.Should().Contain("1081");
    }

    [Fact]
    public void Rounding_drift_within_tolerance_does_not_fail_an_otherwise_correct_invoice()
    {
        var (invoice, lines) = ReferenceInvoice();
        invoice.TotalAmount = 1081.02m; // two rappen of per-line rounding

        var report = CreateValidator().Validate(invoice, lines);

        report.Contains(ValidationCodes.TotalsDoNotBalance).Should().BeFalse();
    }

    [Fact]
    public void Drift_beyond_tolerance_is_reported()
    {
        var (invoice, lines) = ReferenceInvoice();
        invoice.TotalAmount = 1081.60m;

        var report = CreateValidator().Validate(invoice, lines);

        report.Contains(ValidationCodes.TotalsDoNotBalance).Should().BeTrue();
    }

    [Fact]
    public void A_line_whose_tax_contradicts_its_rate_is_caught()
    {
        var (invoice, lines) = ReferenceInvoice();
        lines[0].TaxAmount = 100m;   // not 8.1% of 1000
        lines[0].GrossAmount = 1100m;
        invoice.TaxAmount = 100m;
        invoice.TotalAmount = 1100m;

        var report = CreateValidator().Validate(invoice, lines);

        report.Contains(ValidationCodes.TaxAmountInconsistent).Should().BeTrue();
    }

    [Fact]
    public void A_line_whose_net_contradicts_quantity_times_price_is_caught()
    {
        var (invoice, lines) = ReferenceInvoice();
        lines[0].Quantity = 3m; // 3 x 250 = 750, but net still claims 1000

        var report = CreateValidator().Validate(invoice, lines);

        report.Contains(ValidationCodes.LineArithmeticInvalid).Should().BeTrue();
    }

    [Fact]
    public void A_line_in_a_different_currency_from_its_invoice_is_an_error_not_a_warning()
    {
        var (invoice, lines) = ReferenceInvoice();
        lines[0].Currency = "EUR";

        var report = CreateValidator().Validate(invoice, lines);

        report.Contains(ValidationCodes.CurrencyMismatch).Should().BeTrue();
        report.Issues.Single(i => i.Code == ValidationCodes.CurrencyMismatch).Severity
            .Should().Be(ValidationSeverity.Error);
    }

    [Fact]
    public void An_invoice_with_no_lines_is_an_error()
    {
        var (invoice, _) = ReferenceInvoice();

        var report = CreateValidator().Validate(invoice, []);

        report.Contains(ValidationCodes.NoLines).Should().BeTrue();
    }

    [Fact]
    public void A_missing_invoice_date_is_an_error_and_a_missing_number_only_a_warning()
    {
        var (invoice, lines) = ReferenceInvoice();
        invoice.InvoiceDate = null;
        invoice.InvoiceNumber = null;

        var report = CreateValidator().Validate(invoice, lines);

        report.Issues.Single(i => i.Code == ValidationCodes.InvoiceDateMissing).Severity.Should().Be(ValidationSeverity.Error);
        report.Issues.Single(i => i.Code == ValidationCodes.InvoiceNumberMissing).Severity.Should().Be(ValidationSeverity.Warning);
    }

    [Fact]
    public void A_far_future_invoice_date_is_flagged_as_implausible()
    {
        var (invoice, lines) = ReferenceInvoice();
        invoice.InvoiceDate = new DateOnly(2027, 1, 1);
        invoice.DueDate = null;

        var report = CreateValidator().Validate(invoice, lines);

        report.Contains(ValidationCodes.InvoiceDateImplausible).Should().BeTrue();
    }

    [Fact]
    public void A_due_date_before_the_invoice_date_is_flagged()
    {
        var (invoice, lines) = ReferenceInvoice();
        invoice.DueDate = new DateOnly(2026, 2, 1);

        var report = CreateValidator().Validate(invoice, lines);

        report.Contains(ValidationCodes.DueDateBeforeInvoiceDate).Should().BeTrue();
    }

    [Fact]
    public void An_unresolved_customer_is_an_error_because_bexio_cannot_book_without_a_contact()
    {
        var (invoice, lines) = ReferenceInvoice();
        invoice.CustomerId = null;
        invoice.Customer = null;

        var report = CreateValidator().Validate(invoice, lines);

        report.Issues.Single(i => i.Code == ValidationCodes.CustomerMissing).Severity.Should().Be(ValidationSeverity.Error);
    }

    [Fact]
    public void A_company_with_no_vat_number_is_a_warning_that_routes_to_review()
    {
        var (invoice, lines) = ReferenceInvoice();
        invoice.Customer!.VatNumber = null;

        var report = CreateValidator().Validate(invoice, lines);

        report.Contains(ValidationCodes.VatNumberMissing).Should().BeTrue();
        report.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void An_anonymised_buyer_is_surfaced_rather_than_hidden()
    {
        var (invoice, lines) = ReferenceInvoice();
        invoice.Customer!.IsAnonymised = true;
        invoice.Customer.CompanyName = null;

        var report = CreateValidator().Validate(invoice, lines);

        report.Contains(ValidationCodes.CustomerAmbiguous).Should().BeTrue();
    }

    [Fact]
    public void Header_shipping_is_counted_once_when_there_is_no_shipping_line()
    {
        var (invoice, lines) = ReferenceInvoice();
        invoice.ShippingAmount = 20m;
        invoice.TotalAmount = 1101m;
        lines.Add(new InvoiceLine
        {
            TenantId = invoice.TenantId, InvoiceId = invoice.Id, LineNumber = 2,
            Description = "Rounding", Kind = LineKind.Rounding,
            Quantity = 1m, UnitPrice = 20m, NetAmount = 20m,
            TaxRatePercent = 0m, TaxAmount = 0m, GrossAmount = 20m, Currency = "CHF",
        });

        var report = CreateValidator().Validate(invoice, lines);

        report.HasErrors.Should().BeFalse(because: string.Join("; ", report.Issues));
    }

    [Fact]
    public void Shipping_carried_as_a_line_is_not_double_counted_against_the_header()
    {
        var tenantId = Guid.CreateVersion7();
        var customer = new Customer { TenantId = tenantId, CompanyName = "ABC Swiss GmbH", CountryCode = "CH", VatNumber = "CHE-116.281.710" };
        var invoice = new Invoice
        {
            TenantId = tenantId, SourceSystem = SourceSystem.Shopify, SourceDocumentId = "o-2",
            InvoiceNumber = "INV-2", InvoiceDate = new DateOnly(2026, 3, 1), Currency = "CHF",
            CustomerId = customer.Id, Customer = customer,
            SubtotalAmount = 1020m, ShippingAmount = 20m, TaxAmount = 82.62m, TotalAmount = 1122.62m,
        };
        invoice.BillingAddress.CountryCode = "CH";

        var lines = new List<InvoiceLine>
        {
            new() { TenantId = tenantId, InvoiceId = invoice.Id, LineNumber = 1, Description = "Widget", Quantity = 4m, UnitPrice = 250m, NetAmount = 1000m, TaxRatePercent = 8.1m, TaxAmount = 81m, GrossAmount = 1081m, Currency = "CHF" },
            new() { TenantId = tenantId, InvoiceId = invoice.Id, LineNumber = 2, Description = "Shipping", Kind = LineKind.Shipping, Quantity = 1m, UnitPrice = 20m, NetAmount = 20m, TaxRatePercent = 8.1m, TaxAmount = 1.62m, GrossAmount = 21.62m, Currency = "CHF" },
        };

        var report = CreateValidator().Validate(invoice, lines);

        report.HasErrors.Should().BeFalse(because: string.Join("; ", report.Issues));
    }

    [Fact]
    public void A_negative_total_is_a_warning_on_an_invoice_and_silent_on_a_credit_note()
    {
        var (invoice, lines) = ReferenceInvoice();
        invoice.SubtotalAmount = -1000m;
        invoice.TaxAmount = -81m;
        invoice.TotalAmount = -1081m;
        lines[0].UnitPrice = -250m;
        lines[0].NetAmount = -1000m;
        lines[0].TaxAmount = -81m;
        lines[0].GrossAmount = -1081m;

        CreateValidator().Validate(invoice, lines).Contains(ValidationCodes.NegativeTotal).Should().BeTrue();

        invoice.DocumentStatus = DocumentStatus.CreditNote;
        CreateValidator().Validate(invoice, lines).Contains(ValidationCodes.NegativeTotal).Should().BeFalse();
    }
}

/// <summary>
/// The Swiss UID check-digit implementation. We claim only to validate shape, so these tests pin
/// exactly that claim and no more.
/// </summary>
public sealed class SwissVatNumberTests
{
    [Theory]
    [InlineData("CHE-116.281.710")]
    [InlineData("CHE-105.980.910")]
    [InlineData("CHE116281710 MWST")]
    public void Well_formed_swiss_uid_numbers_pass_the_check_digit(string value)
    {
        SwissVatNumber.LooksValid(value).Should().BeTrue();
    }

    [Theory]
    [InlineData("CHE-116.281.711")]  // correct prefix, wrong check digit
    [InlineData("CHE-105.980.916")]  // correct prefix, wrong check digit
    [InlineData("CHE-000.000.001")]
    [InlineData("CHE-1234")]         // too few digits
    public void Malformed_or_bad_check_digit_swiss_numbers_fail(string value)
    {
        SwissVatNumber.LooksValid(value).Should().BeFalse();
    }

    [Fact]
    public void A_prefix_whose_check_digit_would_be_ten_is_rejected_because_such_a_uid_is_never_issued()
    {
        // 5,4,3,2,7,6,5,4 weighted sum of 10000000 is 5; 11 - 5 = 6, so pick a prefix summing to 1 mod 11.
        // 10000001 -> 5*1 + 4*0*... + 4*1 = 9 -> 11-9 = 2. Instead assert the rule directly on a
        // constructed case: any digit string whose computed check digit is 10 must be refused.
        var rejected = Enumerable.Range(0, 1000)
            .Select(i => i.ToString("D8", System.Globalization.CultureInfo.InvariantCulture))
            .Where(p => ComputedCheckDigit(p) == 10)
            .Take(1)
            .ToList();

        rejected.Should().NotBeEmpty("the test needs at least one prefix that produces check digit 10");
        SwissVatNumber.LooksValid($"CHE{rejected[0]}0").Should().BeFalse();
    }

    private static int ComputedCheckDigit(string eightDigits)
    {
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4];
        var sum = eightDigits.Select((d, i) => (d - '0') * weights[i]).Sum();
        var check = 11 - (sum % 11);
        return check == 11 ? 0 : check;
    }

    [Fact]
    public void Non_swiss_numbers_are_accepted_because_we_make_no_claim_about_their_shape()
    {
        SwissVatNumber.LooksValid("DE123456789").Should().BeTrue();
        SwissVatNumber.LooksValid("ATU12345678").Should().BeTrue();
    }

    [Fact]
    public void An_empty_value_is_not_valid()
    {
        SwissVatNumber.LooksValid(null).Should().BeFalse();
        SwissVatNumber.LooksValid("  ").Should().BeFalse();
    }
}
