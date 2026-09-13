using System.Collections.Frozen;

namespace IelBexio.Domain.Workflow;

/// <summary>
/// The single authority on which workflow transitions are legal. Every state change in the system
/// goes through <see cref="Transition"/>; there is no other way to move an invoice forward. This is
/// what makes "unapproved data cannot reach Bexio" (acceptance criterion 12) a structural property
/// rather than a convention.
/// </summary>
public static class InvoiceWorkflow
{
    private static readonly FrozenDictionary<InvoiceWorkflowState, FrozenSet<InvoiceWorkflowState>> Allowed =
        new Dictionary<InvoiceWorkflowState, FrozenSet<InvoiceWorkflowState>>
        {
            [InvoiceWorkflowState.Imported] = Set(
                InvoiceWorkflowState.Extracted,
                InvoiceWorkflowState.ExtractionFailed),

            [InvoiceWorkflowState.Extracted] = Set(
                InvoiceWorkflowState.Validated,
                InvoiceWorkflowState.NeedsReview,
                InvoiceWorkflowState.ValidationFailed),

            [InvoiceWorkflowState.Validated] = Set(
                InvoiceWorkflowState.NeedsReview,
                InvoiceWorkflowState.ReadyForApproval,
                InvoiceWorkflowState.ValidationFailed),

            [InvoiceWorkflowState.NeedsReview] = Set(
                InvoiceWorkflowState.HumanVerified,
                InvoiceWorkflowState.ValidationFailed,
                // re-validation after a human correction may clear every issue outright
                InvoiceWorkflowState.Validated),

            [InvoiceWorkflowState.HumanVerified] = Set(
                InvoiceWorkflowState.ReadyForApproval,
                InvoiceWorkflowState.NeedsReview),

            [InvoiceWorkflowState.ReadyForApproval] = Set(
                InvoiceWorkflowState.Approved,
                InvoiceWorkflowState.ApprovalRejected,
                InvoiceWorkflowState.NeedsReview),

            [InvoiceWorkflowState.Approved] = Set(
                InvoiceWorkflowState.QueuedForBexio),

            [InvoiceWorkflowState.QueuedForBexio] = Set(
                InvoiceWorkflowState.Syncing),

            [InvoiceWorkflowState.Syncing] = Set(
                InvoiceWorkflowState.Synced,
                InvoiceWorkflowState.SyncFailed,
                InvoiceWorkflowState.BexioRejected,
                InvoiceWorkflowState.ReconciliationRequired,
                // a transient failure returns the message to the queue for another attempt
                InvoiceWorkflowState.QueuedForBexio),

            // Terminal for posting purposes: a synced invoice can never return to a sync-eligible
            // state, which is what stops it being posted twice. Reconciliation is the one exception —
            // discovering that Bexio no longer agrees with us must be recordable, and a record stuck in
            // Synced while the two sides disagree is worse than one flagged for a human. Note that
            // ReconciliationRequired is itself not sync-eligible, so this opens no path back to posting.
            [InvoiceWorkflowState.Synced] = Set(InvoiceWorkflowState.ReconciliationRequired),

            [InvoiceWorkflowState.ExtractionFailed] = Set(InvoiceWorkflowState.Imported, InvoiceWorkflowState.NeedsReview),
            [InvoiceWorkflowState.ValidationFailed] = Set(InvoiceWorkflowState.NeedsReview, InvoiceWorkflowState.Extracted),
            [InvoiceWorkflowState.ApprovalRejected] = Set(InvoiceWorkflowState.NeedsReview),
            [InvoiceWorkflowState.SyncFailed] = Set(InvoiceWorkflowState.QueuedForBexio, InvoiceWorkflowState.ReconciliationRequired, InvoiceWorkflowState.NeedsReview),
            [InvoiceWorkflowState.BexioRejected] = Set(InvoiceWorkflowState.NeedsReview, InvoiceWorkflowState.ReconciliationRequired),
            [InvoiceWorkflowState.ReconciliationRequired] = Set(InvoiceWorkflowState.NeedsReview, InvoiceWorkflowState.Synced),
        }.ToFrozenDictionary();

    private static FrozenSet<InvoiceWorkflowState> Set(params InvoiceWorkflowState[] states) => states.ToFrozenSet();

    /// <summary>States from which a Bexio synchronisation may legitimately be dispatched.</summary>
    public static readonly FrozenSet<InvoiceWorkflowState> SyncEligible =
        Set(InvoiceWorkflowState.QueuedForBexio, InvoiceWorkflowState.Syncing);

    public static bool IsFailure(InvoiceWorkflowState state) => (int)state >= 100;

    public static bool IsTerminalSuccess(InvoiceWorkflowState state) => state == InvoiceWorkflowState.Synced;

    public static bool CanTransition(InvoiceWorkflowState from, InvoiceWorkflowState to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyCollection<InvoiceWorkflowState> AllowedFrom(InvoiceWorkflowState from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : Set();

    /// <summary>Throws <see cref="InvalidWorkflowTransitionException"/> unless the transition is legal.</summary>
    public static void Transition(InvoiceWorkflowState from, InvoiceWorkflowState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidWorkflowTransitionException(from, to);
        }
    }
}

public sealed class InvalidWorkflowTransitionException : InvalidOperationException
{
    public InvalidWorkflowTransitionException(InvoiceWorkflowState from, InvoiceWorkflowState to)
        : base($"Illegal workflow transition {from} -> {to}.")
    {
        From = from;
        To = to;
    }

    public InvalidWorkflowTransitionException() : base("Illegal workflow transition.") { }

    public InvalidWorkflowTransitionException(string message) : base(message) { }

    public InvalidWorkflowTransitionException(string message, Exception innerException) : base(message, innerException) { }

    public InvoiceWorkflowState From { get; }
    public InvoiceWorkflowState To { get; }
}
