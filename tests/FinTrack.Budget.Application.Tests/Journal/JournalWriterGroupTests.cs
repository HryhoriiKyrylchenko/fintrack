using System.Globalization;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;

namespace FinTrack.Budget.Application.Tests.Journal;

/// <summary>
/// <see cref="JournalWriter"/> with several entries in one command (plan 1b B15), against in-memory fakes: one commit
/// for the whole group, a repeat recognised over the whole group (any difference, a part of a group, or new entries in
/// a stored group is <c>entry.id_reused</c>), every entry's rules, and invariant 1 on one replay with all of them.
/// No command writes several entries yet (allocation, a deposit with a limit raise: later steps), so a test draft of
/// deposits and spends stands in for one.
/// </summary>
public class JournalWriterGroupTests
{
    // FakeClock.DefaultStart is 2026-01-15 09:30 UTC: in Warsaw that is 2026-01-15.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly Bucket cushion;
    private readonly Bucket holiday;
    private readonly Guid group = Guid.CreateVersion7();

    public JournalWriterGroupTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
        cushion = StoredBuckets.Of(Guid.CreateVersion7(), "Cushion", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        holiday = StoredBuckets.Of(Guid.CreateVersion7(), "Holiday", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.AddRange([cushion, holiday]);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Group_IsWrittenOnce_InOneCommit_EveryEntryCarryingTheGroup()
    {
        ledger.Version = 4;
        Line[] lines = [Deposit(1, cushion, 100m), Deposit(2, holiday, 20m), Spend(3, cushion, 30m)];

        var result = await Write(group, lines);

        result.IsSuccess.ShouldBeTrue();
        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "version", "commit"]);
        var commit = ledger.Commits.ShouldHaveSingleItem();
        commit.ExpectedVersion.ShouldBe(4);
        commit.Saves.ShouldBeEmpty();
        commit.Appends.Select(entry => entry.Id).ShouldBe(lines.Select(line => line.Id));
        commit.Appends.Cast<JournalEntry>().ShouldAllBe(entry => entry.GroupId == group && entry.RecordedAt == FakeClock.DefaultStart);
        commit.Appends.Select(entry => entry.GetType()).ShouldBe([typeof(Deposit), typeof(Deposit), typeof(Spend)]);
    }

