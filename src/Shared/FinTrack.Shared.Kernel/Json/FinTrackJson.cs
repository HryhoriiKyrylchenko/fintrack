using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinTrack.Shared.Kernel.Json;

/// <summary>
/// The JSON contract between the Api and its clients (ARCHITECTURE 2.7). One set of options,
/// used by the Api and the Web app, so both sides read and write the same format.
/// </summary>
/// <remarks>
/// <para>The format:</para>
/// <list type="bullet">
/// <item><description>property names in camelCase, matched with exact case;</description></item>
/// <item><description><see cref="decimal"/> as a JSON string with its exact scale (<c>"1.10"</c>), never a number:
/// a JSON number is read as a binary double by JavaScript and most tools;</description></item>
/// <item><description><c>Currency</c> as its code; <c>Money</c> as <c>{ "amount": "1.10", "currency": "PLN" }</c>;</description></item>
/// <item><description>strongly typed IDs as the GUID string (<c>D</c> format);</description></item>
/// <item><description><see cref="DateOnly"/> as <c>yyyy-MM-dd</c>; <c>YearMonth</c> as <c>yyyy-MM</c>; <see cref="DateTimeOffset"/>
/// as ISO 8601 in UTC ending in <c>Z</c>, with the fraction only as long as needed;</description></item>
/// <item><description>enums as their names.</description></item>
/// </list>
/// <para>
/// Reading is strict, like the storage format: an amount sent as a number, a decimal that would
/// be rounded, an unknown currency or one with too many decimals, an enum as a number or in another
/// case, a month in any spelling but <c>yyyy-MM</c> or out of range, an instant without <c>Z</c>, an unknown, repeated or missing property and <c>null</c> for
/// a non-nullable value all fail with a <see cref="JsonException"/>. A property is optional only
/// when its constructor parameter has a default value. Messages never contain amounts.
/// </para>
/// <para>
/// Writing an instant with an offset or an undefined enum value throws
/// <see cref="InvalidOperationException"/>: both are bugs.
/// </para>
/// </remarks>
public static class FinTrackJson
{
    /// <summary>The contract's options. Read-only; use <see cref="Configure"/> for options owned by a framework.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// Applies the contract to <paramref name="options"/>, for example ASP.NET Core's own
    /// serializer options. Every setting the contract depends on is set explicitly, so the starting
    /// defaults (such as <see cref="JsonSerializerDefaults.Web"/>) do not matter.
    /// </summary>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        options.PropertyNameCaseInsensitive = false;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.AllowDuplicateProperties = false;
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        options.AllowTrailingCommas = false;
        options.ReadCommentHandling = JsonCommentHandling.Disallow;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;

        // First, so they win over any converter the framework added.
        JsonConverter[] converters =
        [
            new DecimalStringConverter(),
            new CurrencyJsonConverter(),
            new MoneyJsonConverter(),
            new IsoDateOnlyJsonConverter(),
            new YearMonthJsonConverter(),
            new UtcInstantJsonConverter(),
            new StronglyTypedIdJsonConverterFactory(),
            new EnumNameJsonConverterFactory(),
        ];

        for (var index = 0; index < converters.Length; index++)
        {
            options.Converters.Insert(index, converters[index]);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        Configure(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
