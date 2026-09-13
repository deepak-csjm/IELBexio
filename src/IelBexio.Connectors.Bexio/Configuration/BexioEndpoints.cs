namespace IelBexio.Connectors.Bexio.Configuration;

/// <summary>
/// How confident we are that a given endpoint or scope string is correct.
/// <para>
/// This exists because of a hard constraint in the build environment: <c>docs.bexio.com</c>,
/// <c>developer.bexio.com</c>, <c>api.bexio.com</c> and <c>auth.bexio.com</c> are all unreachable
/// through the egress policy, so no value below could be confirmed against primary documentation and
/// no live call was ever made. Specification §4 requires that such uncertainty be isolated and
/// documented rather than hidden, and §3 forbids inventing endpoints and presenting them as fact.
/// Marking each entry is how this codebase complies.
/// </para>
/// </summary>
public enum VerificationStatus
{
    /// <summary>Confirmed against primary Bexio documentation or an observed live response.</summary>
    VerifiedAgainstOfficialDocs = 0,

    /// <summary>Given verbatim in the project specification, which we treat as authoritative for this POC.</summary>
    StatedInProjectSpecification = 1,

    /// <summary>Corroborated by multiple independent secondary sources (SDKs, community docs). NOT primary.</summary>
    CorroboratedBySecondarySources = 2,

    /// <summary>Our own assumption. Must be confirmed before any production use.</summary>
    UnverifiedAssumption = 3,
}

/// <summary>One endpoint in the catalog, with its verification provenance attached.</summary>
public sealed record BexioEndpoint(string Name, string Method, string PathTemplate, VerificationStatus Verification, string Note)
{
    public override string ToString() => $"{Method} {PathTemplate} [{Verification}]";
}

/// <summary>
/// The single place every Bexio path, scope and host string is written down.
/// <para>
/// <b>Why a catalog rather than inline strings.</b> Because the values could not be verified against
/// primary documentation, they must be correctable in one edit. Nothing outside this file hardcodes a
/// Bexio path, and the API client resolves every request through it. If the real API differs, the fix
/// is this file plus the contract tests that pin it — not a hunt through the codebase.
/// </para>
/// <para>
/// Bexio exposes several endpoint generations (<c>/2.0</c>, <c>/3.0</c>, …) and they do not agree
/// resource by resource (§5), so each entry carries its own version prefix rather than sharing one.
/// </para>
/// </summary>
public static class BexioEndpoints
{
    /// <summary>API host. Stated verbatim in the project specification §4.</summary>
    public const string DefaultApiBaseUrl = "https://api.bexio.com";

    /// <summary>OIDC issuer. Stated verbatim in the project specification §4.</summary>
    public const string DefaultIssuer = "https://auth.bexio.com/realms/bexio";

    /// <summary>Authorization endpoint. Stated verbatim in the project specification §4.</summary>
    public const string DefaultAuthorizationEndpoint = "https://auth.bexio.com/realms/bexio/protocol/openid-connect/auth";

    /// <summary>Token endpoint. Stated verbatim in the project specification §4.</summary>
    public const string DefaultTokenEndpoint = "https://auth.bexio.com/realms/bexio/protocol/openid-connect/token";

    public static readonly BexioEndpoint CompanyProfile = new(
        "CompanyProfile", "GET", "/2.0/company_profile",
        VerificationStatus.CorroboratedBySecondarySources,
        "Company/organisation profile. Version prefix not confirmed against primary docs.");

    public static readonly BexioEndpoint Taxes = new(
        "Taxes", "GET", "/3.0/taxes",
        VerificationStatus.StatedInProjectSpecification,
        "Specification §6 states taxes are exposed under /3.0/taxes and that invoice positions reference tax_id.");

    public static readonly BexioEndpoint Accounts = new(
        "Accounts", "GET", "/2.0/accounts",
        VerificationStatus.CorroboratedBySecondarySources,
        "Chart of accounts. Used to resolve revenue accounts dynamically; ids are never hardcoded.");

    public static readonly BexioEndpoint Currencies = new(
        "Currencies", "GET", "/3.0/currencies",
        VerificationStatus.UnverifiedAssumption,
        "Currency list. Used only to warn during preflight; a failure here is non-fatal.");

    public static readonly BexioEndpoint ContactList = new(
        "ContactList", "GET", "/2.0/contact",
        VerificationStatus.CorroboratedBySecondarySources,
        "Contacts/customers.");

    public static readonly BexioEndpoint ContactSearch = new(
        "ContactSearch", "POST", "/2.0/contact/search",
        VerificationStatus.CorroboratedBySecondarySources,
        "Bexio's search endpoints take a POST body of field/criteria objects.");

