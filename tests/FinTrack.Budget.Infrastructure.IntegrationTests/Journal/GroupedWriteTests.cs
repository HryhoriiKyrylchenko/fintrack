using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using BucketKind = FinTrack.Budget.Contracts.V1.Buckets.BucketKind;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Journal;

/// <summary>
/// Several entries of one command (plan 1b B15) through the real ledger and MongoDB: one transaction for the whole
/// group (one version step, every entry with the group), a repeat that writes nothing, and a refused group that leaves
/// nothing behind. No command writes several entries yet, so a draft of a deposit and a spend stands in for one.
/// </summary>
public sealed class GroupedWriteTests(BudgetFixture fixture)
{
    // The fixture's clock is FakeClock.DefaultStart: 2026-01-15 in Warsaw.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Group_IsCommittedInOneTransaction_AndARepeatWritesNothing()
    {
        var owner = await NewOwnerWithSettingsAsync();
        var bucket = await CreateBucketAsync(owner);
        var group = Guid.CreateVersion7();
        var ids = OrderedIds();

        (await WriteAsync(owner, ids, group, bucket, deposit: 50m, spend: 30m)).IsSuccess.ShouldBeTrue();
        (await WriteAsync(owner, ids, group, bucket, deposit: 50m, spend: 30m)).IsSuccess.ShouldBeTrue();

        var state = await StateAsync(owner);
        state.Version.ShouldBe(3); // settings, bucket, the group once
        state.Journal.Select(entry => entry.Id).ShouldBe(ids, ignoreOrder: true);
        state.Journal.ShouldAllBe(entry => entry.GroupId == group);
        state.FindGroup(group).Count.ShouldBe(2);
        state.StateAsOf(Today).BalanceOf(Domain.Buckets.BucketId.From(bucket)).Available.ShouldBe(Money.From(20m, Currency.Pln));
    }

    [Fact]
    public async Task ChangedOrPartialGroup_IsIdReused_AndWritesNothing()
    {
        var owner = await NewOwnerWithSettingsAsync();
        var bucket = await CreateBucketAsync(owner);
        var group = Guid.CreateVersion7();
        var ids = OrderedIds();
        await WriteAsync(owner, ids, group, bucket, deposit: 50m, spend: 30m);

        (await WriteAsync(owner, ids, group, bucket, deposit: 50m, spend: 31m)).ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);
        (await WriteAsync(owner, [ids[0], Guid.CreateVersion7()], group, bucket, deposit: 50m, spend: 30m))
            .ShouldFailWith(BudgetErrorCodes.EntryIdReused, ErrorKind.Conflict);

        var state = await StateAsync(owner);
        state.Version.ShouldBe(3);
        state.Journal.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GroupThatWouldGoBelowZero_LeavesNothingBehind()
    {
        var owner = await NewOwnerWithSettingsAsync();
        var bucket = await CreateBucketAsync(owner);

        (await WriteAsync(owner, OrderedIds(), Guid.CreateVersion7(), bucket, deposit: 10m, spend: 30m))
            .ShouldFailWith(BudgetErrorCodes.BucketInsufficientFunds, ErrorKind.BusinessRule);

        var state = await StateAsync(owner);
        state.Version.ShouldBe(2);
        state.Journal.ShouldBeEmpty();
    }

    // Two version 7 IDs a millisecond apart: the deposit comes before the spend in the replay order (same date and RecordedAt).
    private static Guid[] OrderedIds()
    {
        var now = DateTimeOffset.UtcNow;
        return [Guid.CreateVersion7(now), Guid.CreateVersion7(now.AddMilliseconds(1))];
    }

    // A deposit and then a spend of the same bucket, dated today, in the given group.
    private async Task<Result> WriteAsync(OwnerId owner, Guid[] ids, Guid group, Guid bucket, decimal deposit, decimal spend)
    {
        await using var scope = fixture.ScopeFor(owner);
        var writer = scope.ServiceProvider.GetRequiredService<JournalWriter>();
        return await writer.WriteAsync(ids, group, context =>
        {
            var target = context.ResolveTarget(bucket, null).Value;
            var info = new EntryInfo(null, [], EntrySource.Manual, context.GroupId);
            Result<JournalEntry>[] entries =
            [
                Deposit.Create(context.EntryIds[0], context.Owner, target.BucketId, Money.From(deposit, Currency.Pln), Today, context.Today, info, context.Now)
                    .Map(entry => (JournalEntry)entry),
                Spend.Create(context.EntryIds[1], context.Owner, target.BucketId, Money.From(spend, Currency.Pln), Today, context.Today, info, context.Now)
                    .Map(entry => (JournalEntry)entry),
            ];
            return new JournalDraft(entries, Result.Ok());
        }, Cancellation);
    }

    private async Task<OwnerId> NewOwnerWithSettingsAsync()
    {
        var owner = BudgetFixture.NewOwner();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<SaveOwnerSettingsHandler>(scope.ServiceProvider)
            .Handle(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"), Cancellation)).IsSuccess.ShouldBeTrue();
        return owner;
    }

    private async Task<Guid> CreateBucketAsync(OwnerId owner)
    {
        var id = Guid.CreateVersion7();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<CreateBucketHandler>(scope.ServiceProvider)
            .Handle(new CreateBucket(id, "Cushion", Currency.Pln, BucketKind.Savings, null), Cancellation)).IsSuccess.ShouldBeTrue();
        return id;
    }

    private async Task<BudgetSnapshot> StateAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return (await scope.ServiceProvider.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation)).Value;
    }
}
