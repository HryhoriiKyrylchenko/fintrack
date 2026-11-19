# Test audit after phase 1a (19 Nov 2026)

Between phase 1a and the 1b plan: are the tests reliable (no flaky tests), and do they check real behaviour (no tests that pass whatever the code does)? Branch `stage-1b/00-test-audit`, starting from `main` at `c9a5634` (1387 tests).

## Result

- **No flaky test found.** The whole suite ran 20 times in a row (Release, MongoDB): 27,740 test executions, 0 failures. Of 71 CI runs before the audit, 4 failed, none at random: a real race in the state loader (plan 1a step 7, fixed in the product), a test tied to one ICU version's culture data (step 13, fixed), a deliberate check that CI blocks merging, and one Stage 0 run.
- **Two tests that proved little, and the gaps listed below in what the tests check,** found by mutation testing and closed with 53 new test cases (1440 tests now, one explicit test skipped). One gap was a rule nobody had tested: reversing a transfer whose *other* side is an archived bucket.
- **Mutation scores** (Stryker.NET 5.1.0; share of tested mutants the tests notice):

  | Project | Before | After | Survivors after |
  |---|---|---|---|
  | Budget.Domain | 84.0 % (36 survived, 14 not covered) | **98.9 %** | 3, all equivalent |
  | Budget.Application | 74.6 % (60 survived, 22 not covered) | **97.5 %** | 7: 6 equivalent, 1 covered by the MongoDB integration tests |

  The "before" runs still counted argument guards, `ConfigureAwait` and exception messages; the "after" runs skip them (see the configuration below), so the gain comes partly from that and partly from the new tests.

## Method

1. **Flakiness, measured:** 20 consecutive runs of the whole suite, every failure logged by name.
2. **Flakiness, by pattern:** every use of the real clock in tests (dates of "yesterday" in `Etc/UTC`, unique database names, the clock's own test: all safe by construction), delays and time-outs (the 30 s delay in the rate provider's test is cancelled by the client's time-out), the order of version 7 IDs created in one millisecond (tests that depend on it space their IDs explicitly), shared state in CsCheck properties, which run samples in parallel (the model test locks its one shared set; nothing else is shared).
3. **Tests without real checks:** every test method (777, 1388 cases at the start) has at least one assertion; a scan flagged 15 whose assertions look like only success or "not empty". Most of them either check a value as well or test what is *allowed* (a valid time zone, a backdated entry), where success is the claim; two would pass even if the behaviour were wrong and were strengthened (an investment bucket's deposit is now checked to land in that bucket; filtering by an archived bucket now checks its entries are returned).
4. **Mutation testing** with Stryker.NET on the pure layers, where the money rules live: it changes the code in small ways (`>` to `>=`, a removed line, `All` to `Any`) and reports every change no test notices. Each survivor was classified as a missing test or an equivalent change.

## Gaps found and closed

| Where | What a broken version did that no test noticed | New test |
|---|---|---|
| `BudgetState` (`BucketBalance.Available`) | `NetContributed + OutstandingPrincipal` instead of `−`: the principal is always 0 until loans (phase 1b), so invariant 2's formula was unpinned exactly where 1b needs it | `Available_IsNetContributed_LessOutstandingPrincipal` (ARCHITECTURE 4.3's example) |
| `ReverseEntry` | `All` instead of `Any` for "touches an archived bucket": a transfer with one archived side could be reversed, putting money back into an archived bucket | `ReversingATransfer_ThatTouchesAnArchivedBucket_OnEitherSide_IsRejected` |
| `AddTransfer`, `ReverseEntry` validators | without their note, tag and amount rules: an invalid note would reach the domain and end as a server error instead of a field message | `EntryValidatorTests` (deposit, transfer and reversal alike) |
| `EntryRules` | ten tags refused (`<` instead of `<=`) | 10 valid, 11 too many |
| Every handler | ignoring a failed state read: a crash instead of `concurrency.conflict` | `StateKeepsChangingTests` (all ten handlers; each also writes nothing) |
| `EntryInfo` | `All` changed to `Any` for tags; a missing source; a 30-character tag refused | three domain tests |
| `Bucket.Rename`, `BucketId`, `JournalEntryId` | an invalid name, or an empty ID, accepted | domain tests |
| `ReplayPosition` | its `<`, `>`, `<=`, `>=` operators had no domain test (the journal cursor relies on them) | `ReplayPositionTests` |
| `GetJournalValidator`, `JournalCursor` | a period with only an end refused; a cursor with an impossible day or instant accepted; the earliest instant refused | validator and cursor tests |
| `CreateBucketValidator`, `GetBucketsHandler` | currency, kind or profile rules missing; buckets created in one instant listed in the store's order | validator test; same-instant order by ID |
| `AddTransfer` | a transfer to an unknown destination (the branch had no test at all) | `UnknownDestination_IsNotFound` |

## Survivors accepted, and why

- **Equivalent** (no test can tell them apart, because the program behaves the same): an early `return 0` for the same entry that the comparison returns anyway; `break` in a loop over unique bucket IDs; `FirstOrDefault … ?? throw` against `First` (both an `InvalidOperationException` for a bug); the cached replay of `BudgetSnapshot.State` computed again (speed only); `??=` against `=` for the missing-rate reason (every reason is `fx.rate_unavailable`); `<=` against `<` when two rate dates are equal; the two sides of `??` swapped where only one can be set (`JournalWriter`); a reordered decode check in `JournalCursor` already guaranteed by the exact-length and alphabet checks before it.
- **Covered elsewhere:** the ledger name `"budget"` is pinned by the MongoDB integration tests, which Stryker does not run for Application.
- **Skipped by configuration** (`stryker-config.json`): argument guards (`ThrowIfNull`, `ThrowIfNegativeOrZero`), `ConfigureAwait` (the Functions host has no synchronisation context) and the messages of exceptions, which only describe programming errors. String mutations stay on, because in production code strings are resource keys and error codes.

## Limits

- **Web project:** Stryker 5.1 cannot compile a Blazor WebAssembly project (it does not run the Razor source generators; even with every mutation removed its build fails). Its plain classes stay covered by the hand-made mutations of plan 1a steps 13 to 17 (29, all caught).
- **Infrastructure and Api:** not mutated; they need MongoDB and are covered by integration tests and the per-step mutations.
- Stryker's support for Microsoft.Testing.Platform is marked preview; every survivor above was checked by reading the code. Stryker's project analysis needs the target framework given explicitly (`target-framework` in the configuration), because this repository sets it in `Directory.Build.props`.

## How to run it again

```
dotnet tool restore
cd tests/FinTrack.Budget.Domain.Tests
dotnet stryker --config-file ../../stryker-config.json --project FinTrack.Budget.Domain.csproj
```

The same from `tests/FinTrack.Budget.Application.Tests` with `--project FinTrack.Budget.Application.csproj`. The HTML report is in `StrykerOutput/<time>/reports/` (ignored by git). Recommended before closing a phase, next to the per-step mutations.
