namespace IelBexio.Domain.Validation;

public enum ValidationSeverity
{
    /// <summary>Informational; does not affect routing.</summary>
    Info = 0,

    /// <summary>Routes the record to human review but does not block approval once verified.</summary>
    Warning = 1,

    /// <summary>Blocks approval and synchronisation until resolved.</summary>
    Error = 2,
}

/// <summary>
/// One deterministic validation finding. Codes are stable strings so the UI, tests and audit trail
/// can refer to them without depending on message wording.
/// </summary>
public sealed record ValidationIssue(
    string Code,
    ValidationSeverity Severity,
    string Message,
    string? FieldPath = null,
    string? Detail = null)
{
    public override string ToString() => $"[{Severity}] {Code} {FieldPath}: {Message}";
}

/// <summary>Stable validation codes. Referenced by tests and by the review UI.</summary>
public static class ValidationCodes
{
    public const string TotalsDoNotBalance = "TOTALS_DO_NOT_BALANCE";
    public const string LineTotalsDoNotMatchHeader = "LINE_TOTALS_DO_NOT_MATCH_HEADER";
    public const string LineArithmeticInvalid = "LINE_ARITHMETIC_INVALID";
    public const string TaxAmountInconsistent = "TAX_AMOUNT_INCONSISTENT";
    public const string CurrencyMissing = "CURRENCY_MISSING";
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";
    public const string InvoiceDateMissing = "INVOICE_DATE_MISSING";
    public const string InvoiceDateImplausible = "INVOICE_DATE_IMPLAUSIBLE";
    public const string DueDateBeforeInvoiceDate = "DUE_DATE_BEFORE_INVOICE_DATE";
    public const string InvoiceNumberMissing = "INVOICE_NUMBER_MISSING";
    public const string NoLines = "NO_LINES";
    public const string NegativeTotal = "NEGATIVE_TOTAL";
    public const string CustomerMissing = "CUSTOMER_MISSING";
    public const string CustomerAmbiguous = "CUSTOMER_AMBIGUOUS";
    public const string VatNumberMissing = "VAT_NUMBER_MISSING";
    public const string VatNumberMalformed = "VAT_NUMBER_MALFORMED";
    public const string TaxUndetermined = "TAX_UNDETERMINED";
    public const string TaxUnverified = "TAX_UNVERIFIED";
    public const string TaxRateUnexpected = "TAX_RATE_UNEXPECTED";
    public const string DuplicateSourceDocument = "DUPLICATE_SOURCE_DOCUMENT";
    public const string DuplicateInvoiceNumber = "DUPLICATE_INVOICE_NUMBER";
    public const string AlreadySynchronized = "ALREADY_SYNCHRONIZED";
    public const string CustomerMappingMissing = "CUSTOMER_MAPPING_MISSING";
    public const string TaxMappingMissing = "TAX_MAPPING_MISSING";
    public const string TaxMappingInactive = "TAX_MAPPING_INACTIVE";
    public const string AccountMappingMissing = "ACCOUNT_MAPPING_MISSING";
    public const string BexioNotConnected = "BEXIO_NOT_CONNECTED";
    public const string BexioScopeMissing = "BEXIO_SCOPE_MISSING";
    public const string CurrencyNotSupportedByBexio = "CURRENCY_NOT_SUPPORTED_BY_BEXIO";
    public const string ExtractionIncomplete = "EXTRACTION_INCOMPLETE";
    public const string AiProposalPending = "AI_PROPOSAL_PENDING";
}

/// <summary>Outcome of running a validator set. Immutable and safely aggregatable.</summary>
public sealed class ValidationReport
{
    private readonly List<ValidationIssue> _issues = [];

    public IReadOnlyList<ValidationIssue> Issues => _issues;

    public bool HasErrors => _issues.Any(i => i.Severity == ValidationSeverity.Error);
    public bool HasWarnings => _issues.Any(i => i.Severity == ValidationSeverity.Warning);
    public bool IsClean => _issues.Count == 0;

    public ValidationReport Add(ValidationIssue issue)
    {
        _issues.Add(issue);
        return this;
    }

    public ValidationReport AddRange(IEnumerable<ValidationIssue> issues)
    {
        _issues.AddRange(issues);
        return this;
    }

    public ValidationReport Error(string code, string message, string? fieldPath = null, string? detail = null) =>
        Add(new ValidationIssue(code, ValidationSeverity.Error, message, fieldPath, detail));

    public ValidationReport Warning(string code, string message, string? fieldPath = null, string? detail = null) =>
        Add(new ValidationIssue(code, ValidationSeverity.Warning, message, fieldPath, detail));

    public ValidationReport Info(string code, string message, string? fieldPath = null, string? detail = null) =>
        Add(new ValidationIssue(code, ValidationSeverity.Info, message, fieldPath, detail));

    public bool Contains(string code) => _issues.Any(i => string.Equals(i.Code, code, StringComparison.Ordinal));
}
