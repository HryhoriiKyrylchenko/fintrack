# Test audit after phase 1a (19 Nov 2026)

Between phase 1a and the 1b plan: are the tests reliable (no flaky tests), and do they check real behaviour (no tests that pass whatever the code does)? Branch `stage-1b/00-test-audit`, starting from `main` at `c9a5634` (1387 tests). Mutation testing first covered only the two Budget projects with 1a's rules; a second round (branch `stage-1b/00-test-audit-shared`, from `b0b6a1b`) added Stage 0's kernel and both Infrastructure projects, see [Second round](#second-round-stage-0-code-and-infrastructure).

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

## Second round: Stage 0 code and Infrastructure

The first round left out `Shared.Kernel` (money, currencies, the strict JSON contract, IDs: the most money-critical code), `Shared.Infrastructure` (storage format, ledger, outbox) and `Budget.Infrastructure`. The first report called the MongoDB projects not feasible, which was wrong: Stryker runs their integration tests against the local server (about 10 minutes each). The gaps below were closed with 26 new test cases (1466 tests now, one explicit test skipped).

| Project | Before | After | Survivors after |
|---|---|---|---|
| Shared.Kernel | 87.9 % (25 survived) | **91.8 %** | 17: 10 equivalent, 7 in setup that runs once per process (below) |
| Shared.Infrastructure | 84.4 % (39 survived, 34 not covered, 95 time-outs) | **80.2 %** (22 time-outs) | 61: 36 in setup that runs once per process, 9 log messages, the rest equivalent or accepted (below) |
| Budget.Infrastructure | 18.9 % (136 survived) | not run again | almost all in setup that runs once per process; sampled by hand (below) |

**Raw scores with MongoDB depend on the run.** The first two runs of Shared.Infrastructure on nearly the same code differed by 8 points: Stryker counts a mutant whose tests time out as killed, and the number of time-outs against the database varied (95, then 18). The gaps above come from reading every survivor of both runs and checking the doubtful ones by hand, not from the score.

**Setup that runs once per process shows as surviving.** Stryker builds every mutant into one assembly and switches mutants on while the tests run, in a test process that has already done its one-time setup: the BSON class maps (`registered = true`), the serializers and conventions, `FinTrackJson.Options`, the static `Currency` instances, the stored replay sort. A mutant there is never active when it matters, so it survives whatever the tests check. These survivors were checked by hand instead, each mutation applied to the source and the suite run:

- killed: an element name (`"ownerId"` emptied, 22 tests fail), the entry type name `"Deposit"` (4), the constructor lookup flags (87), the extra-elements convention (2), the decimal serializer (3), the Guid byte order (8), `"recordedAt"` dropped from the replay sort (1), the trailing-comma, duplicate-property and nullable JSON options (1 each), the `EUR` code (72), a validator left unregistered (2 Api tests);
- equivalent: `SetIsRequired` on a member set through the private constructor (the constructor mapping already rejects a missing argument), `SetIsRequired(true)` on an optional member (its default value wins), `SetIsRootClass` (the scalar `type` discriminator does not depend on it), `MakeReadOnly` on the JSON options (System.Text.Json locks them on first use).

### Gaps found and closed

| Where | What a broken version did that no test noticed | New test |
|---|---|---|
| `Money.Split` | each part as `Amount / weight / total`: with two parts the first part takes the rest, so both existing cases still passed | three unequal weights (20, 40, 40) |
| `Money` comparisons | `<` as `<=`, `>` as `>=`, `>=` as `>`: no test compared equal amounts | equal amounts with different scales |
| `UtcInstantJsonConverter` | an empty string read past its end (a crash instead of a rejection) | `""` is rejected |
| `LedgerName`, `Currency` | the shortest ledger name and the longest currency code refused | boundary cases |
| `CurrencyMismatchException` | a message without the currencies | the message names both |
| `RetryOnConflictBehavior` | `All` as `Any`: a conflict together with another failure was retried | not retried |
| `ValidationBehavior` | a rule on the whole message (empty property name) crashed | its field is empty |
| `MutableCollection.DeleteAsync` | a version below 1 accepted | throws, like `ReplaceAsync` |
| `MongoOptionsValidator` | a 63-character database name refused; a forbidden character at the start or a control character accepted; the messages emptied | boundary cases and the exact messages |
| `OutboxDispatcher` | a failed read of the outbox right after a commit thrown to the caller (the commit has already succeeded; the sweep delivers later): the branch had no test | dispatch against an unreachable server returns and logs |
| `OutboxDispatcher` | `&` as `\|` in the dispatch filter (other pending messages and already dispatched ones delivered too) and in the bookkeeping filter (another message of the same owner marked as dispatched): no test had two messages pending | dispatching one of two pending messages; dispatching an event twice |
| `OutboxDispatcher` sweep | `&=` as `\|=` in its paging: a failing message came back on every later page (more than once per sweep; with a page of failures, without end) | one failing message among more than a batch, tried once |
| `MongoLedger` | without its registration check, a commit to an unknown ledger created that ledger's collections on the fly, without indexes | throws and creates nothing |
| `LedgerDocuments`, `LedgerIndexInitializer` | ledger `testledger` accepting `testledger2_journal`; the sweep's `pending` index unnamed; collections created only where an index is (the existing check passed because earlier tests had created the collection) | boundary name; the outbox index; a fresh ledger's collections |
| `EntrySourceSerializer` | a stored source that is not a string: no test | rejected on read |

A test for `EventNameAttribute` on a derived type was added as well. Its mutant (`inherit: true`) is equivalent, because the attribute is also declared `Inherited = false`, but the rule (each stored event type names itself) is now pinned.

**Equivalent survivors in this round:** a removed `throw` where the next line rejects the value anyway (the JSON converters for a wrong token type; a GUID in the wrong format becomes `Guid.Empty`, which is rejected; the BSON serializers, whose errors the driver wraps in a `FormatException` in any case); `>>` as `>>>` on a byte (always positive); `ValidateOptionsResult.Fail` with no failures (the options factory throws only for messages); log messages; the `validators.Length == 0` shortcut (same result without it); the ledger's own owner check, which the collections repeat (defence in depth: both owner tests still throw and write nothing); `SingleOrDefault` as `FirstOrDefault` for the ledger head (one per owner by a unique index).

**Not covered by these test projects, covered elsewhere or accepted:** the FX options validation and `GetHealth` are tested by the Api integration tests; `LedgerCollections.Find` differs only when a type and its base type are both mapped, which no ledger does; a handler that fails with an error that is not an `AppError` (the convention forbids it); the defaults of `MongoOptions` (any default fails validation at start); concurrency races (an index created twice at once, a duplicate key between two rate writers) and a failed outbox bookkeeping write would need fault injection in MongoDB; a misconfigured owner context is a bug guard.

## Limits

- **Web project:** Stryker 5.1 cannot compile a Blazor WebAssembly project (it does not run the Razor source generators; even with every mutation removed its build fails). Its plain classes stay covered by the hand-made mutations of plan 1a steps 13 to 17 (29, all caught).
- **Api:** not mutated; it is thin by design (parse, send, map), and its integration tests caught the hand-made mutations of every step.
- **One-time setup** (second round, above) cannot be measured by Stryker; it is checked by hand and by the raw-BSON and JSON tests.
- Stryker's support for Microsoft.Testing.Platform is marked preview; every survivor above was checked by reading the code. Stryker's project analysis needs the target framework given explicitly (`target-framework` in the configuration), because this repository sets it in `Directory.Build.props`, and a checkout that has been built once (in a fresh clone its analysis fails).
- **Stryker leaves its mutated build in `bin/`.** An incremental build afterwards may keep it (one test failed for that reason during this audit). Rebuild with `--no-incremental` before running the tests or the app.

## How to run it again

```
dotnet tool restore
cd tests/FinTrack.Budget.Domain.Tests
dotnet stryker --config-file ../../stryker-config.json --project FinTrack.Budget.Domain.csproj
```

The same from `tests/FinTrack.Budget.Application.Tests` with `--project FinTrack.Budget.Application.csproj`, and from `tests/FinTrack.Shared.Tests` with `--project FinTrack.Shared.Kernel.csproj`. The Infrastructure projects need MongoDB running:

```
cd tests/FinTrack.Shared.Infrastructure.IntegrationTests
dotnet stryker --config-file ../../stryker-config.json --project FinTrack.Shared.Infrastructure.csproj --test-project FinTrack.Shared.Infrastructure.IntegrationTests.csproj --test-project ../FinTrack.Shared.Tests/FinTrack.Shared.Tests.csproj
cd ../FinTrack.Budget.Infrastructure.IntegrationTests
dotnet stryker --config-file ../../stryker-config.json --project FinTrack.Budget.Infrastructure.csproj
cd ../..
dotnet build --no-incremental
```

When a mutant makes a test time out, Stryker ends the test process before the fixture drops its database, so test databases (`fintrack_it_<date>_<time>_<id>`) stay behind (220 after this audit). Drop them afterwards, and only them:

```
mongosh "mongodb://localhost:27017/?replicaSet=rs0" --quiet --eval 'db.adminCommand({listDatabases:1,nameOnly:true}).databases.map(d => d.name).filter(n => /^fintrack_it_\d{8}_\d{6}_[0-9a-f]{16}$/.test(n)).forEach(n => db.getSiblingDB(n).dropDatabase())'
```

The HTML report is in `StrykerOutput/<time>/reports/` (ignored by git). Recommended before closing a phase, next to the per-step mutations.
