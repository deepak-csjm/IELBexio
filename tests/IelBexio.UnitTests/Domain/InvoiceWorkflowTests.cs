using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Workflow;

namespace IelBexio.UnitTests.Domain;

/// <summary>
/// The workflow is what enforces acceptance criterion 12 ("unapproved data cannot reach Bexio"). These
/// tests assert that the guarantee is structural — that there is no sequence of legal calls that gets
/// an unapproved invoice into a sync-eligible state.
/// </summary>
public sealed class InvoiceWorkflowTests
{
    [Fact]
    public void The_specified_happy_path_is_walkable_end_to_end()
    {
        var invoice = NewInvoice();

        InvoiceWorkflowState[] path =
        [
            InvoiceWorkflowState.Extracted,
            InvoiceWorkflowState.Validated,
            InvoiceWorkflowState.NeedsReview,
            InvoiceWorkflowState.HumanVerified,
            InvoiceWorkflowState.ReadyForApproval,
            InvoiceWorkflowState.Approved,
            InvoiceWorkflowState.QueuedForBexio,
            InvoiceWorkflowState.Syncing,
            InvoiceWorkflowState.Synced,
        ];

        foreach (var state in path)
        {
            invoice.TransitionTo(state);
        }

        invoice.WorkflowState.Should().Be(InvoiceWorkflowState.Synced);
    }

    [Theory]
    [InlineData(InvoiceWorkflowState.Imported)]
    [InlineData(InvoiceWorkflowState.Extracted)]
    [InlineData(InvoiceWorkflowState.Validated)]
    [InlineData(InvoiceWorkflowState.NeedsReview)]
    [InlineData(InvoiceWorkflowState.HumanVerified)]
    [InlineData(InvoiceWorkflowState.ReadyForApproval)]
    public void No_state_before_approval_may_jump_straight_to_a_sync_eligible_state(InvoiceWorkflowState from)
    {
        foreach (var syncState in InvoiceWorkflow.SyncEligible)
        {
            InvoiceWorkflow.CanTransition(from, syncState).Should().BeFalse(
                "an invoice in {0} has not been approved and must not become {1}", from, syncState);
        }
    }

    [Fact]
    public void Queued_for_bexio_is_reachable_only_from_approved_or_a_retryable_failure()
    {
        var sources = Enum.GetValues<InvoiceWorkflowState>()
            .Where(s => InvoiceWorkflow.CanTransition(s, InvoiceWorkflowState.QueuedForBexio))
            .ToList();

        sources.Should().BeEquivalentTo(
        [
            InvoiceWorkflowState.Approved,
            // Re-queueing after a failed attempt is a retry of an already-approved invoice, not a
            // second approval, so these two do not weaken the guarantee.
            InvoiceWorkflowState.Syncing,
            InvoiceWorkflowState.SyncFailed,
        ]);
    }

    [Fact]
    public void A_synced_invoice_is_terminal_and_can_never_be_posted_again()
    {
        InvoiceWorkflow.AllowedFrom(InvoiceWorkflowState.Synced).Should().BeEmpty();

        var invoice = NewInvoice();
        invoice.RestoreWorkflowState(InvoiceWorkflowState.Synced);

        var act = () => invoice.TransitionTo(InvoiceWorkflowState.QueuedForBexio);
        act.Should().Throw<InvalidWorkflowTransitionException>();
    }

    [Fact]
    public void An_illegal_transition_throws_rather_than_silently_succeeding()
    {
        var invoice = NewInvoice();

        var act = () => invoice.TransitionTo(InvoiceWorkflowState.Approved);

        act.Should().Throw<InvalidWorkflowTransitionException>()
            .Which.To.Should().Be(InvoiceWorkflowState.Approved);
    }

    [Fact]
    public void A_rejected_approval_returns_the_invoice_to_review_and_not_to_approved()
    {
        InvoiceWorkflow.AllowedFrom(InvoiceWorkflowState.ApprovalRejected)
            .Should().BeEquivalentTo([InvoiceWorkflowState.NeedsReview]);
    }

    [Fact]
    public void Failure_states_are_identifiable_without_enumerating_them_at_every_call_site()
    {
        InvoiceWorkflow.IsFailure(InvoiceWorkflowState.SyncFailed).Should().BeTrue();
        InvoiceWorkflow.IsFailure(InvoiceWorkflowState.BexioRejected).Should().BeTrue();
        InvoiceWorkflow.IsFailure(InvoiceWorkflowState.Synced).Should().BeFalse();
        InvoiceWorkflow.IsFailure(InvoiceWorkflowState.NeedsReview).Should().BeFalse();
    }

    [Fact]
    public void Every_state_is_reachable_from_imported_except_by_design_isolated_ones()
    {
        // Guards against a future edit that adds a state and forgets to wire it into the graph.
        var reachable = new HashSet<InvoiceWorkflowState> { InvoiceWorkflowState.Imported };
        var frontier = new Queue<InvoiceWorkflowState>([InvoiceWorkflowState.Imported]);

        while (frontier.Count > 0)
        {
            foreach (var next in InvoiceWorkflow.AllowedFrom(frontier.Dequeue()))
            {
                if (reachable.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        reachable.Should().BeEquivalentTo(Enum.GetValues<InvoiceWorkflowState>());
    }

    private static Invoice NewInvoice() => new()
    {
        TenantId = Guid.CreateVersion7(),
        SourceSystem = SourceSystem.Shopify,
        SourceDocumentId = "test-1",
        Currency = "CHF",
    };
}
