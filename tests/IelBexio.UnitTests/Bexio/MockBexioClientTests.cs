using IelBexio.Application.Bexio;
using IelBexio.Connectors.Bexio.Configuration;
using IelBexio.Connectors.Bexio.Mock;
using IelBexio.Domain.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IelBexio.UnitTests.Bexio;

/// <summary>
/// The mock is the default Bexio implementation, so its behaviour is load-bearing for every other test
/// and for the demo. It must behave like an API that says no, not like one that says yes to everything.
/// </summary>
public sealed class MockBexioClientTests
{
    private static MockBexioClient Create(MockFailureScenario scenario = MockFailureScenario.None, int failOnCall = 0) =>
        new(Options.Create(new MockBexioOptions { FailureScenario = scenario, FailOnCallNumber = failOnCall }),
            NullLogger<MockBexioClient>.Instance);

    private static BexioInvoiceRequest ReferenceRequest(string contactId = "mock-contact-1") => new(
        ContactId: contactId,
        InvoiceDate: new DateOnly(2026, 3, 1),
        DueDate: new DateOnly(2026, 3, 31),
        CurrencyCode: "CHF",
        CurrencyId: null,
        Title: "INV-10001",
        ReferenceNumber: "INV-10001",
        Positions:
        [
            new BexioInvoicePositionRequest("Alpha Widget", 4m, 250m, "mock-tax-std-81", "mock-acct-3200"),
        ]);

