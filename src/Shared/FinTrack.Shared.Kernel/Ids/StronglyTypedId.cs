using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Shared.Kernel.Ids;

/// <summary>Creates identifiers from values that cannot be trusted.</summary>
public static class StronglyTypedId
{
    /// <summary>
    /// Accepts an identifier sent by a client. Clients generate document identifiers
    /// themselves (ARCHITECTURE 2.2), so the value must be a version 7 UUID.
    /// Fails with <see cref="ErrorCodes.IdInvalid"/> otherwise.
    /// </summary>
    public static Result<TId> FromClient<TId>(Guid value)
        where TId : struct, IStronglyTypedId<TId> =>
        UuidV7.IsValid(value)
            ? Result.Ok(TId.From(value))
            : Result.Fail<TId>(AppError.Validation(ErrorCodes.IdInvalid));
}
