using System.Globalization;
using System.Text.Json;
using IelBexio.Application.Sources;
using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;

namespace IelBexio.Connectors.Shopify.Normalization;

/// <summary>
/// Converts a Shopify Admin GraphQL <c>Order</c> node into canonical domain objects.
/// <para>
/// This is the boundary the specification insists on (§7): Shopify's vocabulary stops here. Nothing
/// downstream knows what a <c>currentTotalPriceSet</c> or a <c>MoneyBag</c> is. Every mapping is
/// recorded in <see cref="SourceInvoiceDocument.FieldSourceMap"/> so the canonical value can be traced
/// back to the exact source field (§22).
/// </para>
/// <para>
/// <b>Money handling.</b> Shopify returns money amounts as JSON <em>strings</em> precisely so clients
/// do not parse them as doubles. They are parsed with <see cref="decimal"/> and invariant culture, and
/// a value that will not parse is an error rather than a silent zero — a silently-zeroed amount is the
/// single most dangerous failure mode in an accounting integration.
/// </para>
/// <para>
/// <b>Shop money vs presentment money.</b> <c>shopMoney</c> is used throughout: it is the merchant's
/// own currency, which is what the books are kept in. Presentment (buyer) currency is deliberately
/// ignored rather than mixed in.
/// </para>
/// </summary>
public sealed class ShopifyOrderNormalizer
{
    public const string Version = "1.0.0";

    /// <summary>
    /// Normalises one order node. <paramref name="tenantId"/> is applied to every produced entity so
    /// nothing tenant-less can escape the connector.
    /// </summary>
    /// <remarks>
    /// Kept as an instance member although it currently holds no state: the normaliser is registered
    /// and injected as a service, and making it static would push every consumer to a type reference
    /// that could not later be swapped for a version-specific implementation.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Injected as a service; see remarks.")]
    public SourceInvoiceDocument Normalize(JsonElement order, Guid tenantId, Guid? sourceConnectionId, string? apiVersion)
    {
        var sourceId = GetString(order, "id") ?? throw new ShopifyNormalizationException("Order node has no 'id'.");
        var updatedAt = GetDateTimeOffset(order, "updatedAt");

        // The source document version is what makes idempotency meaningful across re-imports: the same
        // order re-fetched unchanged yields the same version, while an edited order yields a new one
        // and is therefore treated as a new document rather than silently overwriting the old (§19).
        var version = updatedAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "1";

        var currency = GetString(order, "currencyCode") ?? "CHF";
        var fieldMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();

        var customer = BuildCustomer(order, tenantId, sourceConnectionId, fieldMap, notes);

        var invoice = new Invoice
        {
            TenantId = tenantId,
            SourceSystem = SourceSystem.Shopify,
            SourceConnectionId = sourceConnectionId,
            SourceDocumentId = sourceId,
            SourceDocumentVersion = version,
            InvoiceNumber = GetString(order, "name"),
            InvoiceDate = ToDateOnly(GetDateTimeOffset(order, "processedAt") ?? GetDateTimeOffset(order, "createdAt")),
            Currency = currency,
            CustomerId = customer?.Id,
            Customer = customer,
            ExtractionStatus = ExtractionStatus.NotRequired,
        };

        fieldMap["invoiceNumber"] = "name";
        fieldMap["invoiceDate"] = "processedAt";
        fieldMap["currency"] = "currencyCode";

        CopyAddress(order, "billingAddress", invoice.BillingAddress, fieldMap, "billingAddress");
        CopyAddress(order, "shippingAddress", invoice.ShippingAddress, fieldMap, "shippingAddress");

        invoice.SubtotalAmount = MoneyBag(order, "subtotalPriceSet", currency, "subtotalAmount", fieldMap);
        invoice.DiscountAmount = MoneyBag(order, "totalDiscountsSet", currency, "discountAmount", fieldMap);
        invoice.ShippingAmount = MoneyBag(order, "totalShippingPriceSet", currency, "shippingAmount", fieldMap);
        invoice.TaxAmount = MoneyBag(order, "totalTaxSet", currency, "taxAmount", fieldMap);
        invoice.TotalAmount = MoneyBag(order, "currentTotalPriceSet", currency, "totalAmount", fieldMap);

        invoice.PaymentStatus = MapFinancialStatus(GetString(order, "displayFinancialStatus"));
        fieldMap["paymentStatus"] = "displayFinancialStatus";

        // A fully refunded order is a credit note, not an invoice with odd signs. Saying so explicitly
        // keeps the negative-total validation honest.
        invoice.DocumentStatus = invoice.PaymentStatus == PaymentStatus.Refunded || invoice.TotalAmount < 0m
            ? DocumentStatus.CreditNote
            : DocumentStatus.Issued;

        var lines = BuildLines(order, invoice, currency, fieldMap, notes);
        var payments = BuildPayments(order, invoice, fieldMap);

        if (GetBool(order, "test") == true)
        {
            notes.Add("Shopify flags this order as a test order; it should not normally be booked.");
        }

        return new SourceInvoiceDocument
        {
            SourceDocumentId = sourceId,
            SourceDocumentVersion = version,
            SourceSystem = SourceSystem.Shopify,
            RawPayload = order.GetRawText(),
            ApiVersion = apiVersion,
            Invoice = invoice,
            Customer = customer,
            Lines = lines,
            Payments = payments,
            FieldSourceMap = fieldMap,
            Notes = notes,
        };
    }

