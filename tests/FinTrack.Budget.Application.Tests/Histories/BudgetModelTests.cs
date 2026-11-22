using CsCheck;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Loans;
using FinTrack.Budget.Application.Overview;
using FinTrack.Budget.Application.Plans;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Loans;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;
using FluentResults;
using Contract = FinTrack.Budget.Contracts.V1.Buckets;
using GetBudgetOverview = FinTrack.Budget.Contracts.V1.Overview.GetBudgetOverview;
using GetJournal = FinTrack.Budget.Contracts.V1.Journal.GetJournal;
using GetLoans = FinTrack.Budget.Contracts.V1.Loans.GetLoans;

namespace FinTrack.Budget.Application.Tests.Histories;

/// <summary>
/// The model test of plan 1a step 10: random sequences of every Budget command (buckets in three
/// currencies, opening balances, deposits, spends, adjustments up and down (plan 1b step 4), borrows and repayments (step 5), transfers
/// with and without fee and across currencies, contribution plans with versions and ends and deposits and transfers linked to them
/// (step 9), reversals, archiving, unarchiving (plan 1b step 2), renaming, backdating, and repeats of earlier commands) run through
/// the real handlers. After every command and at the end, invariants 1, 2, 4, 5, 6, 10, 11 and 13 must hold. At the
/// end, the read queries (step 12) must agree with the state: the overview's balances and totals, and the
/// journal read page by page; and every plan's dues must equal an independent month-by-month sum.
/// </summary>
public class BudgetModelTests
{
    private static readonly Currency[] Currencies = [Currency.Pln, Currency.Eur, Currency.Usd];

    // The fake clock starts at 2026-01-15 09:30 UTC; the owner is in Warsaw.
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly YearMonth CurrentMonth = YearMonth.FromDate(Today);

    private enum Kind
    {
        CreateBucket,
        OpeningBalance,
        Deposit,
        Spend,
        Adjust,
        Borrow,
        Repay,
        Transfer,
        CreatePlan,
        ChangePlanAmount,
        SetPlanEnd,
        Reverse,
        Archive,
        Unarchive,
        Rename,
        Repeat,
    }

    /// <summary>
    /// One generated operation; its numbers are interpreted against the state when it runs. A negative
    /// <c>DaysBack</c> dates the entry tomorrow, which every entry type must reject.
    /// </summary>
    private sealed record Operation(Kind Kind, int A, int B, long Units, long OtherUnits, int DaysBack, int Fee, bool Unallocated, Guid IdSeed);

    private static readonly Gen<Operation> Operations =
        Gen.Select(
            Gen.Select(Gen.Enum<Kind>(), Gen.Int[0, 50], Gen.Int[0, 50], Gen.Long[1, 30_000], Gen.Long[1, 30_000], Gen.Int[-1, 60], Gen.Int[0, 3], Gen.Bool,
                (kind, a, b, units, otherUnits, daysBack, fee, unallocated) => (kind, a, b, units, otherUnits, daysBack, fee, unallocated)),
            Gen.Guid,
            (parts, id) => new Operation(parts.kind, parts.a, parts.b, parts.units, parts.otherUnits, parts.daysBack, parts.fee, parts.unallocated, id));

    private static readonly Gen<Operation[]> Histories = Operations.Array[1, 50];

    [Fact]
    public void EveryInvariantHolds_AfterAnyHistory() =>
        Histories.Sample(operations =>
        {
            var run = new ModelRun();
            foreach (var operation in operations)
            {
                run.Execute(operation);
            }

            run.CheckInvariants();
        });

    [Fact]
    public void Histories_ExerciseEveryCommand_BothAcceptedAndRejected()
    {
        var accepted = new HashSet<Kind>();
        var rejected = new HashSet<Kind>();

        Histories.Sample(
            operations =>
            {
                var run = new ModelRun();
                foreach (var operation in operations)
                {
                    var (kind, result) = run.Execute(operation);

                    // CsCheck runs samples on several threads.
                    lock (accepted)
                    {
                        (result.IsSuccess ? accepted : rejected).Add(kind);
                    }
                }
            },
            iter: 300);

        // Every kind is accepted somewhere, and every kind that can break a rule is rejected somewhere,
        // so the invariants above are checked against real successes and real refusals of each command.
        accepted.ShouldBe(Enum.GetValues<Kind>(), ignoreOrder: true);
        Enum.GetValues<Kind>().Where(kind => kind != Kind.Repeat).ShouldAllBe(kind => rejected.Contains(kind));
    }

    /// <summary>One owner's history, run through the real handlers against <see cref="InMemoryBudget"/>.</summary>
    private sealed class ModelRun
    {
        private readonly InMemoryBudget budget = new();
        private readonly FakeClock clock = new();
        private readonly FakeOwnerContext owner = new();
        private readonly List<object> succeeded = [];

