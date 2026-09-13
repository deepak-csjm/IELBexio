using System.Globalization;
using System.Text;
using System.Text.Json;
using IelBexio.Domain.Common;
using IelBexio.Domain.Documents;
using IelBexio.Domain.Invoicing;

namespace IelBexio.Application.Documents;

/// <summary>The outcome of attempting to extract an invoice from a document.</summary>
public sealed record DocumentExtraction(
    bool Succeeded,
    ExtractionMethod Method,
    string ExtractorVersion,
    Invoice? Invoice,
    IReadOnlyList<InvoiceLine> Lines,
    Customer? Customer,
    string? FailureReason,
    IReadOnlyList<string> Notes)
{
    public static DocumentExtraction Failed(ExtractionMethod method, string version, string reason) =>
        new(false, method, version, null, [], null, reason, []);
}

/// <summary>
/// A deterministic extractor for one document format. Registered per format; the pipeline picks the
/// one that claims the document.
/// <para>
/// These exist because of principle 4: if ordinary software can reliably solve something, do not use
/// AI. A CSV and a JSON invoice are fully machine-readable, so sending them to a language model would
/// add cost, latency and a hallucination risk in exchange for nothing.
/// </para>
/// </summary>
public interface IDeterministicDocumentExtractor
{
    ExtractionMethod Method { get; }
    string Version { get; }
    bool CanExtract(DocumentKind kind, string contentType);
    DocumentExtraction Extract(ReadOnlySpan<byte> content, Guid tenantId, string sourceDocumentId);
}

/// <summary>
/// Extracts an invoice from a JSON document in this system's own canonical exchange shape.
/// <para>
/// Deliberately strict: unknown structure is a clean failure that routes the document to review, never
/// a partial guess. A half-extracted invoice is more dangerous than an unextracted one, because it
/// looks finished.
/// </para>
/// </summary>
public sealed class JsonInvoiceExtractor : IDeterministicDocumentExtractor
{
    public ExtractionMethod Method => ExtractionMethod.JsonParser;
    public string Version => "1.0.0";

