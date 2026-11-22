using FinTrack.Budget.Contracts.V1.Journal;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using FluentValidation;
using Mediator;

namespace FinTrack.Budget.Application.Journal;

/// <summary>Shape checks of <see cref="GetJournal"/>: page size, period and cursor.</summary>
public sealed class GetJournalValidator : AbstractValidator<GetJournal>
{
    /// <summary>The <c>rule</c> when <see cref="GetJournal.From"/> is after <see cref="GetJournal.To"/> (reported at <c>to</c>).</summary>
    public const string FromNotAfterTo = "FromNotAfterTo";

    /// <summary>Creates the rules.</summary>
    public GetJournalValidator()
    {
        RuleFor(query => query.Limit).InclusiveBetween(1, GetJournal.MaxLimit);
        RuleFor(query => query.To)
            .Must((query, to) => query.From is null || to is null || query.From <= to)
            .WithErrorCode(FromNotAfterTo);
        RuleFor(query => query.Cursor)
            .Must(cursor => cursor is null || JournalCursor.TryParse(cursor, out _))
            .WithErrorCode(JournalCursor.FormatRule);
    }
}

/// <summary>
/// Answers <see cref="GetJournal"/> from the journal as loaded by <see cref="BudgetStateLoader"/>: filters,
/// sorts newest first (the reverse of <see cref="ReplayOrder"/>) and pages with <see cref="JournalCursor"/>.
/// </summary>
/// <remarks>
/// Phase 1a filters and pages in memory after loading the whole journal (it is small for one owner;
/// plan 1a assumptions). The cursor is a replay position, so a database-side read can replace this without
/// changing the contract.
/// </remarks>
public sealed class GetJournalHandler(BudgetStateLoader loader) : IQueryHandler<GetJournal, Result<JournalPageDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<JournalPageDto>> Handle(GetJournal query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        ReplayPosition? after = null;
        if (query.Cursor is not null)
        {
            // The validator runs first in the pipeline; a cursor that reaches here unread is a bug.
            after = JournalCursor.TryParse(query.Cursor, out var position)
                ? position
                : throw new ArgumentException("The cursor is not one GetJournal wrote.", nameof(query));
        }

        var loaded = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailed)
        {
            return loaded.ToResult<JournalPageDto>();
        }

        var snapshot = loaded.Value;
        if (query.BucketId is { } bucketId && snapshot.FindBucket(bucketId) is null)
        {
            return Result.Fail<JournalPageDto>(AppError.NotFound(BudgetErrorCodes.BucketNotFound));
        }

        var byId = snapshot.Journal.ToDictionary(entry => entry.Id);
        if (query.LoanId is { } loanId && !(byId.TryGetValue(loanId, out var opened) && opened is Borrow))
        {
            return Result.Fail<JournalPageDto>(AppError.NotFound(BudgetErrorCodes.LoanNotFound));
        }

        var reversedBy = snapshot.Journal.OfType<Reversal>().ToDictionary(reversal => reversal.ReversedEntryId.Value, reversal => reversal.Id);

        var page = snapshot.Journal
            .Where(entry => query.From is null || entry.OccurredOn >= query.From)
            .Where(entry => query.To is null || entry.OccurredOn <= query.To)
            .Where(entry => query.BucketId is null || Touches(entry, BucketId.From(query.BucketId.Value), byId))
            .Where(entry => query.LoanId is null || BelongsTo(entry, query.LoanId.Value, byId))
            .Where(entry => after is null || ReplayPosition.Of(entry) < after.Value)
            .OrderDescending(ReplayOrder.Instance)
            .Take(query.Limit + 1)
            .ToList();

        var hasMore = page.Count > query.Limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        IReadOnlyList<JournalEntryDto> entries =
            [.. page.Select(entry => JournalContractMapping.ToDto(entry, reversedBy.TryGetValue(entry.Id, out var reversal) ? reversal : null, byId))];

        return Result.Ok(new JournalPageDto(entries, hasMore ? JournalCursor.Encode(ReplayPosition.Of(page[^1])) : null));
    }

    // Whether an entry is part of the loan's history: its borrow, its repayments, and reversals of either.
    private static bool BelongsTo(JournalEntry entry, Guid loanId, IReadOnlyDictionary<Guid, JournalEntry> byId) => entry switch
    {
        Borrow borrow => borrow.Id == loanId,
        Repayment repayment => repayment.LoanId.Value == loanId,
        Reversal reversal => byId.TryGetValue(reversal.ReversedEntryId.Value, out var reversed) && BelongsTo(reversed, loanId, byId),
        _ => false,
    };

    // Whether an entry moves money of the bucket, or defines one of its plans; a reversal touches what the entry it cancels touches.
    private static bool Touches(JournalEntry entry, BucketId bucketId, IReadOnlyDictionary<Guid, JournalEntry> byId) => entry switch
    {
        SingleBucketEntry single => single.BucketId == bucketId,
        Transfer transfer => transfer.FromBucketId == bucketId || transfer.ToBucketId == bucketId,
        Borrow borrow => borrow.LenderBucketId == bucketId || borrow.ToBucketId == bucketId,
        Repayment repayment => repayment.FromBucketId == bucketId || JournalContractMapping.LenderOf(repayment, byId) == bucketId,
        PlanCreated created => created.BucketId == bucketId,
        PlanAmountChanged changed => JournalContractMapping.BucketOf(changed.PlanId, byId) == bucketId,
        PlanEndChanged ended => JournalContractMapping.BucketOf(ended.PlanId, byId) == bucketId,
        Reversal reversal => byId.TryGetValue(reversal.ReversedEntryId.Value, out var reversed) && Touches(reversed, bucketId, byId),
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry?.GetType().Name, "Unknown journal entry type."),
    };
}