        public ModelRun()
        {
            var saved = Send(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"));
            saved.IsSuccess.ShouldBeTrue();
        }

        public (Kind Kind, Result Result) Execute(Operation operation)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var command = Interpret(operation);

            // Only the very command object taken from the history is a repeat (commands are records, so an
            // equal new command, for example the same rename after another one, is not); with nothing to
            // repeat, the model creates a bucket instead.
            var isRepeat = operation.Kind == Kind.Repeat && succeeded.Exists(earlier => ReferenceEquals(earlier, command));

            var version = budget.Version;
            var entries = budget.Journal.Count;
            var buckets = budget.Buckets.ToList();
            var journalBefore = budget.Journal.ToList();

            var result = Send(command);

            if (isRepeat)
            {
                // Invariant 13: a repeated command never writes. It succeeds, except a bucket's creation
                // repeated after the bucket was renamed: its values differ now, so it is bucket.id_reused (step 5).
                budget.Version.ShouldBe(version, "a repeat wrote something");
                if (command is CreateBucket create && buckets.Single(bucket => bucket.Id == create.Id).Name != create.Name)
                {
                    result.Errors.ShouldHaveSingleItem().Message.ShouldBe(Domain.BudgetErrorCodes.BucketIdReused);
                }
                else
                {
                    result.IsSuccess.ShouldBeTrue($"a repeat of {command.GetType().Name} failed: {string.Join(", ", result.Errors.Select(error => error.Message))}");
                }
            }
            else if (result.IsFailed)
            {
                // A rejected command writes nothing.
                budget.Version.ShouldBe(version);
                budget.Journal.Count.ShouldBe(entries);
                budget.Buckets.ShouldBe(buckets);
            }
            else
            {
                succeeded.Add(command);
                CheckPastMonthsUnchanged(command, journalBefore);
            }

            return (operation.Kind, result);
        }

        // Invariant 6, checked independently: an accepted plan command, or the reversal of a plan entry, leaves what every plan
        // that existed before accrued in each month before the current one unchanged. Money entries are not held to it (B8).
        private void CheckPastMonthsUnchanged(object command, List<JournalEntry> journalBefore)
        {
            var isDefinitionChange = command is CreatePlan or ChangePlanAmount or SetPlanEnd
                || (command is ReverseEntry reverse && journalBefore.Exists(entry => entry.Id == reverse.ReversedEntryId && entry is IDefinitionEntry));
            if (!isDefinitionChange)
            {
                return;
            }

            var before = Accruals(journalBefore, CurrentMonth.Previous());
            var after = Accruals([.. budget.Journal], CurrentMonth.Previous());
            var plansBefore = before.Keys.Select(key => key.Plan).ToHashSet();
            foreach (var key in before.Keys.Union(after.Keys).Where(key => plansBefore.Contains(key.Plan)))
            {
                after.GetValueOrDefault(key).ShouldBe(before.GetValueOrDefault(key), $"plan {key.Plan} changed in {key.Month}");
            }
        }

        // What each effective plan accrues in each month through "last", rebuilt from the entries without BudgetState: versions
        // and ends in replay order, the version with the latest "from" at or before the month (later on a tie).
        private static Dictionary<(Guid Plan, YearMonth Month), decimal> Accruals(List<JournalEntry> journal, YearMonth last)
        {
            var reversed = BudgetState.ReversedEntryIds(journal);
            var effective = ReplayOrder.Sort(journal.Where(entry => entry is not Reversal && !reversed.Contains(entry.Id)));
            var accruals = new Dictionary<(Guid, YearMonth), decimal>();
            foreach (var created in effective.OfType<PlanCreated>())
            {
                var versions = new List<(YearMonth From, decimal Amount)> { (created.ValidFrom, created.Amount.Amount) };
                var validTo = created.ValidTo;
                foreach (var entry in effective)
                {
                    if (entry is PlanAmountChanged changed && changed.PlanId == created.PlanId)
                    {
                        versions.Add((changed.From, changed.Amount.Amount));
                    }
                    else if (entry is PlanEndChanged ended && ended.PlanId == created.PlanId)
                    {
                        validTo = ended.ValidTo;
                    }
                }

                for (var month = created.ValidFrom; month <= last; month = month.Next())
                {
                    decimal amount = 0m;
                    YearMonth? inForce = null;
                    foreach (var (from, value) in versions)
                    {
                        if (from <= month && (inForce is null || from >= inForce))
                        {
                            inForce = from;
                            amount = value;
                        }
                    }

                    accruals[(created.Id, month)] = validTo is { } end && month > end ? 0m : amount;
                }
            }

            return accruals;
        }

