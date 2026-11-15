using System.Collections.Frozen;
using System.Text.RegularExpressions;
using FluentResults;

namespace FinTrack.Shared.Kernel.Results;

/// <summary>
/// The only kind of error a failed <see cref="Result"/> carries in this code base: a stable
/// code, a <see cref="ErrorKind"/> and named parameters. The API turns it into RFC 7807
/// <c>ProblemDetails</c>; the UI turns the code into text (ARCHITECTURE 2.7).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Error.Message"/> is the code itself, never a sentence for users.
/// </para>
/// <para>
/// Parameters are part of the API contract, so their values are limited to <see cref="string"/>,
/// <see cref="bool"/>, <see cref="int"/>, <see cref="long"/> and <see cref="decimal"/>.
/// Log the <see cref="Code"/> only, never the parameters: they may contain user data.
/// </para>
/// </remarks>
public sealed partial class AppError : Error
{
    private static readonly FrozenSet<Type> AllowedParameterTypes =
        new[] { typeof(string), typeof(bool), typeof(int), typeof(long), typeof(decimal) }.ToFrozenSet();

    private AppError(string code, ErrorKind kind, IReadOnlyDictionary<string, object> parameters)
        : base(code)
    {
        Code = code;
        Kind = kind;
        Parameters = parameters;
    }

    /// <summary>
    /// The stable code: lower-case segments separated by dots, at least two segments,
    /// for example <c>bucket.insufficient_funds</c>.
    /// </summary>
    public string Code { get; }

    /// <summary>What kind of failure this is.</summary>
    public ErrorKind Kind { get; }

    /// <summary>Named values for the UI text, for example <c>currency</c> = <c>PLN</c>. Names are camelCase.</summary>
    public IReadOnlyDictionary<string, object> Parameters { get; }

    /// <summary>The input is malformed or out of range.</summary>
    public static AppError Validation(string code, params (string Name, object Value)[] parameters) =>
        Create(code, ErrorKind.Validation, parameters);

    /// <summary>A referenced object does not exist for this owner.</summary>
    public static AppError NotFound(string code, params (string Name, object Value)[] parameters) =>
        Create(code, ErrorKind.NotFound, parameters);

    /// <summary>A concurrent change or a conflicting state.</summary>
    public static AppError Conflict(string code, params (string Name, object Value)[] parameters) =>
        Create(code, ErrorKind.Conflict, parameters);

    /// <summary>A business rule forbids the operation.</summary>
    public static AppError BusinessRule(string code, params (string Name, object Value)[] parameters) =>
        Create(code, ErrorKind.BusinessRule, parameters);

    // Invalid codes or parameters are programming errors, so they throw.
    private static AppError Create(string code, ErrorKind kind, (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(parameters);

        if (!StableName.IsValid(code))
        {
            throw new ArgumentException($"Error code '{code}' must be lower-case segments separated by dots, for example '{StableName.Example}'.", nameof(code));
        }

        var byName = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var (name, value) in parameters)
        {
            if (name is null || !ParameterNameFormat().IsMatch(name))
            {
                throw new ArgumentException($"Parameter name '{name}' of error '{code}' must be camelCase.", nameof(parameters));
            }

            if (value is null || !AllowedParameterTypes.Contains(value.GetType()))
            {
                throw new ArgumentException($"Parameter '{name}' of error '{code}' must be a string, bool, int, long or decimal.", nameof(parameters));
            }

            if (!byName.TryAdd(name, value))
            {
                throw new ArgumentException($"Parameter '{name}' of error '{code}' is given twice.", nameof(parameters));
            }
        }

        return new AppError(code, kind, byName.ToFrozenDictionary(StringComparer.Ordinal));
    }

    [GeneratedRegex("^[a-z][a-zA-Z0-9]*\\z")]
    private static partial Regex ParameterNameFormat();
}
