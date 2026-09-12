using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Tax;

namespace IelBexio.Application.Tax;

/// <summary>
/// One row of the deterministic tax rule table. Rates are data, not code, so a rate change is a
/// configuration change and the version stamp makes old conclusions interpretable.
/// </summary>
public sealed record TaxRule
{
    public required string CountryCode { get; init; }
    public required TaxType TaxType { get; init; }
    public required decimal RatePercent { get; init; }

    /// <summary>Our stable internal code, e.g. "CH-VAT-STD-8.1".</summary>
    public required string InternalTaxCode { get; init; }

    public DateOnly ValidFrom { get; init; } = new(2000, 1, 1);
    public DateOnly? ValidTo { get; init; }
    public string? Description { get; init; }

    public bool IsValidOn(DateOnly date) => date >= ValidFrom && (ValidTo is null || date <= ValidTo);
}

/// <summary>
/// The configured tax rule table.
/// <para>
/// <b>Scope warning.</b> This is deliberately a small lookup of published headline rates so the POC can
/// classify demo invoices — it is explicitly <em>not</em> a tax engine (§39) and makes no determination
/// about place of supply, export treatment, reverse charge or registration thresholds. Anything it
/// cannot classify confidently is routed to a human, which is the entire point of the design.
/// </para>
/// </summary>
public sealed class TaxRuleTable
{
    public const string Version = "1.0.0";

    private readonly IReadOnlyList<TaxRule> _rules;

    public TaxRuleTable(IEnumerable<TaxRule>? rules = null) => _rules = (rules ?? DefaultRules()).ToList();

    public IReadOnlyList<TaxRule> Rules => _rules;

    /// <summary>
    /// Default demo rules. Swiss rates as published from 2024-01-01 (standard 8.1%, reduced 2.6%,
    /// accommodation 3.8%). These are demo configuration values, not tax advice.
    /// </summary>
    public static IReadOnlyList<TaxRule> DefaultRules() =>
    [
        new() { CountryCode = "CH", TaxType = TaxType.StandardRate, RatePercent = 8.1m, InternalTaxCode = "CH-VAT-STD-8.1", ValidFrom = new DateOnly(2024, 1, 1), Description = "Swiss VAT standard rate" },
        new() { CountryCode = "CH", TaxType = TaxType.ReducedRate, RatePercent = 2.6m, InternalTaxCode = "CH-VAT-RED-2.6", ValidFrom = new DateOnly(2024, 1, 1), Description = "Swiss VAT reduced rate" },
        new() { CountryCode = "CH", TaxType = TaxType.SpecialRate, RatePercent = 3.8m, InternalTaxCode = "CH-VAT-ACC-3.8", ValidFrom = new DateOnly(2024, 1, 1), Description = "Swiss VAT accommodation rate" },
        new() { CountryCode = "CH", TaxType = TaxType.StandardRate, RatePercent = 7.7m, InternalTaxCode = "CH-VAT-STD-7.7", ValidFrom = new DateOnly(2018, 1, 1), ValidTo = new DateOnly(2023, 12, 31), Description = "Swiss VAT standard rate (pre-2024)" },
        new() { CountryCode = "CH", TaxType = TaxType.ZeroRated, RatePercent = 0m, InternalTaxCode = "CH-VAT-ZERO", Description = "Zero-rated / exempt with credit" },
        new() { CountryCode = "*", TaxType = TaxType.ZeroRated, RatePercent = 0m, InternalTaxCode = "GEN-ZERO", Description = "Zero rate, any jurisdiction" },
    ];

    /// <summary>Finds the rule matching a country and rate on a date. Country-specific rules win over wildcards.</summary>
    public TaxRule? Find(string? countryCode, decimal ratePercent, DateOnly onDate)
    {
        var country = (countryCode ?? "*").ToUpperInvariant();

        return _rules
            .Where(r => r.IsValidOn(onDate))
            .Where(r => r.RatePercent == ratePercent)
            .Where(r => string.Equals(r.CountryCode, country, StringComparison.Ordinal) || r.CountryCode == "*")
            .OrderBy(r => r.CountryCode == "*" ? 1 : 0)
            .FirstOrDefault();
    }

    public IReadOnlyList<TaxRule> ForCountry(string? countryCode, DateOnly onDate)
    {
        var country = (countryCode ?? "*").ToUpperInvariant();
        return _rules
            .Where(r => r.IsValidOn(onDate))
            .Where(r => string.Equals(r.CountryCode, country, StringComparison.Ordinal) || r.CountryCode == "*")
            .ToList();
    }
}

