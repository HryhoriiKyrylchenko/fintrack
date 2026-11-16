# FinTrack: Personal Finance Tracker. Architecture and Requirements (v2)

Status: design baseline, 15 Nov 2026. Supersedes all earlier drafts.
Place this file at `docs/ARCHITECTURE.md`. It is the source of truth for the design.
Priority tags: **[P1]** build first, **[P2]** next, **[Later]** deferred but the model leaves room.

## 1. Goals and principles

- A personal finance platform, budget planning first: money is split into buckets, with contribution plans, self-loans, and investment buckets with richer mechanics (crypto first, then stocks/ETFs).
- Manual entry is the primary input. Read-only imports from exchanges, wallets and brokers come later; the groundwork is built in from the start.
- Fully serverless and ready to deploy to the cloud, but it must run completely on a local machine for one user. The cloud may never be used; nothing may depend on it.
- Cost tends to zero: free tiers only, pay-per-use, no always-on compute.
- Correctness first. Financial history is never lost, entries are never edited, and no balance goes negative.
- Build in phases. Fewer features, done well. Models leave room to grow, but no dead code.
- Open source from the start: public GitHub repository under **AGPL-3.0**. Possible future: several users (SaaS). Each user is an individual account; there are no family groups.

## 2. Architecture

### 2.1 Shape

- **Front end**: Blazor WebAssembly, hosted as a static site. Installable as a PWA later (this replaces a separate mobile app).
- **Back end**: Azure Functions, .NET 10 (LTS), isolated worker model. Each function is one small endpoint or job: HTTP-triggered (`AddDeposit`, `PausePlan`, `GetBalances`), timer-triggered (price refresh, outbox sweep, valuation snapshot). A function only parses input, calls one mediator handler and maps the `Result` to a response. No business logic in functions.
- **Code organization**: bounded contexts as class libraries: `Budget`, `Investments`, `Integrations`, plus `Shared` (kernel). Each context has `Domain`, `Application` (CQRS through the source-generated `Mediator` library, Result pattern), `Infrastructure` and a small `Contracts` project. A context may reference only another context's `Contracts`, never its Domain or Infrastructure. `Shared` is two projects: `FinTrack.Shared.Kernel` (plain types and abstractions; its only package is FluentResults, so every Domain project can reference it) and `FinTrack.Shared.Infrastructure` (the MongoDB adapter, the outbox dispatcher and the mediator pipeline behaviours; never referenced by Domain or Application). Context projects are created in the phase that first needs them.
- **Deployment units**: **one Function App (`Api`) that hosts the functions of all contexts**, grouped by folder. This is serverless: it scales to zero and is billed per execution. One app means one deployment, one cold start, and atomic operations inside one process. Because contexts are isolated by references, splitting into one Function App per context later is a move of folders, not a redesign. Do not build a long-running ASP.NET host.

### 2.2 Storage: MongoDB (decided)

- One database engine everywhere: **MongoDB** with the official .NET driver. Local: MongoDB Community as a single-node replica set. Cloud: **MongoDB Atlas** (free cluster to start).
- Why MongoDB and not Cosmos DB (final): the app must be able to live on a local machine forever. MongoDB is a real database that can be installed and self-hosted; Cosmos DB cannot. Locally Cosmos exists only as an emulator, which is a development tool (the current one is a Docker-only preview with a subset of features) and not a place to keep real financial data for years. MongoDB also gives the same engine and the same code locally and in the cloud, real multi-document transactions and no lock-in. Cosmos DB for MongoDB limits transactions to a single unsharded collection, which does not fit. If the app is deployed to Azure, the Atlas cluster can be created in an Azure region next to the functions.
- Transactions require a replica set, so the local `mongod` runs with `--replSet` (one node is enough).
- Supported server versions: **8.0 or later**. 8.0 matches the Atlas free cluster and is the version CI tests against; a local server may be newer. Setup: `docs/LOCAL-SETUP.md`.
- Each context owns its collections. No cross-context queries or joins. Aggregates are stored as documents.
- Every document carries `Id` (client-generated UUIDv7), `OwnerId` and `SchemaVersion`. Beyond that there are two shapes:
  - **Mutable documents** (aggregates, settings, `LedgerHead`): also `Version` (optimistic concurrency), `CreatedAt`, `UpdatedAt`, and a soft-delete flag where deletion is allowed at all.
  - **Append-only documents** (journal entries, outbox messages, processed-event records): also `RecordedAt`. They are never updated, so they have no `Version` and no `UpdatedAt`. Delivery bookkeeping on an outbox message (attempts, dispatched time) is the only field group that changes after insert.
- Storage format (fixed in phase 0; a later change needs an upcaster): element names in camelCase, `Guid` and strongly typed IDs as standard UUID (binary subtype 4), `decimal` as `Decimal128`, enums as their names, business dates (`DateOnly`) as ISO strings (`yyyy-MM-dd`), instants (`DateTimeOffset`) as BSON dates in UTC with whole milliseconds, `Currency` as its code, `Money` as `{ amount, currency }`. Reading is strict: an unknown element, a decimal stored as a double or integer, an enum stored as a number or an unknown name, or an unregistered currency makes the read fail. Writing an instant with an offset or a sub-millisecond part fails; `IClock` returns UTC in whole milliseconds, so a value read back equals the value written. The format lives in `FinTrack.Shared.Infrastructure/Mongo/MongoSerialization`.
- Collection names are prefixed by context. Per context, besides its own collections: `<ctx>_ledger_heads`, `<ctx>_outbox`, `<ctx>_processed_events` (for Budget: `budget_ledger_heads`, `budget_outbox`, `budget_processed_events`).
- Free tier limits change. Verify before deployment. At the time of writing the Atlas free cluster had 512 MB storage and about 100 operations per second.

