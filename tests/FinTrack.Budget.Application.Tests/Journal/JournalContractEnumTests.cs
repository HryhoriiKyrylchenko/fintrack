using Contract = FinTrack.Budget.Contracts.V1.Journal;
using Model = FinTrack.Budget.Domain.Journal;

namespace FinTrack.Budget.Application.Tests.Journal;

/// <summary>
/// The V1 contract has its own copy of the adjustment direction (Contracts may not reference Domain). Its member names
/// are the JSON and the stored names, so the two copies must stay identical. The mapping of each member to its namesake is
/// covered by the handler (contract to domain) and the journal query (domain to contract).
/// </summary>
public class JournalContractEnumTests
{
    [Fact]
    public void AdjustmentDirections_Match() =>
        Enum.GetNames<Contract.AdjustmentDirection>().ShouldBe(Enum.GetNames<Model.AdjustmentDirection>());
}
