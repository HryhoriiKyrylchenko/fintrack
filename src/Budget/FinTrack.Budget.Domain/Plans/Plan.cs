using FinTrack.Budget.Domain.Buckets;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;

namespace FinTrack.Budget.Domain.Plans;

/// <summary>
/// A contribution plan (ARCHITECTURE 4.5, plan 1b B7, B10, B11), derived by the state builder from its <c>PlanCreated</c>, its
/// effective changes, pauses and waivers and the contributions linked to it. Never stored. Every amount is in the bucket's currency.
/// What it accrues and owes is computed by <see cref="PlanSchedule"/>.
/// </summary>
/// <param name="PlanId">The plan: its <c>PlanCreated</c> entry's ID.</param>
/// <param name="BucketId">The bucket it saves into.</param>
/// <param name="ValidFrom">The first month that accrues.</param>
/// <param name="ValidTo">The last month as defined, or <c>null</c> without an end (the latest <c>PlanEndChanged</c> wins); shift pauses move the effective end (<see cref="PlanSchedule.EffectiveEnd"/>).</param>
/// <param name="CreatedOn">The business date of its <c>PlanCreated</c>.</param>
/// <param name="Versions">Its amounts, in replay order: the first from <paramref name="ValidFrom"/>, then each <c>PlanAmountChanged</c>.</param>
/// <param name="Contributed">The sum of the effective deposits and transfers in linked to it (plan 1b B9).</param>
public sealed record Plan(
    PlanId PlanId,
    BucketId BucketId,
    YearMonth ValidFrom,
    YearMonth? ValidTo,
    DateOnly CreatedOn,
    IReadOnlyList<PlanVersion> Versions,
    Money Contributed)
{
    /// <summary>Its pauses with their current values (the latest change of each wins), in the replay order of their start (B10).</summary>
    public IReadOnlyList<PlanPause> Pauses { get; init; } = [];

    /// <summary>Its waivers as written, in replay order (B11); <see cref="PlanSchedule"/> decides how much of each is effective.</summary>
    public IReadOnlyList<PlanWaiver> Waivers { get; init; } = [];

    /// <summary>The plan's currency: its bucket's.</summary>
    public Currency Currency => Contributed.Currency;

    /// <summary>
    /// The amount of the version in force in <paramref name="month"/>: the one with the latest <c>from</c> at or before it,
    /// the later in replay order on a tie; zero before every version. Whether the month accrues at all is
    /// <see cref="PlanSchedule.Accrual"/>.
    /// </summary>
    public Money AmountIn(YearMonth month)
    {
        PlanVersion? inForce = null;
        foreach (var version in Versions)
        {
            if (version.From <= month && (inForce is null || version.From >= inForce.From))
            {
                inForce = version;
            }
        }

        return inForce?.Amount ?? Money.Zero(Currency);
    }

    /// <summary>The pause that covers <paramref name="month"/>, or <c>null</c>. Pauses of one plan never overlap (B10).</summary>
    public PlanPause? PauseIn(YearMonth month) => Pauses.FirstOrDefault(pause => pause.Covers(month));
}

/// <summary>One version of a plan's monthly amount.</summary>
/// <param name="From">Its first month.</param>
/// <param name="Amount">The monthly amount, above zero.</param>
public sealed record PlanVersion(YearMonth From, Money Amount);

/// <summary>A pause of a plan with its current values (plan 1b B10).</summary>
/// <param name="PauseId">The pause: its <c>PlanPaused</c> entry's ID.</param>
/// <param name="From">The first paused month.</param>
/// <param name="To">The last paused month, or <c>null</c> while it lasts until changed.</param>
/// <param name="Mode">Shift (the end moves) or skip (the end stays).</param>
public sealed record PlanPause(PauseId PauseId, YearMonth From, YearMonth? To, PauseMode Mode)
{
    /// <summary>Whether <paramref name="month"/> is paused by this pause.</summary>
    public bool Covers(YearMonth month) => month >= From && !(month > To);

    /// <summary>Whether this pause and <paramref name="other"/> share a month.</summary>
    public bool Overlaps(PlanPause other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return !(To < other.From) && !(other.To < From);
    }
}

/// <summary>A waiver of a plan's shortfall as written (plan 1b B11).</summary>
/// <param name="Month">The month of its date: it settles only months before this one.</param>
/// <param name="Amount">The amount written, above zero.</param>
public sealed record PlanWaiver(YearMonth Month, Money Amount);
