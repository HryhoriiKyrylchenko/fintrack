namespace FinTrack.Budget.Domain;

/// <summary>
/// Error codes of the Budget context. A public contract: the UI maps each one to a text in its
/// resource files. Never rename or reuse a code; add a new one instead.
/// </summary>
public static class BudgetErrorCodes
{
    /// <summary>The owner has not saved settings yet (first run). No parameters.</summary>
    public const string SettingsNotFound = "settings.not_found";

    /// <summary>The time zone is not an IANA time zone ID in its exact form, for example <c>Europe/Warsaw</c>. No parameters.</summary>
    public const string SettingsTimeZoneUnknown = "settings.time_zone_unknown";

    /// <summary>The locale is not the exact name of a predefined culture, for example <c>pl-PL</c>. No parameters.</summary>
    public const string SettingsLocaleUnknown = "settings.locale_unknown";

    /// <summary>No bucket with this ID exists for the owner. No parameters.</summary>
    public const string BucketNotFound = "bucket.not_found";

    /// <summary>The bucket is archived and accepts no change or new entry. No parameters.</summary>
    public const string BucketArchived = "bucket.archived";

    /// <summary>An active bucket of the owner already has this name (compared without regard to case). No parameters.</summary>
    public const string BucketNameTaken = "bucket.name_taken";

    /// <summary>A bucket with this ID exists with a different definition (repeated create, invariant 13). No parameters.</summary>
    public const string BucketIdReused = "bucket.id_reused";

    /// <summary>The bucket is a system bucket (<c>Unallocated</c>) and cannot be renamed or archived. No parameters.</summary>
    public const string BucketSystemBucket = "bucket.system_bucket";

    /// <summary>
    /// The entry would take a savings bucket below zero at some point of the replay order (invariant 1,
    /// D2). Parameter <c>date</c>: the first such day (<c>yyyy-MM-dd</c>); never an amount.
    /// </summary>
    public const string BucketInsufficientFunds = "bucket.insufficient_funds";

    /// <summary>Money cannot be spent from an investment bucket (D11, ARCHITECTURE 4.9). No parameters.</summary>
    public const string BucketSpendNotAllowed = "bucket.spend_not_allowed";

    /// <summary>The bucket already has an opening balance that is not reversed (D13). No parameters.</summary>
    public const string BucketOpeningBalanceExists = "bucket.opening_balance_exists";

    /// <summary>
    /// The bucket's <c>NetContributed</c> is not zero, so it cannot be archived (plan 1b B17: <c>Available</c> alone would
    /// miss a lender whose money is all lent out). No parameters.
    /// </summary>
    public const string BucketNotEmpty = "bucket.not_empty";

    /// <summary>The bucket is the lender of an open loan and cannot be archived (plan 1b B17); checked before <see cref="BucketNotEmpty"/>. No parameters.</summary>
    public const string BucketHasOpenLoan = "bucket.has_open_loan";

    /// <summary>
    /// The bucket has an active plan and cannot be archived (plan 1b B17): a plan that still accrues in the current month
    /// or later, or that has money due. End the plan, then pay or waive its shortfall. Checked after
    /// <see cref="BucketHasOpenLoan"/> and before <see cref="BucketNotEmpty"/>. No parameters.
    /// </summary>
    public const string BucketHasActivePlan = "bucket.has_active_plan";

    /// <summary>No plan with this ID exists for the owner: no effective <c>PlanCreated</c> has it (plan 1b B7). No parameters.</summary>
    public const string PlanNotFound = "plan.not_found";

    /// <summary>
    /// The change, or the reversal, would alter what a plan accrued in a month before the current month (invariant 6,
    /// plan 1b B8). Parameter <c>month</c>: the first such month (<c>yyyy-MM</c>); never an amount.
    /// </summary>
    public const string PlanPastPeriodChanged = "plan.past_period_changed";

    /// <summary>The plan's last accruing month has passed, so its amount can no longer change (plan 1b B7). No parameters.</summary>
    public const string PlanEnded = "plan.ended";

    /// <summary>A contribution names a plan of another bucket than the one receiving the money (plan 1b B9). No parameters.</summary>
    public const string PlanBucketMismatch = "plan.bucket_mismatch";

