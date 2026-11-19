using System.Globalization;
using System.Text;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Web.Formatting;
using FinTrack.Web.Resources;

namespace FinTrack.Web.Entries;

/// <summary>What an entry does; each kind has its own Api route (plan 1a, API surface).</summary>
public enum EntryKind
{
    /// <summary>The starting amount of a bucket.</summary>
    OpeningBalance,

    /// <summary>New money into a bucket.</summary>
    Deposit,

    /// <summary>Money that leaves for good.</summary>
    Spend,

    /// <summary>Money moved between two buckets.</summary>
    Transfer,
}

/// <summary>What the owner types for a transfer between currencies (plan 1a D6): what arrived, or the rate.</summary>
public enum TransferInput
{
    /// <summary>The amount received; the rate is derived from it.</summary>
    ReceivedAmount,

    /// <summary>The rate; the amount received is computed from it, rounded once.</summary>
    Rate,
}

/// <summary>The names of the form's fields, for their messages.</summary>
public static class EntryField
{
    /// <summary>The bucket of an opening balance, deposit or spend.</summary>
    public const string Target = "target";

    /// <summary>The amount, or the amount sent by a transfer.</summary>
    public const string Amount = "amount";

    /// <summary>The source of a transfer.</summary>
    public const string From = "from";

    /// <summary>The destination of a transfer.</summary>
    public const string To = "to";

    /// <summary>The fee of a transfer.</summary>
    public const string Fee = "fee";

    /// <summary>What a transfer between currencies delivered.</summary>
    public const string Received = "received";

    /// <summary>The rate of a transfer between currencies.</summary>
    public const string Rate = "rate";

    /// <summary>The business date.</summary>
    public const string Date = "date";

    /// <summary>The note.</summary>
    public const string Note = "note";
}

/// <summary>A bucket an entry can name: one of the owner's buckets, or <c>Unallocated</c> of a currency.</summary>
/// <param name="Key">A stable key for a choice list: the bucket ID, or <c>unallocated:&lt;code&gt;</c>.</param>
/// <param name="BucketId">The bucket, or <c>null</c> for <c>Unallocated</c> (named by its currency; created on first use).</param>
/// <param name="Currency">The bucket's currency; amounts are read in it.</param>
/// <param name="Name">The owner's name, or <c>null</c> for <c>Unallocated</c>.</param>
/// <param name="Kind">Savings or investment (<c>Unallocated</c> is a savings bucket).</param>
public sealed record EntryTarget(string Key, Guid? BucketId, Currency Currency, string? Name, BucketKind Kind)
{
    /// <summary>Whether this is <c>Unallocated</c> of <see cref="Currency"/>.</summary>
    public bool IsUnallocated => BucketId is null;

    /// <summary>The target for one of the owner's buckets.</summary>
    public static EntryTarget Of(BucketDto bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        return new EntryTarget(bucket.Id.ToString(), bucket.Id, bucket.Currency, bucket.Name, bucket.Kind);
    }

    /// <summary>The target <c>Unallocated</c> of <paramref name="currency"/>.</summary>
    public static EntryTarget UnallocatedOf(Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new EntryTarget($"unallocated:{currency.Code}", null, currency, null, BucketKind.Savings);
    }
}

/// <summary>
/// The values of the "Add entry" form (plan 1a step 16): opening balance, deposit, spend or transfer. The server's
/// rules are checked here before anything is sent (DESIGN 9: say why before the owner confirms), with the server's
/// own codes where one exists (<c>entry.date_in_future</c>, <c>transfer.same_bucket</c>,
/// <c>transfer.fee_exceeds_amount</c>, <c>money.amount_not_positive</c>); the server still decides, and its refusal is
/// shown the same way. Values are rejected, never trimmed or corrected (ARCHITECTURE 4.2).
/// </summary>
/// <remarks>
/// <para>
/// One version 7 ID per form (invariant 13): it stays through every edit, the summary step and every retry, so a double
/// click or a retry after a lost answer writes one entry. <see cref="StartAnother"/> gives the next entry a new one.
/// </para>
/// <para>
/// A transfer between currencies is entered by what arrived or by a rate (plan 1a D6). From a rate, the amount received
/// is computed once, with banker's rounding at the destination's precision, and that amount is what is sent: the amounts
/// are the truth, and the server derives and stores the rate from them. Within one currency what arrives is what was sent
/// less the fee.
/// </para>
/// </remarks>
public sealed class EntryFormModel
{
    /// <summary>No bucket chosen.</summary>
    public const string TargetRequired = "input.target_required";

