using System.Diagnostics;
using System.Text.Json;
using IelBexio.Application.Abstractions;
using IelBexio.Application.Ai;
using IelBexio.Domain.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IelBexio.Ai.Gateway;

/// <summary>Performs the actual provider call. Separated so the gateway's policy is testable without a provider.</summary>
public interface IAiCompletionClient
{
    string Model { get; }

    Task<AiCompletionResponse> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string jsonSchema,
        string schemaName,
        float temperature,
        int maxOutputTokens,
        CancellationToken cancellationToken);
}

public sealed record AiCompletionResponse(string Content, int PromptTokens, int CompletionTokens);

/// <summary>
/// The single controlled gateway to Azure OpenAI (§12, §13).
/// <para>
/// Everything AI-related funnels through <see cref="CompleteJsonAsync"/>: the model allow-list,
/// operation allow-list, size caps, per-invoice call cap, monthly budget, temperature ceiling, timeout,
/// retry, JSON-schema validation, prompt versioning, caching, cost accounting and correlation. Business
/// code never constructs a provider client, so none of these can be bypassed by a new call site.
/// </para>
/// <para>
/// <b>Failure is always a normal outcome.</b> Every path returns an <see cref="AiResult{T}"/> rather
/// than throwing, because acceptance criterion 9 requires deterministic processing to continue when AI
/// is disabled, over budget, timed out, unavailable, or returning malformed output.
/// </para>
/// </summary>
public sealed class AiGateway : IAiService
{
    private readonly AiOptions _options;
    private readonly AiGuard _guard;
    private readonly IAiCompletionClient _client;
    private readonly IAiUsageLedger _ledger;
    private readonly IClock _clock;
    private readonly ICorrelationContext _correlation;
    private readonly ILogger<AiGateway> _logger;

    public AiGateway(
        IOptions<AiOptions> options,
        AiGuard guard,
        IAiCompletionClient client,
        IAiUsageLedger ledger,
        IClock clock,
        ICorrelationContext correlation,
        ILogger<AiGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _guard = guard;
        _client = client;
        _ledger = ledger;
        _clock = clock;
        _correlation = correlation;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled && _options.IsConfigured;

    public async Task<AiResult<string>> CompleteJsonAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = new AiCallMetadata
        {
            Operation = request.Operation,
            Model = _client.Model,
            PromptVersion = AiPromptLibrary.Version,
            SchemaVersion = AiPromptLibrary.SchemaVersion,
            CostCurrency = _options.BudgetCurrency,
            CorrelationId = _correlation.CorrelationId,
        };

        var refusal = await _guard.EvaluateAsync(request, _client.Model, cancellationToken);
        if (refusal is not null)
        {
            // A refusal is recorded too: "we chose not to call AI" is as much a part of the audit story
            // as a call that happened, and the AI Usage screen shows both.
            await RecordUsageAsync(request, metadata, succeeded: false, refusal.Reason, refusal.Message, 0, 0, 0m, false, cancellationToken);
            _logger.LogInformation("AI request refused: {Reason} — {Message}", refusal.Reason, refusal.Message);
            return AiResult<string>.Refused(refusal.Reason, refusal.Message, metadata);
        }

        // The cache key must include prompt version, schema version and model: reusing a result produced
        // by a different prompt or model would silently mix generations of behaviour (§13).
        var cacheKey = request.CacheKey is null
            ? null
            : $"{request.Operation}|{_client.Model}|{AiPromptLibrary.Version}|{AiPromptLibrary.SchemaVersion}|{request.CacheKey}";

        if (_options.EnableCaching && cacheKey is not null)
        {
            var cached = await _ledger.TryGetCachedAsync(cacheKey, cancellationToken);
            if (cached is not null)
            {
                var cachedMetadata = metadata with { ServedFromCache = true };
                await RecordUsageAsync(request, cachedMetadata, true, AiFailureReason.None, null, 0, 0, 0m, true, cancellationToken);
                return AiResult<string>.Ok(cached, cachedMetadata);
            }
        }

        var userPrompt = BuildUserPrompt(request);
        var schema = request.ResponseJsonSchema;
        var temperature = Math.Min(_options.Temperature, _options.MaxTemperature);

        var stopwatch = Stopwatch.StartNew();
        AiCompletionResponse? response = null;
        var attempt = 0;

        while (attempt < Math.Max(1, _options.MaxRetryAttempts))
        {
            attempt++;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);

            try
            {
                response = await _client.CompleteAsync(
                    AiPromptLibrary.SystemPrompt, userPrompt, schema, request.SchemaName,
                    temperature, _options.MaxOutputTokens, timeout.Token);
                break;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                _logger.LogWarning("AI call timed out after {Timeout}.", _options.Timeout);

                if (attempt >= _options.MaxRetryAttempts)
                {
                    var timedOut = metadata with { LatencyMs = (int)stopwatch.ElapsedMilliseconds };
                    await RecordUsageAsync(request, timedOut, false, AiFailureReason.Timeout, "The AI request timed out.", 0, 0, 0m, false, cancellationToken);
                    return AiResult<string>.Refused(AiFailureReason.Timeout, "The AI request timed out; the record is routed to human review.", timedOut);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "AI provider call failed on attempt {Attempt}.", attempt);

                if (attempt >= _options.MaxRetryAttempts)
                {
                    var failed = metadata with { LatencyMs = (int)stopwatch.ElapsedMilliseconds };
                    await RecordUsageAsync(request, failed, false, AiFailureReason.ProviderUnavailable, ex.GetType().Name, 0, 0, 0m, false, cancellationToken);
                    return AiResult<string>.Refused(
                        AiFailureReason.ProviderUnavailable,
                        "The AI provider is unavailable; deterministic processing continues and the record is routed to human review.",
                        failed);
                }
            }
        }

