using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using FluentValidation;
using Mediator;
using ContractPauseMode = FinTrack.Budget.Contracts.V1.Plans.PauseMode;

namespace FinTrack.Budget.Application.Plans;

/// <summary>
/// New values for a pause of a plan (<c>POST /api/v1/plans/{id}/pauses/{pauseId}</c>, plan 1b B10), written as a
/// <c>PlanPauseChanged</c>; resuming is a change that sets <paramref name="To"/>. Fails with <see cref="BudgetErrorCodes.PlanNotFound"/>,
/// <see cref="BudgetErrorCodes.PlanPauseNotFound"/> (404, also for a pause of another plan); <see cref="BudgetErrorCodes.BucketArchived"/>,
/// <see cref="BudgetErrorCodes.PlanPauseOverlaps"/> (422); the entry's own rules; and <see cref="BudgetErrorCodes.PlanPastPeriodChanged"/>
/// (422, invariant 6, checked by <see cref="JournalWriter"/>). Repeats as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client.</param>
/// <param name="PlanId">The plan.</param>
/// <param name="PauseId">The pause: its <c>PlanPaused</c> entry's ID.</param>
/// <param name="From">The first paused month.</param>
/// <param name="To">The last paused month, or <c>null</c> until changed.</param>
/// <param name="Mode">Shift or skip.</param>
/// <param name="OccurredOn">The day the owner made the change.</param>
/// <param name="Note">An optional note.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record ChangePlanPause(
    Guid Id,
    Guid PlanId,
    Guid PauseId,
    YearMonth From,
    YearMonth? To,
    ContractPauseMode Mode,
    DateOnly OccurredOn,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks: the plan and pause IDs, a defined mode, note and tags.</summary>
public sealed class ChangePlanPauseValidator : AbstractValidator<ChangePlanPause>
{
    /// <summary>Creates the rules.</summary>
    public ChangePlanPauseValidator()
    {
        RuleFor(command => command.PlanId).NotEmpty();
        RuleFor(command => command.PauseId).NotEmpty();
        RuleFor(command => command.Mode).IsInEnum();
        RuleFor(command => command.Note).EntryNote();
        RuleFor(command => command.Tags).EntryTags();
        RuleForEach(command => command.Tags).EntryTag();
    }
}

/// <summary>Writes the change through <see cref="JournalWriter"/>, whose replay holds it to invariant 6.</summary>
public sealed class ChangePlanPauseHandler(JournalWriter writer) : ICommandHandler<ChangePlanPause, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(ChangePlanPause command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command.Id,
            context =>
            {
                // Built from the command's IDs; the plan and the pause are checked among the rules, after a repeat (invariant 13).
                var pauseId = Domain.Plans.PauseId.From(command.PauseId);
                var mode = PlanContractMapping.ToDomain(command.Mode);
                var entry = PlanPauseChanged.Create(
                        context.EntryId, context.Owner, pauseId, command.From, command.To, mode, command.OccurredOn, context.Today,
                        new EntryInfo(command.Note, command.Tags, EntrySource.Manual), context.Now)
                    .Map(changed => (JournalEntry)changed);

                var rules = PlanRules.FindChangeable(Domain.Plans.PlanId.From(command.PlanId), context.State, context.Balances)
                    .Bind(plan => plan.Pauses.Any(pause => pause.PauseId == pauseId)
                        ? PlanRules.CheckOverlap(plan, new PlanPause(pauseId, command.From, command.To, mode))
                        : Result.Fail(AppError.NotFound(BudgetErrorCodes.PlanPauseNotFound)));
                return Result.Ok(new JournalDraft(entry, rules));
            },
            cancellationToken);
    }
}
