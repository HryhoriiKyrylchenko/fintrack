using CsCheck;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Invariant 1 (ARCHITECTURE 7) for arbitrary, backdated sequences of opening balances, deposits,
/// spends, transfers (to the next bucket, sometimes with a fee) and reversals over savings and investment buckets: each entry is accepted only when the replay with it has
/// no overdraft (the domain's check), and then an independent running sum confirms that no savings
/// bucket is below zero after any entry of the replay order.
/// </summary>
public class NeverNegativePropertyTests
{
    private static readonly OwnerId Owner = OwnerId.From(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly EntryInfo Reason = new("Counted again", [], EntrySource.Manual);

    private enum Kind
    {
        Opening,
        Deposit,
        Spend,
        Transfer,
        Reversal,
        AdjustUp,
        AdjustDown,
    }

    // Two savings buckets and one investment bucket, all in PLN.
    private static readonly Gen<Bucket[]> Buckets = Gen.Guid.Array[3].Select(ids => new[]
    {
        Bucket.Create(BucketId.From(ids[0]), Owner, "Cushion", Currency.Pln, BucketKind.Savings, null, []).Value,
        Bucket.Create(BucketId.From(ids[1]), Owner, "Holiday", Currency.Pln, BucketKind.Savings, null, []).Value,
        Bucket.Create(BucketId.From(ids[2]), Owner, "Crypto", Currency.Pln, BucketKind.Investment, InvestmentProfile.Crypto, []).Value,
    });

    // An attempted entry: kind, bucket, amount (whole złoty up to 500, so spends often exceed what is
    // there), how many days back (dates often earlier than entries already made), and an ID.
    private static readonly Gen<(Kind Kind, int Bucket, long Amount, int DaysBack, Guid Id)> Attempts =
        Gen.Select(Gen.Enum<Kind>(), Gen.Int[0, 2], Gen.Long[1, 500], Gen.Int[0, 30], Gen.Guid);

    private static readonly Gen<(Bucket[] Buckets, (Kind Kind, int Bucket, long Amount, int DaysBack, Guid Id)[] Attempts)> Histories =
        Gen.Select(Buckets, Attempts.Array[1, 60]);

    [Fact]
    public void AcceptedJournals_NeverHaveASavingsBucketBelowZero_AtAnyPoint() =>
        Histories.Sample(history =>
        {
            var journal = Accept(history.Buckets, history.Attempts);
            return NeverBelowZero(history.Buckets, journal);
        });

    [Fact]
    public void AcceptedJournals_AreConsistentWithInvariant2_AndTheStepByStepState() =>
        Histories.Sample(history =>
        {
            var journal = Accept(history.Buckets, history.Attempts);
            var replayed = BudgetState.Replay(history.Buckets, journal, Today);
            // Step by step over the effective journal: what is left after reversals (D3).
            var reversed = BudgetState.ReversedEntryIds(journal);
            var effective = journal.Where(entry => entry is not Reversal && !reversed.Contains(entry.Id));
            var stepByStep = ReplayOrder.Sort(effective).Aggregate(BudgetState.Replay(history.Buckets, [], Today), (state, entry) => state.Apply(entry));

            return replayed.Balances.SequenceEqual(stepByStep.Balances)
                && replayed.Balances.All(balance => balance.Available == balance.NetContributed - balance.OutstandingPrincipal)
                && replayed.FirstOverdraft is null;
        });

    // The decision the handlers make: the type's own rules, then "no overdraft with this entry".
    private static List<JournalEntry> Accept(Bucket[] buckets, (Kind Kind, int Bucket, long Amount, int DaysBack, Guid Id)[] attempts)
    {
        var journal = new List<JournalEntry>();
        for (var index = 0; index < attempts.Length; index++)
        {
            var (kind, bucketIndex, units, daysBack, id) = attempts[index];
            var bucket = buckets[bucketIndex];
            var state = BudgetState.Replay(buckets, journal, Today);

            if (kind == Kind.Reversal)
            {
                // Reverse one of the accepted entries that is not a reversal and not reversed yet.
                var reversed = BudgetState.ReversedEntryIds(journal);
                var candidates = journal.Where(entry => entry is not Reversal && !reversed.Contains(entry.Id)).ToList();
                if (candidates.Count > 0)
                {
                    var target = candidates[(int)(units % candidates.Count)];
                    var reversal = Reversal.Create(JournalEntryId.From(id), Owner, target.EntryId, Today, Today, EntryInfo.None, FakeClock.DefaultStart.AddSeconds(index)).Value;
                    if (BudgetState.Replay(buckets, [.. journal, reversal], Today).FirstOverdraft is null)
                    {
                        journal.Add(reversal);
                    }
                }

                continue;
            }

            if ((kind == Kind.Spend && bucket.Kind == BucketKind.Investment)
                || (kind == Kind.Opening && state.BalanceOf(bucket.BucketId).HasOpeningBalance))
            {
                continue;
            }

            var amount = Money.From(units, bucket.Currency);
            var occurredOn = Today.AddDays(-daysBack);
            var recordedAt = FakeClock.DefaultStart.AddSeconds(index);
            var entryId = JournalEntryId.From(id);
            // A transfer goes to the next bucket; every third one carries a fee of 1 złoty.
            var next = buckets[(bucketIndex + 1) % buckets.Length];
            var fee = units > 1 && index % 3 == 0 ? Money.From(1m, bucket.Currency) : null;
            JournalEntry entry = kind switch
            {
                Kind.Opening => OpeningBalance.Create(entryId, Owner, bucket.BucketId, amount, occurredOn, Today, EntryInfo.None, recordedAt).Value,
                Kind.Deposit => Deposit.Create(entryId, Owner, bucket.BucketId, amount, occurredOn, Today, EntryInfo.None, recordedAt).Value,
                Kind.Spend => Spend.Create(entryId, Owner, bucket.BucketId, amount, occurredOn, Today, EntryInfo.None, recordedAt).Value,
                Kind.AdjustUp => Adjustment.Create(entryId, Owner, bucket.BucketId, amount, AdjustmentDirection.Increase, occurredOn, Today, Reason, recordedAt).Value,
                Kind.AdjustDown => Adjustment.Create(entryId, Owner, bucket.BucketId, amount, AdjustmentDirection.Decrease, occurredOn, Today, Reason, recordedAt).Value,
                _ => Transfer.Create(entryId, Owner, bucket.BucketId, next.BucketId, amount, fee is null ? amount : amount - fee, fee, occurredOn, Today, EntryInfo.None, recordedAt).Value,
            };

            if (BudgetState.Replay(buckets, [.. journal, entry], Today).FirstOverdraft is null)
            {
                journal.Add(entry);
            }
        }

        return journal;
    }

    // An independent check: running sums per bucket over the replay order, written without BudgetState
    // (the "N" form of a GUID is its standard bytes in hex, so ordinal text order is byte order).
    private static bool NeverBelowZero(Bucket[] buckets, List<JournalEntry> journal)
    {
        var sums = buckets.ToDictionary(bucket => bucket.BucketId, _ => 0m);
        var savings = buckets.Where(bucket => bucket.Kind == BucketKind.Savings).Select(bucket => bucket.BucketId).ToHashSet();

        // Reversed entries count as if they never happened; reversals move no money.
        var reversedIds = journal.OfType<Reversal>().Select(reversal => reversal.ReversedEntryId.Value).ToHashSet();
        var effective = journal.Where(entry => entry is not Reversal && !reversedIds.Contains(entry.Id));

        foreach (var entry in effective.OrderBy(entry => entry.OccurredOn).ThenBy(entry => entry.RecordedAt).ThenBy(entry => entry.Id.ToString("N"), StringComparer.Ordinal))
        {
            switch (entry)
            {
                case Transfer transfer:
                    sums[transfer.FromBucketId] -= transfer.FromAmount.Amount;
                    sums[transfer.ToBucketId] += transfer.ToAmount.Amount;
                    break;
                case Spend spend:
                    sums[spend.BucketId] -= spend.Amount.Amount;
                    break;
                case Adjustment { Direction: AdjustmentDirection.Decrease } decrease:
                    sums[decrease.BucketId] -= decrease.Amount.Amount;
                    break;
                case SingleBucketEntry raise:
                    sums[raise.BucketId] += raise.Amount.Amount;
                    break;
            }

            if (savings.Any(bucket => sums[bucket] < 0m))
            {
                return false;
            }
        }

        return true;
    }
}
