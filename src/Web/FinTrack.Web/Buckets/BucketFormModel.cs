using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Shared.Kernel.Monetary;

namespace FinTrack.Web.Buckets;

/// <summary>
/// The values of the create and rename dialogs. The name rules are the server's (ARCHITECTURE 4.1: 1 to 60
/// characters, no surrounding white space, no control characters), checked here so the owner sees the reason
/// before sending; a name is never trimmed or corrected. Uniqueness is the server's to judge
/// (<c>bucket.name_taken</c>), because it compares without case and Unicode form against every active bucket.
/// </summary>
/// <remarks>
/// A new bucket gets its version 7 ID when the form opens, and every submit of that form sends it: a double
/// click or a retry after a lost answer creates one bucket (invariant 13).
/// </remarks>
public sealed class BucketFormModel
{
    /// <summary>Resource key: the name is empty or only spaces.</summary>
    public const string NameRequired = "Buckets.Name.Required";

    /// <summary>Resource key: the name is longer than <see cref="MaxNameLength"/>.</summary>
    public const string NameTooLong = "Buckets.Name.TooLong";

    /// <summary>Resource key: the name starts or ends with a space.</summary>
    public const string NameSpaces = "Buckets.Name.Spaces";

    /// <summary>Resource key: the name has a tab, a line break or another control character.</summary>
    public const string NameControlCharacters = "Buckets.Name.ControlCharacters";

    /// <summary>Resource key: an investment bucket has no profile chosen.</summary>
    public const string ProfileRequired = "Buckets.Profile.Required";

    /// <summary>The longest name, as the server allows it.</summary>
    public const int MaxNameLength = 60;

    private readonly string? currentName;

    private BucketFormModel(Guid id, string name, Currency currency, BucketKind kind, InvestmentProfile? profile, string? currentName)
    {
        Id = id;
        Name = name;
        Currency = currency;
        Kind = kind;
        Profile = profile;
        this.currentName = currentName;
    }

    /// <summary>The currencies a bucket can have in phase 1a.</summary>
    public static IReadOnlyList<Currency> Currencies { get; } = [Currency.Pln, Currency.Eur, Currency.Usd];

    /// <summary>The new bucket's ID, or the bucket being renamed.</summary>
    public Guid Id { get; }

    /// <summary>Whether the form renames an existing bucket; only the name can change then.</summary>
    public bool IsRename => currentName is not null;

    /// <summary>The name as typed.</summary>
    public string Name { get; set; }

    /// <summary>The currency of a new bucket.</summary>
    public Currency Currency { get; set; }

    /// <summary>Savings or investment.</summary>
    public BucketKind Kind { get; set; }

    /// <summary>The profile of an investment bucket; ignored for a savings bucket.</summary>
    public InvestmentProfile? Profile { get; set; }

    /// <summary>The resource key of what is wrong with the name, or <c>null</c>.</summary>
    public string? NameError => Name switch
    {
        null or { Length: 0 } => NameRequired,
        _ when string.IsNullOrWhiteSpace(Name) => NameRequired,
        { Length: > MaxNameLength } => NameTooLong,
        _ when char.IsWhiteSpace(Name[0]) || char.IsWhiteSpace(Name[^1]) => NameSpaces,
        _ when Name.Any(char.IsControl) => NameControlCharacters,
        _ => null,
    };

    /// <summary>The resource key of what is wrong with the profile, or <c>null</c>.</summary>
    public string? ProfileError => !IsRename && Kind == BucketKind.Investment && Profile is null ? ProfileRequired : null;

    /// <summary>Whether the values can be sent: valid, and for a rename, different from the current name.</summary>
    public bool CanSubmit =>
        NameError is null
        && ProfileError is null
        && (!IsRename || !string.Equals(Name, currentName, StringComparison.Ordinal));

    /// <summary>A form for a new bucket in <paramref name="reportingCurrency"/>, with a new client ID.</summary>
    public static BucketFormModel ForCreate(Currency reportingCurrency)
    {
        ArgumentNullException.ThrowIfNull(reportingCurrency);
        return new BucketFormModel(Guid.CreateVersion7(), string.Empty, reportingCurrency, BucketKind.Savings, profile: null, currentName: null);
    }

    /// <summary>A form that renames <paramref name="bucket"/>.</summary>
    /// <exception cref="ArgumentException">The bucket is a system bucket, which has no name to change (a bug).</exception>
    public static BucketFormModel ForRename(BucketDto bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);

        return bucket is { SystemRole: BucketSystemRole.None, Name: { } name }
            ? new BucketFormModel(bucket.Id, name, bucket.Currency, bucket.Kind, bucket.Profile, name)
            : throw new ArgumentException("A system bucket cannot be renamed.", nameof(bucket));
    }

    /// <summary>The request that creates the bucket.</summary>
    public CreateBucketRequest ToCreateRequest() =>
        new(Id, Name, Currency, Kind, Kind == BucketKind.Investment ? Profile : null);

    /// <summary>The request that renames the bucket.</summary>
    public RenameBucketRequest ToRenameRequest() => new(Name);
}