    public static readonly BexioEndpoint ContactById = new(
        "ContactById", "GET", "/2.0/contact/{id}",
        VerificationStatus.CorroboratedBySecondarySources,
        "Single contact by id.");

    public static readonly BexioEndpoint ContactCreate = new(
        "ContactCreate", "POST", "/2.0/contact",
        VerificationStatus.CorroboratedBySecondarySources,
        "Create contact. Only used when an operator explicitly asks to create a Bexio contact.");

    public static readonly BexioEndpoint ArticleList = new(
        "ArticleList", "GET", "/2.0/article",
        VerificationStatus.CorroboratedBySecondarySources,
        "Articles/products.");

    public static readonly BexioEndpoint ArticleSearch = new(
        "ArticleSearch", "POST", "/2.0/article/search",
        VerificationStatus.CorroboratedBySecondarySources,
        "Article search by code or name.");

    public static readonly BexioEndpoint InvoiceCreate = new(
        "InvoiceCreate", "POST", "/2.0/kb_invoice",
        VerificationStatus.CorroboratedBySecondarySources,
        "Create invoice. Multiple independent SDKs use /2.0/kb_invoice with a positions array.");

    public static readonly BexioEndpoint InvoiceById = new(
        "InvoiceById", "GET", "/2.0/kb_invoice/{id}",
        VerificationStatus.CorroboratedBySecondarySources,
        "Read back a created invoice; used for reconciliation.");

    public static IReadOnlyList<BexioEndpoint> All =>
    [
        CompanyProfile, Taxes, Accounts, Currencies,
        ContactList, ContactSearch, ContactById, ContactCreate,
        ArticleList, ArticleSearch, InvoiceCreate, InvoiceById,
    ];

    /// <summary>Substitutes <c>{id}</c> in a path template, URL-encoding the value.</summary>
    public static string Path(BexioEndpoint endpoint, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return id is null
            ? endpoint.PathTemplate
            : endpoint.PathTemplate.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);
    }
}

/// <summary>
/// The OAuth scopes we request, each with the reason it is needed (§5 requires minimum necessary
/// scopes with a documented justification). Note the deliberate omission of any delete or accounting
/// write scope: this application creates invoices and reads reference data, nothing more.
/// </summary>
public sealed record BexioScope(string Value, VerificationStatus Verification, string Justification);

public static class BexioScopes
{
    public static readonly BexioScope OpenId = new(
        "openid", VerificationStatus.CorroboratedBySecondarySources,
        "Required by the OpenID Connect authorization-code flow to receive an ID token.");

    public static readonly BexioScope Profile = new(
        "profile", VerificationStatus.CorroboratedBySecondarySources,
        "Identifies the connecting user so the connection can be attributed in the audit log.");

    public static readonly BexioScope OfflineAccess = new(
        "offline_access", VerificationStatus.CorroboratedBySecondarySources,
        "Required to obtain a refresh token. Without it the connection dies at first token expiry and " +
        "the background sync worker cannot operate unattended.");

    public static readonly BexioScope CompanyProfile = new(
        "company_profile", VerificationStatus.CorroboratedBySecondarySources,
        "Reads the connected company so the UI can show which Bexio organisation is targeted, and so a " +
        "misdirected connection is visible before anything is posted.");

    public static readonly BexioScope ContactEdit = new(
        "contact_edit", VerificationStatus.CorroboratedBySecondarySources,
        "Reads contacts for customer mapping and creates a contact when an operator explicitly requests " +
        "it. Secondary sources indicate a write scope implies its read scope, so contact_show is not " +
        "requested separately.");

    public static readonly BexioScope ArticleShow = new(
        "article_show", VerificationStatus.CorroboratedBySecondarySources,
        "Reads articles for product mapping. Read-only: this application never creates Bexio articles.");

    public static readonly BexioScope KbInvoiceEdit = new(
        "kb_invoice_edit", VerificationStatus.CorroboratedBySecondarySources,
        "Creates the approved invoice. This is the single write capability the system exercises against " +
        "Bexio, and it is reachable only from the outbox worker after explicit human approval.");

    public static readonly BexioScope Accounting = new(
        "accounting", VerificationStatus.UnverifiedAssumption,
        "Believed to be required to read the chart of accounts and the tax table. If the sandbox rejects " +
        "it, remove it from configuration — account and tax discovery then degrade to a preflight error " +
        "rather than a silent wrong booking.");

    /// <summary>The default requested set.</summary>
    public static readonly IReadOnlyList<BexioScope> Default =
    [
        OpenId, Profile, OfflineAccess, CompanyProfile, ContactEdit, ArticleShow, KbInvoiceEdit, Accounting,
    ];

    public static string ToScopeString(IEnumerable<BexioScope> scopes) =>
        string.Join(' ', scopes.Select(s => s.Value));
}
