using System.Text;
using IelBexio.Application.Documents;
using IelBexio.Application.Invoices;
using IelBexio.Domain.Documents;

namespace IelBexio.UnitTests.Documents;

/// <summary>
/// Deterministic extraction exists so machine-readable documents never reach an LLM (principle 4).
/// These tests also cover the ways a naive parser corrupts financial data.
/// </summary>
public sealed class DeterministicExtractorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    // ---- JSON ----------------------------------------------------------------------------------

    [Fact]
    public void A_canonical_json_invoice_extracts_and_validates_without_any_ai()
    {
        var json = """
            {
              "invoiceNumber": "DOC-2001",
              "invoiceDate": "2026-03-10",
              "currency": "CHF",
              "customerName": "ABC Swiss GmbH",
              "customerVatNumber": "CHE-116.281.710",
              "customerCountry": "CH",
              "subtotal": 1000.00,
              "taxAmount": 81.00,
              "total": 1081.00,
              "lines": [
                { "sku": "SKU-ALPHA", "description": "Alpha Widget", "quantity": 4, "unitPrice": 250.00, "taxRatePercent": 8.1 }
              ]
            }
            """;

        var result = new JsonInvoiceExtractor().Extract(Encoding.UTF8.GetBytes(json), TenantId, "doc-1");

        result.Succeeded.Should().BeTrue();
        result.Method.Should().Be(ExtractionMethod.JsonParser);
        result.Invoice!.TotalAmount.Should().Be(1081.00m);
        result.Lines.Should().ContainSingle();
        result.Lines[0].NetAmount.Should().Be(1000.00m);
        result.Lines[0].TaxAmount.Should().Be(81.00m);

        new InvoiceValidator().Validate(result.Invoice, result.Lines).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void A_json_document_that_states_no_total_has_one_derived_and_says_so()
    {
        var json = """
            {
              "invoiceNumber": "DOC-2002",
              "invoiceDate": "2026-03-10",
              "currency": "CHF",
              "customerName": "ABC Swiss GmbH",
              "lines": [
                { "description": "Widget", "quantity": 2, "unitPrice": 100.00, "taxRatePercent": 8.1 }
              ]
            }
            """;

        var result = new JsonInvoiceExtractor().Extract(Encoding.UTF8.GetBytes(json), TenantId, "doc-2");

        result.Invoice!.TotalAmount.Should().Be(216.20m, "200 net + 16.20 tax");
        result.Notes.Should().Contain(n => n.Contains("derived", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_json_document_whose_stated_totals_contradict_its_lines_is_caught_by_validation()
    {
        var json = """
            {
              "invoiceNumber": "DOC-2003",
              "invoiceDate": "2026-03-10",
              "currency": "CHF",
              "customerName": "ABC Swiss GmbH",
              "subtotal": 1000.00,
              "taxAmount": 81.00,
              "total": 5000.00,
              "lines": [
                { "description": "Widget", "quantity": 4, "unitPrice": 250.00, "taxRatePercent": 8.1 }
              ]
            }
            """;

        var result = new JsonInvoiceExtractor().Extract(Encoding.UTF8.GetBytes(json), TenantId, "doc-3");

        result.Succeeded.Should().BeTrue("extraction succeeded; it is validation's job to object");
        new InvoiceValidator().Validate(result.Invoice!, result.Lines).HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Invalid_json_fails_cleanly_rather_than_producing_a_partial_invoice()
    {
        var result = new JsonInvoiceExtractor().Extract(Encoding.UTF8.GetBytes("{ not json"), TenantId, "doc-4");

        result.Succeeded.Should().BeFalse();
        result.Invoice.Should().BeNull("a half-extracted invoice is more dangerous than an unextracted one");
        result.FailureReason.Should().Contain("not valid JSON");
    }

    [Fact]
    public void A_json_document_with_no_lines_fails()
    {
        var result = new JsonInvoiceExtractor().Extract(
            Encoding.UTF8.GetBytes("""{"invoiceNumber":"X","lines":[]}"""), TenantId, "doc-5");

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("no invoice lines");
    }

    // ---- CSV -----------------------------------------------------------------------------------

    [Fact]
    public void A_csv_line_export_extracts_and_validates()
    {
        var csv = """
            invoiceNumber,invoiceDate,currency,customerName,customerVatNumber,customerCountry,sku,description,quantity,unitPrice,taxRatePercent
            DOC-3001,2026-03-11,CHF,ABC Swiss GmbH,CHE-116.281.710,CH,SKU-ALPHA,Alpha Widget,4,250.00,8.1
            DOC-3001,2026-03-11,CHF,ABC Swiss GmbH,CHE-116.281.710,CH,SKU-BOOK,Printed Manual,2,50.00,2.6
            """;

        var result = new CsvInvoiceExtractor().Extract(Encoding.UTF8.GetBytes(csv), TenantId, "doc-csv-1");

        result.Succeeded.Should().BeTrue();
        result.Lines.Should().HaveCount(2);
        result.Invoice!.SubtotalAmount.Should().Be(1100.00m);
        result.Invoice.TaxAmount.Should().Be(83.60m, "81.00 at 8.1% plus 2.60 at 2.6%");
        result.Invoice.TotalAmount.Should().Be(1183.60m);

        new InvoiceValidator().Validate(result.Invoice, result.Lines).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void A_quoted_description_containing_a_comma_does_not_shift_every_later_column()
    {
        // The classic naive-split bug: without RFC 4180 quoting, "Widget, large" becomes two fields and
        // the quantity column silently receives the price.
        var csv = """
            invoiceNumber,currency,description,quantity,unitPrice,taxRatePercent
            DOC-3002,CHF,"Widget, large, blue",2,125.00,8.1
            """;

        var result = new CsvInvoiceExtractor().Extract(Encoding.UTF8.GetBytes(csv), TenantId, "doc-csv-2");

        result.Succeeded.Should().BeTrue();
        result.Lines[0].Description.Should().Be("Widget, large, blue");
        result.Lines[0].Quantity.Should().Be(2m);
        result.Lines[0].UnitPrice.Should().Be(125.00m);
    }

    [Fact]
    public void Doubled_quotes_inside_a_quoted_field_are_read_as_a_literal_quote()
    {
        var csv = "description,quantity,unitPrice\n\"A \"\"special\"\" widget\",1,10.00\n";

        var result = new CsvInvoiceExtractor().Extract(Encoding.UTF8.GetBytes(csv), TenantId, "doc-csv-3");

        result.Lines[0].Description.Should().Be("A \"special\" widget");
    }

    [Fact]
    public void A_row_with_an_unparseable_price_fails_the_whole_extraction_rather_than_booking_zero()
    {
        var csv = """
            invoiceNumber,currency,description,quantity,unitPrice
            DOC-3003,CHF,Widget,2,not-a-number
            """;

        var result = new CsvInvoiceExtractor().Extract(Encoding.UTF8.GetBytes(csv), TenantId, "doc-csv-4");

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("not a valid number");
    }

    [Fact]
    public void A_csv_missing_a_required_column_fails_with_a_specific_reason()
    {
        var csv = "invoiceNumber,currency,description\nDOC-3004,CHF,Widget\n";

        var result = new CsvInvoiceExtractor().Extract(Encoding.UTF8.GetBytes(csv), TenantId, "doc-csv-5");

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("quantity");
    }

    [Fact]
    public void A_csv_with_only_a_header_fails()
    {
        var csv = "description,quantity,unitPrice\n";

        new CsvInvoiceExtractor().Extract(Encoding.UTF8.GetBytes(csv), TenantId, "doc-csv-6")
            .Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Non_utf8_content_fails_cleanly()
    {
        byte[] invalidUtf8 = [0xFF, 0xFE, 0x41, 0x00, 0x42, 0x00];

        var result = new CsvInvoiceExtractor().Extract(invalidUtf8, TenantId, "doc-csv-7");

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Contain("UTF-8");
    }

    [Fact]
    public void Semicolon_delimited_csv_is_handled_because_swiss_exports_commonly_use_it()
    {
        var csv = "invoiceNumber;currency;description;quantity;unitPrice;taxRatePercent\nDOC-3005;CHF;Widget;2;100.00;8.1\n";

        var result = new CsvInvoiceExtractor().Extract(Encoding.UTF8.GetBytes(csv), TenantId, "doc-csv-8");

        result.Succeeded.Should().BeTrue();
        result.Lines[0].UnitPrice.Should().Be(100.00m);
    }

    [Fact]
    public void Extractors_declare_which_formats_they_handle()
    {
        var json = new JsonInvoiceExtractor();
        var csv = new CsvInvoiceExtractor();

        json.CanExtract(DocumentKind.Json, "application/json").Should().BeTrue();
        json.CanExtract(DocumentKind.Csv, "text/csv").Should().BeFalse();
        csv.CanExtract(DocumentKind.Csv, "text/csv").Should().BeTrue();
        csv.CanExtract(DocumentKind.InvoicePdf, "application/pdf").Should().BeFalse();
    }
}
