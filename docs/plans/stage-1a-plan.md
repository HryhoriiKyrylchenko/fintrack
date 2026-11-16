# FinTrack Phase 1a (first usable version): implementation plan

## Context

Stage 0 is complete (PR #19, `3c372b6`, 424 tests): kernel (`Money`, `Currency`, IDs, `IClock`, `AppError`, `ILedger`), MongoDB adapter (storage format, owner-scoped collections, atomic ledger commit, outbox), pipeline behaviours, a Function App with health and sweep, a Blazor shell with a status page, the run script and CI on MongoDB 8.0. The Budget projects exist but hold only an `AssemblyMarker`.

Phase 1a is the first version that can be used next to the spreadsheet: buckets, `Unallocated`, `OpeningBalance`, `Deposit`, `Spend`, `Transfer`, `Reversal`, multi-currency, balances, a simple overview and a journal screen (ARCHITECTURE 8). The largest risk named in the decision log is never reaching real use, so every step below is a vertical slice and the UI arrives as early as the back end allows.

Status: approved 16 Nov 2026, with every decision as written, including the three rule changes D8, D9 and D14 and the new package `Microsoft.Extensions.Http`. Tick a step's `[ ]` when its pull request is merged.

## Phase 1a scope

- **In:** `Bucket` (create, rename, archive), system bucket `Unallocated` per currency, the Budget journal with `OpeningBalance`, `Deposit`, `Spend`, `Transfer` (same and different currencies), `Reversal`; the state builder and balances (`NetContributed`, `OutstandingPrincipal` = 0, `Available`); command idempotency; owner settings (reporting currency, time zone, locale) with a first-run screen; `IFxRateProvider` with Frankfurter and stored rates; read contracts (overview, buckets, journal, settings); Web screens: first run, Overview, Buckets, entry forms with a confirm step, Journal with reversal, Settings (with the status page under it).
- **Out (Phase 1b):** contribution plans, pauses, limits and `LimitRaised`, loans (`Borrow`, `Repayment`), `Adjustment`, manual allocation, "needs attention", bucket fields `limit` and `Priority`.
- **Out (later):** accounts and reconciliation, charts and reports (income report, net worth over time), Investments context and its events, imports and drafts, crypto currencies, backup and export, cloud and login, anything AI.

## Decisions taken

Approved by the developer on 16 Nov 2026. Each decision keeps its reason and the alternative that was rejected; all are recorded in `docs/ARCHITECTURE.md` (sections 2 to 4, 7 and the decision log).

**D1. Journal entry model and stable type names.**
- *Decision:* in `Budget.Domain`, an abstract record `JournalEntry : IAppendOnlyDocument` with the common fields of ARCHITECTURE 4.2 (`Id`, `OwnerId`, `SchemaVersion`, `RecordedAt`, `OccurredOn`, `Note`, `Tags`, `GroupId`, `Source`, `ExternalId`) and one sealed record per type (`OpeningBalance`, `Deposit`, `Spend`, `Transfer`, `Reversal`). Each is created through a static factory returning `Result`; constructors are private. No attributes in Domain.
- Mapping lives in `Budget.Infrastructure` (`BudgetMongoMapping.Register()`): one `BsonClassMap` per type with `MapCreator`/constructor mapping, the root marked as root class, the discriminator element named `type` (a scalar discriminator convention registered for `JournalEntry`) and its value set explicitly to the spec's type name (`"Deposit"`, never the CLR name).
- Guard tests: every concrete subclass of `JournalEntry` has an explicitly registered discriminator; a raw-BSON test pins each stored shape (as `StorageFormatTests` did in Stage 0).
- `Source` is a small value type stored as a string (`Manual` now; `Import:<connector>` and `Agent:<name>` later). References between documents use typed IDs (`BucketId`, `JournalEntryId`); `IDocument.Id` stays `Guid`.
- *Why:* one type per concept (decision log "Domain types are stored directly"), names that survive refactoring (Stage 0 step 12 finding).
- *Alternative rejected:* one flat document with nullable fields per type: simpler mapping, but every rule becomes "if type is X then field Y must be set".

**D2. Replay order, and when "never negative" is checked.**
- *Decision:* replay order is (`OccurredOn`, `RecordedAt`, `Id`), supported by the index `{ ownerId, occurredOn, recordedAt, _id }` on `budget_journal`. `RecordedAt` is equal for entries of one commit, so `Id` breaks the tie; random within a millisecond but stable.
- Invariant 1 is checked **at every point of that order**, not only on the final balance: a new (possibly backdated) entry is accepted only if no savings bucket is below zero after any entry of the resulting journal.
- *Why:* the spec says `Available` is never negative "at any moment", and a negative day in history would later corrupt "bucket history" and "net worth over time".
- *Alternative:* check the final balance only: more forgiving while catching up from the spreadsheet, but allows impossible history.

**D3. Reversal.**
- *Decision:* a `Reversal` entry references one entry (`ReversedEntryId`). The state builder then skips the referenced entry, as if it never happened at its original date; the reversal itself moves no money. Any entry except a `Reversal` can be reversed, once. A reversal cannot be reversed: to redo, enter a new entry. Reversing a `Transfer` voids both sides (it is one entry). A reversal is rejected when the journal without the entry would break D2 (for example reversing a deposit that was already spent).
- Errors: `entry.not_found`, `entry.already_reversed`, `entry.not_reversible`, `bucket.insufficient_funds`.
- *Why:* this is what "edit = reversal + new entry" needs: history as of the original date becomes correct. Nothing is updated or deleted (rule 5).
- *Alternative:* a compensating entry dated today: balances today are right, but past dates keep the mistake.

**D4. Command idempotency (invariant 13).**
- *Decision:* the client generates the IDs (UUIDv7, already the rule): a command carries the ID of the entry (or bucket) it creates. The handler loads the journal anyway; if an entry with that ID exists and equals what the command would write (all fields except `RecordedAt`), the handler returns success and writes nothing; if it differs, `Conflict` `entry.id_reused` (`bucket.id_reused` for buckets). A duplicate that slips in concurrently is caught by the `_id` unique index, becomes `concurrency.conflict`, is retried by `RetryOnConflictBehavior`, and then takes the "already exists" path.
- *Why:* no extra collection, no extra write; the journal itself is the record of handled commands.

**D5. `Unallocated`.**
- *Decision:* `Bucket` has `SystemRole` (`None`, `Unallocated`). Commands address a target either by `bucketId` or as "Unallocated of currency X". The handler resolves it from state and, when missing, creates the bucket (server-generated ID) **in the same `LedgerCommit`** as the entry (`Saves`, version 0). Two concurrent first uses conflict on the ledger version and the retry finds the bucket; a partial unique index (`ownerId`, `systemRole`, `currency`) is the backstop. It cannot be renamed or archived (`bucket.system_bucket`); its display name comes from the UI resources.
- *Why:* "created on demand" with no separate setup call and no window where the entry exists without its bucket.

**D6. Transfer.**
- *Decision:* one entry with `FromBucketId`, `ToBucketId`, `FromAmount` (source currency: everything that leaves the source, fee included), `ToAmount` (destination currency: what arrives), optional `Fee` (source currency, part of `FromAmount`) and `Rate`.
- The amounts are the truth. `Rate` is computed by the domain as `ToAmount ÷ (FromAmount − Fee)` and stored for display; the form lets the user type the rate or the received amount and shows the other before confirming.
- Rules: different buckets (`transfer.same_bucket`); each amount in its bucket's currency (`bucket.currency_mismatch`); `Fee < FromAmount` (`transfer.fee_exceeds_amount`); same currency: `ToAmount = FromAmount − Fee` exactly (`transfer.amounts_inconsistent`).
- Balances: source `−FromAmount`, destination `+ToAmount`. A transfer is never income.
- *Why:* storing a typed rate next to both amounts can never be made exactly consistent because of rounding; storing a derived rate cannot be inconsistent.
- *Note:* the spec says the rate is recorded "as entered by the user"; here it is recorded as implied by the amounts the user confirmed. ARCHITECTURE section 3 says so.

**D7. Owner settings.**
- *Decision:* `OwnerSettings` (mutable document, `budget_settings`, one per owner, unique `ownerId`) with `ReportingCurrency`, `TimeZone` (IANA ID) and `Locale` (BCP 47 tag). Budget owns it; Investments will read it through `Budget.Contracts`. Saved through `ILedger`.
- First run: `GET /v1/settings` answers 404 `settings.not_found`; the Web app then shows a setup screen (reporting currency PLN/EUR/USD; time zone and locale prefilled from the browser) and nothing else until it is saved. Later changes on the Settings page; the reporting currency can be switched at any time.
- The Web app applies the locale before the first render; number, date and money formatting use it.
- *Why:* the spec puts these settings in "shared kernel" terms but a document needs one owning context; a separate settings context is not worth it yet.

**D8. FX rates are reference data without an owner (changes rule 9).**
- *Problem:* rates are public data, the same for every owner, yet rule 9 and invariant 12 say every document has `OwnerId` and every query filters by it.
- *Decision:* a second documented exception, like the outbox sweep: **reference data** (FX rates now, prices in phase 3) lives in collections prefixed `ref_` (`ref_fx_rates`), has no `OwnerId`, and is reachable only through a dedicated `ReferenceCollection<T>` in `Shared.Infrastructure`; owner data never goes there. `IFxRateProvider` and `FxRate` are declared in the kernel (both contexts need them), implemented in `Shared.Infrastructure` (`FrankfurterFxRateProvider` plus the stored history).
- Behaviour: rates are stored EUR-based per rate date; cross rates go through EUR. A request uses a rate fetched today if there is one, otherwise calls Frankfurter (5 s limit) and stores the answer; when the call fails (offline) it uses the newest stored rate; when none exists the converted total is absent and the DTO says why (`fx.rate_unavailable`). No timer in 1a (the app does not run all day locally). Totals convert each currency subtotal once (banker's rounding) and always carry the rate date (DESIGN 6).
- *Alternative:* store rates per owner: keeps rule 9 literally, but duplicates public data per user and a refresh has no owner to run under.

**D9. What goes into `Budget.Contracts` (changes a guard rule).**
- *Decision:* `Contracts` holds (a) the read queries as mediator messages (`GetBudgetOverview`, `GetBuckets`, `GetJournal`, `GetOwnerSettings`) with documented DTOs in a `V1` namespace, and (b) the request bodies of the write endpoints as plain records. Commands and all handlers stay in `Application`.
- This needs `Mediator.Abstractions` and `FluentResults` in `Contracts`, which the architecture guard (`ArchitectureRules.AllowedPackages[Contracts] = []`) forbids today: the allow-list gains exactly these two.
- The Web project references `Budget.Contracts` and the kernel from the first UI step (the guard already allows it).
- *Why:* the spec wants the queries themselves in `Contracts` (they are the future agent tools, and another context may reference only `Contracts`); a mediator query must implement `IQuery<T>`.
- *Alternative:* DTOs only in `Contracts`, query types in `Application`: no rule change, but another context could then not send the query.

**D10. JSON contract.**
- *Decision:* camelCase; enums as names; dates as `yyyy-MM-dd`; instants ISO 8601 UTC; **amounts as JSON strings** (`"amount": "1234.56"`) with the currency code next to them. One shared options object with the converters lives in the kernel (`System.Text.Json` is part of .NET, no package) and is used by the Api and the Web app.
- *Why:* a JSON number is read as a binary double by JavaScript and most tools, which cannot hold 18 decimal places; the contract should not depend on both sides being .NET.

**D11. Investment buckets in 1a.**
- *Decision:* follow ARCHITECTURE 4.1 and 4.9: kinds `Savings` and `Investment` (profiles `Crypto`, `RealValueAssets`) exist. An investment bucket accepts opening balance, deposit and transfers; `Spend` is rejected (`bucket.spend_not_allowed`); its figure is shown as "Invested" and, as the spec says, may become negative (D2 applies to savings buckets only). No market value until phase 3.
- *Alternative:* savings buckets only in 1a, investment kind in phase 3: smaller, but real use next to the spreadsheet would miss the money already invested.

**D12. Dates in the future.**
- *Decision:* an entry's `OccurredOn` may not be later than today in the owner's time zone (`entry.date_in_future`). Backdating stays allowed.
- *Why:* otherwise "balance now" would contain money that has not arrived.

**D13. Amounts and opening balances.**
- *Decision:* every entry amount is strictly positive (`money.amount_not_positive`); at most one non-reversed `OpeningBalance` per bucket (`bucket.opening_balance_exists`).

**D14. New test project `tests/FinTrack.Budget.Infrastructure.IntegrationTests`.**
- *Decision:* add it for the permanent storage format of Budget documents (raw BSON, discriminators, indexes, strict reading) and for handlers against real MongoDB. End-to-end function tests stay in `FinTrack.Api.IntegrationTests`; handler logic with in-memory fakes in `FinTrack.Budget.Application.Tests`.
- *Alternative:* put everything into `FinTrack.Api.IntegrationTests`: no new project, but format tests then sit far from the mapping they pin.

**D15. Bucket names** are unique among an owner's non-archived buckets, compared without case (`bucket.name_taken`); 1 to 60 characters.

**D16. Screenshot helper.** Keep the DevTools screenshot script from Stage 0 in the repository as `scripts/dev/screenshot.mjs` (Node and Edge or Chrome, no package), so UI steps can be checked the same way. It moves into the repository in the first UI step that needs screenshots.

**D17. No integration events in 1a.** Nothing consumes them before phase 3 ("no dead code"); the outbox stays as built.

## Contradictions found, and how the plan resolves them

| # | Issue | Resolution |
|---|---|---|
| 1 | Rule 9 and invariant 12: every document has `OwnerId`. FX rate history is public data with no owner. | D8: reference data is a second documented exception (`ref_` collections, own collection type). Recorded in ARCHITECTURE 2.2, 2.3, invariant 12 and CLAUDE.md rule 9. |
| 2 | Spec 2.6: read queries live in `Contracts`. The guard test allows `Contracts` no packages, but a mediator query needs `Mediator.Abstractions` (and `FluentResults` for `Result<T>`). | D9: allow exactly these two for `Contracts`. |
| 3 | Spec 3 allows backdated entries; invariant 1 says never negative "at any moment". A backdated spend can make a past day negative while today's balance is fine. | D2: checked at every point of the replay order. |
| 4 | Spec 4.2 and 3: the rate is recorded "as entered by the user". With rounding, a typed rate and two typed amounts cannot be exactly consistent. | D6: amounts are the truth, the rate is derived and stored. |
| 5 | Spec 3 describes reporting currency and time zone as owner settings without an owning context; contexts may not share collections. | D7: Budget owns `budget_settings`; others read through `Contracts`. |
| 6 | Spec 4.1 lists bucket fields `limit` and `Priority` as [P1]; limits are Phase 1b, priority serves allocation (P2). | Added in the phase that uses them. Adding an optional field later needs no upcaster (a missing element reads as empty); step 5 proves it with a test. |
| 7 | DESIGN 6 says "Fiat: 2 decimals"; since Stage 0 each `Currency` carries its own precision. | The UI formats with `Currency.DecimalPlaces`; DESIGN 6 is corrected in step 1. |
| 8 | DESIGN 8 lists the navigation items; Stage 0's Status page is not among them and is the start page. | Overview becomes the start page; Status moves under Settings. |
| 9 | Invariant 5 ("income counts `Deposit` only") has no visible feature in 1a (reports are phase 2). | The state builder exposes deposits separately and a domain test fixes the rule; the report comes in phase 2. |

## Assumptions (accepted with the plan)

- Currencies in 1a: PLN, EUR, USD (already registered). Crypto arrives with Investments.
- `Budget.Domain` stays pure: the state builder and all rule checks are functions over (buckets, entries, today); no clock, no I/O. "Today" is computed in `Application` from `IClock` and the owner's time zone.
- One place reads state (spec 2.3): `BudgetStateLoader` in `Application` reads the ledger version **first**, then journal, buckets and settings through an `IBudgetStore` port implemented in `Budget.Infrastructure` on the Stage 0 collection types. All writes go through `ILedger`. Queries use the same loader.
- All commands are `ILedgerCommand` (bucket and settings changes too, per the decision log "mutable documents are committed under the ledger version"). Successful commands answer 204: the client already knows the IDs.
- `Budget.Infrastructure` registers `LedgerDocuments(budget)` with `budget_journal`, `budget_buckets`, `budget_settings`, its index initializer and its mapping; `ApiComposition` calls one `AddBudget…` method per layer.
- `GetJournal` filters and pages in memory after the replay in 1a (the journal is small); the contract already has a cursor so a database-side implementation can replace it.
- UI logic (formatting, parsing, form state, view models) lives in plain classes tested in `FinTrack.Web.Tests`; Razor files stay thin. Screens are checked by hand at 360 px and 1280 px in both themes.
- Every new error code gets a resource text in the same step (the existing test enforces known codes).
- Route style: plural nouns under `/api/v1`, function name = use case.

## API surface (target)

| Function | Route | Notes |
|---|---|---|
| `GetOwnerSettings` / `SaveOwnerSettings` | `GET` / `PUT /v1/settings` | 404 `settings.not_found` before first run |
| `GetBuckets` | `GET /v1/buckets` | includes archived on request |
| `CreateBucket` | `POST /v1/buckets` | body has the bucket `id` |
| `RenameBucket` / `ArchiveBucket` | `POST /v1/buckets/{id}/rename`, `/archive` | archive only with zero balance (`bucket.not_empty`) |
| `GetBudgetOverview` | `GET /v1/overview` | totals per currency, reporting total with rate date, one summary per bucket |
| `GetJournal` | `GET /v1/journal?bucketId=&from=&to=&limit=&cursor=` | newest first |
| `AddOpeningBalance`, `AddDeposit`, `AddSpend`, `AddTransfer`, `ReverseEntry` | `POST /v1/journal/opening-balances`, `/deposits`, `/spends`, `/transfers`, `/reversals` | body has the entry `id`; target by `bucketId` or `unallocated: "<currency>"` |

Error codes introduced (all with resource texts; parameters never contain amounts): `settings.not_found`, `settings.time_zone_unknown`, `settings.locale_unknown`, `bucket.not_found`, `bucket.archived`, `bucket.name_taken`, `bucket.id_reused`, `bucket.system_bucket`, `bucket.not_empty`, `bucket.currency_mismatch`, `bucket.insufficient_funds`, `bucket.spend_not_allowed`, `bucket.opening_balance_exists`, `money.amount_not_positive`, `entry.not_found`, `entry.id_reused`, `entry.already_reversed`, `entry.not_reversible`, `entry.date_in_future`, `transfer.same_bucket`, `transfer.fee_exceeds_amount`, `transfer.amounts_inconsistent`; in DTOs only: `fx.rate_unavailable`.

## Packages

| Package | Version | Licence | Used in | Status |
|---|---|---|---|---|
| Microsoft.Extensions.Http | 10.0.15 | MIT (same family as the packages confirmed in Stage 0; licence line re-read in step 11) | Shared.Infrastructure (`IHttpClientFactory` for Frankfurter) | new, approved 16 Nov 2026 |
| Mediator.Abstractions | 3.0.2 | MIT | additionally Budget.Contracts (D9) | existing |
| FluentResults | 4.0.0 | MIT | additionally Budget.Contracts (D9) | existing |
| MongoDB.Driver 3.14.0, FluentValidation 12.2.0, MudBlazor 9.13.0, Functions Worker 2.52.0, xunit.v3 4.0.3, Shouldly 4.3.0, CsCheck 5.0.1 | unchanged | as recorded | as today | latest stable on NuGet, checked 16 Nov 2026 |

Not verified while drafting: the exact Frankfurter endpoints and answer shape (the call was not made); step 11 starts by checking them. Frankfurter itself is a free web service, not a package.

## Steps

Complexity: **C** complex, **M** moderate, **R** routine. Every step is one branch `stage-1a/NN-short-name` and one pull request into `main`.

Rules for every step: branch from an up-to-date `main`; tests first; when the code works, break it on purpose two or three times (one change at a time, each must compile) and confirm the tests catch it; build Debug and Release with 0 warnings; run `dotnet test`; tick the step with a "Result" note and update ARCHITECTURE.md / CLAUDE.md when a decision changes; **no commit or push before your review**; then one conventional commit, push, pull request, green CI, merge commit, delete the branch remotely and locally, pull `main`, confirm CI on `main`. Stop after each step.

### [x] Step 1. Record Phase 1a decisions in the docs (R)
- **Goal:** spec, rules and design guide match what will be built.
- **What we do:** commit this plan; ARCHITECTURE (2.2/2.3 and invariant 12 for reference data, 3 and 4.2 for the transfer rate, 4.1 for field timing, decision log rows for D1 to D17); CLAUDE.md (rule 9 wording, conventions, layout if D14 is accepted); DESIGN (6 precision, 8 navigation).
- **Done when:** every row of the contradictions table is reflected; no code changed.
- **Depends on:** nothing.
- **Result:** this plan committed with its status set to approved and the recommendations rewritten as decisions. ARCHITECTURE: 2.2 (who generates IDs, the stable `type` element for stored subtypes, Budget's collections and the replay index, reference data in `ref_` collections), 2.3 (two exceptions to owner scoping), 2.6 (what `Contracts` holds and may reference), 2.7 (JSON contract with amounts as strings, repeated commands), 3 (transfer rate derived from the amounts, FX rate behaviour, owner settings, no future dates), 4.1 (fields added in their phase, name rule, archived buckets, `Unallocated` created in the same commit, investment buckets in 1a), 4.2 (one class per type, positive amounts, one opening balance, replay order, `Transfer` and `Reversal` rows), 4.3 (never negative at any point of the replay order), 4.9 (transfers until phase 3; never-negative for savings only), invariant 12 (both exceptions), one decision-log row for each of D1 to D17, section 11 (`Microsoft.Extensions.Http`). CLAUDE.md: rule 9 (who generates IDs, the reference-data exception), solution layout (`Budget.Contracts`, the new integration test project), conventions (`ReferenceCollection<T>`, `ref_` collections, stable type names for stored subtypes, repeated commands, JSON contract), current phase points to this plan. DESIGN: 6 (precision from the currency), 8 (Overview is the start page, Status under Settings). Contradiction 9 needs no document change: ARCHITECTURE 4.3 already says income counts `Deposit` only; step 6 adds the domain test. Found while writing: ARCHITECTURE 2.2 said every `Id` is client-generated, while `LedgerHead` (Stage 0) and `Unallocated` (D5) get server-generated IDs; both places now say which is which. ARCHITECTURE 4.9 said money leaves an investment bucket only through a portfolio withdrawal, which does not exist before phase 3; it now says transfers are used until then (D11). `Source` now lists `Agent:<name>` in 4.2, as section 12 already required. Verified: only the plan, ARCHITECTURE, DESIGN and CLAUDE.md changed; Release build 0 warnings; 424 tests pass; all edited files LF without control characters.

### [x] Step 2. Contract groundwork: JSON conventions and `Contracts` rules (M)
- **Goal:** one wire format, used by both sides, before the first endpoint exists.
- **What we do (tests first):** kernel JSON options and converters (D10) with round-trip and exact-decimal tests (18 decimals, scale kept); guard rule for `Contracts` (D9) with its table test; Api uses the options for requests and responses (health keeps working); Web `ApiClient` uses them and gains body-carrying calls (PUT/POST) returning the same value-or-error-code result; Web references `Budget.Contracts` and the kernel.
- **Done when:** tests pass; the status page still works end to end.
- **Depends on:** 1.
- **Risks:** the wire format is a contract; changing it later breaks clients.
- **Result:** kernel `Json/FinTrackJson` (no package): read-only `Options` and `Configure(options)` for options a framework owns; converters for `decimal` (string, exact scale; reading accepts only the exact text the value writes, so numbers, exponents, `+`, leading zeros and anything that would round are rejected), `Currency`, `Money` (exactly `amount` and `currency`, precision checked), strongly typed IDs (GUID `D`, never empty), `DateOnly`, instants (written UTC with `Z`, an offset throws; reading needs `Z`) and enums (exact names); unknown, repeated and missing properties and `null` for non-nullable values are rejected. Settled: an amount as a JSON number is rejected; an unreadable request body is 400 `validation.failed` with `rule` = `JsonFormat` and `field` = the camelCase path when it is made of names and indexes only (no new code, no amounts echoed). Guard: `Contracts` may reference `FluentResults` and `Mediator.Abstractions` (table rows added); the package references follow in step 3 with the first query. Api: the Functions ASP.NET Core integration calls `AddMvc()` and executes `IActionResult` with `ExecuteResultAsync` (found in the package), so the options go into MVC's `JsonOptions`; `Program.cs` now calls `ApiComposition.ConfigureFinTrackApi`, which a test uses to build the real host and execute results: the problem JSON, the 500 problem and the `ExceptionMiddleware` output were pinned byte for byte before the change and are unchanged; values follow the contract; `RequestBody.ReadAsync<T>` reads bodies. Web: references the kernel; `ApiClient` reads and writes with the shared options and gains `PostAsync`/`PutAsync` returning a non-generic `ApiResult` (any 2xx is success; `ApiResult<T>` derives from it); a body the contract cannot write throws before sending. `Budget.Contracts` is referenced from step 4, its first use (D9, decision log). `FinTrack.Web.Tests` links `tests/Common` again. Decision log: three rows (strict JSON reading, `JsonFormat`, MVC options). Mutations, each compiling and each caught: decimal reader without the exact-text check (5 failures), unknown properties skipped (1), Api configuring the minimal-API `JsonOptions` instead of MVC's (2: proves which options count), Web reading values with default options (3). Verified: Debug and Release 0 warnings; 569 tests pass (424 before); `run-local.ps1`: `GET /api/v1/health` answers `"checkedAt":"…Z"`, and a headless Edge screenshot at 1280 px shows the status page "Everything is working" with the local time. Not verified: CI (runs on the pull request); the 360 px view (the page did not change).

### [ ] Step 3. Slice: owner settings, back end (C)
- **Goal:** the first Budget use case through every layer.
- **What we do (tests first):** `OwnerSettings` and its rules (D7); `SaveOwnerSettings` command with validator, `GetOwnerSettings` query and DTO; `IBudgetStore`, `BudgetStateLoader` (version first); `Budget.Infrastructure`: mapping, `LedgerDocuments`, index initializer, store, registration; functions; the new integration test project if D14 is accepted (raw format, unique owner, owner isolation, 404 before first save).
- **Done when:** the endpoints work against real MongoDB locally and in CI.
- **Depends on:** 2.
- **Risks:** first mapping of a domain type with a private constructor; settle the mapping pattern here, the journal reuses it.

### [ ] Step 4. Web: first run, settings, locale, navigation (M)
- **Goal:** the app asks for its settings once and formats by them.
- **What we do (tests first for the plain classes):** first-run screen and Settings page; locale applied before the first render; time zone helper for "today"; navigation per DESIGN 8 for existing pages, Status under Settings; resource texts.
- **Done when:** a fresh database leads to the setup screen, saving leads to the app; checked at 360 px and 1280 px in both themes.
- **Depends on:** 3.
- **Risks:** culture data in WebAssembly (which locales format correctly without loading all globalization data); decide with a measurement of download size.

### [ ] Step 5. Slice: buckets, back end (C)
- **Goal:** create, rename and list buckets.
- **What we do (tests first):** `Bucket` (name, currency, kind, profile, status, system role), `BucketId`; `CreateBucket` (idempotent, D4), `RenameBucket`, `GetBuckets`; rules D5 (system bucket protected), D11, D15; mapping, collection, indexes (including the partial unique index for `Unallocated`); functions; a test that a document without an optional element still reads (contradiction 6).
- **Done when:** integration tests prove the rules, owner isolation and idempotent create.
- **Depends on:** 3.

### [ ] Step 6. Slice: journal foundation and `Deposit` (C)
- **Goal:** the first money movement and the state it produces.
- **What we do (tests first):** `JournalEntry`, `Deposit`, `EntrySource`, typed IDs; mapping with explicit discriminators and the guard test (D1); replay order and its index (D2); `BudgetState` and the state builder (`NetContributed`, `OutstandingPrincipal` = 0, `Available`); `AddDeposit` with target resolution and `Unallocated` on demand (D5), idempotency (D4), future-date and amount rules (D12, D13); function; first property tests (invariants 2 and 10: state from replay equals state built step by step, and is independent of insertion order).
- **Done when:** a deposit appears in the journal collection in the pinned format and in the computed balance; concurrent first deposits to `Unallocated` create one bucket.
- **Depends on:** 5.
- **Risks:** the journal format is permanent; review the stored shape before merging.

### [ ] Step 7. Slices: `OpeningBalance` and `Spend` (C)
- **Goal:** balances can start from real numbers and go down, never below zero.
- **What we do (tests first):** both entry types; one opening balance per bucket (D13); spend rules (D11); the "never negative at any point" check (D2) with property tests for invariant 1 over random, also backdated, sequences.
- **Done when:** a spend that would make any day negative is rejected with `bucket.insufficient_funds` and writes nothing.
- **Depends on:** 6.

### [ ] Step 8. Slice: `Transfer` (C)
- **Goal:** money moves between buckets, also across currencies.
- **What we do (tests first):** the entry and its rules (D6); derived rate; both sides in one entry; property tests: a same-currency transfer without fee leaves the owner's total unchanged, no currency is ever added to another (invariant 11).
- **Done when:** same-currency and cross-currency transfers work end to end; inconsistent input is rejected with its code.
- **Depends on:** 7.

### [ ] Step 9. Slices: `Reversal` and archiving a bucket (C)
- **Goal:** mistakes are corrected without editing history; empty buckets can be put away.
- **What we do (tests first):** `Reversal` (D3) for every entry type; `ArchiveBucket` (only with zero balance, never a system bucket; archived buckets reject new entries with `bucket.archived`).
- **Done when:** reversing restores the earlier balances exactly; a reversal that would break D2 is rejected; invariant 3 holds (nothing in the journal collection is ever updated or deleted: the collection type has no such method, and a test compares stored entries before and after).
- **Depends on:** 8.

### [ ] Step 10. Model-wide property tests and isolation (M)
- **Goal:** the invariants hold for arbitrary histories, not only for the examples.
- **What we do:** a CsCheck model of random command sequences over several buckets and currencies (all five entry types, reversals, backdating, repeats of the same command): invariants 1, 2, 10, 11, 13; integration tests that two owners never see or affect each other through any handler (invariant 12). Run the properties for 10 seconds each (`CsCheck_Time=10`).
- **Done when:** all pass; three deliberate rule mutations are caught.
- **Depends on:** 9.

### [ ] Step 11. FX rates: `IFxRateProvider`, Frankfurter, stored history (C)
- **Goal:** totals in the reporting currency, also offline.
- **What we do (tests first):** verify the Frankfurter endpoints and licence of `Microsoft.Extensions.Http` first; kernel interface and `FxRate`; `ReferenceCollection<T>` and `ref_fx_rates` (D8) with its index; provider with a fake HTTP handler in tests (fresh, stale, offline with and without stored rates, weekend dates, cross rate through EUR, malformed answer); logs carry no amounts.
- **Done when:** tests pass without network; one manual run against the real service is recorded in the Result note.
- **Depends on:** 3 (independent of 5 to 10).
- **Risks:** an external service; timeouts and failures must never fail a page.

### [ ] Step 12. Read contracts: overview and journal (M)
- **Goal:** the stable queries the UI (and later agents) use.
- **What we do (tests first):** `GetBudgetOverview` (totals per currency, reporting total with rate date or the reason it is missing, a documented summary per bucket) and `GetJournal` (filters, cursor, reversed entries marked with a link to their reversal); DTOs documented field by field; functions.
- **Done when:** integration tests cover totals, conversion, missing rates and paging.
- **Depends on:** 10, 11.

### [ ] Step 13. Web: money formatting, input and shared pieces (M)
- **Goal:** amounts look and parse right everywhere before any screen shows them.
- **What we do (tests first, plain classes):** money formatter (currency always shown, precision from `Currency`, locale separators, real minus sign, tabular figures) and input parser (locale decimal separator, rejects too many decimals without rounding); shared components for amount display, error display from codes with parameters, empty and loading states.
- **Done when:** tests cover PLN, EUR, USD in at least three locales.
- **Depends on:** 4.

### [ ] Step 14. Web: Buckets screen (M)
- **Goal:** create, rename and archive buckets in the UI.
- **What we do:** list (cards on mobile), create and rename forms, archive with a confirmation naming the bucket; empty state with the first action.
- **Done when:** works end to end at both widths and themes.
- **Depends on:** 9, 13.

### [ ] Step 15. Web: Overview screen (M)
- **Goal:** where things stand, at a glance.
- **What we do:** totals per currency and in the reporting currency with the rate date in a caption; one card per bucket with its available amount (investment buckets: "Invested"); `Unallocated` shown when it holds money; skeletons, empty state.
- **Done when:** numbers match the Api for a prepared data set; offline shows per-currency totals and says why the converted total is missing.
- **Depends on:** 12, 13.

### [ ] Step 16. Web: entry forms with confirmation (C)
- **Goal:** enter opening balances, deposits, spends and transfers.
- **What we do (tests first for the form models):** one "Add entry" flow: type, target (bucket or `Unallocated`), amount, date (default today in the owner's time zone), note, tags; transfer with rate or received amount; a summary step before writing (DESIGN 1.6); rejected operations explained from their codes; a generated ID per form instance so a double submit writes once.
- **Done when:** each type can be entered end to end; a double click creates one entry.
- **Depends on:** 14, 15.

### [ ] Step 17. Web: Journal screen with reversal (M)
- **Goal:** see and correct history.
- **What we do:** table on desktop, cards on mobile; filters by bucket and period; reversed entries marked by label and icon, not colour alone; reverse with a confirmation naming the entry and amount; paging.
- **Done when:** works end to end at both widths and themes.
- **Depends on:** 16.

### [ ] Step 18. Phase 1a verification and docs sync (R)
- **Goal:** 1a is closed and real use next to the spreadsheet can start.
- **What we do:** walk the checklist below with evidence; fix drift in ARCHITECTURE, CLAUDE.md, README, LOCAL-SETUP and DESIGN; write a short "how to start using it" note (first run, opening balances, where the data lives, that there is no backup feature yet and how to run `mongodump` by hand); set "Current phase" to 1b.
- **Done when:** every checklist item is ticked with its evidence.
- **Depends on:** all.

## Phase 1a is complete when…

- [ ] A fresh database leads through first-run setup to an empty overview.
- [ ] Buckets can be created, renamed and archived; `Unallocated` appears per currency on first use and cannot be renamed or archived.
- [ ] Opening balance, deposit, spend, transfer (same and different currencies) and reversal can each be entered in the UI with a confirmation step, and appear in the journal.
- [ ] `Available = NetContributed − OutstandingPrincipal` and never below zero for savings buckets at any point of the replay order (property tests, invariants 1 and 2).
- [ ] State always comes from replaying the journal; building it step by step gives the same result (invariant 10).
- [ ] The journal collection is never updated or deleted from; corrections are reversals (invariant 3).
- [ ] Repeating any command with the same ID writes nothing new (invariant 13).
- [ ] Two owners never see or affect each other's data; reference data holds no owner data (invariant 12 with its two documented exceptions).
- [ ] Stored journal entries carry explicit, stable type names; a guard test fails for a new entry type without one.
- [ ] The overview shows totals per currency and in the reporting currency with the rate date; it still works offline.
- [ ] Every failure reaches the UI as a code with a resource text; no amount appears in logs or error parameters.
- [ ] All screens work at 360 px and at desktop width, in light and dark, with no hard-coded colour or string.
- [ ] `dotnet build -c Release` has zero warnings and `dotnet test` passes locally and in CI (MongoDB 8.0).
- [ ] ARCHITECTURE, CLAUDE.md, DESIGN, README and LOCAL-SETUP describe what was built; "Current phase" is 1b.

## Deliberately deferred

- **Phase 1b:** plans with carry-over, pauses, limits and `LimitRaised` (bucket `limit`), loans (`Borrow`, `Repayment`, `OutstandingPrincipal` above zero), `Adjustment`, manual allocation and `GroupId` spanning several entries, bucket card extras and "needs attention".
- **Phase 2:** accounts and reconciliation, `Priority` and automatic allocation, reports (income counts `Deposit` only), charts, period snapshots, stored projections if replay is measured as slow, database-side journal paging.
- **Phase 3 and later:** Investments context, market value of investment buckets, integration events between contexts, crypto currencies, price history (second user of `ref_` collections), imports and drafts.
- **When needed:** a timer that refreshes FX rates, scheduled `mongodump` backup and JSON/CSV export, a retry limit for outbox messages, upcasters, UI test tooling.

## Verification of the whole phase

1. Clean clone → `pwsh scripts/check-prereqs.ps1` → `dotnet build -c Release` → `dotnet test`.
2. `pwsh scripts/run-local.ps1` on an empty database → first-run setup → create buckets in two currencies → opening balances → deposit to `Unallocated` → transfer to a bucket → cross-currency transfer → spend → reverse the spend → overview and journal show the expected numbers (a prepared scenario with invented amounts, written down in step 18).
3. Disconnect the network → overview still shows per-currency totals and the last known rate with its date.
4. Push a branch → GitHub Actions green.
