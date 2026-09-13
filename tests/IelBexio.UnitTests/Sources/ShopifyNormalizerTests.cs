using System.Text.Json;
using IelBexio.Application.Invoices;
using IelBexio.Connectors.Shopify.Normalization;
using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Validation;

namespace IelBexio.UnitTests.Sources;

/// <summary>
/// Normalisation is where source data becomes financial record, so these tests assert on exact
/// amounts. The fixtures are the same files the demo uses, which means the demo cannot silently drift
/// away from what is tested.
/// </summary>
public sealed class ShopifyNormalizerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static JsonElement LoadFixture(string name)
    {
        var path = FixturePath.Resolve("shopify", name);
        var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.Clone();
    }

    private static (global::IelBexio.Domain.Invoicing.Invoice Invoice, IReadOnlyList<InvoiceLine> Lines, IReadOnlyList<string> Notes) Normalize(string fixture)
    {
        var document = new ShopifyOrderNormalizer().Normalize(LoadFixture(fixture), TenantId, null, "2025-10");
        return (document.Invoice, document.Lines, document.Notes);
    }

    [Fact]
    public void The_reference_order_normalises_to_the_exact_amounts_the_specification_states()
    {
        var (invoice, lines, _) = Normalize("order-inv-10001.json");

        invoice.InvoiceNumber.Should().Be("INV-10001");
        invoice.Currency.Should().Be("CHF");
        invoice.SubtotalAmount.Should().Be(1000.00m);
        invoice.TaxAmount.Should().Be(81.00m);
        invoice.TotalAmount.Should().Be(1081.00m);
        invoice.InvoiceDate.Should().Be(new DateOnly(2026, 3, 1));
        invoice.PaymentStatus.Should().Be(PaymentStatus.Paid);

        lines.Should().ContainSingle();
        lines[0].Sku.Should().Be("SKU-ALPHA");
        lines[0].Quantity.Should().Be(4m);
        lines[0].NetAmount.Should().Be(1000.00m);
        lines[0].TaxRatePercent.Should().Be(8.1m);
        lines[0].TaxAmount.Should().Be(81.00m);
    }

    [Fact]
    public void The_reference_order_passes_deterministic_validation_end_to_end()
    {
        var (invoice, lines, _) = Normalize("order-inv-10001.json");

        var report = new InvoiceValidator().Validate(invoice, lines);

        report.HasErrors.Should().BeFalse(because: string.Join("; ", report.Issues));
    }

    [Fact]
    public void Every_canonical_amount_is_traceable_to_its_shopify_source_field()
    {
        var document = new ShopifyOrderNormalizer().Normalize(LoadFixture("order-inv-10001.json"), TenantId, null, "2025-10");

        document.FieldSourceMap["totalAmount"].Should().Be("currentTotalPriceSet.shopMoney.amount");
        document.FieldSourceMap["taxAmount"].Should().Be("totalTaxSet.shopMoney.amount");
        document.FieldSourceMap["customer.vatNumber"].Should().Contain("localizationExtensions");
    }

    [Fact]
    public void The_raw_payload_is_retained_verbatim_for_provenance_and_replay()
    {
        var document = new ShopifyOrderNormalizer().Normalize(LoadFixture("order-inv-10001.json"), TenantId, null, "2025-10");

        document.RawPayload.Should().Contain("gid://shopify/Order/10001");
        JsonDocument.Parse(document.RawPayload).Should().NotBeNull("the retained payload must stay valid JSON");
    }

    [Fact]
    public void The_source_document_version_is_derived_from_updated_at_so_an_edit_is_a_new_version()
    {
        var document = new ShopifyOrderNormalizer().Normalize(LoadFixture("order-inv-10001.json"), TenantId, null, "2025-10");

        var expected = DateTimeOffset.Parse("2026-03-01T09:15:00Z", System.Globalization.CultureInfo.InvariantCulture)
            .ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        document.SourceDocumentVersion.Should().Be(expected);
    }

    [Fact]
    public void The_buyer_tax_credential_is_read_from_localization_extensions()
    {
        var document = new ShopifyOrderNormalizer().Normalize(LoadFixture("order-inv-10001.json"), TenantId, null, "2025-10");

        document.Customer!.VatNumber.Should().Be("CHE-116.281.710");
        document.Customer.CompanyName.Should().Be("ABC Swiss GmbH");
    }

    [Fact]
    public void A_mixed_rate_order_with_discount_and_shipping_normalises_and_validates()
    {
        var (invoice, lines, _) = Normalize("order-inv-10002-mixed-tax.json");

        invoice.DiscountAmount.Should().Be(100.00m);
        invoice.ShippingAmount.Should().Be(50.00m);
        invoice.TaxAmount.Should().Be(165.75m);
        invoice.TotalAmount.Should().Be(2565.75m);

        lines.Should().HaveCount(4, "three product lines plus a shipping line");
        lines.Select(l => l.TaxRatePercent).Should().Contain([8.1m, 2.6m, 0m]);
        lines.Should().ContainSingle(l => l.Kind == LineKind.Shipping);

        var report = new InvoiceValidator().Validate(invoice, lines);
        report.HasErrors.Should().BeFalse(because: string.Join("; ", report.Issues));
    }

    [Fact]
    public void Shopify_shipping_becomes_an_explicit_line_so_it_can_be_booked_to_its_own_account()
    {
        var (_, lines, _) = Normalize("order-inv-10002-mixed-tax.json");

        var shipping = lines.Single(l => l.Kind == LineKind.Shipping);
        shipping.NetAmount.Should().Be(50.00m);
        shipping.TaxAmount.Should().Be(4.05m);
    }

    [Fact]
    public void A_foreign_currency_order_keeps_its_own_currency_and_is_never_converted()
    {
        var (invoice, lines, _) = Normalize("order-inv-10003-foreign-currency.json");

        invoice.Currency.Should().Be("EUR");
        invoice.TotalAmount.Should().Be(571.20m);
        lines.Should().OnlyContain(l => l.Currency == "EUR");
    }

    [Fact]
    public void The_deliberately_broken_fixture_is_caught_by_validation_rather_than_imported_silently()
    {
        var (invoice, lines, _) = Normalize("order-inv-10004-invalid-total.json");

        var report = new InvoiceValidator().Validate(invoice, lines);

        report.HasErrors.Should().BeTrue("this fixture exists precisely to prove validation catches it");
        report.Contains(ValidationCodes.TotalsDoNotBalance).Should().BeTrue();
    }

    [Fact]
    public void A_company_order_with_no_tax_credential_produces_a_note_and_a_validation_warning()
    {
        var document = new ShopifyOrderNormalizer().Normalize(LoadFixture("order-inv-10005-missing-vat.json"), TenantId, null, "2025-10");

        document.Customer!.VatNumber.Should().BeNull();
        document.Notes.Should().Contain(n => n.Contains("tax credential", StringComparison.OrdinalIgnoreCase));

        var report = new InvoiceValidator().Validate(document.Invoice, document.Lines);
        report.Contains(ValidationCodes.VatNumberMissing).Should().BeTrue();
        report.HasErrors.Should().BeFalse("a missing VAT number routes to review; it does not block the import");
    }

    [Fact]
    public void A_refunded_order_becomes_a_credit_note_rather_than_an_invoice_with_odd_signs()
    {
        var (invoice, lines, _) = Normalize("order-inv-10006-refund.json");

        invoice.DocumentStatus.Should().Be(DocumentStatus.CreditNote);
        invoice.TotalAmount.Should().Be(-270.25m);
        invoice.PaymentStatus.Should().Be(PaymentStatus.Refunded);

        var report = new InvoiceValidator().Validate(invoice, lines);
        report.Contains(ValidationCodes.NegativeTotal).Should().BeFalse("a credit note is legitimately negative");
    }

    [Fact]
    public void A_refund_transaction_is_recorded_with_a_negative_sign()
    {
        var document = new ShopifyOrderNormalizer().Normalize(LoadFixture("order-inv-10006-refund.json"), TenantId, null, "2025-10");

        document.Payments.Should().ContainSingle();
        document.Payments[0].Amount.Should().Be(-270.25m, "a refund reduces what was received");
    }

    [Fact]
    public void A_money_amount_in_an_unexpected_currency_is_refused_rather_than_silently_mixed()
    {
        var malformed = JsonDocument.Parse("""
            {
              "id": "gid://shopify/Order/999",
              "name": "BAD-1",
              "updatedAt": "2026-03-01T09:15:00Z",
              "currencyCode": "CHF",
              "currentTotalPriceSet": { "shopMoney": { "amount": "100.00", "currencyCode": "EUR" } }
            }
            """).RootElement;

        var act = () => new ShopifyOrderNormalizer().Normalize(malformed, TenantId, null, "2025-10");

        act.Should().Throw<ShopifyNormalizationException>().WithMessage("*EUR*CHF*");
    }

    [Fact]
    public void An_unparseable_money_amount_throws_rather_than_becoming_zero()
    {
        var malformed = JsonDocument.Parse("""
            {
              "id": "gid://shopify/Order/998",
              "name": "BAD-2",
              "updatedAt": "2026-03-01T09:15:00Z",
              "currencyCode": "CHF",
              "currentTotalPriceSet": { "shopMoney": { "amount": true, "currencyCode": "CHF" } }
            }
            """).RootElement;

        var act = () => new ShopifyOrderNormalizer().Normalize(malformed, TenantId, null, "2025-10");

        act.Should().Throw<ShopifyNormalizationException>(
            "a silently zeroed amount is the most dangerous failure mode in an accounting integration");
    }

    [Fact]
    public void An_order_with_no_id_is_refused()
    {
        var malformed = JsonDocument.Parse("""{ "name": "NO-ID" }""").RootElement;

        var act = () => new ShopifyOrderNormalizer().Normalize(malformed, TenantId, null, "2025-10");

        act.Should().Throw<ShopifyNormalizationException>();
    }

    [Fact]
    public void Every_produced_entity_carries_the_tenant_id()
    {
        var document = new ShopifyOrderNormalizer().Normalize(LoadFixture("order-inv-10002-mixed-tax.json"), TenantId, null, "2025-10");

        document.Invoice.TenantId.Should().Be(TenantId);
        document.Customer!.TenantId.Should().Be(TenantId);
        document.Lines.Should().OnlyContain(l => l.TenantId == TenantId);
    }
}

/// <summary>Locates the repository's fixtures directory regardless of where the test host runs from.</summary>
internal static class FixturePath
{
    public static string Resolve(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, "fixtures", .. segments]);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate fixtures/{string.Join('/', segments)} above {AppContext.BaseDirectory}.");
    }

    /// <summary>Resolves a fixture sub-directory, e.g. "shopify" or "amazon".</summary>
    public static string ResolveDirectory(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "fixtures", name);
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate fixtures/{name} above {AppContext.BaseDirectory}.");
    }
}
