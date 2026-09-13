using IelBexio.Application.Documents;
using IelBexio.Domain.Documents;
using IelBexio.Infrastructure.Documents;
using IelBexio.UnitTests.Support;

namespace IelBexio.UnitTests.Documents;

/// <summary>
/// PDF extraction, which is the one document format where the honest answer is usually "no".
/// <para>
/// A Factur-X or ZUGFeRD PDF carries the invoice as embedded XML and can be read exactly. Every other
/// PDF carries a printed page, and these tests hold the line that the extractor refuses those rather
/// than producing a plausible-looking invoice nobody can check.
/// </para>
/// </summary>
public sealed class PdfExtractionTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static PdfInvoiceExtractor Extractor() => new(new PdfPigReader());

    /// <summary>A Factur-X invoice: two lines at 8.1% Swiss VAT, with the issuer's own header totals.</summary>
    private const string FacturXInvoice = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rsm:CrossIndustryInvoice
            xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100"
            xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100"
            xmlns:udt="urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100">
          <rsm:ExchangedDocument>
            <ram:ID>FX-2026-0042</ram:ID>
            <ram:IssueDateTime>
              <udt:DateTimeString format="102">20260317</udt:DateTimeString>
            </ram:IssueDateTime>
          </rsm:ExchangedDocument>
          <rsm:SupplyChainTradeTransaction>
            <ram:IncludedSupplyChainTradeLineItem>
              <ram:AssociatedDocumentLineDocument><ram:LineID>1</ram:LineID></ram:AssociatedDocumentLineDocument>
              <ram:SpecifiedTradeProduct>
                <ram:SellerAssignedID>SKU-ALPHA</ram:SellerAssignedID>
                <ram:Name>Alpha Widget</ram:Name>
              </ram:SpecifiedTradeProduct>
              <ram:SpecifiedLineTradeAgreement>
                <ram:GrossPriceProductTradePrice><ram:ChargeAmount>120.00</ram:ChargeAmount></ram:GrossPriceProductTradePrice>
                <ram:NetPriceProductTradePrice><ram:ChargeAmount>100.00</ram:ChargeAmount></ram:NetPriceProductTradePrice>
              </ram:SpecifiedLineTradeAgreement>
              <ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode="C62">8</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery>
              <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                  <ram:TypeCode>VAT</ram:TypeCode>
                  <ram:CategoryCode>S</ram:CategoryCode>
                  <ram:RateApplicablePercent>8.1</ram:RateApplicablePercent>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                  <ram:LineTotalAmount>800.00</ram:LineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
              </ram:SpecifiedLineTradeSettlement>
            </ram:IncludedSupplyChainTradeLineItem>
            <ram:IncludedSupplyChainTradeLineItem>
              <ram:AssociatedDocumentLineDocument><ram:LineID>2</ram:LineID></ram:AssociatedDocumentLineDocument>
              <ram:SpecifiedTradeProduct><ram:Name>Delivery</ram:Name></ram:SpecifiedTradeProduct>
              <ram:SpecifiedLineTradeAgreement>
                <ram:NetPriceProductTradePrice><ram:ChargeAmount>200.00</ram:ChargeAmount></ram:NetPriceProductTradePrice>
              </ram:SpecifiedLineTradeAgreement>
              <ram:SpecifiedLineTradeDelivery><ram:BilledQuantity unitCode="C62">1</ram:BilledQuantity></ram:SpecifiedLineTradeDelivery>
              <ram:SpecifiedLineTradeSettlement>
                <ram:ApplicableTradeTax>
                  <ram:TypeCode>VAT</ram:TypeCode>
                  <ram:RateApplicablePercent>8.1</ram:RateApplicablePercent>
                </ram:ApplicableTradeTax>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                  <ram:LineTotalAmount>200.00</ram:LineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
              </ram:SpecifiedLineTradeSettlement>
            </ram:IncludedSupplyChainTradeLineItem>
            <ram:ApplicableHeaderTradeAgreement>
              <ram:SellerTradeParty><ram:Name>Muster Handels AG</ram:Name></ram:SellerTradeParty>
              <ram:BuyerTradeParty>
                <ram:Name>ABC Swiss GmbH</ram:Name>
                <ram:PostalTradeAddress><ram:CountryID>CH</ram:CountryID></ram:PostalTradeAddress>
                <ram:SpecifiedTaxRegistration>
                  <ram:ID schemeID="FC">123.456.789</ram:ID>
                </ram:SpecifiedTaxRegistration>
                <ram:SpecifiedTaxRegistration>
                  <ram:ID schemeID="VA">CHE-116.281.710</ram:ID>
                </ram:SpecifiedTaxRegistration>
              </ram:BuyerTradeParty>
            </ram:ApplicableHeaderTradeAgreement>
            <ram:ApplicableHeaderTradeSettlement>
              <ram:InvoiceCurrencyCode>CHF</ram:InvoiceCurrencyCode>
              <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
                <ram:LineTotalAmount>1000.00</ram:LineTotalAmount>
                <ram:TaxBasisTotalAmount>1000.00</ram:TaxBasisTotalAmount>
                <ram:TaxTotalAmount currencyID="CHF">81.00</ram:TaxTotalAmount>
                <ram:GrandTotalAmount>1081.00</ram:GrandTotalAmount>
              </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
            </ram:ApplicableHeaderTradeSettlement>
          </rsm:SupplyChainTradeTransaction>
        </rsm:CrossIndustryInvoice>
        """;

    [Fact]
    public void A_factur_x_pdf_is_extracted_exactly_and_without_any_ai()
    {
        var pdf = PdfFixtures.WithAttachment("factur-x.xml", FacturXInvoice);

        var extraction = Extractor().Extract(pdf, TenantId, "document:test");

        extraction.Succeeded.Should().BeTrue(extraction.FailureReason);
        extraction.Method.Should().Be(ExtractionMethod.EmbeddedXmlParser);

        var invoice = extraction.Invoice!;
        invoice.InvoiceNumber.Should().Be("FX-2026-0042");
        invoice.InvoiceDate.Should().Be(new DateOnly(2026, 3, 17));
        invoice.Currency.Should().Be("CHF");

        // The issuer's own header totals, not a recomputation of them.
        invoice.SubtotalAmount.Should().Be(1000.00m);
        invoice.TaxAmount.Should().Be(81.00m);
        invoice.TotalAmount.Should().Be(1081.00m);

        extraction.Customer!.CompanyName.Should().Be("ABC Swiss GmbH");
        extraction.Customer.CountryCode.Should().Be("CH");

        extraction.Lines.Should().HaveCount(2);

        var first = extraction.Lines[0];
        first.Description.Should().Be("Alpha Widget");
        first.Sku.Should().Be("SKU-ALPHA");
        first.Quantity.Should().Be(8m);

        // The net price, not the gross one: using the gross price would overstate a discounted line.
        first.UnitPrice.Should().Be(100.00m);
        first.NetAmount.Should().Be(800.00m);
        first.TaxRatePercent.Should().Be(8.1m);
        first.TaxAmount.Should().Be(64.80m);
        first.GrossAmount.Should().Be(864.80m);
    }

    [Fact]
    public void The_vat_number_is_taken_from_the_vat_registration_and_not_from_a_local_tax_number()
    {
        // The buyer above carries both: schemeID "FC" is a local tax number, "VA" is the VAT identifier.
        // Picking the wrong one produces a plausible number that fails at the accounting system.
        var extraction = Extractor().Extract(
            PdfFixtures.WithAttachment("factur-x.xml", FacturXInvoice), TenantId, "document:test");

        extraction.Customer!.VatNumber.Should().Be("CHE-116.281.710");
    }

    [Fact]
    public void An_invoice_xml_without_a_currency_is_refused_rather_than_defaulted()
    {
        var withoutCurrency = FacturXInvoice.Replace(
            "<ram:InvoiceCurrencyCode>CHF</ram:InvoiceCurrencyCode>", string.Empty, StringComparison.Ordinal);

        var extraction = Extractor().Extract(
            PdfFixtures.WithAttachment("factur-x.xml", withoutCurrency), TenantId, "document:test");

        extraction.Succeeded.Should().BeFalse();
        extraction.FailureReason.Should().Contain("currency");
    }

    [Fact]
    public void An_issue_date_in_an_unrecognised_format_yields_no_date_rather_than_a_guess()
    {
        // "01022026" is 1 February or 2 January depending on the format code. Guessing produces a date
        // that looks right and lands in the wrong VAT period.
        var ambiguous = FacturXInvoice.Replace("format=\"102\">20260317", "format=\"203\">01022026", StringComparison.Ordinal);

        var extraction = Extractor().Extract(
            PdfFixtures.WithAttachment("factur-x.xml", ambiguous), TenantId, "document:test");

        extraction.Succeeded.Should().BeTrue(extraction.FailureReason);
        extraction.Invoice!.InvoiceDate.Should().BeNull();
    }

    [Fact]
    public void A_line_whose_stated_total_disagrees_with_quantity_times_price_keeps_the_stated_total_and_says_so()
    {
        // 8 × 100 is 800; the document claims 750. The issuer's figure is what is owed, and the
        // disagreement is a reviewer's problem rather than something to quietly recompute away.
        var discounted = FacturXInvoice.Replace(
            "<ram:LineTotalAmount>800.00</ram:LineTotalAmount>",
            "<ram:LineTotalAmount>750.00</ram:LineTotalAmount>",
            StringComparison.Ordinal);

        var extraction = Extractor().Extract(
            PdfFixtures.WithAttachment("factur-x.xml", discounted), TenantId, "document:test");

        extraction.Succeeded.Should().BeTrue(extraction.FailureReason);
        extraction.Lines[0].NetAmount.Should().Be(750.00m);
        extraction.Notes.Should().Contain(n => n.Contains("750", StringComparison.Ordinal) && n.Contains("800", StringComparison.Ordinal));
    }

    [Fact]
    public void An_embedded_file_that_is_not_an_invoice_attachment_is_left_alone()
    {
        // A PDF may legitimately carry attachments. Treating any XML in one as financial data would be
        // the silent corruption this pipeline exists to prevent.
        var extraction = Extractor().Extract(
            PdfFixtures.WithAttachment("meeting-notes.xml", FacturXInvoice), TenantId, "document:test");

        // The very same XML that extracts perfectly under the name factur-x.xml produces nothing at all
        // under another name, which is the whole point: recognition is by the name the specifications
        // mandate, never by sniffing at whatever a document happens to carry.
        extraction.Succeeded.Should().BeFalse();
        extraction.Invoice.Should().BeNull();
        extraction.FailureReason.Should().NotContain("FX-2026-0042");
    }

    [Fact]
    public void An_ordinary_printed_pdf_is_refused_and_the_refusal_says_what_the_file_actually_is()
    {
        var pdf = PdfFixtures.WithText(
            "Muster Handels AG",
            "Invoice INV-9001",
            "Alpha Widget   8 x 100.00   800.00",
            "Total including VAT   1081.00 CHF");

        var extraction = Extractor().Extract(pdf, TenantId, "document:test");

        extraction.Succeeded.Should().BeFalse();
        extraction.Invoice.Should().BeNull();

        // No partial invoice: the figures are visible in the text and are still not extracted, because
        // reading them off a layout is guessing however plausible the guess looks.
        extraction.FailureReason.Should().Contain("no embedded invoice XML");
        extraction.FailureReason.Should().Contain("1 page");
        extraction.FailureReason.Should().Contain("characters of text");
    }

    [Fact]
    public void A_pdf_with_no_text_layer_is_reported_as_needing_character_recognition()
    {
        var extraction = Extractor().Extract(PdfFixtures.WithNoTextLayer(), TenantId, "document:test");

        extraction.Succeeded.Should().BeFalse();
        extraction.FailureReason.Should().Contain("no text layer");
        extraction.FailureReason.Should().Contain("Character recognition is not configured");
    }

    [Fact]
    public void Bytes_that_are_not_a_pdf_fail_cleanly_instead_of_throwing()
    {
        // Uploaded files are hostile input. A parser that throws here would turn one bad document into
        // a failed request rather than a rejected document.
        var extraction = Extractor().Extract("%PDF-1.7\nthis is not a pdf"u8, TenantId, "document:test");

        extraction.Succeeded.Should().BeFalse();
        extraction.FailureReason.Should().Contain("could not be read");
    }

    [Fact]
    public void The_extractor_claims_pdfs_and_nothing_else()
    {
        var extractor = Extractor();

        extractor.CanExtract(DocumentKind.InvoicePdf, "application/pdf").Should().BeTrue();
        extractor.CanExtract(DocumentKind.Csv, "text/csv").Should().BeFalse();
        extractor.CanExtract(DocumentKind.Json, "application/json").Should().BeFalse();
    }
}
