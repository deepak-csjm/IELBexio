using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using IelBexio.Domain.Common;
using IelBexio.Domain.Documents;
using IelBexio.Domain.Invoicing;

namespace IelBexio.Application.Documents;

/// <summary>
/// Parses a Factur-X / ZUGFeRD invoice — the UN/CEFACT Cross Industry Invoice XML that a compliant
/// PDF carries as an embedded attachment — into the canonical model.
/// <para>
/// This is the one way a PDF invoice can be read with no AI and no guessing at all: the XML is the
/// authoritative structured invoice, and the printed page is a rendering of it. Where a document
/// carries one, reading it is strictly better than looking at the text, which is why it is tried first.
/// </para>
/// <para>
/// <b>Verification status: unverified against a real Factur-X document.</b> The element names below
/// come from the UN/CEFACT CII structure and were exercised against fixtures written in this
/// repository, not against an invoice produced by commercial software. Matching is therefore done on
/// <em>local names only</em>, ignoring namespaces, so that ZUGFeRD 1.0, ZUGFeRD 2.x, Factur-X and
/// XRechnung-over-CII are all read by the same code rather than by a namespace guess that would fail
/// silently on one of them. Confirm against a genuine document before relying on this in production.
/// </para>
/// </summary>
public static class CrossIndustryInvoiceParser
{
    /// <summary>File names a Factur-X or ZUGFeRD attachment is published under.</summary>
    /// <remarks>
    /// The specifications name the attachment exactly, so recognising it is a lookup rather than a
    /// heuristic. Anything else embedded in the PDF is left alone: a document can legitimately carry
    /// attachments that are not invoices, and treating an arbitrary XML file as financial data would
    /// be precisely the silent-corruption risk this pipeline exists to avoid.
    /// </remarks>
    public static readonly string[] RecognisedAttachmentNames =
    [
        "factur-x.xml",
        "zugferd-invoice.xml",
        "xrechnung.xml",
        "cii.xml",
        "order-x.xml",
    ];