    public bool CanExtract(DocumentKind kind, string contentType) =>
        kind == DocumentKind.Json || string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase);

    public DocumentExtraction Extract(ReadOnlySpan<byte> content, Guid tenantId, string sourceDocumentId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content.ToArray());
        }
        catch (JsonException ex)
        {
            return DocumentExtraction.Failed(Method, Version, $"The document is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return DocumentExtraction.Failed(Method, Version, "The JSON document's root is not an object.");
            }

            var notes = new List<string>();
            var currency = String(root, "currency") ?? "CHF";

            var customer = new Customer
            {
                TenantId = tenantId,
                SourceSystem = SourceSystem.DocumentUpload,
                CompanyName = String(root, "customerName"),
                VatNumber = String(root, "customerVatNumber"),
                CountryCode = String(root, "customerCountry"),
            };

            var invoice = new Invoice
            {
                TenantId = tenantId,
                SourceSystem = SourceSystem.DocumentUpload,
                SourceDocumentId = sourceDocumentId,
                SourceDocumentVersion = "1",
                InvoiceNumber = String(root, "invoiceNumber"),
                InvoiceDate = Date(root, "invoiceDate"),
                DueDate = Date(root, "dueDate"),
                Currency = currency,
                CustomerId = customer.Id,
                Customer = customer,
                ExtractionStatus = ExtractionStatus.Succeeded,
            };

            invoice.BillingAddress.CountryCode = customer.CountryCode;

            var lines = new List<InvoiceLine>();
            if (root.TryGetProperty("lines", out var lineArray) && lineArray.ValueKind == JsonValueKind.Array)
            {
                var lineNumber = 0;
                foreach (var lineNode in lineArray.EnumerateArray())
                {
                    lineNumber++;
                    var quantity = Decimal(lineNode, "quantity") ?? 1m;
                    var unitPrice = Decimal(lineNode, "unitPrice") ?? 0m;
                    var discount = Decimal(lineNode, "discount") ?? 0m;
                    var net = Decimal(lineNode, "netAmount") ?? decimal.Round((quantity * unitPrice) - discount, Money.StorageScale, MidpointRounding.ToEven);
                    var rate = Decimal(lineNode, "taxRatePercent") ?? 0m;
                    var tax = Decimal(lineNode, "taxAmount") ?? decimal.Round(net * rate / 100m, Money.StorageScale, MidpointRounding.ToEven);

                    lines.Add(new InvoiceLine
                    {
                        TenantId = tenantId,
                        InvoiceId = invoice.Id,
                        LineNumber = lineNumber,
                        Sku = String(lineNode, "sku"),
                        Description = String(lineNode, "description") ?? "(untitled line)",
                        Quantity = quantity,
                        UnitPrice = unitPrice,
                        DiscountAmount = discount,
                        NetAmount = net,
                        TaxRatePercent = rate,
                        TaxAmount = tax,
                        GrossAmount = decimal.Round(net + tax, Money.StorageScale, MidpointRounding.ToEven),
                        Currency = currency,
                    });
                }
            }

            if (lines.Count == 0)
            {
                return DocumentExtraction.Failed(Method, Version, "The document contains no invoice lines.");
            }

            // Header totals are taken from the document when present and derived from the lines when
            // not. Either way deterministic validation re-checks them afterwards, so a document whose
            // stated totals disagree with its own lines is caught rather than trusted.
            invoice.SubtotalAmount = Decimal(root, "subtotal") ?? decimal.Round(lines.Where(l => l.Kind != LineKind.Shipping).Sum(l => l.NetAmount), Money.StorageScale, MidpointRounding.ToEven);
            invoice.DiscountAmount = Decimal(root, "discount") ?? 0m;
            invoice.ShippingAmount = Decimal(root, "shipping") ?? 0m;
            invoice.TaxAmount = Decimal(root, "taxAmount") ?? decimal.Round(lines.Sum(l => l.TaxAmount), Money.StorageScale, MidpointRounding.ToEven);
            invoice.TotalAmount = Decimal(root, "total") ?? decimal.Round(invoice.SubtotalAmount + invoice.ShippingAmount + invoice.TaxAmount - invoice.DiscountAmount, Money.StorageScale, MidpointRounding.ToEven);

            if (Decimal(root, "total") is null)
            {
                notes.Add("The document stated no total; it was derived from the lines and must be verified.");
            }

            return new DocumentExtraction(true, Method, Version, invoice, lines, customer, null, notes);
        }
    }

    private static string? String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? Decimal(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static DateOnly? Date(JsonElement parent, string name) =>
        String(parent, name) is { } text && DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}

/// <summary>
/// Extracts an invoice from a CSV line-item export.
/// <para>
/// Expected header (order-independent, case-insensitive):
/// <c>invoiceNumber, invoiceDate, currency, customerName, customerVatNumber, sku, description,
/// quantity, unitPrice, discount, taxRatePercent</c>.
/// </para>
/// <para>
/// The parser handles RFC 4180 quoting, because an unquoted naive split on commas silently mangles any
/// description containing a comma — and a mangled description shifts every subsequent column, turning a
/// price into a quantity.
/// </para>
/// </summary>
public sealed class CsvInvoiceExtractor : IDeterministicDocumentExtractor
{
    public ExtractionMethod Method => ExtractionMethod.CsvParser;
    public string Version => "1.0.0";

    public bool CanExtract(DocumentKind kind, string contentType) =>
        kind == DocumentKind.Csv || string.Equals(contentType, "text/csv", StringComparison.OrdinalIgnoreCase);

    public DocumentExtraction Extract(ReadOnlySpan<byte> content, Guid tenantId, string sourceDocumentId)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (ArgumentException)
        {
            return DocumentExtraction.Failed(Method, Version, "The file is not valid UTF-8 text.");
        }

        var rows = ParseCsv(text);
        if (rows.Count < 2)
        {
            return DocumentExtraction.Failed(Method, Version, "The CSV has no data rows.");
        }

        var header = rows[0]
            .Select((name, index) => (Name: name.Trim(), Index: index))
            .ToDictionary(h => h.Name, h => h.Index, StringComparer.OrdinalIgnoreCase);

