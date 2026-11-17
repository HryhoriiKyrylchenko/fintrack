using System.Text;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FluentResults;

namespace FinTrack.Budget.Domain.Buckets;

/// <summary>
/// A bucket (ARCHITECTURE 4.1): a named pot of money in one currency. Phase 1a fields only; the limit
/// (phase 1b) and the priority (phase 2) are added as optional fields when they are used.
/// </summary>
/// <remarks>
/// <para>
/// Created with <see cref="Create"/> or <see cref="Unallocated"/>, changed with <see cref="Rename"/>;
/// each returns a new object. The constructor is private: the database mapping restores stored
/// buckets through it.
/// </para>
/// <para>
/// Names (plan 1a D15): 1 to <see cref="MaxNameLength"/> characters, no leading or trailing white
/// space and no control characters (<see cref="IsValidName"/>; input is checked before it reaches the
/// domain, so an invalid name here is a bug). A name is unique among the owner's active buckets,
/// compared without regard to case and Unicode form (<see cref="SameName"/>); an archived bucket frees
/// its name. The system bucket <c>Unallocated</c> has no name: the UI names it.
/// </para>
/// </remarks>
public sealed class Bucket : IMutableDocument
{
    /// <summary>The stored shape's version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The longest name.</summary>
    public const int MaxNameLength = 60;

    // The mapping restores stored documents through this constructor; its parameter names match the properties.
    private Bucket(
        Guid id,
        OwnerId ownerId,
        int schemaVersion,
        long version,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string? name,
        Currency currency,
        BucketKind kind,
        InvestmentProfile? profile,
        BucketStatus status,
        BucketSystemRole systemRole)
    {
        Id = id;
        OwnerId = ownerId;
        SchemaVersion = schemaVersion;
        Version = version;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Name = name;
        Currency = currency;
        Kind = kind;
        Profile = profile;
        Status = status;
        SystemRole = systemRole;
    }

    /// <inheritdoc />
    public Guid Id { get; }

    /// <summary>The identifier as its own type.</summary>
    public BucketId BucketId => BucketId.From(Id);

    /// <inheritdoc />
    public OwnerId OwnerId { get; }

    /// <inheritdoc />
    public int SchemaVersion { get; }

    /// <inheritdoc />
    public long Version { get; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>The name; <c>null</c> only for a system bucket.</summary>
    public string? Name { get; }

    /// <summary>Every amount in this bucket is in this currency.</summary>
    public Currency Currency { get; }

    /// <summary>Savings or investment.</summary>
    public BucketKind Kind { get; }

    /// <summary>The profile of an investment bucket; <c>null</c> for a savings bucket.</summary>
    public InvestmentProfile? Profile { get; }

    /// <summary>Active or archived.</summary>
    public BucketStatus Status { get; }

    /// <summary>Whether the system created and protects this bucket.</summary>
    public BucketSystemRole SystemRole { get; }

    /// <summary>
    /// A new bucket of the user (version 0, not stored yet). Fails with
    /// <see cref="BudgetErrorCodes.BucketNameTaken"/> when an active bucket of
    /// <paramref name="ownersBuckets"/> has the same name.
    /// </summary>
    /// <exception cref="ArgumentException">The name is invalid, or the profile does not fit the kind (bugs: input is validated first).</exception>
    public static Result<Bucket> Create(
        BucketId id,
        OwnerId ownerId,
        string name,
        Currency currency,
        BucketKind kind,
        InvestmentProfile? profile,
        IEnumerable<Bucket> ownersBuckets)
    {
        ArgumentNullException.ThrowIfNull(currency);
        EnsureValidName(name);

        if ((kind == BucketKind.Investment) != (profile is not null))
        {
            throw new ArgumentException("An investment bucket has a profile; a savings bucket has none.", nameof(profile));
        }

        return NameTaken(name, ownersBuckets, except: null)
            ? Result.Fail<Bucket>(AppError.BusinessRule(BudgetErrorCodes.BucketNameTaken))
            : Result.Ok(new Bucket(id.Value, ownerId, CurrentSchemaVersion, version: 0, default, default, name, currency, kind, profile, BucketStatus.Active, BucketSystemRole.None));
    }

    /// <summary>The system bucket <c>Unallocated</c> for <paramref name="currency"/> (version 0): savings, no name.</summary>
    public static Bucket Unallocated(BucketId id, OwnerId ownerId, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new Bucket(id.Value, ownerId, CurrentSchemaVersion, version: 0, default, default, name: null, currency, BucketKind.Savings, profile: null, BucketStatus.Active, BucketSystemRole.Unallocated);
    }

    /// <summary>Whether <paramref name="name"/> may be a bucket's name (length, no surrounding white space, no control characters).</summary>
    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= MaxNameLength }
        && !char.IsWhiteSpace(name[0])
        && !char.IsWhiteSpace(name[^1])
        && !name.Any(char.IsControl);

    /// <summary>Whether two names count as the same: compared without regard to case, after Unicode normalisation (NFC).</summary>
    public static bool SameName(string? first, string? second) =>
        first is not null
        && second is not null
        && string.Equals(first.Normalize(NormalizationForm.FormC), second.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The same bucket with a new name; identity and version are kept, so the ledger replaces the
    /// stored version. Fails with <see cref="BudgetErrorCodes.BucketSystemBucket"/>,
    /// <see cref="BudgetErrorCodes.BucketArchived"/> or <see cref="BudgetErrorCodes.BucketNameTaken"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The name is invalid (a bug: input is validated first).</exception>
    public Result<Bucket> Rename(string name, IEnumerable<Bucket> ownersBuckets)
    {
        EnsureValidName(name);

        if (SystemRole != BucketSystemRole.None)
        {
            return Result.Fail<Bucket>(AppError.BusinessRule(BudgetErrorCodes.BucketSystemBucket));
        }

        if (Status == BucketStatus.Archived)
        {
            return Result.Fail<Bucket>(AppError.BusinessRule(BudgetErrorCodes.BucketArchived));
        }

        return NameTaken(name, ownersBuckets, except: Id)
            ? Result.Fail<Bucket>(AppError.BusinessRule(BudgetErrorCodes.BucketNameTaken))
            : Result.Ok(new Bucket(Id, OwnerId, SchemaVersion, Version, CreatedAt, UpdatedAt, name, Currency, Kind, Profile, Status, SystemRole));
    }

    /// <summary>
    /// Whether this bucket is exactly what <see cref="Create"/> would make from these values; a repeated
    /// create with the same ID is then harmless (invariant 13). The name is compared exactly.
    /// </summary>
    public bool HasDefinition(string name, Currency currency, BucketKind kind, InvestmentProfile? profile) =>
        string.Equals(Name, name, StringComparison.Ordinal)
        && Currency == currency
        && Kind == kind
        && Profile == profile;

    private static bool NameTaken(string name, IEnumerable<Bucket> ownersBuckets, Guid? except)
    {
        ArgumentNullException.ThrowIfNull(ownersBuckets);
        return ownersBuckets.Any(bucket => bucket.Id != except && bucket.Status == BucketStatus.Active && SameName(bucket.Name, name));
    }

    private static void EnsureValidName(string? name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"A bucket name has 1 to {MaxNameLength} characters, no surrounding white space and no control characters.", nameof(name));
        }
    }
}
