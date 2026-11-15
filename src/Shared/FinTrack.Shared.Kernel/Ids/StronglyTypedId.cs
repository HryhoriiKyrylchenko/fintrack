using FluentResults;

namespace FinTrack.Shared.Kernel.Ids;

/// <summary>Creates identifiers from values that cannot be trusted.</summary>
public static class StronglyTypedId
{
    /// <summary>Error code for an identifier that is not a version 7 UUID.</summary>
    public const string InvalidIdCode = "id.invalid";

    /// <summary>
    /// Accepts an identifier sent by a client. Clients generate document identifiers
    /// themselves (ARCHITECTURE 2.2), so the value must be a version 7 UUID.
    /// </summary>
    public static Result<TId> FromClient<TId>(Guid value)
        where TId : struct, IStronglyTypedId<TId> =>
        UuidV7.IsValid(value)
            ? Result.Ok(TId.From(value))
            : Result.Fail<TId>(new Error(InvalidIdCode));
}