### 2.3 Consistency

- **The journal is the source of truth.** It is append-only.
- **State is derived by replay.** Balances, plan status and positions are computed from the journal on each request. For one user the journal is small, so this is fast and removes a whole class of consistency bugs.
- **Stored projections are deferred, not dropped.** They are added as an optimization when measurements show replay is too slow (many users, long history). They will then be a rebuildable cache, and a test will assert projection == replay. Code that reads state goes through one place per context (a state builder), so adding a cache later does not touch handlers.
- **Write path**: read the owner's `LedgerHead.Version` → **then** load the owner's journal → build state → validate invariants → in **one MongoDB transaction**: insert journal entries, insert or replace the context's changed mutable documents (for example a bucket), increase the owner's `LedgerHead.Version` (a single small document per owner and context, checked for the version read first), insert outbox events, and for an event handler record the handled event. A version conflict means another write happened in between: reload and retry. The version is read before the journal so that a write landing in between makes the commit fail instead of passing on stale data. Handlers have no side effects before the commit, because a retry runs them again. The contract is `ILedger` in the shared kernel.
- **Domain types are the stored documents.** Journal entries and aggregates implement the kernel's document interfaces and are mapped to MongoDB in Infrastructure; Domain has no database attributes and there is no second set of persistence classes.
- **Events between contexts**: transactional outbox. Events are written in the same transaction as the data, dispatched in-process right after commit, and a timer function re-dispatches anything left undelivered. Handlers are idempotent (they record processed event IDs). No queue is needed while there is one Function App. A queue (Azure Storage Queue) is introduced only if apps are split or for long-running imports.
- **The outbox sweep is the one exception to owner scoping.** The timer function must find undelivered messages of all owners, so its query for pending outbox messages is not filtered by `OwnerId`. Each message is then handled under that message's own `OwnerId`. No other query or write may skip the owner filter.
- Stored events are identified by a stable event name declared on the event type, never by the CLR type name, because stored events outlive refactorings.
- **Schema evolution**: `SchemaVersion` on every document; old versions are upgraded on read (upcasters). The journal lives forever, its format will change.

### 2.4 Local run (no Docker required)

- Prerequisites: .NET SDK, Azure Functions Core Tools, MongoDB Community (local service, single-node replica set), Azurite (storage emulator, needed by timer triggers).
- One command or script starts API and Web. The code path is identical to the cloud. Works offline except for price, FX and import calls.
- An optional Docker Compose file may be provided for MongoDB and Azurite as a convenience, never as a requirement.
- Local mode: one default user, no login. Data lives in the local MongoDB data directory, outside the repository. Automatic backup = scheduled `mongodump` to a user-chosen folder, plus JSON/CSV export from the app.

### 2.5 Cloud [Later]

- Azure Static Web Apps (front end), Azure Functions on the **Flex Consumption** plan (the older Linux Consumption plan does not receive .NET 10), MongoDB Atlas, Key Vault for secrets, Application Insights for logs. Infrastructure as Bicep templates.
- Authentication through an external OIDC provider. The app never stores passwords. `OwnerId` comes from the token. The code depends only on standard OIDC and JWT validation, so the provider is replaceable by configuration. Default choice: **Microsoft Entra External ID** (same Azure account and bill, has a free tier). Alternative: Auth0 (free plan, simpler setup, not tied to Azure). Confirm free-tier limits before going live.
- Mandatory before going live: an Azure budget alert, because usage above free grants is billed.
- Agents and other long-running work (section 12) run asynchronously on a queue-triggered function; this is the point where a queue is introduced.
- SaaS readiness: per-user data isolation by `OwnerId` in every query (enforced in the repository base class), data export and account deletion, sign-in audit log.

### 2.6 Read contracts (built from phase 1)

- Every context exposes **read-only summary queries with stable, versioned DTOs** in its `Contracts` project: bucket balances, plan status and dues, loans, portfolio totals, sections, positions with PnL, targets and their status, value history. The UI uses them now. Later the same queries become the tools of AI agents (section 12) and the input for reports. Keep them free of UI concerns and document each field.

### 2.7 Engineering conventions

- **API errors**: every failure is returned as RFC 7807 `ProblemDetails` built from the `Result`. Each business error has a **stable error code** (for example `bucket.insufficient_funds`) plus parameters. The back end never returns user-facing sentences as the contract; the UI turns codes into text. Status by error kind: Validation 400, NotFound 404, Conflict 409, BusinessRule 422, Unavailable 503; an unexpected failure is 500 with code `server.error` and no details. Body: `type` (`urn:fintrack:error:<code>`), `title` (the code), `status`, `code` and `params` of the first error, and `errors` (every error as `{ code, params }`).
- **Language**: the UI is English only for now. All user-facing strings live in resource files (`IStringLocalizer`) from the first screen, with no text concatenation and no hard-coded strings in components. Numbers, dates and currency formatting follow the user's locale setting. Translations are added later without touching code.
- **Logging**: structured logging through `ILogger`. Never log amounts, balances, account identifiers, API keys or personal data; log IDs and error codes.
- **CI**: build and all tests run on every push (GitHub Actions). A failing test blocks merging.

### 2.8 Configuration and secrets

- The repository contains code and example configuration only. No financial data and no secrets, ever.
- Local secrets: .NET user secrets or environment variables. Cloud: Key Vault.
- Exchange and broker API keys are read-only keys, stored encrypted. Wallet private keys and seed phrases are never requested or stored.

## 3. Shared kernel

