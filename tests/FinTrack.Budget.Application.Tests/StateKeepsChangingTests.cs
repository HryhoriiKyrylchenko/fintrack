using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Overview;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Contracts.V1.Overview;
using FinTrack.Budget.Contracts.V1.Settings;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;

namespace FinTrack.Budget.Application.Tests;

/// <summary>
/// When other commits keep landing while the state is read, <see cref="BudgetStateLoader"/> gives up with
/// <c>concurrency.conflict</c> (ARCHITECTURE 2.3). Every handler must pass that on as it is (a command is then retried
/// by the pipeline, a query answers 409) and write nothing, instead of reading a state that does not exist.
/// </summary>
public class StateKeepsChangingTests
{
    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();

    public StateKeepsChangingTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };

        // A commit lands after every read of the journal, so no two version readings ever agree.
        store.AfterJournalRead = () => ledger.Version++;
    }

    public static TheoryData<string> Handlers =>
    [
        "GetBuckets", "GetOwnerSettings", "GetBudgetOverview", "GetJournal",
        "CreateBucket", "RenameBucket", "ArchiveBucket", "UnarchiveBucket", "SaveOwnerSettings", "AddDeposit", "AddAdjustment", "ReverseEntry",
    ];

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task Handler_ReportsTheConflict_AndWritesNothing(string handler)
    {
        var result = await RunAsync(handler);

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
        ledger.Commits.ShouldBeEmpty();
    }

    private async Task<IResultBase> RunAsync(string handler)
    {
        var loader = new BudgetStateLoader(ledger, store);
        var owner = new FakeOwnerContext();
        var today = new DateOnly(2026, 1, 15);

        return handler switch
        {
            "GetBuckets" => await new GetBucketsHandler(loader).Handle(new GetBuckets(), Cancellation),
            "GetOwnerSettings" => await new GetOwnerSettingsHandler(loader).Handle(new GetOwnerSettings(), Cancellation),
            "GetBudgetOverview" => await new GetBudgetOverviewHandler(loader, new FakeFxRateProvider(), clock).Handle(new GetBudgetOverview(), Cancellation),
            "GetJournal" => await new GetJournalHandler(loader).Handle(new GetJournal(), Cancellation),
            "CreateBucket" => await new CreateBucketHandler(loader, ledger, owner).Handle(
                new CreateBucket(Guid.CreateVersion7(), "Cushion", Currency.Pln, BucketKind.Savings, null), Cancellation),
            "RenameBucket" => await new RenameBucketHandler(loader, ledger).Handle(new RenameBucket(Guid.CreateVersion7(), "Safety net"), Cancellation),
            "ArchiveBucket" => await new ArchiveBucketHandler(loader, ledger, clock).Handle(new ArchiveBucket(Guid.CreateVersion7()), Cancellation),
            "UnarchiveBucket" => await new UnarchiveBucketHandler(loader, ledger).Handle(new UnarchiveBucket(Guid.CreateVersion7()), Cancellation),
            "SaveOwnerSettings" => await new SaveOwnerSettingsHandler(loader, ledger, owner, clock).Handle(
                new SaveOwnerSettings(Currency.Eur, "Europe/Warsaw", "pl-PL"), Cancellation),
            "AddDeposit" => await new AddDepositHandler(new BucketEntryWriter(loader, ledger, owner, clock)).Handle(
                new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), today, null, Currency.Pln, null, []), Cancellation),
            "AddAdjustment" => await new AddAdjustmentHandler(new BucketEntryWriter(loader, ledger, owner, clock)).Handle(
                new AddAdjustment(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Contracts.V1.Journal.AdjustmentDirection.Increase, today, null, Currency.Pln, "Recount", []), Cancellation),
            _ => await new ReverseEntryHandler(new JournalWriter(loader, ledger, owner, clock), new JournalEntryDependencies()).Handle(
                new ReverseEntry(Guid.CreateVersion7(), Guid.CreateVersion7(), today, null, []), Cancellation),
        };
    }
}
