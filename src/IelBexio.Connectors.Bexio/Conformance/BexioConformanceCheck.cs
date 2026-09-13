using System.Diagnostics;
using System.Text.Json;
using IelBexio.Application.Bexio;
using IelBexio.Connectors.Bexio.Configuration;
using IelBexio.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace IelBexio.Connectors.Bexio.Conformance;

/// <summary>What a single conformance probe concluded about one assumption.</summary>
public enum ConformanceOutcome
{
    /// <summary>The assumption held. Its <see cref="VerificationStatus"/> can be promoted.</summary>
    Confirmed = 0,

    /// <summary>The assumption is wrong. The report says how.</summary>
    Refuted = 1,

    /// <summary>Could not be determined — usually a missing scope or an empty account.</summary>
    Inconclusive = 2,

    /// <summary>Not attempted, because a prerequisite probe failed.</summary>
    Skipped = 3,
}

public sealed record ConformanceProbe(
    string Name,
    string Assumption,
    VerificationStatus DeclaredStatus,
    ConformanceOutcome Outcome,
    string Detail,
    int ElapsedMs,
    string? Evidence = null);

public sealed record ConformanceReport(
    DateTimeOffset RunAt,
    string ApiBaseUrl,
    IReadOnlyList<string> GrantedScopes,
    IReadOnlyList<ConformanceProbe> Probes,
    string ClientMode,
    bool AgainstLiveApi)
{
    public int Confirmed => Probes.Count(p => p.Outcome == ConformanceOutcome.Confirmed);
    public int Refuted => Probes.Count(p => p.Outcome == ConformanceOutcome.Refuted);
    public int Inconclusive => Probes.Count(p => p.Outcome == ConformanceOutcome.Inconclusive);
    public int Skipped => Probes.Count(p => p.Outcome == ConformanceOutcome.Skipped);

    /// <summary>True when nothing was refuted. Inconclusive results are not failures.</summary>
    public bool Passed => Refuted == 0;
}

/// <summary>
/// Runs every assumption in <see cref="BexioEndpoints"/> and <see cref="BexioScopes"/> against a real
/// Bexio account and reports which ones hold.
/// <para>
/// <b>Why this exists.</b> The Bexio adapter was written without access to Bexio's documentation or
/// API, so every path, field name and scope carries an explicit <see cref="VerificationStatus"/>
/// saying so. Those markers are only useful if converting them into facts is easy — otherwise the
/// verification never happens and the markers become decoration.
/// </para>
/// <para>
/// This turns that verification into one command. Point it at a sandbox, and it reports exactly which
/// assumptions are confirmed, which are refuted and how, and which could not be determined. The
/// refuted ones are the work list; the confirmed ones can have their marker promoted.
/// </para>
/// <para>
/// <b>It is read-only by default.</b> Creating an invoice in someone's accounting system as a side
/// effect of a diagnostic would be indefensible, so the write probe is opt-in
/// (<see cref="BexioConformanceOptions.AllowWriteProbe"/>) and is the only probe that mutates anything.
/// </para>
/// </summary>
public sealed class BexioConformanceCheck
{
    private readonly IBexioClient _client;
    private readonly IBexioTokenProvider _tokens;
    private readonly BexioOptions _options;
    private readonly ILogger<BexioConformanceCheck> _logger;

    public BexioConformanceCheck(
        IBexioClient client,
        IBexioTokenProvider tokens,
        BexioOptions options,
        ILogger<BexioConformanceCheck> logger)
    {
        _client = client;
        _tokens = tokens;
        _options = options;
        _logger = logger;
    }