    /// <summary>
    /// The months do not fit: an end before the start, or a new amount from a month outside the plan's months (plan 1b B7).
    /// No parameters.
    /// </summary>
    public const string PlanMonthsInvalid = "plan.months_invalid";

    /// <summary>No pause with this ID exists for the plan: no effective <c>PlanPaused</c> of it has it (plan 1b B10). No parameters.</summary>
    public const string PlanPauseNotFound = "plan.pause_not_found";

    /// <summary>The pause would overlap another pause of the same plan (plan 1b B10). No parameters.</summary>
    public const string PlanPauseOverlaps = "plan.pause_overlaps";

    /// <summary>
    /// The waiver is above the plan's shortfall at its date: what the months before the waiver's month still lack after the
    /// contributions and earlier waivers (plan 1b B11). The current month's due is not a shortfall yet. No parameters.
    /// </summary>
    public const string PlanWaiverExceedsShortfall = "plan.waiver_exceeds_shortfall";

    /// <summary>Money can be borrowed from a savings bucket only, never from an investment bucket (ARCHITECTURE 4.9, plan 1b B2). No parameters.</summary>
    public const string BucketBorrowNotAllowed = "bucket.borrow_not_allowed";

    /// <summary>A borrow's destination is its lender (plan 1b B2). No parameters.</summary>
    public const string BorrowSameBucket = "borrow.same_bucket";

    /// <summary>No loan with this ID exists for the owner: no effective borrow has it (plan 1b B2). No parameters.</summary>
    public const string LoanNotFound = "loan.not_found";

    /// <summary>The loan is fully repaid; nothing is outstanding (plan 1b B2). No parameters.</summary>
    public const string LoanClosed = "loan.closed";

    /// <summary>
    /// A repayment would take a loan's outstanding principal below zero at some point of the replay order (invariant 4,
    /// plan 1b B3, B4), also when it is dated before its borrow. Parameter <c>date</c>: the first such day
    /// (<c>yyyy-MM-dd</c>); never an amount.
    /// </summary>
    public const string LoanRepaymentExceedsOutstanding = "loan.repayment_exceeds_outstanding";

    /// <summary>A repayment is paid from its own lender (plan 1b B2). No parameters.</summary>
    public const string RepaymentFromLender = "repayment.from_lender";

    /// <summary>No entry with this ID exists for the owner. No parameters.</summary>
    public const string EntryNotFound = "entry.not_found";

    /// <summary>The entry has been reversed already; an entry is reversed at most once (D3). No parameters.</summary>
    public const string EntryAlreadyReversed = "entry.already_reversed";

    /// <summary>The entry is a reversal, which cannot be reversed (D3): enter a new entry instead. No parameters.</summary>
    public const string EntryNotReversible = "entry.not_reversible";

    /// <summary>
    /// Effective entries still refer to the entry, so it cannot be reversed (plan 1b B5: a borrow with repayments, a plan
    /// with changes or contributions, a pause with changes); reverse those first. No parameters.
    /// </summary>
    public const string EntryHasDependents = "entry.has_dependents";

    /// <summary>An amount is not in the bucket's currency. No parameters.</summary>
    public const string BucketCurrencyMismatch = "bucket.currency_mismatch";

    /// <summary>A transfer's source and destination are the same bucket (D6). No parameters.</summary>
    public const string TransferSameBucket = "transfer.same_bucket";

    /// <summary>A transfer's fee is not below the amount that leaves the source (D6). No parameters.</summary>
    public const string TransferFeeExceedsAmount = "transfer.fee_exceeds_amount";

    /// <summary>In one currency, what arrives is not exactly what leaves minus the fee (D6). No parameters.</summary>
    public const string TransferAmountsInconsistent = "transfer.amounts_inconsistent";

    /// <summary>
    /// An entry with this ID exists with different content (repeated command, invariant 13); for a command that writes
    /// several entries, the stored group differs from the command's entries in any way (plan 1b B15). No parameters.
    /// </summary>
    public const string EntryIdReused = "entry.id_reused";

    /// <summary>An entry is dated after today in the owner's time zone (plan 1a D12). No parameters.</summary>
    public const string EntryDateInFuture = "entry.date_in_future";
}
