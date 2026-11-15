using FinTrack.Shared.Kernel.Ids;

namespace FinTrack.Shared.Tests.Ids;

public class OwnerIdTests
{
    private static readonly Guid SomeGuid = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    [Fact]
    public void From_KeepsValue() => OwnerId.From(SomeGuid).Value.ShouldBe(SomeGuid);

    [Fact]
    public void From_EmptyGuid_Throws() => Should.Throw<ArgumentException>(() => OwnerId.From(Guid.Empty));

    [Fact]
    public void Equality_IsByValue()
    {
        OwnerId.From(SomeGuid).ShouldBe(OwnerId.From(SomeGuid));
        OwnerId.From(SomeGuid).ShouldNotBe(OwnerId.From(Guid.CreateVersion7()));
    }

    [Fact]
    public void ToString_IsTheGuid() => OwnerId.From(SomeGuid).ToString().ShouldBe("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
}
