namespace FinTrack.Shared.Tests;

public class CiGateCheckTests
{
    [Fact]
    public void AlwaysFails_ToProveTheMergeGate() => 1.ShouldBe(2);
}