    [Fact]
    public async Task Reference_data_is_discoverable_and_carries_the_active_flag()
    {
        var client = Create();

        var taxes = await client.GetTaxesAsync();
        var accounts = await client.GetAccountsAsync();

        taxes.Should().NotBeEmpty();
        taxes.Should().Contain(t => t.RatePercent == 8.1m && t.IsActive);
        taxes.Should().Contain(t => !t.IsActive, "an inactive tax must exist so the inactive-tax path is testable");
        accounts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Mock_tax_ids_are_obviously_synthetic_so_they_cannot_be_mistaken_for_real_bexio_ids()
    {
        var taxes = await Create().GetTaxesAsync();

        taxes.Should().OnlyContain(t => t.Id.StartsWith("mock-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invoice_is_created_with_computed_totals()
    {
        var client = Create();

        var result = await client.CreateInvoiceAsync(ReferenceRequest(), "key-1");

        result.Id.Should().NotBeNullOrWhiteSpace();
        result.TotalNet.Should().Be(1000m);
        result.TotalTax.Should().Be(81m);
        result.TotalGross.Should().Be(1081m);
        client.CreatedInvoiceCount.Should().Be(1);
    }

    [Fact]
    public async Task The_same_idempotency_key_returns_the_original_invoice_and_creates_nothing_new()
    {
        var client = Create();

        var first = await client.CreateInvoiceAsync(ReferenceRequest(), "idem-key");
        var second = await client.CreateInvoiceAsync(ReferenceRequest(), "idem-key");

        second.Id.Should().Be(first.Id);
        client.CreatedInvoiceCount.Should().Be(1, "a replayed request must not produce a second invoice");
    }

    [Fact]
    public async Task A_different_idempotency_key_does_create_a_second_invoice()
    {
        var client = Create();

        await client.CreateInvoiceAsync(ReferenceRequest(), "key-a");
        await client.CreateInvoiceAsync(ReferenceRequest(), "key-b");

        client.CreatedInvoiceCount.Should().Be(2);
    }

    [Fact]
    public async Task An_unknown_contact_is_rejected_as_a_validation_failure()
    {
        var act = async () => await Create().CreateInvoiceAsync(ReferenceRequest("does-not-exist"), "k");

        var ex = await act.Should().ThrowAsync<BexioApiException>();
        ex.Which.Category.Should().Be(SyncErrorCategory.Validation);
        ex.Which.IsRetryable.Should().BeFalse("retrying an invalid payload forever is exactly what §20 forbids");
    }

    [Fact]
    public async Task An_unknown_tax_id_is_rejected_rather_than_silently_accepted()
    {
        var request = ReferenceRequest() with
        {
            Positions = [new BexioInvoicePositionRequest("Widget", 1m, 100m, "tax-that-does-not-exist", "mock-acct-3200")],
        };

        var act = async () => await Create().CreateInvoiceAsync(request, "k");

        (await act.Should().ThrowAsync<BexioApiException>()).Which.Category.Should().Be(SyncErrorCategory.Validation);
    }

    [Fact]
    public async Task An_inactive_tax_is_rejected()
    {
        var request = ReferenceRequest() with
        {
            Positions = [new BexioInvoicePositionRequest("Widget", 1m, 100m, "mock-tax-std-77", "mock-acct-3200")],
        };

        var act = async () => await Create().CreateInvoiceAsync(request, "k");

        (await act.Should().ThrowAsync<BexioApiException>()).Which.Message.Should().Contain("not active");
    }

    [Fact]
    public async Task An_unconfigured_currency_is_rejected()
    {
        var request = ReferenceRequest() with { CurrencyCode = "XYZ" };

        var act = async () => await Create().CreateInvoiceAsync(request, "k");

        (await act.Should().ThrowAsync<BexioApiException>()).Which.Category.Should().Be(SyncErrorCategory.Validation);
    }

    [Fact]
    public async Task An_invoice_with_no_positions_is_rejected()
    {
        var request = ReferenceRequest() with { Positions = [] };

        var act = async () => await Create().CreateInvoiceAsync(request, "k");

        (await act.Should().ThrowAsync<BexioApiException>()).Which.Category.Should().Be(SyncErrorCategory.Validation);
    }

    [Theory]
    [InlineData(MockFailureScenario.AuthenticationFailure, SyncErrorCategory.Authentication, true)]
    [InlineData(MockFailureScenario.AuthorizationFailure, SyncErrorCategory.Authorization, false)]
    [InlineData(MockFailureScenario.ValidationFailure, SyncErrorCategory.Validation, false)]
    [InlineData(MockFailureScenario.RateLimited, SyncErrorCategory.RateLimit, true)]
    [InlineData(MockFailureScenario.Timeout, SyncErrorCategory.Network, true)]
    [InlineData(MockFailureScenario.Conflict, SyncErrorCategory.Conflict, false)]
    [InlineData(MockFailureScenario.ServerError, SyncErrorCategory.Transient, true)]
    [InlineData(MockFailureScenario.NetworkFailure, SyncErrorCategory.Network, true)]
    [InlineData(MockFailureScenario.NotFound, SyncErrorCategory.NotFound, false)]
    public async Task Each_failure_scenario_maps_to_its_category_and_retry_policy(
        MockFailureScenario scenario, SyncErrorCategory expectedCategory, bool expectedRetryable)
    {
        var act = async () => await Create(scenario).GetTaxesAsync();

        var ex = await act.Should().ThrowAsync<BexioApiException>();
        ex.Which.Category.Should().Be(expectedCategory);
        ex.Which.IsRetryable.Should().Be(expectedRetryable);
    }

    [Fact]
    public async Task A_rate_limit_failure_carries_a_retry_after_hint()
    {
        var act = async () => await Create(MockFailureScenario.RateLimited).GetTaxesAsync();

        (await act.Should().ThrowAsync<BexioApiException>()).Which.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task A_failure_can_be_scheduled_for_a_specific_call_so_retry_then_succeed_is_testable()
    {
        var client = Create(MockFailureScenario.ServerError, failOnCall: 1);

        var first = async () => await client.GetTaxesAsync();
        await first.Should().ThrowAsync<BexioApiException>();

        var second = await client.GetTaxesAsync();
        second.Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_already_created_invoice_is_returned_even_when_a_failure_is_injected()
    {
        // A correct idempotent API returns the prior result rather than re-running the operation, so
        // the idempotency check must come before any failure simulation.
        var options = new MockBexioOptions();
        var client = new MockBexioClient(Options.Create(options), NullLogger<MockBexioClient>.Instance);

        var created = await client.CreateInvoiceAsync(ReferenceRequest(), "stable-key");

        options.FailureScenario = MockFailureScenario.ServerError;
        var replayed = await client.CreateInvoiceAsync(ReferenceRequest(), "stable-key");

        replayed.Id.Should().Be(created.Id);
        client.CreatedInvoiceCount.Should().Be(1);
    }

    [Fact]
    public void The_mode_name_makes_it_unmistakable_that_no_real_bexio_was_contacted()
    {
        Create().ModeName.Should().Contain("Mock");
    }
}
