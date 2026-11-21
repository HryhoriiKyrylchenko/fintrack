using FinTrack.Budget.Contracts.V1.Loans;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using Mediator;
using Model = FinTrack.Budget.Domain.Loans;

namespace FinTrack.Budget.Application.Loans;

/// <summary>
/// Answers <see cref="GetLoans"/> from the replayed state for today (<see cref="BudgetStateLoader"/>, <c>BudgetState</c>): every
/// loan of an effective borrow, in the replay order of the borrows, closed ones on request (plan 1b B19).
/// </summary>
public sealed class GetLoansHandler(BudgetStateLoader loader, IClock clock) : IQueryHandler<GetLoans, Result<IReadOnlyList<LoanDto>>>
{
    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<LoanDto>>> Handle(GetLoans query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var loaded = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailed)
        {
            return loaded.ToResult<IReadOnlyList<LoanDto>>();
        }

        var snapshot = loaded.Value;
        if (snapshot.Settings is not { } settings)
        {
            // The loans are for today in the owner's time zone (plan 1b B20), which needs the settings.
            return Result.Fail<IReadOnlyList<LoanDto>>(AppError.NotFound(BudgetErrorCodes.SettingsNotFound));
        }

        var state = snapshot.StateAsOf(settings.Today(clock.UtcNow));
        IReadOnlyList<LoanDto> loans =
        [
            .. state.Loans
                .Where(loan => query.IncludeClosed || loan.Status == Model.LoanStatus.Open)
                .Select(loan => ToDto(loan, (Borrow)snapshot.FindEntry(loan.LoanId.Value)!)),
        ];

        return Result.Ok(loans);
    }

    /// <summary>The contract's copy of <paramref name="status"/>.</summary>
    private static LoanStatus ToContract(Model.LoanStatus status) => status switch
    {
        Model.LoanStatus.Open => LoanStatus.Open,
        Model.LoanStatus.Closed => LoanStatus.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown loan status."),
    };

    private static LoanDto ToDto(Model.Loan loan, Borrow borrow) => new(
        loan.LoanId.Value,
        loan.LenderBucketId.Value,
        loan.ToBucketId?.Value,
        loan.Principal,
        loan.BorrowedOn,
        loan.Repaid,
        loan.Outstanding,
        ToContract(loan.Status),
        loan.ClosedOn,
        borrow.Note);
}
