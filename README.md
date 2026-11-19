# FinTrack

A personal finance tracker for planning savings and tracking investments. Money is split into buckets (emergency cushion, car, vacation, investments), each with contribution plans, limits and self-loans. Investment buckets track crypto first, then stocks and ETFs.

FinTrack runs entirely on your own machine. The cloud deployment is optional.

> **Status:** phase 1a, the first usable version, is complete: buckets in several currencies, opening balances, deposits, spends, transfers (also between currencies), corrections by reversal, an overview with totals in your reporting currency, and the journal. It is meant to be used next to your existing spreadsheet for now; plans, limits and loans follow in phase 1b. See [docs/GETTING-STARTED.md](docs/GETTING-STARTED.md).

## Principles

- **Your data stays yours.** Everything is stored in your own database. The app only calls the internet for exchange rates and asset prices.
- **History is never lost.** Every operation is written to an append-only journal; mistakes are fixed by reversal entries, not by editing.
- **Manual first.** You enter your data yourself. Read-only imports from exchanges and brokers are planned.
- **Free to run.** Local use costs nothing; the cloud version is designed to stay within free tiers.

## Planned features

- Buckets in any currency, totals in a reporting currency of your choice
- Monthly contribution plans with carry-over of shortfalls, pauses and limits
- Self-loans between buckets with repayment plans
- Crypto portfolio: sections (core, active, reserve), positions, average price, profit and loss, target levels
- Stocks and ETFs
- Charts and reports: net worth over time, plan versus actual, portfolio value versus amount invested
- Later: read-only imports, AI-assisted analysis of your own portfolio

The full specification is in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). UI rules are in [docs/DESIGN.md](docs/DESIGN.md).

## Tech stack

.NET 10, Azure Functions (isolated worker), Blazor WebAssembly with MudBlazor, MongoDB.

## Running locally

Prerequisites:

- .NET 10 SDK
- Azure Functions Core Tools
- MongoDB Community 8.0 or later, running as a single-node replica set
- Node.js and Azurite (Azure Storage emulator)

Setup instructions are in [docs/LOCAL-SETUP.md](docs/LOCAL-SETUP.md). Docker is optional (`docker-compose.yml`).

Then, from the repository folder (PowerShell 7):

```
pwsh scripts/check-prereqs.ps1   # is this machine ready?
pwsh scripts/run-local.ps1       # starts Azurite, the Api and the Web app; Ctrl+C stops all
```

Open http://localhost:5180. The first run asks for your reporting currency, time zone and number format; [docs/GETTING-STARTED.md](docs/GETTING-STARTED.md) explains how to start, where the data lives and how to back it up (there is no backup feature yet). **Settings → System status** shows whether the Api and the database answer.

Keep your database and backups outside the repository folder.

## Disclaimer

FinTrack is a tracking and planning tool. Nothing it shows is financial, investment or tax advice. Prices and exchange rates come from third-party sources and may be delayed or wrong. Use at your own risk.

## Contributing

Bug reports and ideas are welcome as issues. Pull requests are not accepted yet.

## License

Copyright (C) 2026 Hryhorii Kyrylchenko

FinTrack is free software: you can redistribute it and/or modify it under the terms of the GNU Affero General Public License as published by the Free Software Foundation, version 3. See [LICENSE](LICENSE).

In short: you may use, study, modify and share it. If you run a modified version as a service for other people, you must make your modified source code available to them under the same license.
