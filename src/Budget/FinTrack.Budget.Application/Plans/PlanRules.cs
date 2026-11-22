using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Budget.Domain.State;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Application.Plans;

/// <summary>
/// The rules about plans that several commands share. Each runs among a command's rules, after the repeat check: a
/// repeated command must succeed even when its plan has changed since (invariant 13).
/// </summary>
internal static class PlanRules
{
    /// <summary>
    /// A contribution's plan (plan 1b B9): none, or an effective plan (<see cref="BudgetErrorCodes.PlanNotFound"/>) of the
    /// receiving bucket (<see cref="BudgetErrorCodes.PlanBucketMismatch"/>).
    /// </summary>
    public static Result CheckContribution(Guid? planId, Bucket receiving, BudgetState state)
    {
        if (planId is not { } id)
        {
            return Result.Ok();
        }

        if (state.FindPlan(PlanId.From(id)) is not { } plan)
        {
            return Result.Fail(AppError.NotFound(BudgetErrorCodes.PlanNotFound));
        }

        return plan.BucketId == receiving.BucketId
            ? Result.Ok()
            : Result.Fail(AppError.BusinessRule(BudgetErrorCodes.PlanBucketMismatch));
    }

    /// <summary>
    /// The plan a change names: an effective plan (<see cref="BudgetErrorCodes.PlanNotFound"/>) whose bucket is not archived
    /// (<see cref="BudgetErrorCodes.BucketArchived"/>: an archived bucket accepts no change).
    /// </summary>
    public static Result<Plan> FindChangeable(PlanId planId, BudgetSnapshot snapshot, BudgetState state)
    {
        if (state.FindPlan(planId) is not { } plan)
        {
            return Result.Fail<Plan>(AppError.NotFound(BudgetErrorCodes.PlanNotFound));
        }

        return snapshot.FindBucket(plan.BucketId.Value) is { Status: BucketStatus.Archived }
            ? Result.Fail<Plan>(AppError.BusinessRule(BudgetErrorCodes.BucketArchived))
            : Result.Ok(plan);
    }
}