    private static List<InvoiceLine> BuildLines(
        JsonElement order, Invoice invoice, string currency, Dictionary<string, string> fieldMap, List<string> notes)
    {
        var lines = new List<InvoiceLine>();
        var lineNumber = 0;

        foreach (var edge in Edges(order, "lineItems"))
        {
            lineNumber++;
            var path = $"lineItems.edges[{lineNumber - 1}].node";

            var quantity = GetDecimal(edge, "quantity") ?? 1m;
            var unitPrice = MoneyBagValue(edge, "originalUnitPriceSet", currency);
            var net = MoneyBagValue(edge, "discountedTotalSet", currency);
            var discount = MoneyBagValue(edge, "totalDiscountSet", currency);

            // taxLines is a list because a jurisdiction may levy several taxes on one line. The POC
            // aggregates them into a single effective rate and says so, rather than silently keeping
            // only the first, which would understate the tax.
            var (taxAmount, effectiveRate, taxTitle, taxLineCount) = AggregateTaxLines(edge, currency, net);

            if (taxLineCount > 1)
            {
                notes.Add($"Line {lineNumber} carries {taxLineCount} Shopify tax lines; they were aggregated into one effective rate of {effectiveRate}%.");
            }

            var line = new InvoiceLine
            {
                TenantId = invoice.TenantId,
                InvoiceId = invoice.Id,
                LineNumber = lineNumber,
                SourceLineId = GetString(edge, "id"),
                Sku = GetString(edge, "sku") ?? GetString(GetObject(edge, "variant"), "sku"),
                Description = GetString(edge, "name") ?? "(untitled line)",
                Quantity = quantity,
                UnitPrice = unitPrice,
                DiscountAmount = discount,
                NetAmount = net,
                TaxRatePercent = effectiveRate,
                TaxAmount = taxAmount,
                GrossAmount = decimal.Round(net + taxAmount, Money.StorageScale, MidpointRounding.ToEven),
                Currency = currency,
                Kind = LineKind.Product,
            };

            fieldMap[$"lines[{lineNumber}].netAmount"] = $"{path}.discountedTotalSet.shopMoney.amount";
            fieldMap[$"lines[{lineNumber}].taxAmount"] = $"{path}.taxLines[].priceSet.shopMoney.amount";
            fieldMap[$"lines[{lineNumber}].sku"] = $"{path}.sku";

            lines.Add(line);
        }

        // Shopify carries shipping as a separate object, not a line item. It becomes an explicit
        // shipping line so it can be mapped to its own Bexio revenue account and taxed correctly.
        var shippingLine = GetObject(order, "shippingLine");
        if (shippingLine is { ValueKind: JsonValueKind.Object })
        {
            var shippingNet = MoneyBagValue(shippingLine.Value, "originalPriceSet", currency);
            if (shippingNet != 0m)
            {
                lineNumber++;
                var (shipTax, shipRate, _, _) = AggregateTaxLines(shippingLine.Value, currency, shippingNet);

                lines.Add(new InvoiceLine
                {
                    TenantId = invoice.TenantId,
                    InvoiceId = invoice.Id,
                    LineNumber = lineNumber,
                    Description = GetString(shippingLine.Value, "title") ?? "Shipping",
                    Kind = LineKind.Shipping,
                    Quantity = 1m,
                    UnitPrice = shippingNet,
                    NetAmount = shippingNet,
                    TaxRatePercent = shipRate,
                    TaxAmount = shipTax,
                    GrossAmount = decimal.Round(shippingNet + shipTax, Money.StorageScale, MidpointRounding.ToEven),
                    Currency = currency,
                });

                fieldMap[$"lines[{lineNumber}].netAmount"] = "shippingLine.originalPriceSet.shopMoney.amount";
            }
        }

        return lines;
    }

