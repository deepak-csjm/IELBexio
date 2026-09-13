using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IelBexio.Domain.Common;

namespace IelBexio.Application.Sync;

/// <summary>
/// Builds the deterministic keys that make duplicate posting impossible (§19).
/// <para>
/// The key is derived from <c>tenant + source system + source document id + source document version +
/// operation</c>, exactly as the specification requires. Two properties matter:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>It is deterministic.</b> A retry, a page refresh, a double-click, a worker restart, a duplicate
/// webhook and a repeated import all regenerate the identical key, and the unique index on
/// <c>synchronization_attempts.idempotency_key</c> means only the first one can exist.
/// </description></item>
/// <item><description>
/// <b>It is independent of our own row ids.</b> Keying on a generated invoice id would not help: a
/// re-import that created a second row would produce a second key. Keying on the source document's own
/// identity is what makes the guarantee hold across re-imports.
/// </description></item>
/// </list>
/// </summary>
public static class IdempotencyKey
{
    public const string CreateInvoiceOperation = "CreateInvoice";

    public static string ForInvoiceSync(Guid tenantId, SourceSystem sourceSystem, string sourceDocumentId, string sourceDocumentVersion) =>
        Build(tenantId, sourceSystem, sourceDocumentId, sourceDocumentVersion, CreateInvoiceOperation);

    public static string Build(Guid tenantId, SourceSystem sourceSystem, string sourceDocumentId, string sourceDocumentVersion, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDocumentId);

        var raw = string.Join('|',
            tenantId.ToString("n", CultureInfo.InvariantCulture),
            sourceSystem.ToString(),
            sourceDocumentId,
            sourceDocumentVersion,
            operation);

        // Hashed so the key is a bounded, index-friendly length regardless of how long a source id is,
        // and so a source identifier never leaks into logs through the key itself.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

        // The readable prefix keeps an operator able to see what a key refers to without reversing it.
        return $"{sourceSystem}:{operation}:{hash[..40]}";
    }

    /// <summary>
    /// Hashes the canonical request body so "same key, different payload" is detectable. A repeat with
    /// a changed payload is a genuine anomaly — it means the record was edited after being queued.
    /// </summary>
    /// <summary>Deterministic ordering: a hash that depends on property order is not a hash of the content.</summary>
    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string HashRequest<T>(T request)
    {
        var json = JsonSerializer.Serialize(request, HashOptions);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
