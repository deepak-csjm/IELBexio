using IelBexio.Application.Bexio;
using IelBexio.Connectors.Bexio.Conformance;
using IelBexio.Connectors.Bexio.Configuration;
using IelBexio.Connectors.Bexio.Mock;
using IelBexio.Domain.Sync;
using IelBexio.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IelBexio.UnitTests.Bexio;

/// <summary>
/// The conformance harness is the tool that converts the adapter's unverified assumptions into facts
/// once a Bexio licence exists. It is therefore worth testing that it actually detects the failures it
/// claims to — a diagnostic that reports success regardless is worse than none.
/// </summary>
public sealed class BexioConformanceCheckTests
{
    private static BexioConformanceCheck Create(IBexioClient client, IBexioTokenProvider? tokens = null) =>
        new(client,
            tokens ?? new StubTokenProvider(connected: true, scopes: ["openid", "kb_invoice_edit"]),
            new BexioOptions(),
            NullLogger<BexioConformanceCheck>.Instance);

    private static MockBexioClient Mock(MockFailureScenario scenario = MockFailureScenario.None) =>
        new(Options.Create(new MockBexioOptions { FailureScenario = scenario }), NullLogger<MockBexioClient>.Instance);

    [Fact]
    public async Task Against_the_mock_every_read_probe_runs_and_nothing_is_refuted()
    {
        var report = await Create(Mock()).RunAsync(new BexioConformanceOptions());

        report.Passed.Should().BeTrue();
        report.Confirmed.Should().BeGreaterThan(5);
        report.AgainstLiveApi.Should().BeFalse();
        report.ClientMode.Should().Contain("Mock");
    }

    [Fact]
    public async Task A_mock_run_is_never_presented_as_grounds_to_promote_a_marker()
    {
        // The whole point of the VerificationStatus markers is that they only move on real evidence.
        var report = await Create(Mock()).RunAsync(new BexioConformanceOptions());
        var markdown = ConformanceReportWriter.ToMarkdown(report);

        markdown.Should().Contain("did not contact Bexio");
        markdown.Should().Contain("NOT promotable");
        markdown.Should().NotContain("observed working against a real Bexio account");
    }

    [Fact]
    public async Task The_write_probe_is_skipped_unless_explicitly_allowed()
    {
        var client = Mock();

        var report = await Create(client).RunAsync(new BexioConformanceOptions { AllowWriteProbe = false });

        report.Probes.Single(p => p.Name == "InvoiceCreate").Outcome.Should().Be(ConformanceOutcome.Skipped);
        client.CreatedInvoiceCount.Should().Be(0, "a diagnostic must not write to an accounting system by default");
    }

    [Fact]
    public async Task The_write_probe_creates_exactly_one_clearly_labelled_invoice_when_allowed()
    {
        var client = Mock();

        var report = await Create(client).RunAsync(new BexioConformanceOptions { AllowWriteProbe = true });

        report.Probes.Single(p => p.Name == "InvoiceCreate").Outcome.Should().Be(ConformanceOutcome.Confirmed);
        report.Probes.Single(p => p.Name == "InvoiceById").Outcome.Should().Be(ConformanceOutcome.Confirmed);
        client.CreatedInvoiceCount.Should().Be(1);

        report.Probes.Single(p => p.Name == "InvoiceCreate").Detail
            .Should().Contain("DELETE THIS INVOICE");
    }

    [Fact]
    public async Task A_missing_connection_on_a_live_client_refutes_and_skips_the_rest()
    {
        var report = await Create(
                new LiveLikeClient(),
                new StubTokenProvider(connected: false, scopes: []))
            .RunAsync(new BexioConformanceOptions());

        report.Passed.Should().BeFalse();
        report.Probes[0].Name.Should().Be("Connection");
        report.Probes[0].Outcome.Should().Be(ConformanceOutcome.Refuted);

        // Twenty identical downstream failures would bury the actual cause.
        report.Probes.Skip(1).Should().OnlyContain(p => p.Outcome == ConformanceOutcome.Skipped);
    }

    [Fact]
    public async Task A_requested_but_ungranted_scope_is_refuted()
    {
        var options = new BexioOptions();
        var check = new BexioConformanceCheck(
            new LiveLikeClient(),
            new StubTokenProvider(connected: true, scopes: ["openid"]),
            options,
            NullLogger<BexioConformanceCheck>.Instance);

        var report = await check.RunAsync(new BexioConformanceOptions());

        var invoiceScope = report.Probes.Single(p => p.Name == "Scope: kb_invoice_edit");
        invoiceScope.Outcome.Should().Be(ConformanceOutcome.Refuted);
        invoiceScope.Detail.Should().Contain("NOT granted");
    }

    [Fact]
    public async Task A_404_is_reported_as_the_path_being_wrong_rather_than_as_a_generic_error()
    {
        var report = await Create(Mock(MockFailureScenario.NotFound)).RunAsync(new BexioConformanceOptions());

        var taxes = report.Probes.Single(p => p.Name == "Taxes");
        taxes.Outcome.Should().Be(ConformanceOutcome.Refuted);
        taxes.Detail.Should().Contain("does not exist");
    }

