# Getting started

How to start using FinTrack next to your spreadsheet (phase 1a). Setting up the machine is in [LOCAL-SETUP.md](LOCAL-SETUP.md).

## Start the app

```
pwsh scripts/check-prereqs.ps1   # is this machine ready?
pwsh scripts/run-local.ps1       # starts everything; Ctrl+C stops it
```

Open http://localhost:5180. The app runs only while the script runs.

## First run

The first screen asks for three settings, prefilled from your browser:

- **Reporting currency** (PLN, EUR or USD): totals across currencies are shown in it. You can switch it at any time.
- **Time zone**: decides which day is "today", the default date of a new entry and the latest one allowed.
- **Number and date format**: how amounts and dates are written (`1 234,56 PLN` or `PLN 1,234.56`). The texts stay in English. Changing it later restarts the app.

All three can be changed later under **Settings**. **Settings → System status** shows whether the app reaches its server and database.

## Set up your buckets

On **Buckets**, create one bucket for each purpose you set money aside for (a cushion, a car, a holiday). Each bucket has one currency, which cannot change later. Choose **Investment** for money put into a portfolio: it records what you put in (no market value yet), and spending from it is not possible.

A bucket you no longer need can be archived on **Buckets** once it is empty: it disappears from the lists and accepts no entries, but keeps its history. Nothing is archived automatically; a bucket you have emptied stays active. **Show archived** lists archived buckets, and **Unarchive** makes one active again with all its entries (if another bucket has taken its name meanwhile, rename one of them first).

You never create **Unallocated** yourself: there is one per currency for money that has arrived but is not distributed yet, created the first time an entry names it. It cannot be renamed or archived.

## Enter your starting point

Choose a start day, for example the day of your spreadsheet's latest figures. Then, with **Add entry** (on the Overview or the Journal):

1. **Opening balance** for each bucket: what it held on the start day, dated that day. One per bucket; it is not counted as income.
2. From then on, record what happens, on the day it happened:
   - **Deposit**: new money coming in, into a bucket or into Unallocated. Deposits are your income.
   - **Transfer**: money moved between buckets. Between currencies, type the amount that arrived or the rate; the other is shown before you confirm. A fee is part of the amount sent.
   - **Spend**: money that left for good.

Every entry shows a summary before it is saved; nothing is written until you confirm. Dates can be in the past but never after today. A savings bucket can never go below zero on any day, so enter the money before the spending (an entry that would break this is refused and names the day).

The **Overview** shows each bucket and the totals; the **Journal** lists every entry.

## Fix a mistake

Entries are never edited or deleted. To correct one, reverse it in the **Journal** (it then counts as if it never happened, on its original date) and add the right entry. A reversal cannot be undone. If reversing an entry would leave a savings bucket below zero on some day (for example a deposit that was spent later), it is refused: reverse the later entry first.

## Exchange rates

Totals across currencies use daily central-bank reference rates from the free Frankfurter service, fetched at most once a day and kept in the database. Offline, the app uses the newest rates it has and shows their date. Until it has fetched rates once, the total in your reporting currency is left out with an explanation; each currency's total is still shown.

## Where your data lives

- In the local MongoDB server, database **`fintrack`**, in the server's data folder (`storage.dbPath` of its configuration; on Windows by default `C:\Program Files\MongoDB\Server\<version>\data`). Nothing is stored in the repository folder.
- All local data belongs to one owner, the ID `FinTrack__LocalOwnerId` in `src/Api/FinTrack.Api/local.settings.json` (created by `run-local.ps1` on first start, ignored by git). **Never change it**: with another ID the app would start empty, as for a new user (the data is still there, under the old ID).

## Backups

FinTrack has no backup feature yet (planned). Until then, back up by hand with MongoDB's own tools, which are a separate download: **MongoDB Database Tools** (https://www.mongodb.com/try/download/database-tools; they provide `mongodump` and `mongorestore`). The Windows installer puts them in `C:\Program Files\MongoDB\Tools\<version>\bin` and does not add that folder to PATH: add it, or call the tools by their full path (`& "C:\Program Files\MongoDB\Tools\100\bin\mongodump.exe" ...` in PowerShell).

Back up into a folder outside the repository, for example once a week and before upgrading MongoDB or FinTrack:

```
mongodump --uri "mongodb://localhost:27017/?replicaSet=rs0" --db fintrack --out "D:\Backups\FinTrack\2026-11-19"
```

Restore a backup (this **replaces** the current `fintrack` database with the backup's content):

```
mongorestore --uri "mongodb://localhost:27017/?replicaSet=rs0" --nsInclude "fintrack.*" --drop "D:\Backups\FinTrack\2026-11-19"
```

A backup keeps every document exactly, decimal amounts with their scale, and the indexes. Keep backups on another disk or in your own cloud storage; a backup on the same disk does not survive the disk.

## Not there yet

Phase 1a is the first usable version. Contribution plans, limits and loans between buckets arrive in phase 1b; reports and charts in phase 2; crypto and stock portfolios from phase 3 ([ARCHITECTURE.md](ARCHITECTURE.md), section 8). Keep your spreadsheet running alongside and compare the two for a while.
