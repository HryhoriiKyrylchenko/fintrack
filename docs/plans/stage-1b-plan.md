# FinTrack Phase 1b (Budget core): implementation plan

## Context

Phase 1a is complete and merged (step 18: PR #37, `c9a5634`): buckets, `Unallocated`, `OpeningBalance`, `Deposit`, `Spend`, `Transfer`, `Reversal`, multi-currency balances, the overview, the journal and the Web screens for all of them. A test audit followed on `main` (PR #38 `b0b6a1b` and PR #39 `9d5ecee`, `docs/plans/test-audit.md`): no flaky test in 20 runs, mutation testing with Stryker.NET on the kernel, Budget.Domain, Budget.Application and both Infrastructure projects, 1466 tests (one explicit test skipped). One audit finding matters directly here: `Available = NetContributed − OutstandingPrincipal` is now pinned by a test, because the principal was always 0 in 1a.

Phase 1b turns the first usable version into the Budget core (ARCHITECTURE 8): plans with carry-over, plan pause (shift and skip), limits, loans without interest, `Adjustment`, manual allocation, summaries. The owner already uses 1a next to the spreadsheet, so every step keeps the app usable and the stored format readable: new fields are optional (`MapOptional`), new entry types are additions, nothing stored by 1a changes.

Status: approved 19 Nov 2026, with every decision as written: B1 chosen by the developer (option A), B2 to B22 as recommended, B23 (unarchiving) added at the developer's request; including the spec changes B1, B6, B9, B12 and B23 and the new error codes. No new package. Tick a step's `[ ]` when its pull request is merged.

## Phase 1b scope

- **In:**
  - Loans: `Borrow` and `Repayment` entries, loans derived by replay (open, closed), a versioned repayment plan, `OutstandingPrincipal` above zero, invariant 4.
  - Contribution plans: versions, end date, carry-over of shortfalls, oldest-period-first settlement, prepayment, deposits (and transfers, B9) linked to a plan, waiving a shortfall with a reason.
  - Pauses: `Shift` and `Skip`, open-ended or with an end month, changed later by a new entry.
  - Bucket limit: set, change, remove; checked against `NetContributed`; a deposit above it offers to raise it; plans stop accruing at it.
  - `Adjustment` with a mandatory comment.
  - Manual allocation: a pure engine, a preview, and one command that writes several entries in one commit with one `GroupId`.
  - Archiving rules of invariant 8 (open loan, active plan), and unarchiving a bucket (B23).
  - Read contracts: plans, loans, an allocation preview; overview card extras and "needs attention"; the journal with every new type.
  - Web: Plans and Loans screens (DESIGN 8), forms for borrow, repay, adjust, limit, plan changes, pause, waive (DESIGN 9 confirmation) and allocate (with a summary step), Overview card extras and "needs attention", the Journal with the new types.
- **Out (phase 2):** bulk pause (`BatchId`), proportional loan repayment, goals with a date, automatic and semi-automatic allocation, `Priority`, `RemainderPolicy`, accounts and reconciliation, reports and charts (net worth over time, plan versus actual, monthly report), period snapshots, stored projections.
- **Out (later):** loan interest (`LoanInterest`, `InterestDue`), notifications, cross-currency loans (B2), reversing a whole group in one action.

## Decisions taken

Approved by the developer on 19 Nov 2026. Numbered **B1 to B23** so they cannot be confused with phase 1a's D1 to D17. Each keeps its reason and the alternative that was rejected; the ones that change the spec are marked **(changes the spec)** and appear under "Contradictions". All are recorded in `docs/ARCHITECTURE.md` (sections 2, 4, 7 and the decision log).

**B1. Where plan, pause, limit and repayment-plan definitions live. (changes the spec) Decided by the developer on 19 Nov 2026: option A.**
- *Not in question:* the limit itself. Every bucket can have a limit, shown on its card with the progress to it, and the owner can set, raise, lower or remove it at any time (ARCHITECTURE 4.6). B1 decides only how a change is recorded.
- *Problem:* ARCHITECTURE 4.5 describes `ContributionPlan` as an entity with versions, 2.2 says aggregates are stored as documents, and 4.1 makes `limit` a bucket field. But 2.3 says "plan status ... computed from the journal", 4.5 says pauses are "changed later by a new event", 4.6 says confirming a raise "writes `LimitRaised`", and invariant 6 needs the history of every definition: a period accrues by the plan's amount *and* by the limit in force at that time (B12), so a limit kept as one current value would change past periods when it is raised.
- *Option A (chosen): definitions are journal entries.* `PlanCreated`, `PlanAmountChanged`, `PlanEndChanged`, `PlanPaused`, `PlanPauseChanged`, `ShortfallWaived`, `LimitChanged`, `RepaymentPlanChanged`. They move no money (like `Reversal`); the state builder replays them with the money entries. A plan's ID is the ID of its `PlanCreated` entry, as a loan's is its `Borrow` (B2).
- *Option B (rejected):* mutable documents (`budget_plans` with lists of versions and pauses, a limit history in the bucket document, a repayment plan document), committed under the ledger version like buckets.
- *Why A:* (1) append-only by construction: `AppendOnlyCollection` has no update, while a version list in a mutable document is protected only by the domain methods that append to it; (2) one state builder over one input (buckets + journal), as rule 6 and ARCHITECTURE 2.3 say; (3) every change carries `OccurredOn` and `RecordedAt`, is visible in the Journal and can later feed the monthly report ("shortfalls carried over"); (4) correcting a definition reuses `Reversal` with the same invariant checks (B8) instead of new delete commands; (5) bulk pause in phase 2 becomes several `PlanPaused` entries with one `GroupId`, which 4.5 already describes as a shared `BatchId`; (6) no new collection, mapping pattern or index.
- *Cost of A:* more entry types (eleven new ones in total with `Borrow`, `Repayment`, `Adjustment`), a longer flat journal DTO (B18), and the Journal screen shows non-money entries (filterable by kind in the UI).
- *Consequence:* the limit is not stored as a field of the bucket document (ARCHITECTURE 4.1); its history is replayed from dated `LimitChanged` entries, and the current limit reaches the UI through the overview (`limit`, `limitReached`, B19). Contradiction 1.

**B2. Loans: `Borrow` and `Repayment`; a loan is derived, never stored.**
- `Borrow`: the common fields, then `lenderBucketId`, `principal` (Money, above zero) and `toBucketId` (optional: absent means the money went outside the app). Type name `Borrow`. The loan's ID (`LoanId`) is the `Borrow` entry's ID.
- `Repayment`: the common fields, then `loanId`, `amount` (above zero) and `fromBucketId` (optional: absent means new money from outside). Type name `Repayment`. The money goes to the loan's lender.
- *Why derived:* a stored loan with a status would be a second truth that every repayment, reversal and backdated entry must keep in step (a stored projection, rule 6). Replay gives `Principal`, `Repaid`, `Outstanding`, `Status` (`Open`, `Closed` when outstanding is zero) and `ClosedOn`. 4.4 calls it an entity with a status: it is a Domain entity built by `BudgetState`, not a document. A reversed repayment reopens the loan by itself.
- Rules: the lender is an active savings bucket of the user (investment: `bucket.borrow_not_allowed`, 4.9; `Unallocated`: `bucket.system_bucket`); the destination (bucket or `Unallocated` of the currency) and the repayment source are active and in the lender's currency (`bucket.currency_mismatch`); destination ≠ lender (`borrow.same_bucket`, 400); source ≠ lender (`repayment.from_lender`, 422); repaying a closed loan `loan.closed`; an unknown loan `loan.not_found`.
- *Same currency only in 1b:* a cross-currency loan needs two amounts and a rate like a transfer; borrow into a bucket of the lender's currency and transfer from there. Deferred, not dropped.

**B3. Balances with loans (invariants 1, 2, 4).**
- Lender: `OutstandingPrincipal += principal`, `NetContributed` unchanged, so `Available` falls by the principal. A repayment lowers `OutstandingPrincipal`; `NetContributed` never changes, so a returnable loan does not free space under the limit (4.6) and a repayment is never income (`Deposited` unchanged, invariant 5).
- Destination bucket: `NetContributed += principal`, like a transfer in. Repayment source bucket: `NetContributed −= amount`, like a transfer out.
- Totals (`Available` per currency): borrowing into a bucket leaves the total unchanged; borrowing to the outside lowers it (the money is gone; a debt to yourself is not an asset); a repayment from outside raises it.
- Checked at every point of the replay order, by the replay that includes the new entry (as D2): invariant 1 for every savings bucket (so a loan never exceeds the lender's `Available` at its date), and **invariant 4: a loan's outstanding principal never goes below zero** (`loan.repayment_exceeds_outstanding`, parameter `date`). A repayment dated before its borrow, or reversing a borrow that has repayments, fails by the same rule (B5 gives the clearer code for the second).

**B4. A repayment above the outstanding principal.**
- 4.4 says the excess "goes to remainder handling". A single `AddRepayment` has no remainder: it is rejected (`loan.repayment_exceeds_outstanding`), and the form shows the outstanding amount and links to the allocation flow (B16), which caps the loan line and asks where the remainder goes.
- *Alternative:* silently split the excess into a deposit: rejected, it writes an entry the owner did not confirm (DESIGN 1.6).

**B5. Reversal of an entry that others depend on.**
- New rule: an entry cannot be reversed while an effective (not reversed) entry refers to it: a `Borrow` with repayments or a repayment plan, a `PlanCreated` with changes, pauses, waivers or linked contributions, a `PlanPaused` with changes (`entry.has_dependents`, 422). Reverse the later entries first, as 1a already asks for spends.
- Reversals stay subject to every invariant check (1, 4, limit, 6). Reversing a `Repayment` raises the outstanding principal (and gives the source its money back); reversing a definition entry is allowed only when it leaves past periods unchanged (B8).
- *Why:* the generic replay check would also catch most cases, but with a code that names the symptom (`loan.repayment_exceeds_outstanding`) instead of the cause; and a repayment of a reversed loan has no meaning at all.

**B6. Months. (changes the spec: a new value format)**
- Periods are calendar months. A month is a kernel value type `YearMonth` (year 1 to 9999, month 1 to 12), stored and sent as `"yyyy-MM"` (`"2026-11"`), read strictly like `DateOnly` (one spelling, no day, no other separator).
- The month of an entry is the month of its `OccurredOn`, which is already a business date in the owner's time zone, so Domain needs no time zone. The current month is the month of `OwnerSettings.Today(now)`, computed in Application as today is.
- *Alternative:* a `DateOnly` that must be the 1st: no new format, but a second spelling of every month and a rule every reader must remember. Rejected.

**B7. Contribution plans: periods and dues.**
- `PlanCreated`: `bucketId`, `amount` (monthly, in the bucket's currency), `validFrom` (month), `validTo` (optional month, the last month that accrues). `PlanAmountChanged`: `planId`, `amount`, `from` (month): a new version; the latest version with `from` ≤ a month gives that month's amount. `PlanEndChanged`: `planId`, `validTo` (optional; absent removes the end).
- A period accrues on the first day of its month (the current month is due during the month). Accrual of month M: the version's amount, unless M is paused (B10) or capped by the limit (B12).
- `Contributed` = linked deposits and transfers in (B9). `Credit` = `Contributed` + effective waivers (B11). `DueNow` = max(0, accrued through the current month − credit). `Shortfall` = the part of `DueNow` from months before the current month (oldest first: credit settles the oldest accrued month first, so the current month is the last to be paid). `Prepaid` = max(0, contributions − accrued): it settles future months as they accrue (4.5). One-time deposits (no `planId`) never count.
- Example (4.5): plan 500, month 1 paid 300 → in month 2 `DueNow` 700, `Shortfall` 200.
- The end month stops new periods, not the obligation: an ended plan keeps its shortfall until paid or waived.
- A plan may start in the past (catching up from the spreadsheet: no period exists yet to change) or in a future month. Plans on `Unallocated` are rejected (`bucket.system_bucket`); plans on investment buckets work on `NetContributed` (= net invested, 4.9).
- Status (derived): `Active`, `Paused` (the current month is paused), `LimitReached` (the current month accrued nothing because of the limit), `Ended` (the last accruing month has passed). `DueNow` and `Shortfall` are separate fields, so `Ended` with a shortfall is visible.

**B8. Invariant 6, enforced generically.**
- Every definition entry (B1) and every reversal of one is checked by comparing the accrual of every plan in **every month before the current month** without and with the new entry; any difference fails with `plan.past_period_changed` (parameter `month`, the first changed one). The current month and later may change.
- This covers a version starting in the past, a pause added or shortened in the past, an end month moved into or out of the past, a limit change that would alter an earlier month's cap, and the reversal of any of them. A plan created by mistake with a past start is ended and its shortfall waived, with a note.
- Money entries are not held to it: a backdated deposit can change an earlier month's limit cap (B12) because it corrects what the bucket held; invariant 6 is about changing a plan. ARCHITECTURE 7 is worded to say so.
- *Why generic:* one check, property-tested once, instead of a rule per kind of change that each could miss a case.

**B9. Contributions linked to a plan. (changes the spec)**
- `Deposit` gains the optional `planId` (deposit remarks already foresee it); **`Transfer` gains it too**: the owner's usual flow is salary into `Unallocated`, then distribution by transfer, and 4.5 counts only deposits. A transfer linked to a plan still is not income (invariant 5). The plan's bucket must be the receiving bucket (`plan.bucket_mismatch`); a reversed or unknown plan is `plan.not_found`. Optional field, `MapOptional`: stored 1a entries read unchanged, no upcaster.
- *Alternative:* deposits only, as written: then money already in `Unallocated` could never pay a plan.

**B10. Pauses.**
- `PlanPaused`: `planId`, `from` (month), `to` (optional month, inclusive; absent = until changed), `mode` (`Shift` or `Skip`). The pause's ID is the entry's ID. `PlanPauseChanged`: `pauseId`, `from`, `to`, `mode`, replacing the pause's values (the latest change wins; earlier ones stay in the journal). Resuming is a change that sets `to`.
- In both modes a paused month accrues nothing. `Shift` moves the end month later by the number of paused months inside the plan's span; `Skip` keeps it. A plan without an end month behaves the same in both modes. While a `Shift` pause has no `to`, the effective end is unknown (`null` in the DTO).
- Pauses of one plan may not overlap (`plan.pause_overlaps`); `to` not before `from` (`plan.months_invalid`, 400). Every change passes B8, so a pause can start or end in the current month or later, never rewrite an earlier month. One-time deposits are never blocked (4.5).

**B11. Waiving a shortfall.**
- `ShortfallWaived`: `planId`, `amount`, a **mandatory** `note` (the reason; the common note field, required for this type). It moves no money.
- The amount may not exceed the plan's `Shortfall` at the waiver's date (`plan.waiver_exceeds_shortfall`); the current month's due is not a shortfall yet. It settles the oldest months like a payment. If a later backdated contribution covers the same months, the part of a waiver that is no longer needed is void: **a waiver never creates a prepayment.**
- Reversible (the shortfall returns). DESIGN 9: the UI confirms with a dialog naming the plan and the amount.

**B12. Bucket limit. (changes the spec)**
- `LimitChanged`: `bucketId`, `limit` (optional Money above zero, in the bucket's currency; absent removes the limit). Replaces 4.6's `LimitRaised`: one type sets, raises, lowers and removes. Not on `Unallocated` (`bucket.system_bucket`). Applies to savings and investment buckets (4.6, 4.9), always against `NetContributed`.
- **Entries that raise `NetContributed` into a limited bucket** (`Deposit`, `Transfer` in, `Borrow` into it) are rejected when `NetContributed` after them exceeds the limit in force at their date (`bucket.limit_exceeded`, parameter `date`). Exempt: `OpeningBalance` and `Adjustment` (they record what is, not a contribution), `Reversal` (it restores history), and repayments (they never raise `NetContributed`), which is 4.6's "at the limit, payments can only repay loans". Lowering a limit below `NetContributed` is allowed: the goal counts as reached.
- **"Raise the limit to X?" (4.6):** `AddDeposit` and `AllocatePayment` accept an optional `raiseLimit { id, limit }`; when set, a `LimitChanged` dated like the deposit is written in the same commit, both entries sharing one `GroupId` (B15). Declining is not sending. Other entries above the limit get the error and the UI offers to change the limit first.
- **Plans stop accruing at the limit:** month M of a plan accrues `min(amount, room)`, where `room` = max(0, limit in force on M's first day − `NetContributed` at the end of the previous month), shared by the bucket's plans in the order they were created. No limit, no cap. This keeps a plan from asking for money the limit would refuse; a carried shortfall can still exceed the room, and paying it then offers the raise.
- *Why history matters:* raising a limit must not revive months in which the bucket was full; with the limit replayed from dated entries, each month uses the limit of its own first day, and B8 rejects a change that would alter an earlier month.

**B13. Repayment plan.**
- `RepaymentPlanChanged`: `loanId`, `amount` (monthly), `from` (month): a new version (4.4: "monthly amount, versioned"). Not on a closed loan (`loan.closed`).
- Expected repaid by the current month = min(principal, sum of the monthly amounts from the first version's `from` through the current month); `Behind` = max(0, expected − repaid). No pause, waiver or carry-over of its own: the principal is the obligation, the plan only says how fast. Stopping a schedule is the reversal of its entries (B8 decides whether that is allowed).

**B14. `Adjustment`.**
- One type `Adjustment` with `bucketId`, `amount` (above zero, D13) and `direction` (`Increase` or `Decrease`, stored by name): the type gives the direction of every other entry, here one enum gives it (4.2: one type). A signed amount was rejected (D13); two types (`AdjustmentUp`, `AdjustmentDown`) would double the rules for one concept.
- The mandatory comment is the common `note`, required for this type (`validation.failed`, field `note`, rule `NotEmpty`). It changes `NetContributed`, is never income, is tracked separately in the state (`AdjustedIn`, `AdjustedOut`) so phase 2 reports can show it separately, is exempt from the limit (B12), and a decrease is held to invariant 1. Allowed on any active bucket, `Unallocated` and investment buckets included (reconciliation in phase 2 will need both).

**B15. Several entries in one commit; `GroupId`.**
- `JournalWriter` writes one or more entries per command (a draft becomes a list). Every entry still has its own client-generated version 7 ID; a command that writes several also carries a client-generated `groupId`, set on each of its entries. IDs derived from one group ID were rejected: rule 9 wants client IDs to be UUIDv7, and a derived ID would be a second kind.
- Repeat (invariant 13): if any of the command's entry IDs exists, every one must exist with the same content and the same group, and the command writes nothing; otherwise `entry.id_reused`. Checked first, as today.
- Each entry of a group can be reversed on its own (4.2: a reversal cancels exactly one entry); reversing a whole group in one action is deferred.

**B16. Manual allocation (4.7).**
- **Engine (Domain, pure):** input = the payment (Money), its source (from outside, or from a bucket such as `Unallocated`), lines (each a plan, a loan or a bucket for a one-time contribution, with an amount) and the state (plan dues, outstanding principals, room under limits). Output = the proposed lines, each capped (plan line at `DueNow`, loan line at outstanding principal, any line at its bucket's room; lines to one bucket share the room, in the given order), and the remainder. Deterministic; the lines plus the remainder sum exactly to the payment (invariant 9).
- **Preview:** `PreviewAllocation` is a read query (`POST /v1/allocations/preview`, a body but no write) returning the capped lines, the remainder and why each line was capped. The Web app never computes it (1a decision: the page shows only the Api's figures).
- **Remainder:** the owner chooses where it goes (4.7): a one-time contribution to a bucket, a loan repayment, a plan prepayment, or `Unallocated` of the currency; with `Unallocated` as source, leaving it there is "no line".
- **Command:** `AllocatePayment` carries the final lines (each with its own entry ID) and the `groupId`. From outside: plan and bucket lines become `Deposit`s (plan lines with `planId`), loan lines `Repayment`s without a source, the remainder to `Unallocated` a `Deposit` there. From a bucket: `Transfer`s (plan lines with `planId`) and `Repayment`s from that bucket. The command enforces only hard rules: the sum equals the payment (`allocation.sum_mismatch`, 400), no two lines with the same target (`allocation.duplicate_line`, 400), one currency, and every entry's own checks by replay (invariants 1, 4, limit). A plan line above `DueNow` is a prepayment and allowed; the preview's caps are proposals.
- *Why a command with explicit lines:* the confirmed summary is exactly what is written; a re-run of the engine at commit time could write something the owner did not see.

**B17. Archiving (invariant 8).**
- A bucket can be archived only when it is the lender of no open loan (`bucket.has_open_loan`), has no active plan (`bucket.has_active_plan`) and its `NetContributed` is zero (`bucket.not_empty`). Checked in this order. 1a checks `Available`, which is wrong once loans exist: a lender whose money is all lent out has `Available` 0 (contradiction 6).
- An active plan: one whose last accruing month is the current month or later (or that has no end), or that has `DueNow` above zero. To archive: end the plan, then pay or waive its shortfall.
- *Why separate codes:* 4.1 names one code (`bucket.not_empty`) for three causes; the UI must say which one and what to do next (DESIGN 9). Contradiction 4.

**B18. How V1 contracts grow.**
- *Rule:* within V1, changes are **additive**: new response properties, new enum members (`JournalEntryType`, new kinds), new request properties with a default (strict reading makes a property optional only through its default), new queries. Renaming or removing a property, changing its meaning or making it required is a V2. Every field stays documented.
- Strict reading rejects unknown properties, so an older Web app would fail on a newer Api; that is acceptable because they ship together (decision log), and the pinned JSON tests make every change deliberate. Other contexts and, in phase 7, agent tools use the query types in-process, where an added property breaks nothing. The first consumer that does not ship with the Api (a split deployment, an external client) freezes V1; from then on additions go to V2.
- `JournalEntryDto` stays one flat shape (1a decision) and gains: `planId`, `loanId`, `pauseId`, `direction`, `fromMonth`, `toMonth`, `pauseMode`, `limit`. Existing fields are reused where the meaning fits, documented per type (`bucketId` and `amount` for `Adjustment`, plan and limit entries; `fromBucketId` lender and `toBucketId` destination for `Borrow`; `fromBucketId` source and `toBucketId` lender for `Repayment`). Twelve types make it long; the alternative (polymorphic JSON) was rejected in 1a and is not reopened.

**B19. Read contracts.**
- `GetLoans(includeClosed)` → `LoanDto`: `id`, `lenderBucketId`, `toBucketId` (or `null` for outside), `principal`, `borrowedOn`, `repaid`, `outstanding`, `status`, `closedOn`, `repaymentPlan` (`amount`, `from`, `expectedByNow`, `behind`) or `null`, `note`.
- `GetPlans(bucketId?, includeEnded)` → `PlanDto`: `id`, `bucketId`, `amount` (this month's version), `validFrom`, `validTo`, `effectiveEnd`, `status`, `dueNow`, `shortfall`, `prepaid`, `currentPause`; `GetPlan(id)` → `PlanDetailDto`: the same plus `versions`, `pauses` and `periods` (each month: `month`, `accrued`, `settled`, `state` = `Paid`, `PartlyPaid`, `Unpaid`, `Waived`, `Paused`, `Skipped`, `CappedByLimit`, `Future`).
- Overview: `BucketSummaryDto` gains `limit`, `limitReached`, `dueByPlans` (sum of `DueNow` of its plans), `shortfall`, `openLoans` (count) and keeps `outstandingPrincipal` (now non-zero); `BudgetOverviewDto` gains `attention`: a list of `AttentionItemDto` (`kind`, `bucketId`, `planId`, `loanId`, `amount`, `since`), kinds `PlanShortfall`, `RepaymentBehind`, `LimitReached` (a bucket at its limit with an active plan: the goal is reached, consider ending it), ordered by kind then oldest first. No progress percentage in the DTO: `limit` and `netContributed` are there, and the Web app's progress bar is display, not a figure.
- `GetJournal` gains a filter `planId` and `loanId` (an entry and everything that refers to it) beside `bucketId`.

**B20. One state builder, now with today.**
- `BudgetState.Replay(buckets, journal, today)`: dues need the current month, which only "today" gives. Domain stays pure (today is a parameter, computed in Application from `IClock` and the settings). `BudgetSnapshot.State` becomes `StateAsOf(today)`; handlers already know today. The state gains `Loans`, `Plans` (with periods), `Limits` (history per bucket), and the checks the writer needs: `FirstOverdraft` (1), `FirstOverRepayment` (4), `LimitBreaches` (entries that crossed a limit), `PastPeriods` (accrual of every month before the current month, compared by B8).
- Rule 6 unchanged: no stored projection; the replay of one owner stays fast (measured in step 19, not assumed).

**B21. Web.**
- Navigation (DESIGN 8): Overview, Buckets, Plans, Loans, Journal, Settings; Plans and Loans appear with their pages.
- Every write uses the 1a pattern: a form model in plain C# (one version 7 ID per form, and a `groupId` for several entries), the server's codes checked client-side where they exist and `input.*` codes otherwise, a summary step before writing (DESIGN 1.6), refusals explained from codes with parameters.
- Add entry gains Borrow, Repay and Adjust; a deposit or transfer may choose a plan of its bucket; a deposit refused with `bucket.limit_exceeded` offers "Raise the limit to X?" in the summary, where X is the bucket's `NetContributed` plus the amount (both from the Api), and confirming resends with `raiseLimit`.
- Plans: list per bucket, detail with the period table, create, change amount, end, pause and resume, waive with a dialog naming the plan and the amount (DESIGN 9). Loans: open and closed loans, detail with repayments, set the repayment plan. Allocate: payment, source, lines, preview, remainder choice, summary, confirm.
- Overview cards: progress to limit (bar with the numbers, not colour alone), due by plans, shortfall (warning token with an icon and a label), lent out and open loans; "Needs attention" above the cards.

**B22. No new packages.** Everything above uses what is there (table below).

**B23. Unarchiving a bucket. (changes the spec; added at the developer's request on 19 Nov 2026)**
- *Decision:* `UnarchiveBucket` sets an archived bucket back to active. It fails with `bucket.not_found` (404), `bucket.system_bucket` (never archived anyway) and `bucket.name_taken` when an active bucket now has its name (archiving frees the name; the owner renames one of the two first). Unarchiving an active bucket succeeds and writes nothing, as archiving an archived one does. A mutable-document change committed under the ledger version, like archiving; no journal entry, because no balance or plan depends on when a bucket was archived.
- *Why it is safe:* an archived bucket is always empty, lends to no open loan and has no active plan (B17), and nothing was written to it or reversed on it while archived, so the journal and every replayed figure are the same before and after. Old entries become reversible again under the usual checks (invariant 1, B5, B8). Ended plans stay ended.
- *Why not a new bucket instead:* one purpose keeps one bucket and one ID, so the Journal filter, its old plans and the phase-2 bucket history show one continuous history instead of two buckets.
- *Alternative rejected:* archiving automatically when a bucket becomes empty, or a setting for it: archiving stays the owner's explicit action (an empty bucket with a plan still expects contributions).

## Contradictions found, and how the plan resolves them

| # | Issue | Resolution |
|---|---|---|
| 1 | ARCHITECTURE 4.1 adds `limit` as a bucket field (1a contradiction 6 kept it for 1b), 4.6 says a raise "writes `LimitRaised`" (not an entry type in 4.2), and B12 needs the limit's history so a raise never changes past periods (invariant 6). | B1 (decided: option A) and B12: the limit, still a required feature of every bucket, is replayed from dated `LimitChanged` journal entries instead of a stored bucket field. 4.1, 4.2 (table and shapes), 4.6 and the decision log are updated in step 1. |
| 2 | 2.2 "aggregates are stored as documents" and 4.5 "`ContributionPlan` ... versions" against 2.3 "plan status ... computed from the journal" and 4.5 "changed later by a new event". | B1, option A (decided 19 Nov 2026): plan definitions are journal entries; a plan is an entity derived by replay. |
| 3 | 4.5 counts only deposits linked to a plan; the usual flow pays plans by transfer from `Unallocated`. | B9: `Transfer` gains the optional `planId` as well. 4.5 and 4.2 updated. |
| 4 | 4.1 and invariant 8 name one code, `bucket.not_empty`, for a balance, an open loan and an active plan. | B17: `bucket.has_open_loan` and `bucket.has_active_plan` beside `bucket.not_empty`. |
| 5 | 4.4: the excess of a repayment "goes to remainder handling", which a single repayment does not have. | B4: a single repayment above the outstanding principal is rejected; the allocation flow handles the excess. |
| 6 | 1a archives when `Available` is zero (`Bucket.Archive`); with loans a lender with everything lent out has `Available` 0 and an open loan. | B17: check open loans, active plans, then `NetContributed`. |
| 7 | 4.2: "any entry except a reversal can be reversed". With loans and plans, reversing a borrow with repayments or a plan with contributions leaves entries that refer to nothing, and reversing a definition can change past periods. | B5 (`entry.has_dependents`) and B8 (`plan.past_period_changed`) also apply to reversals. 4.2 updated. |
| 8 | 4.7: lines are capped at the plan's due, yet the remainder may go to "plan prepayment", which is above the due. | B16: caps belong to the preview (a proposal); the command allows a plan line above the due as a prepayment and enforces only hard rules. |
| 9 | Invariant 6 says changing a plan never alters past periods; with B12 a backdated deposit changes an earlier month's limit cap. | B8: invariant 6 is about definition changes; money entries correct what the bucket held. ARCHITECTURE 7 worded accordingly. |
| 10 | 4.6 speaks of savings buckets; 4.9 says the limit of an investment bucket works on net invested. | Both kinds; always `NetContributed` (= net invested for an investment bucket). |
| 11 | 4.6: only "a deposit above the limit" is described. Transfers in and borrowing into a bucket raise `NetContributed` too; opening balances and adjustments record facts. | B12: deposit, transfer in and borrow into are checked; opening balance, adjustment and reversal are exempt; only deposit and allocation offer the raise in one step. |
| 12 | 2.2 and 2.7 define every value format; a month (`yyyy-MM`) is not among them. | B6: kernel `YearMonth`, strict in BSON and JSON; 2.2 and 2.7 updated. |
| 13 | 2.6 asks for stable, versioned DTOs; V1 DTOs gain fields in 1b and the Web reads strictly. | B18: additive within V1 while Api and Web ship together; the first external consumer freezes V1. 2.6 updated. |
| 14 | The XML comments of `JournalEntry.GroupId`, `Deposit`, `BucketBalance.OutstandingPrincipal`, `BucketSummaryDto.OutstandingPrincipal` and `Bucket` say "phase 1b" or "always zero". | Updated in the step that changes each. |
| 15 | 4.4 lists loans "from savings buckets only"; `Unallocated` is a savings bucket. | B2: the lender must be a user's savings bucket; `Unallocated` holds money not yet distributed and lends nothing (`bucket.system_bucket`). |
| 16 | 4.1 says buckets are archived and archived buckets accept nothing, with no way back; a pot used again later would need a new bucket and split its history. | B23: `UnarchiveBucket`. 4.1 updated. |

## Assumptions (accepted with the plan)

- Currencies stay PLN, EUR and USD; loans and plans are in their bucket's currency (B2).
- `Budget.Domain` stays pure: every rule above is a function of (buckets, journal, today); "today" and the current month come from `IClock` and the owner's time zone in Application.
- Every new write is an `ILedgerCommand` through `JournalWriter` (one commit, retried on `concurrency.conflict`); every successful command answers 204; the client already knows the IDs.
- Definition entries carry `OccurredOn` = the day the owner made the change (not after today), and the months they affect as separate fields.
- The replay of one owner's whole journal stays fast enough without a projection; step 19 measures it on a generated journal of several years (target: the overview under 200 ms locally) and records the number.
- A plan's amounts and the allocation lines are in one currency; an allocation in several currencies is several allocations.
- The model test (`BudgetModelTests`) generates dates up to about four months back so plans have past periods.
- UI logic lives in plain classes tested in `FinTrack.Web.Tests`; Razor stays thin; screens are checked by hand at 360 px and 1280 px in light and dark.
- Every new error code gets a resource text and an entry in `KnownCodes` in the step that introduces it; parameters are only dates, months and other non-amount values.

## Packages

Checked on NuGet on 19 Nov 2026.

| Package | In use | Newest | Licence (nuspec) | Note |
|---|---|---|---|---|
| FluentResults | 4.0.0 | 4.0.0 | MIT | unchanged |
| Mediator.Abstractions / SourceGenerator | 3.0.2 | 3.0.2 | licence file (MIT, as recorded in Stage 0) | unchanged |
| FluentValidation | 12.2.0 | 12.2.0 | Apache-2.0 | unchanged |
| MongoDB.Driver | 3.14.0 | 3.14.0 | Apache-2.0 | unchanged |
| MudBlazor | 9.13.0 | 9.14.0 | MIT | stay on 9.13.0; an upgrade is not needed for 1b and would be its own decision |
| Microsoft.Extensions.* | 10.0.15 | 10.0.15 on the 10.x line (11.0.0 exists) | MIT | stay on the .NET 10 LTS line |
| xunit.v3 | 4.0.3 | 4.0.3 | Apache-2.0 | unchanged |
| Shouldly | 4.3.0 | 4.3.0 | BSD-3-Clause | unchanged |
| CsCheck | 5.0.1 | 5.0.1 | Apache-2.0 | unchanged |
| dotnet-stryker (local tool) | 5.1.0 | 5.1.0 | licence file (Apache-2.0, as recorded in the test audit) | unchanged |

No new package is proposed.

## API surface (target)

| Function | Route | Notes |
|---|---|---|
| `AddBorrow` | `POST /v1/journal/borrows` | `id`, `lenderBucketId`, `principal`, `occurredOn`, `toBucketId` or `toUnallocated` or neither (outside), note, tags |
| `AddRepayment` | `POST /v1/journal/repayments` | `id`, `loanId`, `amount`, `occurredOn`, `fromBucketId` or `fromUnallocated` or neither (outside) |
| `AddAdjustment` | `POST /v1/journal/adjustments` | `id`, `bucketId` or `unallocated`, `amount`, `direction`, `occurredOn`, `note` (required) |
| `AddDeposit`, `AddTransfer` (changed) | as in 1a | optional `planId`; `AddDeposit` optional `raiseLimit { id, limit }` and `groupId` |
| `UnarchiveBucket` | `POST /v1/buckets/{id}/unarchive` | no body; 204; `bucket.name_taken` when the name is in use again |
| `SetBucketLimit` | `POST /v1/buckets/{id}/limit` | `id` (entry), `limit` or none (removes), `occurredOn` |
| `CreatePlan` | `POST /v1/plans` | `id`, `bucketId`, `amount`, `validFrom`, `validTo?`, `occurredOn`, note |
| `ChangePlanAmount` | `POST /v1/plans/{id}/amount` | `id` (entry), `amount`, `from`, `occurredOn` |
| `SetPlanEnd` | `POST /v1/plans/{id}/end` | `id` (entry), `validTo?`, `occurredOn` |
| `PausePlan` | `POST /v1/plans/{id}/pauses` | `id` (entry = pause), `from`, `to?`, `mode`, `occurredOn` |
| `ChangePlanPause` | `POST /v1/plans/{id}/pauses/{pauseId}` | `id` (entry), `from`, `to?`, `mode`, `occurredOn` (resume = set `to`) |
| `WaiveShortfall` | `POST /v1/plans/{id}/waivers` | `id` (entry), `amount`, `occurredOn`, `note` (required) |
| `SetRepaymentPlan` | `POST /v1/loans/{id}/repayment-plan` | `id` (entry), `amount`, `from`, `occurredOn` |
| `GetPlans` / `GetPlan` | `GET /v1/plans?bucketId=&includeEnded=`, `GET /v1/plans/{id}` | B19 |
| `GetLoans` | `GET /v1/loans?includeClosed=` | B19 |
| `PreviewAllocation` | `POST /v1/allocations/preview` | read only; B16 |
| `AllocatePayment` | `POST /v1/allocations` | `groupId`, `amount`, source, lines with their entry IDs, optional `raiseLimit` per bucket |
| `GetBudgetOverview`, `GetJournal` (changed) | as in 1a | B18, B19 |

Every write is an `ILedgerCommand`; every route parses its IDs with `RequestValues` (`RouteFormat`).

Error codes introduced (each with a resource text; parameters never contain amounts):

| Code | Kind | Parameters |
|---|---|---|
| `loan.not_found` | 404 | |
| `loan.closed` | 422 | |
| `loan.repayment_exceeds_outstanding` | 422 | `date` |
| `borrow.same_bucket` | 400 | |
| `bucket.borrow_not_allowed` | 422 | |
| `repayment.from_lender` | 422 | |
| `entry.has_dependents` | 422 | |
| `bucket.has_open_loan` | 422 | |
| `bucket.has_active_plan` | 422 | |
| `bucket.limit_exceeded` | 422 | `date` |
| `plan.not_found` | 404 | |
| `plan.pause_not_found` | 404 | |
| `plan.past_period_changed` | 422 | `month` |
| `plan.ended` | 422 | |
| `plan.bucket_mismatch` | 422 | |
| `plan.months_invalid` | 400 | |
| `plan.pause_overlaps` | 422 | |
| `plan.waiver_exceeds_shortfall` | 422 | |
| `allocation.sum_mismatch` | 400 | |
| `allocation.duplicate_line` | 400 | |

Reused: `bucket.system_bucket`, `bucket.archived`, `bucket.currency_mismatch`, `bucket.insufficient_funds`, `money.amount_not_positive`, `entry.*`, `validation.failed` (a missing note: rule `NotEmpty`; a month in the wrong form: `JsonFormat`). Client-side codes are added as the forms need them (`input.month_invalid`, `input.reason_required`, `input.lines_required` and similar).

## Steps

Complexity: **C** complex, **M** moderate, **R** routine. Every step is one branch `stage-1b/NN-short-name` and one pull request into `main`.

Rules for every step: branch from an up-to-date `main`; show the step's plan in a few lines with a recommendation wherever there is a trade-off, then do it; tests first (domain test, domain code, handler, function, UI); a new Budget command joins `BudgetModelTests` (and its coverage check: accepted and rejected somewhere) in the same step; a new entry type gets its stable name in `BudgetMongoMapping.JournalEntryTypes`, a case in `BudgetState.Apply` and a raw-BSON test of its shape; when the code works, break it on purpose 3 to 5 times (one mutation at a time; each broken version must compile with warnings as errors) and confirm the tests catch it, reporting equivalent mutants honestly; build Debug and Release with 0 warnings; `dotnet test`; run end to end with `pwsh scripts/run-local.ps1` against a throwaway database (`Mongo__DatabaseName`, offline rates with `FxRates__BaseAddress=http://127.0.0.1:9/` and `FxRates__Timeout=00:00:02` where rates do not matter), drop it afterwards and never touch `fintrack`; stop the run and check that ports 7071, 5180 and 10000 are free; check UI steps in the browser at 360 px and 1280 px, light and dark; check edited files for CRLF and invisible characters; tick the step with a "Result" note (what was verified and what was not) and update ARCHITECTURE (with the decision log), CLAUDE.md and DESIGN when a decision changes. **No commit or push before the developer's review**; then one conventional commit, push, pull request, green CI, merge commit, delete the branch on GitHub and locally, pull `main`, confirm CI on `main`. Stop after each step.

### [ ] Step 1. Record the phase 1b decisions in the docs (R)
- **Goal:** spec, rules and design guide match what will be built.
- **What we do:** commit this plan with its status set to approved and the recommendations rewritten as decisions; ARCHITECTURE: 2.2 and 2.7 (`YearMonth`), 2.6 (how V1 grows), 4.1 (no `limit` field; archive rules and codes), 4.2 (the new types in the table and their stored shapes, `GroupId`, reversal rules), 4.3 (balances with loans), 4.4 to 4.7 as decided, 7 (wording of invariants 4, 6 and 8), 8 (current phase), decision-log rows for B1 to B23; CLAUDE.md (conventions: definitions as entries, several entries per command, `YearMonth`); DESIGN (8: Plans and Loans, the overview cards and "needs attention"; 9: confirmations for waive and allocate).
- **Done when:** every contradiction row is reflected; no code changed; build and tests unchanged.
- **Depends on:** the developer's approval of this plan (19 Nov 2026).
- **Result:** this plan committed with its status set to approved (B1 chosen by the developer: option A; B2 to B22 as recommended; B23 added at the developer's request) and the recommendations rewritten as decisions. ARCHITECTURE: 2.2 (months stored as `yyyy-MM`), 2.6 (what 1b adds to the read contracts; how `V1` grows), 2.7 (months in JSON), 4.1 (the limit is replayed, not a bucket field; three archive codes; unarchiving), 4.2 (definitions in the journal, several entries per command with a `GroupId`, the stored fields of every new type, the table with the new types, reversal rules), 4.3 (`NetContributed` and `Available` with loans and adjustments), 4.4 to 4.7 ("as built in phase 1b" paragraphs below the original text, which stays as the requirement), 7 (invariants 4 and 6 worded; testing approach), 8 (link to this plan), 23 decision-log rows (B1 to B23). CLAUDE.md: current phase points to this plan; conventions for several entries per command, definitions as journal entries with invariant 6, how `V1` grows, months in JSON. DESIGN: 8 (the 1b card extras and "needs attention"), 9 (allocation summary, the limit-raise question). Every contradiction row is reflected: 1 (4.1, 4.2, 4.6), 2 (4.2, 4.5), 3 (4.2, 4.5), 4 and 6 (4.1), 5 (4.4), 7 (4.2), 8 (4.7), 9 (invariant 6), 10 and 11 (4.6), 12 (2.2, 2.7), 13 (2.6), 15 (4.2, 4.4), 16 (4.1); 14 (XML comments) belongs to the code steps that change them. Verified: only the plan, ARCHITECTURE, CLAUDE.md and DESIGN changed; Release build 0 warnings; 1466 tests pass (1465 passed, 1 explicit skipped); all edited files LF without control characters.
- **Not verified:** CI (runs on the pull request).

### [ ] Step 2. Slice: unarchive a bucket (M)
- **Goal:** a bucket put away can be used again, keeping its history (B23).
- **What we do (tests first):** `Bucket.Unarchive(ownersBuckets)` (system bucket, name taken among active buckets, an active bucket unchanged); `UnarchiveBucket` command and handler (`ILedgerCommand`, committed under the ledger version); function `POST /v1/buckets/{id}/unarchive` (`RouteFormat`); the model test gains the command (archive, unarchive, then entries and reversals on the bucket again); Mongo integration (stored status, version, owner isolation); Web: an "Unarchive" action on archived buckets in the Buckets screen with a confirmation naming the bucket, `bucket.name_taken` explained with a hint to rename; `BucketRow` and `BucketsClient` tests; resource texts.
- **Done when:** archive, unarchive, deposit and reverse an old entry work end to end; unarchiving under a taken name is refused and writes nothing; checked at 360 px and 1280 px, light and dark.
- **Depends on:** 1.

### [ ] Step 3. Write path for several entries; `GroupId`; dependent entries (M)
- **Goal:** the groundwork loans, limits and allocation build on, with 1a's behaviour unchanged.
- **What we do (tests first):** `JournalDraft` holds a list of entries; `JournalWriter` checks the repeat over all of them (B15), the rules, invariant 1 by one replay, and commits them together; the entry factories accept a `groupId`; `ReverseEntry` gains the `entry.has_dependents` rule as an extension point (with no dependent types yet, its tests use a test double of the dependency lookup); `BudgetState.Replay` takes `today` (B20) with no behaviour change yet.
- **Done when:** all 1466 existing tests pass unchanged; new tests cover a group written once, a repeat of the group, a group with one entry changed (`entry.id_reused`), and a partial group (`entry.id_reused`).
- **Depends on:** 1.
- **Risks:** a refactor of the one write path; the model test and the step-7 race tests of 1a guard it.

### [ ] Step 4. Slice: `Adjustment`, back end (M)
- **Goal:** corrections with a reason, the simplest new type first.
- **What we do (tests first):** `Adjustment` (B14) with `direction`, required note; state (`NetContributed`, `AdjustedIn`, `AdjustedOut`, not income); invariant 1 for a decrease; mapping (`Adjustment`, `direction` by name) and a raw-BSON test; `AddAdjustment` with validator (note required), handler on `BucketEntryWriter`, function `POST /v1/journal/adjustments`; the model test; `JournalEntryType.Adjustment` and `direction` in the journal DTO (B18); resource texts.
- **Done when:** an increase and a decrease work end to end; a decrease below zero on any day is `bucket.insufficient_funds`; a missing note is `validation.failed` at `note`.
- **Depends on:** 3.

### [ ] Step 5. Slice: loans, back end (C)
- **Goal:** borrowing from a bucket and paying it back, with invariants 2 and 4 at every point.
- **What we do (tests first):** `LoanId`; `Borrow` and `Repayment` (B2); `BudgetState`: loans by replay (principal, repaid, outstanding, status, closed on), lender and destination and source balances (B3), `FirstOverRepayment`; `AddBorrow` and `AddRepayment` through `JournalWriter` (targets with `Unallocated` on demand, the rules of B2, B4); `entry.has_dependents` for a borrow with repayments (B5); `Bucket.Archive` with `bucket.has_open_loan` and `NetContributed` (B17); mappings and raw-BSON tests (`toBucketId` and `fromBucketId` optional); functions; error codes and texts. CsCheck properties: invariant 2 with random borrows and repayments, invariant 4 at every point (an independent running sum per loan), "reversing any entry equals the journal without it" with the new types, totals unchanged by a borrow into a bucket. The model test gains both commands.
- **Done when:** a loan opens, is repaid in parts, closes and reopens when a repayment is reversed; a repayment above the outstanding principal, a backdated repayment before its borrow and the reversal of a borrow with repayments are each refused with their code and write nothing; a lender with an open loan cannot be archived.
- **Depends on:** 3.
- **Risks:** the stored shapes are permanent: review them before merging.

### [ ] Step 6. Read contracts for loans and adjustments (M)
- **Goal:** the UI and later agents can see loans.
- **What we do (tests first):** `GetLoans` and `LoanDto` (B19, without the repayment plan yet: the field exists and is `null` until step 12); the journal DTO gains `loanId` and documents `Borrow` and `Repayment`; the `loanId` filter; the overview's `outstandingPrincipal` documented as real and `openLoans` added; functions; JSON shapes pinned; the model test checks the loan list against the state after each history.
- **Done when:** integration tests cover open and closed loans, paging of a loan's entries, owner isolation.
- **Depends on:** 4, 5.

### [ ] Step 7. Web: borrow, repay, adjust; Loans screen (C)
- **Goal:** loans and corrections usable in the browser, early.
- **What we do (tests first for the plain classes):** `EntryFormModel` gains Borrow (lender, destination or "outside"), Repay (an open loan, source or "outside", the outstanding amount shown, a link to Allocate when the amount is above it: B4) and Adjust (direction, note required) with summaries; page `/loans` (open loans first, closed on request, table and cards) with a loan's repayments; "Loans" in the navigation; the Journal rows for the three types; resource texts.
- **Done when:** each form works end to end at both widths and themes; a double submit writes once; refusals are explained in the summary.
- **Depends on:** 6.

### [ ] Step 8. Kernel `YearMonth` (M)
- **Goal:** months as a type before any plan uses them.
- **What we do (tests first):** `YearMonth` in the kernel (from a `DateOnly`, comparison, next and previous, months between); its JSON converter in `FinTrackJson` and its BSON serializer in `MongoSerialization` (`"yyyy-MM"`, strict: one spelling, range); round trips, rejections, a raw-BSON test.
- **Done when:** both formats are pinned and documented in ARCHITECTURE 2.2 and 2.7.
- **Depends on:** 1 (independent of 3 to 7).

### [ ] Step 9. Slice: contribution plans, back end (C)
- **Goal:** plans with versions, an end month, carry-over, prepayment, and contributions linked to them.
- **What we do (tests first):** `PlanId`; `PlanCreated`, `PlanAmountChanged`, `PlanEndChanged` (B7); the pure period engine in Domain (accrual per month, credit oldest first, `DueNow`, `Shortfall`, `Prepaid`, status); B8's comparison of past months and `plan.past_period_changed`; the optional `planId` on `Deposit` and `Transfer` (B9), with a test that a stored 1a deposit without it still reads; `entry.has_dependents` for a plan with entries; `bucket.has_active_plan` (B17); commands `CreatePlan`, `ChangePlanAmount`, `SetPlanEnd`; functions; raw-BSON tests; CsCheck properties for invariant 6 (any definition entry dated today leaves every earlier month's accrual equal) and for "dues equal an independent month-by-month sum"; the model test gains the three commands and linked contributions.
- **Done when:** 4.5's example (500, paid 300 → 700 due) holds end to end; a version starting last month is refused with the month; an ended plan keeps its shortfall; prepayment settles later months.
- **Depends on:** 3, 8.
- **Risks:** the largest rule set of the phase; the period engine is written and property-tested before any handler uses it.

### [ ] Step 10. Slice: pauses and waivers, back end (C)
- **Goal:** pause with shift or skip, resume, write off a shortfall.
- **What we do (tests first):** `PlanPaused`, `PlanPauseChanged` (B10) and `ShortfallWaived` (B11) with their rules; the engine's paused months, shifted end and void waiver excess; `PausePlan`, `ChangePlanPause`, `WaiveShortfall`; functions; raw-BSON tests; properties: shift and skip accrue the same months, shift moves the end by the paused months, a waiver never creates a prepayment, invariant 6 with pauses; the model test.
- **Done when:** a pause from the current month, its resume next month, an overlapping pause refused, a pause starting last month refused, a waiver above the shortfall refused, a waiver reversed.
- **Depends on:** 9.

### [ ] Step 11. Slice: bucket limits, back end (C)
- **Goal:** a limit that caps contributions and stops plans.
- **What we do (tests first):** `LimitChanged` (B12); the limit history in the state; `LimitBreaches` and `bucket.limit_exceeded` for deposit, transfer in and borrow into; exemptions; `raiseLimit` on `AddDeposit` (two entries, one group); the accrual cap shared by a bucket's plans; `SetBucketLimit`; function; raw-BSON test; properties: no checked entry ever leaves `NetContributed` above the limit of its date, raising a limit never changes an earlier month (B8); the model test.
- **Done when:** a deposit above the limit is refused with the date and accepted with `raiseLimit` (both entries, one `GroupId`); a plan accrues only up to the room; a repayment into a full bucket is accepted.
- **Depends on:** 9, 3.

### [ ] Step 12. Slice: repayment plans, back end (M)
- **Goal:** a schedule for a loan.
- **What we do (tests first):** `RepaymentPlanChanged` (B13); expected and behind; `SetRepaymentPlan`; `LoanDto.repaymentPlan` filled; function; raw-BSON test; B8 applies; the model test.
- **Done when:** behind and on-track loans are reported correctly; a schedule on a closed loan is refused.
- **Depends on:** 6, 9.

### [ ] Step 13. Read contracts for plans; overview extras; "needs attention" (M)
- **Goal:** the summaries of 13.1's 1b row.
- **What we do (tests first):** `GetPlans`, `GetPlan` with periods (B19); overview: `limit`, `limitReached`, `dueByPlans`, `shortfall`, `attention`; the journal DTO's new fields and the `planId` filter; functions; JSON pinned; the model test checks plans, overview and attention against the state.
- **Done when:** integration tests cover each attention kind, each period state and owner isolation.
- **Depends on:** 10, 11, 12.

### [ ] Step 14. Web: Plans screen (C)
- **Goal:** create and manage plans in the browser.
- **What we do (tests first for the plain classes):** page `/plans` (by bucket, status, due, shortfall), plan detail with the period table, dialogs or pages for create, change amount, end, pause and resume (months typed or picked), waive with the confirmation naming the plan and the amount (DESIGN 9); "Plans" in the navigation; refusals such as `plan.past_period_changed` explained with the month.
- **Done when:** every plan action works end to end at both widths and themes.
- **Depends on:** 13.

### [ ] Step 15. Web: limits, plan contributions and the limit raise (M)
- **Goal:** the limit and plan links in the existing forms.
- **What we do (tests first):** set, change and remove a limit from the Buckets screen; deposit and transfer forms offer the bucket's plans; a deposit refused with `bucket.limit_exceeded` offers "Raise the limit to X?" in its summary and resends with `raiseLimit`; repayment plan on a loan.
- **Done when:** each path works end to end; declining the raise writes nothing.
- **Depends on:** 14, 7.

### [ ] Step 16. Web: Overview extras, "needs attention", Journal with every type (M)
- **Goal:** where things stand, including obligations.
- **What we do (tests first):** card extras (progress to limit with numbers, due by plans, shortfall with icon and label, lent out); the attention list with a link from each item to its plan or loan; journal rows and filters for every new type (plan, loan), with definition entries labelled as such.
- **Done when:** every figure equals the Api's for a prepared data set; nothing is computed in the Web app.
- **Depends on:** 13, 15.

### [ ] Step 17. Slice: manual allocation, back end (C)
- **Goal:** one payment split across plans, loans and buckets, written in one commit.
- **What we do (tests first):** the engine (B16) in Domain with CsCheck properties for invariant 9 (lines plus remainder equal the payment for any input; the same input gives the same output; no line above its cap); `PreviewAllocation` (query, POST); `AllocatePayment` (from outside or from a bucket; entries per B16; `raiseLimit`; one `GroupId`); `allocation.*` codes; functions; the model test with random allocations, including repeats.
- **Done when:** an allocation of new money and one from `Unallocated` each write the expected entries once; a changed state between preview and confirm is refused by the hard rules, never written differently.
- **Depends on:** 11, 12.

### [ ] Step 18. Web: allocation flow (C)
- **Goal:** split a payment in the browser.
- **What we do (tests first for the form model):** page `/allocate` reached from the Overview: amount, source, lines (plan, loan, bucket), preview from the Api with the reason for every cap, the remainder choice, the summary (DESIGN 1.6), confirm; one `groupId` and one ID per line per form.
- **Done when:** works end to end at both widths and themes; a double submit writes once.
- **Depends on:** 16, 17.

### [ ] Step 19. Mutation testing and replay speed (M)
- **Goal:** the new rules are tested where it counts, and replay is still fast enough.
- **What we do:** Stryker.NET on Budget.Domain, Budget.Application and Budget.Infrastructure (commands in `docs/plans/test-audit.md`); every survivor classified as a missing test (closed) or equivalent (listed); `dotnet build --no-incremental` afterwards and the leftover test databases dropped; replay timing on a generated journal of several years of entries (the overview and one command), recorded.
- **Done when:** the scores and survivors are recorded in `docs/plans/test-audit.md`; the timing is recorded; a stored projection is proposed only if the timing requires it (rule 6).
- **Depends on:** 18.

### [ ] Step 20. Phase 1b verification and docs sync (R)
- **Goal:** 1b is closed.
- **What we do:** walk the checklist below with evidence; run the prepared scenario (below) through the UI on an empty throwaway database; fix drift in ARCHITECTURE, CLAUDE.md, DESIGN, README, LOCAL-SETUP and GETTING-STARTED (plans, pauses, limits, loans, adjustments, allocation; what is still missing); set "Current phase" to 2 (its plan to be written and approved first).
- **Done when:** every checklist item is ticked with its evidence.
- **Depends on:** all.

## Phase 1b is complete when…

- [ ] A loan can be borrowed from a savings bucket into a bucket or to the outside, repaid in parts from a bucket or from outside, closes when repaid and reopens when a repayment is reversed; `Available = NetContributed − OutstandingPrincipal` and never below zero at any point of the replay order (invariants 1 and 2, properties).
- [ ] A repayment never exceeds the outstanding principal at any point (invariant 4, property); the excess is handled by allocation.
- [ ] Plans accrue monthly in the owner's time zone with versions and an end month; shortfalls carry over and are settled oldest first; overpaying prepays later months; a shortfall can be waived with a reason after a confirmation naming the plan and the amount.
- [ ] Pauses in `Shift` and `Skip` mode work, can be changed and resumed; no change of a plan, a pause or a limit alters a month before the current one (invariant 6, property).
- [ ] A limit caps contributions; a deposit above it offers the raise and writes both entries in one group; plans stop accruing at it; repayments into a full bucket still work.
- [ ] `Adjustment` needs a note, is never income and is tracked separately.
- [ ] An allocation's lines and remainder sum exactly to the payment, the same input gives the same proposal, and nothing is written before the owner confirms (invariant 9, property).
- [ ] A bucket with a balance, an open loan or an active plan cannot be archived, each with its own code (invariant 8); an archived bucket can be unarchived unless its name is in use again.
- [ ] State always comes from the journal (invariant 10): the model test runs every Budget command, 1a's and 1b's, in random histories with repeats (invariant 13) and checks invariants 1, 2, 4, 6, 9, 10, 11 and 13; the coverage check finds every command accepted and rejected.
- [ ] Every new entry type has a stable stored name, a raw-BSON test of its shape, and 1a's stored documents still read unchanged.
- [ ] Plans and Loans screens, the new forms, the Overview extras and "needs attention" work at 360 px and desktop width, light and dark, with no hard-coded colour or string; every new code has a resource text.
- [ ] Mutation testing recorded for the Budget projects; replay timing recorded.
- [ ] `dotnet build -c Release` has zero warnings and `dotnet test` passes locally and in CI (MongoDB 8.0).
- [ ] ARCHITECTURE, CLAUDE.md, DESIGN, README, LOCAL-SETUP and GETTING-STARTED describe what was built; "Current phase" is 2.

## Deliberately deferred

- **Phase 2:** bulk pause with `BatchId`, proportional repayment of a bucket's open loans, goals with a date, automatic and semi-automatic allocation with `Priority` and `RemainderPolicy`, accounts and reconciliation (it will use `Adjustment`), reports and charts (income counts `Deposit` only; adjustments shown separately; plan versus actual; monthly report with shortfalls carried over, loans opened and repaid), period snapshots, stored projections if the step-19 timing or later measurements require them, database-side journal paging.
- **Later:** loan interest (`LoanInterest`, `InterestDue`, its own entry type), cross-currency loans, reversing a whole group in one action, notifications.
- **When needed:** a V2 of the read contracts when the first consumer that does not ship with the Api appears (B18); a MudBlazor upgrade.

## Verification of the whole phase

1. Clean clone → `pwsh scripts/check-prereqs.ps1` → `dotnet build -c Release` → `dotnet test`.
2. `pwsh scripts/run-local.ps1` on an empty throwaway database, then the prepared scenario below through the UI only. M is the month of the run, M−1 and M−2 the two before it; every amount is invented; locale `pl-PL`, time zone `Europe/Warsaw`, reporting currency PLN.

   | # | Action in the UI | Expected |
   |---|---|---|
   | 1 | First run; buckets Cushion (PLN), Car (PLN), Holiday (EUR), all savings | three buckets |
   | 2 | Opening balances on the 1st of M−2: Cushion 10 000,00 PLN, Car 2 000,00 PLN | saved |
   | 3 | Limit of Car 5 000,00 PLN, then a plan for Car of 500,00 PLN from M−2 | both saved (no period existed before) |
   | 4 | Deposits to Car linked to the plan: 500,00 in M−2, 300,00 in M−1 | plan due now 700,00, shortfall 200,00 |
   | 5 | Change the Car plan to 400,00 from M−1 | refused: `plan.past_period_changed` naming M−1 |
   | 6 | Borrow 1 500,00 PLN from Cushion to the outside, in M−1; repayment plan 250,00 from M−1 | Cushion available 8 500,00, outstanding 1 500,00 |
   | 7 | Repay 2 000,00 PLN from outside | refused: `loan.repayment_exceeds_outstanding` |
   | 8 | Repay 400,00 PLN from outside in M | outstanding 1 100,00; repayment plan expected 500,00, behind 100,00 |
   | 9 | Plan for Holiday 100,00 EUR from M−2; pause it from M−1, Skip | refused: `plan.past_period_changed` naming M−1 |
   | 10 | Pause the Holiday plan from M to M, Skip | due now 200,00 EUR, all of it shortfall (M−2 and M−1) |
   | 11 | Waive 100,00 EUR of the Holiday shortfall, reason "Trip cancelled" | the dialog names the plan and 100,00 EUR; M−2 waived, shortfall 100,00 (M−1) |
   | 12 | Allocate 2 000,00 PLN of new money: Car plan 800,00, loan 1 000,00 | preview caps Car at 700,00; remainder 300,00 |
   | 13 | Send the remainder to Unallocated (PLN) and confirm | three entries with one `GroupId` (deposit 700,00 to Car with the plan, repayment 1 000,00, deposit 300,00 to Unallocated); Car due 0, outstanding 100,00 |
   | 14 | Adjust Cushion down 12,34 PLN, note "Bank fee not recorded" | saved |
   | 15 | Deposit 2 000,00 PLN to Car (no plan) | refused in the summary with "Raise the limit to 5 500,00 PLN?"; confirmed: deposit and limit change in one group |
   | 16 | Archive Cushion | refused: `bucket.has_open_loan` |
   | - | Overview | Cushion available 9 887,66 (net contributed 9 987,66, lent out 100,00); Car 5 500,00 at its limit; Unallocated (PLN) 300,00; PLN total 15 687,66; Holiday 0,00 EUR with a shortfall of 100,00 EUR; needs attention: Holiday shortfall 100,00 EUR since M−1, Car limit reached with an active plan |
   | - | Plans, Loans, Journal | Car plan paid up; Holiday plan with M−2 waived, M−1 unpaid, M skipped; one open loan with 100,00 outstanding, on track (expected 500,00, repaid 1 400,00); every accepted action above in the Journal, nothing written by the refused ones |

3. Disconnect the network → the overview still shows per-currency totals and the last known rate with its date.
4. Push a branch → GitHub Actions green.