        public void CheckInvariants()
        {
            var bucketList = budget.Buckets.ToList();
            var journal = budget.Journal.ToList();
            var state = BudgetState.Replay(bucketList, journal, Today);

            var reversed = BudgetState.ReversedEntryIds(journal);
            var effective = journal.Where(entry => entry is not Reversal && !reversed.Contains(entry.Id)).ToList();

            // Invariants 1 and 4, checked independently: running sums of Available per bucket and of the outstanding
            // principal per loan over the effective journal in replay order.
            var sums = bucketList.ToDictionary(bucket => bucket.BucketId, _ => 0m);
            var outstanding = new Dictionary<Guid, decimal>();
            var borrows = journal.OfType<Borrow>().ToDictionary(borrow => borrow.Id);
            foreach (var entry in ReplayOrder.Sort(effective))
            {
                foreach (var (bucketId, change) in Changes(entry, borrows))
                {
                    sums[bucketId] += change;
                }

                bucketList.Where(bucket => bucket.Kind == BucketKind.Savings)
                    .ShouldAllBe(bucket => sums[bucket.BucketId] >= 0m, "a savings bucket went below zero");

                if (entry is Borrow borrow)
                {
                    outstanding[borrow.Id] = borrow.Principal.Amount;
                }
                else if (entry is Repayment repayment)
                {
                    outstanding.ShouldContainKey(repayment.LoanId.Value, "a repayment came before its borrow");
                    outstanding[repayment.LoanId.Value] -= repayment.Amount.Amount;
                    outstanding[repayment.LoanId.Value].ShouldBeGreaterThanOrEqualTo(0m, "a repayment exceeded the outstanding principal");
                }
            }

            state.FirstOverRepayment.ShouldBeNull();
            state.Loans.Select(loan => (loan.LoanId.Value, loan.Outstanding.Amount)).ShouldBe(outstanding.Select(pair => (pair.Key, pair.Value)), ignoreOrder: true);
            bucketList.ShouldAllBe(bucket => state.BalanceOf(bucket.BucketId).OutstandingPrincipal.Amount
                == state.Loans.Where(loan => loan.LenderBucketId == bucket.BucketId).Sum(loan => loan.Outstanding.Amount));

            // Invariant 5, checked independently: income is the effective deposits only (adjustments, opening balances and
            // transfers never count), and adjustments are tracked apart by direction.
            foreach (var bucket in bucketList)
            {
                var balance = state.BalanceOf(bucket.BucketId);
                balance.Deposited.Amount.ShouldBe(effective.OfType<Deposit>().Where(deposit => deposit.BucketId == bucket.BucketId).Sum(deposit => deposit.Amount.Amount));
                balance.AdjustedIn.Amount.ShouldBe(effective.OfType<Adjustment>()
                    .Where(adjustment => adjustment.BucketId == bucket.BucketId && adjustment.Direction == AdjustmentDirection.Increase).Sum(adjustment => adjustment.Amount.Amount));
                balance.AdjustedOut.Amount.ShouldBe(effective.OfType<Adjustment>()
                    .Where(adjustment => adjustment.BucketId == bucket.BucketId && adjustment.Direction == AdjustmentDirection.Decrease).Sum(adjustment => adjustment.Amount.Amount));
            }

            // Invariant 2.
            state.Balances.ShouldAllBe(balance => balance.Available == balance.NetContributed - balance.OutstandingPrincipal);
            state.FirstOverdraft.ShouldBeNull();

            // Invariant 10: the replay equals the step-by-step build and does not depend on the stored order.
            var stepByStep = ReplayOrder.Sort(effective).Aggregate(BudgetState.Replay(bucketList, [], Today), (current, entry) => current.Apply(entry));
            state.Balances.ShouldBe(stepByStep.Balances);
            BudgetState.Replay(bucketList, Enumerable.Reverse(journal), Today).Balances.ShouldBe(state.Balances);

            // Invariant 11: per-currency totals equal the independent sums; no currency is added to another.
            foreach (var bucket in bucketList)
            {
                state.BalanceOf(bucket.BucketId).Available.Amount.ShouldBe(sums[bucket.BucketId]);
                state.BalanceOf(bucket.BucketId).Available.Currency.ShouldBe(bucket.Currency);
            }

            // Archived buckets are empty, and nothing references a bucket that does not exist.
            bucketList.Where(bucket => bucket.Status == BucketStatus.Archived)
                .ShouldAllBe(bucket => sums[bucket.BucketId] == 0m);

            // Invariant 8 for plans: an archived bucket has no active plan.
            bucketList.Where(bucket => bucket.Status == BucketStatus.Archived)
                .ShouldAllBe(bucket => !state.Plans.Any(plan => plan.BucketId == bucket.BucketId && state.StandingOf(plan).IsActive));

            CheckPlans(state, effective);
            CheckOverview(state, bucketList);
            CheckLoans(state, journal);
            CheckJournalPages(journal);
        }