    [Fact]
    public async Task A_403_is_inconclusive_rather_than_refuted_because_the_path_may_still_be_right()
    {
        var report = await Create(Mock(MockFailureScenario.AuthorizationFailure)).RunAsync(new BexioConformanceOptions());

        var taxes = report.Probes.Single(p => p.Name == "Taxes");
        taxes.Outcome.Should().Be(ConformanceOutcome.Inconclusive);
        taxes.Detail.Should().Contain("scopes");
    }

    [Fact]
    public async Task Taxes_that_all_parse_as_zero_rated_are_refuted_as_a_probable_field_name_error()
    {
        // The failure this catches: the endpoint responds, the DTO parses, and every rate silently
        // becomes 0 because the rate field is called something else. Nothing else would notice.
        var report = await Create(new ZeroRateTaxClient()).RunAsync(new BexioConformanceOptions());

        var taxes = report.Probes.Single(p => p.Name == "Taxes");
        taxes.Outcome.Should().Be(ConformanceOutcome.Refuted);
        taxes.Detail.Should().Contain("rate field name");
    }

    [Fact]
    public async Task Every_probe_names_the_file_to_change()
    {
        var report = await Create(new ZeroRateTaxClient()).RunAsync(new BexioConformanceOptions());
        var markdown = ConformanceReportWriter.ToMarkdown(report);

        markdown.Should().Contain("Fix in:");
        markdown.Should().Contain("BexioEndpoints");
    }

    [Fact]
    public async Task The_report_serialises_to_json_for_machine_consumption()
    {
        var report = await Create(Mock()).RunAsync(new BexioConformanceOptions());

        var json = ConformanceReportWriter.ToJson(report);
        var parsed = System.Text.Json.JsonDocument.Parse(json);

        parsed.RootElement.GetProperty("refuted").GetInt32().Should().Be(0);
        parsed.RootElement.GetProperty("probes").GetArrayLength().Should().BeGreaterThan(5);
    }

    // ---- Doubles --------------------------------------------------------------------------------

    private sealed class StubTokenProvider : IBexioTokenProvider
    {
        private readonly bool _connected;
        private readonly IReadOnlyList<string> _scopes;

        public StubTokenProvider(bool connected, IReadOnlyList<string> scopes)
        {
            _connected = connected;
            _scopes = scopes;
        }

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("token");
        public Task<IReadOnlyList<string>> GetGrantedScopesAsync(CancellationToken cancellationToken = default) => Task.FromResult(_scopes);
        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default) => Task.FromResult(_connected);
    }

    /// <summary>A client that claims to need an authorised connection, so the live code path is exercised.</summary>
    private class LiveLikeClient : IBexioClient
    {
        public string ModeName => "Bexio API (test double)";
        public bool RequiresAuthorizedConnection => true;

        public virtual Task<IReadOnlyList<BexioTax>> GetTaxesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BexioTax>>([new BexioTax("1", "MWST", 8.1m, true, null, null)]);

        public Task<BexioCompanyInfo> GetCompanyInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BexioCompanyInfo("1", "Test AG", "CH", "CHF"));

        public Task<IReadOnlyList<BexioAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BexioAccount>>([new BexioAccount("1", "3200", "Ertrag", true, "revenue")]);

        public Task<IReadOnlyList<BexioCurrency>> GetCurrenciesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BexioCurrency>>([new BexioCurrency("1", "CHF", true)]);

        public Task<IReadOnlyList<BexioContact>> SearchContactsAsync(string? nameFragment, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BexioContact>>([new BexioContact("1", "Test AG", null, null, "CH", null, null, null, null)]);

        public Task<BexioContact?> GetContactAsync(string contactId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BexioContact?>(null);

        public Task<BexioContact> CreateContactAsync(BexioContactRequest request, CancellationToken cancellationToken = default) =>
            throw new BexioApiException(SyncErrorCategory.Permanent, null, "not used");

        public Task<IReadOnlyList<BexioArticle>> SearchArticlesAsync(string? codeOrName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BexioArticle>>([new BexioArticle("1", "SKU", "Article", 10m, "1", true)]);

        public Task<BexioInvoiceResult> CreateInvoiceAsync(BexioInvoiceRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BexioInvoiceResult("1", "INV-1", 1.08m, 1m, 0.08m, "CHF", null));

        public Task<BexioInvoiceResult?> GetInvoiceAsync(string invoiceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BexioInvoiceResult?>(new BexioInvoiceResult("1", "INV-1", 1.08m, 1m, 0.08m, "CHF", null));
    }

    /// <summary>Every tax parses with a zero rate — the silent field-name failure.</summary>
    private sealed class ZeroRateTaxClient : LiveLikeClient
    {
        public override Task<IReadOnlyList<BexioTax>> GetTaxesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BexioTax>>(
            [
                new BexioTax("1", "MWST 8.1%", 0m, true, null, null),
                new BexioTax("2", "MWST 2.6%", 0m, true, null, null),
            ]);
    }
}