/// <summary>
/// Produces a <see cref="TaxAssessment"/> per invoice line, deterministically (§6).
/// <para>
/// The order of preference is: what the source told us → what our rule table concludes → back-computed
/// from the amounts on the document → undetermined. AI never appears in this chain; an AI tax
/// suggestion is written as a separate <c>AiProposal</c> that a human must accept, and accepting it
/// records <see cref="TaxDeterminationMethod.HumanEntered"/>, not <c>AiProposed</c>.
/// </para>
/// </summary>
public sealed class TaxDeterminationService
{
    private readonly TaxRuleTable _ruleTable;

    public TaxDeterminationService(TaxRuleTable ruleTable) => _ruleTable = ruleTable;

    /// <summary>Confidence at or above this is treated as "no review needed on tax grounds".</summary>
    public const decimal HighConfidence = 0.95m;

    /// <summary>Below this the record is routed to human review.</summary>
    public const decimal ReviewThreshold = 0.80m;

    public IReadOnlyList<TaxAssessment> Determine(Invoice invoice, IReadOnlyList<InvoiceLine> lines)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(lines);

        var onDate = invoice.InvoiceDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        // Place of supply is a genuinely hard question. For the POC we use the billing country and say
        // so plainly in the rationale, so a reviewer knows exactly what assumption they are checking.
        var country = FirstNonEmpty(
            invoice.BillingAddress.CountryCode,
            invoice.Customer?.CountryCode,
            invoice.ShippingAddress.CountryCode);

        var assessments = new List<TaxAssessment>(lines.Count);

        foreach (var line in lines)
        {
            assessments.Add(DetermineForLine(invoice, line, country, onDate));
        }

        return assessments;
    }

    private TaxAssessment DetermineForLine(Invoice invoice, InvoiceLine line, string? country, DateOnly onDate)
    {
        var assessment = new TaxAssessment
        {
            TenantId = invoice.TenantId,
            InvoiceId = invoice.Id,
            InvoiceLineId = line.Id,
            CountryCode = country,
            Jurisdiction = country,
            Currency = line.Currency,
            TaxableAmount = line.NetAmount,
            TaxAmount = line.TaxAmount,
            DeterminationVersion = TaxRuleTable.Version,
        };

        // 1. Rate the source actually stated, if any.
        var effectiveRate = line.TaxRatePercent;

        // 2. If no rate was stated but amounts are present, back-compute it deterministically.
        var backComputed = false;
        if (effectiveRate == 0m && line.TaxAmount != 0m && line.NetAmount != 0m)
        {
            effectiveRate = decimal.Round(line.TaxAmount / line.NetAmount * 100m, 2, MidpointRounding.ToEven);
            backComputed = true;
        }

        assessment.RatePercent = effectiveRate;
        assessment.SourceTaxRatePercent = line.TaxRatePercent == 0m ? null : line.TaxRatePercent;

        var rule = _ruleTable.Find(country, effectiveRate, onDate);

        if (rule is not null)
        {
            assessment.InternalTaxCode = rule.InternalTaxCode;
            assessment.TaxType = rule.TaxType;
            assessment.DeterminationMethod = backComputed
                ? TaxDeterminationMethod.DerivedFromAmounts
                : TaxDeterminationMethod.RuleTable;

            // Full confidence only when the source stated the rate and the rule table agrees on a
            // country we actually have rules for.
            assessment.ConfidenceSignal = (backComputed, country is null, rule.CountryCode == "*") switch
            {
                (false, false, false) => 1.00m,
                (true, false, false) => 0.90m,
                (false, _, true) => 0.70m,
                _ => 0.60m,
            };

            assessment.Rationale =
                $"Matched rule '{rule.InternalTaxCode}' ({rule.RatePercent}% {rule.TaxType}) for country " +
                $"{rule.CountryCode} valid on {onDate:yyyy-MM-dd}. Rate {(backComputed ? "back-computed from tax ÷ net" : "taken from the source document")}. " +
                $"Place of supply assumed to be the billing country — verify.";
        }
        else if (effectiveRate == 0m && line.TaxAmount == 0m)
        {
            assessment.InternalTaxCode = "GEN-ZERO";
            assessment.TaxType = TaxType.ZeroRated;
            assessment.DeterminationMethod = TaxDeterminationMethod.SourceProvided;
            assessment.ConfidenceSignal = 0.75m;
            assessment.Rationale =
                "The source reported no tax for this line. Classified as zero-rated, but whether this is " +
                "zero-rating, exemption, export or reverse charge cannot be determined from the data available — verify.";
        }
        else
        {
            assessment.TaxType = TaxType.Unknown;
            assessment.DeterminationMethod = TaxDeterminationMethod.Undetermined;
            assessment.ConfidenceSignal = 0m;
            assessment.Rationale =
                $"No tax rule matches {effectiveRate}% for country '{country ?? "(unknown)"}' on {onDate:yyyy-MM-dd}. " +
                "Human determination required.";
        }

        return assessment;
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.ToUpperInvariant();
}
