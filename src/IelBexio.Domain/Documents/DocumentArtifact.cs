using IelBexio.Domain.Common;

namespace IelBexio.Domain.Documents;

public enum DocumentKind { Unknown = 0, InvoicePdf = 1, InvoiceImage = 2, Csv = 3, Json = 4, EmailAttachment = 5, Other = 6 }

public enum DocumentProcessingStatus { Received = 0, Validated = 1, Classified = 2, Extracted = 3, Failed = 4, Rejected = 5, DuplicateOfExisting = 6 }

/// <summary>
/// A stored document. The bytes live in blob storage; this row carries the integrity and provenance
/// metadata required by §11 and §25. The blob URI is never a public URL.
/// </summary>
public sealed class DocumentArtifact : TenantEntity
{
    public Guid? InvoiceId { get; set; }

    /// <summary>Storage key inside the private container. Generated — never derived from user input (§25).</summary>
    public string BlobUri { get; set; } = string.Empty;

    /// <summary>Original filename as supplied, retained for display only. Never used as a path.</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>MIME type determined from the file signature, not from the client-supplied header.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the bytes, hex lower-case. The duplicate-detection key.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public SourceSystem SourceSystem { get; set; } = SourceSystem.DocumentUpload;
    public string? SourceReference { get; set; }
    public DocumentKind Kind { get; set; }
    public DocumentProcessingStatus Status { get; set; }

    /// <summary>Version of the ingestion pipeline that processed this artifact.</summary>
    public string ProcessingVersion { get; set; } = "1.0.0";

    public DateTimeOffset IngestedAt { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? DuplicateOfDocumentId { get; set; }

    /// <summary>Result of the malware-scan hook. <see cref="MalwareScanState.NotScanned"/> in the POC.</summary>
    public MalwareScanState MalwareScanState { get; set; } = MalwareScanState.NotScanned;

    public string? MalwareScanDetail { get; set; }
    public string CorrelationId { get; set; } = Guid.CreateVersion7().ToString("n");
}

public enum MalwareScanState { NotScanned = 0, Pending = 1, Clean = 2, Infected = 3, ScanFailed = 4 }

/// <summary>
/// The structured result of extracting an invoice from a document, whether by a deterministic parser
/// or by AI. Stored as a proposal-shaped record — it is not the canonical invoice.
/// </summary>
public sealed class ExtractionResult : TenantEntity
{
    public Guid DocumentArtifactId { get; set; }
    public Guid? InvoiceId { get; set; }

    /// <summary>Version of the extractor that produced this result — part of the cache key (§13).</summary>
    public string ExtractorVersion { get; set; } = "1.0.0";

    /// <summary>Model name when AI was used; null for deterministic parsers.</summary>
    public string? Model { get; set; }

    /// <summary>Prompt version when AI was used; null for deterministic parsers.</summary>
    public string? PromptVersion { get; set; }

    public ExtractionMethod Method { get; set; }

    /// <summary>The extracted structure as JSON. Schema-validated before it is stored.</summary>
    public string ExtractedJson { get; set; } = "{}";

    /// <summary>Per-field workflow signals, as a JSON object of field path → decimal in [0,1].</summary>
    public string? FieldConfidenceJson { get; set; }

    /// <summary>Deterministic validation errors found in the extracted structure.</summary>
    public string? ValidationErrorsJson { get; set; }

    public bool Succeeded { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset ProducedAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>Cache key: document hash + extractor version + prompt version + model (§13).</summary>
    public string CacheKey { get; set; } = string.Empty;
}

/// <remarks>Values are persisted. Append only — renumbering would silently relabel history.</remarks>
public enum ExtractionMethod
{
    Unknown = 0,
    StructuredApi = 1,
    CsvParser = 2,
    JsonParser = 3,
    AiExtraction = 4,
    Manual = 5,

    /// <summary>Read from structured XML the document carried itself, such as Factur-X or ZUGFeRD.</summary>
    EmbeddedXmlParser = 6,
}
