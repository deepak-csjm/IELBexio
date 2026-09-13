using System.Text;
using IelBexio.Application.Documents;
using IelBexio.Domain.Documents;

namespace IelBexio.UnitTests.Documents;

/// <summary>
/// File inspection is a security control, so these tests are written as attacks rather than as happy
/// paths (§25, §32).
/// </summary>
public sealed class FileInspectorTests
{
    private static FileInspector Create(Action<FileSecurityOptions>? configure = null)
    {
        var options = new FileSecurityOptions();
        configure?.Invoke(options);
        return new FileInspector(options);
    }

    private static byte[] MinimalPdf() =>
        Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n%%EOF");

    private static byte[] MinimalPng() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    [Fact]
    public void A_genuine_pdf_is_accepted_and_classified()
    {
        var result = Create().Inspect(MinimalPdf(), "application/pdf", "invoice.pdf");

        result.IsAccepted.Should().BeTrue();
        result.DetectedContentType.Should().Be("application/pdf");
        result.Kind.Should().Be(DocumentKind.InvoicePdf);
    }

    [Fact]
    public void An_executable_renamed_to_pdf_is_rejected_because_the_bytes_are_checked_not_the_name()
    {
        // MZ header — a Windows executable claiming to be an invoice.
        byte[] executable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];

        var result = Create().Inspect(executable, "application/pdf", "invoice.pdf");

        result.IsAccepted.Should().BeFalse();
        result.RejectionReason.Should().Contain("executable");
    }

    [Fact]
    public void An_elf_binary_is_rejected()
    {
        byte[] elf = [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00];

        Create().Inspect(elf, "application/pdf", "x.pdf").IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void A_zip_based_archive_is_rejected_because_this_system_does_not_process_archives()
    {
        byte[] zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00];

        var result = Create().Inspect(zip, "application/pdf", "invoice.pdf");

        result.IsAccepted.Should().BeFalse();
        result.RejectionReason.Should().Contain("ZIP");
    }

    [Fact]
    public void A_shell_script_is_rejected()
    {
        var script = Encoding.ASCII.GetBytes("#!/bin/sh\nrm -rf /\n");

        Create().Inspect(script, "text/csv", "data.csv").IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void A_declared_type_that_contradicts_the_bytes_is_rejected()
    {
        var result = Create().Inspect(MinimalPng(), "application/pdf", "invoice.pdf");

        result.IsAccepted.Should().BeFalse();
        result.RejectionReason.Should().Contain("declared as 'application/pdf'");
    }

    [Fact]
    public void Benign_browser_type_disagreements_are_tolerated()
    {
        // Browsers commonly report a CSV as text/plain or as an Excel type. Refusing those would make
        // the system unusable without improving safety, since the bytes are still verified.
        var csv = Encoding.UTF8.GetBytes("description,quantity,unitPrice\nWidget,1,10\n");

        Create().Inspect(csv, "text/plain", "invoice.csv").IsAccepted.Should().BeTrue();
        Create().Inspect(csv, "application/vnd.ms-excel", "invoice.csv").IsAccepted.Should().BeTrue();
        Create().Inspect(csv, "application/octet-stream", "invoice.csv").IsAccepted.Should().BeTrue();
    }

    [Fact]
    public void An_oversized_file_is_rejected_before_it_is_stored()
    {
        var big = new byte[2048];
        MinimalPdf().CopyTo(big, 0);

        var result = Create(o => o.MaxFileBytes = 1024).Inspect(big, "application/pdf", "big.pdf");

        result.IsAccepted.Should().BeFalse();
        result.RejectionReason.Should().Contain("above the");
    }

    [Fact]
    public void An_empty_file_is_rejected()
    {
        Create().Inspect([], "application/pdf", "empty.pdf").IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void A_corrupted_file_with_no_recognisable_format_is_rejected()
    {
        byte[] garbage = [0x13, 0x37, 0xBE, 0xEF, 0x00, 0x42, 0x99];

        var result = Create().Inspect(garbage, null, "mystery.bin");

        result.IsAccepted.Should().BeFalse();
        result.RejectionReason.Should().Contain("do not match any accepted document format");
    }

    [Fact]
    public void Binary_content_cannot_masquerade_as_a_text_format()
    {
        // Starts like JSON but contains a NUL byte, which no legitimate text document does.
        byte[] sneaky = [.. Encoding.UTF8.GetBytes("{\"a\":1}"), 0x00, 0xFF, 0xFE];

        Create().Inspect(sneaky, "application/json", "data.json").IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void The_sha256_is_computed_over_the_exact_bytes_and_is_stable()
    {
        var content = MinimalPdf();

        var first = Create().Inspect(content, "application/pdf", "a.pdf");
        var second = Create().Inspect(content, "application/pdf", "b.pdf");

        first.Sha256.Should().Be(second.Sha256, "the hash identifies content, not filename");
        first.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Different_content_produces_a_different_hash_so_duplicates_are_detectable()
    {
        var a = Create().Inspect(Encoding.UTF8.GetBytes("{\"invoiceNumber\":\"A\"}"), "application/json", "a.json");
        var b = Create().Inspect(Encoding.UTF8.GetBytes("{\"invoiceNumber\":\"B\"}"), "application/json", "b.json");

        a.Sha256.Should().NotBe(b.Sha256);
    }

    [Fact]
    public void A_hash_is_still_returned_for_a_rejected_file_so_repeat_attempts_are_traceable()
    {
        byte[] executable = [0x4D, 0x5A, 0x90, 0x00];

        var result = Create().Inspect(executable, "application/pdf", "bad.pdf");

        result.IsAccepted.Should().BeFalse();
        result.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void A_content_type_removed_from_the_allow_list_is_refused()
    {
        var result = Create(o => o.AllowedContentTypes.Remove("image/png"))
            .Inspect(MinimalPng(), "image/png", "scan.png");

        result.IsAccepted.Should().BeFalse();
        result.RejectionReason.Should().Contain("not in the accepted list");
    }
}
