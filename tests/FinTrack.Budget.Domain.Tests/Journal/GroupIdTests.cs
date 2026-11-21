using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Journal;

/// <summary>
/// The group of a command that writes several entries (plan 1b B15): every factory takes it from
/// <see cref="EntryInfo.GroupId"/>, and a repeat recognises an entry only with the same group.
/// </summary>
public class GroupIdTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly BucketId Cushion = BucketId.From(Guid.Parse("01900000-0000-7000-8000-000000000001"));
    private static readonly BucketId Holiday = BucketId.From(Guid.Parse("01900000-0000-7000-8000-000000000002"));
    private static readonly JournalEntryId EntryId = JournalEntryId.From(Guid.Parse("01900000-0000-7000-8000-0000000000aa"));
    private static readonly JournalEntryId ReversedId = JournalEntryId.From(Guid.Parse("01900000-0000-7000-8000-0000000000bb"));

    public static TheoryData<string> Types => ["OpeningBalance", "Deposit", "Spend", "Transfer", "Reversal", "Adjustment", "Borrow", "Repayment"];

    [Theory]
    [MemberData(nameof(Types))]
    public void EveryFactory_CarriesTheGroup(string type)
    {
        var group = Guid.CreateVersion7();

        Create(type, Info(group)).GroupId.ShouldBe(group);
        Create(type, EntryInfo.None).GroupId.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void SameEntry_InAnotherGroupOrInNone_HasOtherContent(string type)
    {
        var group = Guid.CreateVersion7();
        var grouped = Create(type, Info(group));

        grouped.HasSameContent(Create(type, Info(group))).ShouldBeTrue();
        grouped.HasSameContent(Create(type, Info(Guid.CreateVersion7()))).ShouldBeFalse();
        grouped.HasSameContent(Create(type, EntryInfo.None)).ShouldBeFalse();
        Create(type, EntryInfo.None).HasSameContent(grouped).ShouldBeFalse();
    }

    private static EntryInfo Info(Guid group) => new(null, [], EntrySource.Manual, group);

    private static JournalEntry Create(string type, EntryInfo info)
    {
        var amount = Money.From(10m, Currency.Pln);
        var at = FakeClock.DefaultStart;
        return type switch
        {
            "OpeningBalance" => OpeningBalance.Create(EntryId, Owner, Cushion, amount, Today, Today, info, at).Value,
            "Deposit" => Deposit.Create(EntryId, Owner, Cushion, amount, Today, Today, info, at).Value,
            "Spend" => Spend.Create(EntryId, Owner, Cushion, amount, Today, Today, info, at).Value,
            "Transfer" => Transfer.Create(EntryId, Owner, Cushion, Holiday, amount, amount, null, Today, Today, info, at).Value,
            "Borrow" => Borrow.Create(EntryId, Owner, Cushion, amount, Holiday, Today, Today, info, at).Value,
            "Repayment" => Repayment.Create(EntryId, Owner, Loans.LoanId.From(ReversedId.Value), amount, null, Today, Today, info, at).Value,
            "Adjustment" => Adjustment.Create(EntryId, Owner, Cushion, amount, AdjustmentDirection.Decrease, Today, Today, new EntryInfo("Recount", info.Tags, info.Source, info.GroupId), at).Value,
            _ => Reversal.Create(EntryId, Owner, ReversedId, Today, Today, info, at).Value,
        };
    }
}
