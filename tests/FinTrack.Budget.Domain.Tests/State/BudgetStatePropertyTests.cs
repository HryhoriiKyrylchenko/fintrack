using CsCheck;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Invariants 2 and 10 (ARCHITECTURE 7) for arbitrary journals of deposits over several buckets and
/// currencies: <c>Available</c> = <c>NetContributed</c> − <c>OutstandingPrincipal</c>; the state from a
/// replay equals the state built entry by entry, and does not depend on the order entries were stored in.
/// </summary>
public class BudgetStatePropertyTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly Currency[] Currencies = [Currency.Pln, Currency.Eur, Currency.Usd];
    private static readonly DateOnly Today = new(2026, 1, 15);

    // IDs come from the generator (not Guid.CreateVersion7), so a failing case can be replayed from its seed.
    private static readonly Gen<Bucket[]> Buckets =
        Gen.Select(Gen.Int[0, Currencies.Length - 1], Gen.Guid).Array[1, 5].Select(specs =>
            specs.Select((spec, index) => Bucket.Create(
                BucketId.From(spec.Item2), Owner, $"Bucket {index}", Currencies[spec.Item1], BucketKind.Savings, null, []).Value).ToArray());

    // A deposit into one of the buckets: up to a million in whole minor units, dated in the last two
    // years, recorded at one of a few instants (so equal dates and equal instants both occur).
    private static Gen<Deposit> DepositsInto(Bucket[] buckets) =>
        Gen.Select(Gen.Int[0, buckets.Length - 1], Gen.Long[1, 100_000_000], Gen.Int[0, 730], Gen.Int[0, 3], Gen.Guid)
            .Select((index, units, daysBack, instant, id) => Deposit.Create(
                JournalEntryId.From(id),
                Owner,
                buckets[index].BucketId,
                Money.From(units / 100m, buckets[index].Currency),
                Today.AddDays(-daysBack),
                Today,
                EntryInfo.None,
                FakeClock.DefaultStart.AddMinutes(instant)).Value);

    private static readonly Gen<(Bucket[] Buckets, Deposit[] Journal)> Histories =
        Buckets.SelectMany(buckets => DepositsInto(buckets).Array[0, 40].Select(journal => (buckets, journal)));

    [Fact]
    public void Available_IsNetContributedMinusOutstandingPrincipal() =>
        Histories.Sample(history =>
            BudgetState.Replay(history.Buckets, history.Journal).Balances
                .All(balance => balance.Available == balance.NetContributed - balance.OutstandingPrincipal));

    [Fact]
    public void Replay_EqualsApplyingEntryByEntryInReplayOrder() =>
        Histories.Sample(history =>
        {
            var replayed = BudgetState.Replay(history.Buckets, history.Journal);
            var stepByStep = ReplayOrder.Sort(history.Journal)
                .Aggregate(BudgetState.Replay(history.Buckets, []), (state, entry) => state.Apply(entry));

            return replayed.Balances.SequenceEqual(stepByStep.Balances);
        });

    [Fact]
    public void Replay_DoesNotDependOnTheStoredOrder() =>
        Gen.Select(Histories, Gen.Int).Sample((history, seed) =>
        {
            var shuffled = history.Journal.OrderBy(_ => new Random(seed).Next()).ToArray();

            return BudgetState.Replay(history.Buckets, history.Journal).Balances
                .SequenceEqual(BudgetState.Replay(history.Buckets, shuffled).Balances);
        });

    [Fact]
    public void Deposited_IsTheSumOfDeposits_PerBucket() =>
        Histories.Sample(history =>
        {
            var state = BudgetState.Replay(history.Buckets, history.Journal);
            return history.Buckets.All(bucket => state.BalanceOf(bucket.BucketId).Deposited.Amount
                == history.Journal.Where(deposit => deposit.BucketId == bucket.BucketId).Sum(deposit => deposit.Amount.Amount));
        });
}