    [Fact]
    public async Task RepeatOfTheGroup_SucceedsAndWritesNothing()
    {
        Line[] lines = [Deposit(1, cushion, 100m), Spend(2, cushion, 30m)];
        await WriteAndStore(group, lines);
        clock.Advance(TimeSpan.FromMinutes(5));

        var result = await Write(group, lines);

        result.IsSuccess.ShouldBeTrue();
        ledger.Commits.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task GroupWithOneEntryChanged_IsIdReused(int changed)
    {
        Line[] lines = [Deposit(1, cushion, 100m), Deposit(2, holiday, 20m)];
        await WriteAndStore(group, lines);
        var repeat = lines.ToArray();
        repeat[changed] = repeat[changed] with { Amount = repeat[changed].Amount + 1m };

        (await Write(group, repeat)).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);

        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GroupThatExtendsAStoredPart_IsIdReused()
    {
        // Only the first entry of the group is stored (as if an earlier command had sent it alone with this group).
        await WriteAndStore(group, Deposit(1, cushion, 100m));

        (await Write(group, Deposit(1, cushion, 100m), Deposit(2, holiday, 20m))).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);

        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PartOfAStoredGroup_IsIdReused()
    {
        await WriteAndStore(group, Deposit(1, cushion, 100m), Deposit(2, holiday, 20m));

        (await Write(group, Deposit(1, cushion, 100m))).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);

        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task NewEntriesInAStoredGroup_AreIdReused()
    {
        await WriteAndStore(group, Deposit(1, cushion, 100m), Deposit(2, holiday, 20m));

        (await Write(group, Deposit(3, cushion, 5m), Deposit(4, holiday, 5m))).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);

        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task StoredGroupRepeatedUnderAnotherGroupId_IsIdReused()
    {
        Line[] lines = [Deposit(1, cushion, 100m), Deposit(2, holiday, 20m)];
        await WriteAndStore(group, lines);

        (await Write(Guid.CreateVersion7(), lines)).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);

        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EntryWrittenAlone_RepeatedInAGroup_IsIdReused()
    {
        await WriteAndStore(null, Deposit(1, cushion, 100m));

        (await Write(group, Deposit(1, cushion, 100m), Deposit(2, holiday, 20m))).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);

        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SpendThatOnlyTheGroupsDepositFunds_IsAccepted()
    {
        (await Write(group, Deposit(1, cushion, 50m), Spend(2, cushion, 30m))).IsSuccess.ShouldBeTrue();

        ledger.Commits.ShouldHaveSingleItem().Appends.Count.ShouldBe(2);
    }

    [Fact]
    public async Task EntryOfTheGroupBelowZero_IsInsufficientFunds_AndNothingIsWritten()
    {
        var result = await Write(group, Deposit(1, cushion, 10m), Spend(2, cushion, 30m));

        result.ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule)
            .Parameters.ShouldBe(new Dictionary<string, object> { ["date"] = Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task OneEntrysOwnRule_FailsTheWholeGroup()
    {
        (await Write(group, Deposit(1, cushion, 10m), Deposit(2, holiday, 0m)))
            .ShouldFailWith(ErrorCodes.MoneyAmountNotPositive, ErrorKind.Validation);

        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task OneEntrysBucketRule_FailsTheWholeGroup()
    {
        store.Buckets[1] = StoredBuckets.Of(holiday.Id, "Holiday", Currency.Pln, 2, FakeClock.DefaultStart, BucketStatus.Archived);

        (await Write(group, Deposit(1, cushion, 10m), Deposit(2, holiday, 5m)))
            .ShouldFailWith(BudgetErrorCodes.BucketArchived, ErrorKind.BusinessRule);

        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoEntriesIntoUnallocated_CreateItOnce_InTheSameCommit()
    {
        (await Write(group, Deposit(1, null, 10m), Deposit(2, null, 5m))).IsSuccess.ShouldBeTrue();

        var commit = ledger.Commits.ShouldHaveSingleItem();
        var unallocated = commit.Saves.ShouldHaveSingleItem().ShouldBeOfType<Bucket>();
        commit.Appends.ShouldAllBe(entry => ((SingleBucketEntry)entry).BucketId == unallocated.BucketId);
    }

    [Fact]
    public async Task GroupIdThatIsNotVersion7_IsIdInvalid_BeforeAnyRead()
    {
        (await Write(Guid.NewGuid(), Deposit(1, cushion, 10m), Deposit(2, holiday, 5m))).ShouldFailWith(ErrorCodes.IdInvalid, ErrorKind.Validation);

        log.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task EntryIdThatIsNotVersion7_IsIdInvalid_BeforeAnyRead()
    {
        var line = Deposit(2, holiday, 5m) with { Id = Guid.NewGuid() };

        (await Write(group, Deposit(1, cushion, 10m), line)).ShouldFailWith(ErrorCodes.IdInvalid, ErrorKind.Validation);

        log.Calls.ShouldBeEmpty();
    }

    // The validators of a command keep these out; reaching the writer with them is a bug.
    [Fact]
    public async Task SeveralEntriesWithoutAGroup_Throw() =>
        await Should.ThrowAsync<ArgumentException>(() => Write(null, Deposit(1, cushion, 10m), Deposit(2, holiday, 5m)));

    [Fact]
    public async Task TheSameEntryIdTwice_Throws() =>
        await Should.ThrowAsync<ArgumentException>(() => Write(group, Deposit(1, cushion, 10m), Deposit(1, holiday, 5m)));

    [Fact]
    public async Task NoEntry_Throws() =>
        await Should.ThrowAsync<ArgumentException>(() => Write(group));

    [Fact]
    public async Task DraftWhoseEntriesDoNotMatchTheIds_Throws()
    {
        Line[] lines = [Deposit(1, cushion, 10m), Deposit(2, holiday, 5m)];

        await Should.ThrowAsync<InvalidOperationException>(() => Writer().WriteAsync(
            [.. lines.Select(line => line.Id)], group, context => Draft(context, [lines[1], lines[0]]), Cancellation).AsTask());
    }

    [Fact]
    public async Task DraftWhoseEntriesAreOutsideTheGroup_Throws()
    {
        Line[] lines = [Deposit(1, cushion, 10m), Deposit(2, holiday, 5m)];

        await Should.ThrowAsync<InvalidOperationException>(() => Writer().WriteAsync(
            [.. lines.Select(line => line.Id)], group, context => Draft(context, lines, groupId: null), Cancellation).AsTask());
    }

    [Fact]
    public async Task EntryId_OfAContextWithSeveralEntries_Throws()
    {
        InvalidOperationException? thrown = null;

        await Writer().WriteAsync([Id(1), Id(2)], group, context =>
        {
            thrown = Should.Throw<InvalidOperationException>(() => context.EntryId);
            context.EntryIds.Select(id => id.Value).ShouldBe([Id(1), Id(2)]);
            context.GroupId.ShouldBe(group);
            return Draft(context, [Deposit(1, cushion, 10m), Deposit(2, holiday, 5m)]);
        }, Cancellation);

        thrown.ShouldNotBeNull();
    }

    // Test IDs in a fixed order (version 7, distinct timestamps), so the replay order of one commit is known.
    private static Guid Id(int number) => Guid.Parse($"01900000-{number:X4}-7000-8000-000000000000");

    private static Line Deposit(int number, Bucket? bucket, decimal amount) => new(Id(number), false, bucket?.Id, amount);

    private static Line Spend(int number, Bucket bucket, decimal amount) => new(Id(number), true, bucket.Id, amount);

    private Task<Result> Write(Guid? groupId, params Line[] lines) =>
        Writer().WriteAsync([.. lines.Select(line => line.Id)], groupId, context => Draft(context, lines), Cancellation).AsTask();

    private async Task WriteAndStore(Guid? groupId, params Line[] lines)
    {
        var commits = ledger.Commits.Count;
        (await Write(groupId, lines)).IsSuccess.ShouldBeTrue();
        store.Journal.AddRange(ledger.Commits[commits].Appends.Cast<JournalEntry>());
        store.Buckets.AddRange(ledger.Commits[commits].Saves.Cast<Bucket>());
    }

    private JournalWriter Writer() => new(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock);

    // Each line becomes a deposit or a spend in the context's group (or in `groupId` when given), in the given order.
    private static Result<JournalDraft> Draft(JournalWriteContext context, IReadOnlyList<Line> lines) => Draft(context, lines, context.GroupId);

    private static Result<JournalDraft> Draft(JournalWriteContext context, IReadOnlyList<Line> lines, Guid? groupId)
    {
        var entries = new List<Result<JournalEntry>>();
        var rules = Result.Ok();
        foreach (var line in lines)
        {
            var target = context.ResolveTarget(line.Bucket, line.Bucket is null ? Currency.Pln : null);
            if (target.IsFailed)
            {
                return target.ToResult<JournalDraft>();
            }

            var id = JournalEntryId.From(line.Id);
            var amount = Money.From(line.Amount, Currency.Pln);
            var info = new EntryInfo(null, [], EntrySource.Manual, groupId);
            entries.Add(line.IsSpend
                ? Domain.Journal.Spend.Create(id, context.Owner, target.Value.BucketId, amount, Today, context.Today, info, context.Now).Map(entry => (JournalEntry)entry)
                : Domain.Journal.Deposit.Create(id, context.Owner, target.Value.BucketId, amount, Today, context.Today, info, context.Now).Map(entry => (JournalEntry)entry));
            rules = rules.Bind(() => JournalWriteContext.CheckBucket(target.Value, amount));
        }

        return new JournalDraft(entries, rules);
    }

    private sealed record Line(Guid Id, bool IsSpend, Guid? Bucket, decimal Amount);
}