        stopwatch.Stop();

        if (response is null)
        {
            var noResponse = metadata with { LatencyMs = (int)stopwatch.ElapsedMilliseconds };
            await RecordUsageAsync(request, noResponse, false, AiFailureReason.ProviderUnavailable, "No response.", 0, 0, 0m, false, cancellationToken);
            return AiResult<string>.Refused(AiFailureReason.ProviderUnavailable, "The AI provider returned no response.", noResponse);
        }

        var cost = EstimateCost(response.PromptTokens, response.CompletionTokens);
        metadata = metadata with
        {
            LatencyMs = (int)stopwatch.ElapsedMilliseconds,
            PromptTokens = response.PromptTokens,
            CompletionTokens = response.CompletionTokens,
            EstimatedCost = cost,
        };

        // Malformed output is rejected and sent to review; it never becomes a proposal (§13).
        if (!IsWellFormedJsonObject(response.Content))
        {
            await RecordUsageAsync(request, metadata, false, AiFailureReason.MalformedOutput, "Response was not a JSON object.", response.PromptTokens, response.CompletionTokens, cost, false, cancellationToken);
            _logger.LogWarning("AI returned malformed output for {Operation}; rejecting it.", request.Operation);
            return AiResult<string>.Refused(
                AiFailureReason.MalformedOutput,
                "The AI response was not valid JSON and has been rejected. The record is routed to human review.",
                metadata);
        }

        if (!AiSchemaValidator.Validate(response.Content, schema, out var schemaError))
        {
            await RecordUsageAsync(request, metadata, false, AiFailureReason.SchemaValidationFailed, schemaError, response.PromptTokens, response.CompletionTokens, cost, false, cancellationToken);
            _logger.LogWarning("AI response failed schema validation for {Operation}: {Error}", request.Operation, schemaError);
            return AiResult<string>.Refused(
                AiFailureReason.SchemaValidationFailed,
                $"The AI response did not match the required schema ({schemaError}) and has been rejected.",
                metadata);
        }

        if (_options.EnableCaching && cacheKey is not null)
        {
            await _ledger.CacheAsync(cacheKey, response.Content, _options.CacheLifetime, cancellationToken);
        }

        await RecordUsageAsync(request, metadata, true, AiFailureReason.None, null, response.PromptTokens, response.CompletionTokens, cost, false, cancellationToken);
        return AiResult<string>.Ok(response.Content, metadata);
    }

    /// <summary>
    /// Builds the user message. Trusted context and untrusted content are concatenated in a fixed order
    /// with the untrusted part fenced — the caller never controls the structure.
    /// </summary>
    private static string BuildUserPrompt(AiRequest request)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.TrustedContext))
        {
            parts.Add(request.TrustedContext);
        }

        if (!string.IsNullOrWhiteSpace(request.UntrustedContent))
        {
            parts.Add(AiPromptLibrary.Fence(request.UntrustedContent));
        }

        return string.Join("\n\n", parts);
    }

    private decimal EstimateCost(int promptTokens, int completionTokens) =>
        decimal.Round(
            (promptTokens / 1000m * _options.CostPer1kPromptTokens) +
            (completionTokens / 1000m * _options.CostPer1kCompletionTokens),
            6,
            MidpointRounding.ToEven);

    private static bool IsWellFormedJsonObject(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records safe metadata only. Prompts and responses are deliberately not stored: they contain
    /// customer names, addresses and line descriptions, and keeping them would create a second copy of
    /// the PII with none of the access controls the canonical tables have (§13 PII minimisation).
    /// </summary>
    private Task RecordUsageAsync(
        AiRequest request, AiCallMetadata metadata, bool succeeded, AiFailureReason reason, string? detail,
        int promptTokens, int completionTokens, decimal cost, bool fromCache, CancellationToken cancellationToken) =>
        _ledger.RecordAsync(new AiUsageRecord
        {
            Operation = request.Operation,
            Model = metadata.Model,
            PromptVersion = metadata.PromptVersion,
            OccurredAt = _clock.UtcNow,
            LatencyMs = metadata.LatencyMs,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            EstimatedCost = cost,
            CostCurrency = _options.BudgetCurrency,
            Succeeded = succeeded,
            FailureReason = reason,
            FailureDetail = Truncate(detail, 2000),
            CorrelationId = metadata.CorrelationId,
            InvoiceId = request.InvoiceId,
            ServedFromCache = fromCache,
        }, cancellationToken);

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}

/// <summary>
/// Disabled AI. Registered whenever AI is switched off or unconfigured, so the rest of the application
/// depends on <see cref="IAiService"/> unconditionally and never branches on "is AI available".
/// </summary>
public sealed class DisabledAiService : IAiService
{
    public bool IsEnabled => false;

    public Task<AiResult<string>> CompleteJsonAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(AiResult<string>.Refused(
            AiFailureReason.Disabled,
            "AI assistance is disabled for this deployment.",
            new AiCallMetadata { Operation = request.Operation, Model = "(disabled)" }));
    }
}