- `Money` = `decimal` amount + `Currency`. Arithmetic across different currencies is a compile-time or runtime error (an exception: it is a programming error, not a business rule failure); conversion is always explicit.
- Precision per currency: each `Currency` carries its own number of decimal places (0 to 18). Most fiat currencies have 2 (PLN, EUR and USD are registered first), some have 0 or 3; crypto quantities use up to 18. A `Money` value never has more decimals than its currency allows; user input with more decimals is rejected, never rounded silently. Rounding a single value (for example a conversion): banker's rounding at the currency's precision. A split into parts (by positive weights): each part is cut toward zero at the currency's precision and the remainder (less than one minor unit per part) goes to the first line, so the parts always sum exactly to the original and never have the opposite sign.
- **Multi-currency [P1]**: every bucket has one currency, chosen per bucket (PLN, EUR, USD and so on). The owner has a `ReportingCurrency` setting for totals and reports; it is chosen on first run and can be switched at any time (PLN, EUR, USD), because totals are computed at read time. Crypto portfolios are always valued in USD; stablecoins such as USDT and USDC are assets priced against USD. A movement between different currencies records both amounts, the actual rate and the fee as entered by the user. Totals across currencies are computed at read time with rates from `IFxRateProvider` (first implementation: the Frankfurter API, free and without an API key, central-bank reference rates including PLN; daily rates, cached, stored historically) and always show the rate date. Converted totals are never stored as truth.
- **Two dates on every entry**: `OccurredOn` (business date, chosen by the user) and `RecordedAt` (system time). Backdated entries are allowed; projections from that date are rebuilt.
- Time zone and the "month" of a plan follow the owner's configured time zone.
- `Result` pattern for all handlers (FluentResults-based). No exceptions for business rule failures.
- `IClock` abstraction; no direct `DateTime.Now` in domain or application code.

## 4. Budget context

### 4.1 Buckets [P1]

- One entity, `Bucket`. No presets; the user creates buckets. Kinds: `Savings` and `Investment`. Investment profiles: `Crypto` and `RealValueAssets` (stocks, ETFs). **One investment bucket holds exactly one portfolio.**
- Fields: name, currency, kind, profile, limit (optional), `Priority` (numeric), status.
- Buckets are archived, never deleted, and only when they hold no balance, no open loan and no active plan.
- System bucket **`Unallocated`** (one per currency, created on demand): money that has arrived but is not yet distributed. It cannot be archived and is included in all totals.

### 4.2 Journal [P1]

Append-only. A mistake is fixed by a reversal entry plus a new entry; the UI may present this as "edit". Every entry has: note, tags, `GroupId` (entries created by one user action), `Source` (`Manual`, `Import:<connector>`), `ExternalId` (for deduplication), `OccurredOn`, `RecordedAt`.

| Type | Meaning |
|---|---|
| `OpeningBalance` | Starting amount when the user begins using the app. Entered manually, backdated. Not income. |
| `Deposit` | New money into a bucket. Optionally linked to a plan (`PlanId`), otherwise a one-time extra. |
| `Spend` | Money leaves for good. No debt. Includes spending "by purpose" (the cushion used as a cushion). |
| `Transfer` | Between two buckets. If currencies differ: both amounts, rate, fee. |
| `Borrow` | Taken from a savings bucket with an obligation to return. Opens a `Loan`. The destination is optional: another bucket or outside. |
| `Repayment` | Returns loan principal to the bucket. Never counted as income. |
| `Adjustment` | Manual correction with a mandatory comment. Shown separately in reports. |
| `Reversal` | Cancels a previous entry by reference. |

### 4.3 Balances of a savings bucket [P1]

- `NetContributed` = opening balance + deposits + transfers in − spends − transfers out.
- `OutstandingPrincipal` = borrowed − repaid principal.
- `Available` = `NetContributed` − `OutstandingPrincipal`.
- Example: 1,000,000 contributed, 100,000 borrowed and not yet returned → 900,000 available.
- `Available` is never negative; an operation that would break this is rejected.
- Income reports count `Deposit` only.
- Interest is **not** part of `OutstandingPrincipal` (see 4.4). This fixes an error in the earlier draft.

### 4.4 Loans

- **[P1]** `Loan`: lender bucket, principal, date, status. Created by `Borrow`. A loan can stay open with no schedule, or have a `RepaymentPlan` (monthly amount, versioned). A repayment cannot exceed the outstanding principal; the excess goes to remainder handling. A fully repaid loan gets status `Closed` and stays in history. Loans are allowed from savings buckets only.
- **[P2]** Proportional repayment of all open loans of a bucket with one payment.
- **[Later] Interest on self-loans.** Design note for when it is built: interest is a separate entity (`LoanInterest`) that can be cancelled without touching the principal; accrued interest is tracked as `InterestDue`, separate from principal; paid interest is its own entry type that increases `NetContributed` and is reported separately from deposits; two methods (equal parts, on remaining balance); interest goes to the lender bucket.

### 4.5 Contribution plans [P1]

- `ContributionPlan`: bucket, monthly amount, `ValidFrom`, optional `ValidTo`, versions. Changing the amount creates a new version and never rewrites past periods. A bucket may have several plans.
- `DueByPlan` = sum of accrued periods from plan start to now − deposits linked to the plan. Computed, not entered. Payments settle the oldest period first.
- Shortfalls carry over (plan 500, paid 300 → next month 700 is due). This is an obligation to the plan, not a loan: it never changes `Available` and is never mixed with `Loan`.
- The end date stops new periods, not the obligation. A shortfall stays until it is paid or written off (`Waive`, with a reason).
- Overpaying a plan creates a prepayment that reduces future periods. One-time deposits without `PlanId` are outside this calculation.
- **Pauses (simplified)**: a pause exists only on a plan: `PlanPause { From, To?, Mode }`. `Shift` = paused periods do not accrue and the end date moves later. `Skip` = paused periods are dropped, the end date stays. The mode and dates can be changed later by a new event.
- **[P2] Bulk pause**: "pause all plans of a bucket" and "pause everything" are commands that create one `PlanPause` per plan with a shared `BatchId`. Any single plan can then be resumed on its own, including with a smaller amount (a new version). There are no scope-precedence rules.
- One-time deposits are never blocked by a pause.