        // Every plan's dues, checked independently: accrued through the current month (or its end) month by month, credit the
        // effective linked deposits and transfers in, DueNow, Shortfall and Prepaid from them (B7).
        private static void CheckPlans(BudgetState state, List<JournalEntry> effective)
        {
            var accruals = Accruals(effective, CurrentMonth);
            state.Plans.Select(plan => plan.PlanId.Value).ShouldBe(effective.OfType<PlanCreated>().Select(created => created.Id), ignoreOrder: true);
            foreach (var plan in state.Plans)
            {
                var standing = state.StandingOf(plan);
                var before = accruals.Where(pair => pair.Key.Plan == plan.PlanId.Value && pair.Key.Month < CurrentMonth).Sum(pair => pair.Value);
                var current = accruals.GetValueOrDefault((plan.PlanId.Value, CurrentMonth));
                var credit = effective.OfType<Deposit>().Where(deposit => deposit.PlanId == plan.PlanId).Sum(deposit => deposit.Amount.Amount)
                    + effective.OfType<Transfer>().Where(transfer => transfer.PlanId == plan.PlanId).Sum(transfer => transfer.ToAmount.Amount);

                standing.Credit.Amount.ShouldBe(credit);
                standing.Accrued.Amount.ShouldBe(before + current);
                standing.DueNow.Amount.ShouldBe(Math.Max(0m, before + current - credit));
                standing.Shortfall.Amount.ShouldBe(Math.Max(0m, before - credit));
                standing.Prepaid.Amount.ShouldBe(Math.Max(0m, credit - before - current));
            }
        }

