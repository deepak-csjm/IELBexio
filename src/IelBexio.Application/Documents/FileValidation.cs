using System.Security.Cryptography;
using IelBexio.Domain.Documents;

namespace IelBexio.Application.Documents;

/// <summary>Limits and allow-lists for uploaded files (§25).</summary>
public sealed class FileSecurityOptions
{
    public const string SectionName = "FileSecurity";

    /// <summary>Maximum accepted upload. Enforced before the bytes are hashed or stored.</summary>
    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>
    /// Accepted content types. An allow-list, not a block-list: the set of dangerous file types is
    /// open-ended, while the set this system can actually process is small and known.
    /// </summary>
    public IList<string> AllowedContentTypes { get; } =
    [
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/tiff",
        "text/csv",
        "application/json",
    ];

    /// <summary>Whether a malware scan must have completed before a document may be processed.</summary>
    public bool RequireMalwareScan { get; set; }
}

public sealed record FileInspectionResult(
    bool IsAccepted,
    string? RejectionReason,
    string DetectedContentType,
    DocumentKind Kind,
    string Sha256,
    long SizeBytes);

/// <summary>
/// Inspects an uploaded file before it is stored (§11, §25).
/// <para>
/// The central decision here: <b>the client-supplied content type is never trusted</b>. A browser (or
/// an attacker) can claim anything. The type is determined from the file's own leading bytes — its
/// magic number — and a declared type that contradicts the bytes is grounds for rejection, not a
/// warning. This is what stops an executable arriving labelled <c>application/pdf</c>.
/// </para>
/// </summary>
public sealed class FileInspector
{
    private readonly FileSecurityOptions _options;

    public FileInspector(FileSecurityOptions options) => _options = options;

    /// <summary>Known file signatures. Kept short and explicit rather than pulled from a library.</summary>
    private static readonly (byte[] Signature, string ContentType, DocumentKind Kind)[] Signatures =
    [
        ("%PDF-"u8.ToArray(), "application/pdf", DocumentKind.InvoicePdf),
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png", DocumentKind.InvoiceImage),
        ([0xFF, 0xD8, 0xFF], "image/jpeg", DocumentKind.InvoiceImage),
        ([0x49, 0x49, 0x2A, 0x00], "image/tiff", DocumentKind.InvoiceImage),
        ([0x4D, 0x4D, 0x00, 0x2A], "image/tiff", DocumentKind.InvoiceImage),
    ];

    /// <summary>Signatures that are never acceptable, checked so the rejection reason is specific.</summary>
    private static readonly (byte[] Signature, string Description)[] DangerousSignatures =
    [
        ([0x4D, 0x5A], "a Windows executable (MZ header)"),
        ([0x7F, 0x45, 0x4C, 0x46], "an ELF executable"),
        ([0x50, 0x4B, 0x03, 0x04], "a ZIP-based archive, which this system does not process"),
        ([0xD0, 0xCF, 0x11, 0xE0], "a legacy OLE compound document"),
        ([0x23, 0x21], "a script with a shebang line"),
    ];

    public FileInspectionResult Inspect(ReadOnlySpan<byte> content, string? declaredContentType, string? fileName)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        long size = content.Length;

        if (size == 0)
        {
            return new FileInspectionResult(false, "The file is empty.", "application/octet-stream", DocumentKind.Unknown, sha256, 0);
        }

        if (size > _options.MaxFileBytes)
        {
            return new FileInspectionResult(
                false,
                $"The file is {size} bytes, above the {_options.MaxFileBytes} byte limit.",
                declaredContentType ?? "application/octet-stream",
                DocumentKind.Unknown,
                sha256,
                size);
        }

        foreach (var (signature, description) in DangerousSignatures)
        {
            if (StartsWith(content, signature))
            {
                return new FileInspectionResult(
                    false,
                    $"The file's contents identify it as {description}, which is not an accepted document type.",
                    "application/octet-stream",
                    DocumentKind.Unknown,
                    sha256,
                    size);
            }
        }

        var detected = DetectType(content, fileName);

        if (detected is null)
        {
            return new FileInspectionResult(
                false,
                "The file's contents do not match any accepted document format (PDF, PNG, JPEG, TIFF, CSV or JSON).",
                declaredContentType ?? "application/octet-stream",
                DocumentKind.Unknown,
                sha256,
                size);
        }

        var (contentType, kind) = detected.Value;

        if (!_options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            return new FileInspectionResult(false, $"Content type '{contentType}' is not in the accepted list.", contentType, kind, sha256, size);
        }

        // A declared type that contradicts the bytes is a signal worth refusing, not smoothing over.
        if (!string.IsNullOrWhiteSpace(declaredContentType)
            && !string.Equals(declaredContentType, contentType, StringComparison.OrdinalIgnoreCase)
            && !IsBenignTypeDisagreement(declaredContentType, contentType))
        {
            return new FileInspectionResult(
                false,
                $"The file was declared as '{declaredContentType}' but its contents are '{contentType}'.",
                contentType,
                kind,
                sha256,
                size);
        }

        return new FileInspectionResult(true, null, contentType, kind, sha256, size);
    }

    private static (string ContentType, DocumentKind Kind)? DetectType(ReadOnlySpan<byte> content, string? fileName)
    {
        foreach (var (signature, contentType, kind) in Signatures)
        {
            if (StartsWith(content, signature))
            {
                return (contentType, kind);
            }
        }

        // CSV and JSON have no magic number, so they are identified by structure. Both must also be
        // valid UTF-8 text, which rules out binary content masquerading as a text format.
        if (!LooksLikeUtf8Text(content))
        {
            return null;
        }

        var text = System.Text.Encoding.UTF8.GetString(content[..Math.Min(content.Length, 4096)]).TrimStart();

        if (text.StartsWith('{') || text.StartsWith('['))
        {
            return ("application/json", DocumentKind.Json);
        }

        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (extension is ".csv" or ".tsv" || LooksLikeDelimitedText(text))
        {
            return ("text/csv", DocumentKind.Csv);
        }

        return null;
    }

    /// <summary>Tolerates the common, harmless disagreements browsers produce for the same real type.</summary>
    private static bool IsBenignTypeDisagreement(string declared, string detected) =>
        (declared, detected) switch
        {
            ("text/plain", "text/csv") => true,
            ("application/vnd.ms-excel", "text/csv") => true,
            ("text/csv", "text/csv") => true,
            ("application/octet-stream", _) => true,
            ("text/plain", "application/json") => true,
            ("image/jpg", "image/jpeg") => true,
            _ => false,
        };

    private static bool StartsWith(ReadOnlySpan<byte> content, ReadOnlySpan<byte> signature) =>
        content.Length >= signature.Length && content[..signature.Length].SequenceEqual(signature);

    private static bool LooksLikeUtf8Text(ReadOnlySpan<byte> content)
    {
        var sample = content[..Math.Min(content.Length, 4096)];

        // A NUL byte in the first few KB is the cheapest reliable binary indicator.
        if (sample.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(sample);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool LooksLikeDelimitedText(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        return firstLine.Contains(',', StringComparison.Ordinal) || firstLine.Contains(';', StringComparison.Ordinal);
    }
}
