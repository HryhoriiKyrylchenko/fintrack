# CLAUDE.md

Guidance for Claude Code in this repository.

## Project

FinTrack (internal working name) is a personal finance tracker: budget planning with buckets, contribution plans and self-loans, plus investment buckets (crypto first, then stocks/ETFs). Manual entry first, read-only imports later. Fully serverless, runs entirely on a local machine, cloud deployment optional. Public open-source repository, licensed AGPL-3.0 (see `LICENSE`).

**The full specification is `docs/ARCHITECTURE.md`. Read it before designing or changing behaviour.** If a task conflicts with it, stop and say so instead of guessing. When a decision changes, update that file (including its decision log) in the same change.

## Communication

- Everything is in English: conversation, code, identifiers, comments, commit messages and docs.
- Keep replies short. Lead with the result. Ask at most one question at a time, and only when a wrong guess would be costly.
- Personal preferences of the current developer, if any, are in `CLAUDE.local.md` (not committed).

## Stack

- .NET 10 (LTS), C#, nullable enabled, warnings as errors.
- Code style: `.editorconfig` in the root is binding and enforced in the build. `Directory.Build.props` sets `EnforceCodeStyleInBuild` and `GenerateDocumentationFile` to `true` (the second is needed for the unused-usings rule; missing XML comments are switched off in `.editorconfig`). Line endings are LF (`.gitattributes`).
- Back end: Azure Functions, isolated worker model. Front end: Blazor WebAssembly.
- `Mediator` (the source-generated library by Martin Othamar, MIT) for commands and queries (CQRS). **Do not use MediatR**: its current versions are commercially licensed. FluentResults-based `Result` for outcomes, FluentValidation for input.
- UI components: MudBlazor. Visual rules are in `docs/DESIGN.md`.
- MongoDB with the official .NET driver. Local: single-node replica set. Cloud: MongoDB Atlas.
- Tests: xUnit, Shouldly for assertions (**not FluentAssertions**: version 8 and later need a paid licence for commercial use), CsCheck for property-based tests, integration tests against a real local MongoDB.
- External data, always behind interfaces and cached, never called from Domain: Frankfurter API for fiat FX rates (`IFxRateProvider`), CoinGecko Demo API for crypto prices (`IPriceProvider`).
- Currencies: each bucket has its own currency; the user's `ReportingCurrency` is a switchable setting; crypto is valued in USD.
- Cloud target (later): Azure Functions Flex Consumption, Azure Static Web Apps, MongoDB Atlas, Microsoft Entra External ID for login (code stays provider-neutral: standard OIDC and JWT validation only). Do not use Cosmos DB.

## Solution layout

```
src/
  Shared/FinTrack.Shared.Kernel           Money, Currency, Result helpers, IDs, IClock, document, event and ledger abstractions
  Shared/FinTrack.Shared.Infrastructure   MongoDB adapter, outbox dispatcher, mediator pipeline behaviours
  Budget/FinTrack.Budget.Domain
  Budget/FinTrack.Budget.Application
  Budget/FinTrack.Budget.Infrastructure
  Budget/FinTrack.Budget.Contracts
  Investments/...                         same four projects (created in phase 3)
  Integrations/...                        same four projects (created in phase 5)
  Api/FinTrack.Api                        Azure Functions app; folders per context
  Web/FinTrack.Web                        Blazor WASM
tests/
  Common/                                 test helpers linked into test projects (not a project)
  FinTrack.Shared.Tests                   kernel unit and property tests, architecture guard tests
  FinTrack.Shared.Infrastructure.IntegrationTests   MongoDB adapter against a real MongoDB
  FinTrack.Budget.Domain.Tests
  FinTrack.Budget.Application.Tests
  FinTrack.Api.IntegrationTests
scripts/                                  PowerShell 7 scripts (prerequisite check, local run)
docs/ARCHITECTURE.md                      specification (source of truth)
docs/DESIGN.md                            UI rules, colours, typography
docs/plans/                               step-by-step plans per stage
infra/                                    Bicep (cloud phase)
```

`FinTrack` is an internal working name and namespace prefix, not a product name.

`FinTrack.Shared.Kernel` has one package, FluentResults: no MongoDB, no mediator library. Anything that needs either goes to `FinTrack.Shared.Infrastructure`, which Domain and Application projects never reference. Do not create a project before the phase that needs it.

## Architecture rules (do not break)

1. **Serverless only.** One Function App (`FinTrack.Api`) with many small functions. Never add a long-running ASP.NET host or background service.
2. **A function is thin**: parse input → `mediator.Send` → map `Result` to a response. No business logic in `FinTrack.Api`.
3. **Context isolation**: a context may reference another context's `Contracts` project only. Never its Domain or Infrastructure. No shared collections, no cross-context queries.
4. **Dependencies point inward**: Domain has no references to infrastructure, MongoDB, Functions or the mediator library. Application depends on Domain. Infrastructure implements Application interfaces.
5. **Journal is append-only.** Never update or delete a journal entry. Corrections are reversal entries.
6. **Journal is the source of truth; state is derived by replaying it** through one state builder per context. Do not add stored projections or caches yet: they are a planned optimization, added only when measurements require it.
7. **One transaction per command**: journal entries + `LedgerHead.Version` increase (one document per owner and context, checked for the expected value) + outbox events. Version conflict → reload and retry.
8. **Cross-context communication** goes through the transactional outbox with idempotent handlers. No queue until the app is split.
9. **Every document has** `Id` (UUIDv7, client-generated), `OwnerId`, `SchemaVersion`. Mutable documents also have `Version`, `CreatedAt`, `UpdatedAt`; append-only documents (journal, outbox, processed events) have `RecordedAt` instead. **Every query and write is filtered by `OwnerId`.** The only exception is the outbox sweep timer, which reads pending messages of all owners and handles each under its own `OwnerId`.
10. **Money**: always the `Money` type (`decimal` + currency). Never `double` or `float`. Never add different currencies without an explicit conversion.
11. **Time**: use `IClock`. No `DateTime.Now` / `UtcNow` in Domain or Application. Entries carry `OccurredOn` and `RecordedAt`.
12. **Business rule failures return `Result.Fail`**, not exceptions. Exceptions are for bugs and infrastructure faults.
13. **Imports and any automated source create drafts**; only a user confirmation writes to the journal.
14. **The repository is public.** Never commit financial data, database dumps, secrets, API keys or connection strings. Use user secrets and environment variables. Keep `local.settings.json` out of git; maintain `local.settings.example.json`. Tests, fixtures, seed data, screenshots and docs use invented numbers only.

