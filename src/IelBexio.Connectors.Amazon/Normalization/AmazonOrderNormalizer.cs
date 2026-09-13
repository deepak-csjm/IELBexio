using System.Globalization;
using System.Text.Json;
using IelBexio.Application.Sources;
using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;

namespace IelBexio.Connectors.Amazon.Normalization;

/// <summary>
/// Converts an Amazon SP-API order plus its order items into canonical domain objects.
/// <para>
/// <b>Amazon is not Shopify, and this normaliser refuses to pretend otherwise (§10).</b> Three
/// differences shape the code:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>Prices are tax-inclusive.</b> SP-API reports <c>ItemPrice</c> as the amount the buyer paid
/// <em>including</em> <c>ItemTax</c>, whereas Shopify reports a net line total with tax alongside.
/// The canonical model stores net, so the net is derived as <c>ItemPrice − ItemTax</c>. Getting this
/// backwards would overstate revenue on every Amazon order, so it is asserted by tests.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Buyer identity is frequently absent.</b> Without an approved PII data-access role and a
/// Restricted Data Token, buyer name and email are null. That produces a deliberately anonymised
/// customer flagged for human review — never a fabricated one.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>There is no per-line tax rate.</b> Amazon reports tax amounts, not rates, so the rate is
/// back-computed and the assessment records that it was derived rather than stated.
/// </description>
/// </item>
/// </list>
/// </summary>
public sealed class AmazonOrderNormalizer
{
    public const string Version = "1.0.0";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Injected as a service, matching the Shopify normaliser.")]
    public SourceInvoiceDocument Normalize(JsonElement root, Guid tenantId, Guid? sourceConnectionId, string? apiVersion)
    {
        var order = root.TryGetProperty("order", out var orderNode) ? orderNode : root;

        var amazonOrderId = GetString(order, "AmazonOrderId")
            ?? throw new AmazonNormalizationException("Order payload has no 'AmazonOrderId'.");

        var lastUpdate = GetDateTimeOffset(order, "LastUpdateDate");
        var version = lastUpdate?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "1";

        var currency = GetString(GetObject(order, "OrderTotal"), "CurrencyCode") ?? "EUR";
        var fieldMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();

        var customer = BuildCustomer(order, tenantId, sourceConnectionId, fieldMap, notes);

        var invoice = new Invoice
        {
            TenantId = tenantId,
            SourceSystem = SourceSystem.Amazon,
            SourceConnectionId = sourceConnectionId,
            SourceDocumentId = amazonOrderId,
            SourceDocumentVersion = version,

            // Amazon has no invoice number of its own; the order id is the only stable document
            // reference, and it is used verbatim rather than a number being invented.
            InvoiceNumber = amazonOrderId,
            InvoiceDate = ToDateOnly(GetDateTimeOffset(order, "PurchaseDate")),
            Currency = currency,
            CustomerId = customer?.Id,
            Customer = customer,
            PaymentStatus = MapOrderStatus(GetString(order, "OrderStatus")),
            ExtractionStatus = ExtractionStatus.NotRequired,
        };

        fieldMap["invoiceNumber"] = "order.AmazonOrderId";
        fieldMap["invoiceDate"] = "order.PurchaseDate";
        fieldMap["currency"] = "order.OrderTotal.CurrencyCode";

        CopyAddress(GetObject(order, "ShippingAddress"), invoice.ShippingAddress);
        CopyAddress(GetObject(order, "ShippingAddress"), invoice.BillingAddress);
        fieldMap["billingAddress.countryCode"] = "order.ShippingAddress.CountryCode";
        notes.Add("Amazon supplies a shipping address only; it has been used for the billing address as well. Verify before booking.");

        var lines = BuildLines(root, invoice, currency, fieldMap, notes);

        invoice.SubtotalAmount = decimal.Round(lines.Where(l => l.Kind != LineKind.Shipping).Sum(l => l.NetAmount), Money.StorageScale, MidpointRounding.ToEven);
        invoice.ShippingAmount = decimal.Round(lines.Where(l => l.Kind == LineKind.Shipping).Sum(l => l.NetAmount), Money.StorageScale, MidpointRounding.ToEven);
        invoice.DiscountAmount = 0m; // promotions are already netted into the line amounts below
        invoice.TaxAmount = decimal.Round(lines.Sum(l => l.TaxAmount), Money.StorageScale, MidpointRounding.ToEven);
        invoice.TotalAmount = decimal.Round(invoice.SubtotalAmount + invoice.ShippingAmount + invoice.TaxAmount, Money.StorageScale, MidpointRounding.ToEven);

        fieldMap["subtotalAmount"] = "sum(orderItems[].ItemPrice - orderItems[].ItemTax)";
        fieldMap["taxAmount"] = "sum(orderItems[].ItemTax + orderItems[].ShippingTax)";
        fieldMap["totalAmount"] = "computed: subtotal + shipping + tax";

        // Amazon's own OrderTotal is the buyer-facing gross. Comparing our computed total against it is
        // a free consistency check, and a mismatch is surfaced rather than hidden.
        var reportedTotal = GetDecimal(GetObject(order, "OrderTotal"), "Amount");
        if (reportedTotal is { } reported && Math.Abs(reported - invoice.TotalAmount) > 0.05m)
        {
            notes.Add(
                $"Computed total {invoice.TotalAmount} {currency} differs from Amazon's reported OrderTotal " +
                $"{reported} {currency}. This usually means item-level data is incomplete (for example a " +
                "promotion or a gift-wrap charge that the Orders API does not itemise) and needs review.");
        }

        return new SourceInvoiceDocument
        {
            SourceDocumentId = amazonOrderId,
            SourceDocumentVersion = version,
            SourceSystem = SourceSystem.Amazon,
            RawPayload = root.GetRawText(),
            ApiVersion = apiVersion,
            Invoice = invoice,
            Customer = customer,
            Lines = lines,
            Payments = [],
            FieldSourceMap = fieldMap,
            Notes = notes,
        };
    }

