using FinTrack.Budget.Contracts.V1.Buckets;
using Model = FinTrack.Budget.Domain.Buckets;

namespace FinTrack.Budget.Application.Buckets;

/// <summary>
/// Converts between the V1 contract's bucket types and the domain's. Written out member by member:
/// the two sets of enums are separate on purpose (Contracts may not reference Domain), and a new
/// member must be mapped deliberately (an unmapped one throws, which is a bug).
/// </summary>
internal static class BucketContractMapping
{
    public static Model.BucketKind ToDomain(BucketKind kind) => kind switch
    {
        BucketKind.Savings => Model.BucketKind.Savings,
        BucketKind.Investment => Model.BucketKind.Investment,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown bucket kind."),
    };

    public static Model.InvestmentProfile? ToDomain(InvestmentProfile? profile) => profile switch
    {
        null => null,
        InvestmentProfile.Crypto => Model.InvestmentProfile.Crypto,
        InvestmentProfile.RealValueAssets => Model.InvestmentProfile.RealValueAssets,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown investment profile."),
    };

    public static BucketDto ToDto(Model.Bucket bucket) => new(
        bucket.Id,
        bucket.Name,
        bucket.Currency,
        ToContract(bucket.Kind),
        ToContract(bucket.Profile),
        bucket.Status switch
        {
            Model.BucketStatus.Active => BucketStatus.Active,
            Model.BucketStatus.Archived => BucketStatus.Archived,
            _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket.Status, "Unknown bucket status."),
        },
        ToContract(bucket.SystemRole));

    public static BucketKind ToContract(Model.BucketKind kind) => kind switch
    {
        Model.BucketKind.Savings => BucketKind.Savings,
        Model.BucketKind.Investment => BucketKind.Investment,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown bucket kind."),
    };

    public static InvestmentProfile? ToContract(Model.InvestmentProfile? profile) => profile switch
    {
        null => null,
        Model.InvestmentProfile.Crypto => InvestmentProfile.Crypto,
        Model.InvestmentProfile.RealValueAssets => InvestmentProfile.RealValueAssets,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown investment profile."),
    };

    public static BucketSystemRole ToContract(Model.BucketSystemRole role) => role switch
    {
        Model.BucketSystemRole.None => BucketSystemRole.None,
        Model.BucketSystemRole.Unallocated => BucketSystemRole.Unallocated,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown system role."),
    };
}
