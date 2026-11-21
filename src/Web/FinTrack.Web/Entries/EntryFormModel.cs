using System.Globalization;
using System.Text;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Contracts.V1.Loans;
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

    /// <summary>Money borrowed from a savings bucket, to another bucket or outside the app (plan 1b step 7).</summary>
    Borrow,

    /// <summary>Loan principal paid back to its lender.</summary>
    Repay,

    /// <summary>A correction of one bucket, up or down, with a reason.</summary>
    Adjust,
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

    /// <summary>The lender of a borrow.</summary>
    public const string Lender = "lender";

    /// <summary>Where a borrow's money goes.</summary>
    public const string Destination = "destination";

    /// <summary>The loan a repayment pays.</summary>
    public const string Loan = "loan";

    /// <summary>Where a repayment's money comes from.</summary>
    public const string Source = "source";

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
/// The values of the "Add entry" form (plan 1a step 16): opening balance, deposit, spend or transfer; from plan 1b step 7
/// also borrow, repay and adjust. The server's rules are checked here before anything is sent (DESIGN 9: say why before
/// the owner confirms), with the server's own codes where one exists (<c>entry.date_in_future</c>,
/// <c>transfer.same_bucket</c>, <c>transfer.fee_exceeds_amount</c>, <c>money.amount_not_positive</c>,
/// <c>loan.repayment_exceeds_outstanding</c>); the server still decides, and its refusal is shown the same way. Values
/// are rejected, never trimmed or corrected (ARCHITECTURE 4.2).
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
/// <para>
/// Loans (plan 1b B2): a borrow's lender is one of the owner's savings buckets (never <c>Unallocated</c> or an investment
/// bucket); its destination is another bucket of the lender's currency, <c>Unallocated</c> of it, or outside the app
/// (<see cref="OutsideKey"/>). A repayment pays an open loan (the Api's list), from a bucket of the loan's currency other
/// than its lender, <c>Unallocated</c> of it, or outside; it may not exceed what the loan still owes (B4). Only the lists
/// offer what the server allows, so a refused choice cannot be made.
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

    /// <summary>No loan chosen for a repayment.</summary>
    public const string LoanRequired = "input.loan_required";

    /// <summary>An adjustment without its reason (the note, required for this kind).</summary>
    public const string ReasonRequired = "input.reason_required";

    /// <summary>The key of "outside the app" as a borrow's destination or a repayment's source.</summary>
    public const string OutsideKey = "outside";

    private const string RepaymentExceedsOutstanding = "loan.repayment_exceeds_outstanding";

    private const string SameBucket = "transfer.same_bucket";
    private const string FeeExceedsAmount = "transfer.fee_exceeds_amount";
    private const string DateInFuture = "entry.date_in_future";

    private readonly CultureInfo culture;
    private readonly List<string> tags = [];

    private EntryFormModel(EntryKind kind, IReadOnlyList<EntryTarget> targets, IReadOnlyList<LoanDto> openLoans, DateOnly today, CultureInfo culture)
    {
        Id = Guid.CreateVersion7();
        Kind = kind;
        TransferTargets = targets;
        OpenLoans = openLoans;
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

    /// <summary>The open loans a repayment can pay, in the Api's order.</summary>
    public IReadOnlyList<LoanDto> OpenLoans { get; private set; }

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

    /// <summary>The <see cref="EntryTarget.Key"/> of a borrow's lender.</summary>
    public string? LenderKey { get; set; }

    /// <summary>The key of a borrow's destination, or <see cref="OutsideKey"/>.</summary>
    public string? DestinationKey { get; set; }

    /// <summary>The ID of the loan a repayment pays, as text.</summary>
    public string? LoanKey { get; set; }

    /// <summary>The key of a repayment's source, or <see cref="OutsideKey"/>.</summary>
    public string? SourceKey { get; set; }

    /// <summary>Whether an adjustment raises or lowers its bucket.</summary>
    public AdjustmentDirection Direction { get; set; }

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

    /// <summary>The buckets that can lend: the owner's savings buckets (plan 1b B2).</summary>
    public IReadOnlyList<EntryTarget> Lenders => [.. TransferTargets.Where(target => !target.IsUnallocated && target.Kind == BucketKind.Savings)];

    /// <summary>The chosen lender of a borrow.</summary>
    public EntryTarget? Lender => Find(Lenders, LenderKey);

    /// <summary>Where a borrow's money can go besides outside the app: the other buckets of the lender's currency and its <c>Unallocated</c>.</summary>
    public IReadOnlyList<EntryTarget> Destinations =>
        Lender is { } lender ? [.. TransferTargets.Where(target => target.Currency == lender.Currency && target.Key != lender.Key)] : [];

    /// <summary>The chosen destination of a borrow; <c>null</c> for none or outside the app.</summary>
    public EntryTarget? Destination => Find(Destinations, DestinationKey);

    /// <summary>The chosen loan of a repayment.</summary>
    public LoanDto? Loan => LoanKey is null ? null : OpenLoans.FirstOrDefault(loan => loan.Id.ToString() == LoanKey);

    /// <summary>Where a repayment's money can come from besides outside the app: the buckets of the loan's currency except its lender, and its <c>Unallocated</c>.</summary>
    public IReadOnlyList<EntryTarget> Sources =>
        Loan is { } loan ? [.. TransferTargets.Where(target => target.Currency == loan.Principal.Currency && target.BucketId != loan.LenderBucketId)] : [];

    /// <summary>The chosen source of a repayment; <c>null</c> for none or outside the app.</summary>
    public EntryTarget? Source => Find(Sources, SourceKey);

    /// <summary>Whether both sides of a transfer are chosen and share a currency (what arrives is then computed).</summary>
    public bool IsSameCurrencyTransfer => Kind == EntryKind.Transfer && From is { } from && To is { } to && from.Currency == to.Currency;

    /// <summary>Whether both sides of a transfer are chosen in different currencies (the owner types what arrived or a rate).</summary>
    public bool IsCrossCurrencyTransfer => Kind == EntryKind.Transfer && From is { } from && To is { } to && from.Currency != to.Currency;

    /// <summary>
    /// A form for <paramref name="kind"/>: the owner's active buckets (<paramref name="buckets"/>, in the Api's order;
    /// system and archived ones left out), then <c>Unallocated</c> of every currency of <see cref="Currencies"/>; the open
    /// ones of <paramref name="loans"/> for a repayment.
    /// </summary>
    public static EntryFormModel Create(EntryKind kind, IReadOnlyList<BucketDto> buckets, DateOnly today, CultureInfo culture, IReadOnlyList<LoanDto>? loans = null)
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

        return new EntryFormModel(kind, targets, OpenOf(loans ?? []), today, culture);
    }

    /// <summary>The open loans after a write changed them; a chosen loan that is closed now is no longer chosen.</summary>
    public void ReplaceLoans(IReadOnlyList<LoanDto> loans)
    {
        ArgumentNullException.ThrowIfNull(loans);
        OpenLoans = OpenOf(loans);
        if (Loan is null)
        {
            LoanKey = null;
        }
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

        return Kind switch
        {
            EntryKind.Transfer => BuildTransfer(errors, note, tagList),
            EntryKind.Borrow => BuildBorrow(errors, note, tagList),
            EntryKind.Repay => BuildRepay(errors, note, tagList),
            _ => BuildSingle(errors, note, tagList),
        };
    }

    // An opening balance, deposit, spend or adjustment: one bucket and an amount; an adjustment also needs its reason.
    private (IReadOnlyDictionary<string, ErrorInfo> Errors, EntryDraft? Draft) BuildSingle(Dictionary<string, ErrorInfo> errors, string? note, IReadOnlyList<string> tagList)
    {
        if (Kind == EntryKind.Adjust && note is null)
        {
            errors[EntryField.Note] = Error(ReasonRequired);
        }

        var target = Target;
        if (target is null)
        {
            errors[EntryField.Target] = Error(TargetRequired);
            return (errors, null);
        }

        var amount = Positive(AmountText, target.Currency, EntryField.Amount, errors);
        return errors.Count > 0 || amount is null
            ? (errors, null)
            : (errors, new EntryDraft(Kind, Id, target, amount, null, null, null, null, null, null, OccurredOn, note, tagList,
                Direction: Kind == EntryKind.Adjust ? Direction : null));
    }

    // A borrow: a lender, a destination (outside the app is a choice), the principal in the lender's currency.
    private (IReadOnlyDictionary<string, ErrorInfo> Errors, EntryDraft? Draft) BuildBorrow(Dictionary<string, ErrorInfo> errors, string? note, IReadOnlyList<string> tagList)
    {
        var lender = Lender;
        if (lender is null)
        {
            errors[EntryField.Lender] = Error(TargetRequired);
            return (errors, null);
        }

        var destination = Destination;
        if (destination is null && DestinationKey != OutsideKey)
        {
            errors[EntryField.Destination] = Error(TargetRequired);
        }

        var principal = Positive(AmountText, lender.Currency, EntryField.Amount, errors);
        return errors.Count > 0 || principal is null
            ? (errors, null)
            : (errors, new EntryDraft(Kind, Id, null, principal, lender, destination, null, null, null, null, OccurredOn, note, tagList));
    }

    // A repayment: an open loan, a source (outside the app is a choice), an amount in the loan's currency, at most what it owes.
    private (IReadOnlyDictionary<string, ErrorInfo> Errors, EntryDraft? Draft) BuildRepay(Dictionary<string, ErrorInfo> errors, string? note, IReadOnlyList<string> tagList)
    {
        var loan = Loan;
        if (loan is null)
        {
            errors[EntryField.Loan] = Error(LoanRequired);
            return (errors, null);
        }

        var source = Source;
        if (source is null && SourceKey != OutsideKey)
        {
            errors[EntryField.Source] = Error(TargetRequired);
        }

        var amount = Positive(AmountText, loan.Principal.Currency, EntryField.Amount, errors);
        if (amount is not null && amount > loan.Outstanding)
        {
            errors[EntryField.Amount] = ErrorInfo.From(AppError.BusinessRule(RepaymentExceedsOutstanding));
        }

        var lender = TransferTargets.FirstOrDefault(target => target.BucketId == loan.LenderBucketId);
        return errors.Count > 0 || amount is null
            ? (errors, null)
            : (errors, new EntryDraft(Kind, Id, null, amount, source, lender, null, null, null, null, OccurredOn, note, tagList, Loan: loan));
    }

    private (IReadOnlyDictionary<string, ErrorInfo> Errors, EntryDraft? Draft) BuildTransfer(Dictionary<string, ErrorInfo> errors, string? note, IReadOnlyList<string> tagList)
    {
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

    private static List<LoanDto> OpenOf(IReadOnlyList<LoanDto> loans) => [.. loans.Where(loan => loan.Status == LoanStatus.Open)];

    private static EntryTarget? Find(IReadOnlyList<EntryTarget> targets, string? key) =>
        key is null ? null : targets.FirstOrDefault(target => target.Key == key);

    private static ErrorInfo Error(string code) => ErrorInfo.From(AppError.Validation(code));
}
