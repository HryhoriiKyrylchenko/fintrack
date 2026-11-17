using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using BucketKind = FinTrack.Budget.Contracts.V1.Buckets.BucketKind;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Journal;

/// <summary>Deposits through the real ledger and MongoDB: the stored entry, the replayed balance, <c>Unallocated</c> on demand, repeats, owners.</summary>
public sealed class AddDepositTests(BudgetFixture fixture)
{
    // The fixture's clock is FakeClock.DefaultStart: 2026-01-15 in Warsaw.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deposit_IsStored_AndRaisesTheReplayedBalance()
    {
        var owner = await NewOwnerWithSettingsAsync();
        var bucket = await CreateBucketAsync(owner, "Cushion", Currency.Pln);

        (await DepositAsync(owner, new AddDeposit(Guid.CreateVersion7(), Money.From(100.50m, Currency.Pln), Today, bucket, null, null, []))).IsSuccess.ShouldBeTrue();
        (await DepositAsync(owner, new AddDeposit(Guid.CreateVersion7(), Money.From(20m, Currency.Pln), Today.AddDays(-30), bucket, null, "Back pay", ["work"]))).IsSuccess.ShouldBeTrue();

        var state = await StateAsync(owner);
        state.Journal.Count.ShouldBe(2);
        state.Journal[0].OccurredOn.ShouldBe(Today.AddDays(-30));
        var balance = state.State.BalanceOf(BucketId.From(bucket));
        balance.Available.ShouldBe(Money.From(120.50m, Currency.Pln));
        balance.Deposited.ShouldBe(Money.From(120.50m, Currency.Pln));
    }

