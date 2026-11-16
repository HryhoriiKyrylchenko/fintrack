# FinTrack Stage 0 (Foundation): implementation plan

## Context

FinTrack is at Phase 0. The repository holds only documents and config (`CLAUDE.md`, `docs/ARCHITECTURE.md`, `docs/DESIGN.md`, `.editorconfig`, `.gitignore`, `.gitattributes`, an empty `FinTrack.sln`). Stage 0 builds the foundation that Phase 1a (buckets, journal, balances) sits on, so that 1a can start directly with business slices.

Status: approved 15 Nov 2026, implementation not started. Tick a step's `[ ]` when its pull request is merged.

## Stage 0 scope

Source: `docs/ARCHITECTURE.md` §8, `CLAUDE.md` "Current phase".

- **In:** solution skeleton; Shared kernel (`Money`, `Currency`, IDs, `IClock`, `Result` helpers); MongoDB adapter (transactions, optimistic concurrency, outbox); local run script; CI build and test; test projects.
- **Agreed extension (walking skeleton):** health function, `Result` → `ProblemDetails` mapper, Mediator pipeline behaviours, outbox sweep timer, Web shell with theme, localization and one status page.
- **Out:** buckets, journal entry types, balances, any business screen (1a); plans, limits, loans (1b); FX and price providers; cloud, Bicep, login; upcasters; stored projections; Investments and Integrations projects; anything AI.

## Decisions taken (your answers)

1. Three projects beyond the `CLAUDE.md` layout: `src/Shared/FinTrack.Shared.Infrastructure`, `tests/FinTrack.Shared.Tests`, `tests/FinTrack.Shared.Infrastructure.IntegrationTests`.
2. Only the Budget context is created now. Investments and Integrations projects are added in Phases 3 and 5.
3. Property-based testing: **CsCheck** (Apache-2.0).
4. Api and Web are built as a walking skeleton, not empty shells.
5. The kernel project is named `src/Shared/FinTrack.Shared.Kernel` (namespace `FinTrack.Shared.Kernel`), not `FinTrack.Shared`, so it is not confused with its sibling `FinTrack.Shared.Infrastructure`. The unit test project stays `tests/FinTrack.Shared.Tests` (it covers the whole Shared area).
6. Inter is self-hosted under its OFL-1.1 licence: an accepted exception to the MIT/Apache/BSD rule, for fonts only, recorded in the decision log.
7. Git workflow: one branch per step (`stage-0/NN-short-name`), one pull request into `main`, merged only when CI is green. Nothing is committed directly to `main`.
8. Branch protection on `main` is already enabled. The required status check can only be selected after the CI workflow has run once, so that is done and verified in step 4.

## Contradictions found, and how the plan resolves them

| # | Issue | Resolution (recorded in the spec in step 1) |
|---|---|---|
| 1 | Layout puts "outbox" in `FinTrack.Shared`, but Domain references Shared and must not reference MongoDB or Mediator. | The kernel, renamed `FinTrack.Shared.Kernel`, holds only plain abstractions (FluentResults is its single package). Mongo and Mediator code lives in `FinTrack.Shared.Infrastructure`. |
| 2 | Rule 9 lists four document fields; §2.2 adds `CreatedAt`, `UpdatedAt`, soft delete. `Version`/`UpdatedAt` make no sense on append-only documents. | Mutable documents: `Id`, `OwnerId`, `SchemaVersion`, `Version`, `CreatedAt`, `UpdatedAt`. Append-only documents (journal, outbox, processed events): `Id`, `OwnerId`, `SchemaVersion`, `RecordedAt`. |
| 3 | "Fiat 2 decimals" is wrong for some currencies (JPY has 0). | `Currency` carries its own `DecimalPlaces` (0 to 18). PLN, EUR, USD registered first. |
| 4 | Names of the outbox, `LedgerHead` and processed-event collections are not in the spec. | `budget_outbox`, `budget_ledger_heads`, `budget_processed_events` (same pattern for later contexts). |
| 5 | "Every query is filtered by `OwnerId`" versus the outbox sweep timer, which must find pending events of all owners. | One documented exception: the sweep reads pending outbox messages across owners, then handles each message under that message's `OwnerId`. |
| 6 | `CLAUDE.md` layout lists Investments/Integrations; spec says "no dead code". | Layout annotated: those projects are created in their phases. |

## Assumptions (accepted with the plan)

