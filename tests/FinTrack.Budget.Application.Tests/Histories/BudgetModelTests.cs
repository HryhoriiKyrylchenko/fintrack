using CsCheck;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;
using FluentResults;
using Contract = FinTrack.Budget.Contracts.V1.Buckets;

namespace FinTrack.Budget.Application.Tests.Histories;

/// <summary>
/// The model test of plan 1a step 10: random sequences of every Budget command (buckets in three
/// currencies, opening balances, deposits, spends, transfers with and without fee and across
/// currencies, reversals, archiving, renaming, backdating, and repeats of earlier commands) run through
/// the real handlers. After every command and at the end, invariants 1, 2, 10, 11 and 13 must hold.
/// </summary>
public class BudgetModelTests
{
    private static readonly Currency[] Currencies = [Currency.Pln, Currency.Eur, Currency.Usd];

    // The fake clock starts at 2026-01-15 09:30 UTC; the owner is in Warsaw.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private enum Kind
    {
        CreateBucket,
        OpeningBalance,
        Deposit,
        Spend,
        Transfer,
        Reverse,
        Archive,
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
            }

            return (operation.Kind, result);
        }

        public void CheckInvariants()
        {
            var bucketList = budget.Buckets.ToList();
            var journal = budget.Journal.ToList();
            var state = BudgetState.Replay(bucketList, journal);

            var reversed = BudgetState.ReversedEntryIds(journal);
            var effective = journal.Where(entry => entry is not Reversal && !reversed.Contains(entry.Id)).ToList();

            // Invariant 1, checked independently: running sums over the effective journal in replay order.
            var sums = bucketList.ToDictionary(bucket => bucket.BucketId, _ => 0m);
            foreach (var entry in ReplayOrder.Sort(effective))
            {
                foreach (var (bucketId, change) in Changes(entry))
                {
                    sums[bucketId] += change;
                }

                bucketList.Where(bucket => bucket.Kind == BucketKind.Savings)
                    .ShouldAllBe(bucket => sums[bucket.BucketId] >= 0m, "a savings bucket went below zero");
            }

            // Invariant 2.
            state.Balances.ShouldAllBe(balance => balance.Available == balance.NetContributed - balance.OutstandingPrincipal);
            state.FirstOverdraft.ShouldBeNull();

            // Invariant 10: the replay equals the step-by-step build and does not depend on the stored order.
            var stepByStep = ReplayOrder.Sort(effective).Aggregate(BudgetState.Replay(bucketList, []), (current, entry) => current.Apply(entry));
            state.Balances.ShouldBe(stepByStep.Balances);
            BudgetState.Replay(bucketList, Enumerable.Reverse(journal)).Balances.ShouldBe(state.Balances);

            // Invariant 11: per-currency totals equal the independent sums; no currency is added to another.
            foreach (var bucket in bucketList)
            {
                state.BalanceOf(bucket.BucketId).Available.Amount.ShouldBe(sums[bucket.BucketId]);
                state.BalanceOf(bucket.BucketId).Available.Currency.ShouldBe(bucket.Currency);
            }

            // Archived buckets are empty, and nothing references a bucket that does not exist.
            bucketList.Where(bucket => bucket.Status == BucketStatus.Archived)
                .ShouldAllBe(bucket => sums[bucket.BucketId] == 0m);
        }

        private static IEnumerable<(BucketId Bucket, decimal Change)> Changes(JournalEntry entry) => entry switch
        {
            Spend spend => [(spend.BucketId, -spend.Amount.Amount)],
            SingleBucketEntry single => [(single.BucketId, single.Amount.Amount)],
            Transfer transfer => [(transfer.FromBucketId, -transfer.FromAmount.Amount), (transfer.ToBucketId, transfer.ToAmount.Amount)],
            _ => [],
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
                    var amount = Money.From(operation.Units / 100m, currency);
                    return operation.Kind switch
                    {
                        Kind.OpeningBalance => new AddOpeningBalance(id, amount, occurredOn, bucketId, unallocated, null, []),
                        Kind.Deposit => new AddDeposit(id, amount, occurredOn, bucketId, unallocated, null, []),
                        _ => new AddSpend(id, amount, occurredOn, bucketId, unallocated, null, []),
                    };

                case Kind.Transfer:
                    var from = Target(operation.Unallocated && operation.A % 2 == 0, operation.A, active, all);
                    var to = Target(operation.Unallocated && operation.A % 2 == 1, operation.B, active, all);
                    var fee = operation.Fee == 0 ? null : Money.From(operation.Fee, from.Currency);
                    var fromAmount = Money.From(operation.Units / 100m + operation.Fee, from.Currency);
                    var toAmount = from.Currency == to.Currency
                        ? Money.From(operation.Units / 100m, to.Currency)
                        : Money.From(operation.OtherUnits / 100m, to.Currency);
                    return new AddTransfer(id, fromAmount, toAmount, fee, occurredOn, from.BucketId, from.Unallocated, to.BucketId, to.Unallocated, null, []);

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

                case Kind.Rename:
                    return new RenameBucket(all.Count == 0 ? Guid.CreateVersion7() : all[operation.A % all.Count].Id, $"Bucket {operation.B % 6}");

                default:
                    // A repeat of an earlier successful command that has a client ID (CreateBucket or an entry).
                    var repeatable = succeeded.Where(command => command is CreateBucket or IBucketEntryCommand or AddTransfer or ReverseEntry).ToList();
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
                ArchiveBucket archive => new ArchiveBucketHandler(loader, budget).Handle(archive, cancellation),
                AddOpeningBalance opening => new AddOpeningBalanceHandler(entries).Handle(opening, cancellation),
                AddDeposit deposit => new AddDepositHandler(entries).Handle(deposit, cancellation),
                AddSpend spend => new AddSpendHandler(entries).Handle(spend, cancellation),
                AddTransfer transfer => new AddTransferHandler(writer).Handle(transfer, cancellation),
                ReverseEntry reverse => new ReverseEntryHandler(writer).Handle(reverse, cancellation),
                _ => throw new InvalidOperationException($"Unknown command {command.GetType().Name}."),
            }).AsTask().GetAwaiter().GetResult();
        }
    }
}
