using FluentResults;
using Mediator;

namespace FinTrack.Budget.Contracts.V1.Journal;

/// <summary>
/// One page of the owner's journal, newest first (the reverse of the replay order: business date, then
/// recorded time, then ID). Every entry is listed, reversals and reversed entries included; a reversed entry
/// names its reversal. <c>GET /api/v1/journal?bucketId=&amp;loanId=&amp;from=&amp;to=&amp;limit=&amp;cursor=</c>.
/// </summary>
/// <remarks>
/// <para>
/// Paging: ask without <paramref name="Cursor"/> for the newest entries, then pass the page's
/// <see cref="JournalPageDto.NextCursor"/> for the entries after them, until it is <c>null</c>. Entries are
/// only ever added, so a page never repeats or skips an entry that existed when the first page was read; an
/// entry added or backdated later appears where its date puts it.
/// </para>
/// <para>
/// Failures: <c>validation.failed</c> (400) at <c>limit</c> (rule <c>InclusiveBetweenValidator</c>), at
/// <c>to</c> (rule <c>FromNotAfterTo</c>) or at <c>cursor</c> (rule <c>CursorFormat</c>); <c>bucket.not_found</c>
/// (404) for a bucket the owner does not have; <c>loan.not_found</c> (404) for a loan no borrow of the owner opened.
/// </para>
/// </remarks>
/// <param name="BucketId">Only entries that touch this bucket: its own entries, either side of a transfer, and reversals of such entries. Archived buckets may be named.</param>
/// <param name="From">Only entries with a business date on or after this day.</param>
/// <param name="To">Only entries with a business date on or before this day; not before <paramref name="From"/>.</param>
/// <param name="Limit">The most entries on one page, 1 to <see cref="MaxLimit"/>; <see cref="DefaultLimit"/> when not given.</param>
/// <param name="Cursor">The <see cref="JournalPageDto.NextCursor"/> of the previous page, unchanged; <c>null</c> for the first page. Opaque: its content is not part of the contract.</param>
/// <param name="LoanId">
/// Only the loan's entries (plan 1b B19): its <c>Borrow</c>, its <c>Repayment</c>s, and reversals of either. A reversed borrow may be
/// named, so its history stays readable. Combines with the other filters.
/// </param>
public sealed record GetJournal(
    Guid? BucketId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int Limit = GetJournal.DefaultLimit,
    string? Cursor = null,
    Guid? LoanId = null) : IQuery<Result<JournalPageDto>>
{
    /// <summary>The page size when none is asked for.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The largest page that can be asked for.</summary>
    public const int MaxLimit = 200;
}
