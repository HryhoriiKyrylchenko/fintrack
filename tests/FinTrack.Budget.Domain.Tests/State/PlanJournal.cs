using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Builds plan entries and linked contributions for the state tests, all in PLN, each recorded a little later than the
/// one before so the replay order is the order they are built in (for one date). Invented numbers only.
/// </summary>
internal sealed class PlanJournal(DateOnly today)
{
    public static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    private int recorded;

    public DateOnly Today { get; } = today;

    public Bucket Cushion { get; } = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value;

    public Bucket Holiday { get; } = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Holiday", Currency.Pln, BucketKind.Savings, null, []).Value;

    public static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);

    public static YearMonth Month(int year, int month) => new(year, month);

    public BudgetState Replay(params JournalEntry[] journal) => BudgetState.Replay([Cushion, Holiday], journal, Today);

    public PlanCreated Create(Bucket bucket, decimal amount, YearMonth from, YearMonth? to = null, int day = 0) =>
        PlanCreated.Create(NewId(), Owner, bucket.BucketId, Pln(amount), from, to, Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    public PlanAmountChanged Change(PlanCreated plan, decimal amount, YearMonth from, int day = 0) =>
        PlanAmountChanged.Create(NewId(), Owner, plan.PlanId, Pln(amount), from, Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    public PlanEndChanged End(PlanCreated plan, YearMonth? to, int day = 0) =>
        PlanEndChanged.Create(NewId(), Owner, plan.PlanId, to, Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt()).Value;

    public Deposit Pay(Bucket bucket, decimal amount, PlanCreated? plan, int day = 0) =>
        Deposit.Create(NewId(), Owner, bucket.BucketId, Pln(amount), Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt(), plan?.PlanId).Value;

    public Transfer Move(Bucket from, Bucket to, decimal amount, PlanCreated? plan, int day = 0) =>
        Transfer.Create(NewId(), Owner, from.BucketId, to.BucketId, Pln(amount), Pln(amount), null, Today.AddDays(day), Today, EntryInfo.None, NextRecordedAt(), plan?.PlanId).Value;

    public Reversal Reverse(JournalEntry entry) =>
        Reversal.Create(NewId(), Owner, entry.EntryId, Today, Today, EntryInfo.None, NextRecordedAt()).Value;

    private static JournalEntryId NewId() => JournalEntryId.From(Guid.CreateVersion7());

    private DateTimeOffset NextRecordedAt() => FakeClock.DefaultStart.AddSeconds(++recorded);
}
