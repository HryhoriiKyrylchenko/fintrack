namespace FinTrack.Shared.Infrastructure.Fx;

/// <summary>Where the FX rates come from (configuration section <c>FxRates</c>); the defaults need no configuration.</summary>
public sealed class FxRateOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "FxRates";

    /// <summary>The longest a request may take (ARCHITECTURE 3: at most 5 seconds).</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Frankfurter's API root, ending with a slash.</summary>
    public Uri BaseAddress { get; set; } = new("https://api.frankfurter.dev/v1/");

    /// <summary>How long one request may take before the stored rates are used; above zero, at most <see cref="MaxTimeout"/>.</summary>
    public TimeSpan Timeout { get; set; } = MaxTimeout;
}
