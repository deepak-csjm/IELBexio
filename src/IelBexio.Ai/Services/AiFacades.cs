using System.Globalization;
using System.Text.Json;
using IelBexio.Ai.Gateway;
using IelBexio.Application.Ai;
using IelBexio.Domain.Ai;

namespace IelBexio.Ai.Services;

/// <summary>
/// The specialised AI services (§12). Each one builds a request and routes it through
/// <see cref="IAiService"/>; none of them holds a provider client, and none of them writes to a
/// canonical field. Their only output is a parsed proposal shape that the review layer turns into an
/// <c>AiProposal</c> row.
/// </summary>
public sealed class AiDocumentExtractor : IAiDocumentExtractor
{
    private readonly IAiService _ai;

    public AiDocumentExtractor(IAiService ai) => _ai = ai;

    public async Task<AiResult<ExtractedInvoiceCandidate>> ExtractAsync(
        string documentText, Guid? invoiceId, string? cacheKey, CancellationToken cancellationToken = default)
    {
        var result = await _ai.CompleteJsonAsync(new AiRequest
        {
            Operation = AiOperation.DocumentExtraction,
            TrustedContext =
                "Extract the invoice fields you can see in the document content that follows. " +
                "Return null for anything not clearly present. Do not calculate or reconcile totals; " +
                "report only what is written.",
            UntrustedContent = documentText,
            ResponseJsonSchema = AiPromptLibrary.InvoiceExtractionSchema,
            SchemaName = "invoice_extraction",
            InvoiceId = invoiceId,
            CacheKey = cacheKey,
        }, cancellationToken);

        if (!result.Succeeded)
        {
            return new AiResult<ExtractedInvoiceCandidate>(false, null, result.Refusal, result.Metadata);
        }

        try
        {
            using var document = JsonDocument.Parse(result.Value!);
            var root = document.RootElement;

            var lines = new List<ExtractedLineCandidate>();
            if (root.TryGetProperty("lines", out var lineArray) && lineArray.ValueKind == JsonValueKind.Array)
            {
                lines.AddRange(lineArray.EnumerateArray().Select(l => new ExtractedLineCandidate(
                    OptionalString(l, "description"),
                    OptionalDecimal(l, "quantity"),
                    OptionalDecimal(l, "unitPrice"),
                    OptionalDecimal(l, "netAmount"),
                    OptionalDecimal(l, "taxRatePercent"))));
            }

            var confidence = new Dictionary<string, decimal>(StringComparer.Ordinal);
            if (root.TryGetProperty("fieldConfidence", out var confidenceNode) && confidenceNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in confidenceNode.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        confidence[property.Name] = property.Value.GetDecimal();
                    }
                }
            }

            var candidate = new ExtractedInvoiceCandidate(
                OptionalString(root, "invoiceNumber"),
                OptionalString(root, "invoiceDate"),
                OptionalString(root, "currency"),
                OptionalString(root, "customerName"),
                OptionalString(root, "customerVatNumber"),
                OptionalDecimal(root, "subtotal"),
                OptionalDecimal(root, "taxAmount"),
                OptionalDecimal(root, "total"),
                lines,
                confidence);

            return AiResult<ExtractedInvoiceCandidate>.Ok(candidate, result.Metadata);
        }
        catch (JsonException)
        {
            // Schema validation already ran in the gateway, so reaching here means something genuinely
            // unexpected. It is still a refusal rather than an exception: ingestion must not fall over.
            return AiResult<ExtractedInvoiceCandidate>.Refused(
                AiFailureReason.MalformedOutput, "The extraction response could not be read.", result.Metadata);
        }
    }

    internal static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static decimal? OptionalDecimal(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}

public sealed class AiClassificationService : IAiClassificationService
{
    private readonly IAiService _ai;

    public AiClassificationService(IAiService ai) => _ai = ai;

    public async Task<AiResult<DocumentClassification>> ClassifyAsync(string documentText, string? cacheKey, CancellationToken cancellationToken = default)
    {
        var result = await _ai.CompleteJsonAsync(new AiRequest
        {
            Operation = AiOperation.DocumentClassification,
            TrustedContext = "Classify the kind of business document shown in the content that follows.",
            UntrustedContent = documentText,
            ResponseJsonSchema = AiPromptLibrary.ClassificationSchema,
            SchemaName = "document_classification",
            CacheKey = cacheKey,
        }, cancellationToken);

        if (!result.Succeeded)
        {
            return new AiResult<DocumentClassification>(false, null, result.Refusal, result.Metadata);
        }

        using var document = JsonDocument.Parse(result.Value!);
        var root = document.RootElement;

        return AiResult<DocumentClassification>.Ok(
            new DocumentClassification(
                AiDocumentExtractor.OptionalString(root, "kind") ?? "Other",
                AiDocumentExtractor.OptionalDecimal(root, "confidence") ?? 0m,
                AiDocumentExtractor.OptionalString(root, "rationale")),
            result.Metadata);
    }
}

/// <summary>
/// Mapping suggestions. Note the shape of the contract: the caller supplies the candidate list, and the
/// model may only choose from it. The model cannot introduce a Bexio id that does not exist, which
/// makes a hallucinated identifier structurally impossible rather than merely unlikely.
/// </summary>
public sealed class AiMappingSuggestionService : IAiMappingSuggestionService
{
    private readonly IAiService _ai;

