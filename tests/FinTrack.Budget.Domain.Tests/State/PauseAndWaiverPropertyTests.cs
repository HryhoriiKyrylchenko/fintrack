using CsCheck;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Plans;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Time;

namespace FinTrack.Budget.Domain.Tests.State;

/// <summary>
/// Property-based tests of pauses and waivers (plan 1b B10, B11): shift and skip pause the same months, a shift pause moves the end
/// by exactly the paused months and a skip pause keeps it, a waiver never creates a prepayment nor covers its own month, and the
/// past-month comparison (invariant 6) stays exact with pauses, each against an independent month-by-month walk.
/// </summary>
public class PauseAndWaiverPropertyTests
{
    private static readonly YearMonth Current = new(2026, 3);

    // A plan from up to 24 months back with 1 to 24 months, and up to three pauses that do not overlap (each placed after the one
    // before), the last one possibly without an end.
    private static readonly Gen<(int Start, int Months, (int Gap, int Length)[] Pauses, bool OpenLast)> Shapes =
        Gen.Select(Gen.Int[-24, 2], Gen.Int[1, 24], Gen.Select(Gen.Int[0, 6], Gen.Int[1, 5]).Array[0, 3], Gen.Bool);

    private static Plan Build((int Start, int Months, (int Gap, int Length)[] Pauses, bool OpenLast) shape, PauseMode mode, bool withEnd = true)
    {
        var from = Current.AddMonths(shape.Start);
        var pauses = new List<PlanPause>();
        var next = from.AddMonths(-2);
        for (var index = 0; index < shape.Pauses.Length; index++)
        {
            var (gap, length) = shape.Pauses[index];
            var pauseFrom = next.AddMonths(gap);
            var open = shape.OpenLast && index == shape.Pauses.Length - 1;
            YearMonth? to = open ? null : pauseFrom.AddMonths(length - 1);
            pauses.Add(new PlanPause(PauseId.From(Guid.CreateVersion7()), pauseFrom, to, mode));
            next = (to ?? pauseFrom).AddMonths(1);
        }

        return new Plan(PlanId.From(Guid.CreateVersion7()), BucketId.From(Guid.CreateVersion7()), from, withEnd ? from.AddMonths(shape.Months - 1) : null,
            new DateOnly(2024, 1, 1), [new PlanVersion(from, Pln(10m))], Pln(0m)) { Pauses = pauses };
    }

    // The months a plan accrues in, by walking month by month: a skip-paused month uses up one of the plan's months, a shift-paused
    // month uses up none, an open shift pause never ends; null when the walk would not end (no end, or an open shift pause).
    private static List<YearMonth>? AccruingMonths(Plan plan, int limit = 400)
    {
        if (plan.ValidTo is not { } end)
        {
            return null;
        }

        var left = YearMonth.MonthsBetween(plan.ValidFrom, end) + 1;
        var months = new List<YearMonth>();
        for (var month = plan.ValidFrom; left > 0; month = month.Next())
        {
            if (--limit == 0)
            {
                return null;
            }

            var pause = plan.Pauses.FirstOrDefault(candidate => month >= candidate.From && (candidate.To is null || month <= candidate.To));
            if (pause is { Mode: PauseMode.Shift })
            {
                continue;
            }

            left--;
            if (pause is null)
            {
                months.Add(month);
            }
        }

        return months;
    }

    [Fact]
    public void ShiftAndSkip_PauseTheSameMonths_WithinThePlansMonths() =>
        Shapes.Sample(shape =>
        {
            var shift = Build(shape, PauseMode.Shift);
            var skip = Build(shape, PauseMode.Skip);
            for (var month = shift.ValidFrom; month <= shift.ValidTo; month = month.Next())
            {
                if (PlanSchedule.Accrual(shift, month) != PlanSchedule.Accrual(skip, month))
                {
                    return false;
                }
            }

            return true;
        });

    [Fact]
    public void AShiftPause_MovesTheEndByThePausedMonths_AndASkipPauseKeepsIt() =>
        Shapes.Sample(shape =>
        {
            var shift = Build(shape, PauseMode.Shift);
            var skip = Build(shape, PauseMode.Skip);
            var walked = AccruingMonths(shift);
            var end = PlanSchedule.EffectiveEnd(shift);

            var shiftOk = walked is null
                ? end is null
                : end is { } last
                    && YearMonth.MonthsBetween(shift.ValidTo!.Value, last) == shift.Pauses.Sum(pause => CountWithin(pause, shift.ValidFrom, last))
                    && walked.Count == YearMonth.MonthsBetween(shift.ValidFrom, shift.ValidTo!.Value) + 1;

            return shiftOk && PlanSchedule.EffectiveEnd(skip) == skip.ValidTo;
        });

    [Fact]
    public void Accrual_IsTheAmountInExactlyTheWalkedMonths() =>
        Gen.Select(Shapes, Gen.Bool).Sample((shape, isShift) =>
        {
            var plan = Build(shape, isShift ? PauseMode.Shift : PauseMode.Skip);
            var walked = AccruingMonths(plan);
            if (walked is null)
            {
                return true;
            }

            var until = (walked.Count > 0 ? walked[^1] : plan.ValidTo!.Value).AddMonths(3);
            for (var month = plan.ValidFrom.AddMonths(-2); month <= until; month = month.Next())
            {
                var expected = walked.Contains(month) ? 10m : 0m;
                if (PlanSchedule.Accrual(plan, month).Amount != expected)
                {
                    return false;
                }
            }

            return true;
        });