### 4.6 Bucket limit [P1]

- Savings buckets: the limit is compared with `NetContributed` (= available + outstanding principal), so a loan that will be returned does not free space.
- At the limit: plans stop accruing new periods and the UI shows that the goal is reached; payments can only repay loans. A deposit above the limit asks "raise the limit to X?"; confirming writes `LimitRaised`, declining cancels the deposit.
- **[P2] Goal with a date**: limit + target date → required monthly amount and a forecast of when the goal is reached.

### 4.7 Payment allocation

- The engine is a pure function: input = amount, lines, plans, loans, rules; output = a proposed split and an unallocated remainder. Nothing is written until the user confirms. The split always sums exactly to the payment.
- **[P1] Manual mode**: the user gives explicit lines, each with a target (a plan, a loan, a one-time deposit into a bucket). The engine validates and caps each line (plan due, outstanding principal, bucket limit) and asks where the remainder goes: one-time deposit, loan repayment, plan prepayment, or `Unallocated`.
- **[P2] Automatic and semi-automatic modes**: buckets by numeric `Priority`; at equal priority debts first (plan shortfalls, then open loans, oldest first); if still undecided the system asks. `RemainderPolicy` per bucket or global (`Ask`, `ExtraDeposit`, `RepayLoan`, `Prepay`).

### 4.8 Accounts and reconciliation [P2]

- `Account` = where money physically is: bank account, cash, exchange, wallet, broker. It has a currency (or holds assets) and a user-entered or imported actual balance with a date.
- Buckets are virtual; accounts are real. Reconciliation compares, per currency, the sum of bucket balances with the sum of account balances and shows the difference. A difference is resolved with an `Adjustment` and a comment.
- This is the main protection against silent bookkeeping errors.

### 4.9 Investment buckets as seen by Budget [P1 model, P3 behaviour]

- An investment bucket has two independent numbers, both always shown:
  - **Invested**: `NetInvested` = deposited − withdrawn, in the bucket currency. It may become negative when more has been withdrawn than was put in.
  - **Current value**: `MarketValue`, provided by the Investments context at current prices.
  - `TotalPnL` = `MarketValue` − `NetInvested`.
- Plans and the limit of an investment bucket work on contributions (`NetInvested`), never on market value, so price moves do not block or unblock plans.
- Money leaves an investment bucket only through a withdrawal of portfolio cash (see 5.6). `Spend` and `Borrow` are not available on investment buckets.

## 5. Investments context [P3 crypto, P4 stocks/ETFs]

### 5.1 Structure

- Investment bucket → one `Portfolio` → `Section`s → holdings. Crypto default sections are configurable: **core** (5-6 coins with target shares), **active** (bots, open positions, unallocated stablecoins), **reserve** (stablecoins for adding to the core). The user can add, rename and remove sections.
- Everything held is an `Asset` with a quantity: coins, stablecoins, fiat cash, stocks, ETFs. A portfolio has a `ValuationCurrency` (USD for crypto).
- A system section **`Pending`** always exists and cannot be removed. A section cannot be closed while it holds anything; holdings are first moved elsewhere or to `Pending` (optionally marked "to sell"). Totals are computed from holdings, so rearranging sections can never change the portfolio total.
- A section is an **effective-dated assignment**. Moving a holding writes `Reclassify`; old entries are not rewritten, and a report "as of date X" shows the section of that date.
- `Location` of a holding = an `Account` (exchange, wallet, broker).

### 5.2 Portfolio journal

Separate journals for crypto and for stocks/ETFs, with shared base types. Append-only, same `Source` / `ExternalId` / two-date rules as Budget.

| Type | Meaning |
|---|---|
| `OpeningPosition` | Existing holding at start: quantity, cost basis, date. |
| `CashIn` | Contribution arriving from the Budget deposit (created by event). |
| `Trade` | Gives X of asset A, receives Y of asset B, plus fee. Covers buy, sell, crypto-to-crypto swap and fiat-to-stablecoin conversion. The UI may label them Buy / Sell / Swap. |
| `Income` | Staking, airdrop, bot payout, dividend (with withholding tax). Credited to a chosen section. |
| `Fee` | Standalone fee (withdrawal, network). |
| `Allocate` | Assigns cash or stablecoins to a section or container. |
| `Move` | Between locations (exchange → wallet). No change in quantity except the fee. |
| `Reclassify` | Changes the section of a holding from a date. |
| `CorporateAction` | Stock split, ticker change. [P4] |
| `CashOut` | Withdrawal of cash from the portfolio back to Budget or outside. |

### 5.3 Lots, positions, PnL

- Every acquisition creates a **lot** (quantity, unit cost, date, fee). Positions (quantity, average price, entry point, realized and unrealized PnL) are projections computed from lots. Storing lots keeps both average-cost and FIFO available later (for tax reports).
- Stablecoins allocated to a section count toward that section as unrealized (not yet deployed) capital.
- Prices come from `IPriceProvider` (first implementation: CoinGecko, free Demo plan with an API key; attribution to CoinGecko is shown in the UI), cached by a timer function and refreshed only as often as the free monthly quota allows. A paid plan can be added later without code changes. Stocks and ETFs [P4] use the same interface with end-of-day prices, which is enough because the broker holds the authoritative price and the app only needs a value for tracking. Free APIs cover European exchanges poorly (for example the free Twelve Data plan is US-only), so for a handful of ETFs any free end-of-day source is acceptable (the candidate to check first is Stooq daily data), and manual price entry always works as a fallback. The source is chosen in phase 4; a paid, reliable feed is added only if the app becomes a public service. Each price has a timestamp; stale prices are flagged in the UI; a manual price override is possible.

