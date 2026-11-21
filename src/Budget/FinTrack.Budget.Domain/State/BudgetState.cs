using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Budget.Domain.State;

/// <summary>
/// Budget's state for one owner, derived only by replaying the journal (ARCHITECTURE 2.3, invariant
/// 10). The one state builder of the context: handlers and queries read balances from here, so a
/// stored projection can later replace the replay without touching them. Pure: no clock, no I/O; today is a
/// parameter (plan 1b B20).
/// </summary>
/// <remarks>
/// <para>
/// Balances follow 4.3: <c>NetContributed</c> = opening balance + deposits + transfers in − spends −
/// transfers out + adjustments up − adjustments down; <c>OutstandingPrincipal</c> is 0 until loans exist
/// (phase 1b); <c>Available</c> = <c>NetContributed</c> − <c>OutstandingPrincipal</c>. Every amount stays in
/// its bucket's currency; currencies are never added together (invariant 11).
/// </para>
/// <para>
/// Invariant 1 holds at every point of the replay order (D2): each balance remembers the first date its
/// <c>Available</c> went below zero, and <see cref="FirstOverdraft"/> reports the earliest such date of a
/// savings bucket. A command checks it on the replay that includes its new entry; investment buckets may
/// go below zero (4.9).
/// </para>
/// <para>
/// Reversals (D3) are resolved before the replay: every reversed entry and every reversal are left out,
/// so a reversed entry counts as if it never happened at its original date (a reversed transfer voids
/// both sides). <see cref="Apply"/> is for the remaining, effective entries; a reversal applied there
/// changes nothing.
/// </para>
/// </remarks>
public sealed class BudgetState
{
    private readonly IReadOnlyList<BucketBalance> balances;

    private BudgetState(IReadOnlyList<BucketBalance> balances, DateOnly today)
    {
        this.balances = balances;
        Today = today;
    }

    /// <summary>One balance per bucket, in the order the buckets were given.</summary>
    public IReadOnlyList<BucketBalance> Balances => balances;

    /// <summary>
    /// Today in the owner's time zone: the day this state is for (plan 1b B20). It gives the current month that
    /// plan dues need (from step 9); no balance depends on it.
    /// </summary>
    public DateOnly Today { get; }

    /// <summary>
    /// The earliest point at which a savings bucket was below zero (the earliest date, then the first
    /// bucket in order), or <c>null</c> when none ever was. A journal that respects invariant 1 has none.
    /// </summary>
    public Overdraft? FirstOverdraft => balances
        .Where(balance => balance.Kind == BucketKind.Savings && balance.FirstNegativeOn is not null)
        .OrderBy(balance => balance.FirstNegativeOn)
        .Select(balance => new Overdraft(balance.BucketId, balance.FirstNegativeOn!.Value))
        .FirstOrDefault();

    /// <summary>
    /// The state after every entry of <paramref name="journal"/>, applied in replay order (the order
    /// they are given in does not matter), for <paramref name="today"/>.
    /// </summary>
    /// <param name="buckets">The owner's buckets, archived and system ones included.</param>
    /// <param name="journal">The owner's journal, in any order.</param>
    /// <param name="today">Today in the owner's time zone (from the clock and the settings, in Application).</param>
    /// <exception cref="InvalidOperationException">An entry refers to a bucket that is not among <paramref name="buckets"/> (a bug).</exception>
    public static BudgetState Replay(IEnumerable<Bucket> buckets, IEnumerable<JournalEntry> journal, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(journal);

        var entries = journal.ToList();
        var reversed = ReversedEntryIds(entries);
        var effective = entries.Where(entry => entry is not Reversal && !reversed.Contains(entry.Id));

        var state = new BudgetState([.. buckets.Select(BucketBalance.Empty)], today);
        return ReplayOrder.Sort(effective).Aggregate(state, (current, entry) => current.Apply(entry));
    }

    /// <summary>The IDs of the entries that a reversal in <paramref name="journal"/> cancels.</summary>
    public static IReadOnlySet<Guid> ReversedEntryIds(IEnumerable<JournalEntry> journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.OfType<Reversal>().Select(reversal => reversal.ReversedEntryId.Value).ToHashSet();
    }

