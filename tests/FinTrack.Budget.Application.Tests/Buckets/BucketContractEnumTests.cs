using Contract = FinTrack.Budget.Contracts.V1.Buckets;
using Model = FinTrack.Budget.Domain.Buckets;

namespace FinTrack.Budget.Application.Tests.Buckets;

/// <summary>
/// The V1 contract has its own copies of the bucket enums (Contracts may not reference Domain). Their
/// member names are the JSON and the stored names, so the two copies must stay identical.
/// </summary>
public class BucketContractEnumTests
{
    [Fact]
    public void Kinds_Match() => Enum.GetNames<Contract.BucketKind>().ShouldBe(Enum.GetNames<Model.BucketKind>());

    [Fact]
    public void Profiles_Match() => Enum.GetNames<Contract.InvestmentProfile>().ShouldBe(Enum.GetNames<Model.InvestmentProfile>());

    [Fact]
    public void Statuses_Match() => Enum.GetNames<Contract.BucketStatus>().ShouldBe(Enum.GetNames<Model.BucketStatus>());

    [Fact]
    public void SystemRoles_Match() => Enum.GetNames<Contract.BucketSystemRole>().ShouldBe(Enum.GetNames<Model.BucketSystemRole>());
}
