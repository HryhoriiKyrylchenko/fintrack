using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using FluentValidation;
using Mediator;
using ContractPauseMode = FinTrack.Budget.Contracts.V1.Plans.PauseMode;

namespace FinTrack.Budget.Application.Plans;

/// <summary>
/// A pause of a plan (<c>POST /api/v1/plans/{id}/pauses</c>, plan 1b B10), written as a <c>PlanPaused</c> whose ID is the pause's.
/// Fails with <see cref="BudgetErrorCodes.PlanNotFound"/> (404); <see cref="BudgetErrorCodes.BucketArchived"/>,
/// <see cref="BudgetErrorCodes.PlanEnded"/>, <see cref="BudgetErrorCodes.PlanPauseOverlaps"/> (422); the entry's own rules
/// (<see cref="BudgetErrorCodes.PlanMonthsInvalid"/>, <see cref="BudgetErrorCodes.EntryDateInFuture"/>); and
/// <see cref="BudgetErrorCodes.PlanPastPeriodChanged"/> (422) when it would change what a month before the current one accrued
/// (invariant 6, checked by <see cref="JournalWriter"/>). Repeats as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client; also the pause's ID.</param>
/// <param name="PlanId">The plan.</param>
/// <param name="From">The first paused month.</param>
/// <param name="To">The last paused month, or <c>null</c> until changed.</param>
/// <param name="Mode">Shift or skip.</param>
/// <param name="OccurredOn">The day the owner made the pause.</param>
/// <param name="Note">An optional note.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record PausePlan(
    Guid Id,
    Guid PlanId,
    YearMonth From,
    YearMonth? To,
    ContractPauseMode Mode,
    DateOnly OccurredOn,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks: a plan ID, a defined mode, note and tags.</summary>
public sealed class PausePlanValidator : AbstractValidator<PausePlan>
{
    /// <summary>Creates the rules.</summary>
    public PausePlanValidator()
    {
        RuleFor(command => command.PlanId).NotEmpty();
        RuleFor(command => command.Mode).IsInEnum();
        RuleFor(command => command.Note).EntryNote();
        RuleFor(command => command.Tags).EntryTags();
        RuleForEach(command => command.Tags).EntryTag();
    }
}

/// <summary>Writes the pause through <see cref="JournalWriter"/>, whose replay holds it to invariant 6.</summary>
public sealed class PausePlanHandler(JournalWriter writer) : ICommandHandler<PausePlan, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(PausePlan command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command.Id,
            context =>
            {
                // Built from the command's plan ID; the plan is checked among the rules, after a repeat (invariant 13).
                var planId = Domain.Plans.PlanId.From(command.PlanId);
                var mode = PlanContractMapping.ToDomain(command.Mode);
                var entry = PlanPaused.Create(
                        context.EntryId, context.Owner, planId, command.From, command.To, mode, command.OccurredOn, context.Today,
                        new EntryInfo(command.Note, command.Tags, EntrySource.Manual), context.Now)
                    .Map(paused => (JournalEntry)paused);

                var pause = new PlanPause(PauseId.From(context.EntryId.Value), command.From, command.To, mode);
                return Result.Ok(new JournalDraft(entry, Rules(PlanRules.FindChangeable(planId, context.State, context.Balances), pause, context.Balances)));
            },
            cancellationToken);
    }

    // The plan, its bucket active, not ended, and no other pause of it sharing a month.
    private static Result Rules(Result<Plan> found, PlanPause pause, BudgetState state)
    {
        if (found.IsFailed)
        {
            return found.ToResult();
        }

        if (state.StandingOf(found.Value).Status == PlanStatus.Ended)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.PlanEnded));
        }

        return PlanRules.CheckOverlap(found.Value, pause);
    }
}
