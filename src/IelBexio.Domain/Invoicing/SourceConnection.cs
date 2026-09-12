using IelBexio.Domain.Common;

namespace IelBexio.Domain.Invoicing;

/// <summary>
/// A configured link to a source system. Credentials are NOT stored here in plain text — this row
/// holds only the non-secret reference plus a pointer to the secret store (§24).
/// </summary>
public sealed class SourceConnection : TenantEntity
{
    public SourceSystem SourceSystem { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Shop domain, marketplace id, or equivalent non-secret identifier.</summary>
    public string? ExternalAccountId { get; set; }

    /// <summary>Logical name of the secret in the configured secret store. Never the secret itself.</summary>
    public string? SecretReference { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Watermark for incremental synchronisation (§9): only records updated after this are fetched.</summary>
    public DateTimeOffset? LastSyncedUpdatedAt { get; set; }

    public string? LastSyncCursor { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>One execution of an import from a source connection.</summary>
public sealed class ImportRun : TenantEntity
{
    public Guid? SourceConnectionId { get; set; }
    public SourceSystem SourceSystem { get; set; }
    public string CorrelationId { get; set; } = Guid.CreateVersion7().ToString("n");
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ImportRunStatus Status { get; set; } = ImportRunStatus.Running;
    public int DocumentsSeen { get; set; }
    public int InvoicesCreated { get; set; }
    public int InvoicesUpdated { get; set; }
    public int DuplicatesSkipped { get; set; }
    public int Failures { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Describes what was requested — incremental window, fixture set, etc.</summary>
    public string? RequestDescription { get; set; }
}

public enum ImportRunStatus { Running = 0, Succeeded = 1, PartiallySucceeded = 2, Failed = 3 }

/// <summary>
/// The untouched source payload, retained for provenance and replay (§8). Stored separately from the
/// canonical record so normalisation can be re-run and diffed without re-contacting the source.
/// </summary>
public sealed class RawSourcePayload : TenantEntity
{
    public SourceSystem SourceSystem { get; set; }
    public Guid? ImportRunId { get; set; }
    public string SourceDocumentId { get; set; } = string.Empty;
    public string SourceDocumentVersion { get; set; } = "1";

    /// <summary>Verbatim payload as received (JSON). Never edited.</summary>
    public string Payload { get; set; } = "{}";

    /// <summary>SHA-256 of <see cref="Payload"/>, hex lower-case. Detects "same document, changed content".</summary>
    public string PayloadSha256 { get; set; } = string.Empty;

    public string? ApiVersion { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
}
