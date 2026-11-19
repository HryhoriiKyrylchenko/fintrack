using System.Buffers.Binary;
using System.Buffers.Text;
using FinTrack.Budget.Domain.Journal;

namespace FinTrack.Budget.Application.Journal;

/// <summary>
/// The paging cursor of <c>GetJournal</c>: the replay position (<see cref="ReplayPosition"/>) of the last
/// entry on a page, so the next page starts right after it whatever was added in between. Opaque to clients.
/// </summary>
/// <remarks>
/// <para>
/// A position, not an entry ID, so that a database-side implementation can continue with a range read on
/// the <c>replay_order</c> index (<c>{ ownerId, occurredOn, recordedAt, _id }</c>) without first looking the
/// entry up; the in-memory paging of phase 1a uses the same comparison (plan 1a assumptions).
/// </para>
/// <para>
/// Format: Base64url without padding of 29 bytes: a format version (1), the ID's 16 big-endian bytes, the
/// day number of <c>OccurredOn</c> (4 bytes) and <c>RecordedAt</c> in Unix milliseconds (8 bytes), both
/// big-endian. Reading is strict: exactly that length, the Base64url alphabet without padding, the unused
/// bits of the last character zero (the decoder rejects anything else, so every cursor has one spelling;
/// a test pins it), this version, and values in range. A new format gets a new version byte.
/// </para>
/// </remarks>
public static class JournalCursor
{
    /// <summary>The <c>rule</c> of a <c>validation.failed</c> error for a cursor that is not one.</summary>
    public const string FormatRule = "CursorFormat";

    private const byte FormatVersion = 1;
    private const int GuidLength = 16;
    private const int Length = 1 + GuidLength + sizeof(int) + sizeof(long);

    /// <summary>The cursor that continues after <paramref name="position"/>.</summary>
    public static string Encode(ReplayPosition position)
    {
        Span<byte> bytes = stackalloc byte[Length];
        bytes[0] = FormatVersion;
        position.Id.TryWriteBytes(bytes.Slice(1, GuidLength), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(bytes.Slice(1 + GuidLength, sizeof(int)), position.OccurredOn.DayNumber);
        BinaryPrimitives.WriteInt64BigEndian(bytes.Slice(1 + GuidLength + sizeof(int)), position.RecordedAt.ToUnixTimeMilliseconds());
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>Reads a cursor written by <see cref="Encode"/>; <c>false</c> for anything else.</summary>
    public static bool TryParse(string? text, out ReplayPosition position)
    {
        position = default;
        if (text is null || text.Length != Base64Url.GetEncodedLength(Length) || !Base64Url.IsValid(text))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Length];
        if (!Base64Url.TryDecodeFromChars(text, bytes, out var written) || written != Length || bytes[0] != FormatVersion)
        {
            return false;
        }

        var dayNumber = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(1 + GuidLength, sizeof(int)));
        var milliseconds = BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(1 + GuidLength + sizeof(int)));
        if (dayNumber < DateOnly.MinValue.DayNumber || dayNumber > DateOnly.MaxValue.DayNumber
            || milliseconds < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return false;
        }

        position = new ReplayPosition(
            DateOnly.FromDayNumber(dayNumber),
            DateTimeOffset.FromUnixTimeMilliseconds(milliseconds),
            new Guid(bytes.Slice(1, GuidLength), bigEndian: true));
        return true;
    }
}