- Keep the classic `FinTrack.sln` (already committed) rather than `.slnx`.
- Central package management (`Directory.Packages.props`) and `global.json` pinning SDK 10.0.4xx with `rollForward: latestFeature`.
- Default analyzer set (no `AnalysisMode=All`).
- HTTP functions use the **ASP.NET Core integration** of the isolated worker (`HttpRequest`, `IActionResult`). This is not a long-running ASP.NET host: the Functions host still starts and stops the process per demand; only the request types come from ASP.NET Core. It makes `ProblemDetails` straightforward.
- `OwnerId` is a strongly typed wrapper over `Guid`. Local mode uses one fixed owner from configuration behind `IOwnerContext`; the cloud phase maps the token's `sub` to an `OwnerId`.
- `IClock` exposes `UtcNow` as `DateTimeOffset`; `OccurredOn` is a `DateOnly`.
- `Money` arithmetic on different currencies throws (`CurrencyMismatchException`): it is a programming error, not a business rule failure. Parsing or creating money from user input returns `Result`.
- BSON: element names camelCase, `Guid` as standard UUID (subtype 4), `decimal` as `Decimal128`, enums as strings, `DateOnly` as ISO string, unknown elements rejected (strict), `Money` as `{ amount, currency }`. Added in step 10: `DateTimeOffset` as a BSON date in UTC with whole milliseconds.
- Integration tests reach MongoDB through `FINTRACK_TEST_MONGO` (default `mongodb://localhost:27017/?replicaSet=rs0`), one uniquely named database per test run, dropped afterwards. No Testcontainers (would make Docker a requirement).
- Scripts are PowerShell 7 in `scripts/` (works on Windows, Linux and macOS).
- CI runs on `ubuntu-latest`; MongoDB there is a container started as a single-node replica set. Docker in CI does not make Docker a local requirement.
- MongoDB server: 8.0 is the minimum supported version (the Atlas free cluster) and the version CI tests; a newer local server is allowed (this machine runs 9.0.8, verified in step 9). Note: MongoDB Community Server is SSPL. It is a separately installed server, not a linked dependency, so the "permissive dependencies" rule (which covers packages) is not broken; the driver is Apache-2.0.
- Test helpers shared by several test projects (`FakeClock`) are linked source files under `tests/Common/`, not a new project.
- No upcasters and no index beyond what the adapter needs: only the `SchemaVersion` field and the extension point exist.

## Open questions

None. The three earlier ones (Inter licence, git workflow, branch protection) are answered in decisions 6 to 8.

Two things to confirm at kick-off, neither changes the plan:

- Who merges each pull request: you, or Claude on your instruction once CI is green.
- The protection rule must not require an approving review, otherwise a solo developer cannot merge their own pull request.

## Packages (latest stable on NuGet, checked 15 Nov 2026)

| Package | Version | Licence | Used in |
|---|---|---|---|
| FluentResults | 4.0.0 | MIT | Shared.Kernel and above |
| Mediator.Abstractions / Mediator.SourceGenerator | 3.0.2 | Licence file in package; confirm MIT in step 2 | Application, Shared.Infrastructure / Api |
| FluentValidation | 12.2.0 | Apache-2.0 | Application, Shared.Infrastructure |
| MongoDB.Driver | 3.14.0 | Apache-2.0 | Shared.Infrastructure, Budget.Infrastructure |
| Microsoft.Azure.Functions.Worker (+ Sdk, Http.AspNetCore, Timer) | 2.52.0 / 2.1.0 / 2.1.1 / 4.3.1 | MIT | Api |
| MudBlazor | 9.13.0 | MIT | Web |
| xunit.v3 | 4.0.3 | Apache-2.0 | tests (on Microsoft.Testing.Platform; see step 2 note) |
| Shouldly | 4.3.0 | BSD-3-Clause | tests |
| CsCheck | 5.0.1 | Apache-2.0 | tests |

Every step that adds a package re-checks its licence and version first.

## Project reference graph (target)

```
FinTrack.Shared.Kernel             -> (FluentResults only)
FinTrack.Budget.Contracts          -> Shared.Kernel
FinTrack.Budget.Domain             -> Shared.Kernel
FinTrack.Budget.Application        -> Budget.Domain, Budget.Contracts, Shared.Kernel
FinTrack.Shared.Infrastructure     -> Shared.Kernel
FinTrack.Budget.Infrastructure     -> Budget.Application, Shared.Infrastructure
FinTrack.Api                       -> Budget.Application, Budget.Infrastructure, Budget.Contracts, Shared.Infrastructure
FinTrack.Web                       -> Budget.Contracts, Shared.Kernel
```

## Steps

Complexity: **C** = complex and architecturally significant (strongest model), **M** = moderate, **R** = routine.

Every step is one branch `stage-0/NN-short-name` and one pull request into `main`. "Done" always includes: the pull request's CI run is green (from step 4 on) and it is merged.

### [x] Step 1. Record Stage 0 decisions in the docs (R)
- **Goal:** spec and `CLAUDE.md` match what will be built.
- **What we do:** edit `docs/ARCHITECTURE.md` (§2.2 document fields, §2.3 collection names and the sweep exception, §3 currency decimals, §10 decision log including the Inter OFL-1.1 exception, §11 libraries: CsCheck) and `CLAUDE.md` (layout with `FinTrack.Shared.Kernel` and the three new projects, Investments/Integrations marked "created in their phase", rule 9 wording, CsCheck, the font exception in the licence rule, the branch-per-step workflow).
- **Done when:** both files contain every row of the contradictions table; no code changed.
- **Depends on:** nothing.
- **Commit:** `docs: record stage 0 decisions`.

