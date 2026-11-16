using System.Reflection;
using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Budget.Domain.Tests.Settings;

/// <summary>
/// Builds <see cref="OwnerSettings"/> as the database returns it, through its private constructor,
/// the way the MongoDB mapping does. Tests only.
/// </summary>
internal static class OwnerSettingsFactory
{
    public static OwnerSettings Restore(
        Guid id,
        OwnerId ownerId,
        long version,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        Currency reportingCurrency,
        string timeZone,
        string locale)
    {
        var constructor = typeof(OwnerSettings).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (OwnerSettings)constructor.Invoke(
            [id, ownerId, OwnerSettings.CurrentSchemaVersion, version, createdAt, updatedAt, reportingCurrency, timeZone, locale]);
    }
}
