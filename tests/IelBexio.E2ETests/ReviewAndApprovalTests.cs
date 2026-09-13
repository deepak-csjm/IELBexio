using System.Net.Http.Json;
using System.Text.Json;
using IelBexio.E2ETests.Support;
using Microsoft.Playwright;

namespace IelBexio.E2ETests;

/// <summary>
/// Drives the real UI in a real browser through the review and approval flow.
/// <para>
/// These exist because every other test in this repository calls services or the HTTP API directly.
/// That proves the logic, but it cannot prove that a reviewer can actually see a validation warning,
/// that the approve button is disabled when pre-flight fails, or that an unauthorised user is stopped
/// by the server rather than merely by a hidden button. Those are the claims a financial review tool
/// lives or dies on, and only a browser can check them.
/// </para>
/// </summary>
[Collection(SerialE2E.Name)]
public sealed class ReviewAndApprovalTests : IAsyncLifetime
{
    // Each test gets its own application and database. Sharing one would be cheaper, but these tests
    // mutate workflow state — an approval in one test would change what another test finds — and a
    // suite whose failures depend on execution order cannot be trusted to report on a financial
    // control. The cost is a few seconds of start-up per test.
    private readonly AppFixture _app = new();

    public Task InitializeAsync() => _app.InitializeAsync();

    public Task DisposeAsync() => _app.DisposeAsync();

