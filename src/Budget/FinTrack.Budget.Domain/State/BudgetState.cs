using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Budget.Domain.State;

/// <summary>
/// Budget's state for one owner, derived only by replaying the journal (ARCHITECTURE 2.3, invariant
/// 10). The one state builder of the context: handlers and queries read balances from here, so a
/// stored projection can later replace the replay without touching them. Pure: no clock, no I/O.
/// </summary>
/// <remarks>
/// Balances follow 4.3: <c>NetContributed</c> = opening balance + deposits + transfers in − spends −
/// transfers out (phase 1a step 6: deposits only); <c>OutstandingPrincipal</c> is 0 until loans exist
/// (phase 1b); <c>Available</c> = <c>NetContributed</c> − <c>OutstandingPrincipal</c>. Every amount stays in
/// its bucket's currency; currencies are never added together (invariant 11).
/// </remarks>
public sealed class BudgetState
{
    private readonly IReadOnlyList<BucketBalance> balances;

    private BudgetState(IReadOnlyList<BucketBalance> balances) => this.balances = balances;

    /// <summary>One balance per bucket, in the order the buckets were given.</summary>
    public IReadOnlyList<BucketBalance> Balances => balances;

    /// <summary>
    /// The state after every entry of <paramref name="journal"/>, applied in replay order (the order
    /// they are given in does not matter).
    /// </summary>
    /// <exception cref="InvalidOperationException">An entry refers to a bucket that is not among <paramref name="buckets"/> (a bug).</exception>
    public static BudgetState Replay(IEnumerable<Bucket> buckets, IEnumerable<JournalEntry> journal)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(journal);

        var state = new BudgetState([.. buckets.Select(BucketBalance.Empty)]);
        return ReplayOrder.Sort(journal).Aggregate(state, (current, entry) => current.Apply(entry));
    }

    /// <summary>The state after one more entry. Apply entries in replay order; <see cref="Replay"/> does that.</summary>
    /// <exception cref="InvalidOperationException">The entry refers to an unknown bucket, or its type is not known here (bugs).</exception>
    public BudgetState Apply(JournalEntry entry) => entry switch
    {
        Deposit deposit => With(deposit.BucketId, balance => balance with
        {
            NetContributed = balance.NetContributed + deposit.Amount,
            Deposited = balance.Deposited + deposit.Amount,
        }),
        _ => throw new InvalidOperationException($"The state builder does not know the entry type {entry?.GetType().Name}."),
    };

    /// <summary>The balance of <paramref name="bucketId"/>.</summary>
    /// <exception cref="InvalidOperationException">The bucket is unknown (a bug).</exception>
    public BucketBalance BalanceOf(BucketId bucketId) =>
        balances.FirstOrDefault(balance => balance.BucketId == bucketId)
        ?? throw new InvalidOperationException($"Bucket {bucketId} is not part of this state.");

    private BudgetState With(BucketId bucketId, Func<BucketBalance, BucketBalance> change)
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
        changed[index] = change(changed[index]);
        return new BudgetState(changed);
    }
}

/// <summary>One bucket's balances (ARCHITECTURE 4.3), all in the bucket's currency.</summary>
/// <param name="BucketId">The bucket.</param>
/// <param name="NetContributed">Opening balance + deposits + transfers in − spends − transfers out.</param>
/// <param name="OutstandingPrincipal">Borrowed − repaid principal; 0 until loans exist (phase 1b).</param>
/// <param name="Deposited">The sum of deposits: the only income (invariant 5).</param>
public sealed record BucketBalance(BucketId BucketId, Money NetContributed, Money OutstandingPrincipal, Money Deposited)
{
    /// <summary><c>NetContributed</c> − <c>OutstandingPrincipal</c>: what can be spent or moved.</summary>
    public Money Available => NetContributed - OutstandingPrincipal;

    /// <summary>All zero, in the bucket's currency.</summary>
    public static BucketBalance Empty(Bucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        var zero = Money.Zero(bucket.Currency);
        return new BucketBalance(bucket.BucketId, zero, zero, zero);
    }
}
