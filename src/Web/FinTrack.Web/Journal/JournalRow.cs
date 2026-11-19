using System.Globalization;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Web.Api;
using FinTrack.Web.Buckets;
using FinTrack.Web.Entries;
using FinTrack.Web.Formatting;
using FinTrack.Web.Resources;
using Microsoft.Extensions.Localization;

namespace FinTrack.Web.Journal;

/// <summary>
/// One journal entry as the Journal screen reads it (plan 1a step 17): its type, the buckets it touches by name,
/// its amount with the direction the type gives it, and whether the owner may reverse it.
/// </summary>
/// <param name="entry">The entry.</param>
/// <param name="buckets">Every bucket of the owner by ID, archived ones included, to name the buckets.</param>
/// <param name="loaded">The entries loaded so far by ID, to name what a reversal cancels when it is among them.</param>
public sealed class JournalRow(JournalEntryDto entry, IReadOnlyDictionary<Guid, BucketDto> buckets, IReadOnlyDictionary<Guid, JournalEntryDto> loaded)
{
    /// <summary>The entry.</summary>
    public JournalEntryDto Entry { get; } = entry ?? throw new ArgumentNullException(nameof(entry));

    /// <summary>The resource key of the entry's type.</summary>
    public string TypeKey => TypeKeyOf(Entry.Type);

    /// <summary>Whether a reversal cancels this entry: it counts as if it never happened.</summary>
    public bool IsReversed => Entry.ReversedBy is not null;

    /// <summary>Whether this entry is a reversal; it moves no money itself.</summary>
    public bool IsReversal => Entry.Type == JournalEntryType.Reversal;

    /// <summary>
    /// The amount of an opening balance, deposit or spend with its direction: money in is positive, a spend is
    /// negative. <c>null</c> for a transfer (two amounts) and a reversal (none).
    /// </summary>
    public Money? SignedAmount => Entry switch
    {
        { Type: JournalEntryType.Spend, Amount: { } amount } => -amount,
        { Type: JournalEntryType.OpeningBalance or JournalEntryType.Deposit, Amount: { } amount } => amount,
        _ => null,
    };

    /// <summary>What left the source of a transfer, fee included.</summary>
    public Money? FromAmount => Entry.FromAmount;

    /// <summary>What arrived at the destination of a transfer.</summary>
    public Money? ToAmount => Entry.ToAmount;

    /// <summary>
    /// Whether the owner may reverse the entry: not a reversal, not reversed already, and touching no archived
    /// bucket (an archived bucket's balance never changes again; the server refuses with <c>bucket.archived</c>).
    /// The server still checks that no savings bucket would go below zero.
    /// </summary>
    public bool CanReverse =>
        !IsReversal && !IsReversed && BucketIds().All(id => buckets.TryGetValue(id, out var bucket) && bucket.Status == BucketStatus.Active);

    /// <summary>The line under the type: the bucket, "from → to", or what a reversal cancels.</summary>
    public string Describe(IStringLocalizer<SharedResource> localizer, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(culture);

        return Entry.Type switch
        {
            JournalEntryType.Transfer => localizer["Journal.TransferLine", Name(Entry.FromBucketId, localizer), Name(Entry.ToBucketId, localizer)],
            JournalEntryType.Reversal => Entry.ReversedEntryId is { } cancelled && loaded.TryGetValue(cancelled, out var reversed)
                ? localizer["Journal.Cancels", localizer[ArticleKeyOf(reversed.Type)], reversed.OccurredOn.ToString("d", culture), new JournalRow(reversed, buckets, loaded).Place(localizer)]
                : localizer["Journal.CancelsEarlier"],
            _ => Name(Entry.BucketId, localizer),
        };
    }

    /// <summary>The question of the reversal dialog, naming the entry, its amount, its buckets and its date (DESIGN 9).</summary>
    public string ReversalTitle(IStringLocalizer<SharedResource> localizer, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(culture);

        var date = Entry.OccurredOn.ToString("d", culture);
        return Entry.Type == JournalEntryType.Transfer
            ? localizer["Journal.ReverseTransferTitle", MoneyFormatter.Format(Entry.FromAmount!, culture), Name(Entry.FromBucketId, localizer), Name(Entry.ToBucketId, localizer), date]
            : localizer["Journal.ReverseTitle", localizer[ArticleKeyOf(Entry.Type)], MoneyFormatter.Format(Entry.Amount!, culture), Place(localizer), date];
    }

    /// <summary>The resource key of a type's name.</summary>
    public static string TypeKeyOf(JournalEntryType type) => "Journal.Type." + type;

    // The type in a sentence ("the deposit").
    private static string ArticleKeyOf(JournalEntryType type) => "Journal.TheType." + type;

