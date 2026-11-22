using Contract = FinTrack.Budget.Contracts.V1.Plans;
using Model = FinTrack.Budget.Domain.Plans;

namespace FinTrack.Budget.Application.Plans;

/// <summary>Converts the plan enums between the V1 contract and the domain, member by member (an unknown one throws, a bug).</summary>
internal static class PlanContractMapping
{
    /// <summary>The domain's copy of <paramref name="mode"/>.</summary>
    public static Model.PauseMode ToDomain(Contract.PauseMode mode) => mode switch
    {
        Contract.PauseMode.Shift => Model.PauseMode.Shift,
        Contract.PauseMode.Skip => Model.PauseMode.Skip,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown pause mode."),
    };

    /// <summary>The contract's copy of <paramref name="mode"/>.</summary>
    public static Contract.PauseMode ToContract(Model.PauseMode mode) => mode switch
    {
        Model.PauseMode.Shift => Contract.PauseMode.Shift,
        Model.PauseMode.Skip => Contract.PauseMode.Skip,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown pause mode."),
    };
}
