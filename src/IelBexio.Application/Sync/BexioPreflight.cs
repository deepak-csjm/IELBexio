using IelBexio.Application.Bexio;
using IelBexio.Application.Mapping;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Mapping;
using IelBexio.Application.Tax;
using IelBexio.Domain.Tax;
using IelBexio.Domain.Validation;
using IelBexio.Domain.Workflow;

namespace IelBexio.Application.Sync;

/// <summary>What the operator sees before approving, and what the worker re-checks before posting (§18).</summary>
public sealed record SyncPreview(
    bool CanSynchronize,
    ValidationReport Report,
    string? BexioContactId,
    string? BexioContactName,
    IReadOnlyList<SyncPreviewLine> Lines,
    decimal TotalNet,
    decimal TotalTax,
    decimal TotalGross,
    string Currency,
    string Destination);

public sealed record SyncPreviewLine(
    int LineNumber,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal NetAmount,
    decimal TaxAmount,
    string? BexioTaxId,
    string? BexioTaxName,
    decimal? BexioTaxRatePercent,
    string? BexioAccountId,
    string? BexioAccountName,
    string? BexioArticleId);

/// <summary>
/// The checks that run before anything is sent to Bexio (§18).
/// <para>
/// Deliberately run twice: once to build the preview an operator approves, and again inside the worker
/// immediately before posting. The second run is not redundant — configuration can change, a Bexio tax
/// can be deactivated, or the connection can be revoked between approval and dispatch, and posting
/// against a stale preview is how integrations write wrong numbers into a ledger.
/// </para>
/// </summary>
public sealed class BexioPreflightService
{
    private readonly IMappingService _mappings;
    private readonly IBexioTokenProvider _tokens;
    private readonly IBexioClient _bexio;
    private readonly Invoices.InvoiceValidator _validator;

    public BexioPreflightService(
        IMappingService mappings,
        IBexioTokenProvider tokens,
        IBexioClient bexio,
        Invoices.InvoiceValidator validator)
    {
        _mappings = mappings;
        _tokens = tokens;
        _bexio = bexio;
        _validator = validator;
    }

    /// <summary>Scopes required to create an invoice. Checked before posting so a scope gap fails early and clearly.</summary>
    private static readonly string[] RequiredScopes = ["kb_invoice_edit"];

