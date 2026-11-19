# Local setup

What a machine needs to build, test and run FinTrack locally. Docker is optional and never required.

Check a machine at any time with:

```
pwsh scripts/check-prereqs.ps1
```

It looks for each tool on PATH and in the default install locations, reads the MongoDB replica-set state, and exits with 1 when anything required is missing. It only reads; it never changes anything. Pass `-MongoUri` to check another server (default: the `FINTRACK_TEST_MONGO` environment variable, otherwise `mongodb://localhost:27017/?replicaSet=rs0`).

## What is needed and why

| Tool | Version | Why |
|---|---|---|
| PowerShell | 7 or later | The scripts in `scripts/` (Windows, Linux, macOS). |
| .NET SDK | as in `global.json` (10.0.404 or a later 10.0 patch or feature band) | Builds and tests everything. |
| MongoDB Community Server | 8.0 or later, running as a **single-node replica set** | The only database. |
| mongosh | 2.x | MongoDB shell; used to set up the replica set and by the check script. |
| Azure Functions Core Tools | 4.x (`func`) | Runs the back end (`FinTrack.Api`) locally. |
| Node.js | current LTS | Azurite is an npm package and runs on Node.js. |
| Azurite | 3.x | Local emulator of Azure Storage. |
| Docker | any | Optional, see [Docker instead](#docker-instead-optional). |

Why each piece matters:

- **Replica set.** MongoDB allows a transaction across several documents and collections only on a replica set, never on a standalone server. This differs from SQL Server, where every database has transactions. FinTrack writes journal entries, the ledger version and outbox events in one transaction (ARCHITECTURE 2.3), so a replica set is required. One member is enough: it has no copies and does no extra work, it just turns transactions on.
- **Server version.** 8.0 is the oldest supported version: it matches the MongoDB Atlas free cluster, and CI tests against it. A newer local server (for example 9.0) is fine; CI on 8.0 catches the use of anything newer.
- **Functions Core Tools.** In the cloud the Azure Functions host starts the app on demand. Running the Api project (`dotnet run`) starts the same host on your machine through Core Tools, so the code path is identical.
- **Azurite.** The Functions host keeps the state of timer triggers (for example the outbox sweep) in Azure Storage. Locally Azurite plays that role. No real Azure account is involved.

## Windows

1. **.NET SDK**: install the version from `global.json` from https://dotnet.microsoft.com.
2. **MongoDB Community Server**: install the MSI from https://www.mongodb.com/try/download/community with "Install MongoDB as a Service". The installer puts `mongod.exe` in `C:\Program Files\MongoDB\Server\<version>\bin` and does **not** add it to PATH; that is fine.
3. **mongosh**: install from https://www.mongodb.com/try/download/shell (default location `%LOCALAPPDATA%\Programs\mongosh`).
4. **Turn the server into a replica set** (once, see below).
5. **Node.js**: install the LTS version from https://nodejs.org.
6. **Azurite**: `npm install -g azurite` (installs into `%APPDATA%\npm`).
7. **Functions Core Tools**: install the v4 MSI (see https://learn.microsoft.com/azure/azure-functions/functions-run-local). It lands in `C:\Program Files\Microsoft\Azure Functions Core Tools`.
8. Run `pwsh scripts/check-prereqs.ps1`.

## Linux and macOS

Install the same tools with the package manager the vendors document (MongoDB's own repository or Homebrew tap, `npm install -g azurite`, Functions Core Tools v4 from Microsoft's repository or Homebrew). The check script also looks in `/usr/local/bin`, `/usr/bin`, `/opt/homebrew/bin` and `~/.local/bin`. The replica-set steps are the same; the configuration file is usually `/etc/mongod.conf` (Homebrew: `$(brew --prefix)/etc/mongod.conf`).

## Turn MongoDB into a single-node replica set

Skip this when the check script already reports the replica set as `OK`.

1. Open the server's configuration file as administrator. On Windows it is `C:\Program Files\MongoDB\Server\<version>\bin\mongod.cfg`. Add (or uncomment) the replication section:

   ```yaml
   replication:
     replSetName: rs0
   ```

   Keep `net.bindIp: 127.0.0.1` so the server is not reachable from the network.

2. Restart the service. Windows (administrator PowerShell): `Restart-Service MongoDB`. Linux: `sudo systemctl restart mongod`. Homebrew: `brew services restart mongodb-community`.

3. Initiate the set **once**:

   ```
   mongosh "mongodb://127.0.0.1:27017/?directConnection=true" --eval "rs.initiate({ _id: 'rs0', members: [{ _id: 0, host: '127.0.0.1:27017' }] })"
   ```

   `directConnection=true` is needed because the set does not exist yet, so the shell cannot discover it by name. After a few seconds, `rs.status()` shows the member as `PRIMARY`. The setting survives restarts; never run `rs.initiate()` again.

4. From now on, connect with `mongodb://localhost:27017/?replicaSet=rs0`. This is also the default of the integration tests (`FINTRACK_TEST_MONGO`).

## Where data lives

- MongoDB keeps its data in `storage.dbPath` of its configuration file (Windows default: `C:\Program Files\MongoDB\Server\<version>\data`), outside the repository. Never point it inside the repository folder.
- Integration tests create one uniquely named database per run and drop it afterwards; they never touch other databases.
- When a new major MongoDB version is installed, the installer creates a new folder; the data stays where `dbPath` points. Read MongoDB's upgrade notes first: a server cannot open data files written by a newer major version.
- The app's database is `fintrack` (`Mongo__DatabaseName` in `local.settings.json`); all local data belongs to the owner `FinTrack__LocalOwnerId` there, which must never change. Running the Api against another database for a test: set the environment variable `Mongo__DatabaseName` before `run-local.ps1`.
- There is no backup feature yet (scheduled `mongodump` and app exports are planned, ARCHITECTURE 2.4). How to back up and restore by hand with MongoDB Database Tools: [GETTING-STARTED.md](GETTING-STARTED.md#backups). Keep backups outside the repository as well.

## Run everything

```
pwsh scripts/run-local.ps1
```

It runs the prerequisite check, creates `src/Api/FinTrack.Api/local.settings.json` from the example if it is missing, starts Azurite (or uses one that already runs, for example Rider's), the Api and the Web app in this console, waits until both answer and prints the address (`http://localhost:5180`). Ctrl+C, or one of them stopping, stops everything it started. Azurite's data goes to `%LOCALAPPDATA%\FinTrack\azurite` (Linux and macOS: `~/.local/share/FinTrack/azurite`); `-AzuriteLocation` chooses another folder outside the repository. The script stops with a message when port 7071 or 5180 is already in use.

The sections below do the same by hand.

## Run the Api

1. Copy `src/Api/FinTrack.Api/local.settings.example.json` to `local.settings.json` in the same folder (it is ignored by git). Keep the invented `FinTrack__LocalOwnerId` or choose any other GUID once; it is the owner of all local data, so do not change it later.
2. Start Azurite (`azurite --silent --location <a folder outside the repository>`). The Functions host keeps timer state in it.
3. From `src/Api/FinTrack.Api`, run `dotnet run`. It builds the project and starts the Functions host through Core Tools on port 7071; Core Tools 4.16 recommends this over `func start` for .NET isolated apps.
4. Open `http://localhost:7071/api/v1/health`: `{"status":"healthy",...}`. With MongoDB stopped it answers 503 with `application/problem+json` and code `health.database_unavailable`.

## Run the Web app

With the Api running, from `src/Web/FinTrack.Web` run `dotnet run`, then open `http://localhost:5180`. A new database leads to the first-run screen; **Settings → System status** (`/settings/status`) shows whether the Api and MongoDB answer. How to use the app: [GETTING-STARTED.md](GETTING-STARTED.md). The Api address is in `wwwroot/appsettings.json`; the Api allows the origins `http://localhost:5180` and `https://localhost:7180` (CORS in `local.settings.json`).

In Rider, the Web run configuration needs a Chromium-based browser (Edge or Chrome) under "Browser / Live Edit": Rider debugs Blazor WebAssembly through the browser's DevTools and refuses the "default system browser".

## Docker instead (optional)

`docker-compose.yml` in the repository root starts MongoDB 8.0 as a single-node replica set (it initiates itself on first start) and Azurite, both bound to `127.0.0.1` with data in Docker volumes:

```
docker compose up -d
pwsh scripts/check-prereqs.ps1
```

Use it **instead of** a locally installed MongoDB service, not next to it: both use port 27017. `docker compose down` stops the containers and keeps the data; `docker compose down -v` deletes it. The .NET SDK and Functions Core Tools are still needed on the machine itself.

## Troubleshooting

| The check reports | Meaning and fix |
|---|---|
| `not found on PATH or in the default install locations` | The tool is not installed, or installed somewhere unusual. Install it, or add its folder to PATH. |
| `MongoDB is not reachable` | The service is stopped or listens on another port. Windows: `Get-Service MongoDB`, `Start-Service MongoDB`. |
| `runs standalone` | The configuration has no `replication.replSetName`. Follow the replica-set steps above. |
| `the set is not initiated` | The name is configured but `rs.initiate()` was never run. Run step 3 above. |
| `does not connect to it; check the host and replicaSet name` | The set name in the connection string differs from `replSetName`, or the member's host name is not reachable from this machine. |
| Azurite `cannot run because Node.js is not on PATH` | Azurite runs on Node.js. Add the Node.js folder to PATH. |
| Another service already uses port 27017 | Only one MongoDB may listen on a port. Stop the other service or the Docker container, or give one of them another port. |