    // "in Cushion" for money in, "from Cushion" for a spend, "from Cushion to Holiday" for a transfer.
    private string Place(IStringLocalizer<SharedResource> localizer) => Entry.Type switch
    {
        JournalEntryType.Spend => localizer["Journal.FromBucket", Name(Entry.BucketId, localizer)],
        JournalEntryType.Transfer => localizer["Journal.FromTo", Name(Entry.FromBucketId, localizer), Name(Entry.ToBucketId, localizer)],
        _ => localizer["Journal.InBucket", Name(Entry.BucketId, localizer)],
    };

    private IEnumerable<Guid> BucketIds() =>
        new[] { Entry.BucketId, Entry.FromBucketId, Entry.ToBucketId }.OfType<Guid>();

    private string Name(Guid? bucketId, IStringLocalizer<SharedResource> localizer) =>
        bucketId is { } id && buckets.TryGetValue(id, out var bucket)
            ? BucketNames.Display(bucket, localizer)
            : localizer["Journal.UnknownBucket"];
}

/// <summary>The Journal screen's filters (plan 1a step 17): a bucket and a period, each optional.</summary>
/// <param name="BucketId">Only entries that touch this bucket.</param>
/// <param name="From">Only entries on or after this day.</param>
/// <param name="To">Only entries on or before this day.</param>
public sealed record JournalFilter(Guid? BucketId, DateOnly? From, DateOnly? To)
{
    /// <summary>The period ends before it starts.</summary>
    public const string PeriodInvalid = "input.period_invalid";

    /// <summary>No filter: every entry.</summary>
    public static JournalFilter None { get; } = new(null, null, null);

    /// <summary>Whether any filter is set.</summary>
    public bool IsActive => BucketId is not null || From is not null || To is not null;

    /// <summary>Why the period cannot be asked for, or <c>null</c>.</summary>
    public ErrorInfo? PeriodError => From is { } from && To is { } to && from > to ? ErrorInfo.From(AppError.Validation(PeriodInvalid)) : null;
}

/// <summary>
/// The reversal dialog's values: one version 7 ID per dialog (a double click writes one reversal), dated today in
/// the owner's time zone (a reversal's date is the day of the correction, ARCHITECTURE 4.2), and an optional note.
/// </summary>
/// <param name="reversedEntryId">The entry to cancel.</param>
/// <param name="today">Today in the owner's time zone.</param>
public sealed class ReversalForm(Guid reversedEntryId, DateOnly today)
{
    /// <summary>The reversal's ID; the same for every submit of this dialog.</summary>
    public Guid Id { get; } = Guid.CreateVersion7();

    /// <summary>Why, as typed; empty is no note.</summary>
    public string? Note { get; set; }

    /// <summary>What is wrong with the note, or <c>null</c>.</summary>
    public ErrorInfo? NoteError =>
        !string.IsNullOrEmpty(Note) && !EntryFormModel.IsValidNote(Note) ? ErrorInfo.From(AppError.Validation(EntryFormModel.NoteInvalid)) : null;

    /// <summary>The request that writes the reversal.</summary>
    public ReverseEntryRequest ToRequest() => new(Id, reversedEntryId, today, string.IsNullOrEmpty(Note) ? null : Note, null);
}

/// <summary>The journal endpoints of the Api, through <see cref="ApiClient"/>.</summary>
public sealed class JournalClient(ApiClient api)
{
    /// <summary>Entries per page.</summary>
    public const int PageSize = 25;

    /// <summary>One page of the journal, newest first; <paramref name="cursor"/> is the previous page's, unchanged.</summary>
    public Task<ApiResult<JournalPageDto>> PageAsync(JournalFilter filter, string? cursor, CancellationToken cancellationToken) =>
        api.GetAsync<JournalPageDto>(PagePath(filter, cursor), cancellationToken);

    /// <summary>Writes a reversal; repeating the same request writes nothing.</summary>
    public Task<ApiResult> ReverseAsync(ReverseEntryRequest request, CancellationToken cancellationToken) =>
        api.PostAsync("v1/journal/reversals", request, cancellationToken);

    /// <summary>The route of a page: the filter's values in the Api's spelling, the page size and the cursor.</summary>
    public static string PagePath(JournalFilter filter, string? cursor)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var parts = new List<string>();
        if (filter.BucketId is { } bucketId)
        {
            parts.Add($"bucketId={bucketId:D}");
        }

        if (filter.From is { } from)
        {
            parts.Add($"from={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        if (filter.To is { } to)
        {
            parts.Add($"to={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        parts.Add($"limit={PageSize.ToString(CultureInfo.InvariantCulture)}");
        if (cursor is not null)
        {
            parts.Add($"cursor={Uri.EscapeDataString(cursor)}");
        }

        return "v1/journal?" + string.Join('&', parts);
    }
}