    public async Task<SyncPreview> BuildAsync(
        Invoice invoice,
        IReadOnlyList<InvoiceLine> lines,
        IReadOnlyList<TaxAssessment> assessments,
        bool requireConnection = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(assessments);

        // Deterministic validation runs again here rather than being trusted from earlier: the record
        // may have been corrected by a human since it was last validated.
        var report = _validator.Validate(invoice, lines);

        var onDate = invoice.InvoiceDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        // ---- No prior synchronisation (§18, §19) --------------------------------------------------
        if (!string.IsNullOrEmpty(invoice.BexioInvoiceId))
        {
            report.Error(
                ValidationCodes.AlreadySynchronized,
                $"This invoice has already been created in Bexio as {invoice.BexioInvoiceId}; it will not be posted again.",
                nameof(Invoice.BexioInvoiceId));
        }

        if (invoice.WorkflowState == InvoiceWorkflowState.Synced)
        {
            report.Error(ValidationCodes.AlreadySynchronized, "This invoice is already marked as synchronised.");
        }

        // ---- Connection and scopes ------------------------------------------------------------------
        // Only meaningful for an implementation that actually authenticates. Asking the client keeps
        // mode-awareness out of the workflow entirely.
        if (requireConnection && _bexio.RequiresAuthorizedConnection)
        {
            if (!await _tokens.IsConnectedAsync(cancellationToken))
            {
                report.Error(ValidationCodes.BexioNotConnected, "Bexio is not connected for this tenant.");
            }
            else
            {
                var granted = await _tokens.GetGrantedScopesAsync(cancellationToken);
                foreach (var scope in RequiredScopes.Where(s => !granted.Contains(s, StringComparer.Ordinal)))
                {
                    report.Error(
                        ValidationCodes.BexioScopeMissing,
                        $"The Bexio connection does not grant the '{scope}' scope, which is required to create an invoice.");
                }
            }
        }

        // ---- Customer mapping ------------------------------------------------------------------------
        string? contactId = invoice.BexioContactId;
        string? contactName = null;

        if (invoice.Customer is { } customer)
        {
            var mapping = await _mappings.ResolveCustomerAsync(customer, cancellationToken);
            if (mapping.Succeeded)
            {
                contactId ??= mapping.Value!.BexioContactId;
                contactName = mapping.Value!.BexioContactName;
            }
            else if (string.IsNullOrEmpty(contactId))
            {
                report.Error(
                    ValidationCodes.CustomerMappingMissing,
                    $"No Bexio contact is mapped for customer '{customer.DisplayName}'. Map it before approving.",
                    "customer");
            }
        }
        else if (string.IsNullOrEmpty(contactId))
        {
            report.Error(ValidationCodes.CustomerMappingMissing, "The invoice has no customer to map to a Bexio contact.");
        }

        // ---- Currency --------------------------------------------------------------------------------
        await ValidateCurrencyAsync(invoice, report, requireConnection, cancellationToken);

        // ---- Per-line tax and account mappings ---------------------------------------------------------
        var previewLines = new List<SyncPreviewLine>(lines.Count);

        foreach (var line in lines.OrderBy(l => l.LineNumber))
        {
            var assessment = assessments.FirstOrDefault(a => a.InvoiceLineId == line.Id);

            string? taxId = line.BexioTaxId;
            string? taxName = null;
            decimal? taxRate = null;

            if (assessment is null)
            {
                report.Error(
                    ValidationCodes.TaxUndetermined,
                    $"Line {line.LineNumber} has no tax assessment.",
                    $"lines[{line.LineNumber}]");
            }
            else
            {
                if (assessment.DeterminationMethod == TaxDeterminationMethod.Undetermined)
                {
                    report.Error(
                        ValidationCodes.TaxUndetermined,
                        $"Line {line.LineNumber}: the tax treatment could not be determined and needs a human decision.",
                        $"lines[{line.LineNumber}].tax");
                }

                // An unverified low-confidence tax conclusion blocks posting. This is the point where
                // "AI may propose, humans decide" becomes enforceable rather than aspirational (§40).
                if (!assessment.HumanVerified && assessment.ConfidenceSignal < TaxDeterminationService.ReviewThreshold)
                {
                    report.Error(
                        ValidationCodes.TaxUnverified,
                        $"Line {line.LineNumber}: the tax conclusion has a low confidence signal " +
                        $"({assessment.ConfidenceSignal:P0}) and has not been verified by a human.",
                        $"lines[{line.LineNumber}].tax");
                }

                var taxMapping = await _mappings.ResolveTaxAsync(assessment, onDate, cancellationToken);
                if (taxMapping.Succeeded)
                {
                    var mapping = taxMapping.Value!;
                    taxId ??= mapping.BexioTaxId;
                    taxName = mapping.BexioTaxName;
                    taxRate = mapping.BexioTaxRatePercent;

                    if (!mapping.BexioTaxIsActive)
                    {
                        report.Error(
                            ValidationCodes.TaxMappingInactive,
                            $"Line {line.LineNumber}: the mapped Bexio tax '{mapping.BexioTaxName}' is not active and cannot be used.",
                            $"lines[{line.LineNumber}].tax");
                    }

                    // Our computed rate and Bexio's configured rate must agree, or the posted tax will
                    // silently differ from the validated tax.
                    if (mapping.BexioTaxRatePercent is { } bexioRate && Math.Abs(bexioRate - assessment.RatePercent) > 0.001m)
                    {
                        report.Error(
                            ValidationCodes.TaxRateUnexpected,
                            $"Line {line.LineNumber}: this system determined {assessment.RatePercent}% but the mapped Bexio tax " +
                            $"'{mapping.BexioTaxName}' is {bexioRate}%. Posting would book a different tax amount than the one validated.",
                            $"lines[{line.LineNumber}].tax");
                    }
                }
                else if (string.IsNullOrEmpty(taxId))
                {
                    report.Error(
                        ValidationCodes.TaxMappingMissing,
                        $"Line {line.LineNumber}: no Bexio tax is mapped for internal tax code " +
                        $"'{assessment.InternalTaxCode ?? "(none)"}'. {taxMapping.ErrorMessage}",
                        $"lines[{line.LineNumber}].tax");
                }
            }

            // ---- Account mapping -------------------------------------------------------------------
            var accountCode = line.Kind switch
            {
                LineKind.Shipping => InternalAccountCodes.RevenueShipping,
                LineKind.Discount => InternalAccountCodes.RevenueDiscount,
                _ => InternalAccountCodes.RevenueGoods,
            };

            string? accountId = line.BexioAccountId;
            string? accountName = null;

            var accountMapping = await _mappings.ResolveAccountAsync(accountCode, cancellationToken);
            if (accountMapping.Succeeded)
            {
                accountId ??= accountMapping.Value!.BexioAccountId;
                accountName = accountMapping.Value!.BexioAccountName;
            }
            else if (string.IsNullOrEmpty(accountId))
            {
                report.Error(
                    ValidationCodes.AccountMappingMissing,
                    $"Line {line.LineNumber}: no Bexio account is mapped for '{accountCode}'.",
                    $"lines[{line.LineNumber}].account");
            }

            string? articleId = line.BexioArticleId;
            if (articleId is null && !string.IsNullOrWhiteSpace(line.Sku))
            {
                var productMapping = await _mappings.ResolveProductAsync(line.Sku, cancellationToken);
                if (productMapping.Succeeded)
                {
                    // A missing article mapping is deliberately NOT an error: Bexio accepts a custom
                    // position with free text, so an unmapped SKU degrades to a text line rather than
                    // blocking the invoice.
                    articleId = productMapping.Value!.BexioArticleId;
                    accountId ??= productMapping.Value.BexioAccountId;
                }
            }

            previewLines.Add(new SyncPreviewLine(
                line.LineNumber, line.Description, line.Quantity, line.UnitPrice,
                line.NetAmount, line.TaxAmount, taxId, taxName, taxRate, accountId, accountName, articleId));
        }

        var canSynchronize = !report.HasErrors;

        return new SyncPreview(
            canSynchronize,
            report,
            contactId,
            contactName,
            previewLines,
            decimal.Round(lines.Sum(l => l.NetAmount), 2, MidpointRounding.ToEven),
            decimal.Round(lines.Sum(l => l.TaxAmount), 2, MidpointRounding.ToEven),
            decimal.Round(lines.Sum(l => l.GrossAmount), 2, MidpointRounding.ToEven),
            invoice.Currency,
            _bexio.ModeName);
    }

    private async Task ValidateCurrencyAsync(Invoice invoice, ValidationReport report, bool requireConnection, CancellationToken cancellationToken)
    {
        if (!requireConnection)
        {
            return;
        }

        // The mock exposes a currency list too, so this check still runs in mock mode — it is the
        // connection requirement that differs between implementations, not the data.

        try
        {
            var currencies = await _bexio.GetCurrenciesAsync(cancellationToken);

            // An empty list means the endpoint is unavailable — the lowest-confidence entry in the
            // endpoint catalog. Treat that as "cannot check", not as "no currencies are valid".
            if (currencies.Count > 0 &&
                !currencies.Any(c => string.Equals(c.Code, invoice.Currency, StringComparison.OrdinalIgnoreCase)))
            {
                report.Error(
                    ValidationCodes.CurrencyNotSupportedByBexio,
                    $"Currency '{invoice.Currency}' is not configured in the target Bexio account.",
                    nameof(Invoice.Currency));
            }
        }
        catch (BexioApiException ex)
        {
            report.Warning(
                ValidationCodes.CurrencyNotSupportedByBexio,
                $"The currency list could not be retrieved from Bexio ({ex.Category}); currency was not verified.",
                nameof(Invoice.Currency));
        }
    }
}
