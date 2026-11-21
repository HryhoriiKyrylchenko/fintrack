using CsCheck;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Loans;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Property-based tests of loans in the state builder (plan 1b step 5): random histories of deposits, spends, borrows,
/// repayments and reversals, accepted as the handlers accept them (no overdraft, no over-repayment on the replay with
/// the new entry). Invariant 2 at the end, invariant 4 at every point by an independent running sum per loan, the
/// replay equal to the step-by-step build, a reversal equal to the journal without the entry, and the totals a borrow
/// and a repayment leave.
/// </summary>
public class LoanPropertyTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly DateOnly Today = new(2026, 1, 15);

    private enum Kind
    {
        Deposit,
        Spend,
        Borrow,
        Repay,
        Reverse,
    }

    // Two savings buckets and one investment bucket, all in PLN.
    private static readonly Gen<Bucket[]> Buckets = Gen.Guid.Array[3].Select(ids => new[]
    {
        Bucket.Create(BucketId.From(ids[0]), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value,
        Bucket.Create(BucketId.From(ids[1]), Owner, "Holiday", Currency.Pln, BucketKind.Savings, null, []).Value,
        Bucket.Create(BucketId.From(ids[2]), Owner, "Crypto", Currency.Pln, BucketKind.Investment, InvestmentProfile.Crypto, []).Value,
    });

    // Kind, a bucket, another bucket or "outside" (3), an amount in whole złoty, days back, a pick, an ID.
    private static readonly Gen<(Kind Kind, int Bucket, int Other, long Amount, int DaysBack, int Pick, Guid Id)> Attempts =
        Gen.Select(Gen.Enum<Kind>(), Gen.Int[0, 2], Gen.Int[0, 3], Gen.Long[1, 400], Gen.Int[0, 30], Gen.Int[0, 1000], Gen.Guid);

    private static readonly Gen<(Bucket[] Buckets, (Kind Kind, int Bucket, int Other, long Amount, int DaysBack, int Pick, Guid Id)[] Attempts)> Histories =
        Gen.Select(Buckets, Attempts.Array[1, 60]);

    [Fact]
    public void AcceptedJournals_KeepInvariant2_AndEqualTheStepByStepState() =>
        Histories.Sample(history =>
        {
            var journal = Accept(history.Buckets, history.Attempts);
            var replayed = BudgetState.Replay(history.Buckets, journal, Today);
            var stepByStep = ReplayOrder.Sort(Effective(journal)).Aggregate(BudgetState.Replay(history.Buckets, [], Today), (state, entry) => state.Apply(entry));

            return replayed.Balances.All(balance => balance.Available == balance.NetContributed - balance.OutstandingPrincipal)
                && replayed.Balances.SequenceEqual(stepByStep.Balances)
                && replayed.Loans.SequenceEqual(stepByStep.Loans)
                && replayed.FirstOverdraft is null
                && replayed.FirstOverRepayment is null;
        });

    // Invariant 4, checked without BudgetState: a running outstanding principal per loan over the effective journal in
    // replay order never goes below zero, no repayment comes before its borrow, and the end agrees with the state.
    [Fact]
    public void AcceptedJournals_NeverRepayMoreThanIsOutstanding_AtAnyPoint() =>
        Histories.Sample(history =>
        {
            var journal = Accept(history.Buckets, history.Attempts);
            var outstanding = new Dictionary<Guid, decimal>();
            var lentBy = history.Buckets.ToDictionary(bucket => bucket.BucketId, _ => 0m);

            foreach (var entry in Effective(journal).OrderBy(entry => entry.OccurredOn).ThenBy(entry => entry.RecordedAt).ThenBy(entry => entry.Id.ToString("N"), StringComparer.Ordinal))
            {
                switch (entry)
                {
                    case Borrow borrow:
                        outstanding[borrow.Id] = borrow.Principal.Amount;
                        lentBy[borrow.LenderBucketId] += borrow.Principal.Amount;
                        break;
                    case Repayment repayment:
                        if (!outstanding.TryGetValue(repayment.LoanId.Value, out var left) || left < repayment.Amount.Amount)
                        {
                            return false;
                        }

                        outstanding[repayment.LoanId.Value] = left - repayment.Amount.Amount;
                        break;
                }
            }

            var state = BudgetState.Replay(history.Buckets, journal, Today);
            return state.Loans.Count == outstanding.Count
                && state.Loans.All(loan => loan.Outstanding.Amount == outstanding[loan.LoanId.Value]
                    && loan.Status == (outstanding[loan.LoanId.Value] == 0m ? LoanStatus.Closed : LoanStatus.Open))
                && history.Buckets.All(bucket => state.BalanceOf(bucket.BucketId).OutstandingPrincipal.Amount
                    == state.Loans.Where(loan => loan.LenderBucketId == bucket.BucketId).Sum(loan => loan.Outstanding.Amount));
        });

    // "Reversing restores the earlier state exactly", with loans: balances, loans and both invariant checks.
    [Fact]
    public void ReversingAnyEntry_EqualsTheJournalWithoutIt() =>
        Gen.Select(Histories, Gen.Int[0, 1000]).Sample((history, pick) =>
        {
            var journal = Accept(history.Buckets, history.Attempts);
            var candidates = Effective(journal).ToList();
            if (candidates.Count == 0)
            {
                return true;
            }

            var target = candidates[pick % candidates.Count];
            var reversal = Reversal.Create(JournalEntryId.From(Guid.CreateVersion7()), Owner, target.EntryId, Today, Today, EntryInfo.None, FakeClock.DefaultStart.AddDays(1)).Value;

            var withReversal = BudgetState.Replay(history.Buckets, [.. journal, reversal], Today);
            var without = BudgetState.Replay(history.Buckets, [.. journal.Where(entry => entry != target)], Today);

            return withReversal.Balances.SequenceEqual(without.Balances)
                && withReversal.Loans.SequenceEqual(without.Loans)
                && withReversal.FirstOverRepayment == without.FirstOverRepayment
                && withReversal.FirstOverdraft == without.FirstOverdraft;
        });

    // A borrow into a bucket moves money between two buckets of one currency, so the total is unchanged; a borrow to the
    // outside lowers it by the principal; a repayment from outside raises it by the amount.
    [Fact]
    public void Totals_MoveOnlyWithMoneyThatLeavesOrEntersTheApp() =>
        Gen.Select(Histories, Gen.Long[1, 50], Gen.Int[0, 1], Gen.Bool).Sample((history, units, destination, outside) =>
        {
            var buckets = history.Buckets;
            var journal = Accept(buckets, history.Attempts);
            var amount = Money.From(units, Currency.Pln);
            var before = Total(BudgetState.Replay(buckets, journal, Today));

            var borrow = Borrow.Create(JournalEntryId.From(Guid.CreateVersion7()), Owner, buckets[0].BucketId, amount, outside ? null : buckets[1 + destination].BucketId,
                Today, Today, EntryInfo.None, FakeClock.DefaultStart.AddDays(1)).Value;
            var afterBorrow = Total(BudgetState.Replay(buckets, [.. journal, borrow], Today));

            var repayment = Repayment.Create(JournalEntryId.From(Guid.CreateVersion7()), Owner, borrow.LoanId, amount, null, Today, Today, EntryInfo.None,
                FakeClock.DefaultStart.AddDays(1).AddSeconds(1)).Value;
            var afterRepayment = Total(BudgetState.Replay(buckets, [.. journal, borrow, repayment], Today));

            return afterBorrow == (outside ? before - units : before)
                && afterRepayment == afterBorrow + units;
        });

    // The decision the handlers make: the entry's own rules, then no overdraft and no over-repayment with it.
    private static List<JournalEntry> Accept(Bucket[] buckets, (Kind Kind, int Bucket, int Other, long Amount, int DaysBack, int Pick, Guid Id)[] attempts)
    {
        var journal = new List<JournalEntry>();
        for (var index = 0; index < attempts.Length; index++)
        {
            var (kind, bucketIndex, other, units, daysBack, pick, id) = attempts[index];
            var bucket = buckets[bucketIndex];
            var target = other < 3 && other != bucketIndex ? buckets[other].BucketId : (BucketId?)null;
            var entryId = JournalEntryId.From(id);
            var amount = Money.From(units, Currency.Pln);
            var occurredOn = Today.AddDays(-daysBack);
            var recordedAt = FakeClock.DefaultStart.AddSeconds(index);
            var effective = Effective(journal).ToList();

            JournalEntry? entry = kind switch
            {
                Kind.Deposit => Deposit.Create(entryId, Owner, bucket.BucketId, amount, occurredOn, Today, EntryInfo.None, recordedAt).Value,
                Kind.Spend when bucket.Kind == BucketKind.Savings => Spend.Create(entryId, Owner, bucket.BucketId, amount, occurredOn, Today, EntryInfo.None, recordedAt).Value,
                Kind.Borrow when bucket.Kind == BucketKind.Savings => Borrow.Create(entryId, Owner, bucket.BucketId, amount, target, occurredOn, Today, EntryInfo.None, recordedAt).Value,
                Kind.Repay when effective.OfType<Borrow>().ToList() is { Count: > 0 } borrows
                    && borrows[pick % borrows.Count] is var loan
                    && target != loan.LenderBucketId =>
                    Repayment.Create(entryId, Owner, loan.LoanId, amount, target, occurredOn, Today, EntryInfo.None, recordedAt).Value,
                Kind.Reverse when effective.Count > 0 && effective[pick % effective.Count] is var reversed
                    && !(reversed is Borrow borrowed && effective.OfType<Repayment>().Any(repayment => repayment.LoanId == borrowed.LoanId)) =>
                    Reversal.Create(entryId, Owner, reversed.EntryId, Today, Today, EntryInfo.None, recordedAt).Value,
                _ => null,
            };

            if (entry is null)
            {
                continue;
            }

            var state = BudgetState.Replay(buckets, [.. journal, entry], Today);
            if (state.FirstOverdraft is null && state.FirstOverRepayment is null)
            {
                journal.Add(entry);
            }
        }

        return journal;
    }

    private static IEnumerable<JournalEntry> Effective(List<JournalEntry> journal)
    {
        var reversed = BudgetState.ReversedEntryIds(journal);
        return journal.Where(entry => entry is not Reversal && !reversed.Contains(entry.Id));
    }

    private static decimal Total(BudgetState state) => state.Balances.Sum(balance => balance.Available.Amount);
}