    /// <summary>The note breaks a rule (1 to 500 characters, no surrounding white space, no control characters but line breaks).</summary>
    public const string NoteInvalid = "input.note_invalid";

    /// <summary>A tag breaks a rule (1 to 30 characters, no surrounding white space, no control characters).</summary>
    public const string TagInvalid = "input.tag_invalid";

    /// <summary>The tag is already there (compared without regard to case).</summary>
    public const string TagDuplicate = "input.tag_duplicate";

    /// <summary>There are already ten tags.</summary>
    public const string TagsTooMany = "input.tags_too_many";

    /// <summary>The most characters in a note, as the server allows.</summary>
    public const int MaxNoteLength = 500;

    /// <summary>The most tags, as the server allows.</summary>
    public const int MaxTags = 10;

    /// <summary>The most characters in a tag, as the server allows.</summary>
    public const int MaxTagLength = 30;

    /// <summary>Decimal places of a derived rate, as the server stores it.</summary>
    public const int RateDecimalPlaces = 12;

    private const string SameBucket = "transfer.same_bucket";
    private const string FeeExceedsAmount = "transfer.fee_exceeds_amount";
    private const string DateInFuture = "entry.date_in_future";

    private readonly CultureInfo culture;
    private readonly List<string> tags = [];

    private EntryFormModel(EntryKind kind, IReadOnlyList<EntryTarget> targets, DateOnly today, CultureInfo culture)
    {
        Id = Guid.CreateVersion7();
        Kind = kind;
        TransferTargets = targets;
        Today = today;
        OccurredOn = today;
        this.culture = culture;
    }

    /// <summary>The entry's ID; the same for every submit of this form.</summary>
    public Guid Id { get; private set; }

    /// <summary>What the entry does.</summary>
    public EntryKind Kind { get; set; }

    /// <summary>Today in the owner's time zone: the default date and the latest allowed.</summary>
    public DateOnly Today { get; }

    /// <summary>Every bucket an entry can name, for the two sides of a transfer.</summary>
    public IReadOnlyList<EntryTarget> TransferTargets { get; }

    /// <summary>The buckets the chosen kind may name: a spend never comes from an investment bucket (plan 1a D11).</summary>
    public IReadOnlyList<EntryTarget> Targets =>
        Kind == EntryKind.Spend ? [.. TransferTargets.Where(target => target.Kind != BucketKind.Investment)] : TransferTargets;

    /// <summary>The <see cref="EntryTarget.Key"/> of the bucket of an opening balance, deposit or spend.</summary>
    public string? TargetKey { get; set; }

    /// <summary>The source of a transfer.</summary>
    public string? FromKey { get; set; }

    /// <summary>The destination of a transfer.</summary>
    public string? ToKey { get; set; }

    /// <summary>The amount as typed; for a transfer, everything that leaves the source, fee included.</summary>
    public string? AmountText { get; set; }

    /// <summary>A transfer's optional fee as typed, in the source's currency.</summary>
    public string? FeeText { get; set; }

    /// <summary>What the owner types for a transfer between currencies.</summary>
    public TransferInput TransferInput { get; set; }

    /// <summary>What arrived, as typed, in the destination's currency.</summary>
    public string? ReceivedText { get; set; }

    /// <summary>The rate as typed: units of the destination's currency per unit of the source's.</summary>
    public string? RateText { get; set; }

    /// <summary>The business date.</summary>
    public DateOnly OccurredOn { get; set; }

