namespace FinTrack.Shared.Kernel.Results;

/// <summary>
/// What kind of failure an <see cref="AppError"/> is. The API maps it to an HTTP status
/// (Validation 400, NotFound 404, Conflict 409, BusinessRule 422, Unavailable 503).
/// </summary>
public enum ErrorKind
{
    /// <summary>The input is malformed or out of range; nothing was checked against stored data.</summary>
    Validation,

    /// <summary>A referenced object does not exist for this owner.</summary>
    NotFound,

    /// <summary>Another write happened in between (optimistic concurrency) or the object is in a conflicting state.</summary>
    Conflict,

    /// <summary>The input is valid, but a business rule forbids the operation (for example insufficient funds).</summary>
    BusinessRule,

    /// <summary>
    /// A dependency the operation needs cannot be reached right now (the database, an exchange-rate
    /// or price service); trying again later may succeed. Nothing was changed.
    /// </summary>
    Unavailable,
}
