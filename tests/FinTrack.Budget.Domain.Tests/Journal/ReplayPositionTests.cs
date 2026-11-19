using FinTrack.Budget.Domain.Journal;

namespace FinTrack.Budget.Domain.Tests.Journal;

/// <summary>
/// A replay position compares exactly as the replay order (ARCHITECTURE 4.2): by business date, then recorded time,
/// then the ID's big-endian bytes. The journal's paging cursor keeps a position apart from its entry and relies on
/// these operators.
/// </summary>
public class ReplayPositionTests
{
    private static readonly DateOnly Day = new(2026, 1, 15);
    private static readonly DateTimeOffset Recorded = new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid LowId = Guid.Parse("01000000-0000-7000-8000-000000000000");
    private static readonly Guid HighId = Guid.Parse("02000000-0000-7000-8000-000000000000");

    public static TheoryData<string> Pairs => ["date", "recorded", "id"];

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Operators_FollowTheReplayOrder(string decidedBy)
    {
        var (earlier, later) = decidedBy switch
        {
            "date" => (new ReplayPosition(Day, Recorded, HighId), new ReplayPosition(Day.AddDays(1), Recorded.AddDays(-5), LowId)),
            "recorded" => (new ReplayPosition(Day, Recorded, HighId), new ReplayPosition(Day, Recorded.AddMilliseconds(1), LowId)),
            _ => (new ReplayPosition(Day, Recorded, LowId), new ReplayPosition(Day, Recorded, HighId)),
        };

        (earlier < later).ShouldBeTrue();
        (later > earlier).ShouldBeTrue();
        (earlier <= later).ShouldBeTrue();
        (later >= earlier).ShouldBeTrue();

        (later < earlier).ShouldBeFalse();
        (earlier > later).ShouldBeFalse();
        (later <= earlier).ShouldBeFalse();
        (earlier >= later).ShouldBeFalse();
    }

    [Fact]
    public void ThePosition_IsEqualToItself_ForEveryOperator()
    {
        var position = new ReplayPosition(Day, Recorded, LowId);
        var same = new ReplayPosition(Day, Recorded, LowId);

        (position < same).ShouldBeFalse();
        (position > same).ShouldBeFalse();
        (position <= same).ShouldBeTrue();
        (position >= same).ShouldBeTrue();
        position.CompareTo(same).ShouldBe(0);
    }

    [Fact]
    public void JournalEntryId_CannotBeEmpty() => Should.Throw<ArgumentException>(() => JournalEntryId.From(Guid.Empty));
}