    /// <summary>The note as typed; empty is no note.</summary>
    public string? Note { get; set; }

    /// <summary>The tags added so far.</summary>
    public IReadOnlyList<string> Tags => tags;

    /// <summary>The chosen target of an opening balance, deposit or spend.</summary>
    public EntryTarget? Target => Find(Targets, TargetKey);

    /// <summary>The chosen source of a transfer.</summary>
    public EntryTarget? From => Find(TransferTargets, FromKey);

    /// <summary>The chosen destination of a transfer.</summary>
    public EntryTarget? To => Find(TransferTargets, ToKey);

    /// <summary>Whether both sides of a transfer are chosen and share a currency (what arrives is then computed).</summary>
    public bool IsSameCurrencyTransfer => Kind == EntryKind.Transfer && From is { } from && To is { } to && from.Currency == to.Currency;

    /// <summary>Whether both sides of a transfer are chosen in different currencies (the owner types what arrived or a rate).</summary>
    public bool IsCrossCurrencyTransfer => Kind == EntryKind.Transfer && From is { } from && To is { } to && from.Currency != to.Currency;

    /// <summary>
    /// A form for <paramref name="kind"/>: the owner's active buckets (<paramref name="buckets"/>, in the Api's order;
    /// system and archived ones left out), then <c>Unallocated</c> of every currency of <see cref="Currencies"/>.
    /// </summary>
    public static EntryFormModel Create(EntryKind kind, IReadOnlyList<BucketDto> buckets, DateOnly today, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(culture);

        IReadOnlyList<EntryTarget> targets =
        [
            .. buckets
                .Where(bucket => bucket.SystemRole == BucketSystemRole.None && bucket.Status == BucketStatus.Active)
                .Select(EntryTarget.Of),
            .. Currencies.Select(EntryTarget.UnallocatedOf),
        ];

        return new EntryFormModel(kind, targets, today, culture);
    }

    /// <summary>The currencies of phase 1a; each has an <c>Unallocated</c> target.</summary>
    public static IReadOnlyList<Currency> Currencies { get; } = [Currency.Pln, Currency.Eur, Currency.Usd];

    /// <summary>Adds a tag; returns why it cannot be added, or <c>null</c>.</summary>
    public ErrorInfo? AddTag(string? tag)
    {
        if (tag is null || tag.Length is 0 or > MaxTagLength || char.IsWhiteSpace(tag[0]) || char.IsWhiteSpace(tag[^1]) || tag.Any(char.IsControl))
        {
            return Error(TagInvalid);
        }

        if (tags.Exists(existing => string.Equals(existing.Normalize(NormalizationForm.FormC), tag.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase)))
        {
            return Error(TagDuplicate);
        }

        if (tags.Count >= MaxTags)
        {
            return Error(TagsTooMany);
        }

        tags.Add(tag);
        return null;
    }

    /// <summary>Removes a tag.</summary>
    public void RemoveTag(string tag) => tags.Remove(tag);

    /// <summary>What is wrong, by field (<see cref="EntryField"/>); empty when the entry can be shown for confirmation.</summary>
    public IReadOnlyDictionary<string, ErrorInfo> Validate() => Build().Errors;

    /// <summary>The entry to confirm, or <c>null</c> while something is wrong.</summary>
    public EntryDraft? ToDraft() => Build().Draft;

    /// <summary>Clears the form for the next entry, with a new ID; keeps the kind, the date and the buckets chosen.</summary>
    public void StartAnother()
    {
        Id = Guid.CreateVersion7();
        AmountText = null;
        FeeText = null;
        ReceivedText = null;
        RateText = null;
        Note = null;
        tags.Clear();
    }

