using IelBexio.Application.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace IelBexio.Infrastructure.Documents;

/// <summary>
/// Reads PDFs with PdfPig.
/// <para>
/// Chosen because it is managed code with no native dependency, so it runs unchanged in the same
/// container as the rest of the application and brings no separate patching surface. It reads; it
/// never renders, executes, or follows anything the document points at.
/// </para>
/// </summary>
public sealed class PdfPigReader : IPdfReader
{
    public PdfContent Read(ReadOnlySpan<byte> content)
    {
        try
        {
            using var document = PdfDocument.Open(content.ToArray());

            var pageTexts = new List<string>(document.NumberOfPages);
            var imageCount = 0;

            foreach (var page in document.GetPages())
            {
                pageTexts.Add(page.Text ?? string.Empty);
                imageCount += page.NumberOfImages;
            }

            var attachments = new List<PdfAttachment>();

            if (document.Advanced.TryGetEmbeddedFiles(out var embedded))
            {
                attachments.AddRange(embedded.Select(file => new PdfAttachment(file.Name, file.Bytes.ToArray())));
            }

            return new PdfContent(pageTexts, attachments, imageCount);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new PdfReadException("the document is encrypted and no password is held for it", ex);
        }
        catch (Exception ex) when (ex is PdfDocumentFormatException or PdfDocumentStackDepthException)
        {
            throw new PdfReadException($"the document is malformed ({ex.Message})", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or NotSupportedException)
        {
            // A PDF arriving here came from an upload, so a library that trips over a malformed one must
            // produce a handled failure for that document rather than an unhandled exception that takes
            // down the request. The reason is surfaced to the reviewer either way.
            throw new PdfReadException($"the document could not be parsed ({ex.GetType().Name}: {ex.Message})", ex);
        }
    }
}
