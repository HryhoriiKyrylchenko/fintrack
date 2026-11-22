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

namespace FinTrack.Budget.Application.Plans;

/// <summary>
/// A plan's new last month, or no end (<c>POST /api/v1/plans/{id}/end</c>, plan 1b B7), written as a <c>PlanEndChanged</c>. The end
/// stops new months, not the obligation. Fails with <see cref="BudgetErrorCodes.PlanNotFound"/> (404);
/// <see cref="BudgetErrorCodes.BucketArchived"/> (422); <see cref="BudgetErrorCodes.PlanMonthsInvalid"/> (400: an end before the plan's
/// first month); the entry's own rules; and <see cref="BudgetErrorCodes.PlanPastPeriodChanged"/> (422) when it would remove or add a
/// month before the current one (invariant 6, checked by <see cref="JournalWriter"/>): a plan can be ended as of last month, and an
/// ended plan extended only when no past month would accrue again. Repeats as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client.</param>
/// <param name="PlanId">The plan.</param>
/// <param name="ValidTo">The last month, or <c>null</c> to remove the end.</param>
/// <param name="OccurredOn">The day the owner made the change.</param>
/// <param name="Note">An optional note.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record SetPlanEnd(
    Guid Id,
    Guid PlanId,
    YearMonth? ValidTo,
    DateOnly OccurredOn,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks: a plan ID, note and tags.</summary>
public sealed class SetPlanEndValidator : AbstractValidator<SetPlanEnd>
{
    /// <summary>Creates the rules.</summary>
    public SetPlanEndValidator()
    {
        RuleFor(command => command.PlanId).NotEmpty();
        RuleFor(command => command.Note).EntryNote();
        RuleFor(command => command.Tags).EntryTags();
        RuleForEach(command => command.Tags).EntryTag();
    }
}

/// <summary>Writes the end through <see cref="JournalWriter"/>, whose replay holds it to invariant 6.</summary>
public sealed class SetPlanEndHandler(JournalWriter writer) : ICommandHandler<SetPlanEnd, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(SetPlanEnd command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command.Id,
            context =>
            {
                // Built from the command's plan ID; the plan is checked among the rules, after a repeat (invariant 13).
                var planId = Domain.Plans.PlanId.From(command.PlanId);
                var entry = PlanEndChanged.Create(
                        context.EntryId, context.Owner, planId, command.ValidTo, command.OccurredOn, context.Today,
                        new EntryInfo(command.Note, command.Tags, EntrySource.Manual), context.Now)
                    .Map(ended => (JournalEntry)ended);

                return Result.Ok(new JournalDraft(entry, Rules(command.ValidTo, PlanRules.FindChangeable(planId, context.State, context.Balances))));
            },
            cancellationToken);
    }

    // The plan, its bucket active, and an end not before its first month.
    private static Result Rules(YearMonth? validTo, Result<Plan> found)
    {
        if (found.IsFailed)
        {
            return found.ToResult();
        }

        return validTo < found.Value.ValidFrom
            ? Result.Fail(AppError.Validation(BudgetErrorCodes.PlanMonthsInvalid))
            : Result.Ok();
    }
}
