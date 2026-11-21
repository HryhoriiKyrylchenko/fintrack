using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Contracts.V1.Loans;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Web.Api;

namespace FinTrack.Web.Entries;

/// <summary>
/// A checked entry, shown for confirmation before it is written (DESIGN 1.6) and then sent as its request.
/// Either <see cref="Target"/> and <see cref="Amount"/> are set (opening balance, deposit, spend, adjustment with its
/// <see cref="Direction"/>), or the transfer fields; a borrow has its lender in <see cref="From"/>, its destination in
/// <see cref="To"/> (<c>null</c>: outside the app) and its principal in <see cref="Amount"/>; a repayment has its
/// <see cref="Loan"/>, its source in <see cref="From"/> (<c>null</c>: outside the app), the lender in <see cref="To"/>
/// and its amount in <see cref="Amount"/>.
/// </summary>
/// <param name="Kind">What the entry does.</param>
/// <param name="Id">The client-generated ID; the same for every submit of one form.</param>
/// <param name="Target">The bucket of an opening balance, deposit or spend.</param>
/// <param name="Amount">Its amount, in the target's currency.</param>
/// <param name="From">The source of a transfer.</param>
/// <param name="To">The destination of a transfer.</param>
/// <param name="FromAmount">Everything that leaves the source, fee included.</param>
/// <param name="Fee">The transfer's fee, or <c>null</c>.</param>
/// <param name="ToAmount">What arrives at the destination.</param>
/// <param name="Rate">The rate the server will derive and store: <c>ToAmount ÷ (FromAmount − Fee)</c>, 12 places, banker's rounding.</param>
/// <param name="OccurredOn">The business date.</param>
/// <param name="Note">The note, or <c>null</c>.</param>
/// <param name="Tags">The tags, possibly none.</param>
/// <param name="Loan">The loan a repayment pays, as the Api listed it when the form was filled.</param>
/// <param name="Direction">Whether an adjustment raises or lowers its bucket.</param>
public sealed record EntryDraft(
    EntryKind Kind,
    Guid Id,
    EntryTarget? Target,
    Money? Amount,
    EntryTarget? From,
    EntryTarget? To,
    Money? FromAmount,
    Money? Fee,
    Money? ToAmount,
    decimal? Rate,
    DateOnly OccurredOn,
    string? Note,
    IReadOnlyList<string> Tags,
    LoanDto? Loan = null,
    AdjustmentDirection? Direction = null)
{
    /// <summary>The Api route and body that write this entry.</summary>
    public EntryRequest ToRequest() => Kind switch
    {
        EntryKind.OpeningBalance => new("v1/journal/opening-balances",
            new AddOpeningBalanceRequest(Id, Amount!, OccurredOn, Target!.BucketId, UnallocatedCurrency(Target), Note, Tags)),
        EntryKind.Deposit => new("v1/journal/deposits",
            new AddDepositRequest(Id, Amount!, OccurredOn, Target!.BucketId, UnallocatedCurrency(Target), Note, Tags)),
        EntryKind.Spend => new("v1/journal/spends",
            new AddSpendRequest(Id, Amount!, OccurredOn, Target!.BucketId, UnallocatedCurrency(Target), Note, Tags)),
        EntryKind.Transfer => new("v1/journal/transfers",
            new AddTransferRequest(Id, FromAmount!, ToAmount!, OccurredOn, From!.BucketId, UnallocatedCurrency(From), To!.BucketId, UnallocatedCurrency(To), Fee, Note, Tags)),
        EntryKind.Borrow => new("v1/journal/borrows",
            new AddBorrowRequest(Id, From!.BucketId!.Value, Amount!, OccurredOn, To?.BucketId, To is { } to ? UnallocatedCurrency(to) : null, Note, Tags)),
        EntryKind.Repay => new("v1/journal/repayments",
            new AddRepaymentRequest(Id, Loan!.Id, Amount!, OccurredOn, From?.BucketId, From is { } from ? UnallocatedCurrency(from) : null, Note, Tags)),
        EntryKind.Adjust => new("v1/journal/adjustments",
            new AddAdjustmentRequest(Id, Amount!, Direction!.Value, OccurredOn, Note, Target!.BucketId, UnallocatedCurrency(Target), Tags)),
        _ => throw new InvalidOperationException($"Unknown entry kind {Kind}."),
    };

    private static Currency? UnallocatedCurrency(EntryTarget target) => target.IsUnallocated ? target.Currency : null;
}

/// <summary>An entry's Api call.</summary>
/// <param name="Path">The route, relative to the Api's base address.</param>
/// <param name="Body">The request body, one of the <c>Add…Request</c> contracts.</param>
public sealed record EntryRequest(string Path, object Body);

/// <summary>Writes entries through <see cref="ApiClient"/>.</summary>
public sealed class EntriesClient(ApiClient api)
{
    /// <summary>Sends <paramref name="request"/>; a repeat with the same ID and content writes nothing and succeeds.</summary>
    public Task<ApiResult> SubmitAsync(EntryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return api.PostAsync(request.Path, request.Body, cancellationToken);
    }
}