    private static List<InvoiceLine> BuildLines(
        JsonElement root, Invoice invoice, string currency, Dictionary<string, string> fieldMap, List<string> notes)
    {
        var lines = new List<InvoiceLine>();

        if (!root.TryGetProperty("orderItems", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            notes.Add("No order items were supplied; the invoice has no lines and cannot be booked.");
            return lines;
        }

        var lineNumber = 0;

        foreach (var item in items.EnumerateArray())
        {
            lineNumber++;

            var quantity = GetDecimal(item, "QuantityOrdered") ?? 1m;
            var grossItem = Amount(item, "ItemPrice", currency);
            var itemTax = Amount(item, "ItemTax", currency);
            var promotion = Amount(item, "PromotionDiscount", currency);

            // SP-API ItemPrice is tax-inclusive: net is price minus tax. Promotions are subtracted so
            // the line reflects what was actually charged.
            var net = decimal.Round(grossItem - itemTax - promotion, Money.StorageScale, MidpointRounding.ToEven);
            var rate = net != 0m ? decimal.Round(itemTax / net * 100m, 4, MidpointRounding.ToEven) : 0m;

            lines.Add(new InvoiceLine
            {
                TenantId = invoice.TenantId,
                InvoiceId = invoice.Id,
                LineNumber = lineNumber,
                SourceLineId = GetString(item, "OrderItemId"),
                Sku = GetString(item, "SellerSKU"),
                ProductCode = GetString(item, "ASIN"),
                Description = GetString(item, "Title") ?? "(untitled item)",
                Quantity = quantity,
                UnitPrice = quantity != 0m ? decimal.Round(net / quantity, Money.StorageScale, MidpointRounding.ToEven) : net,
                DiscountAmount = promotion,
                NetAmount = net,
                TaxRatePercent = rate,
                TaxAmount = itemTax,
                GrossAmount = decimal.Round(net + itemTax, Money.StorageScale, MidpointRounding.ToEven),
                Currency = currency,
                Kind = LineKind.Product,
            });

            fieldMap[$"lines[{lineNumber}].netAmount"] = $"orderItems[{lineNumber - 1}].ItemPrice - ItemTax - PromotionDiscount";
            fieldMap[$"lines[{lineNumber}].taxAmount"] = $"orderItems[{lineNumber - 1}].ItemTax";
            fieldMap[$"lines[{lineNumber}].sku"] = $"orderItems[{lineNumber - 1}].SellerSKU";

            // Shipping is charged per item on Amazon, so it becomes its own line only when non-zero.
            var shippingGross = Amount(item, "ShippingPrice", currency);
            var shippingTax = Amount(item, "ShippingTax", currency);
            if (shippingGross != 0m || shippingTax != 0m)
            {
                lineNumber++;
                var shippingNet = decimal.Round(shippingGross - shippingTax, Money.StorageScale, MidpointRounding.ToEven);

                lines.Add(new InvoiceLine
                {
                    TenantId = invoice.TenantId,
                    InvoiceId = invoice.Id,
                    LineNumber = lineNumber,
                    Description = $"Shipping — {GetString(item, "Title") ?? "item"}",
                    Kind = LineKind.Shipping,
                    Quantity = 1m,
                    UnitPrice = shippingNet,
                    NetAmount = shippingNet,
                    TaxRatePercent = shippingNet != 0m ? decimal.Round(shippingTax / shippingNet * 100m, 4, MidpointRounding.ToEven) : 0m,
                    TaxAmount = shippingTax,
                    GrossAmount = decimal.Round(shippingNet + shippingTax, Money.StorageScale, MidpointRounding.ToEven),
                    Currency = currency,
                });
            }
        }

        notes.Add(
            "Amazon reports tax amounts but not tax rates; the per-line rate shown was back-computed " +
            "from tax ÷ net and must be verified before booking.");

        return lines;
    }

    private static Customer? BuildCustomer(
        JsonElement order, Guid tenantId, Guid? connectionId, Dictionary<string, string> fieldMap, List<string> notes)
    {
        var buyerInfo = GetObject(order, "BuyerInfo");
        var shippingAddress = GetObject(order, "ShippingAddress");
        var amazonOrderId = GetString(order, "AmazonOrderId");

        var buyerName = GetString(buyerInfo, "BuyerName") ?? GetString(shippingAddress, "Name");
        var buyerEmail = GetString(buyerInfo, "BuyerEmail");
        var companyName = GetString(GetObject(buyerInfo ?? default, "BuyerTaxInfo"), "CompanyLegalName");

        var hasIdentity = !string.IsNullOrWhiteSpace(buyerName) || !string.IsNullOrWhiteSpace(buyerEmail);

        var customer = new Customer
        {
            TenantId = tenantId,
            SourceSystem = SourceSystem.Amazon,
            SourceConnectionId = connectionId,

            // The Amazon order id is the only stable buyer-side reference available when PII is absent.
            SourceCustomerId = $"amazon-order:{amazonOrderId}",
            CompanyName = companyName ?? (hasIdentity ? null : "Amazon buyer (identity not supplied)"),
            FirstName = null,
            LastName = buyerName,
            Email = buyerEmail,
            CountryCode = GetString(shippingAddress, "CountryCode"),
            IsAnonymised = !hasIdentity,
        };

        CopyAddressInto(shippingAddress, customer.Address);

        fieldMap["customer.name"] = "order.BuyerInfo.BuyerName";
        fieldMap["customer.email"] = "order.BuyerInfo.BuyerEmail";

        if (!hasIdentity)
        {
            notes.Add(
                "Amazon supplied no buyer name or email. This is the normal case without an approved PII " +
                "data-access role and a Restricted Data Token. A placeholder customer has been created and " +
                "flagged for human review; no buyer identity has been invented.");
        }

        return customer;
    }

    private static PaymentStatus MapOrderStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "SHIPPED" or "INVOICEUNCONFIRMED" => PaymentStatus.Paid,
        "PENDING" or "UNSHIPPED" or "PARTIALLYSHIPPED" => PaymentStatus.Unpaid,
        "CANCELED" => PaymentStatus.Voided,
        _ => PaymentStatus.Unknown,
    };

