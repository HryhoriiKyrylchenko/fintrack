namespace FinTrack.Shared.Kernel.Results;

/// <summary>
/// Error codes shared by all contexts. Each context keeps its own codes next to its domain.
/// </summary>
/// <remarks>
/// Codes are a public contract: the UI maps each one to a text in its resource files.
/// Never rename or reuse a code; add a new one instead.
/// </remarks>
public static class ErrorCodes
{
    /// <summary>Input failed validation. Parameters name the fields (added with the validation pipeline).</summary>
    public const string ValidationFailed = "validation.failed";

    /// <summary>Another write changed the data in between; the client may reload and retry.</summary>
    public const string ConcurrencyConflict = "concurrency.conflict";

    /// <summary>An identifier sent by a client is not a version 7 UUID.</summary>
    public const string IdInvalid = "id.invalid";

    /// <summary>A currency code is not registered. Parameter <c>currency</c>: the code as sent.</summary>
    public const string CurrencyUnknown = "currency.unknown";

    /// <summary>
    /// An amount has more decimal places than its currency allows.
    /// Parameters <c>currency</c> and <c>maxDecimalPlaces</c>.
    /// </summary>
    public const string MoneyTooManyDecimals = "money.too_many_decimals";

    /// <summary>An amount that must be above zero (an entry amount, plan 1a D13) is zero or negative. No parameters.</summary>
    public const string MoneyAmountNotPositive = "money.amount_not_positive";

    /// <summary>
    /// No exchange rate is known for a pair of currencies: the rate source cannot be reached and none
    /// is stored (plan 1a D8). Kind <c>Unavailable</c>. A total that needs it is left out with this
    /// code as the reason; no parameters.
    /// </summary>
    public const string FxRateUnavailable = "fx.rate_unavailable";
}