    /// <summary>The state after one more entry. Apply entries in replay order; <see cref="Replay"/> does that.</summary>
    /// <exception cref="InvalidOperationException">The entry refers to an unknown bucket, or its type is not known here (bugs).</exception>
    public BudgetState Apply(JournalEntry entry) => entry switch
    {
        OpeningBalance opening => With(opening.BucketId, opening.OccurredOn, balance => balance with
        {
            NetContributed = balance.NetContributed + opening.Amount,
            HasOpeningBalance = true,
        }),
        Deposit deposit => With(deposit.BucketId, deposit.OccurredOn, balance => balance with
        {
            NetContributed = balance.NetContributed + deposit.Amount,
            Deposited = balance.Deposited + deposit.Amount,
        }),
        Spend spend => With(spend.BucketId, spend.OccurredOn, balance => balance with
        {
            NetContributed = balance.NetContributed - spend.Amount,
        }),
        Adjustment { Direction: AdjustmentDirection.Increase } increase => With(increase.BucketId, increase.OccurredOn, balance => balance with
        {
            NetContributed = balance.NetContributed + increase.Amount,
            AdjustedIn = balance.AdjustedIn + increase.Amount,
        }),
        Adjustment { Direction: AdjustmentDirection.Decrease } decrease => With(decrease.BucketId, decrease.OccurredOn, balance => balance with
        {
            NetContributed = balance.NetContributed - decrease.Amount,
            AdjustedOut = balance.AdjustedOut + decrease.Amount,
        }),
        Transfer transfer => With(transfer.FromBucketId, transfer.OccurredOn, balance => balance with
            {
                NetContributed = balance.NetContributed - transfer.FromAmount,
            })
            .With(transfer.ToBucketId, transfer.OccurredOn, balance => balance with
            {
                NetContributed = balance.NetContributed + transfer.ToAmount,
            }),
        Reversal => this,
        _ => throw new InvalidOperationException($"The state builder does not know the entry type {entry?.GetType().Name}."),
    };

    /// <summary>The balance of <paramref name="bucketId"/>.</summary>
    /// <exception cref="InvalidOperationException">The bucket is unknown (a bug).</exception>
    public BucketBalance BalanceOf(BucketId bucketId) =>
        balances.FirstOrDefault(balance => balance.BucketId == bucketId)
        ?? throw new InvalidOperationException($"Bucket {bucketId} is not part of this state.");

    // Applies a change to one bucket and records the first date its Available goes below zero.
    private BudgetState With(BucketId bucketId, DateOnly occurredOn, Func<BucketBalance, BucketBalance> change)
    {
        var index = -1;
        for (var position = 0; position < balances.Count; position++)
        {
            if (balances[position].BucketId == bucketId)
            {
                index = position;
                break;
            }
        }

        if (index < 0)
        {
            throw new InvalidOperationException($"An entry refers to bucket {bucketId}, which is not part of this state.");
        }

        var changed = balances.ToArray();
        var balance = change(changed[index]);
        if (balance.FirstNegativeOn is null && balance.Available.Amount < 0m)
        {
            balance = balance with { FirstNegativeOn = occurredOn };
        }

        changed[index] = balance;
        return new BudgetState(changed, Today);
    }
}

/// <summary>One bucket's balances (ARCHITECTURE 4.3), all in the bucket's currency.</summary>
/// <param name="BucketId">The bucket.</param>
/// <param name="Kind">Savings or investment (only savings buckets must never go below zero).</param>
/// <param name="NetContributed">Opening balance + deposits + transfers in − spends − transfers out + adjustments up − adjustments down.</param>
/// <param name="OutstandingPrincipal">Borrowed − repaid principal; 0 until loans exist (phase 1b).</param>
/// <param name="Deposited">The sum of deposits: the only income (invariant 5).</param>
/// <param name="AdjustedIn">The sum of adjustments up (plan 1b B14): never income, kept apart for the reports.</param>
/// <param name="AdjustedOut">The sum of adjustments down, above zero.</param>
/// <param name="HasOpeningBalance">Whether the bucket has an opening balance (D13: at most one).</param>
/// <param name="FirstNegativeOn">The first date in replay order on which <see cref="Available"/> was below zero, or <c>null</c>.</param>
public sealed record BucketBalance(
    BucketId BucketId,
    BucketKind Kind,
    Money NetContributed,
    Money OutstandingPrincipal,
    Money Deposited,
    Money AdjustedIn,
    Money AdjustedOut,
    bool HasOpeningBalance,
    DateOnly? FirstNegativeOn)
{
    /// <summary><c>NetContributed</c> − <c>OutstandingPrincipal</c>: what can be spent or moved.</summary>
    public Money Available => NetContributed - OutstandingPrincipal;

    /// <summary>All zero, in the bucket's currency.</summary>
    public static BucketBalance Empty(Bucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        var zero = Money.Zero(bucket.Currency);
        return new BucketBalance(bucket.BucketId, bucket.Kind, zero, zero, zero, zero, zero, HasOpeningBalance: false, FirstNegativeOn: null);
    }
}

/// <summary>A savings bucket below zero at some point of the replay order (invariant 1 broken).</summary>
/// <param name="BucketId">The bucket.</param>
/// <param name="Date">The first date on which it was below zero.</param>
public sealed record Overdraft(BucketId BucketId, DateOnly Date);