    /// <summary>Seeds the tenant with reference data, imported invoices and the mappings pre-flight needs.</summary>
    private async Task<string> SeedAndGetReferenceInvoiceIdAsync()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };

        await client.PostAsync(new Uri("/api/connections/bexio/refresh-reference-data", UriKind.Relative), null);
        await client.PostAsJsonAsync("/api/imports", new { sourceSystem = "Shopify" });

        var invoices = await client.GetFromJsonAsync<JsonElement>("/api/invoices?take=50");

        string? referenceId = null;
        foreach (var invoice in invoices.EnumerateArray())
        {
            var id = invoice.GetProperty("id").GetString()!;
            await client.PostAsync(new Uri($"/api/invoices/{id}/validate", UriKind.Relative), null);

            if (invoice.GetProperty("invoiceNumber").GetString() == "INV-10001")
            {
                referenceId = id;
            }
        }

        referenceId.Should().NotBeNull();

        // The mapping decisions pre-flight refuses to make on a human's behalf.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/invoices/{referenceId}");
        var customerId = detail.GetProperty("customer").GetProperty("id").GetString();

        var contacts = await client.GetFromJsonAsync<JsonElement>("/api/mappings/bexio-reference?kind=Contact");
        var contactId = contacts.EnumerateArray()
            .First(c => (c.GetProperty("name").GetString() ?? string.Empty).Contains("ABC", StringComparison.Ordinal))
            .GetProperty("bexioId").GetString();

        await client.PostAsJsonAsync("/api/mappings/customers", new { customerId, bexioContactId = contactId, label = "ABC Swiss GmbH" });

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/mappings/bexio-reference?kind=Account");
        foreach (var (code, number) in new[] { ("REVENUE_GOODS", "3200"), ("REVENUE_SHIPPING", "3700") })
        {
            var accountId = accounts.EnumerateArray()
                .First(a => a.GetProperty("code").GetString() == number)
                .GetProperty("bexioId").GetString();

            await client.PostAsJsonAsync("/api/mappings/accounts",
                new { internalAccountCode = code, bexioAccountId = accountId, number, name = "mapped by test" });
        }

        return referenceId!;
    }

    [Fact]
    public async Task The_dashboard_renders_real_pipeline_state()
    {
        await SeedAndGetReferenceInvoiceIdAsync();

        await _app.WithPageAsync("the-dashboard-renders-real-pipeline-state", async page =>
        {
            await page.GotoAsync("/");

            await page.WaitForSelectorAsync("text=Dashboard");

            // The mode banner must be unmissable: a reviewer has to know whether this is a real ledger.
            (await page.Locator(".mode-banner").InnerTextAsync()).Should().Contain("MOCK");

            await page.WaitForSelectorAsync("text=Needs review");
            await page.WaitForSelectorAsync("text=Duplicates prevented");
        });
    }

    [Fact]
    public async Task Every_navigation_destination_loads_without_an_error()
    {
        await SeedAndGetReferenceInvoiceIdAsync();

        await _app.WithPageAsync("every-navigation-destination-loads-without-an-error", async page =>
        {

            // Blazor renders server-side errors into the page, so a broken page shows rather than 500s.
            var failures = new List<string>();
            page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    failures.Add(message.Text);
                }
            };

            string[] routes =
            [
                "/", "/connections", "/imports", "/documents", "/invoices", "/customers",
                "/tax-mapping", "/review", "/approvals", "/bexio", "/synchronization",
                "/audit", "/ai-usage", "/settings",
            ];

            foreach (var route in routes)
            {
                var response = await page.GotoAsync(route);
                response!.Status.Should().Be(200, "route {0} should render", route);

                await page.WaitForSelectorAsync("h1");
                (await page.Locator("h1").First.InnerTextAsync()).Should().NotBeNullOrWhiteSpace();

                (await page.ContentAsync()).Should().NotContain("An unhandled error has occurred",
                    "route {0} rendered an error boundary", route);
            }

            failures.Should().BeEmpty("no page should log a console error");
        });
    }

    [Fact]
    public async Task The_review_queue_shows_only_records_that_need_a_human()
    {
        await SeedAndGetReferenceInvoiceIdAsync();

        await _app.WithPageAsync("the-review-queue-shows-only-records-that-need-a-human", async page =>
        {
            await page.GotoAsync("/review");

            await page.WaitForSelectorAsync("h1:has-text('Review queue')");

            // INV-10002 carries a zero-rated line, which is deliberately never high-confidence.
            await page.WaitForSelectorAsync("text=INV-10002");

            // INV-10001 validates cleanly, so it is not a review item.
            (await page.ContentAsync()).Should().NotContain("INV-10001");
        });
    }

    [Fact]
    public async Task The_invoice_screen_shows_the_source_document_beside_the_extracted_data_with_provenance()
    {
        var invoiceId = await SeedAndGetReferenceInvoiceIdAsync();

        await _app.WithPageAsync("the-invoice-screen-shows-the-source-document-beside-the-extracted-data-with-provenance", async page =>
        {
            await page.GotoAsync($"/invoices/{invoiceId}");

            await page.WaitForSelectorAsync("text=INV-10001");

            // §15: original document on the left, extracted data on the right.
            await page.WaitForSelectorAsync("text=Source document (as received)");
            await page.WaitForSelectorAsync("text=Extracted canonical data");

            // The retained raw payload really is on the page, not a placeholder.
            (await page.Locator(".doc-pane").InnerTextAsync()).Should().Contain("gid://shopify/Order/10001");

            // §22: each field says where its value came from.
            (await page.ContentAsync()).Should().Contain("currentTotalPriceSet");

            // The specified figures are rendered.
            var content = await page.ContentAsync();
            content.Should().Contain("1,000.00 CHF");
            content.Should().Contain("81.00 CHF");
            content.Should().Contain("1,081.00 CHF");

            // Tax confidence is labelled as a workflow signal, never as a tax opinion.
            content.Should().Contain("CH-VAT-STD-8.1");
        });
    }

    [Fact]
    public async Task A_reviewer_can_drive_an_invoice_from_the_ui_all_the_way_to_a_bexio_invoice()
    {
        // The flow this whole application exists to support, performed by clicking.
        var invoiceId = await SeedAndGetReferenceInvoiceIdAsync();

        await _app.WithPageAsync("a-reviewer-can-drive-an-invoice-from-the-ui-all-the-way-to-a-bexio-invoice", async page =>
        {
            await page.GotoAsync($"/invoices/{invoiceId}");
            await page.WaitForSelectorAsync("text=INV-10001");

            // Pre-flight passes, so the preview shows what would be sent.
            await page.WaitForSelectorAsync("text=Pre-flight passed");

            await AppFixture.WaitUntilInteractiveAsync(page);

            await page.ClickAsync("button:has-text('Submit for approval')");
            await page.WaitForSelectorAsync("text=Submitted for approval");
            await page.WaitForSelectorAsync("text=Awaiting approval");


            await AppFixture.WaitUntilInteractiveAsync(page);

            await page.ClickAsync("button:has-text('Approve and queue for Bexio')");
            await page.WaitForSelectorAsync("text=Approved and queued for Bexio synchronisation");

            // The background worker picks it up on its own timer; the UI reflects the result on reload.
            var synced = false;
            for (var attempt = 0; attempt < 30 && !synced; attempt++)
            {
                await Task.Delay(1000);
                await page.ReloadAsync();
                synced = (await page.ContentAsync()).Contains("Created in Bexio as", StringComparison.Ordinal);
            }

            synced.Should().BeTrue("the worker should have synchronised the approved invoice");

            var content = await page.ContentAsync();
            content.Should().Contain("Synced to Bexio");
            content.Should().Contain("mock-invoice-");
            content.Should().Contain("can never be posted again");
        });
    }

    [Fact]
    public async Task An_invoice_whose_preflight_fails_cannot_be_approved_from_the_ui()
    {
        // Seeded WITHOUT mappings, so pre-flight blocks it.
        using var client = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        await client.PostAsync(new Uri("/api/connections/bexio/refresh-reference-data", UriKind.Relative), null);
        await client.PostAsJsonAsync("/api/imports", new { sourceSystem = "Amazon" });

        var invoices = await client.GetFromJsonAsync<JsonElement>("/api/invoices?take=50");
        var amazonInvoice = invoices.EnumerateArray().First(i => i.GetProperty("sourceSystem").GetString() == "Amazon");
        var invoiceId = amazonInvoice.GetProperty("id").GetString()!;

        await client.PostAsync(new Uri($"/api/invoices/{invoiceId}/validate", UriKind.Relative), null);

        await _app.WithPageAsync("an-invoice-whose-preflight-fails-cannot-be-approved-from-the-ui", async page =>
        {
            await page.GotoAsync($"/invoices/{invoiceId}");
            await page.WaitForSelectorAsync("text=Pre-flight failed");

            var content = await page.ContentAsync();
            content.Should().Contain("cannot be synchronised");

            // The approve button must not be reachable while pre-flight is failing. It is either absent
            // (wrong state) or present and disabled — both are acceptable; an enabled one is not.
            var approve = page.Locator("button:has-text('Approve and queue for Bexio')");
            if (await approve.CountAsync() > 0)
            {
                (await approve.IsDisabledAsync()).Should().BeTrue("approval must be blocked while pre-flight fails");
            }
        });
    }

    [Fact]
    public async Task A_user_without_the_approver_role_is_refused_by_the_server_not_merely_by_a_hidden_button()
    {
        // The security claim that matters: hiding the button is not the control.
        var invoiceId = await SeedAndGetReferenceInvoiceIdAsync();

        using var client = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        await client.PostAsync(new Uri($"/api/invoices/{invoiceId}/submit", UriKind.Relative), null);

        using var reviewer = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        reviewer.DefaultRequestHeaders.Add("X-Demo-Role", "Reviewer");
        reviewer.DefaultRequestHeaders.Add("X-Demo-User", "reviewer@test.example");

        var response = await reviewer.PostAsJsonAsync($"/api/invoices/{invoiceId}/approve", new { comment = "attempting" });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("FORBIDDEN");

        // And the invoice did not move.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/invoices/{invoiceId}");
        detail.GetProperty("invoice").GetProperty("state").GetString().Should().Be("ReadyForApproval");
    }

    [Fact]
    public async Task The_audit_screen_can_trace_one_invoice_by_correlation_id()
    {
        var invoiceId = await SeedAndGetReferenceInvoiceIdAsync();

        using var client = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/invoices/{invoiceId}");
        var correlationId = detail.GetProperty("invoice").GetProperty("correlationId").GetString()!;

        await _app.WithPageAsync("the-audit-screen-can-trace-one-invoice-by-correlation-id", async page =>
        {
            await page.GotoAsync("/audit");
            await page.WaitForSelectorAsync("h1:has-text('Audit log')");

            await AppFixture.WaitUntilInteractiveAsync(page);

            await page.FillAsync("#correlation", correlationId);
            await page.ClickAsync("button:has-text('Filter')");

            await page.WaitForSelectorAsync("text=invoice.imported");
            (await page.ContentAsync()).Should().Contain("invoice.validated");
        });
    }

    [Fact]
    public async Task The_tax_mapping_screen_shows_mappings_derived_from_discovered_bexio_configuration()
    {
        await SeedAndGetReferenceInvoiceIdAsync();

        await _app.WithPageAsync("the-tax-mapping-screen-shows-mappings-derived-from-discovered-bexio-configuration", async page =>
        {
            await page.GotoAsync("/tax-mapping");
            await page.WaitForSelectorAsync("h1:has-text('Tax and account mapping')");

            var content = await page.ContentAsync();

            // Derived from what Bexio reported, never hardcoded — the mock's ids are deliberately
            // non-numeric so their presence here proves discovery ran.
            content.Should().Contain("CH-VAT-STD-8.1");
            content.Should().Contain("mock-tax-");
            content.Should().Contain("Discovered Bexio taxes");
        });
    }
}