### [x] Step 2. Build configuration (R)
- **Goal:** shared build rules exist before any project.
- **What we do:** root `Directory.Build.props` (`net10.0`, `Nullable`, `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `GenerateDocumentationFile`, `ImplicitUsings`), `Directory.Packages.props` (central versions from the table), `global.json`, `tests/Directory.Build.props` (test packages, linked `tests/Common/*.cs`).
- **Done when:** `dotnet build FinTrack.sln` succeeds; licence of the Mediator package confirmed.
- **Depends on:** 1.
- **Result:** Mediator.Abstractions and Mediator.SourceGenerator 3.0.2 confirmed MIT (licence file in the package). Deviation from the package table: `xunit.v3` 4.x runs only on Microsoft.Testing.Platform v2, which the .NET 10 SDK no longer runs through VSTest. `global.json` therefore selects `Microsoft.Testing.Platform` as the test runner, and `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are not used. Verified with throwaway probe projects (deleted, not committed): tests run, and unused usings, block namespaces, nullable warnings and package versions outside `Directory.Packages.props` all fail the build. The empty solution still reports "Unable to find a project to restore!" until step 3 adds projects.

### [x] Step 3. Solution skeleton and architecture guard tests (M)
- **Goal:** all Stage 0 class libraries exist with the reference graph above, and tests fail if the graph is broken.
- **What we do:** create `src/Shared/FinTrack.Shared.Kernel`, `src/Shared/FinTrack.Shared.Infrastructure`, the four `src/Budget/*` projects, `tests/FinTrack.Shared.Tests`, `tests/FinTrack.Budget.Domain.Tests`, `tests/FinTrack.Budget.Application.Tests`; add all to `FinTrack.sln` in solution folders. Each library gets an `AssemblyMarker`. Guard tests: `FinTrack.Shared.Tests/Architecture/ProjectReferenceRulesTests` parses every `.csproj` and checks project and package references against an allow-list (rules 3 and 4); the Budget test projects assert their assembly references no MongoDB, Functions or (for Domain) Mediator assemblies.
- **Done when:** `dotnet build` and `dotnet test` pass; adding a forbidden reference locally makes a guard test fail (tried once, then reverted).
- **Depends on:** 2.
- **Notes:** `Api`, `Web` and the two integration test projects are created in the steps that give them content, so no project sits empty.
- **Result:** six libraries and three test projects, 62 tests. The rules live in `tests/FinTrack.Shared.Tests/Architecture/ArchitectureRules.cs` (project references by role, package allow-list per role, packages forbidden everywhere: MediatR, FluentAssertions, Cosmos DB) and are tested as a table, including the not-yet-existing Api, Web and a second context. Compiled-assembly checks (`tests/Common/AssemblyGraph.cs`) cover the kernel, Budget Domain and Budget Application. Projects get packages only in the step that first uses them, so the kernel has no FluentResults reference yet. Verified: a forbidden project reference and a forbidden package used in code each fail the tests; both were reverted.

### [x] Step 4. CI: build and test (R)
- **Goal:** every push and pull request is built and tested.
- **What we do:** `.github/workflows/ci.yml`: checkout, `setup-dotnet` from `global.json`, NuGet cache, `dotnet build -c Release`, `dotnet test -c Release`.
- **Done when:** the workflow is green on this step's pull request; the CI job is then added as a required status check in the existing protection rule for `main`; a deliberately failing test on a throwaway branch shows the merge button blocked (branch deleted afterwards).
- **Depends on:** 3.
- **Notes:** GitHub lists a status check for selection only after it has run once, which is why the rule can be completed only here. Steps 1 to 3 are merged through pull requests without a required check. MongoDB is added to the workflow in step 10. Selecting the check is a repository setting: you do it in the GitHub UI, or Claude does it through `gh` if you ask.
- **Result:** `.github/workflows/ci.yml` runs on pull requests, pushes to `main` and on demand (step branches are covered by their pull request, so pushes to them do not start a second run). First run on PR #4: SDK from `global.json`, 0 warnings, 62 tests passed on `ubuntu-latest` in 31 s. Protection is the repository ruleset "Protect main - Require CI Checks", which had been created with enforcement disabled and a required check named `tests` that no job produces; it was changed to require `Build and test` and set to active (other rules unchanged: no deletion, no force push, pull request required with 0 approvals, branch up to date before merging). Verified with throwaway PR #5 (a test that always fails): CI failed and the ready-for-review PR showed `BLOCKED` while PR #4 showed `CLEAN`; PR #5 was closed and its branch deleted. actionlint was not run (the Docker image was not permitted).

### [x] Step 5. Shared kernel: IDs and clock (R)
- **Goal:** UUIDv7 identifiers and time abstraction.
- **What we do (tests first):** `FinTrack.Shared.Kernel/Ids` (`OwnerId`, a small pattern for strongly typed IDs, `IdGenerator` using `Guid.CreateVersion7(clock.UtcNow)`, a check that a client-supplied ID is version 7); `FinTrack.Shared.Kernel/Time` (`IClock`, `SystemClock`); `tests/Common/FakeClock.cs`.
- **Done when:** tests prove IDs are version 7, time-ordered and unique; the invalid-ID check returns `Result.Fail`.
- **Depends on:** 3.
- **Result:** pattern `IStronglyTypedId<TSelf>` (`readonly record struct`, private constructor, `Value`, static `From` that throws on `Guid.Empty`); `OwnerId` (non-empty only, not required to be version 7); `UuidV7.IsValid` (version 7 and RFC variant); `StronglyTypedId.FromClient<TId>` returning `Result.Fail` with code `id.invalid` (replaced by `AppError` in step 7); `IdGenerator(IClock)` with no interface, since tests use it with `FakeClock`. 22 new tests including CsCheck properties; two deliberate code mutations were each caught by the tests. FluentResults 4.0.0 (MIT) added to the kernel; it brings `Microsoft.Extensions.Logging.Abstractions` 2.1.1 and `System.Threading.Tasks.Extensions` 4.5.4 (both MIT) transitively into every project that references the kernel. Within one millisecond the order of generated IDs is random (.NET does not add a counter).

### [x] Step 6. Shared kernel: `Currency` and `Money` (C)
- **Goal:** the money type every later phase relies on.
- **What we do (tests first):** `Currency` (code, `DecimalPlaces`, registry PLN/EUR/USD, `FromCode` → `Result`); `Money` (`decimal` + `Currency`; creation rejects more decimals than the currency allows; `+`, `−`, comparison throw on currency mismatch; explicit banker's rounding; explicit `Convert(rate, target)`; `Split` whose parts sum exactly to the original with the rounding remainder on the first line). CsCheck property tests: split sums exactly (invariant 9 groundwork), add/subtract round trip, rounding stays at currency precision, mismatch always throws (invariant 11).
- **Done when:** unit and property tests pass; no `double`/`float` anywhere.
- **Depends on:** 5.
- **Risks:** this API is used everywhere; changing it later is expensive. Review the public surface before committing.
- **Result:** namespace `FinTrack.Shared.Kernel.Monetary`. `Currency` is a closed registry (PLN, EUR, USD; code of 2 to 10 upper-case letters or digits, 0 to 18 decimal places), `FromCode` fails with `currency.unknown`; tests reach an internal `Currency.Define` through `InternalsVisibleTo`. `Money` is an immutable record: `Create` (untrusted, fails with `money.too_many_decimals`, never rounds silently), `From` (trusted, throws), `Zero`, `Round` (banker's), `Convert(rate, target)`, `Split(weights)`, `+`, binary and unary `−`, `<`, `>`, `<=`, `>=`; mixing currencies throws `CurrencyMismatchException`, equality across currencies is false. Decision taken with the developer: `Split` truncates each part toward zero and adds the remainder to the first part, so parts never flip sign (ARCHITECTURE section 3 and decision log updated). 71 new tests; the seven CsCheck properties also ran for 10 to 15 seconds each (millions of cases). Three deliberate mutations (banker's rounding in `Split`, `+` without the currency check, `Create` rounding silently) were each caught. No `double` or `float` in `src`. Lesson for later steps: `dotnet build -q` means `--question` (checks only, builds nothing), not quiet; a long run once tested a stale build because of it.

### [x] Step 7. Shared kernel: `Result` helpers and error codes (R)
- **Goal:** one way to express a failure with a stable code.
- **What we do (tests first):** `FinTrack.Shared.Kernel/Results`: `AppError : FluentResults.Error` with `Code`, `Kind` (`Validation`, `NotFound`, `Conflict`, `BusinessRule`) and parameters; factory helpers; shared codes (`validation.failed`, `concurrency.conflict`, `id.invalid`, `currency.unknown`, `money.too_many_decimals`). Replace the ad hoc failures from steps 5 and 6.
- **Done when:** tests pass; every `Result.Fail` in the kernel carries an `AppError`.
- **Depends on:** 6.
- **Result:** `AppError` (private constructor; factories `Validation`, `NotFound`, `Conflict`, `BusinessRule` taking `(name, value)` parameters). `Message` is the code. Codes must be lower-case dot-separated segments (at least two); parameter names camelCase, unique; values only `string`, `bool`, `int`, `long`, `decimal`; violations throw (bugs). `ErrorCodes` holds the five shared codes. The three earlier failures now carry `AppError`: `id.invalid` (no parameters), `currency.unknown` (`currency` as sent), `money.too_many_decimals` (`currency`, `maxDecimalPlaces`; never the amount). `ErrorConventionTests` fails the build's tests when any file in `src` creates a plain FluentResults error or fails with a string. Test helper `tests/Common/ResultAssertions.ShouldFailWith(code, kind)`. 41 new tests; three deliberate mutations caught. Rule: log an error's code, never its parameters.

### [x] Step 8. Shared kernel: persistence and event abstractions (C)
- **Goal:** contracts that Application code will use, with no MongoDB or Mediator types.
- **What we do:** in `FinTrack.Shared.Kernel`: `IOwnerContext`; document interfaces for the two shapes (mutable, append-only); `IIntegrationEvent` (`EventId`, `OwnerId`, `OccurredAt`), `IIntegrationEventHandler<T>`, a stable event-name attribute (names never derive from CLR type names, because stored events outlive refactorings); the ledger write contract (`ILedgerWriter`: run a unit of work for one owner and context with an expected `LedgerHead.Version`, append outbox events, result is success or `concurrency.conflict`); marker `ILedgerCommand` for commands that need retry on conflict.
- **Done when:** builds; guard tests still pass; contract documented with XML comments since it is the seam for the future projection cache and app split.
- **Depends on:** 7.
- **Risks:** the shape of `ILedgerWriter` fixes rule 7 for the whole project. Design it against the 1a write path (load journal → build state → validate → commit) before coding.
- **Result:** design approved by the developer before coding, with four decisions (recorded in ARCHITECTURE 2.3 and the decision log, and in CLAUDE.md rule 7):
  1. `ILedger` replaces the planned `ILedgerWriter`: `GetVersionAsync(ledger)` (read **before** the journal) and `CommitAsync(LedgerCommit)` → `Result<LedgerCommitted>` or `Conflict` `concurrency.conflict`.
  2. Domain types are stored directly: they implement `IDocument` / `IAppendOnlyDocument` / `IMutableDocument`; MongoDB mapping lives in Infrastructure.
  3. `LedgerCommit` carries `Appends`, `Saves` (mutable documents: version 0 inserts, otherwise replace if the stored version matches), `Events` and `HandledEvent`.
  4. `HandledEvent` (`ProcessedEvent(handlerName, eventId)`) is written in the handler's commit; a repeated delivery returns `AlreadyHandled = true` and writes nothing.
  Also: `IOwnerContext` is the only source of the owner (documents or events of another owner make the commit throw); `LedgerName` (2 to 20 lower-case letters or digits, also the collection prefix); `IIntegrationEvent`, `IIntegrationEventHandler<TEvent>`, `[EventName]` with `EventNameAttribute.Of(type)`; `ILedgerCommand` marker. Error codes, event names and handler names share one format (internal `StableName`). 34 new tests; three deliberate mutations of `LedgerCommit` caught (one first attempt did not compile and was redone).

### [x] Step 9. Local environment (R)
- **Goal:** the machine can run MongoDB with transactions, Azurite and the Functions host.
- **What we do:** MongoDB and Azurite are already installed, so nothing is installed for them. Verify the existing MongoDB runs as a replica set (`replication.replSetName` in `mongod.cfg`, `rs.status()` shows a primary); if it is a standalone instance, add the setting, restart the service and run `rs.initiate()` once. Check that Azure Functions Core Tools v4 is present and install it only if it is not. Add `docs/LOCAL-SETUP.md` (what a new machine needs) and `scripts/check-prereqs.ps1` (reports each tool and the replica-set state, looking in the default install locations as well as PATH). Optional convenience `docker-compose.yml` for MongoDB and Azurite.
- **Done when:** `scripts/check-prereqs.ps1` reports everything present and a primary replica-set member.
- **Depends on:** nothing (needed before 10).
- **Notes:** unlike SQL Server, MongoDB allows multi-document transactions only on a replica set, hence the one-node set. Data directory stays outside the repository. `mongod`, `mongosh`, `azurite` and `func` are not on the PATH of this machine's shells, so the check script and the run script must not assume PATH. Changing `mongod.cfg` and restarting the service is done only with your confirmation.
- **Result:** nothing on the machine was changed or installed. Found: MongoDB 9.0.8 as the Windows service `MongoDB` (`C:\Program Files\MongoDB\Server\9.0`), `mongod.cfg` already has `replSetName: rs0`, and `rs.status()` shows one healthy `PRIMARY`; mongosh 2.14.2, Azurite 3.37.0, Functions Core Tools 4.16.1, Node.js 24.21.0 and Docker 29.8.2 are installed, and all but `mongod` are on the user PATH (the earlier note that they were not was wrong). Decision taken with the developer: MongoDB 8.0 is the minimum supported version and the CI version (step 10 uses `mongo:8.0`), local servers may be newer (ARCHITECTURE 2.2 and decision log). Added `scripts/check-prereqs.ps1` (PowerShell 7; PATH first, then default locations on Windows, Linux and macOS; checks the .NET SDK against `global.json`, `mongod` 8.0+, mongosh, the replica set through `db.hello()`, `func` 4.x, Node.js and Azurite; Azurite running and Docker are information only; exit code 1 on any problem), `docs/LOCAL-SETUP.md` and an optional `docker-compose.yml` (MongoDB 8.0 replica set that initiates itself, Azurite 3.37.0, localhost only). Verified by running the script: all `OK` on this machine, also with PATH reduced to Windows and pwsh (tools found in default locations). Failure cases each reported correctly with exit code 1: wrong port (unreachable), a throwaway standalone `mongod` (standalone), a throwaway `mongod --replSet` without `rs.initiate()` (not initiated), a wrong set name, a `global.json` asking for a newer SDK, and Azurite without Node.js on PATH (the last one was found by the test and led to the Node.js row). Not verified: `docker compose up` (only `docker compose config`, because the containers would clash with the running service on port 27017), Linux and macOS lookup paths, and PSScriptAnalyzer (not installed).

### [x] Step 10. Mongo foundation and serialization (C)
- **Goal:** a configured connection and a fixed storage format.
- **What we do:** in `FinTrack.Shared.Infrastructure/Mongo`: `MongoOptions`, singleton `MongoClient` registration, conventions and serializers from the assumptions (camelCase, UUID standard, `Decimal128`, enum strings, `DateOnly`, `Money`, strongly typed IDs, strict unknown elements), registered once. Create `tests/FinTrack.Shared.Infrastructure.IntegrationTests` with the database fixture. Add the MongoDB replica-set container (`mongo:8.0`, the minimum supported version) to `ci.yml`.
- **Done when:** round-trip tests pass locally and in CI, including 18-decimal amounts and UUIDv7 ordering.
- **Depends on:** 8, 9, 4.
- **Risks:** the journal lives forever, so these formats are effectively permanent. Unlike EF Core there are no migrations: a format change later means an upcaster.
- **Result:** Atlas free clusters run MongoDB 8.0 (Atlas documentation, checked 16 Nov 2026), so CI uses `mongo:8.0`, started by a `docker run` step as a single-node replica set (GitHub service containers cannot pass `--replSet`); `FINTRACK_TEST_MONGO` is set for the job. Decision taken with the developer: instants (`DateTimeOffset`) are BSON dates, UTC, whole milliseconds; `IClock` promises whole milliseconds, `SystemClock` truncates, `FakeClock` rejects anything finer, and the serializer refuses an offset or a sub-millisecond part (ARCHITECTURE 2.2, decision log, CLAUDE.md rule 11). In `FinTrack.Shared.Infrastructure/Mongo`: `MongoOptions` with a validator (messages never contain the connection string), `AddFinTrackMongo` (singleton `IMongoClient` with application name `FinTrack`, `IMongoDatabase`), `MongoSerialization.Register` (once per process, thread-safe) with serializers for `Guid`, `decimal`, `DateOnly`, `DateTimeOffset`, `Currency`, `Money` (also tells LINQ where `Amount` and `Currency` live) and providers for every strongly typed ID and every FinTrack enum; conventions (camelCase, strict unknown elements) apply to FinTrack types only. Finding during the tests: the driver's own decimal and enum serializers accept a stored double or a number and convert them silently, so both were replaced by strict serializers (decision log). Packages: `Microsoft.Extensions.Options` and `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.15 (MIT) in Shared.Infrastructure, `Microsoft.Extensions.DependencyInjection` 10.0.15 (MIT) in the new test project; MongoDB.Driver 3.14.0 is still the latest (Apache-2.0). New project `tests/FinTrack.Shared.Infrastructure.IntegrationTests` with an assembly fixture (one database per run, unique name, dropped afterwards, clear failure when MongoDB is missing or not a replica set). 58 new tests (55 integration, 3 clock): raw stored format, round trips including 18-decimal and extreme decimals with their scale (500 generated values through the server, a CsCheck property in memory), UUIDv7 order by `_id` and range queries on `_id`, LINQ filters on owner, money, enum, date and instant, 17 kinds of corrupted documents, write guards, options validation. Three deliberate mutations caught (sub-millisecond check removed, typed IDs in the legacy UUID format, decimals read from doubles); one first attempt was placed after the type check, could never run, and was redone. Class maps for real domain types (private constructors) are left to the steps that create them.

### [x] Step 11. Owner-scoped collections and optimistic concurrency (C)
- **Goal:** `OwnerId` isolation cannot be forgotten; stale updates are rejected.
- **What we do (tests first):** `OwnerScopedCollection<T>`: every filter is combined with the current `OwnerId`, inserts are stamped and checked, replace of a mutable document requires the expected `Version` and increases it (sets `UpdatedAt` from `IClock`); the raw collection is not exposed. An index initializer contract that each context implements, run once per process start.
- **Done when:** integration tests show owner A cannot read, update or delete owner B's document, and a stale `Version` yields `concurrency.conflict` (invariant 12).
- **Depends on:** 10.
- **Notes:** MongoDB has no rowversion or `SaveChanges` concurrency token; the check is the filter of the update itself.
- **Result:** in `FinTrack.Shared.Infrastructure/Mongo`: abstract `OwnerScopedCollection<T>` (`GetAsync`, `FindAsync` with expression or filter and optional sort, `InsertAsync`; every filter is ANDed with `ownerId` from `IOwnerContext`; writing another owner's document throws; a duplicate `_id` returns `Conflict` `concurrency.conflict`; every method takes an optional session for step 12) with two sealed types: `AppendOnlyCollection<T>` (insert and read only, so rule 5 is enforced by the type) and `MutableCollection<T>` (insert requires `Version` 0 and stores version 1 with `CreatedAt`/`UpdatedAt` from `IClock`; `ReplaceAsync` and `DeleteAsync` filter on `_id`, `ownerId` and the expected version, store version + 1 and a new `UpdatedAt`, and return `concurrency.conflict` when nothing matches; document objects are never changed). Stored element names and values come from the document's own serializer (`DocumentElements`), so raw filters match the stored format. The raw collection is not exposed (a reflection test guards it). `IMongoIndexInitializer` per context, registered with `AddMongoIndexInitializer<T>()`; `MongoIndexRunner` (registered by `AddFinTrackMongo`) runs them once per process, concurrent callers share the run, a failed run is retried on the next call. Decisions recorded in ARCHITECTURE (decision log) and CLAUDE.md (conventions): contexts use only these collection types; a duplicate `Id` is a conflict. 29 new integration tests (84 integration tests, 317 tests in total): owner B cannot read, find, replace or delete owner A's document; stale versions conflict on replace and delete; ten concurrent replaces of one version give exactly one success; a write inside an aborted transaction leaves nothing; indexes are created once. Three deliberate mutations caught (reads without the owner filter, replace and delete without the version, replace and delete without the owner). Who calls `MongoIndexRunner` at start-up is decided in step 15.

### [x] Step 12. Ledger transaction: `LedgerHead` and atomic commit (C)
- **Goal:** rule 7 implemented once: journal entries + head version + outbox in one transaction.
- **What we do (tests first):** `MongoLedger` implementing `ILedger` (step 8): `GetVersionAsync`; `CommitAsync` with a session and driver transaction (built-in retry of transient errors): conditional increment of the `LedgerHead` (`ownerId` unique; first write creates it; duplicate key or zero modified → conflict), inserts of `Appends`, insert or version-checked replace of `Saves`, outbox inserts for `Events`, insert of the `HandledEvent` record (unique handler + event; a duplicate means already handled → `AlreadyHandled`, nothing written), commit; documents or events of another owner throw. Tests use test-only collections.
- **Done when:** integration tests prove: failure midway persists nothing; two concurrent writers with the same expected version produce exactly one success; version rises by one per commit; outbox documents exist only for committed work; a stale saved document conflicts; a repeated handled event writes nothing.
- **Depends on:** 11.
- **Risks:** the most important step of Stage 0. Collections and indexes are created before use, not inside a transaction.
- **Result:** two additions approved by the developer before coding: `LedgerDocuments` (each context maps its document types, or a base type for subclasses, to collections named `<ledger>_...`; reserved and duplicate names, a type in two ledgers and a ledger registered twice are rejected) and `OutboxMessage` created here rather than in step 13 (`Id` = `EventId`, `EventName`, `Payload` in the storage format, `Attempts`, `DispatchedAt`, `LastErrorCode`), because step 12 already stores events. In `FinTrack.Shared.Infrastructure/Ledger`: `MongoLedger` (bugs such as another owner's document or event, an unmapped type or another context's type throw before the transaction starts; one transaction through the driver's `WithTransactionAsync` with snapshot read concern and majority write concern; order: handled-event record, `LedgerHead`, appends and saves through the step 11 collections, outbox; any conflict aborts and returns `concurrency.conflict`, an exception aborts as well), `LedgerHead`, `ProcessedEventRecord`, `LedgerCollections`, `LedgerIndexInitializer` (creates every ledger and mapped collection, a unique `ownerId` index on heads and a unique (`ownerId`, `handlerName`, `eventId`) index on processed events) and `AddMongoLedger` / `AddLedgerDocuments` (scoped `ILedger`). 35 new integration tests (119 integration tests, 352 in total): versions rise by one per commit; stale and future expected versions conflict and write nothing; a stale saved document and an exception midway both leave nothing; ten concurrent writers, for the first commit and for a later one, give exactly one success with one journal entry and one outbox message; a repeated handled event writes nothing, also with a stale version; two handlers of one event are recorded separately; a handled event in a conflicting commit is not recorded; owners' versions are independent; the indexes exist. The ledger tests ran ten times in a row without a failure. Three deliberate mutations caught (head replaced without the version comparison, documents written outside the transaction, a failed document write skipped). Found for Phase 1a: a subclass stored in its base type's collection gets the driver's default discriminator `_t`, which is the CLR class name; journal entry subtypes need explicit, stable discriminators (added to the deferred list).

### [ ] Step 13. Outbox dispatch and idempotent handlers (C)
- **Goal:** events reach handlers at least once, with effect exactly once.
- **What we do (tests first):** `OutboxMessage` document (created in step 12); event-name registry; `OutboxDispatcher` (in-process dispatch right after commit; `DispatchPendingAsync` for the sweep; each message handled under its own owner through `IOwnerContext`). The processed-event record itself is written by `MongoLedger` (step 12) through `LedgerCommit.HandledEvent`; this step proves the end-to-end behaviour with test handlers.
- **Done when:** integration tests prove: dispatch after commit; an undispatched message is delivered by the sweep; a duplicate delivery changes nothing; a failing handler leaves the message pending with attempts increased (invariant 13). Logs contain IDs and codes only.
- **Depends on:** 12.

### [ ] Step 14. Mediator pipeline behaviours (M)
- **Goal:** validation and retry-on-conflict happen outside handlers.
- **What we do (tests first):** in `FinTrack.Shared.Infrastructure/Messaging`: `ValidationBehavior` (FluentValidation failures → `Result.Fail` with `validation.failed` and field parameters), `RetryOnConflictBehavior` (for `ILedgerCommand`, bounded attempts, then `concurrency.conflict`). Unit tests in `FinTrack.Shared.Tests` call the behaviours directly.
- **Done when:** tests pass for valid, invalid, conflict-then-success and conflict-exhausted cases.
- **Depends on:** 8 (13 not required).
- **Notes:** verify the Mediator 3.x pipeline signature in its docs before coding.

### [ ] Step 15. Api: Functions app walking skeleton (C)
- **Goal:** a running Function App with the cross-cutting pieces every later function reuses.
- **What we do:** create `src/Api/FinTrack.Api` (isolated worker, ASP.NET Core integration), `Program.cs` composition (Mongo, clock, `LocalOwnerContext`, Mediator with scoped lifetime, behaviours, index initializers), `host.json`, `local.settings.example.json` (invented values, CORS for the Web origin). `ProblemDetailsMapper` (`Result` → RFC 7807 with `code` and `params`; `Kind` → 400/404/409/422; no user sentences). Functions: `GetHealth` (`GET /api/v1/health`, pings MongoDB) and `SweepOutbox` (timer → `DispatchPendingAsync`). Create `tests/FinTrack.Api.IntegrationTests`: mapper tests and the health function against real MongoDB.
- **Done when:** `func start` serves `/api/v1/health` with 200; with MongoDB stopped it returns a `ProblemDetails`; tests pass in CI.
- **Depends on:** 13, 14.
- **Notes:** a Function App is a set of small entry points that the Functions host starts on demand and stops when idle, which is why nothing may rely on in-memory state between requests. The timer trigger keeps its schedule state in storage, which is why Azurite is needed locally. Confirm the Mediator generator accepts a project with no handlers yet.

### [ ] Step 16. Web: Blazor WASM shell (M)
- **Goal:** a themed, localized front end that proves the Web → Api path.
- **What we do:** create `src/Web/FinTrack.Web`; MudBlazor; one `MudTheme` with the Indigo tokens of DESIGN §3 (light and dark, default from system, switchable); localization through `IStringLocalizer` and resource files; layout shell (app bar, drawer on desktop, bottom navigation on mobile, only existing pages listed); `ApiClient` that parses `ProblemDetails` and maps error codes to resource strings; a status page calling `/api/v1/health` with skeleton loading and a persistent error state; API base URL in `wwwroot/appsettings.json`.
- **Done when:** `dotnet run` shows the status page at 360 px and desktop widths in both themes; no hard-coded colour or string in components; build has no warnings.
- **Depends on:** 15.
- **Notes:** verified by hand (no UI test library is in the stack). Inter is self-hosted as `woff2` files in `wwwroot/fonts` with its OFL licence text next to them (no font CDN, so the app works offline), weights 400 and 600 only, system sans-serif stack as fallback.

### [ ] Step 17. Local run script (R)
- **Goal:** one command starts everything.
- **What we do:** `scripts/run-local.ps1`: run the prerequisite check, create `local.settings.json` from the example if missing, start Azurite with its data outside the repository, `func start`, `dotnet run` for Web, stop all on Ctrl+C. Update README "Running locally" and the `CLAUDE.md` Commands section.
- **Done when:** on a clean clone, the script brings up Api and Web and the status page shows healthy.
- **Depends on:** 16.

### [ ] Step 18. Stage 0 verification and docs sync (R)
- **Goal:** Stage 0 is closed and Phase 1a can start.
- **What we do:** walk the checklist below; fix drift between code, `docs/ARCHITECTURE.md`, `CLAUDE.md`, README and `docs/LOCAL-SETUP.md`; tick the steps in `docs/plans/stage-0-plan.md`; set "Current phase" to 1a.
- **Done when:** every checklist item is ticked with the evidence noted.
- **Depends on:** all.

## Stage 0 is complete when…

- [ ] `dotnet build -c Release` passes with zero warnings on a clean clone.
- [ ] `dotnet test` passes locally and in GitHub Actions, including integration tests against a real MongoDB replica set.
- [ ] Guard tests enforce the reference rules (Domain free of MongoDB, Mediator, Functions; contexts isolated).
- [ ] `Money` and `Currency` are covered by unit and property tests (exact split, banker's rounding, currency mismatch).
- [ ] UUIDv7 IDs, `IClock` and `AppError` codes exist and are used by all Shared code.
- [ ] Every step was merged through its own pull request, and `main` requires the CI check (verified with a failing test in step 4).
- [ ] An integration test proves journal-style inserts, `LedgerHead.Version` and outbox messages commit or fail together, and concurrent writers conflict.
- [ ] An integration test proves owner isolation and stale-version rejection.
- [ ] Outbox events are dispatched after commit, re-delivered by the sweep, and handled idempotently.
- [ ] `scripts/run-local.ps1` starts Api and Web; `/api/v1/health` answers; the Web status page shows it in light and dark themes.
- [ ] Failures leave the Api as `ProblemDetails` with a stable code.
- [ ] No secrets, connection strings or real financial data in the repository; `local.settings.example.json` exists, `local.settings.json` is ignored.
- [ ] `docs/ARCHITECTURE.md`, `CLAUDE.md`, README and `docs/LOCAL-SETUP.md` describe what was built.

## Deliberately deferred

- **Phase 1a:** `Bucket`, `Unallocated`, the Budget journal and entry types (with explicit, stable discriminators for entry subtypes, never the driver's default CLR class name), state builder, balances, owner settings (reporting currency, time zone), `IFxRateProvider`, read contracts, overview and journal screens, command idempotency on the journal.
- **Phase 1b and 2:** plans, pauses, limits, loans, allocation, accounts, reconciliation, charts.
- **Phases 3 to 5:** Investments and Integrations projects, `IPriceProvider`, crypto currencies and assets, draft-and-confirm flow, async job pattern.
- **Phase 6:** Bicep, Azure deployment, OIDC login, Key Vault, Application Insights, PWA.
- **Phase 7:** Insights context and agents.
- **When needed:** upcasters (the first schema change), stored projections (when replay is measured as slow), a queue (when apps are split), scheduled `mongodump` backup and JSON/CSV export, UI test tooling, Rider `.run/` configurations.

## Verification of the whole stage

1. Clean clone → `scripts/check-prereqs.ps1` → `dotnet build -c Release` → `dotnet test`.
2. `scripts/run-local.ps1` → open the Web status page → healthy; stop MongoDB → the page shows the mapped error; start it again → healthy.
3. Push a branch → GitHub Actions green.
