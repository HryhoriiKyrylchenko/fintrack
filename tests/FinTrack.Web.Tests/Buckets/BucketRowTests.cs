using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Buckets;
using FinTrack.Web.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinTrack.Web.Tests.Buckets;

/// <summary>How a bucket appears in the list: its name (the system bucket's from the resources), its kind, and what can be done with it.</summary>
public sealed class BucketRowTests : IDisposable
{
    private readonly ServiceProvider services = new ServiceCollection()
        .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
        .AddLocalization()
        .BuildServiceProvider();

    private IStringLocalizer<SharedResource> Localizer => services.GetRequiredService<IStringLocalizer<SharedResource>>();

    [Fact]
    public void UsersBucket_IsShownByItsName_AndCanBeRenamedAndArchived()
    {
        var row = new BucketRow(Bucket("Cushion"));

        row.DisplayName(Localizer).ShouldBe("Cushion");
        row.IsSystem.ShouldBeFalse();
        row.CanRename.ShouldBeTrue();
        row.CanArchive.ShouldBeTrue();
        Localizer[row.KindKey].Value.ShouldBe("Savings");
    }

    [Fact]
    public void Unallocated_IsNamedFromTheResources_WithItsCurrency_AndCannotBeChanged()
    {
        var row = new BucketRow(Bucket(null) with { Currency = Currency.Eur, SystemRole = BucketSystemRole.Unallocated });

        row.DisplayName(Localizer).ShouldBe("Unallocated (EUR)");
        row.IsSystem.ShouldBeTrue();
        row.CanRename.ShouldBeFalse();
        row.CanArchive.ShouldBeFalse();
    }

    [Fact]
    public void ArchivedBucket_CannotBeRenamedOrArchivedAgain()
    {
        var row = new BucketRow(Bucket("Old") with { Status = BucketStatus.Archived });

        row.IsArchived.ShouldBeTrue();
        row.CanRename.ShouldBeFalse();
        row.CanArchive.ShouldBeFalse();
    }

    [Theory]
    [InlineData(BucketKind.Savings, null, "Savings")]
    [InlineData(BucketKind.Investment, InvestmentProfile.Crypto, "Investment · Crypto")]
    [InlineData(BucketKind.Investment, InvestmentProfile.RealValueAssets, "Investment · Stocks and ETFs")]
    public void Kind_IsDescribed(BucketKind kind, InvestmentProfile? profile, string expected) =>
        Localizer[new BucketRow(Bucket("Any") with { Kind = kind, Profile = profile }).KindKey].Value.ShouldBe(expected);

    public void Dispose() => services.Dispose();

    private static BucketDto Bucket(string? name) =>
        new(Guid.CreateVersion7(), name, Currency.Pln, BucketKind.Savings, null, BucketStatus.Active, BucketSystemRole.None);
}
