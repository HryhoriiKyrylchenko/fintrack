using System.Reflection;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Domain.Tests.Buckets;

/// <summary>Builds buckets as the database returns them, through the private constructor, as the mapping does. Tests only.</summary>
internal static class StoredBuckets
{
    public static Bucket Restore(
        BucketId id,
        OwnerId ownerId,
        string? name,
        Currency currency,
        BucketKind kind,
        InvestmentProfile? profile,
        BucketStatus status,
        BucketSystemRole systemRole,
        long version = 1)
    {
        var constructor = typeof(Bucket).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (Bucket)constructor.Invoke(
        [
            id.Value, ownerId, Bucket.CurrentSchemaVersion, version, FakeClock.DefaultStart, FakeClock.DefaultStart,
            name, currency, kind, profile, status, systemRole,
        ]);
    }
}
