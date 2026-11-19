using FluentResults;
using Mediator;

namespace FinTrack.Budget.Contracts.V1.Overview;

/// <summary>
/// Where the owner's money stands now: a total per currency, the total in the reporting currency, and one
/// summary per active bucket (ARCHITECTURE 13.1, phase 1a). Computed by replaying the journal on every
/// request; nothing here is stored. <c>GET /api/v1/overview</c>.
/// </summary>
/// <remarks>
/// Fails with <c>NotFound</c> <c>settings.not_found</c> before the owner's settings are saved (the reporting
/// currency is needed). A missing exchange rate never fails the query: the converted total is then left out
/// with its reason (<see cref="ReportingTotalDto.UnavailableReason"/>), and the per-currency totals stay.
/// </remarks>
public sealed record GetBudgetOverview : IQuery<Result<BudgetOverviewDto>>;