### 5.4 Containers for bots and futures

- Bots (spot, futures) and futures positions are not modelled trade by trade. A `Container` lives in a section and has: type, settings (free-form table), `AllocatedCapital` (stablecoins assigned by `Allocate`), manual `Valuation` entries (date, current value), payouts (`Income` credited to any section) and a close with a final result.
- Open spot positions in the active section are ordinary holdings created by `Trade`.

### 5.5 Strategy and targets

- `Target` per asset or section: core accumulation levels (price drops to a level → buy N% of reserve), profit taking in steps relative to the all-time high (exceeds ATH by X% → sell 5%), per-asset multiples (x2, x3, x4).
- Execution is manual. Each step has a status (pending, done, skipped) and is shown in a table; a price approaching a level is highlighted.
- **[Later]** Rebalancing hint: drift of core holdings from target shares and the trades that would restore them.

### 5.6 Getting money out

1. `Trade` asset → stablecoin or fiat (realized PnL is computed from lots).
2. `CashOut` from the portfolio. Budget records the matching movement out of the investment bucket: a `Transfer` into another bucket or a withdrawal outside. `NetInvested` decreases by the withdrawn amount.
3. Money arriving in a savings bucket this way is a transfer, not income. Profit is reported in investment reports.

Rule: a portfolio cannot withdraw more cash than it holds, and no holding quantity can go negative.

### 5.7 Value history [P3]

- The app stores **daily price history** per asset and daily FX rates (`PricePoint`), filled by a timer function. Portfolio value on any past date = holdings replayed from the journal to that date × the stored price of that date, so value history is always reproducible and survives corrections to old entries.
- Locally the app does not run every day: on start it back-fills missing days as far as the price and FX APIs allow (free plans limit how far back). Days that cannot be filled are shown as gaps, never invented.
- This feeds the charts and reports in section 13 and later performance metrics.

## 6. Integrations context [P5, groundwork in P1-P3]

- One connector per source behind `IImportConnector`:
  - crypto exchanges: read-only API keys (no trade or withdrawal permission);
  - wallets: public address, read through an explorer or node API;
  - brokers: API where one exists, otherwise the statement file the broker exports. XTB switched off its API on 14 March 2025, so XTB means file import.
- Pipeline: fetch → normalize into draft journal entries with `Source` and `ExternalId` → deduplicate on (`Source`, `ExternalId`) → show drafts → write on user confirmation. Per-connector auto-confirm may come later.
- Imported balances also feed reconciliation (4.8).
- Groundwork from the start: `Source` and `ExternalId` on all journal entries, the draft-and-confirm flow, the connector interface. No trading or withdrawals through any API, ever.

## 7. Invariants (each covered by automated tests)

1. `Available` of a savings bucket is never below zero.
2. `Available` = `NetContributed` − `OutstandingPrincipal` at any moment.
3. Journal entries are never modified or deleted; corrections are reversals.
4. A repayment never exceeds the outstanding principal.
5. Income counts `Deposit` only.
6. Plan dues are derived from plan versions, pauses and linked deposits; changing a plan never alters past periods.
7. Sum of sections (including `Pending`) = portfolio total; no holding quantity is negative; portfolio cash is never negative.
8. A bucket with a balance, an open loan or an active plan cannot be archived.
9. Allocation is deterministic and the split sums exactly to the payment.
10. State is always derived from the journal. When stored projections are introduced, each must equal a full replay.
11. `Money` of different currencies is never added without an explicit conversion.
12. Every query and write is scoped by `OwnerId` (single exception: the outbox sweep, section 2.3).
13. Handlers are idempotent for repeated commands and events (same `Id` → same outcome, no duplicates).

Testing approach: domain unit tests first; property-based tests for invariants 1, 2, 9 and 10; scenario tests built from real-life cases; integration tests against a real local MongoDB. Acceptance: run the app in parallel with the existing spreadsheet for two to three months and compare.

## 8. Phases

Nothing in this document is cancelled by the phasing. Items marked `[P2]` or `[Later]`, and everything simplified for the first version, stay in the plan and are implemented as the project moves on.

| Phase | Content |
|---|---|
| **0 Foundation** | Solution skeleton, Shared kernel (`Money`, `Result`, IDs, clock), MongoDB adapter with transactions and outbox, local run script, CI, test projects. Built as a walking skeleton: a health function, the `Result` to `ProblemDetails` mapper, mediator pipeline behaviours, the outbox sweep timer and a Web shell with theme and localization. Step-by-step plan: `docs/plans/stage-0-plan.md`. |
| **1a First usable version** | Buckets, `Unallocated`, `OpeningBalance`, `Deposit`, `Spend`, `Transfer`, `Reversal`, multi-currency, balances, a simple overview and journal screen. Goal: start real use in parallel with the spreadsheet as early as possible. |
| **1b Budget core** | Plans with carry-over, plan pause (shift/skip), limits, loans without interest, `Adjustment`, manual allocation, summaries. |
| **2 Budget extensions** | Accounts and reconciliation, automatic and semi-automatic allocation, bulk pause, goals with dates, proportional loan repayment, period snapshots, stored projections if measurements require them, budget charts and the monthly report (section 13). |
| **3 Investments: crypto** | Portfolio, sections, journal, lots and positions, containers, prices, targets, price and value history, portfolio charts (section 13), deposit and withdrawal flows with Budget. |
| **4 Investments: stocks/ETFs** | Second journal, dividends, corporate actions, FX. |
| **5 Integrations** | Connectors: exchange API, wallet address, broker file. |
| **6 Cloud** | Bicep, Azure deployment, OIDC login, Key Vault, budget alert, PWA. |
| **7 Insights and agents** | AI layer as described in section 12: portfolio explanations and questions first, then market and asset research. |
| **Later** | Loan interest, notifications, rebalancing hints, tax report, performance metrics. |