## Planned later: AI agents (do not build yet)

Phase 7 adds an `Insights` context with AI agents (portfolio explanations, questions, market and asset research). See `docs/ARCHITECTURE.md` section 12. Until then:

- Do not add any LLM SDK, agent framework or AI code.
- Do keep every context's read-only summary queries in its `Contracts` project with stable, documented DTOs. They are the future agent tools.
- Agents will be read-only: they never write to the journal and can only create drafts with `Source = Agent:<name>` for the user to confirm. Do not design anything that would need an agent to write directly.

## Domain invariants

The list in `docs/ARCHITECTURE.md` section 7 is binding. The most important:

- `Available` of a savings bucket is never negative.
- `Available = NetContributed − OutstandingPrincipal`.
- A repayment never exceeds outstanding principal. Repayments are not income.
- Plan shortfalls are computed and are not loans.
- Allocation splits sum exactly to the payment.
- State always comes from the journal.

Any new domain behaviour needs tests for the invariants it touches.

## How to work

- Work in small vertical slices: domain + handler + function + test, one use case at a time.
- **Domain first, tests first.** Write the domain test, then the domain code, then the handler, then the function, then UI.
- Follow the phases in `docs/ARCHITECTURE.md` section 8. Do not implement items tagged `[P2]`, `[Later]` or listed as out of scope unless asked. Do leave the extension points the spec describes.
- Do not add libraries, projects or patterns beyond this file without asking.
- **Licences**: the project is AGPL-3.0; dependencies must use permissive open-source licences (MIT, Apache-2.0, BSD). Before proposing any package, check its current licence and say what it is. Never add a package that needs a paid or copyleft licence for commercial use. One accepted exception, for fonts only: Inter, self-hosted under OFL-1.1.
- **Git workflow**: `main` is protected. Each plan step is one branch (`stage-0/NN-short-name`) and one pull request, merged by the developer only when CI is green. Never commit to `main` directly.
- The step-by-step plan for the current stage is in `docs/plans/`. Tick a step when its pull request is merged.
- UI work follows `docs/DESIGN.md`: use its theme tokens, never hard-coded colours in components.
- **Deferred is not dropped.** Everything in the spec stays planned; simplifications in early phases are steps, so keep the extension points and do not design them away.
- Prefer clarity over cleverness: this is financial code and must be easy to audit.
- Before saying a task is done: build, run the tests, and state what was verified and what was not.

**Current phase: 0 (Foundation).** Plan: `docs/plans/stage-0-plan.md`. First tasks: create the solution and projects, the Shared kernel (`Money`, `Currency`, IDs, `IClock`, `Result` helpers), the MongoDB adapter (transactions, optimistic concurrency, outbox), the local run script, CI build and test. Then Phase 1a, the first usable version: buckets, `Unallocated`, opening balances, deposit, spend, transfer, reversal, balances, a simple overview and journal screen. Phase 1b follows with plans, limits and loans.

## Commands

Fill in exact scripts as they are created; keep this section current.

```
dotnet build
dotnet test
func start                      # from src/Api/FinTrack.Api
dotnet run                      # from src/Web/FinTrack.Web
```

Local prerequisites: .NET SDK, Azure Functions Core Tools, MongoDB Community running as a single-node replica set, Azurite. Docker is optional, never required.

## Conventions

- One command or query per file, with its handler and validator next to it.
- Naming: commands are imperative (`AddDeposit`), queries start with `Get`, events are past tense (`DepositAdded`).
- Function names match the use case; routes are REST-like and versioned (`/api/v1/...`).
- Entry types, enum values and collection names follow the spec exactly.
- Mongo collections are prefixed by context (`budget_journal`, `budget_buckets`, `inv_journal`). Each context also has `<ctx>_ledger_heads`, `<ctx>_outbox` and `<ctx>_processed_events`.
- Stored events carry a stable event name declared on the type, never the CLR type name.
- API errors: RFC 7807 `ProblemDetails` built from `Result`, with a stable error code (`bucket.insufficient_funds`) and parameters. The UI maps codes to text.
- UI text: English only for now, but every user-facing string comes from resource files (`IStringLocalizer`); no hard-coded or concatenated strings. Format numbers, dates and money by the user's locale setting.
- Logging: structured, through `ILogger`; never log amounts, balances, keys or personal data.
- Small commits with conventional messages (`feat:`, `fix:`, `test:`, `docs:`, `refactor:`).