    private (IReadOnlyDictionary<string, ErrorInfo> Errors, EntryDraft? Draft) Build()
    {
        var errors = new Dictionary<string, ErrorInfo>(StringComparer.Ordinal);

        if (OccurredOn > Today)
        {
            errors[EntryField.Date] = Error(DateInFuture);
        }

        if (!string.IsNullOrEmpty(Note) && !IsValidNote(Note))
        {
            errors[EntryField.Note] = Error(NoteInvalid);
        }

        var note = string.IsNullOrEmpty(Note) ? null : Note;
        IReadOnlyList<string> tagList = [.. tags];

        if (Kind != EntryKind.Transfer)
        {
            var target = Target;
            if (target is null)
            {
                errors[EntryField.Target] = Error(TargetRequired);
                return (errors, null);
            }

            var amount = Positive(AmountText, target.Currency, EntryField.Amount, errors);
            return errors.Count > 0 || amount is null
                ? (errors, null)
                : (errors, new EntryDraft(Kind, Id, target, amount, null, null, null, null, null, null, OccurredOn, note, tagList));
        }

        var from = From;
        var to = To;
        if (from is null)
        {
            errors[EntryField.From] = Error(TargetRequired);
        }

        if (to is null)
        {
            errors[EntryField.To] = Error(TargetRequired);
        }
        else if (from is not null && from.Key == to.Key)
        {
            errors[EntryField.To] = Error(SameBucket);
        }

        if (from is null || to is null)
        {
            return (errors, null);
        }

        var sent = Positive(AmountText, from.Currency, EntryField.Amount, errors);
        Money? fee = null;
        if (!string.IsNullOrWhiteSpace(FeeText))
        {
            fee = Positive(FeeText, from.Currency, EntryField.Fee, errors);
            if (fee is not null && sent is not null && fee >= sent)
            {
                errors[EntryField.Fee] = Error(FeeExceedsAmount);
            }
        }

        if (sent is null || errors.ContainsKey(EntryField.Fee))
        {
            return (errors, null);
        }

        var net = sent - (fee ?? Money.Zero(from.Currency));
        Money? received = null;
        if (from.Currency == to.Currency)
        {
            received = net;
        }
        else if (TransferInput == TransferInput.ReceivedAmount)
        {
            received = Positive(ReceivedText, to.Currency, EntryField.Received, errors);
        }
        else
        {
            var rate = RateInput.Parse(RateText, culture);
            if (rate.IsFailed)
            {
                errors[EntryField.Rate] = ErrorInfo.From(rate.Errors[0]);
            }
            else
            {
                received = net.Convert(rate.Value, to.Currency);
                if (received.Amount <= 0m)
                {
                    errors[EntryField.Rate] = Error(ErrorCodes.MoneyAmountNotPositive);
                    received = null;
                }
            }
        }

        if (errors.Count > 0 || received is null)
        {
            return (errors, null);
        }

        var derivedRate = decimal.Round(received.Amount / net.Amount, RateDecimalPlaces, MidpointRounding.ToEven);
        return (errors, new EntryDraft(Kind, Id, null, null, from, to, sent, fee, received, derivedRate, OccurredOn, note, tagList));
    }

    // An amount in `currency`, above zero; otherwise its error at `field`.
    private Money? Positive(string? text, Currency currency, string field, Dictionary<string, ErrorInfo> errors)
    {
        var parsed = MoneyInput.Parse(text, currency, culture);
        if (parsed.IsFailed)
        {
            errors[field] = ErrorInfo.From(parsed.Errors[0]);
            return null;
        }

        if (parsed.Value.Amount <= 0m)
        {
            errors[field] = Error(ErrorCodes.MoneyAmountNotPositive);
            return null;
        }

        return parsed.Value;
    }

    /// <summary>Whether <paramref name="note"/> follows the server's rules for a note (a reversal's too).</summary>
    public static bool IsValidNote(string note) =>
        note.Length <= MaxNoteLength
        && !char.IsWhiteSpace(note[0])
        && !char.IsWhiteSpace(note[^1])
        && !note.Any(character => char.IsControl(character) && character is not '\n' and not '\r');

    private static EntryTarget? Find(IReadOnlyList<EntryTarget> targets, string? key) =>
        key is null ? null : targets.FirstOrDefault(target => target.Key == key);

    private static ErrorInfo Error(string code) => ErrorInfo.From(AppError.Validation(code));
}