## 9. Out of scope

Real estate; bank import; trading or withdrawals through APIs; family groups or shared workspaces; a native mobile app (PWA instead); crediting interest to another bucket; day-to-day expense tracking by category (this app allocates savings, it is not an expense tracker).

## 10. Decision log

| Decision | Reason |
|---|---|
| Serverless, one Function App with many small functions | Scales to zero, zero idle cost, one deployment and cold start, atomic in-process work. Contexts are isolated by project references, so a later split is cheap. A long-running host or a single giant function was rejected. |
| MongoDB locally and Atlas in the cloud | One engine and one adapter, real transactions, runs natively without Docker, no lock-in. Cosmos DB for MongoDB cannot do cross-collection transactions. |
| Journal is the truth; state is replayed, stored projections come later | History is never lost. Replay is simple and fast enough for one user; a cache is added only when needed, without changing handlers. |
| MongoDB only, no second relational database | Two stores would double the adapters and tests and cannot share a transaction. Reports are computed from replayed state or aggregation pipelines. |
| A first usable version (1a) before the full Budget core | The largest risk is never reaching real use; early use finds wrong assumptions cheaply. Nothing is dropped. |
| Store daily prices, not daily portfolio valuations | A stored valuation becomes wrong when an old entry is corrected; prices plus replay always give the right history. |
| Error codes in the API, text in the UI; strings in resource files | Keeps the contract stable and makes translation a later, code-free step. |
| Outbox instead of a queue | DB write and event publish must be atomic; a queue alone cannot guarantee it. |
| Repayment is its own entry type | Borrowing from yourself and returning must not inflate income. |
| Plan shortfall is not a loan | Different obligations; mixing them corrupts `Available`. |
| Pause only at plan level, bulk commands for wider scope | Same user experience, no precedence rules to get wrong. |
| Limit measured against contributions | A returnable loan does not free space; market moves do not affect plans. |
| One investment bucket = one portfolio | Limits, plans and pauses live on the bucket and stay unambiguous. |
| Invested and current value shown separately | Both matter for reporting; they are different numbers and must not be merged. |
| `Trade` as one two-legged entry | Buy, sell, swap and conversion are the same operation; fewer types, fewer bugs. |
| Bots and futures as containers | Margin, funding and bot internals do not fit buy/sell; capital + valuation is enough for tracking. |
| Lots are stored | Average price and FIFO both stay possible. |
| Imports create drafts | A wrong import must never write to the journal without the user. |
| Loan interest, auto-allocation, notifications deferred | Low value against complexity; the model leaves room for them. |
| `OwnerId`, no workspaces | Every user is individual. |
| Reporting currency is a switchable user setting | The owner earns in PLN, may hold EUR and tracks crypto in USD; totals are computed at read time, so switching costs nothing. |
| Frankfurter for FX, CoinGecko for crypto prices | Both free; both hidden behind interfaces so they can be replaced. |
| .NET 10 | Current LTS, supported by the Functions isolated worker until November 2028. |
| `Mediator` (source-generated, MIT) instead of MediatR | MediatR 13+ is dual-licensed (RPL-1.5 or commercial); it is free only below a revenue threshold and needs a registered licence key. The replacement has almost the same API, no licence risk, and no reflection at start-up, which shortens serverless cold starts. |
| Shouldly instead of FluentAssertions | FluentAssertions 8+ requires a paid licence for commercial use. |
| AGPL-3.0 for the project | Anyone may use and change it, but whoever runs a modified version as a service must publish the changes, so the code cannot be taken into a closed competing service. As the sole copyright holder the owner can still relicense later. Outside contributions are not accepted until a contributor agreement (CLA) is in place, to keep that option. |
| Permissive licences only for dependencies | The project may become a commercial service or open source; no dependency may block either. |
| Entra External ID by default, provider-neutral OIDC in code | One Azure account and a free tier; no provider SDK in the domain, so Auth0 or another provider is a configuration change. |
| AI agents as a separate read-only `Insights` context | The journal stays protected from model errors; agents reuse the read contracts; nothing in Budget or Investments has to change when AI is added. |
| One `ILedger` contract: read the version, then commit | Reading the version and committing against it are one protocol; the version is read before the journal so a concurrent write always causes a conflict, never a stale check. |
| Mutable documents are committed under the ledger version | Creating or archiving a bucket depends on the journal (no balance, no open loan), so it must be checked against the same version as journal writes; otherwise an archive and a deposit could race. |
| The handled-event record is part of the handler's commit | A handler's writes and the record that it handled the event are atomic, so a repeated delivery writes nothing (invariant 13). |
| Domain types are stored directly | One type per concept instead of a domain type plus a persistence type and mapping code; MongoDB mapping stays in Infrastructure, Domain stays free of database code. |
| Shared is split into `FinTrack.Shared.Kernel` and `FinTrack.Shared.Infrastructure` | Domain projects reference the kernel and must not reference MongoDB or the mediator library, so the adapter and pipeline code cannot live in the kernel. The name `Kernel` keeps the two projects apart. |
| Context projects are created in their own phase | Phase 0 creates Shared, Budget, Api and Web only. Empty Investments and Integrations projects would be dead weight until phases 3 and 5. |
| Two document shapes: mutable and append-only | `Version` and `UpdatedAt` have no meaning on a document that is never updated; append-only documents carry `RecordedAt` instead. |
| Each `Currency` carries its own decimal places | "Fiat has 2 decimals" is false for some currencies (JPY has 0). |
| The outbox sweep reads across owners | A timer has no owner; it must see every pending message. Each message is still handled under its own `OwnerId`. |
| HTTP functions use the ASP.NET Core integration of the isolated worker | Standard request and response types and simple `ProblemDetails`. The Functions host still owns the process, so this is not a long-running ASP.NET host. |
| CsCheck for property-based tests | Apache-2.0, written for C#, no F# runtime dependency. FsCheck was the alternative. |
| A split truncates each part; banker's rounding only for single values | Rounding each part to nearest and giving the remainder to the first line can make the first part negative for a positive amount (0.05 split 0.0001 : 1 : 1 : 1 would give −0.01, 0.02, 0.02, 0.02). Truncation keeps every part on the same side of zero and the sum exact. |
| The set of currencies is closed | Every currency is registered in code, so one code can never exist with two precisions. Tests define extra precisions through an internal factory. |
| Inter font is self-hosted under OFL-1.1 | The only accepted exception to the MIT/Apache/BSD rule, and for fonts only. OFL allows bundling and commercial use. Self-hosting keeps the app working offline. |
| MongoDB 8.0 is the minimum server version; CI tests 8.0, local servers may be newer | Atlas free clusters set the floor. A newer local server (9.0 on the first machine) cannot be downgraded easily once its data files are upgraded, and CI on the oldest version catches the use of newer features. |
| Instants are BSON dates in UTC with whole milliseconds; `IClock` returns whole milliseconds | A native date works with date queries and aggregations. A BSON date cannot hold finer ticks or an offset, so the clock never produces them and the serializer refuses them: in-memory state and state read back from the database are always equal, which keeps replay deterministic. An ISO string would keep 100 ns precision but lose date queries. |
| Reading stored documents is strict | A stored double, a number for an enum or an unknown element would otherwise be converted or ignored without notice; for financial data a loud failure is safer than a silent change. |
| Owner scoping and append-only are enforced by collection types | Contexts never touch a raw MongoDB collection: `AppendOnlyCollection<T>` has no update or delete method (rule 5), `MutableCollection<T>` replaces and deletes only with the expected `Version` in the write's own filter, and both add the `OwnerId` filter to every query and write (rule 9). A rule that cannot be called around is safer than one that must be remembered. |
| A duplicate document `Id` on insert is a `concurrency.conflict` | IDs are client-generated, so a second insert of the same `Id` means the same command ran twice or two writers raced; both are conflicts, never a second copy (invariant 13). Another owner's document behaves as if it did not exist, except that its `Id` cannot be reused. |
| Each context maps its document types to collections once (`LedgerDocuments`) | `LedgerCommit` carries documents, not collection names. A type (or its registered base type, so entry subtypes share the journal) maps to one collection named `<ledger>_...`; a document of another context's ledger makes the commit throw, which also enforces rule 3 at write time. |
| The handled-event record is written first in a commit; an outbox message's `Id` is its `EventId` | A repeated delivery is recognised before the version check, so it returns `AlreadyHandled` even when the handler read a version that has moved on; and one event can be stored in the outbox only once. |
| In-process dispatch through a decorating `DispatchingLedger`; each message in its own scope under its own owner | `MongoLedger` stays a pure write; the decorator delivers the committed events right after the commit and never fails the command. The sweep and the immediate dispatch use one code path: a new DI scope per message whose `ScopedOwnerContext` is the message's owner, so a handler can touch only that owner's data. |
| A failed delivery stays pending with `Attempts` and `LastErrorCode`; no retry limit yet | Handlers are idempotent, so retrying is safe; each failure is logged with its code. A dead-letter policy (a retry limit and a way to inspect stuck messages) is added when real handlers exist. |
| Validation failures: one `validation.failed` error per field, with `field` and `rule` only | The UI can mark each field and pick a text by rule; the value that was sent is never echoed back, because it may be an amount and error parameters may be logged. |
| A ledger command is retried at most 3 times in total on `concurrency.conflict`, without delay | Conflicts are rare for one user; three runs on fresh state are enough, and a bounded loop never hides a real problem. Other failures and exceptions are never retried. |
| `ErrorKind.Unavailable` (HTTP 503) for an unreachable dependency | A database or rate service that does not answer is neither a bug nor a business rule; the client may retry later. The health check reports an unreachable MongoDB this way instead of failing with 500. |
| Worker middleware: exceptions to `server.error`, the owner per HTTP request, indexes once per process | Cross-cutting work stays out of functions, which remain parse, send, map. Indexes are created before the first function that needs them; the health check is exempt so it can report a database that is down. |
| The light or dark choice is stored in the browser (`localStorage`) | It is a display preference of one device, not user data; without a stored choice the theme follows the system. A synced user setting can replace it when settings exist. |
| The Web project references Contracts and the kernel only when it first uses them | Phase 0 has no shared DTOs yet; the health answer is a small client-side record. Unused references would only add download size to the WebAssembly app. |
| One branch and one pull request per plan step | `main` is protected and requires a green CI run, so every change is built and tested before it lands. |

