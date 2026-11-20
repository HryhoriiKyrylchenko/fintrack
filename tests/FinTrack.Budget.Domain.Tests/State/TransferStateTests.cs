using CsCheck;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Transfers in the state builder (ARCHITECTURE 4.3): the source loses <c>FromAmount</c>, the destination
/// gains <c>ToAmount</c>, and a transfer is never income. Invariant 11 as a property: totals per currency
/// equal an independent sum, so no currency is ever added to another.
/// </summary>
public class TransferStateTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly DateOnly Today = new(2026, 1, 15);

    [Fact]
    public void Transfer_MovesTheAmounts_AndIsNotIncome()
    {
        var pln = Savings("Cushion", Currency.Pln);
        var eur = Savings("Holiday", Currency.Eur);
        var deposit = Deposit.Create(NewId(), Owner, pln.BucketId, Money.From(1000m, Currency.Pln), Today.AddDays(-2), Today, EntryInfo.None, FakeClock.DefaultStart).Value;
        var transfer = Transfer.Create(NewId(), Owner, pln.BucketId, eur.BucketId, Money.From(400m, Currency.Pln), Money.From(93m, Currency.Eur), Money.From(4m, Currency.Pln),
            Today.AddDays(-1), Today, EntryInfo.None, FakeClock.DefaultStart).Value;

        var state = BudgetState.Replay([pln, eur], [deposit, transfer], Today);

        state.BalanceOf(pln.BucketId).Available.ShouldBe(Money.From(600m, Currency.Pln));
        state.BalanceOf(eur.BucketId).Available.ShouldBe(Money.From(93m, Currency.Eur));
        state.BalanceOf(eur.BucketId).Deposited.ShouldBe(Money.Zero(Currency.Eur));
        state.FirstOverdraft.ShouldBeNull();
    }

    [Fact]
    public void TransferOutOfASavingsBucketBeforeTheMoneyArrived_IsAnOverdraft()
    {
        var from = Savings("Cushion", Currency.Pln);
        var to = Savings("Holiday", Currency.Pln);
        var deposit = Deposit.Create(NewId(), Owner, from.BucketId, Money.From(10m, Currency.Pln), Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;
        var transfer = Transfer.Create(NewId(), Owner, from.BucketId, to.BucketId, Money.From(10m, Currency.Pln), Money.From(10m, Currency.Pln), null,
            Today.AddDays(-1), Today, EntryInfo.None, FakeClock.DefaultStart.AddSeconds(1)).Value;

        BudgetState.Replay([from, to], [deposit, transfer], Today).FirstOverdraft.ShouldBe(new Overdraft(from.BucketId, Today.AddDays(-1)));
    }

    [Fact]
    public void TransferOutOfAnInvestmentBucket_MayGoBelowZero()
    {
        var stocks = Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, "Stocks", Currency.Pln, BucketKind.Investment, InvestmentProfile.RealValueAssets, []).Value;
        var cushion = Savings("Cushion", Currency.Pln);
        var transfer = Transfer.Create(NewId(), Owner, stocks.BucketId, cushion.BucketId, Money.From(50m, Currency.Pln), Money.From(50m, Currency.Pln), null,
            Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;

        var state = BudgetState.Replay([stocks, cushion], [transfer], Today);

        state.BalanceOf(stocks.BucketId).Available.ShouldBe(-Money.From(50m, Currency.Pln));
        state.FirstOverdraft.ShouldBeNull();
    }

    // --- Invariant 11 and "a transfer without fee in one currency changes no total" ---

    private static readonly Currency[] Currencies = [Currency.Pln, Currency.Eur];

    private static readonly Gen<Bucket[]> Buckets =
        Gen.Select(Gen.Int[0, 1], Gen.Guid).Array[2, 5].Select(specs =>
            specs.Select((spec, index) => Bucket.Create(BucketId.From(spec.Item2), Owner, $"Bucket {index}", Currencies[spec.Item1], BucketKind.Savings, null, []).Value).ToArray());

    private static Gen<JournalEntry> EntriesIn(Bucket[] buckets) =>
        Gen.Select(Gen.Int[0, buckets.Length - 1], Gen.Int[0, buckets.Length - 1], Gen.Long[1, 100_000], Gen.Long[1, 100_000], Gen.Int[0, 3], Gen.Guid, Gen.Bool)
            .Select<int, int, long, long, int, Guid, bool, JournalEntry>((from, to, fromUnits, toUnits, feeUnits, id, isDeposit) =>
            {
                var source = buckets[from];
                if (isDeposit || from == to)
                {
                    return Deposit.Create(JournalEntryId.From(id), Owner, source.BucketId, Money.From(fromUnits / 100m, source.Currency), Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;
                }

                var destination = buckets[to];
                var fromAmount = Money.From((fromUnits + feeUnits) / 100m, source.Currency);
                var fee = feeUnits == 0 ? null : Money.From(feeUnits / 100m, source.Currency);
                var toAmount = source.Currency == destination.Currency
                    ? Money.From(fromUnits / 100m, destination.Currency)
                    : Money.From(toUnits / 100m, destination.Currency);

                return Transfer.Create(JournalEntryId.From(id), Owner, source.BucketId, destination.BucketId, fromAmount, toAmount, fee, Today, Today, EntryInfo.None, FakeClock.DefaultStart).Value;
            });

    private static readonly Gen<(Bucket[] Buckets, JournalEntry[] Journal)> Histories =
        Buckets.SelectMany(buckets => EntriesIn(buckets).Array[0, 40].Select(journal => (buckets, journal)));

    [Fact]
    public void TotalsPerCurrency_EqualAnIndependentSum_NeverMixingCurrencies() =>
        Histories.Sample(history =>
        {
            var state = BudgetState.Replay(history.Buckets, history.Journal, Today);

            return Currencies.All(currency =>
            {
                var fromState = history.Buckets.Where(bucket => bucket.Currency == currency)
                    .Sum(bucket => state.BalanceOf(bucket.BucketId).Available.Amount);

                var expected = history.Journal.Sum(entry => entry switch
                {
                    Deposit deposit when deposit.Amount.Currency == currency => deposit.Amount.Amount,
                    Transfer transfer => (transfer.ToAmount.Currency == currency ? transfer.ToAmount.Amount : 0m)
                        - (transfer.FromAmount.Currency == currency ? transfer.FromAmount.Amount : 0m),
                    _ => 0m,
                });

                return fromState == expected;
            });
        });

    [Fact]
    public void SameCurrencyTransferWithoutFee_LeavesEveryCurrencysTotalUnchanged() =>
        Histories.Sample(history =>
        {
            var plnBuckets = history.Buckets.Where(bucket => bucket.Currency == Currency.Pln).ToArray();
            if (plnBuckets.Length < 2)
            {
                return true;
            }

            var before = BudgetState.Replay(history.Buckets, history.Journal, Today);
            var transfer = Transfer.Create(NewId(), Owner, plnBuckets[0].BucketId, plnBuckets[1].BucketId, Money.From(1m, Currency.Pln), Money.From(1m, Currency.Pln), null,
                Today, Today, EntryInfo.None, FakeClock.DefaultStart.AddDays(1)).Value;
            var after = before.Apply(transfer);

            return Currencies.All(currency => Total(before, history.Buckets, currency) == Total(after, history.Buckets, currency));
        });

    private static decimal Total(BudgetState state, Bucket[] buckets, Currency currency) =>
        buckets.Where(bucket => bucket.Currency == currency).Sum(bucket => state.BalanceOf(bucket.BucketId).Available.Amount);

    private static Bucket Savings(string name, Currency currency) =>
        Bucket.Create(BucketId.From(Guid.CreateVersion7()), Owner, name, currency, BucketKind.Savings, null, []).Value;

    private static JournalEntryId NewId() => JournalEntryId.From(Guid.CreateVersion7());
}
