using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Loans;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using BucketKind = FinTrack.Budget.Contracts.V1.Buckets.BucketKind;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Journal;

/// <summary>
/// Loans (plan 1b step 5) through the real ledger and MongoDB: a loan that opens, is repaid in parts, closes and reopens
/// when a repayment is reversed; the refusals (above the outstanding principal, before the borrow, reversing a borrow with
/// repayments, archiving a lender with an open loan) each write nothing.
/// </summary>
public sealed class LoanTests(BudgetFixture fixture)
{
    // The fixture's clock is FakeClock.DefaultStart: 2026-01-15 in Warsaw.
    private static readonly DateOnly Today = new(2026, 1, 15);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALoan_Opens_IsRepaidInParts_Closes_AndReopensWhenARepaymentIsReversed()
    {
        var owner = await NewOwnerAsync();
        var cushion = await CreateBucketAsync(owner, "Cushion");
        var car = await CreateBucketAsync(owner, "Car");
        await DepositAsync(owner, cushion, 1000m, -20);
        var loan = Guid.CreateVersion7();

        (await SendAsync<AddBorrowHandler, AddBorrow>(owner, new AddBorrow(loan, cushion, Pln(300m), Today.AddDays(-10), car, null, "Repair", []))).IsSuccess.ShouldBeTrue();
        (await SendAsync<AddRepaymentHandler, AddRepayment>(owner, new AddRepayment(Guid.CreateVersion7(), loan, Pln(100m), Today.AddDays(-6), car, null, null, []))).IsSuccess.ShouldBeTrue();
        var last = Guid.CreateVersion7();
        (await SendAsync<AddRepaymentHandler, AddRepayment>(owner, new AddRepayment(last, loan, Pln(200m), Today.AddDays(-2), null, null, null, []))).IsSuccess.ShouldBeTrue();

        var closed = (await StateAsync(owner)).StateAsOf(Today);
        closed.FindLoan(LoanId.From(loan))!.Status.ShouldBe(LoanStatus.Closed);
        closed.FindLoan(LoanId.From(loan))!.ClosedOn.ShouldBe(Today.AddDays(-2));
        closed.BalanceOf(BucketId.From(cushion)).Available.ShouldBe(Pln(1000m));
        closed.BalanceOf(BucketId.From(car)).NetContributed.ShouldBe(Pln(200m));

        (await SendAsync<ReverseEntryHandler, ReverseEntry>(owner, new ReverseEntry(Guid.CreateVersion7(), last, Today, null, []))).IsSuccess.ShouldBeTrue();

        var reopened = (await StateAsync(owner)).StateAsOf(Today);
        reopened.FindLoan(LoanId.From(loan))!.Status.ShouldBe(LoanStatus.Open);
        reopened.FindLoan(LoanId.From(loan))!.Outstanding.ShouldBe(Pln(200m));
        reopened.BalanceOf(BucketId.From(cushion)).OutstandingPrincipal.ShouldBe(Pln(200m));
    }

