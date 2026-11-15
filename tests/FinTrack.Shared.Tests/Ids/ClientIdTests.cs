using CsCheck;
using FinTrack.Shared.Kernel.Ids;

namespace FinTrack.Shared.Tests.Ids;

/// <summary>Identifiers sent by a client must be version 7 UUIDs (ARCHITECTURE 2.2).</summary>
public class ClientIdTests
{
    private const string Version7 = "0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";

    // Any instant from the Unix epoch to the end of year 9999, the range DateTimeOffset supports.
    private static readonly Gen<DateTimeOffset> Instants =
        Gen.Long[0, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()].Select(DateTimeOffset.FromUnixTimeMilliseconds);

    [Fact]
    public void FromClient_Version7_ReturnsId()
    {
        var result = StronglyTypedId.FromClient<TestId>(Guid.Parse(Version7));

        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(Guid.Parse(Version7));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")] // empty
    [InlineData("3f2b8c1e-9d4a-4b6e-8f1a-2c3d4e5f6a7b")] // version 4
    [InlineData("0193a1b2-c3d4-1e5f-8a9b-0c1d2e3f4a5b")] // version 1
    [InlineData("0193a1b2-c3d4-7e5f-ca9b-0c1d2e3f4a5b")] // version 7 digit, wrong variant (110x)
    [InlineData("0193a1b2-c3d4-7e5f-0a9b-0c1d2e3f4a5b")] // version 7 digit, wrong variant (0xxx)
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")] // max
    public void FromClient_NotVersion7_Fails(string value)
    {
        var result = StronglyTypedId.FromClient<TestId>(Guid.Parse(value));

        result.IsFailed.ShouldBeTrue();
        result.Errors.ShouldHaveSingleItem().Message.ShouldBe(StronglyTypedId.InvalidIdCode);
    }

    [Fact]
    public void FromClient_AnyGeneratedVersion7_Succeeds() =>
        Instants.Sample(instant => StronglyTypedId.FromClient<TestId>(Guid.CreateVersion7(instant)).IsSuccess);

    [Fact]
    public void FromClient_AnyOtherVersion_Fails() =>
        Gen.Guid.Where(value => value.Version != 7).Sample(value => StronglyTypedId.FromClient<TestId>(value).IsFailed);
}