    public AiMappingSuggestionService(IAiService ai) => _ai = ai;

    public Task<AiResult<IReadOnlyList<MappingSuggestion>>> SuggestCustomerMatchAsync(
        string customerDescription, IReadOnlyList<(string Id, string Label)> candidates, Guid? invoiceId, CancellationToken cancellationToken = default) =>
        SuggestAsync(
            AiOperation.CustomerMatchSuggestion,
            "Choose which of the listed accounting contacts, if any, refers to the same organisation as the " +
            "customer described below. Only ever return candidateId values from the supplied list. If none " +
            "matches, return an empty list.",
            customerDescription, candidates, invoiceId, cancellationToken);

    public Task<AiResult<IReadOnlyList<MappingSuggestion>>> SuggestTaxCodeAsync(
        string lineDescription, string countryCode, IReadOnlyList<(string Id, string Label)> candidates, Guid? invoiceId, CancellationToken cancellationToken = default) =>
        SuggestAsync(
            AiOperation.TaxCodeSuggestion,
            $"For a sale in country '{countryCode}', suggest which of the listed tax codes might apply to the " +
            "line described below. This is a suggestion for a human reviewer to check, not a tax determination. " +
            "Only return candidateId values from the supplied list.",
            lineDescription, candidates, invoiceId, cancellationToken);

    public Task<AiResult<IReadOnlyList<MappingSuggestion>>> SuggestAccountAsync(
        string lineDescription, IReadOnlyList<(string Id, string Label)> candidates, Guid? invoiceId, CancellationToken cancellationToken = default) =>
        SuggestAsync(
            AiOperation.AccountSuggestion,
            "Suggest which of the listed revenue accounts best fits the line described below. Only return " +
            "candidateId values from the supplied list.",
            lineDescription, candidates, invoiceId, cancellationToken);

    private async Task<AiResult<IReadOnlyList<MappingSuggestion>>> SuggestAsync(
        AiOperation operation, string instruction, string description,
        IReadOnlyList<(string Id, string Label)> candidates, Guid? invoiceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var candidateList = string.Join('\n', candidates.Select(c => $"- {c.Id}: {c.Label}"));

        var result = await _ai.CompleteJsonAsync(new AiRequest
        {
            Operation = operation,
            TrustedContext = $"{instruction}\n\nCandidates:\n{candidateList}",
            UntrustedContent = description,
            ResponseJsonSchema = AiPromptLibrary.SuggestionListSchema,
            SchemaName = "mapping_suggestions",
            InvoiceId = invoiceId,
        }, cancellationToken);

        if (!result.Succeeded)
        {
            return new AiResult<IReadOnlyList<MappingSuggestion>>(false, null, result.Refusal, result.Metadata);
        }

        using var document = JsonDocument.Parse(result.Value!);
        var suggestions = new List<MappingSuggestion>();
        var validIds = candidates.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        if (document.RootElement.TryGetProperty("suggestions", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var id = AiDocumentExtractor.OptionalString(item, "candidateId");

                // Enforced here as well as instructed in the prompt: an id outside the supplied set is
                // dropped, so a hallucinated identifier can never reach a mapping table.
                if (id is null || !validIds.Contains(id))
                {
                    continue;
                }

                suggestions.Add(new MappingSuggestion(
                    id,
                    AiDocumentExtractor.OptionalString(item, "candidateLabel") ?? id,
                    AiDocumentExtractor.OptionalDecimal(item, "confidence") ?? 0m,
                    AiDocumentExtractor.OptionalString(item, "evidence")));
            }
        }

        return AiResult<IReadOnlyList<MappingSuggestion>>.Ok(suggestions, result.Metadata);
    }
}

public sealed class AiAnomalyService : IAiAnomalyService
{
    private readonly IAiService _ai;

    public AiAnomalyService(IAiService ai) => _ai = ai;

    public async Task<AiResult<AnomalyExplanation>> ExplainAsync(string factsJson, Guid? invoiceId, CancellationToken cancellationToken = default)
    {
        var result = await _ai.CompleteJsonAsync(new AiRequest
        {
            Operation = AiOperation.AnomalyExplanation,
            TrustedContext =
                "The following facts were produced by deterministic validation. Summarise, in plain language, " +
                "what a reviewer should look at. Do not recalculate anything and do not contradict the facts.",
            UntrustedContent = factsJson,
            ResponseJsonSchema = AiPromptLibrary.AnomalySchema,
            SchemaName = "anomaly_explanation",
            InvoiceId = invoiceId,
        }, cancellationToken);

        if (!result.Succeeded)
        {
            return new AiResult<AnomalyExplanation>(false, null, result.Refusal, result.Metadata);
        }

        using var document = JsonDocument.Parse(result.Value!);
        var root = document.RootElement;

        var observations = new List<string>();
        if (root.TryGetProperty("observations", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            observations.AddRange(array.EnumerateArray()
                .Where(o => o.ValueKind == JsonValueKind.String)
                .Select(o => o.GetString()!));
        }

        return AiResult<AnomalyExplanation>.Ok(
            new AnomalyExplanation(
                AiDocumentExtractor.OptionalString(root, "summary") ?? string.Empty,
                observations,
                AiDocumentExtractor.OptionalDecimal(root, "confidence") ?? 0m),
            result.Metadata);
    }
}
