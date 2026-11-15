using System.Globalization;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Tests.Common;

namespace FinTrack.Shared.Tests.Ids;

public class IdGeneratorTests
{
    [Fact]
    public void New_CreatesVersion7Id()
    {
        var id = new IdGenerator(new FakeClock()).New<TestId>();

        id.Value.Version.ShouldBe(7);
        UuidV7.IsValid(id.Value).ShouldBeTrue();
    }

    [Fact]
    public void New_EmbedsTheClockTimestamp()
    {
        var clock = new FakeClock();

        var id = new IdGenerator(clock).New<TestId>();

        TimestampOf(id.Value).ShouldBe(clock.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void New_IdsCreatedLater_SortLater()
    {
        var clock = new FakeClock();
        var generator = new IdGenerator(clock);
        var ids = new List<Guid>();

        for (var i = 0; i < 100; i++)
        {
            ids.Add(generator.New<TestId>().Value);
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        ids.Order().ShouldBe(ids);
        ids.Select(id => id.ToString()).Order(StringComparer.Ordinal).ShouldBe(ids.Select(id => id.ToString()));
    }

    [Fact]
    public void New_ManyIdsAtTheSameInstant_AreUnique()
    {
        var generator = new IdGenerator(new FakeClock());

        var ids = Enumerable.Range(0, 10_000).Select(_ => generator.New<TestId>()).ToList();

        ids.Distinct().Count().ShouldBe(ids.Count);
    }

    // The first 48 bits of a version 7 UUID are the Unix time in milliseconds (RFC 9562).
    private static long TimestampOf(Guid id) =>
        long.Parse(id.ToString("N")[..12], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
