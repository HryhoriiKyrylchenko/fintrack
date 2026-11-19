using System.Text.Json;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Contracts.V1.Overview;
using FinTrack.Shared.Kernel.Json;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Web.Tests.Api;

/// <summary>
/// The Web app reads the overview and journal exactly as the Api writes them (the same JSON as the Api's
/// pinned shapes), with the shared strict options: amounts keep their scale, nulls stay null.
/// </summary>
public class ReadContractJsonTests
{
    [Fact]
    public void Overview_IsRead()
    {
        const string Json =
            "{\"reportingCurrency\":\"PLN\",\"asOf\":\"2026-01-15\",\"totals\":["
            + "{\"currency\":\"PLN\",\"available\":{\"amount\":\"10.50\",\"currency\":\"PLN\"},\"conversion\":null},"
            + "{\"currency\":\"EUR\",\"available\":{\"amount\":\"2.00\",\"currency\":\"EUR\"},\"conversion\":{\"amount\":{\"amount\":\"8.55\",\"currency\":\"PLN\"},\"rate\":\"4.2735\",\"rateDate\":\"2026-01-14\"}}],"
            + "\"reportingTotal\":{\"amount\":null,\"rateDate\":null,\"unavailableReason\":\"fx.rate_unavailable\"},"
            + "\"buckets\":[{\"id\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b\",\"name\":null,\"currency\":\"PLN\",\"kind\":\"Savings\",\"profile\":null,\"systemRole\":\"Unallocated\","
            + "\"available\":{\"amount\":\"10.50\",\"currency\":\"PLN\"},\"netContributed\":{\"amount\":\"10.50\",\"currency\":\"PLN\"},\"outstandingPrincipal\":{\"amount\":\"0.00\",\"currency\":\"PLN\"}}]}";

        var overview = JsonSerializer.Deserialize<BudgetOverviewDto>(Json, FinTrackJson.Options)!;

        overview.AsOf.ShouldBe(new DateOnly(2026, 1, 15));
        overview.Totals[1].Conversion.ShouldBe(new ConversionDto(Money.From(8.55m, Currency.Pln), 4.2735m, new DateOnly(2026, 1, 14)));
        overview.Totals[0].Available.Amount.Scale.ShouldBe((byte)2);
        overview.ReportingTotal.ShouldBe(new ReportingTotalDto(null, null, "fx.rate_unavailable"));
        overview.Buckets.ShouldHaveSingleItem().OutstandingPrincipal.Amount.Scale.ShouldBe((byte)2);
    }

    [Fact]
    public void JournalPage_IsRead()
    {
        const string Json =
            "{\"entries\":[{\"id\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b\",\"type\":\"Transfer\",\"occurredOn\":\"2026-01-14\",\"recordedAt\":\"2026-01-15T09:30:00.123Z\",\"note\":null,\"tags\":[\"move\"],"
            + "\"groupId\":null,\"source\":\"Manual\",\"externalId\":null,\"bucketId\":null,\"amount\":null,"
            + "\"fromBucketId\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2d\",\"toBucketId\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2e\","
            + "\"fromAmount\":{\"amount\":\"43.50\",\"currency\":\"PLN\"},\"toAmount\":{\"amount\":\"10.00\",\"currency\":\"EUR\"},\"fee\":{\"amount\":\"0.50\",\"currency\":\"PLN\"},"
            + "\"rate\":\"0.232558139535\",\"reversedEntryId\":null,\"reversedBy\":\"0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2c\"}],\"nextCursor\":null}";

        var page = JsonSerializer.Deserialize<JournalPageDto>(Json, FinTrackJson.Options)!;

        var entry = page.Entries.ShouldHaveSingleItem();
        entry.Type.ShouldBe(JournalEntryType.Transfer);
        entry.RecordedAt.ShouldBe(new DateTimeOffset(2026, 1, 15, 9, 30, 0, 123, TimeSpan.Zero));
        entry.Tags.ShouldBe(["move"]);
        entry.Fee.ShouldBe(Money.From(0.50m, Currency.Pln));
        entry.Rate.ShouldBe(0.232558139535m);
        entry.ReversedBy.ShouldBe(Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2c"));
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public void JournalPage_WithAnUnknownProperty_IsRejected()
    {
        const string Json = """{"entries":[],"nextCursor":null,"extra":1}""";

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<JournalPageDto>(Json, FinTrackJson.Options));
    }
}