    [Fact]
    public async Task FirstDepositIntoUnallocated_CreatesOneBucket_LaterDepositsReuseIt()
    {
        var owner = await NewOwnerWithSettingsAsync();

        await DepositAsync(owner, new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Eur), Today, null, Currency.Eur, null, []));
        await DepositAsync(owner, new AddDeposit(Guid.CreateVersion7(), Money.From(5m, Currency.Eur), Today, null, Currency.Eur, null, []));

        var state = await StateAsync(owner);
        var unallocated = state.Buckets.ShouldHaveSingleItem();
        unallocated.SystemRole.ShouldBe(BucketSystemRole.Unallocated);
        UuidV7.IsValid(unallocated.Id).ShouldBeTrue();
        state.State.BalanceOf(unallocated.BucketId).Available.ShouldBe(Money.From(15m, Currency.Eur));
        state.Version.ShouldBe(3);
    }

    [Fact]
    public async Task RepeatedDeposit_WritesNothing_OtherContentIsIdReused()
    {
        var owner = await NewOwnerWithSettingsAsync();
        var command = new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Usd), Today, null, Currency.Usd, "Gift", []);
        await DepositAsync(owner, command);

        (await DepositAsync(owner, command)).IsSuccess.ShouldBeTrue();
        (await DepositAsync(owner, command with { Note = "Present" })).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);

        var state = await StateAsync(owner);
        state.Journal.ShouldHaveSingleItem();
        state.Version.ShouldBe(2);
    }

    [Fact]
    public async Task OtherOwnersBucket_IsNotFound()
    {
        var first = await NewOwnerWithSettingsAsync();
        var second = await NewOwnerWithSettingsAsync();
        var bucket = await CreateBucketAsync(first, "Cushion", Currency.Pln);

        (await DepositAsync(second, new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Today, bucket, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketNotFound, ErrorKind.NotFound);
        (await StateAsync(second)).Journal.ShouldBeEmpty();
    }

    [Fact]
    public async Task RejectedDeposit_WritesNothing()
    {
        var owner = await NewOwnerWithSettingsAsync();
        var bucket = await CreateBucketAsync(owner, "Cushion", Currency.Pln);

        (await DepositAsync(owner, new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Pln), Today.AddDays(1), bucket, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.EntryDateInFuture, ErrorKind.BusinessRule);
        (await DepositAsync(owner, new AddDeposit(Guid.CreateVersion7(), Money.From(10m, Currency.Eur), Today, null, Currency.Usd, null, [])))
            .ShouldFailWith(BudgetErrorCodes.BucketCurrencyMismatch, ErrorKind.BusinessRule);

        var state = await StateAsync(owner);
        state.Journal.ShouldBeEmpty();
        state.Buckets.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SecondFirstDeposit_ReadingStaleState_Conflicts_AndTheRetryReusesTheBucket()
    {
        var owner = await NewOwnerWithSettingsAsync();
        await using var scope = fixture.ScopeFor(owner);
        var services = scope.ServiceProvider;
        var stale = (await services.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation)).Value;

        // The first deposit creates Unallocated and moves the ledger on.
        (await DepositAsync(owner, new AddDeposit(Guid.CreateVersion7(), Money.From(1m, Currency.Pln), Today, null, Currency.Pln, null, []))).IsSuccess.ShouldBeTrue();

        // The second read its state before that commit (as a concurrent request would).
        var second = new AddDeposit(Guid.CreateVersion7(), Money.From(2m, Currency.Pln), Today, null, Currency.Pln, null, []);
        var staleHandler = new AddDepositHandler(new BucketEntryWriter(
            new BudgetStateLoader(new FrozenVersionLedger(services.GetRequiredService<ILedger>(), stale.Version), new FrozenStore(stale)),
            services.GetRequiredService<ILedger>(),
            services.GetRequiredService<Shared.Kernel.Persistence.IOwnerContext>(),
            services.GetRequiredService<Shared.Kernel.Time.IClock>()));

        (await staleHandler.Handle(second, Cancellation)).ShouldFailWith(Shared.Kernel.Results.ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);

        // The retry (what RetryOnConflictBehavior does) reads fresh state and finds the bucket.
        (await DepositAsync(owner, second)).IsSuccess.ShouldBeTrue();
        var state = await StateAsync(owner);
        state.Buckets.ShouldHaveSingleItem().SystemRole.ShouldBe(BucketSystemRole.Unallocated);
        state.Journal.Count.ShouldBe(2);
    }

    private async Task<OwnerId> NewOwnerWithSettingsAsync()
    {
        var owner = BudgetFixture.NewOwner();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<SaveOwnerSettingsHandler>(scope.ServiceProvider)
            .Handle(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"), Cancellation)).IsSuccess.ShouldBeTrue();
        return owner;
    }

    private async Task<Guid> CreateBucketAsync(OwnerId owner, string name, Currency currency)
    {
        var id = Guid.CreateVersion7();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<CreateBucketHandler>(scope.ServiceProvider)
            .Handle(new CreateBucket(id, name, currency, BucketKind.Savings, null), Cancellation)).IsSuccess.ShouldBeTrue();
        return id;
    }

    private async Task<Result> DepositAsync(OwnerId owner, AddDeposit command)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<AddDepositHandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<BudgetSnapshot> StateAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return (await scope.ServiceProvider.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation)).Value;
    }

    /// <summary>Reports a version read earlier; commits go to the real ledger.</summary>
    private sealed class FrozenVersionLedger(ILedger inner, long version) : ILedger
    {
        public Task<long> GetVersionAsync(LedgerName ledger, CancellationToken cancellationToken) => Task.FromResult(version);

        public Task<Result<LedgerCommitted>> CommitAsync(LedgerCommit commit, CancellationToken cancellationToken) => inner.CommitAsync(commit, cancellationToken);
    }

    /// <summary>Returns documents read earlier.</summary>
    private sealed class FrozenStore(BudgetSnapshot snapshot) : IBudgetStore
    {
        public Task<Domain.Settings.OwnerSettings?> GetSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot.Settings);

        public Task<IReadOnlyList<Bucket>> GetBucketsAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot.Buckets);

        public Task<IReadOnlyList<Domain.Journal.JournalEntry>> GetJournalAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot.Journal);
    }
}
