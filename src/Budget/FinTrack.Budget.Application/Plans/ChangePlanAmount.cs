using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using FluentValidation;
using Mediator;

namespace FinTrack.Budget.Application.Plans;

/// <summary>
/// A plan's new monthly amount from a month on (<c>POST /api/v1/plans/{id}/amount</c>, plan 1b B7): a new version, written as
/// a <c>PlanAmountChanged</c>. Fails with <see cref="BudgetErrorCodes.PlanNotFound"/> (404); <see cref="BudgetErrorCodes.BucketArchived"/>,
/// <see cref="BudgetErrorCodes.BucketCurrencyMismatch"/>, <see cref="BudgetErrorCodes.PlanEnded"/> (422);
/// <see cref="BudgetErrorCodes.PlanMonthsInvalid"/> (400: <paramref name="From"/> outside the plan's months); the entry's own rules; and
/// <see cref="BudgetErrorCodes.PlanPastPeriodChanged"/> (422) when it would change what a month before the current one accrued
/// (invariant 6, checked by <see cref="JournalWriter"/>). Repeats as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client.</param>
/// <param name="PlanId">The plan.</param>
/// <param name="Amount">The new monthly amount, strictly positive, in the plan's currency.</param>
/// <param name="From">The first month of the new amount.</param>
/// <param name="OccurredOn">The day the owner made the change.</param>
/// <param name="Note">An optional note.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record ChangePlanAmount(
    Guid Id,
    Guid PlanId,
    Money Amount,
    YearMonth From,
    DateOnly OccurredOn,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks: a plan ID, the amount, note and tags.</summary>
public sealed class ChangePlanAmountValidator : AbstractValidator<ChangePlanAmount>
{
    /// <summary>Creates the rules.</summary>
    public ChangePlanAmountValidator()
    {
        RuleFor(command => command.PlanId).NotEmpty();
        RuleFor(command => command.Amount).NotNull();
        RuleFor(command => command.Note).EntryNote();
        RuleFor(command => command.Tags).EntryTags();
        RuleForEach(command => command.Tags).EntryTag();
    }
}

/// <summary>Writes the version through <see cref="JournalWriter"/>, whose replay holds it to invariant 6.</summary>
public sealed class ChangePlanAmountHandler(JournalWriter writer) : ICommandHandler<ChangePlanAmount, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(ChangePlanAmount command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command.Id,
            context =>
            {
                // Built from the command's plan ID; the plan is checked among the rules, after a repeat (invariant 13).
                var planId = Domain.Plans.PlanId.From(command.PlanId);
                var entry = PlanAmountChanged.Create(
                        context.EntryId, context.Owner, planId, command.Amount, command.From, command.OccurredOn, context.Today,
                        new EntryInfo(command.Note, command.Tags, EntrySource.Manual), context.Now)
                    .Map(changed => (JournalEntry)changed);

                return Result.Ok(new JournalDraft(entry, Rules(command, PlanRules.FindChangeable(planId, context.State, context.Balances), context.Balances)));
            },
            cancellationToken);
    }

    // The plan, its bucket active, the plan's currency, not ended, and a month within the plan's months.
    private static Result Rules(ChangePlanAmount command, Result<Plan> found, BudgetState state)
    {
        if (found.IsFailed)
        {
            return found.ToResult();
        }

        var plan = found.Value;
        if (command.Amount.Currency != plan.Currency)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.BucketCurrencyMismatch));
        }

        if (state.StandingOf(plan).Status == PlanStatus.Ended)
        {
            return Result.Fail(AppError.BusinessRule(BudgetErrorCodes.PlanEnded));
        }

        return command.From < plan.ValidFrom || command.From > plan.ValidTo
            ? Result.Fail(AppError.Validation(BudgetErrorCodes.PlanMonthsInvalid))
            : Result.Ok();
    }
}