    public static bool IsRecognisedAttachmentName(string? name) =>
        name is not null
        && RecognisedAttachmentNames.Contains(Path.GetFileName(name.Trim()), StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses the XML, or returns a failure describing exactly what was not understood.</summary>
    public static DocumentExtraction Parse(
        ReadOnlySpan<byte> xml,
        Guid tenantId,
        string sourceDocumentId,
        ExtractionMethod method,
        string version)
    {
        XDocument document;

        try
        {
            // Hardened: the XML arrives inside an uploaded file, so it is hostile input until proven
            // otherwise. DTD processing is prohibited and no resolver is supplied, which closes both
            // entity expansion and external entity retrieval.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                CloseInput = true,
            };

            using var stream = new MemoryStream(xml.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);

            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            return DocumentExtraction.Failed(method, version, $"The embedded invoice XML is not well-formed: {ex.Message}");
        }

        var root = document.Root;

        if (root is null || !root.Name.LocalName.Contains("CrossIndustry", StringComparison.Ordinal))
        {
            return DocumentExtraction.Failed(
                method, version,
                $"The embedded XML root is '{root?.Name.LocalName ?? "(none)"}', which is not a Cross Industry Invoice. " +
                "UBL and other formats are not read by this extractor.");
        }

        var notes = new List<string>();

        var exchangedDocument = Descendant(root, "ExchangedDocument") ?? Descendant(root, "HeaderExchangedDocument");
        var transaction = Descendant(root, "SupplyChainTradeTransaction");

        if (transaction is null)
        {
            return DocumentExtraction.Failed(method, version, "The invoice XML has no SupplyChainTradeTransaction.");
        }

        var settlement = Descendant(transaction, "ApplicableHeaderTradeSettlement");
        var agreement = Descendant(transaction, "ApplicableHeaderTradeAgreement");

        var currency = Value(settlement, "InvoiceCurrencyCode");

        if (string.IsNullOrWhiteSpace(currency))
        {
            // Refused rather than defaulted. Guessing a currency on a financial document is the single
            // most damaging default available: every amount would be right and mean something else.
            return DocumentExtraction.Failed(
                method, version,
                "The invoice XML does not state an invoice currency code, and no currency may be assumed.");
        }

        var buyer = Descendant(agreement, "BuyerTradeParty");

        var customer = new Customer
        {
            TenantId = tenantId,
            SourceSystem = SourceSystem.DocumentUpload,
            CompanyName = Value(buyer, "Name"),
            VatNumber = VatRegistration(buyer),
            CountryCode = Value(Descendant(buyer, "PostalTradeAddress"), "CountryID"),
        };

        var invoice = new Invoice
        {
            TenantId = tenantId,
            SourceSystem = SourceSystem.DocumentUpload,
            SourceDocumentId = sourceDocumentId,
            SourceDocumentVersion = "1",
            InvoiceNumber = Value(exchangedDocument, "ID"),
            InvoiceDate = IssueDate(exchangedDocument),
            Currency = currency!.Trim().ToUpperInvariant(),
            CustomerId = customer.Id,
            Customer = customer,
            ExtractionStatus = ExtractionStatus.Succeeded,
        };

        invoice.BillingAddress.CountryCode = customer.CountryCode;

        var lines = new List<InvoiceLine>();

        foreach (var item in transaction.Descendants().Where(e => e.Name.LocalName == "IncludedSupplyChainTradeLineItem"))
        {
            var lineAgreement = Descendant(item, "SpecifiedLineTradeAgreement");
            var lineDelivery = Descendant(item, "SpecifiedLineTradeDelivery");
            var lineSettlement = Descendant(item, "SpecifiedLineTradeSettlement");
            var product = Descendant(item, "SpecifiedTradeProduct");

            var description = Value(product, "Name");

            if (string.IsNullOrWhiteSpace(description))
            {
                return DocumentExtraction.Failed(
                    method, version,
                    $"Line {lines.Count + 1} in the invoice XML has no product name.");
            }

            var quantity = Decimal(Value(lineDelivery, "BilledQuantity"));

            // The net price is preferred over the gross price: the gross one is before allowances, and
            // using it would overstate every discounted line.
            var unitPrice =
                Decimal(Value(Descendant(lineAgreement, "NetPriceProductTradePrice"), "ChargeAmount"))
                ?? Decimal(Value(Descendant(lineAgreement, "GrossPriceProductTradePrice"), "ChargeAmount"));

            if (quantity is null || unitPrice is null)
            {
                return DocumentExtraction.Failed(
                    method, version,
                    $"Line {lines.Count + 1} in the invoice XML has no billed quantity or no unit price.");
            }

            var lineTax = Descendant(lineSettlement, "ApplicableTradeTax");
            var rate = Decimal(Value(lineTax, "RateApplicablePercent")) ?? 0m;

            // The stated line total wins over quantity × price when both are present: it is what the
            // issuer asserts is owed, and a disagreement between the two is a discrepancy for a human
            // rather than something to silently recompute away.
            var statedTotal = Decimal(Value(
                Descendant(lineSettlement, "SpecifiedTradeSettlementLineMonetarySummation"), "LineTotalAmount"));

            var computedTotal = decimal.Round(quantity.Value * unitPrice.Value, Money.StorageScale, MidpointRounding.ToEven);
            var net = statedTotal ?? computedTotal;

            if (statedTotal is { } stated && Math.Abs(stated - computedTotal) > 0.01m)
            {
                notes.Add(
                    $"Line {lines.Count + 1}: the stated line total {stated.ToString(CultureInfo.InvariantCulture)} " +
                    $"differs from quantity × unit price ({computedTotal.ToString(CultureInfo.InvariantCulture)}). " +
                    "The stated total was kept; the difference is usually an allowance or charge this extractor does not read.");
            }

            var tax = decimal.Round(net * rate / 100m, Money.StorageScale, MidpointRounding.ToEven);

            lines.Add(new InvoiceLine
            {
                TenantId = tenantId,
                InvoiceId = invoice.Id,
                LineNumber = lines.Count + 1,
                Sku = SellerAssignedId(product),
                Description = description,
                Quantity = quantity.Value,
                UnitPrice = unitPrice.Value,
                NetAmount = net,
                TaxRatePercent = rate,
                TaxAmount = tax,
                GrossAmount = decimal.Round(net + tax, Money.StorageScale, MidpointRounding.ToEven),
                Currency = invoice.Currency,
            });
        }

        if (lines.Count == 0)
        {
            return DocumentExtraction.Failed(method, version, "The invoice XML contains no line items.");
        }

        var summation = Descendant(settlement, "SpecifiedTradeSettlementHeaderMonetarySummation");

        // Header totals are taken from the document where it states them, because those are the figures
        // the issuer is actually claiming. Derived totals are a fallback, and are recorded as such.
        var lineTotal = Decimal(Value(summation, "LineTotalAmount"));
        var taxTotal = Decimal(Value(summation, "TaxTotalAmount"));
        var grandTotal = Decimal(Value(summation, "GrandTotalAmount"));

        invoice.SubtotalAmount = lineTotal ?? decimal.Round(lines.Sum(l => l.NetAmount), Money.StorageScale, MidpointRounding.ToEven);
        invoice.TaxAmount = taxTotal ?? decimal.Round(lines.Sum(l => l.TaxAmount), Money.StorageScale, MidpointRounding.ToEven);
        invoice.TotalAmount = grandTotal ?? decimal.Round(invoice.SubtotalAmount + invoice.TaxAmount, Money.StorageScale, MidpointRounding.ToEven);

        if (lineTotal is null || taxTotal is null || grandTotal is null)
        {
            notes.Add("The invoice XML omitted one or more header totals; the missing ones were derived from the lines.");
        }

        var sellerName = Value(Descendant(agreement, "SellerTradeParty"), "Name");

        if (!string.IsNullOrWhiteSpace(sellerName))
        {
            notes.Add($"Issued by {sellerName}, according to the embedded invoice XML.");
        }

        notes.Add(
            "Read from the invoice's own embedded XML rather than from the printed page, so no character " +
            "recognition and no language model were involved.");

        return new DocumentExtraction(ExtractionOutcome.Succeeded, method, version, invoice, lines, customer, null, notes);
    }