    /// <summary>
    /// Sums a line's tax lines and derives the effective percentage from the actual amounts rather
    /// than trusting a single reported rate. When several rates apply, the blended rate is the only
    /// number that reproduces the money.
    /// </summary>
    private static (decimal TaxAmount, decimal EffectiveRate, string? Title, int Count) AggregateTaxLines(
        JsonElement node, string currency, decimal netAmount)
    {
        if (!node.TryGetProperty("taxLines", out var taxLines) || taxLines.ValueKind != JsonValueKind.Array)
        {
            return (0m, 0m, null, 0);
        }

        var total = 0m;
        string? title = null;
        var count = 0;

        foreach (var taxLine in taxLines.EnumerateArray())
        {
            count++;
            total += MoneyBagValue(taxLine, "priceSet", currency);
            title ??= GetString(taxLine, "title");
        }

        if (count == 0)
        {
            return (0m, 0m, null, 0);
        }

        decimal effectiveRate;
        if (netAmount != 0m)
        {
            effectiveRate = decimal.Round(total / netAmount * 100m, 4, MidpointRounding.ToEven);
        }
        else if (count == 1)
        {
            // A zero-net line cannot yield a rate by division; fall back to the reported percentage.
            var single = taxLines.EnumerateArray().First();
            effectiveRate = GetDecimal(single, "ratePercentage")
                ?? (GetDecimal(single, "rate") is { } r ? decimal.Round(r * 100m, 4, MidpointRounding.ToEven) : 0m);
        }
        else
        {
            effectiveRate = 0m;
        }

        return (decimal.Round(total, Money.StorageScale, MidpointRounding.ToEven), effectiveRate, title, count);
    }

    private static Customer? BuildCustomer(
        JsonElement order, Guid tenantId, Guid? connectionId, Dictionary<string, string> fieldMap, List<string> notes)
    {
        var customerNode = GetObject(order, "customer");
        var billing = GetObject(order, "billingAddress");

        var company = GetString(billing, "company");
        var email = GetString(customerNode, "email");
        var sourceCustomerId = GetString(customerNode, "id");

        if (sourceCustomerId is null && company is null && email is null)
        {
            notes.Add("Shopify supplied no customer, billing company or email; the invoice has no resolvable customer.");
            return null;
        }

        var customer = new Customer
        {
            TenantId = tenantId,
            SourceSystem = SourceSystem.Shopify,
            SourceConnectionId = connectionId,
            SourceCustomerId = sourceCustomerId,
            CompanyName = company,
            FirstName = GetString(customerNode, "firstName"),
            LastName = GetString(customerNode, "lastName"),
            Email = email,
            Phone = GetString(customerNode, "phone"),
            CountryCode = GetString(billing, "countryCodeV2"),
            VatNumber = ExtractTaxCredential(order),
        };

        CopyAddressInto(billing, customer.Address);

        fieldMap["customer.companyName"] = "billingAddress.company";
        fieldMap["customer.email"] = "customer.email";
        fieldMap["customer.vatNumber"] = "localizationExtensions.edges[].node.value";

        if (customer.VatNumber is null && company is not null)
        {
            notes.Add($"No tax credential (VAT/UID) was present on the order for company '{company}'.");
        }

        return customer;
    }