    public async Task<ConformanceReport> RunAsync(BexioConformanceOptions settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var probes = new List<ConformanceProbe>();
        IReadOnlyList<string> grantedScopes = [];

        // ---- Prerequisite: are we connected at all? ------------------------------------------------
        var connected = await ProbeAsync(
            probes,
            "Connection",
            "An authorised OAuth connection exists and a token can be obtained.",
            VerificationStatus.StatedInProjectSpecification,
            async () =>
            {
                // The mock needs no OAuth connection. Running the harness against it is how we know the
                // harness itself works before any credentials exist — so a client that declares it needs
                // no connection is not a failure, it is a different mode.
                if (!_client.RequiresAuthorizedConnection)
                {
                    return (ConformanceOutcome.Inconclusive,
                        $"Running against '{_client.ModeName}', which needs no OAuth connection. The probes below " +
                        "exercise the harness, not the real Bexio API. Re-run with Bexio:Mode=Api against a sandbox " +
                        "to verify the actual assumptions.", null);
                }

                if (!await _tokens.IsConnectedAsync(cancellationToken))
                {
                    return (ConformanceOutcome.Refuted, "No Bexio connection is established for this tenant.", null);
                }

                grantedScopes = await _tokens.GetGrantedScopesAsync(cancellationToken);
                return (ConformanceOutcome.Confirmed, $"Connected. {grantedScopes.Count} scope(s) granted.", string.Join(' ', grantedScopes));
            });

        // Proceed when either we are genuinely connected, or the active client needs no connection.
        if (!connected && _client.RequiresAuthorizedConnection)
        {
            // Everything downstream needs a token. Reporting twenty identical failures helps nobody.
            foreach (var endpoint in BexioEndpoints.All)
            {
                probes.Add(new ConformanceProbe(
                    endpoint.Name, endpoint.PathTemplate, endpoint.Verification,
                    ConformanceOutcome.Skipped, "Skipped: no Bexio connection.", 0));
            }

            return new ConformanceReport(DateTimeOffset.UtcNow, _options.ApiBaseUrl, grantedScopes, probes, _client.ModeName, _client.RequiresAuthorizedConnection);
        }

        // ---- Scopes ---------------------------------------------------------------------------------
        // Only meaningful against a client that actually authenticates.
        foreach (var scope in _client.RequiresAuthorizedConnection
                     ? BexioScopes.Default.Where(s => _options.Scopes.Contains(s.Value, StringComparer.Ordinal))
                     : [])
        {
            var granted = grantedScopes.Contains(scope.Value, StringComparer.Ordinal);

            probes.Add(new ConformanceProbe(
                $"Scope: {scope.Value}",
                scope.Justification,
                scope.Verification,
                granted ? ConformanceOutcome.Confirmed : ConformanceOutcome.Refuted,
                granted
                    ? "Requested and granted."
                    : "Requested but NOT granted. Either the scope string is wrong or the application is not approved for it.",
                0));
        }

        // ---- Read endpoints --------------------------------------------------------------------------
        await ProbeAsync(probes, BexioEndpoints.CompanyProfile.Name, BexioEndpoints.CompanyProfile.PathTemplate,
            BexioEndpoints.CompanyProfile.Verification, async () =>
            {
                var company = await _client.GetCompanyInfoAsync(cancellationToken);
                return company.Name is null
                    ? (ConformanceOutcome.Inconclusive, "The endpoint responded but no company name was parsed — the field name may differ.", null)
                    : (ConformanceOutcome.Confirmed, $"Company '{company.Name}' ({company.CountryCode}, {company.DefaultCurrency}).", company.CompanyId);
            });

        var taxes = Array.Empty<BexioTax>();
        await ProbeAsync(probes, BexioEndpoints.Taxes.Name, BexioEndpoints.Taxes.PathTemplate,
            BexioEndpoints.Taxes.Verification, async () =>
            {
                var result = await _client.GetTaxesAsync(cancellationToken);
                taxes = result.ToArray();

                if (result.Count == 0)
                {
                    return (ConformanceOutcome.Inconclusive, "The endpoint responded but returned no taxes.", null);
                }

                // A parsed rate of zero across every tax almost certainly means the rate field name is
                // wrong rather than that the account has only zero-rated taxes.
                var withRate = result.Count(t => t.RatePercent > 0m);
                var evidence = string.Join(", ", result.Take(5).Select(t => $"{t.Id}={t.Name} {t.RatePercent}%{(t.IsActive ? "" : " (inactive)")}"));

                return withRate == 0
                    ? (ConformanceOutcome.Refuted,
                       $"{result.Count} tax(es) returned but every parsed rate is 0. The rate field name is probably wrong " +
                       "(the DTO tries 'value' then 'percentage').", evidence)
                    : (ConformanceOutcome.Confirmed, $"{result.Count} tax(es), {withRate} with a non-zero rate.", evidence);
            });

        await ProbeAsync(probes, BexioEndpoints.Accounts.Name, BexioEndpoints.Accounts.PathTemplate,
            BexioEndpoints.Accounts.Verification, async () =>
            {
                var accounts = await _client.GetAccountsAsync(cancellationToken);
                if (accounts.Count == 0)
                {
                    return (ConformanceOutcome.Inconclusive, "The endpoint responded but returned no accounts.", null);
                }

                var revenue = accounts.Count(a => string.Equals(a.AccountType, "revenue", StringComparison.OrdinalIgnoreCase));
                var evidence = string.Join(", ", accounts.Take(5).Select(a => $"{a.AccountNumber} {a.Name} [{a.AccountType}]"));

                return revenue == 0
                    ? (ConformanceOutcome.Inconclusive,
                       $"{accounts.Count} account(s) returned, but none parsed as type 'revenue'. Automatic account mapping " +
                       "relies on that value — check what account_type actually contains.", evidence)
                    : (ConformanceOutcome.Confirmed, $"{accounts.Count} account(s), {revenue} revenue.", evidence);
            });

        await ProbeAsync(probes, BexioEndpoints.Currencies.Name, BexioEndpoints.Currencies.PathTemplate,
            BexioEndpoints.Currencies.Verification, async () =>
            {
                var currencies = await _client.GetCurrenciesAsync(cancellationToken);

                // The client swallows 404/403 here by design and returns an empty list, so an empty
                // result is genuinely ambiguous rather than a failure.
                return currencies.Count == 0
                    ? (ConformanceOutcome.Inconclusive,
                       "No currencies returned. This endpoint is the lowest-confidence entry in the catalog; " +
                       "the client degrades currency validation to a warning when it is unavailable.", null)
                    : (ConformanceOutcome.Confirmed, $"{currencies.Count} currency/currencies.",
                       string.Join(", ", currencies.Take(5).Select(c => c.Code)));
            });

        await ProbeAsync(probes, BexioEndpoints.ContactList.Name, BexioEndpoints.ContactList.PathTemplate,
            BexioEndpoints.ContactList.Verification, async () =>
            {
                var contacts = await _client.SearchContactsAsync(null, cancellationToken);
                if (contacts.Count == 0)
                {
                    return (ConformanceOutcome.Inconclusive, "The endpoint responded but the account has no contacts.", null);
                }

                var named = contacts.Count(c => !string.IsNullOrWhiteSpace(c.Name));
                var evidence = string.Join(", ", contacts.Take(3).Select(c => $"{c.Id}={c.Name}"));

                return named == 0
                    ? (ConformanceOutcome.Refuted,
                       $"{contacts.Count} contact(s) returned but none has a parsed name — the DTO reads 'name_1'.", evidence)
                    : (ConformanceOutcome.Confirmed, $"{contacts.Count} contact(s).", evidence);
            });

        await ProbeAsync(probes, BexioEndpoints.ContactSearch.Name, BexioEndpoints.ContactSearch.PathTemplate,
            BexioEndpoints.ContactSearch.Verification, async () =>
            {
                var all = await _client.SearchContactsAsync(null, cancellationToken);
                if (all.Count == 0)
                {
                    return (ConformanceOutcome.Skipped, "No contacts exist to search for.", null);
                }

                var fragment = (all[0].Name ?? string.Empty).Split(' ').FirstOrDefault();
                if (string.IsNullOrWhiteSpace(fragment))
                {
                    return (ConformanceOutcome.Inconclusive, "No usable name fragment to search with.", null);
                }

                var found = await _client.SearchContactsAsync(fragment, cancellationToken);

                return found.Count == 0
                    ? (ConformanceOutcome.Refuted,
                       $"Searching for '{fragment}' returned nothing, though a contact with that name exists. " +
                       "The search criteria shape (field/value/criteria) is probably wrong.", null)
                    : (ConformanceOutcome.Confirmed, $"Search for '{fragment}' returned {found.Count}.", null);
            });

        await ProbeAsync(probes, BexioEndpoints.ArticleList.Name, BexioEndpoints.ArticleList.PathTemplate,
            BexioEndpoints.ArticleList.Verification, async () =>
            {
                var articles = await _client.SearchArticlesAsync(null, cancellationToken);
                return articles.Count == 0
                    ? (ConformanceOutcome.Inconclusive, "The endpoint responded but the account has no articles.", null)
                    : (ConformanceOutcome.Confirmed, $"{articles.Count} article(s).",
                       string.Join(", ", articles.Take(3).Select(a => $"{a.Id}={a.Code}/{a.Name}")));
            });

        // ---- The write probe, opt-in only ------------------------------------------------------------
        if (!settings.AllowWriteProbe)
        {
            probes.Add(new ConformanceProbe(
                BexioEndpoints.InvoiceCreate.Name, BexioEndpoints.InvoiceCreate.PathTemplate,
                BexioEndpoints.InvoiceCreate.Verification, ConformanceOutcome.Skipped,
                "Skipped: the write probe is opt-in. Re-run with --allow-write against a SANDBOX to verify " +
                "invoice creation. It creates one real invoice.", 0));

            probes.Add(new ConformanceProbe(
                BexioEndpoints.InvoiceById.Name, BexioEndpoints.InvoiceById.PathTemplate,
                BexioEndpoints.InvoiceById.Verification, ConformanceOutcome.Skipped,
                "Skipped: depends on the write probe.", 0));

            return new ConformanceReport(DateTimeOffset.UtcNow, _options.ApiBaseUrl, grantedScopes, probes, _client.ModeName, _client.RequiresAuthorizedConnection);
        }

        string? createdInvoiceId = null;

        await ProbeAsync(probes, BexioEndpoints.InvoiceCreate.Name, BexioEndpoints.InvoiceCreate.PathTemplate,
            BexioEndpoints.InvoiceCreate.Verification, async () =>
            {
                var contacts = await _client.SearchContactsAsync(null, cancellationToken);
                if (contacts.Count == 0)
                {
                    return (ConformanceOutcome.Skipped, "No contact exists to invoice.", null);
                }

                var activeTax = taxes.FirstOrDefault(t => t.IsActive && t.RatePercent > 0m);
                if (activeTax is null)
                {
                    return (ConformanceOutcome.Skipped, "No active non-zero sales tax to use.", null);
                }

                var request = new BexioInvoiceRequest(
                    ContactId: contacts[0].Id,
                    InvoiceDate: DateOnly.FromDateTime(DateTime.UtcNow),
                    DueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                    CurrencyCode: settings.WriteProbeCurrency,
                    CurrencyId: null,
                    Title: settings.WriteProbeReference,
                    ReferenceNumber: settings.WriteProbeReference,
                    Positions:
                    [
                        new BexioInvoicePositionRequest(
                            Text: "Conformance check — safe to delete",
                            Amount: 1m,
                            UnitPrice: 1m,
                            TaxId: activeTax.Id,
                            AccountId: null),
                    ]);

                var created = await _client.CreateInvoiceAsync(request, $"conformance-{Guid.CreateVersion7():n}", cancellationToken);
                createdInvoiceId = created.Id;

                return (ConformanceOutcome.Confirmed,
                    $"Created invoice {created.Id} ({created.DocumentNumber}), gross {created.TotalGross} {created.CurrencyCode}. " +
                    "DELETE THIS INVOICE — it exists only to verify the endpoint.",
                    created.RawJson);
            });

        await ProbeAsync(probes, BexioEndpoints.InvoiceById.Name, BexioEndpoints.InvoiceById.PathTemplate,
            BexioEndpoints.InvoiceById.Verification, async () =>
            {
                if (createdInvoiceId is null)
                {
                    return (ConformanceOutcome.Skipped, "No invoice was created to read back.", null);
                }

                var read = await _client.GetInvoiceAsync(createdInvoiceId, cancellationToken);
                return read is null
                    ? (ConformanceOutcome.Refuted, $"Invoice {createdInvoiceId} was created but could not be read back.", null)
                    : (ConformanceOutcome.Confirmed, $"Read back invoice {read.Id} ({read.DocumentNumber}).", null);
            });

        return new ConformanceReport(DateTimeOffset.UtcNow, _options.ApiBaseUrl, grantedScopes, probes, _client.ModeName, _client.RequiresAuthorizedConnection);
    }

