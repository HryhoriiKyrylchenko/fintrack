using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Application.Tests.Journal;

/// <summary>
/// <c>AddOpeningBalance</c> and <c>AddSpend</c> against in-memory fakes: one opening balance per bucket
/// (D13), no spend from an investment bucket (D11), and never below zero at any point (D2).
/// </summary>
public class OpeningBalanceAndSpendHandlerTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);

    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();
    private readonly Bucket cushion;

    public OpeningBalanceAndSpendHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log) { Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL") };
        cushion = StoredBuckets.Of(Guid.CreateVersion7(), "Cushion", Currency.Pln, 1, FakeClock.DefaultStart, BucketStatus.Active);
        store.Buckets.Add(cushion);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpeningBalance_IsAppended()
    {
        (await Opening(500m, -365)).IsSuccess.ShouldBeTrue();

        var entry = ledger.Commits.ShouldHaveSingleItem().Appends.ShouldHaveSingleItem().ShouldBeOfType<OpeningBalance>();
        entry.BucketId.ShouldBe(cushion.BucketId);
        entry.OccurredOn.ShouldBe(Today.AddDays(-365));
    }

    [Fact]
    public async Task SecondOpeningBalance_IsRejected()
    {
        await Opening(500m, -365);
        Store(ledger.Commits[0]);

        (await Opening(100m, -300)).ShouldFailWith(BudgetErrorCodes.BucketOpeningBalanceExists, ErrorKind.BusinessRule);
        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OpeningBalanceIntoANewUnallocated_CreatesIt()
    {
        var result = await Send(new AddOpeningBalance(Guid.CreateVersion7(), Money.From(10m, Currency.Eur), Today.AddDays(-30), null, Currency.Eur, null, []));

        result.IsSuccess.ShouldBeTrue();
        ledger.Commits.ShouldHaveSingleItem().Saves.ShouldHaveSingleItem().ShouldBeOfType<Bucket>().SystemRole.ShouldBe(BucketSystemRole.Unallocated);
    }

    [Fact]
    public async Task Spend_WithinTheBalance_IsAppended()
    {
        await Opening(100m, -10);
        Store(ledger.Commits[0]);

        (await Spend(100m, -1)).IsSuccess.ShouldBeTrue();

        ledger.Commits[1].Appends.ShouldHaveSingleItem().ShouldBeOfType<Spend>().Amount.ShouldBe(Money.From(100m, Currency.Pln));
    }

    [Fact]
    public async Task Spend_BeyondTheBalance_IsInsufficientFunds_WithTheDay()
    {
        await Opening(100m, -10);
        Store(ledger.Commits[0]);

        var error = (await Spend(100.01m, -1)).ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule);

        error.Parameters.ShouldBe(new Dictionary<string, object> { ["date"] = "2026-01-14" });
        ledger.Commits.Count.ShouldBe(1);
    }

    [Fact]
    public async Task BackdatedSpend_BeforeTheMoneyArrived_IsRejected_EvenThoughTodayIsFine()
    {
        await Opening(100m, -10);
        Store(ledger.Commits[0]);

        // Enough today, but on day -20 the bucket held nothing yet.
        (await Spend(50m, -20)).ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule)
            .Parameters["date"].ShouldBe("2025-12-26");
    }

    [Fact]
    public async Task SpendFromAnInvestmentBucket_IsNotAllowed()
    {
        var crypto = Bucket.Create(BucketId.From(Guid.CreateVersion7()), FakeOwnerContext.Owner, "Crypto", Currency.Pln, BucketKind.Investment, InvestmentProfile.Crypto, []).Value;
        store.Buckets.Add(crypto);

        (await Send(new AddSpend(Guid.CreateVersion7(), Money.From(1m, Currency.Pln), Today, crypto.Id, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketSpendNotAllowed, ErrorKind.BusinessRule);
    }

    [Fact]
    public async Task SpendFromAMissingUnallocated_IsInsufficientFunds_AndCreatesNothing()
    {
        (await Send(new AddSpend(Guid.CreateVersion7(), Money.From(1m, Currency.Usd), Today, null, Currency.Usd, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task RepeatedSpend_IsRecognisedBeforeTheBalanceCheck()
    {
        await Opening(100m, -10);
        Store(ledger.Commits[0]);
        var spend = new AddSpend(Guid.CreateVersion7(), Money.From(100m, Currency.Pln), Today, cushion.Id, null, null, []);
        await Send(spend);
        Store(ledger.Commits[1]);

        // Applied again, it would overdraw the bucket; as a repeat it changes nothing.
        (await Send(spend)).IsSuccess.ShouldBeTrue();
        ledger.Commits.Count.ShouldBe(2);
    }

    private Task<FluentResults.Result> Opening(decimal amount, int day) =>
        Send(new AddOpeningBalance(Guid.CreateVersion7(), Money.From(amount, Currency.Pln), Today.AddDays(day), cushion.Id, null, null, []));

    private Task<FluentResults.Result> Spend(decimal amount, int day) =>
        Send(new AddSpend(Guid.CreateVersion7(), Money.From(amount, Currency.Pln), Today.AddDays(day), cushion.Id, null, null, []));

    private Task<FluentResults.Result> Send(AddOpeningBalance command) =>
        new AddOpeningBalanceHandler(Writer()).Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result> Send(AddSpend command) =>
        new AddSpendHandler(Writer()).Handle(command, Cancellation).AsTask();

    private BucketEntryWriter Writer() => new(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock);

    // What the store would return after a commit.
    private void Store(Shared.Kernel.Ledger.LedgerCommit commit)
    {
        store.Journal.AddRange(commit.Appends.Cast<JournalEntry>());
        store.Buckets.AddRange(commit.Saves.Cast<Bucket>());
    }
}
