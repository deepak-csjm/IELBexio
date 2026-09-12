using IelBexio.Domain.Common;

namespace IelBexio.Domain.Invoicing;

/// <summary>A postal address. Owned value object — it has no identity of its own.</summary>
public sealed class Address
{
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }

    /// <summary>ISO-3166-1 alpha-2, upper case.</summary>
    public string? CountryCode { get; set; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Line1) && string.IsNullOrWhiteSpace(PostalCode) &&
        string.IsNullOrWhiteSpace(City) && string.IsNullOrWhiteSpace(CountryCode);

    public Address Clone() => new()
    {
        Line1 = Line1, Line2 = Line2, PostalCode = PostalCode,
        City = City, Region = Region, CountryCode = CountryCode,
    };
}

/// <summary>
/// Canonical customer. Source-independent: a Shopify customer and an Amazon buyer both normalise
/// into this shape, and neither vendor's field names appear here.
/// </summary>
public sealed class Customer : TenantEntity
{
    public SourceSystem SourceSystem { get; set; }
    public Guid? SourceConnectionId { get; set; }

    /// <summary>Identifier of the customer in the source system, verbatim.</summary>
    public string? SourceCustomerId { get; set; }

    public string? CompanyName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }

    /// <summary>VAT / UID number as supplied by the source. Never normalised silently — see provenance.</summary>
    public string? VatNumber { get; set; }

    public string? CountryCode { get; set; }
    public Address Address { get; set; } = new();

    /// <summary>Free-form external identifiers (e.g. Amazon buyer pseudo-id) as JSON.</summary>
    public string? ExternalIdentifiersJson { get; set; }

    /// <summary>True when buyer identity was unavailable from the source and a placeholder was used.</summary>
    public bool IsAnonymised { get; set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(CompanyName)
            ? CompanyName!
            : string.Join(' ', new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } n
                ? n
                : Email ?? SourceCustomerId ?? "(unknown customer)";
}
