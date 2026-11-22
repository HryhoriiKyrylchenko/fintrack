using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using AdjustmentDirection = FinTrack.Budget.Contracts.V1.Journal.AdjustmentDirection;
using Model = FinTrack.Budget.Domain.Journal;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// Converts journal entries to the V1 contract, type by type. Written out explicitly, like
/// <c>BucketContractMapping</c>: a new entry type must be mapped deliberately (an unmapped one throws, a bug).
/// </summary>
internal static class JournalContractMapping
{
    /// <summary>
    /// The DTO of <paramref name="entry"/>; <paramref name="reversedBy"/> is the ID of the reversal that cancels it, if any;
    /// <paramref name="journal"/> is the owner's journal by ID (a repayment's lender is its borrow's).
    /// </summary>
    public static JournalEntryDto ToDto(JournalEntry entry, Guid? reversedBy, IReadOnlyDictionary<Guid, JournalEntry> journal) => entry switch
    {
        Borrow borrow => Common(borrow, JournalEntryType.Borrow, reversedBy) with
        {
            FromBucketId = borrow.LenderBucketId.Value,
            ToBucketId = borrow.ToBucketId?.Value,
            Amount = borrow.Principal,
            LoanId = borrow.LoanId.Value,
        },
        Repayment repayment => Common(repayment, JournalEntryType.Repayment, reversedBy) with
        {
            FromBucketId = repayment.FromBucketId?.Value,
            ToBucketId = LenderOf(repayment, journal).Value,
            Amount = repayment.Amount,
            LoanId = repayment.LoanId.Value,
        },
        PlanCreated created => Common(created, JournalEntryType.PlanCreated, reversedBy) with
        {
            BucketId = created.BucketId.Value,
            Amount = created.Amount,
            PlanId = created.PlanId.Value,
            FromMonth = created.ValidFrom,
            ToMonth = created.ValidTo,
        },
        PlanAmountChanged changed => Common(changed, JournalEntryType.PlanAmountChanged, reversedBy) with
        {
            BucketId = BucketOf(changed.PlanId, journal).Value,
            Amount = changed.Amount,
            PlanId = changed.PlanId.Value,
            FromMonth = changed.From,
        },
        PlanEndChanged ended => Common(ended, JournalEntryType.PlanEndChanged, reversedBy) with
        {
            BucketId = BucketOf(ended.PlanId, journal).Value,
            PlanId = ended.PlanId.Value,
            ToMonth = ended.ValidTo,
        },
        OpeningBalance opening => Single(opening, JournalEntryType.OpeningBalance, reversedBy),
        Deposit deposit => Single(deposit, JournalEntryType.Deposit, reversedBy) with
        {
            PlanId = deposit.PlanId?.Value,
        },
        Spend spend => Single(spend, JournalEntryType.Spend, reversedBy),
        Adjustment adjustment => Single(adjustment, JournalEntryType.Adjustment, reversedBy) with
        {
            Direction = ToContract(adjustment.Direction),
        },
        Transfer transfer => Common(transfer, JournalEntryType.Transfer, reversedBy) with
        {
            FromBucketId = transfer.FromBucketId.Value,
            ToBucketId = transfer.ToBucketId.Value,
            FromAmount = transfer.FromAmount,
            ToAmount = transfer.ToAmount,
            Fee = transfer.Fee,
            Rate = transfer.Rate,
            PlanId = transfer.PlanId?.Value,
        },
        Reversal reversal => Common(reversal, JournalEntryType.Reversal, reversedBy) with
        {
            ReversedEntryId = reversal.ReversedEntryId.Value,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry?.GetType().Name, "Unknown journal entry type."),
    };

    /// <summary>The lender of <paramref name="repayment"/>'s loan: its borrow's lender. A stored repayment always has its borrow.</summary>
    /// <exception cref="InvalidOperationException">The journal has no borrow with the loan's ID (a bug).</exception>
    public static BucketId LenderOf(Repayment repayment, IReadOnlyDictionary<Guid, JournalEntry> journal) =>
        journal.TryGetValue(repayment.LoanId.Value, out var entry) && entry is Borrow borrow
            ? borrow.LenderBucketId
            : throw new InvalidOperationException($"Repayment {repayment.Id} refers to loan {repayment.LoanId}, which has no borrow.");

    /// <summary>The bucket of plan <paramref name="planId"/>: its <c>PlanCreated</c>'s. A stored plan change always has its plan.</summary>
    /// <exception cref="InvalidOperationException">The journal has no <c>PlanCreated</c> with the plan's ID (a bug).</exception>
    public static BucketId BucketOf(PlanId planId, IReadOnlyDictionary<Guid, JournalEntry> journal) =>
        journal.TryGetValue(planId.Value, out var entry) && entry is PlanCreated created
            ? created.BucketId
            : throw new InvalidOperationException($"An entry refers to plan {planId}, which has no PlanCreated.");

    /// <summary>The contract's copy of <paramref name="direction"/>.</summary>
    public static AdjustmentDirection ToContract(Model.AdjustmentDirection direction) => direction switch
    {
        Model.AdjustmentDirection.Increase => AdjustmentDirection.Increase,
        Model.AdjustmentDirection.Decrease => AdjustmentDirection.Decrease,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown adjustment direction."),
    };

    /// <summary>The domain's copy of <paramref name="direction"/>.</summary>
    public static Model.AdjustmentDirection ToDomain(AdjustmentDirection direction) => direction switch
    {
        AdjustmentDirection.Increase => Model.AdjustmentDirection.Increase,
        AdjustmentDirection.Decrease => Model.AdjustmentDirection.Decrease,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown adjustment direction."),
    };

    private static JournalEntryDto Single(SingleBucketEntry entry, JournalEntryType type, Guid? reversedBy) =>
        Common(entry, type, reversedBy) with
        {
            BucketId = entry.BucketId.Value,
            Amount = entry.Amount,
        };

    private static JournalEntryDto Common(JournalEntry entry, JournalEntryType type, Guid? reversedBy) => new(
        entry.Id,
        type,
        entry.OccurredOn,
        entry.RecordedAt,
        entry.Note,
        [.. entry.Tags],
        entry.GroupId,
        entry.Source.Value,
        entry.ExternalId,
        BucketId: null,
        Amount: null,
        FromBucketId: null,
        ToBucketId: null,
        FromAmount: null,
        ToAmount: null,
        Fee: null,
        Rate: null,
        ReversedEntryId: null,
        ReversedBy: reversedBy);
}