    [Fact]
    public async Task EveryRefusal_WritesNothing()
    {
        var owner = await NewOwnerAsync();
        var cushion = await CreateBucketAsync(owner, "Cushion");
        await DepositAsync(owner, cushion, 1000m, -20);
        var loan = Guid.CreateVersion7();
        (await SendAsync<AddBorrowHandler, AddBorrow>(owner, new AddBorrow(loan, cushion, Pln(300m), Today.AddDays(-10), null, null, null, []))).IsSuccess.ShouldBeTrue();
        (await SendAsync<AddRepaymentHandler, AddRepayment>(owner, new AddRepayment(Guid.CreateVersion7(), loan, Pln(100m), Today.AddDays(-6), null, null, null, []))).IsSuccess.ShouldBeTrue();
        var before = await StateAsync(owner);

        (await SendAsync<AddRepaymentHandler, AddRepayment>(owner, new AddRepayment(Guid.CreateVersion7(), loan, Pln(200.01m), Today, null, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.LoanRepaymentExceedsOutstanding, ErrorKind.BusinessRule).Parameters["date"].ShouldBe("2026-01-15");
        (await SendAsync<AddRepaymentHandler, AddRepayment>(owner, new AddRepayment(Guid.CreateVersion7(), loan, Pln(1m), Today.AddDays(-12), null, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.LoanRepaymentExceedsOutstanding, ErrorKind.BusinessRule).Parameters["date"].ShouldBe("2026-01-03");
        (await SendAsync<ReverseEntryHandler, ReverseEntry>(owner, new ReverseEntry(Guid.CreateVersion7(), loan, Today, null, [])))
            .ShouldFailWith(BudgetErrorCodes.EntryHasDependents, ErrorKind.BusinessRule);
        (await SendAsync<ArchiveBucketHandler, ArchiveBucket>(owner, new ArchiveBucket(cushion)))
            .ShouldFailWith(BudgetErrorCodes.BucketHasOpenLoan, ErrorKind.BusinessRule);
        (await SendAsync<AddRepaymentHandler, AddRepayment>(owner, new AddRepayment(Guid.CreateVersion7(), Guid.CreateVersion7(), Pln(1m), Today, null, null, null, [])))
            .ShouldFailWith(BudgetErrorCodes.LoanNotFound, ErrorKind.NotFound);

        var after = await StateAsync(owner);
        after.Version.ShouldBe(before.Version);
        after.Journal.Count.ShouldBe(before.Journal.Count);
        after.Buckets.Single(bucket => bucket.Id == cushion).Status.ShouldBe(BucketStatus.Active);
    }

    [Fact]
    public async Task ABorrowIntoUnallocated_CreatesItInTheSameCommit_AndARepeatWritesNothing()
    {
        var owner = await NewOwnerAsync();
        var cushion = await CreateBucketAsync(owner, "Cushion");
        await DepositAsync(owner, cushion, 100m, -20);
        var borrow = new AddBorrow(Guid.CreateVersion7(), cushion, Pln(40m), Today, null, Currency.Pln, null, []);
        var before = await StateAsync(owner);

        (await SendAsync<AddBorrowHandler, AddBorrow>(owner, borrow)).IsSuccess.ShouldBeTrue();
        var written = await StateAsync(owner);
        written.Version.ShouldBe(before.Version + 1);
        var unallocated = written.Buckets.Where(bucket => bucket.SystemRole == BucketSystemRole.Unallocated).ShouldHaveSingleItem();
        written.StateAsOf(Today).BalanceOf(unallocated.BucketId).Available.ShouldBe(Pln(40m));

        (await SendAsync<AddBorrowHandler, AddBorrow>(owner, borrow)).IsSuccess.ShouldBeTrue();
        (await StateAsync(owner)).Version.ShouldBe(written.Version);
    }

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);

    private async Task<OwnerId> NewOwnerAsync()
    {
        var owner = BudgetFixture.NewOwner();
        await using var scope = fixture.ScopeFor(owner);
        (await ActivatorUtilities.CreateInstance<SaveOwnerSettingsHandler>(scope.ServiceProvider)
            .Handle(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"), Cancellation)).IsSuccess.ShouldBeTrue();
        return owner;
    }

    private async Task<Guid> CreateBucketAsync(OwnerId owner, string name)
    {
        var id = Guid.CreateVersion7();
        (await SendAsync<CreateBucketHandler, CreateBucket>(owner, new CreateBucket(id, name, Currency.Pln, BucketKind.Savings, null))).IsSuccess.ShouldBeTrue();
        return id;
    }

    private async Task DepositAsync(OwnerId owner, Guid bucket, decimal amount, int day) =>
        (await SendAsync<AddDepositHandler, AddDeposit>(owner, new AddDeposit(Guid.CreateVersion7(), Pln(amount), Today.AddDays(day), bucket, null, null, []))).IsSuccess.ShouldBeTrue();

    private async Task<Result> SendAsync<THandler, TCommand>(OwnerId owner, TCommand command)
        where THandler : Mediator.ICommandHandler<TCommand, Result>
        where TCommand : Mediator.ICommand<Result>
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<THandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<BudgetSnapshot> StateAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return (await scope.ServiceProvider.GetRequiredService<BudgetStateLoader>().LoadAsync(Cancellation)).Value;
    }
}
