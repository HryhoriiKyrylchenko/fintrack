using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Domain.Journal;

namespace FinTrack.Budget.Application.Tests.Journal;

/// <summary>The shape of a <c>GetJournal</c> query (page size, period, cursor) and the cursor's own format.</summary>
public class GetJournalValidatorTests
{
    private static readonly ReplayPosition Position = new(new DateOnly(2026, 1, 15), new DateTimeOffset(2026, 1, 15, 9, 30, 0, 123, TimeSpan.Zero),
        Guid.Parse("0190a1b2-c3d4-7e5f-8a6b-7c8d9e0f1a2b"));

    private readonly GetJournalValidator validator = new();

    [Theory]
    [InlineData(1)]
    [InlineData(GetJournal.DefaultLimit)]
    [InlineData(GetJournal.MaxLimit)]
    public void Limit_FromOneToTheMaximum_IsValid(int limit) => validator.Validate(new GetJournal(Limit: limit)).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(GetJournal.MaxLimit + 1)]
    public void Limit_OutsideTheRange_IsInvalid(int limit) => Failure(new GetJournal(Limit: limit)).ShouldBe(("Limit", "InclusiveBetweenValidator"));

    [Fact]
    public void Period_OfOneDay_IsValid() =>
        validator.Validate(new GetJournal(From: new DateOnly(2026, 1, 15), To: new DateOnly(2026, 1, 15))).IsValid.ShouldBeTrue();

    [Fact]
    public void From_AfterTo_IsInvalidAtTo() =>
        Failure(new GetJournal(From: new DateOnly(2026, 1, 16), To: new DateOnly(2026, 1, 15))).ShouldBe(("To", GetJournalValidator.FromNotAfterTo));

    [Fact]
    public void Period_WithOnlyOneEnd_IsValid()
    {
        validator.Validate(new GetJournal(To: new DateOnly(2026, 1, 15))).IsValid.ShouldBeTrue();
        validator.Validate(new GetJournal(From: new DateOnly(2026, 1, 15))).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Cursor_FromAPage_IsValid() =>
        validator.Validate(new GetJournal(Cursor: JournalCursor.Encode(Position))).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("not a cursor")]
    [InlineData("AQ")]
    public void Cursor_ThatIsNotOne_IsInvalid(string cursor) => Failure(new GetJournal(Cursor: cursor)).ShouldBe(("Cursor", JournalCursor.FormatRule));

    [Fact]
    public void Cursor_RoundTrips_ThePositionExactly()
    {
        JournalCursor.TryParse(JournalCursor.Encode(Position), out var parsed).ShouldBeTrue();

        parsed.ShouldBe(Position);
    }

    [Fact]
    public void Cursor_RoundTrips_TheExtremes()
    {
        var earliest = new ReplayPosition(DateOnly.MinValue, DateTimeOffset.MinValue, Guid.Empty);
        var latest = new ReplayPosition(DateOnly.MaxValue, new DateTimeOffset(9999, 12, 31, 23, 59, 59, 999, TimeSpan.Zero), Guid.AllBitsSet);

        JournalCursor.TryParse(JournalCursor.Encode(earliest), out var first).ShouldBeTrue();
        JournalCursor.TryParse(JournalCursor.Encode(latest), out var last).ShouldBeTrue();

        first.ShouldBe(earliest);
        last.ShouldBe(latest);
    }

    [Fact]
    public void Cursor_IsReadStrictly()
    {
        var cursor = JournalCursor.Encode(Position);

        // Base64 padding, standard Base64 letters, another format version, a cut or longer text are all rejected.
        JournalCursor.TryParse(cursor + "=", out _).ShouldBeFalse();
        JournalCursor.TryParse(cursor.Replace('-', '+').Replace('_', '/') + "+", out _).ShouldBeFalse();
        JournalCursor.TryParse("Ag" + cursor[2..], out _).ShouldBeFalse();
        JournalCursor.TryParse(cursor[..^1], out _).ShouldBeFalse();
        JournalCursor.TryParse(cursor + "A", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(int.MaxValue, 0L)]
    [InlineData(-1, 0L)]
    [InlineData(739_000, long.MaxValue)]
    [InlineData(739_000, long.MinValue)]
    public void Cursor_WithAValueOutOfRange_IsNotOne(int dayNumber, long milliseconds)
    {
        // Well-formed bytes, but a day or an instant no DateOnly or DateTimeOffset can hold.
        var bytes = new byte[29];
        bytes[0] = 1;
        Guid.CreateVersion7().TryWriteBytes(bytes.AsSpan(1, 16), bigEndian: true, out _);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(17, 4), dayNumber);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(21, 8), milliseconds);

        JournalCursor.TryParse(System.Buffers.Text.Base64Url.EncodeToString(bytes), out _).ShouldBeFalse();
    }

    [Fact]
    public void Cursor_HasOneSpellingOnly()
    {
        // The last character carries two unused bits; a lenient decoder reads both spellings as the same bytes.
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var cursor = JournalCursor.Encode(Position);
        var other = cursor[..^1] + Alphabet[Alphabet.IndexOf(cursor[^1], StringComparison.Ordinal) ^ 1];

        JournalCursor.TryParse(other, out _).ShouldBeFalse();
    }

    private (string Field, string Rule) Failure(GetJournal query)
    {
        var failure = validator.Validate(query).Errors.ShouldHaveSingleItem();
        return (failure.PropertyName, failure.ErrorCode);
    }
}