    /// <summary>
    /// Shopify carries a buyer's tax identifier in localization extensions rather than on the customer.
    /// Keys are region-specific (TAX_CREDENTIAL_CH, TAX_CREDENTIAL_IT, …), so any TAX_CREDENTIAL_* key
    /// is accepted rather than hardcoding one market.
    /// </summary>
    private static string? ExtractTaxCredential(JsonElement order)
    {
        foreach (var node in Edges(order, "localizationExtensions"))
        {
            var key = GetString(node, "key");
            if (key is not null && key.StartsWith("TAX_CREDENTIAL", StringComparison.OrdinalIgnoreCase))
            {
                var value = GetString(node, "value");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static List<Payment> BuildPayments(JsonElement order, Invoice invoice, Dictionary<string, string> fieldMap)
    {
        var payments = new List<Payment>();

        if (!order.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array)
        {
            return payments;
        }

        var index = 0;
        foreach (var transaction in transactions.EnumerateArray())
        {
            var kind = GetString(transaction, "kind");
            var amount = MoneyBagValue(transaction, "amountSet", invoice.Currency);

            // A refund reduces what was received, so it is recorded with the opposite sign rather than
            // as another positive payment.
            if (string.Equals(kind, "REFUND", StringComparison.OrdinalIgnoreCase))
            {
                amount = -amount;
            }

            payments.Add(new Payment
            {
                TenantId = invoice.TenantId,
                InvoiceId = invoice.Id,
                SourcePaymentId = GetString(transaction, "id"),
                Method = GetString(transaction, "gateway"),
                PaidAt = GetDateTimeOffset(transaction, "processedAt"),
                Amount = amount,
                Currency = invoice.Currency,
                Status = GetString(transaction, "status"),
                TransactionReference = GetString(transaction, "id"),
            });

            fieldMap[$"payments[{index}].amount"] = $"transactions[{index}].amountSet.shopMoney.amount";
            index++;
        }

        return payments;
    }

    private static PaymentStatus MapFinancialStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "PAID" => PaymentStatus.Paid,
        "PARTIALLY_PAID" => PaymentStatus.PartiallyPaid,
        "PENDING" or "AUTHORIZED" or "UNPAID" => PaymentStatus.Unpaid,
        "REFUNDED" => PaymentStatus.Refunded,
        "PARTIALLY_REFUNDED" => PaymentStatus.PartiallyRefunded,
        "VOIDED" => PaymentStatus.Voided,
        _ => PaymentStatus.Unknown,
    };

    // ---- JSON helpers. Deliberately strict about money, lenient about absence. --------------------

    private static IEnumerable<JsonElement> Edges(JsonElement parent, string connectionName)
    {
        if (!parent.TryGetProperty(connectionName, out var connection) || connection.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (!connection.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var edge in edges.EnumerateArray())
        {
            if (edge.TryGetProperty("node", out var node))
            {
                yield return node;
            }
        }
    }

    private static JsonElement? GetObject(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? GetString(JsonElement? parent, string name) =>
        parent is { } p ? GetString(p, name) : null;

    private static string? GetString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? GetBool(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static decimal? GetDecimal(JsonElement parent, string name)
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

    private static DateTimeOffset? GetDateTimeOffset(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static DateOnly? ToDateOnly(DateTimeOffset? value) =>
        value is { } v ? DateOnly.FromDateTime(v.UtcDateTime) : null;

    private static decimal MoneyBag(JsonElement order, string property, string currency, string canonicalField, Dictionary<string, string> fieldMap)
    {
        fieldMap[canonicalField] = $"{property}.shopMoney.amount";
        return MoneyBagValue(order, property, currency);
    }

    /// <summary>
    /// Reads a Shopify MoneyBag's shopMoney amount. An amount that is present but unparseable throws:
    /// treating it as zero would silently corrupt an invoice total.
    /// </summary>
    private static decimal MoneyBagValue(JsonElement parent, string property, string expectedCurrency)
    {
        if (!parent.TryGetProperty(property, out var bag) || bag.ValueKind != JsonValueKind.Object)
        {
            return 0m;
        }

        if (!bag.TryGetProperty("shopMoney", out var shopMoney) || shopMoney.ValueKind != JsonValueKind.Object)
        {
            return 0m;
        }

        if (!shopMoney.TryGetProperty("amount", out var amount))
        {
            return 0m;
        }

        var currency = GetString(shopMoney, "currencyCode");
        if (currency is not null && !string.Equals(currency, expectedCurrency, StringComparison.OrdinalIgnoreCase))
        {
            throw new ShopifyNormalizationException(
                $"'{property}.shopMoney' is in {currency} but the order is in {expectedCurrency}. " +
                "Mixing currencies within one invoice is refused rather than silently converted.");
        }

        return amount.ValueKind switch
        {
            JsonValueKind.Number => decimal.Round(amount.GetDecimal(), Money.StorageScale, MidpointRounding.ToEven),
            JsonValueKind.String when decimal.TryParse(amount.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                => decimal.Round(parsed, Money.StorageScale, MidpointRounding.ToEven),
            JsonValueKind.Null => 0m,
            _ => throw new ShopifyNormalizationException($"'{property}.shopMoney.amount' is not a parseable monetary amount."),
        };
    }

    private static void CopyAddress(JsonElement order, string property, Address target, Dictionary<string, string> fieldMap, string sourcePath)
    {
        var node = GetObject(order, property);
        if (node is null)
        {
            return;
        }

        CopyAddressInto(node, target);
        fieldMap[$"{property}.countryCode"] = $"{sourcePath}.countryCodeV2";
    }

    private static void CopyAddressInto(JsonElement? node, Address target)
    {
        if (node is not { } n)
        {
            return;
        }

        target.Line1 = GetString(n, "address1");
        target.Line2 = GetString(n, "address2");
        target.PostalCode = GetString(n, "zip");
        target.City = GetString(n, "city");
        target.Region = GetString(n, "province");
        target.CountryCode = GetString(n, "countryCodeV2")?.ToUpperInvariant();
    }
}

/// <summary>Raised when a Shopify payload cannot be normalised safely. Never swallowed into a zero.</summary>
public sealed class ShopifyNormalizationException : Exception
{
    public ShopifyNormalizationException(string message) : base(message) { }
    public ShopifyNormalizationException() : base("Shopify payload could not be normalised.") { }
    public ShopifyNormalizationException(string message, Exception innerException) : base(message, innerException) { }
}
