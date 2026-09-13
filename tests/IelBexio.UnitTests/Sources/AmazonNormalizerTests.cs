using System.Text.Json;
using IelBexio.Application.Invoices;
using IelBexio.Application.Sources;
using IelBexio.Connectors.Amazon.Fixtures;
using IelBexio.Connectors.Amazon.Normalization;
using IelBexio.Domain.Common;
using IelBexio.Domain.Validation;

namespace IelBexio.UnitTests.Sources;

/// <summary>
/// Amazon differs from Shopify in ways that are easy to get silently wrong, so the differences are
/// asserted explicitly rather than assumed (§10).
/// </summary>
public sealed class AmazonNormalizerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static SourceInvoiceDocument Normalize(string fixture)
    {
        var path = FixturePath.Resolve("amazon", fixture);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return new AmazonOrderNormalizer().Normalize(json.RootElement, TenantId, null, "orders/v0");
    }

    [Fact]
    public void Item_price_is_treated_as_tax_inclusive_so_net_is_price_minus_tax()
    {
        // This is the single most consequential Amazon-vs-Shopify difference: ItemPrice includes tax.
        // Treating it as net would overstate revenue on every Amazon order.
        var document = Normalize("order-902-1234567-1234567.json");

        var alpha = document.Lines.Single(l => l.Sku == "SKU-ALPHA");
        alpha.NetAmount.Should().Be(170.16m, "210.08 gross − 39.92 tax");
        alpha.TaxAmount.Should().Be(39.92m);
        alpha.GrossAmount.Should().Be(210.08m);
    }

    [Fact]
    public void The_tax_rate_is_back_computed_because_amazon_reports_amounts_not_rates()
    {
        var document = Normalize("order-902-1234567-1234567.json");

        var alpha = document.Lines.Single(l => l.Sku == "SKU-ALPHA");

        // 39.92 / 170.16 = 23.46%... which is NOT a real German VAT rate. That is the point: the
        // derived number is reported honestly and a human is told to verify it, rather than the system
        // silently asserting a plausible-looking rate.
        alpha.TaxRatePercent.Should().BeApproximately(23.4602m, 0.001m);

        document.Notes.Should().Contain(n => n.Contains("back-computed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Absent_buyer_identity_produces_an_anonymised_customer_and_never_an_invented_one()
    {
        var document = Normalize("order-902-1234567-1234567.json");

        document.Customer!.IsAnonymised.Should().BeTrue();
        document.Customer.Email.Should().BeNull();
        document.Customer.LastName.Should().BeNull();
        document.Customer.SourceCustomerId.Should().Be("amazon-order:902-1234567-1234567");
        document.Notes.Should().Contain(n => n.Contains("no buyer name or email", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_anonymised_customer_is_flagged_for_review_by_the_validator()
    {
        var document = Normalize("order-902-1234567-1234567.json");

        var report = new InvoiceValidator().Validate(document.Invoice, document.Lines);

        report.Contains(ValidationCodes.CustomerAmbiguous).Should().BeTrue();
    }

    [Fact]
    public void Buyer_identity_is_used_when_amazon_does_supply_it()
    {
        var document = Normalize("order-902-7654321-7654321.json");

        document.Customer!.IsAnonymised.Should().BeFalse();
        document.Customer.Email.Should().Be("sofia.keller@buyer.example");
        document.Customer.CompanyName.Should().Be("Keller Handel e.K.");
    }

    [Fact]
    public void The_amazon_order_id_is_used_as_the_document_reference_because_amazon_issues_no_invoice_number()
    {
        var document = Normalize("order-902-7654321-7654321.json");

        document.Invoice.InvoiceNumber.Should().Be("902-7654321-7654321");
        document.SourceDocumentId.Should().Be("902-7654321-7654321");
    }

    [Fact]
    public void Header_totals_are_computed_from_the_lines_and_validate_consistently()
    {
        var document = Normalize("order-902-7654321-7654321.json");

        document.Invoice.SubtotalAmount.Should().Be(81.00m, "100.00 gross − 19.00 tax");
        document.Invoice.TaxAmount.Should().Be(19.00m);
        document.Invoice.TotalAmount.Should().Be(100.00m);

        var report = new InvoiceValidator().Validate(document.Invoice, document.Lines);
        report.HasErrors.Should().BeFalse(because: string.Join("; ", report.Issues));
    }

    [Fact]
    public void A_computed_total_that_disagrees_with_amazons_own_order_total_is_surfaced()
    {
        // The 902-1234567 fixture's OrderTotal (297.50) deliberately does not reconcile with its item
        // data, modelling the common case of an uni-temised charge.
        var document = Normalize("order-902-1234567-1234567.json");

        document.Notes.Should().Contain(n => n.Contains("OrderTotal", StringComparison.Ordinal));
    }

    [Fact]
    public void The_shipping_address_is_reused_for_billing_and_that_assumption_is_stated()
    {
        var document = Normalize("order-902-7654321-7654321.json");

        document.Invoice.BillingAddress.City.Should().Be("München");
        document.Notes.Should().Contain(n => n.Contains("billing address", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_raw_payload_is_retained_for_provenance()
    {
        var document = Normalize("order-902-1234567-1234567.json");

        document.RawPayload.Should().Contain("902-1234567-1234567");
        JsonDocument.Parse(document.RawPayload).Should().NotBeNull();
    }

    [Fact]
    public void An_order_with_no_id_is_refused()
    {
        using var malformed = JsonDocument.Parse("""{ "order": { "OrderStatus": "Shipped" } }""");

        var act = () => new AmazonOrderNormalizer().Normalize(malformed.RootElement, TenantId, null, "orders/v0");

        act.Should().Throw<AmazonNormalizationException>();
    }

    [Fact]
    public void Mixed_currencies_within_one_order_are_refused()
    {
        using var malformed = JsonDocument.Parse("""
            {
              "order": {
                "AmazonOrderId": "902-0000000-0000000",
                "PurchaseDate": "2026-03-01T00:00:00Z",
                "OrderTotal": { "CurrencyCode": "EUR", "Amount": "100.00" }
              },
              "orderItems": [
                { "SellerSKU": "X", "Title": "X", "QuantityOrdered": 1,
                  "ItemPrice": { "CurrencyCode": "USD", "Amount": "100.00" } }
              ]
            }
            """);

        var act = () => new AmazonOrderNormalizer().Normalize(malformed.RootElement, TenantId, null, "orders/v0");

        act.Should().Throw<AmazonNormalizationException>().WithMessage("*USD*EUR*");
    }

    [Fact]
    public void An_order_with_no_items_is_noted_rather_than_silently_producing_an_empty_invoice()
    {
        using var payload = JsonDocument.Parse("""
            {
              "order": {
                "AmazonOrderId": "902-0000000-0000001",
                "PurchaseDate": "2026-03-01T00:00:00Z",
                "OrderTotal": { "CurrencyCode": "EUR", "Amount": "0.00" }
              }
            }
            """);

        var document = new AmazonOrderNormalizer().Normalize(payload.RootElement, TenantId, null, "orders/v0");

        document.Lines.Should().BeEmpty();
        document.Notes.Should().Contain(n => n.Contains("no order items", StringComparison.OrdinalIgnoreCase));

        new InvoiceValidator().Validate(document.Invoice, document.Lines)
            .Contains(ValidationCodes.NoLines).Should().BeTrue();
    }

    [Fact]
    public void The_documented_amazon_restrictions_are_available_as_data_for_the_ui()
    {
        AmazonRestrictions.All.Should().NotBeEmpty();
        AmazonRestrictions.All.Should().Contain(r => r.Contains("Restricted Data Token", StringComparison.Ordinal));
        AmazonRestrictions.All.Should().Contain(r => r.Contains("tax-inclusive", StringComparison.Ordinal));
    }
}