        foreach (var required in new[] { "description", "quantity", "unitPrice" })
        {
            if (!header.ContainsKey(required))
            {
                return DocumentExtraction.Failed(Method, Version, $"The CSV header is missing the required column '{required}'.");
            }
        }

        var notes = new List<string>();
        var firstRow = rows[1];

        var currency = Field(firstRow, header, "currency") ?? "CHF";

        var customer = new Customer
        {
            TenantId = tenantId,
            SourceSystem = SourceSystem.DocumentUpload,
            CompanyName = Field(firstRow, header, "customerName"),
            VatNumber = Field(firstRow, header, "customerVatNumber"),
            CountryCode = Field(firstRow, header, "customerCountry"),
        };

        var invoice = new Invoice
        {
            TenantId = tenantId,
            SourceSystem = SourceSystem.DocumentUpload,
            SourceDocumentId = sourceDocumentId,
            SourceDocumentVersion = "1",
            InvoiceNumber = Field(firstRow, header, "invoiceNumber"),
            InvoiceDate = ParseDate(Field(firstRow, header, "invoiceDate")),
            Currency = currency,
            CustomerId = customer.Id,
            Customer = customer,
            ExtractionStatus = ExtractionStatus.Succeeded,
        };

        invoice.BillingAddress.CountryCode = customer.CountryCode;

        var lines = new List<InvoiceLine>();

        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var description = Field(row, header, "description");
            if (string.IsNullOrWhiteSpace(description))
            {
                notes.Add($"Row {rowIndex + 1} has no description and was skipped.");
                continue;
            }

            var quantity = ParseDecimal(Field(row, header, "quantity"));
            var unitPrice = ParseDecimal(Field(row, header, "unitPrice"));

            if (quantity is null || unitPrice is null)
            {
                // A row whose numbers will not parse is a hard failure, not a zero. Booking a line at
                // zero because a price was malformed is exactly the silent corruption to avoid.
                return DocumentExtraction.Failed(
                    Method, Version,
                    $"Row {rowIndex + 1} has a quantity or unit price that is not a valid number.");
            }

            var discount = ParseDecimal(Field(row, header, "discount")) ?? 0m;
            var rate = ParseDecimal(Field(row, header, "taxRatePercent")) ?? 0m;
            var net = decimal.Round((quantity.Value * unitPrice.Value) - discount, Money.StorageScale, MidpointRounding.ToEven);
            var tax = decimal.Round(net * rate / 100m, Money.StorageScale, MidpointRounding.ToEven);

            lines.Add(new InvoiceLine
            {
                TenantId = tenantId,
                InvoiceId = invoice.Id,
                LineNumber = lines.Count + 1,
                Sku = Field(row, header, "sku"),
                Description = description,
                Quantity = quantity.Value,
                UnitPrice = unitPrice.Value,
                DiscountAmount = discount,
                NetAmount = net,
                TaxRatePercent = rate,
                TaxAmount = tax,
                GrossAmount = decimal.Round(net + tax, Money.StorageScale, MidpointRounding.ToEven),
                Currency = currency,
            });
        }

        if (lines.Count == 0)
        {
            return DocumentExtraction.Failed(Method, Version, "The CSV contained no usable invoice lines.");
        }

        invoice.SubtotalAmount = decimal.Round(lines.Sum(l => l.NetAmount), Money.StorageScale, MidpointRounding.ToEven);
        invoice.TaxAmount = decimal.Round(lines.Sum(l => l.TaxAmount), Money.StorageScale, MidpointRounding.ToEven);
        invoice.TotalAmount = decimal.Round(invoice.SubtotalAmount + invoice.TaxAmount, Money.StorageScale, MidpointRounding.ToEven);

        notes.Add("Header totals were derived from the line items, since a CSV line export carries no header totals.");

        return new DocumentExtraction(true, Method, Version, invoice, lines, customer, null, notes);
    }

    private static string? Field(List<string> row, Dictionary<string, int> header, string name) =>
        header.TryGetValue(name, out var index) && index < row.Count && !string.IsNullOrWhiteSpace(row[index])
            ? row[index].Trim()
            : null;

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    /// <summary>RFC 4180 CSV parsing: quoted fields, doubled quotes, embedded commas and newlines.</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote inside a quoted field is a literal quote.
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                case ';':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