        // The overview shows the replayed balances; each currency's total is the sum of its active buckets,
        // and the reporting total converts each subtotal once.
        private void CheckOverview(BudgetState state, List<Bucket> bucketList)
        {
            var rateDates = new Dictionary<Currency, DateOnly> { [Currency.Eur] = Today.AddDays(-1), [Currency.Usd] = Today.AddDays(-3) };
            var rates = new FakeFxRateProvider()
                .With(Currency.Eur, Currency.Pln, 4.2735m, rateDates[Currency.Eur])
                .With(Currency.Usd, Currency.Pln, 3.6125m, rateDates[Currency.Usd]);
            var overview = new GetBudgetOverviewHandler(new BudgetStateLoader(budget, budget), rates, clock)
                .Handle(new GetBudgetOverview(), TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult().Value;

            var active = bucketList.Where(bucket => bucket.Status == BucketStatus.Active).ToList();
            overview.Buckets.Select(summary => summary.Id).ShouldBe(active.OrderBy(bucket => bucket.CreatedAt).ThenBy(bucket => bucket.Id).Select(bucket => bucket.Id));
            foreach (var summary in overview.Buckets)
            {
                var balance = state.BalanceOf(BucketId.From(summary.Id));
                summary.Available.ShouldBe(balance.Available);
                summary.NetContributed.ShouldBe(balance.NetContributed);
                summary.OutstandingPrincipal.ShouldBe(balance.OutstandingPrincipal);
                summary.OpenLoans.ShouldBe(state.Loans.Count(loan => loan.LenderBucketId == balance.BucketId && loan.Status == LoanStatus.Open));
            }

            var expected = Currencies
                .Select(currency => (Currency: currency, Buckets: active.Where(bucket => bucket.Currency == currency).ToList()))
                .Where(group => group.Buckets.Count > 0)
                .ToDictionary(group => group.Currency, group => group.Buckets.Sum(bucket => state.BalanceOf(bucket.BucketId).Available.Amount));
            overview.Totals.Select(total => total.Currency).ShouldBe(expected.Keys, ignoreOrder: true);
            overview.Totals.ShouldAllBe(total => total.Available.Amount == expected[total.Currency] && total.Available.Currency == total.Currency);

            var converted = expected.Sum(pair => pair.Key == Currency.Pln
                ? pair.Value
                : decimal.Round(pair.Value * (pair.Key == Currency.Eur ? 4.2735m : 3.6125m), 2, MidpointRounding.ToEven));
            var used = expected.Keys.Where(currency => currency != Currency.Pln).Select(currency => rateDates[currency]).ToList();
            overview.ReportingTotal.Amount.ShouldBe(Money.From(converted, Currency.Pln));
            overview.ReportingTotal.RateDate.ShouldBe(used.Count > 0 ? used.Min() : null);
            overview.ReportingTotal.UnavailableReason.ShouldBeNull();
        }

        // The loan list is the state's loans, field by field, open ones alone by default; and each loan's journal filter is
        // its borrow, its repayments and their reversals.
        private void CheckLoans(BudgetState state, List<JournalEntry> journal)
        {
            var handler = new GetLoansHandler(new BudgetStateLoader(budget, budget), clock);
            var all = handler.Handle(new GetLoans(IncludeClosed: true), TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult().Value;
            var open = handler.Handle(new GetLoans(), TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult().Value;

            all.Select(loan => loan.Id).ShouldBe(state.Loans.Select(loan => loan.LoanId.Value));
            open.Select(loan => loan.Id).ShouldBe(state.Loans.Where(loan => loan.Status == LoanStatus.Open).Select(loan => loan.LoanId.Value));
            foreach (var (dto, loan) in all.Zip(state.Loans))
            {
                dto.LenderBucketId.ShouldBe(loan.LenderBucketId.Value);
                dto.ToBucketId.ShouldBe(loan.ToBucketId?.Value);
                dto.Principal.ShouldBe(loan.Principal);
                dto.BorrowedOn.ShouldBe(loan.BorrowedOn);
                dto.Repaid.ShouldBe(loan.Repaid);
                dto.Outstanding.ShouldBe(loan.Outstanding);
                dto.Status.ToString().ShouldBe(loan.Status.ToString());
                dto.ClosedOn.ShouldBe(loan.ClosedOn);

                var history = new GetJournalHandler(new BudgetStateLoader(budget, budget))
                    .Handle(new GetJournal(Limit: GetJournal.MaxLimit, LoanId: loan.LoanId.Value), TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult().Value;
                var expected = journal.Where(entry => entry.Id == loan.LoanId.Value
                    || (entry is Repayment repayment && repayment.LoanId == loan.LoanId)
                    || (entry is Reversal reversal && journal.Any(target => target.Id == reversal.ReversedEntryId.Value
                        && (target.Id == loan.LoanId.Value || (target is Repayment repaid && repaid.LoanId == loan.LoanId)))));
                history.Entries.Select(entry => entry.Id).ShouldBe(ReplayOrder.Sort(expected).Reverse().Select(entry => entry.Id));
            }
        }

        // Read page by page, the journal is every entry exactly once, newest first, and each reversed entry
        // names its reversal.
        private void CheckJournalPages(List<JournalEntry> journal)
        {
            var handler = new GetJournalHandler(new BudgetStateLoader(budget, budget));
            var read = new List<Contracts.V1.Journal.JournalEntryDto>();
            string? cursor = null;
            do
            {
                var page = handler.Handle(new GetJournal(Limit: 3, Cursor: cursor), TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult().Value;
                page.Entries.Count.ShouldBeLessThanOrEqualTo(3);
                read.AddRange(page.Entries);
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            read.Select(entry => entry.Id).ShouldBe(ReplayOrder.Sort(journal).Reverse().Select(entry => entry.Id));
            var reversals = journal.OfType<Reversal>().ToDictionary(reversal => reversal.ReversedEntryId.Value, reversal => reversal.Id);
            read.ShouldAllBe(entry => entry.ReversedBy == (reversals.ContainsKey(entry.Id) ? reversals[entry.Id] : null));
        }

        // How an effective entry changes Available, written without BudgetState; a repayment's lender is its borrow's.
        private static IEnumerable<(BucketId Bucket, decimal Change)> Changes(JournalEntry entry, Dictionary<Guid, Borrow> borrows) => entry switch
        {
            Spend spend => [(spend.BucketId, -spend.Amount.Amount)],
            Adjustment { Direction: AdjustmentDirection.Decrease } decrease => [(decrease.BucketId, -decrease.Amount.Amount)],
            SingleBucketEntry single => [(single.BucketId, single.Amount.Amount)],
            Transfer transfer => [(transfer.FromBucketId, -transfer.FromAmount.Amount), (transfer.ToBucketId, transfer.ToAmount.Amount)],
            Borrow borrow => [(borrow.LenderBucketId, -borrow.Principal.Amount), .. (borrow.ToBucketId is { } to ? [(to, borrow.Principal.Amount)] : Array.Empty<(BucketId, decimal)>())],
            Repayment repayment => [(borrows[repayment.LoanId.Value].LenderBucketId, repayment.Amount.Amount),
                .. (repayment.FromBucketId is { } from ? [(from, -repayment.Amount.Amount)] : Array.Empty<(BucketId, decimal)>())],
            PlanCreated or PlanAmountChanged or PlanEndChanged => [],
            _ => throw new InvalidOperationException($"The model does not know the entry type {entry.GetType().Name}."),
        };

        private object Interpret(Operation operation)
        {
            var id = Version7(operation.IdSeed);
            var occurredOn = Today.AddDays(-operation.DaysBack);
            var active = budget.Buckets.Where(bucket => bucket.Status == BucketStatus.Active).ToList();
            var all = budget.Buckets;

            switch (operation.Kind)
            {
                case Kind.CreateBucket:
                    var investment = operation.A % 4 == 0;
                    return new CreateBucket(id, $"Bucket {operation.B % 6}", Currencies[operation.A % 3],
                        investment ? Contract.BucketKind.Investment : Contract.BucketKind.Savings,
                        investment ? Contract.InvestmentProfile.Crypto : null);

                case Kind.OpeningBalance or Kind.Deposit or Kind.Spend:
                    var (bucketId, unallocated, currency) = Target(operation.Unallocated, operation.A, active, all);

                    // A deposit pays a plan: usually (Fee 1) into the plan's own active bucket, sometimes (Fee 2) naming a plan
                    // ever created, reversed ones included, at the generated target.
                    var plans = budget.Journal.OfType<PlanCreated>().ToList();
                    Guid? paidPlan = null;
                    if (operation.Kind == Kind.Deposit && operation.Fee is 1 or 2 && plans.Count > 0)
                    {
                        var plan = plans[operation.B % plans.Count];
                        paidPlan = plan.Id;
                        if (operation.Fee == 1 && active.Find(bucket => bucket.BucketId == plan.BucketId) is { } planBucket)
                        {
                            (bucketId, unallocated, currency) = (planBucket.Id, null, planBucket.Currency);
                        }
                    }

                    var amount = Money.From(operation.Units / 100m, currency);
                    return operation.Kind switch
                    {
                        Kind.OpeningBalance => new AddOpeningBalance(id, amount, occurredOn, bucketId, unallocated, null, []),
                        Kind.Deposit => new AddDeposit(id, amount, occurredOn, bucketId, unallocated, null, [], paidPlan),
                        _ => new AddSpend(id, amount, occurredOn, bucketId, unallocated, null, []),
                    };

                case Kind.Adjust:
                    // Up or down on any active bucket or Unallocated, always with its reason (the handlers run without the
                    // validator, which rejects a missing note before the domain sees it).
                    var adjusted = Target(operation.Unallocated, operation.A, active, all);
                    return new AddAdjustment(id, Money.From(operation.Units / 100m, adjusted.Currency),
                        operation.B % 2 == 0 ? Contracts.V1.Journal.AdjustmentDirection.Increase : Contracts.V1.Journal.AdjustmentDirection.Decrease,
                        occurredOn, adjusted.BucketId, adjusted.Unallocated, "Recount", []);

                case Kind.Borrow:
                    // Usually (Fee 0 and 1) an active savings bucket of the user lends today at most what it holds, into another
                    // bucket of its currency, into Unallocated of that currency, or to the outside, so borrows are accepted often.
                    // Otherwise any bucket lends (Fee 2: investment, system and archived ones included), or (Fee 3) the
                    // destination is any bucket, the lender itself or one of another currency included, at the generated date and
                    // amount.
                    var today = BudgetState.Replay(all, budget.Journal, Today);
                    var lenders = active.Where(bucket => bucket.Kind == BucketKind.Savings && bucket.SystemRole == BucketSystemRole.None)
                        .OrderByDescending(bucket => today.BalanceOf(bucket.BucketId).Available.Amount > 0m)
                        .ToList();
                    var funded = lenders.Count(bucket => today.BalanceOf(bucket.BucketId).Available.Amount > 0m);
                    var lender = operation.Fee != 2 && lenders.Count > 0
                        ? lenders[operation.A % (funded > 0 ? funded : lenders.Count)]
                        : all.Count == 0 ? null : all[operation.A % all.Count];
                    var lenderCurrency = lender?.Currency ?? Currencies[operation.A % 3];
                    var principal = operation.Units / 100m;
                    Guid? toBucket = null;
                    Currency? toUnallocated = null;
                    if (operation.Fee == 3)
                    {
                        (toBucket, toUnallocated, _) = Target(operation.Unallocated, operation.B, active, all);
                    }
                    else
                    {
                        var available = lender is null ? 0m : today.BalanceOf(lender.BucketId).Available.Amount;
                        principal = available >= 0.01m ? Math.Min(principal, available) : principal;
                        occurredOn = operation.Fee == 2 ? occurredOn : Today;
                        var destinations = active.Where(bucket => bucket.Currency == lenderCurrency && bucket.Id != lender?.Id).ToList();
                        if (operation.B % 3 == 1 && destinations.Count > 0)
                        {
                            toBucket = destinations[operation.B % destinations.Count].Id;
                        }
                        else if (operation.B % 3 == 2)
                        {
                            toUnallocated = lenderCurrency;
                        }
                    }

                    return new AddBorrow(id, lender?.Id ?? Guid.CreateVersion7(), Money.From(principal, lenderCurrency), occurredOn, toBucket, toUnallocated, null, []);

                case Kind.Repay:
                    // Usually (Fee 0 and 1) a loan open today, dated no earlier than its borrow, within what it owes, paid from
                    // outside or from the bucket the money went to, so repayments are accepted often. Otherwise any loan ever
                    // opened (Fee 2: closed and reversed ones included) or an unknown one (Fee 3), at the generated date and
                    // amount, from any bucket (perhaps the lender), from Unallocated of the loan's currency, or from outside.
                    var open = BudgetState.Replay(all, budget.Journal, Today).Loans.Where(candidate => candidate.Status == LoanStatus.Open).ToList();
                    var loans = budget.Journal.OfType<Borrow>().ToList();
                    var regular = operation.Fee < 2 && open.Count > 0;
                    var loan = regular
                        ? loans.Single(borrow => borrow.LoanId == open[operation.A % open.Count].LoanId)
                        : operation.Fee == 3 || loans.Count == 0 ? null : loans[operation.A % loans.Count];
                    var loanCurrency = loan?.Principal.Currency ?? Currencies[operation.A % 3];
                    var units = operation.Units / 100m;
                    Guid? fromBucket = null;
                    Currency? fromUnallocated = null;
                    if (regular)
                    {
                        occurredOn = occurredOn < loan!.OccurredOn ? loan.OccurredOn : occurredOn;
                        units = Math.Min(units, open.Single(candidate => candidate.LoanId == loan.LoanId).Outstanding.Amount);
                        fromBucket = operation.B % 2 == 1 ? loan.ToBucketId?.Value : null;
                    }
                    else if (operation.B % 3 == 1)
                    {
                        (fromBucket, fromUnallocated, _) = Target(false, operation.B, active, all);
                    }
                    else if (operation.B % 3 == 2)
                    {
                        fromUnallocated = loanCurrency;
                    }

                    return new AddRepayment(id, loan?.Id ?? Guid.CreateVersion7(), Money.From(units, loanCurrency), occurredOn, fromBucket, fromUnallocated, null, []);

                case Kind.Transfer:
                    var from = Target(operation.Unallocated && operation.A % 2 == 0, operation.A, active, all);
                    var to = Target(operation.Unallocated && operation.A % 2 == 1, operation.B, active, all);

                    // A quarter of the transfers pay a plan, usually into its own active bucket.
                    var planIds = budget.Journal.OfType<PlanCreated>().ToList();
                    Guid? transferPlan = null;
                    if (operation.B % 4 == 3 && planIds.Count > 0)
                    {
                        var plan = planIds[operation.A % planIds.Count];
                        transferPlan = plan.Id;
                        if (active.Find(bucket => bucket.BucketId == plan.BucketId) is { } planBucket && planBucket.Id != from.BucketId)
                        {
                            to = (planBucket.Id, null, planBucket.Currency);
                        }
                    }
                    var fee = operation.Fee == 0 ? null : Money.From(operation.Fee, from.Currency);
                    var fromAmount = Money.From(operation.Units / 100m + operation.Fee, from.Currency);
                    var toAmount = from.Currency == to.Currency
                        ? Money.From(operation.Units / 100m, to.Currency)
                        : Money.From(operation.OtherUnits / 100m, to.Currency);
                    return new AddTransfer(id, fromAmount, toAmount, fee, occurredOn, from.BucketId, from.Unallocated, to.BucketId, to.Unallocated, null, [], transferPlan);

                case Kind.CreatePlan:
                    // Usually (Fee 0 and 1) an active bucket of the user in its currency, starting up to four months back or two
                    // ahead, sometimes ending; otherwise (Fee 2) another currency, or (Fee 3) any bucket, Unallocated and
                    // archived ones included. An end may come before the start.
                    var users = active.Where(bucket => bucket.SystemRole == BucketSystemRole.None).ToList();
                    var planned = operation.Fee < 3 && users.Count > 0 ? users[operation.A % users.Count] : all.Count == 0 ? null : all[operation.A % all.Count];
                    var planCurrency = planned is null ? Currency.Pln : operation.Fee == 2 ? Currencies[(Array.IndexOf(Currencies, planned.Currency) + 1) % 3] : planned.Currency;
                    var validFrom = CurrentMonth.AddMonths((operation.B % 7) - 4);
                    YearMonth? validTo = operation.Unallocated ? validFrom.AddMonths((int)(operation.OtherUnits % 6) - 1) : null;
                    return new CreatePlan(id, planned?.Id ?? Guid.CreateVersion7(), Money.From(operation.Units / 100m, planCurrency), validFrom, validTo, occurredOn, null, []);

                case Kind.ChangePlanAmount or Kind.SetPlanEnd:
                    // Usually (Fee 0 to 2) a plan that exists (Fee 0 and 1: one that has not ended), otherwise any plan ever
                    // created or an unknown one; a month from two back to three ahead, so past months are tried (and refused
                    // unless nothing changes), kept within the plan's months for Fee 0 and 1 so changes are accepted often.
                    var state = BudgetState.Replay(all, budget.Journal, Today);
                    var running = state.Plans.Where(plan => operation.Fee >= 2 || state.StandingOf(plan).Status == PlanStatus.Active).ToList();
                    var created = budget.Journal.OfType<PlanCreated>().ToList();
                    var planTarget = operation.Fee < 3 && running.Count > 0 ? running[operation.A % running.Count] : null;
                    var changedPlan = planTarget?.PlanId.Value
                        ?? (created.Count > 0 && operation.Unallocated ? created[operation.A % created.Count].Id : Guid.CreateVersion7());
                    var planMoney = state.Plans.FirstOrDefault(plan => plan.PlanId.Value == changedPlan)?.Currency ?? Currency.Pln;
                    var month = CurrentMonth.AddMonths((operation.B % 6) - 2);
                    if (planTarget is not null && operation.Fee < 2)
                    {
                        month = month < planTarget.ValidFrom ? planTarget.ValidFrom : month;
                        month = planTarget.ValidTo is { } end && month > end ? end : month;
                    }

                    return operation.Kind == Kind.ChangePlanAmount
                        ? new ChangePlanAmount(id, changedPlan, Money.From(operation.Units / 100m, planMoney), month, Today, null, [])
                        : new SetPlanEnd(id, changedPlan, operation.OtherUnits % 4 == 0 ? null : month, Today, null, []);

                case Kind.Reverse:
                    var journal = budget.Journal;
                    var target = journal.Count == 0 ? Guid.CreateVersion7() : journal[operation.A % journal.Count].Id;
                    return new ReverseEntry(id, target, Today, null, []);

                case Kind.Archive:
                    // Half of the time the newest bucket of the user, which is often still empty, so that
                    // archiving succeeds often enough to matter; otherwise any bucket, system ones included.
                    var newest = all.LastOrDefault(bucket => bucket.SystemRole == BucketSystemRole.None);
                    return new ArchiveBucket(operation.Unallocated && newest is not null
                        ? newest.Id
                        : all.Count == 0 ? Guid.CreateVersion7() : all[operation.A % all.Count].Id);

                case Kind.Unarchive:
                    // Half of the time an archived bucket, so that unarchiving succeeds often enough to matter (its
                    // name may have been taken again by then); otherwise any bucket, active and system ones included.
                    var archived = all.Where(bucket => bucket.Status == BucketStatus.Archived).ToList();
                    return new UnarchiveBucket(operation.Unallocated && archived.Count > 0
                        ? archived[operation.A % archived.Count].Id
                        : all.Count == 0 ? Guid.CreateVersion7() : all[operation.A % all.Count].Id);

                case Kind.Rename:
                    return new RenameBucket(all.Count == 0 ? Guid.CreateVersion7() : all[operation.A % all.Count].Id, $"Bucket {operation.B % 6}");

                default:
                    // A repeat of an earlier successful command that has a client ID (CreateBucket or an entry).
                    var repeatable = succeeded.Where(command => command is CreateBucket or IBucketEntryCommand or AddTransfer or AddBorrow or AddRepayment or ReverseEntry
                        or CreatePlan or ChangePlanAmount or SetPlanEnd).ToList();
                    return repeatable.Count == 0
                        ? new CreateBucket(id, "Repeatable", Currency.Pln, Contract.BucketKind.Savings, null)
                        : repeatable[operation.A % repeatable.Count];
            }
        }

        // A target: one of the active buckets, or Unallocated of a currency (also when there is no bucket yet).
        private static (Guid? BucketId, Currency? Unallocated, Currency Currency) Target(bool unallocated, int index, List<Bucket> active, IReadOnlyList<Bucket> all)
        {
            if (unallocated || active.Count == 0)
            {
                var currency = Currencies[index % 3];
                return (null, currency, currency);
            }

            var bucket = active[index % active.Count];
            return (bucket.Id, null, bucket.Currency);
        }

        // A version 7 UUID derived from a generated GUID, so a failing case can be replayed from its seed.
        private static Guid Version7(Guid seed)
        {
            var bytes = seed.ToByteArray(bigEndian: true);
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new Guid(bytes, bigEndian: true);
        }

        private Result Send(object command)
        {
            var loader = new BudgetStateLoader(budget, budget);
            var writer = new JournalWriter(loader, budget, owner, clock);
            var entries = new BucketEntryWriter(loader, budget, owner, clock);
            var cancellation = TestContext.Current.CancellationToken;

            return (command switch
            {
                SaveOwnerSettings save => new SaveOwnerSettingsHandler(loader, budget, owner, clock).Handle(save, cancellation),
                CreateBucket create => new CreateBucketHandler(loader, budget, owner).Handle(create, cancellation),
                RenameBucket rename => new RenameBucketHandler(loader, budget).Handle(rename, cancellation),
                ArchiveBucket archive => new ArchiveBucketHandler(loader, budget, clock).Handle(archive, cancellation),
                UnarchiveBucket unarchive => new UnarchiveBucketHandler(loader, budget).Handle(unarchive, cancellation),
                AddOpeningBalance opening => new AddOpeningBalanceHandler(entries).Handle(opening, cancellation),
                AddDeposit deposit => new AddDepositHandler(entries).Handle(deposit, cancellation),
                AddSpend spend => new AddSpendHandler(entries).Handle(spend, cancellation),
                AddAdjustment adjustment => new AddAdjustmentHandler(entries).Handle(adjustment, cancellation),
                AddBorrow borrow => new AddBorrowHandler(writer).Handle(borrow, cancellation),
                AddRepayment repayment => new AddRepaymentHandler(writer).Handle(repayment, cancellation),
                AddTransfer transfer => new AddTransferHandler(writer).Handle(transfer, cancellation),
                CreatePlan plan => new CreatePlanHandler(writer).Handle(plan, cancellation),
                ChangePlanAmount change => new ChangePlanAmountHandler(writer).Handle(change, cancellation),
                SetPlanEnd end => new SetPlanEndHandler(writer).Handle(end, cancellation),
                ReverseEntry reverse => new ReverseEntryHandler(writer, new JournalEntryDependencies()).Handle(reverse, cancellation),
                _ => throw new InvalidOperationException($"Unknown command {command.GetType().Name}."),
            }).AsTask().GetAwaiter().GetResult();
        }
    }
}
