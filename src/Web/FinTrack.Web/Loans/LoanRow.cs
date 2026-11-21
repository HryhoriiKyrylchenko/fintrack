using System.Globalization;
using FinTrack.Budget.Contracts.V1.Buckets;
using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Contracts.V1.Loans;
using FinTrack.Web.Api;
using FinTrack.Web.Buckets;
using FinTrack.Web.Formatting;
using FinTrack.Web.Journal;
using FinTrack.Web.Resources;
using Microsoft.Extensions.Localization;

namespace FinTrack.Web.Loans;

/// <summary>
/// One loan as the Loans screen and the Repay form read it (plan 1b step 7): the lender and the destination by name
/// ("Outside the app" when the money left it), the figures from the Api, and whether it can still be repaid. The Web
/// app computes no figure: principal, repaid and outstanding are the Api's.
/// </summary>
/// <param name="loan">The loan.</param>
/// <param name="buckets">Every bucket of the owner by ID, archived ones included, to name them.</param>
public sealed class LoanRow(LoanDto loan, IReadOnlyDictionary<Guid, BucketDto> buckets)
{
    /// <summary>The loan.</summary>
    public LoanDto Loan { get; } = loan ?? throw new ArgumentNullException(nameof(loan));

    /// <summary>Whether something is still owed: only an open loan can be repaid.</summary>
    public bool IsOpen => Loan.Status == LoanStatus.Open;

    /// <summary>The loans in the order the screen shows them: open ones first, then closed ones, each in the Api's order (oldest borrow first).</summary>
    public static IReadOnlyList<LoanRow> Order(IReadOnlyList<LoanDto> loans, IReadOnlyDictionary<Guid, BucketDto> buckets)
    {
        ArgumentNullException.ThrowIfNull(loans);
        return [.. loans.Where(loan => loan.Status == LoanStatus.Open).Concat(loans.Where(loan => loan.Status != LoanStatus.Open)).Select(loan => new LoanRow(loan, buckets))];
    }

    /// <summary>The bucket the money was borrowed from.</summary>
    public string LenderName(IStringLocalizer<SharedResource> localizer) => Name(Loan.LenderBucketId, localizer);

    /// <summary>The bucket the money went to, or "Outside the app".</summary>
    public string DestinationName(IStringLocalizer<SharedResource> localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return Loan.ToBucketId is { } to ? Name(to, localizer) : localizer["Loans.Outside"];
    }

    /// <summary>The loan in one line, for a choice list and a dialog title: lender, destination, principal and date.</summary>
    public string Describe(IStringLocalizer<SharedResource> localizer, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(culture);
        return localizer["Loans.Line", LenderName(localizer), DestinationName(localizer), MoneyFormatter.Format(Loan.Principal, culture), Loan.BorrowedOn.ToString("d", culture)];
    }

    private string Name(Guid bucketId, IStringLocalizer<SharedResource> localizer) =>
        buckets.TryGetValue(bucketId, out var bucket) ? BucketNames.Display(bucket, localizer) : localizer["Journal.UnknownBucket"];
}

/// <summary>The loan endpoints of the Api, through <see cref="ApiClient"/>.</summary>
public sealed class LoansClient(ApiClient api)
{
    private const string Path = "v1/loans";

    /// <summary>The owner's loans in the order they were borrowed; closed ones only when <paramref name="includeClosed"/>.</summary>
    public Task<ApiResult<IReadOnlyList<LoanDto>>> ListAsync(bool includeClosed, CancellationToken cancellationToken) =>
        api.GetAsync<IReadOnlyList<LoanDto>>(includeClosed ? Path + "?includeClosed=true" : Path, cancellationToken);

    /// <summary>Every journal entry of the loan (its borrow, its repayments and reversals of them), newest first, read page by page.</summary>
    public async Task<ApiResult<IReadOnlyList<JournalEntryDto>>> HistoryAsync(Guid loanId, CancellationToken cancellationToken)
    {
        var entries = new List<JournalEntryDto>();
        string? cursor = null;
        do
        {
            var page = await api.GetAsync<JournalPageDto>(JournalClient.PagePath(JournalFilter.None with { LoanId = loanId }, cursor), cancellationToken).ConfigureAwait(false);
            if (page.Value is not { } value)
            {
                return ApiResult<IReadOnlyList<JournalEntryDto>>.Failure(page.ErrorCode!, page.ErrorParams);
            }

            entries.AddRange(value.Entries);
            cursor = value.NextCursor;
        }
        while (cursor is not null);

        return ApiResult<IReadOnlyList<JournalEntryDto>>.Success(entries);
    }
}
