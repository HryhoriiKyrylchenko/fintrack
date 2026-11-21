using FluentResults;
using Mediator;

namespace FinTrack.Budget.Contracts.V1.Loans;

/// <summary>
/// The owner's loans from their savings buckets (ARCHITECTURE 4.4, plan 1b B19), in the order their borrows were made
/// (business date, then recorded time). Closed loans are left out unless <paramref name="IncludeClosed"/> is set; a reversed
/// borrow is no loan. <c>GET /api/v1/loans[?includeClosed=true]</c>.
/// </summary>
/// <remarks>
/// Every figure is replayed from the journal for today in the owner's time zone; nothing is stored. Failures:
/// <c>settings.not_found</c> (404) before the owner's first setup; <c>validation.failed</c> (400) at <c>includeClosed</c>
/// (rule <c>QueryFormat</c>) for a value other than <c>true</c> or <c>false</c>. A loan's repayment plan joins in step 12.
/// </remarks>
/// <param name="IncludeClosed">Whether fully repaid loans are listed too.</param>
public sealed record GetLoans(bool IncludeClosed = false) : IQuery<Result<IReadOnlyList<LoanDto>>>;
