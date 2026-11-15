using FinTrack.Shared.Kernel.Ledger;

namespace FinTrack.Shared.Tests.Ledger;

public class LedgerNameTests
{
    [Theory]
    [InlineData("budget")]
    [InlineData("inv")]
    [InlineData("insights2")]
    public void From_ValidName_KeepsIt(string value)
    {
        var name = LedgerName.From(value);

        name.Value.ShouldBe(value);
        name.ToString().ShouldBe(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("b")] // at least two characters
    [InlineData("Budget")]
    [InlineData("budget_journal")] // used as a collection prefix: letters and digits only
    [InlineData("budget.x")]
    [InlineData("2budget")]
    [InlineData("averyveryverylongname")] // at most 20 characters
    [InlineData("budget\n")]
    public void From_InvalidName_Throws(string value) =>
        Should.Throw<ArgumentException>(() => LedgerName.From(value));

    [Fact]
    public void Equality_IsByValue() => LedgerName.From("budget").ShouldBe(LedgerName.From("budget"));
}
