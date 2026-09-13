using IelBexio.Domain.Documents;

namespace IelBexio.Application.Documents;

/// <summary>A file carried inside a PDF as an attachment.</summary>
public sealed record PdfAttachment(string Name, byte[] Content);

/// <summary>What could be read out of a PDF without interpreting it.</summary>
/// <param name="PageTexts">The text layer, one entry per page. Empty entries for pages carrying none.</param>
/// <param name="Attachments">Embedded files, which is where a Factur-X invoice lives.</param>
/// <param name="ImageCount">Images across all pages, used to tell a scan from a text document.</param>
public sealed record PdfContent(
    IReadOnlyList<string> PageTexts,
    IReadOnlyList<PdfAttachment> Attachments,
    int ImageCount)
{
    public string FullText => string.Join("\n\n", PageTexts.Where(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>Whether the PDF carries machine-readable text at all, as opposed to being a picture of one.</summary>
    public bool HasTextLayer => PageTexts.Any(t => !string.IsNullOrWhiteSpace(t));

    public int PageCount => PageTexts.Count;
}

/// <summary>
/// Reads the contents of a PDF. A port rather than a direct dependency, so the PDF library stays in
/// the infrastructure layer and the extraction rules above it remain testable without one.
/// </summary>
public interface IPdfReader
{
    /// <summary>Reads a PDF, or throws <see cref="PdfReadException"/> if the bytes are not a readable one.</summary>
    PdfContent Read(ReadOnlySpan<byte> content);
}

/// <summary>A PDF that could not be read at all — malformed, encrypted, or truncated.</summary>
public sealed class PdfReadException : Exception
{
    public PdfReadException(string message) : base(message) { }
    public PdfReadException(string message, Exception innerException) : base(message, innerException) { }
    public PdfReadException() { }
}

/// <summary>
/// Extracts an invoice from a PDF, deterministically or not at all.
/// <para>
/// A PDF is two quite different things wearing one file extension. If it is a Factur-X or ZUGFeRD
/// invoice it carries the whole invoice as embedded XML, and reading that is exact — better than any
/// model could manage, because it is the issuer's own structured data rather than an interpretation of
/// how it was printed. If it is an ordinary PDF it carries only a printed page, whose layout is a
/// design choice rather than a format, and no honest parser can turn that into financial data.
/// </para>
/// <para>
/// So this extractor reads the first case and <b>refuses the second</b>, reporting exactly what it
/// found: how many characters of text, how many pages, whether the file is a scan. It never proposes a
/// partial invoice from a printed page. A half-extracted invoice is more dangerous than an unextracted
/// one, because it looks finished — and on a document that becomes a posted accounting entry, looking
/// finished is the failure mode that costs money.
/// </para>
/// </summary>
public sealed class PdfInvoiceExtractor : IDeterministicDocumentExtractor
{
    private readonly IPdfReader _reader;

    public PdfInvoiceExtractor(IPdfReader reader) => _reader = reader;

    public ExtractionMethod Method => ExtractionMethod.EmbeddedXmlParser;

    public string Version => "1.0.0";

    public bool CanExtract(DocumentKind kind, string contentType) =>
        kind == DocumentKind.InvoicePdf || string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    public DocumentExtraction Extract(ReadOnlySpan<byte> content, Guid tenantId, string sourceDocumentId)
    {
        PdfContent pdf;

        try
        {
            pdf = _reader.Read(content);
        }
        catch (PdfReadException ex)
        {
            return DocumentExtraction.Failed(Method, Version, $"The PDF could not be read: {ex.Message}");
        }

        var invoiceXml = pdf.Attachments.FirstOrDefault(a => CrossIndustryInvoiceParser.IsRecognisedAttachmentName(a.Name));

        if (invoiceXml is not null)
        {
            return CrossIndustryInvoiceParser.Parse(invoiceXml.Content, tenantId, sourceDocumentId, Method, Version);
        }

        // Nothing structured. Say precisely what the file is, because "extraction failed" tells a
        // reviewer nothing about whether to retype it, rescan it, or ask the supplier for a better one.
        var characters = pdf.FullText.Length;

        if (!pdf.HasTextLayer)
        {
            return DocumentExtraction.Refused(
                Method, Version,
                $"This PDF has no text layer across its {Pages(pdf.PageCount)}" +
                (pdf.ImageCount > 0 ? $" and contains {Images(pdf.ImageCount)}, so it is a scan" : string.Empty) +
                ". Character recognition is not configured, so nothing can be read from it without a human.");
        }

        return DocumentExtraction.Refused(
            Method, Version,
            $"This PDF carries no embedded invoice XML, so there is nothing to read deterministically. " +
            $"Its {Pages(pdf.PageCount)} yielded {characters:N0} characters of text, which describes how the " +
            "invoice was printed rather than what it says; extracting figures from that needs AI extraction " +
            "or manual entry, both of which go through review.");
    }

    private static string Pages(int count) => count == 1 ? "1 page" : $"{count} pages";

    private static string Images(int count) => count == 1 ? "1 image" : $"{count} images";
}
