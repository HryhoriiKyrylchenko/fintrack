using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using FluentValidation;
using Mediator;

namespace FinTrack.Budget.Application.Plans;

/// <summary>
/// Part of a plan's shortfall written off, with its reason (<c>POST /api/v1/plans/{id}/waivers</c>, plan 1b B11), written as a
/// <c>ShortfallWaived</c>. Fails with <see cref="BudgetErrorCodes.PlanNotFound"/> (404); <see cref="BudgetErrorCodes.BucketArchived"/>,
/// <see cref="BudgetErrorCodes.BucketCurrencyMismatch"/>, <see cref="BudgetErrorCodes.PlanWaiverExceedsShortfall"/> (422: above what the
/// months before its date's month still lack); and the entry's own rules. A missing reason is <c>validation.failed</c> at <c>note</c>.
/// Repeats as for <see cref="AddDeposit"/>.
/// </summary>
/// <param name="Id">The entry's ID, a version 7 UUID from the client.</param>
/// <param name="PlanId">The plan.</param>
/// <param name="Amount">Strictly positive, in the plan's currency.</param>
/// <param name="OccurredOn">The day of the waiver.</param>
/// <param name="Note">Required: the reason.</param>
/// <param name="Tags">Tags, possibly none.</param>
public sealed record WaiveShortfall(
    Guid Id,
    Guid PlanId,
    Money Amount,
    DateOnly OccurredOn,
    string? Note,
    IReadOnlyList<string> Tags) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks: a plan ID, the amount, a reason (<c>NotEmptyValidator</c> at <c>note</c>), note and tags.</summary>
public sealed class WaiveShortfallValidator : AbstractValidator<WaiveShortfall>
{
    /// <summary>Creates the rules.</summary>
    public WaiveShortfallValidator()
    {
        RuleFor(command => command.PlanId).NotEmpty();
        RuleFor(command => command.Amount).NotNull();
        RuleFor(command => command.Note).NotEmpty();
        RuleFor(command => command.Note).EntryNote();
        RuleFor(command => command.Tags).EntryTags();
        RuleForEach(command => command.Tags).EntryTag();
    }
}

/// <summary>Writes the waiver through <see cref="JournalWriter"/> after checking it fits the shortfall at its date.</summary>
public sealed class WaiveShortfallHandler(JournalWriter writer) : ICommandHandler<WaiveShortfall, Result>
{
    /// <inheritdoc />
    public ValueTask<Result> Handle(WaiveShortfall command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return writer.WriteAsync(
            command.Id,
            context =>
            {
                // Built from the command's plan ID; the plan is checked among the rules, after a repeat (invariant 13).
                var planId = Domain.Plans.PlanId.From(command.PlanId);
                var entry = ShortfallWaived.Create(
                        context.EntryId, context.Owner, planId, command.Amount, command.OccurredOn, context.Today,
                        new EntryInfo(command.Note, command.Tags, EntrySource.Manual), context.Now)
                    .Map(waived => (JournalEntry)waived);

                return Result.Ok(new JournalDraft(entry, Rules(PlanRules.FindChangeable(planId, context.State, context.Balances), command)));
            },
            cancellationToken);
    }

    // The plan, its bucket active, its currency, and an amount within the shortfall at the waiver's date (B11).
    private static Result Rules(Result<Plan> found, WaiveShortfall command)
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

        return command.Amount > PlanSchedule.ShortfallAt(plan, YearMonth.FromDate(command.OccurredOn))
            ? Result.Fail(AppError.BusinessRule(BudgetErrorCodes.PlanWaiverExceedsShortfall))
            : Result.Ok();
    }
}