    /// <summary>Runs one probe, translating an exception into a classified outcome rather than a crash.</summary>
    private async Task<bool> ProbeAsync(
        List<ConformanceProbe> probes,
        string name,
        string assumption,
        VerificationStatus declared,
        Func<Task<(ConformanceOutcome Outcome, string Detail, string? Evidence)>> probe)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var (outcome, detail, evidence) = await probe();
            stopwatch.Stop();

            probes.Add(new ConformanceProbe(name, assumption, declared, outcome, detail, (int)stopwatch.ElapsedMilliseconds, Truncate(evidence)));
            _logger.LogInformation("Conformance probe {Name}: {Outcome} — {Detail}", name, outcome, detail);

            return outcome == ConformanceOutcome.Confirmed;
        }
        catch (BexioApiException ex)
        {
            stopwatch.Stop();

            // The error class says what kind of wrongness this is, which is most of the diagnosis.
            var (outcome, detail) = ex.Category switch
            {
                SyncErrorCategory.NotFound =>
                    (ConformanceOutcome.Refuted, $"404 — the path '{assumption}' does not exist on this API."),
                SyncErrorCategory.Authorization =>
                    (ConformanceOutcome.Inconclusive, $"403 — the granted scopes do not permit this. The path may still be correct. ({ex.Message})"),
                SyncErrorCategory.Authentication =>
                    (ConformanceOutcome.Inconclusive, $"401 — authentication failed. ({ex.Message})"),
                SyncErrorCategory.Permanent when ex.Code == "RESPONSE_SHAPE" =>
                    (ConformanceOutcome.Refuted, $"The endpoint exists but the response shape does not match our DTO. {ex.Message}"),
                SyncErrorCategory.Validation =>
                    (ConformanceOutcome.Refuted, $"The request was rejected as invalid — the payload shape is probably wrong. ({ex.Message})"),
                _ => (ConformanceOutcome.Inconclusive, $"{ex.Category}: {ex.Message}"),
            };

            probes.Add(new ConformanceProbe(name, assumption, declared, outcome, detail, (int)stopwatch.ElapsedMilliseconds));
            _logger.LogWarning("Conformance probe {Name}: {Outcome} — {Detail}", name, outcome, detail);

            return false;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            probes.Add(new ConformanceProbe(name, assumption, declared, ConformanceOutcome.Inconclusive,
                $"Unexpected {ex.GetType().Name}: {ex.Message}", (int)stopwatch.ElapsedMilliseconds));

            return false;
        }
    }

    private static string? Truncate(string? value) =>
        value is null ? null : value.Length <= 400 ? value : value[..400] + "…";
}

public sealed class BexioConformanceOptions
{
    /// <summary>
    /// Whether to attempt invoice creation. Off by default: a diagnostic must not write to someone's
    /// accounting system unless they ask it to.
    /// </summary>
    public bool AllowWriteProbe { get; set; }

    public string WriteProbeCurrency { get; set; } = "CHF";

    /// <summary>Reference put on the probe invoice, so it is obvious what it is and that it can be deleted.</summary>
    public string WriteProbeReference { get; set; } = "CONFORMANCE-CHECK-DELETE-ME";
}