## 11. Setup choices and open questions

Resolved:

- Name: **FinTrack**, an internal working name and namespace prefix (`FinTrack`). It is deliberately plain and not meant as a product name; a public name is chosen only if the app is ever released, and renaming is a find-and-replace.
- Runtime: .NET 10 (LTS).
- Reporting currency: a user setting, switchable at any time. Crypto is valued in USD.
- FX rates: Frankfurter API. Crypto prices: CoinGecko Demo plan. Stock and ETF prices: end-of-day, free source behind `IPriceProvider`, manual entry as fallback.
- Cloud identity: Microsoft Entra External ID by default, provider-neutral OIDC in code.
- Libraries: `Mediator` (Martin Othamar, MIT, source-generated) instead of MediatR; FluentResults; FluentValidation; MudBlazor; xUnit; Shouldly instead of FluentAssertions; CsCheck for property-based tests. Only permissive licences are allowed for dependencies (they are compatible with the project's AGPL-3.0). One exception, for fonts only: Inter under OFL-1.1.
- UI rules: `docs/DESIGN.md`. UI language: English, strings in resource files.
- Storage: MongoDB only.

Open (none blocks the start):

- Stock and ETF price source: any free end-of-day source, chosen in phase 4; a paid feed only for a public service.
- LLM provider and agent framework: decide in phase 7.

## 12. Insights and AI agents [Phase 7, design constraints apply now]

Nothing in this section is built in phases 0 to 6. It is recorded so that earlier phases do not block it.

### 12.1 What it is for

- **Explain my portfolio**: why the value changed, what moved, how the plan is going, what a number means.
- **Ask questions** about own data in plain language.
- **Research**: an asset or project (fundamentals, news, market context), macro context, a trading setup.
- **Suggestions**: options for a situation, always as information for the user to decide on.

Concrete features are specified when the phase starts.

### 12.2 Architecture

- A separate bounded context, **`Insights`**. It may read other contexts only through their read contracts (2.6). It has no access to their collections.
- **Agents never write to the journal.** The most an agent can do is create a draft or a proposal (for example a suggested target level or a suggested allocation), which the user confirms through the normal draft-and-confirm flow. Such drafts carry `Source = Agent:<name>`.
- An agent = a model + a set of **tools**. Tool groups: portfolio tools (the read contracts), market tools (`IPriceProvider`, `IFxRateProvider`, value history), research tools (web search and page reading, added in that phase). All tools are read-only.
- The model sits behind an interface (`ILlmClient`), so the provider can be changed and a local model can be used in offline mode.
- **Runs are asynchronous.** An HTTP function creates an `AgentRun` document (status, steps, tool calls, result, token usage, cost) and returns at once; a queue-triggered function executes the run step by step and saves progress after each step; the UI polls or streams the status. A run can be cancelled and resumed.
- Research results can be saved as `ResearchNote`s linked to an asset, with their sources and date.

### 12.3 Rules

- **Cost**: model calls are paid per token and are the only part of the system that is not free. Agents run only on a user's request, never in the background by default. Each user supplies their own API key (required for local and open-source use). Every run records its cost, and a per-user spending limit stops runs when reached.
- **Privacy**: sending data to an external model is opt-in. Send the minimum needed; an option replaces absolute amounts with percentages. API keys are stored like other secrets (2.8).
- **Trust**: answers show which data and sources they used. Output is information, not financial advice, and is labelled so. Text fetched from the web is untrusted data and is never treated as instructions; because every tool is read-only, a manipulated page cannot change the user's records.
- **No trading**: agents cannot place orders or move money. This follows from section 6 and does not change.

### 12.4 What earlier phases must provide

- Read contracts with stable DTOs (2.6), from phase 1.
- Value history (5.7) and realized/unrealized PnL per position (5.3): needed to explain changes.
- Targets, notes and tags on entries: the context an agent needs to understand intent.
- The draft-and-confirm flow as a shared mechanism (4.7, 6), with `Source` on every entry.
- Asynchronous job pattern (`AgentRun`-style document + queue) introduced no later than phase 5 for imports, then reused.

## 13. Analytics and reports

A finance tool must show where things stand and how they move. Everything here is read-only and computed from the journal plus stored price and FX history; nothing is entered twice. Each chart has a table with the same numbers, and every report can be exported to CSV. Queries live in the read contracts (2.6), so the same data later serves the AI agents.

### 13.1 Budget

| Phase | Item |
|---|---|
| 1a | Overview: total per currency and in the reporting currency; a card per bucket with available amount. |
| 1b | Bucket cards add progress to limit, due by plan, shortfall and open loans. "Needs attention" list. |
| 2 | **Net worth over time**: all buckets in the reporting currency, monthly points (computed by replay at each month end, with that date's FX rate). |
| 2 | **Plan versus actual**: planned and actual contributions per month, per bucket and in total. |
| 2 | **Allocation**: share of each bucket in the total. |
| 2 | **Bucket history**: balance of one bucket over time with its deposits, spends and loans. |
| 2 | **Monthly report**: deposits, spends, transfers, shortfalls carried over, loans opened and repaid, change in net worth. |

### 13.2 Investments

| Phase | Item |
|---|---|
| 3 | **Portfolio value over time** against **net invested** on the same chart, so growth from the market is visibly separate from growth from contributions. Period selector (1M, 3M, 1Y, all). Deposits and withdrawals are marked on the line. |
| 3 | **Allocation** by section and by asset, compared with target shares. |
| 3 | **Positions table**: quantity, average price, current price, value, unrealized and realized PnL in amount and percent. |
| 3 | **Targets table**: next levels and distance from the current price. |
| 4 | The same for stocks and ETFs, plus dividends received per period. |

### 13.3 Later

Time-weighted return and XIRR; comparison with a benchmark; drawdown; yearly summary; tax report. These reuse the stored price history and lots, so no model change is needed.

### 13.4 Rules

- A converted figure always shows its currency and the rate date.
- A chart never draws a value for a day without a price; it shows a gap.
- Reports are generated on request. There is no scheduled report sending in the first phases (notifications are out of scope).