    /// <summary>First descendant with this local name, ignoring namespace.</summary>
    private static XElement? Descendant(XElement? parent, string localName) =>
        parent?.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Value(XElement? parent, string localName)
    {
        var value = Descendant(parent, localName)?.Value.Trim();

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? VatRegistration(XElement? party) =>
        party?.Descendants()
            .Where(e => e.Name.LocalName == "SpecifiedTaxRegistration")
            .SelectMany(e => e.Descendants().Where(i => i.Name.LocalName == "ID"))
            // schemeID "VA" is the VAT identifier; "FC" is a local tax number and is not the same thing.
            .FirstOrDefault(i => string.Equals((string?)i.Attribute("schemeID"), "VA", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();

    private static string? SellerAssignedId(XElement? product) =>
        Value(product, "SellerAssignedID") ?? Value(product, "GlobalID");

    private static decimal? Decimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    /// <summary>
    /// Reads the issue date, which CII expresses as a string plus a format code rather than as a date.
    /// </summary>
    /// <remarks>
    /// Only format 102 (<c>yyyyMMdd</c>) and 610 (<c>yyyyMM</c>) are accepted. An unrecognised format
    /// code yields no date rather than a parse attempt: reading "01022026" as the wrong one of
    /// 1 February and 2 January is a silent error a reviewer has no way to notice.
    /// </remarks>
    private static DateOnly? IssueDate(XElement? exchangedDocument)
    {
        var element = exchangedDocument?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "DateTimeString");

        if (element is null)
        {
            return null;
        }

        var text = element.Value.Trim();
        var format = (string?)element.Attribute("format");

        return format switch
        {
            "102" when DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) => day,
            "610" when DateOnly.TryParseExact(text, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) => month,
            _ => null,
        };
    }
}
