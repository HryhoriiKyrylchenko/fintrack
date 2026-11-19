using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Domain.Journal;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// Converts journal entries to the V1 contract, type by type. Written out explicitly, like
/// <c>BucketContractMapping</c>: a new entry type must be mapped deliberately (an unmapped one throws, a bug).
/// </summary>
internal static class JournalContractMapping
{
    /// <summary>The DTO of <paramref name="entry"/>; <paramref name="reversedBy"/> is the ID of the reversal that cancels it, if any.</summary>
    public static JournalEntryDto ToDto(JournalEntry entry, Guid? reversedBy) => entry switch
    {
        OpeningBalance opening => Single(opening, JournalEntryType.OpeningBalance, reversedBy),
        Deposit deposit => Single(deposit, JournalEntryType.Deposit, reversedBy),
        Spend spend => Single(spend, JournalEntryType.Spend, reversedBy),
        Transfer transfer => Common(transfer, JournalEntryType.Transfer, reversedBy) with
        {
            FromBucketId = transfer.FromBucketId.Value,
            ToBucketId = transfer.ToBucketId.Value,
            FromAmount = transfer.FromAmount,
            ToAmount = transfer.ToAmount,
            Fee = transfer.Fee,
            Rate = transfer.Rate,
        },
        Reversal reversal => Common(reversal, JournalEntryType.Reversal, reversedBy) with
        {
            ReversedEntryId = reversal.ReversedEntryId.Value,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry?.GetType().Name, "Unknown journal entry type."),
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
