using System.Globalization;
using System.Text;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace IelBexio.UnitTests.Support;

/// <summary>
/// Builds the PDFs the extraction tests need.
/// <para>
/// Real PDFs rather than stubbed byte arrays, because the thing under test is whether a PDF library
/// can find an attachment and a text layer in a genuine file. A fake would only prove that the test
/// author and the code agree.
/// </para>
/// </summary>
public static class PdfFixtures
{
    /// <summary>An ordinary printed invoice: a text layer and nothing structured.</summary>
    public static byte[] WithText(params string[] lines)
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        var y = 780;
        foreach (var line in lines)
        {
            page.AddText(line, 11, new UglyToad.PdfPig.Core.PdfPoint(50, y), font);
            y -= 18;
        }

        return builder.Build();
    }

    /// <summary>A page with no text at all — what a scan looks like before character recognition.</summary>
    public static byte[] WithNoTextLayer()
    {
        using var builder = new PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);

        return builder.Build();
    }

    /// <summary>
    /// A PDF carrying an embedded file, as Factur-X and ZUGFeRD do.
    /// </summary>
    /// <remarks>
    /// Written by hand because PdfPig's builder cannot produce attachments. That is an advantage here:
    /// the structure below — a name tree under the catalogue pointing at a file specification, which
    /// points at an embedded-file stream — is the structure the specification actually mandates, so a
    /// reader that finds the attachment in this file is reading the real arrangement rather than one
    /// the same library happened to write.
    /// </remarks>
    public static byte[] WithAttachment(string attachmentName, string content)
    {
        var payload = Encoding.UTF8.GetBytes(content);

        using var stream = new MemoryStream();
        var offsets = new List<long>();

        void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));

        void BeginObject(int number)
        {
            offsets.Add(stream.Position);
            Write($"{number} 0 obj\n");
        }

        Write("%PDF-1.7\n");

        // A binary comment line marks the file as binary for tools that transfer it as text.
        stream.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);

        BeginObject(1);
        Write("<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [");
        Write($"({Escape(attachmentName)}) 5 0 R] >> >> >>\nendobj\n");

        BeginObject(2);
        Write("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        BeginObject(3);
        Write("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>\nendobj\n");

        BeginObject(4);
        Write($"<< /Type /EmbeddedFile /Subtype /text#2Fxml /Length {payload.Length.ToString(CultureInfo.InvariantCulture)} >>\nstream\n");
        stream.Write(payload);
        Write("\nendstream\nendobj\n");

        BeginObject(5);
        Write($"<< /Type /Filespec /F ({Escape(attachmentName)}) /UF ({Escape(attachmentName)}) ");
        Write("/AFRelationship /Alternative /EF << /F 4 0 R >> >>\nendobj\n");

        var startXref = stream.Position;

        Write($"xref\n0 {offsets.Count + 1}\n");
        Write("0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            // Exactly twenty bytes per entry, which the format requires and readers rely on.
            Write($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        }

        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n");
        Write($"{startXref.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");

        return stream.ToArray();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("(", "\\(", StringComparison.Ordinal)
             .Replace(")", "\\)", StringComparison.Ordinal);
}