    [Fact]
    public void AWaiver_NeverCreatesAPrepayment_NorCoversItsOwnMonth() =>
        Gen.Select(Shapes, Gen.Long[0, 300], Gen.Select(Gen.Int[-24, 0], Gen.Long[1, 300]).Array[0, 4], Gen.Int[0, 6])
            .Sample((shape, contributed, waivers, later) =>
            {
                var plan = Build(shape, PauseMode.Skip, withEnd: shape.Months % 2 == 0) with
                {
                    Contributed = Pln(contributed),
                    Waivers = [.. waivers.Select(waiver => new PlanWaiver(Current.AddMonths(waiver.Item1), Pln(waiver.Item2)))],
                };
                var current = Current.AddMonths(later);
                var standing = PlanSchedule.Standing(plan, current);
                var without = PlanSchedule.Standing(plan with { Waivers = [] }, current);

                // Never more than the months before the latest waiver lacked, never a prepayment, never more than written.
                var latest = plan.Waivers.Count == 0 ? plan.ValidFrom : plan.Waivers.Max(waiver => waiver.Month);
                var before = Enumerable.Range(0, Math.Max(0, YearMonth.MonthsBetween(plan.ValidFrom, latest)))
                    .Sum(offset => PlanSchedule.Accrual(plan, plan.ValidFrom.AddMonths(offset)).Amount);
                return standing.Prepaid == without.Prepaid
                    && standing.Waived.Amount <= Math.Max(0m, before - contributed)
                    && standing.Waived.Amount <= plan.Waivers.Sum(waiver => waiver.Amount.Amount)
                    && standing.DueNow.Amount == Math.Max(0m, without.DueNow.Amount - standing.Waived.Amount);
            });

    private enum Change
    {
        Pause,
        ChangePause,
        ReversePause,
        Waive,
    }

    [Fact]
    public void ThePastMonthComparison_StaysExactWithPauses() =>
        Gen.Select(Gen.Int[-12, 1], Gen.OneOf(Gen.Int[0, 18].Select(months => (int?)months), Gen.Const((int?)null)), Gen.Enum<Change>(), Gen.Int[-14, 6], Gen.Int[0, 4], Gen.Bool, Gen.Bool)
            .Sample((start, end, change, month, length, open, shift) =>
            {
                var journal = new PlanJournal(new DateOnly(2026, 3, 15));
                var from = Current.AddMonths(start);
                var plan = journal.Create(journal.Cushion, 3m, from, end is { } months ? from.AddMonths(months) : null, day: -300);
                var pause = journal.Pause(plan, Current.AddMonths(-6), Current.AddMonths(-5), shift ? PauseMode.Shift : PauseMode.Skip, day: -200);
                var existing = new List<JournalEntry> { plan, pause };
                var mode = shift ? PauseMode.Skip : PauseMode.Shift;
                YearMonth? to = open ? null : Current.AddMonths(month + length);

                JournalEntry added = change switch
                {
                    Change.Pause => journal.Pause(plan, Current.AddMonths(month), to, mode),
                    Change.ChangePause => journal.ChangePause(pause, Current.AddMonths(month), to, mode),
                    Change.ReversePause => journal.Reverse(pause),
                    _ => journal.Waive(plan, 1m),
                };

                var found = journal.Replay([.. existing]).FirstPastPeriodChange(journal.Replay([.. existing, added]));
                return found == IndependentFirstChange(plan, existing, [.. existing, added]);
            });

    private static int CountWithin(PlanPause pause, YearMonth from, YearMonth to)
    {
        var count = 0;
        for (var month = from; month <= to; month = month.Next())
        {
            count += pause.Covers(month) ? 1 : 0;
        }

        return count;
    }

    // The accruing months of the plan in a journal, rebuilt from the effective entries (pauses with their latest change), then compared
    // month by month before the current month.
    private static YearMonth? IndependentFirstChange(PlanCreated plan, IReadOnlyList<JournalEntry> before, IReadOnlyList<JournalEntry> after)
    {
        var was = Accruing(plan, before);
        var now = Accruing(plan, after);
        for (var month = plan.ValidFrom; month < Current; month = month.Next())
        {
            if (was(month) != now(month))
            {
                return month;
            }
        }

        return null;
    }

    private static Func<YearMonth, bool> Accruing(PlanCreated created, IReadOnlyList<JournalEntry> journal)
    {
        var reversed = journal.OfType<Reversal>().Select(reversal => reversal.ReversedEntryId.Value).ToHashSet();
        var effective = journal.Where(entry => entry is not Reversal && !reversed.Contains(entry.Id)).ToList();
        var pauses = effective.OfType<PlanPaused>().Select(paused =>
        {
            var latest = effective.OfType<PlanPauseChanged>().LastOrDefault(change => change.PauseId == paused.PauseId);
            return latest is null
                ? new PlanPause(paused.PauseId, paused.From, paused.To, paused.Mode)
                : new PlanPause(paused.PauseId, latest.From, latest.To, latest.Mode);
        }).ToList();
        var plan = new Plan(created.PlanId, created.BucketId, created.ValidFrom, created.ValidTo, created.OccurredOn, [], Pln(0m)) { Pauses = pauses };
        var walked = AccruingMonths(plan);
        return month => month >= created.ValidFrom
            && (walked is null
                ? !pauses.Any(pause => pause.Covers(month)) && (plan.ValidTo is null || PlanSchedule.EffectiveEnd(plan) is null)
                : walked.Contains(month));
    }

    private static Money Pln(decimal amount) => Money.From(amount, Currency.Pln);
}
