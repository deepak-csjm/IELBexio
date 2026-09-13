using IelBexio.Application.Tax;
using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Tax;

namespace IelBexio.UnitTests.Tax;

/// <summary>
/// Tax determination must be deterministic and must degrade to "ask a human" rather than to a guess.
/// These tests pin both halves of that contract (§6).
/// </summary>
public sealed class TaxDeterminationTests
{
    private static TaxDeterminationService CreateService() => new(new TaxRuleTable());

    private static (Invoice Invoice, InvoiceLine Line) Build(
        decimal net, decimal ratePercent, decimal taxAmount, string country = "CH", DateOnly? date = null)
    {
        var tenantId = Guid.CreateVersion7();
        var invoice = new Invoice
        {
            TenantId = tenantId,
            SourceSystem = SourceSystem.Shopify,
            SourceDocumentId = "o-1",
            Currency = "CHF",
            InvoiceDate = date ?? new DateOnly(2026, 3, 1),
        };
        invoice.BillingAddress.CountryCode = country;

        var line = new InvoiceLine
        {
            TenantId = tenantId,
            InvoiceId = invoice.Id,
            LineNumber = 1,
            Description = "Item",
            Quantity = 1m,
            UnitPrice = net,
            NetAmount = net,
            TaxRatePercent = ratePercent,
            TaxAmount = taxAmount,
            GrossAmount = net + taxAmount,
            Currency = "CHF",
        };

        return (invoice, line);
    }

    [Fact]
    public void The_swiss_standard_rate_is_matched_with_full_confidence_when_the_source_stated_it()
    {
        var (invoice, line) = Build(1000m, 8.1m, 81m);

        var assessment = CreateService().Determine(invoice, [line]).Single();

        assessment.InternalTaxCode.Should().Be("CH-VAT-STD-8.1");
        assessment.TaxType.Should().Be(TaxType.StandardRate);
        assessment.RatePercent.Should().Be(8.1m);
        assessment.DeterminationMethod.Should().Be(TaxDeterminationMethod.RuleTable);
        assessment.ConfidenceSignal.Should().Be(1.00m);
        assessment.HumanVerified.Should().BeFalse("determination alone is never verification");
    }

    [Fact]
    public void The_rate_is_back_computed_when_the_source_supplied_only_amounts()
    {
        var (invoice, line) = Build(1000m, ratePercent: 0m, taxAmount: 81m);

        var assessment = CreateService().Determine(invoice, [line]).Single();

        assessment.RatePercent.Should().Be(8.1m);
        assessment.InternalTaxCode.Should().Be("CH-VAT-STD-8.1");
        assessment.DeterminationMethod.Should().Be(TaxDeterminationMethod.DerivedFromAmounts);
        assessment.ConfidenceSignal.Should().BeLessThan(1.00m, "a derived rate deserves less trust than a stated one");
    }

    [Fact]
    public void The_historic_swiss_rate_applies_to_a_historic_invoice_date()
    {
        var (invoice, line) = Build(1000m, 7.7m, 77m, date: new DateOnly(2023, 6, 1));

        var assessment = CreateService().Determine(invoice, [line]).Single();

        assessment.InternalTaxCode.Should().Be("CH-VAT-STD-7.7");
    }

    [Fact]
    public void The_current_rate_table_does_not_apply_the_old_rate_to_a_current_invoice()
    {
        var (invoice, line) = Build(1000m, 7.7m, 77m, date: new DateOnly(2026, 3, 1));

        var assessment = CreateService().Determine(invoice, [line]).Single();

        // 7.7% is no longer valid in 2026, so this must land as undetermined and go to a human.
        assessment.DeterminationMethod.Should().Be(TaxDeterminationMethod.Undetermined);
        assessment.ConfidenceSignal.Should().Be(0m);
    }

    [Fact]
    public void A_zero_tax_line_is_classified_as_zero_rated_but_not_with_full_confidence()
    {
        var (invoice, line) = Build(1000m, 0m, 0m);

        var assessment = CreateService().Determine(invoice, [line]).Single();

        assessment.TaxType.Should().Be(TaxType.ZeroRated);
        assessment.ConfidenceSignal.Should().BeLessThan(TaxDeterminationService.HighConfidence,
            "zero tax could be zero-rating, exemption, export or reverse charge — that needs a human");
        assessment.Rationale.Should().Contain("verify");
    }

    [Fact]
    public void An_unmatched_rate_is_undetermined_rather_than_guessed()
    {
        var (invoice, line) = Build(1000m, 19m, 190m); // a German rate on a Swiss-billed invoice

        var assessment = CreateService().Determine(invoice, [line]).Single();

        assessment.DeterminationMethod.Should().Be(TaxDeterminationMethod.Undetermined);
        assessment.InternalTaxCode.Should().BeNull();
        assessment.ConfidenceSignal.Should().Be(0m);
        assessment.ConfidenceSignal.Should().BeLessThan(TaxDeterminationService.ReviewThreshold);
    }

    [Fact]
    public void An_unknown_billing_country_lowers_confidence_below_the_review_threshold()
    {
        var (invoice, line) = Build(1000m, 0m, 0m, country: null!);
        invoice.BillingAddress.CountryCode = null;

        var assessment = CreateService().Determine(invoice, [line]).Single();

        assessment.ConfidenceSignal.Should().BeLessThan(TaxDeterminationService.HighConfidence);
    }

    [Fact]
    public void A_mixed_rate_invoice_produces_one_assessment_per_line()
    {
        var (invoice, standardLine) = Build(1000m, 8.1m, 81m);
        var reducedLine = new InvoiceLine
        {
            TenantId = invoice.TenantId, InvoiceId = invoice.Id, LineNumber = 2,
            Description = "Printed Manual", Quantity = 1m, UnitPrice = 100m, NetAmount = 100m,
            TaxRatePercent = 2.6m, TaxAmount = 2.60m, GrossAmount = 102.60m, Currency = "CHF",
        };

        var assessments = CreateService().Determine(invoice, [standardLine, reducedLine]);

        assessments.Should().HaveCount(2);
        assessments[0].InternalTaxCode.Should().Be("CH-VAT-STD-8.1");
        assessments[1].InternalTaxCode.Should().Be("CH-VAT-RED-2.6");
    }

    [Fact]
    public void Every_assessment_records_the_determination_version_so_old_conclusions_stay_interpretable()
    {
        var (invoice, line) = Build(1000m, 8.1m, 81m);

        CreateService().Determine(invoice, [line]).Single()
            .DeterminationVersion.Should().Be(TaxRuleTable.Version);
    }

    [Fact]
    public void No_assessment_is_ever_produced_with_the_ai_proposed_method()
    {
        // The deterministic path must never label its own output as AI-derived; AI suggestions live in
        // ai_proposals and only reach an assessment through explicit human acceptance (§12, §40).
        var (invoice, line) = Build(1000m, 8.1m, 81m);

        CreateService().Determine(invoice, [line])
            .Should().OnlyContain(a => a.DeterminationMethod != TaxDeterminationMethod.AiProposed);
    }

    [Fact]
    public void The_rule_table_prefers_a_country_specific_rule_over_the_wildcard()
    {
        var table = new TaxRuleTable();

        table.Find("CH", 0m, new DateOnly(2026, 3, 1))!.InternalTaxCode.Should().Be("CH-VAT-ZERO");
        table.Find("DE", 0m, new DateOnly(2026, 3, 1))!.InternalTaxCode.Should().Be("GEN-ZERO");
    }
}
