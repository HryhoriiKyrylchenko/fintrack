namespace FinTrack.Shared.Kernel.Ids;

/// <summary>Checks for version 7 UUIDs as defined in RFC 9562.</summary>
public static class UuidV7
{
    private const int Version = 7;

    /// <summary>
    /// Whether <paramref name="value"/> is a version 7 UUID: version field 7 and the
    /// RFC variant (binary <c>10</c> in the top two bits of byte 8).
    /// </summary>
    public static bool IsValid(Guid value)
    {
        // Big-endian bytes are in the same order as the text form, so the byte
        // positions below match the RFC layout.
        var bytes = value.ToByteArray(bigEndian: true);

        var version = bytes[6] >> 4;
        var variant = bytes[8] >> 6;

        return version == Version && variant == 0b10;
    }
}
