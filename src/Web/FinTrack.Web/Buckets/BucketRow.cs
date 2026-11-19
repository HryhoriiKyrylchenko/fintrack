using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Resources;
using Microsoft.Extensions.Localization;

namespace FinTrack.Web.Buckets;

/// <summary>One bucket as the list shows it: its name, its kind, and what the owner can do with it.</summary>
/// <param name="Bucket">The bucket.</param>
public sealed record BucketRow(BucketDto Bucket)
{
    /// <summary>Whether the system created the bucket (<c>Unallocated</c>): it cannot be renamed or archived.</summary>
    public bool IsSystem => Bucket.SystemRole != BucketSystemRole.None;

    /// <summary>Whether the bucket is archived.</summary>
    public bool IsArchived => Bucket.Status == BucketStatus.Archived;

    /// <summary>Whether the bucket can be renamed: the owner's own, and not archived.</summary>
    public bool CanRename => !IsSystem && !IsArchived;

    /// <summary>Whether archiving can be offered; the server still refuses a bucket that holds money (<c>bucket.not_empty</c>).</summary>
    public bool CanArchive => !IsSystem && !IsArchived;

    /// <summary>The resource key that describes the bucket's kind and profile.</summary>
    public string KindKey => (Bucket.Kind, Bucket.Profile) switch
    {
        (BucketKind.Investment, InvestmentProfile.Crypto) => "Buckets.Kind.Crypto",
        (BucketKind.Investment, InvestmentProfile.RealValueAssets) => "Buckets.Kind.RealValueAssets",
        (BucketKind.Investment, _) => "Buckets.Kind.Investment",
        _ => "Buckets.Kind.Savings",
    };

    /// <summary>The name to show: the owner's name, or for <c>Unallocated</c> the resource text with its currency.</summary>
    public string DisplayName(IStringLocalizer<SharedResource> localizer) => BucketNames.Display(Bucket, localizer);
}

/// <summary>The one place that names a bucket for the owner, so every screen names <c>Unallocated</c> alike.</summary>
public static class BucketNames
{
    /// <summary>The owner's name of <paramref name="bucket"/>, or the resource name of a system bucket with its currency code.</summary>
    public static string Display(BucketDto bucket, IStringLocalizer<SharedResource> localizer)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        return Display(bucket.Name, bucket.Currency, localizer);
    }

    /// <summary>The owner's <paramref name="name"/>, or for a system bucket (no name) the resource name with <paramref name="currency"/>'s code.</summary>
    public static string Display(string? name, Currency currency, IStringLocalizer<SharedResource> localizer)
    {
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(localizer);

        return name ?? localizer["Buckets.Unallocated", currency.Code];
    }
}
