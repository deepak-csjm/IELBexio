using IelBexio.Application.Ai;
using IelBexio.Domain.Ai;

namespace IelBexio.Ai.Gateway;

/// <summary>Per-invoice and per-month usage the guard needs in order to decide.</summary>
public interface IAiUsageLedger
{
    Task<int> CountCallsForInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<decimal> GetMonthToDateSpendAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken = default);

    /// <summary>Returns a cached response for this key, or null. Part of the §13 extraction cache.</summary>
    Task<string?> TryGetCachedAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task CacheAsync(string cacheKey, string response, TimeSpan lifetime, CancellationToken cancellationToken = default);
}

/// <summary>
/// The pre-flight checks every AI request passes before any network call is made.
/// <para>
/// Separated from the gateway so the policy is independently testable and so it is obvious, on reading,
/// exactly what is enforced and in what order. Cheap local checks come first: there is no reason to
/// query a budget before rejecting a disallowed operation.
/// </para>
/// </summary>
public sealed class AiGuard
{
    private readonly AiOptions _options;
    private readonly IAiUsageLedger _ledger;

    public AiGuard(AiOptions options, IAiUsageLedger ledger)
    {
        _options = options;
        _ledger = ledger;
    }

    /// <summary>Returns a refusal when the request must not proceed, or null when it may.</summary>
    public async Task<AiRefusal?> EvaluateAsync(AiRequest request, string model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_options.Enabled)
        {
            return new AiRefusal(AiFailureReason.Disabled,
                "AI assistance is disabled. Deterministic processing continues; records requiring judgement go to human review.");
        }

        if (!_options.AllowedOperations.Contains(request.Operation))
        {
            return new AiRefusal(AiFailureReason.OperationNotAllowed,
                $"Operation '{request.Operation}' is not in the allowed operation list.");
        }

        if (!_options.AllowedModels.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            return new AiRefusal(AiFailureReason.ModelNotAllowed,
                $"Model '{model}' is not in the allowed model list.");
        }

        if (request.UntrustedContent.Length > _options.MaxUntrustedContentChars)
        {
            return new AiRefusal(AiFailureReason.InputTooLarge,
                $"The content is {request.UntrustedContent.Length} characters, above the {_options.MaxUntrustedContentChars} limit. " +
                "The document is routed to human review rather than being truncated, because a truncated " +
                "invoice extraction is worse than none.");
        }

        if (request.InvoiceId is { } invoiceId)
        {
            var used = await _ledger.CountCallsForInvoiceAsync(invoiceId, cancellationToken);
            if (used >= _options.MaxCallsPerInvoice)
            {
                return new AiRefusal(AiFailureReason.CallLimitExceeded,
                    $"This invoice has already used {used} AI calls, at the configured cap of {_options.MaxCallsPerInvoice}.");
            }
        }

        var spend = await _ledger.GetMonthToDateSpendAsync(DateTimeOffset.UtcNow, cancellationToken);
        if (spend >= _options.MonthlyBudget)
        {
            return new AiRefusal(AiFailureReason.BudgetExceeded,
                $"The monthly AI budget of {_options.MonthlyBudget} {_options.BudgetCurrency} is exhausted " +
                $"({spend} spent). Records are routed to human review.");
        }

        return null;
    }
}
