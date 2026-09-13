using IelBexio.Application.Common;
using IelBexio.Domain.Documents;

namespace IelBexio.Application.Documents;

/// <summary>Outcome of ingesting one uploaded document.</summary>
public sealed record DocumentIngestionResult(
    Guid DocumentId,
    DocumentProcessingStatus Status,
    string Sha256,
    string ContentType,
    DocumentKind Kind,
    Guid? InvoiceId,
    ExtractionMethod? ExtractionMethod,
    string? Message,
    Guid? DuplicateOfDocumentId);

/// <summary>
/// The document ingestion pipeline of §11: validate → hash → store → classify → extract → normalise →
/// validate deterministically → route to review.
/// <para>
/// This lives behind an interface rather than inside the UI for two reasons. First, business logic in a
/// Razor code-behind is untestable — and an ingestion path that decides whether a file is safe is not
/// something to leave untested. Second, the REST API and the UI must behave identically; sharing one
/// service is the only way to guarantee that rather than hope for it.
/// </para>
/// </summary>
public interface IDocumentIngestionService
{
    /// <summary>
    /// Ingests one file. <paramref name="declaredContentType"/> is what the client claimed, and is
    /// treated as a claim to be checked rather than as fact.
    /// </summary>
    Task<Result<DocumentIngestionResult>> IngestAsync(
        byte[] content,
        string fileName,
        string? declaredContentType,
        CancellationToken cancellationToken = default);
}