    // ---- JSON helpers ------------------------------------------------------------------------------

    private static JsonElement? GetObject(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static JsonElement? GetObject(JsonElement? parent, string name) =>
        parent is { } p ? GetObject(p, name) : null;

    private static string? GetString(JsonElement? parent, string name) =>
        parent is { } p ? GetString(p, name) : null;

    private static string? GetString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? GetDecimal(JsonElement? parent, string name) =>
        parent is { } p ? GetDecimal(p, name) : null;

    private static decimal? GetDecimal(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
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

    /// <summary>Reads an SP-API Money object, refusing to mix currencies within one order.</summary>
    private static decimal Amount(JsonElement parent, string property, string expectedCurrency)
    {
        var node = GetObject(parent, property);
        if (node is null)
        {
            return 0m;
        }

        var currency = GetString(node, "CurrencyCode");
        if (currency is not null && !string.Equals(currency, expectedCurrency, StringComparison.OrdinalIgnoreCase))
        {
            throw new AmazonNormalizationException(
                $"'{property}' is in {currency} but the order is in {expectedCurrency}; mixed currencies are refused.");
        }

        var amount = GetDecimal(node.Value, "Amount");
        return amount is { } value
            ? decimal.Round(value, Money.StorageScale, MidpointRounding.ToEven)
            : 0m;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static DateOnly? ToDateOnly(DateTimeOffset? value) => value is { } v ? DateOnly.FromDateTime(v.UtcDateTime) : null;

    private static void CopyAddress(JsonElement? node, Address target) => CopyAddressInto(node, target);

    private static void CopyAddressInto(JsonElement? node, Address target)
    {
        if (node is not { } n)
        {
            return;
        }

        target.Line1 = GetString(n, "AddressLine1");
        target.Line2 = GetString(n, "AddressLine2");
        target.PostalCode = GetString(n, "PostalCode");
        target.City = GetString(n, "City");
        target.Region = GetString(n, "StateOrRegion");
        target.CountryCode = GetString(n, "CountryCode")?.ToUpperInvariant();
    }
}

public sealed class AmazonNormalizationException : Exception
{
    public AmazonNormalizationException(string message) : base(message) { }
    public AmazonNormalizationException() : base("Amazon payload could not be normalised.") { }
    public AmazonNormalizationException(string message, Exception innerException) : base(message, innerException) { }
}
