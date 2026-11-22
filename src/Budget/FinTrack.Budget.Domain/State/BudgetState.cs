using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Loans;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;

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
/// transfers out + adjustments up − adjustments down + borrowed into the bucket − repayments paid from it;
/// <c>OutstandingPrincipal</c> = borrowed from the bucket − repaid to it; <c>Available</c> = <c>NetContributed</c> −
/// <c>OutstandingPrincipal</c> (invariant 2). Every amount stays in its bucket's currency; currencies are never added
/// together (invariant 11).
/// </para>
/// <para>
/// Loans (4.4, plan 1b B2, B3) are derived here, never stored: a <see cref="Borrow"/> opens one, each
/// <see cref="Repayment"/> lowers what is outstanding. Invariant 4 holds at every point of the replay order:
/// <see cref="FirstOverRepayment"/> reports the first point at which a repayment exceeded its loan's outstanding
/// principal, including a repayment replayed before its borrow (its lender is not known there, so only the source
/// changes).
/// </para>
/// <para>
/// Contribution plans (4.5, plan 1b B1, B7, B9) are derived here too: <see cref="Apply"/> records each definition entry and
/// each contribution linked to a plan, and <see cref="Plans"/> gathers them by plan ID, so a change replayed before its
/// <c>PlanCreated</c> (dated earlier) still belongs to its plan. What a plan accrues and owes in the current month is
/// <see cref="PlanSchedule"/>'s; <see cref="FirstPastPeriodChange"/> compares the past months of two states (invariant 6, B8).
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
    private readonly IReadOnlyList<Loan> loans;
    private readonly IReadOnlyList<JournalEntry> planEntries;
    private IReadOnlyList<Plan>? plans;

    private BudgetState(IReadOnlyList<BucketBalance> balances, IReadOnlyList<Loan> loans, IReadOnlyList<JournalEntry> planEntries, OverRepayment? firstOverRepayment, DateOnly today)
    {
        this.balances = balances;
        this.loans = loans;
        this.planEntries = planEntries;
        FirstOverRepayment = firstOverRepayment;
        Today = today;
    }

    /// <summary>One balance per bucket, in the order the buckets were given.</summary>
    public IReadOnlyList<BucketBalance> Balances => balances;

    /// <summary>Every loan opened by an effective borrow, open and closed, in the replay order of their borrows.</summary>
    public IReadOnlyList<Loan> Loans => loans;

    /// <summary>
    /// Every plan created by an effective <c>PlanCreated</c>, in the replay order of their creation, with its effective
    /// changes and linked contributions (whatever their dates).
    /// </summary>
    public IReadOnlyList<Plan> Plans => plans ??= BuildPlans(planEntries);

    /// <summary>The month of <see cref="Today"/>: the current month of every plan (plan 1b B6).</summary>
    public YearMonth CurrentMonth => YearMonth.FromDate(Today);

    /// <summary>
    /// The first point in replay order at which a repayment exceeded its loan's outstanding principal, or was replayed
    /// before its borrow (invariant 4), or <c>null</c>. A journal that respects invariant 4 has none.
    /// </summary>
    public OverRepayment? FirstOverRepayment { get; }

    /// <summary>
    /// Today in the owner's time zone: the day this state is for (plan 1b B20). It gives the current month that
    /// plan dues need (<see cref="CurrentMonth"/>); no balance depends on it.
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

        var state = new BudgetState([.. buckets.Select(BucketBalance.Empty)], [], [], firstOverRepayment: null, today);
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
            })
            .WithPlanEntry(deposit.PlanId is null ? null : deposit),
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
            })
            .WithPlanEntry(transfer.PlanId is null ? null : transfer),
        Borrow borrow => Borrowed(borrow),
        Repayment repayment => Repaid(repayment),
        PlanCreated or PlanAmountChanged or PlanEndChanged => WithPlanEntry(entry),
        Reversal => this,
        _ => throw new InvalidOperationException($"The state builder does not know the entry type {entry?.GetType().Name}."),
    };

    /// <summary>The balance of <paramref name="bucketId"/>.</summary>
    /// <exception cref="InvalidOperationException">The bucket is unknown (a bug).</exception>
    public BucketBalance BalanceOf(BucketId bucketId) =>
        balances.FirstOrDefault(balance => balance.BucketId == bucketId)
        ?? throw new InvalidOperationException($"Bucket {bucketId} is not part of this state.");

    /// <summary>The loan <paramref name="loanId"/>, or <c>null</c> when no effective borrow opened it (yet, in replay order).</summary>
    public Loan? FindLoan(LoanId loanId) => loans.FirstOrDefault(loan => loan.LoanId == loanId);

    /// <summary>The plan <paramref name="planId"/>, or <c>null</c> when no effective <c>PlanCreated</c> created it.</summary>
    public Plan? FindPlan(PlanId planId) => Plans.FirstOrDefault(plan => plan.PlanId == planId);

    /// <summary>Where <paramref name="plan"/> stands in <see cref="CurrentMonth"/>.</summary>
    public PlanStanding StandingOf(Plan plan) => PlanSchedule.Standing(plan, CurrentMonth);

    /// <summary>
    /// The first month before <see cref="CurrentMonth"/> in which a plan of this state accrues differently in
    /// <paramref name="after"/>, or <c>null</c> (invariant 6, plan 1b B8). A plan only in <paramref name="after"/> is new and
    /// has no periods to change; a plan missing there accrues nothing.
    /// </summary>
    public YearMonth? FirstPastPeriodChange(BudgetState after)
    {
        ArgumentNullException.ThrowIfNull(after);
        return PlanSchedule.FirstChangedPastMonth(Plans, after.Plans, CurrentMonth);
    }

    // A plan's creation, versions and end, and its contributions, gathered by plan ID. The entries are in replay order, so
    // the latest end and the later of two versions from the same month win. An entry of a plan that no effective
    // PlanCreated has is left out: the handlers never write one, and a plan with such entries cannot be reversed (B5).
    private static List<Plan> BuildPlans(IReadOnlyList<JournalEntry> entries)
    {
        var built = entries.OfType<PlanCreated>()
            .Select(created => new Plan(
                created.PlanId,
                created.BucketId,
                created.ValidFrom,
                created.ValidTo,
                created.OccurredOn,
                [new PlanVersion(created.ValidFrom, created.Amount)],
                Money.Zero(created.Amount.Currency)))
            .ToList();

        foreach (var entry in entries)
        {
            Func<Plan, Plan>? change = entry switch
            {
                PlanAmountChanged changed => plan => plan with { Versions = [.. plan.Versions, new PlanVersion(changed.From, changed.Amount)] },
                PlanEndChanged ended => plan => plan with { ValidTo = ended.ValidTo },
                Deposit deposit => plan => plan with { Contributed = plan.Contributed + deposit.Amount },
                Transfer transfer => plan => plan with { Contributed = plan.Contributed + transfer.ToAmount },
                _ => null,
            };

            var index = PlanOf(entry) is { } planId ? built.FindIndex(plan => plan.PlanId == planId) : -1;
            if (change is not null && index >= 0)
            {
                built[index] = change(built[index]);
            }
        }

        return built;
    }

    // The plan a recorded entry belongs to; a creation has no plan to change.
    private static PlanId? PlanOf(JournalEntry entry) => entry switch
    {
        PlanAmountChanged changed => changed.PlanId,
        PlanEndChanged ended => ended.PlanId,
        Deposit deposit => deposit.PlanId,
        Transfer transfer => transfer.PlanId,
        _ => null,
    };

    private BudgetState WithPlanEntry(JournalEntry? entry) =>
        entry is null ? this : new BudgetState(balances, loans, [.. planEntries, entry], FirstOverRepayment, Today);

    // A borrow: the lender lends (its Available falls), the destination receives like a transfer in, a loan opens.
    private BudgetState Borrowed(Borrow borrow)
    {
        var state = With(borrow.LenderBucketId, borrow.OccurredOn, balance => balance with
        {
            OutstandingPrincipal = balance.OutstandingPrincipal + borrow.Principal,
        });
        if (borrow.ToBucketId is { } destination)
        {
            state = state.With(destination, borrow.OccurredOn, balance => balance with
            {
                NetContributed = balance.NetContributed + borrow.Principal,
            });
        }

        var loan = new Loan(borrow.LoanId, borrow.LenderBucketId, borrow.ToBucketId, borrow.Principal, borrow.OccurredOn, Money.Zero(borrow.Principal.Currency), ClosedOn: null);
        return new BudgetState(state.balances, [.. state.loans, loan], state.planEntries, state.FirstOverRepayment, Today);
    }

    // A repayment: the source pays like a transfer out, the lender's outstanding principal falls, the loan closes at
    // zero. A repayment of a loan not opened yet in replay order, or one that goes below zero, breaks invariant 4.
    private BudgetState Repaid(Repayment repayment)
    {
        var state = this;
        if (repayment.FromBucketId is { } source)
        {
            state = state.With(source, repayment.OccurredOn, balance => balance with
            {
                NetContributed = balance.NetContributed - repayment.Amount,
            });
        }

        var index = state.IndexOfLoan(repayment.LoanId);
        if (index < 0)
        {
            return state.WithOverRepayment(repayment.LoanId, repayment.OccurredOn);
        }

        var loan = state.loans[index];
        var repaid = loan with { Repaid = loan.Repaid + repayment.Amount };
        repaid = repaid with { ClosedOn = repaid.Status == LoanStatus.Closed ? repayment.OccurredOn : null };

        state = state.With(loan.LenderBucketId, repayment.OccurredOn, balance => balance with
        {
            OutstandingPrincipal = balance.OutstandingPrincipal - repayment.Amount,
        });
        var changedLoans = state.loans.ToArray();
        changedLoans[index] = repaid;
        state = new BudgetState(state.balances, changedLoans, state.planEntries, state.FirstOverRepayment, Today);

        return repaid.Outstanding.Amount < 0m ? state.WithOverRepayment(repayment.LoanId, repayment.OccurredOn) : state;
    }

    private int IndexOfLoan(LoanId loanId)
    {
        for (var position = 0; position < loans.Count; position++)
        {
            if (loans[position].LoanId == loanId)
            {
                return position;
            }
        }

        return -1;
    }

    // Only the first break counts: entries are applied in replay order, so it is the earliest.
    private BudgetState WithOverRepayment(LoanId loanId, DateOnly date) =>
        FirstOverRepayment is null ? new BudgetState(balances, loans, planEntries, new OverRepayment(loanId, date), Today) : this;

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
        return new BudgetState(changed, loans, planEntries, FirstOverRepayment, Today);
    }
}

/// <summary>One bucket's balances (ARCHITECTURE 4.3), all in the bucket's currency.</summary>
/// <param name="BucketId">The bucket.</param>
/// <param name="Kind">Savings or investment (only savings buckets must never go below zero).</param>
/// <param name="NetContributed">Opening balance + deposits + transfers in − spends − transfers out + adjustments up − adjustments down + borrowed into the bucket − repayments paid from it.</param>
/// <param name="OutstandingPrincipal">Principal lent from this bucket and not repaid yet (plan 1b B3).</param>
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

/// <summary>A repayment above its loan's outstanding principal at some point of the replay order (invariant 4 broken).</summary>
/// <param name="LoanId">The loan.</param>
/// <param name="Date">The first date on which it happened.</param>
public sealed record OverRepayment(LoanId LoanId, DateOnly Date);
