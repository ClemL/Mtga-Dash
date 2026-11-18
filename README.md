# MtgaDash

A local Windows dashboard for a few hundred MTG Arena accounts. It watches a folder of archived MTGA
`Player.log` files and the live log, parses logins, inventory, decks and rank into SQLite, and serves a
single-page dashboard on `http://localhost:5080` that updates live.

- **Accounts**: every account from the CSV, with gems, gold, wildcards, boosters, custom decks and a
  color-coded last login. Search, filter by status, sort by any column, click for the detail.
- **Stale**: accounts never logged in or not logged in for N days, oldest first, with one-click email copy.
- **Totals**: gems, gold and wildcards across all accounts, boosters by set, last-login buckets.
- **Account detail**: gems/gold and rare/mythic history charts, latest boosters, rank, custom decks with
  card lists (precons collapsed).
- **Review**: unmatched and ambiguous display names, mapped to their account with one click, and
  accounts marked unused that still hold decks or rare wildcards.
- **Status**: when the last poll ran, how far each log file has been read, recent parse warnings, and a
  warning when MTGA is running with Detailed Logs off.
- **Activity**: what the app found in the logs as you play, for troubleshooting; see below.

Everything runs offline: no npm, no CDN, no build step for the page.

## Prerequisites

- Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the runtime
  alone is enough for a published copy).
- MTGA with **Detailed Logs (Plugin Support)** enabled (Options → Account) for inventory and decks. With it
  off, only logins are recorded.
- Optional: Node.js on the PATH runs the tests of the page's JavaScript; without it they report Inconclusive.

## First run

```bash
git clone https://github.com/ClemL/Mtga-Dash.git C:\dev\mtga\MtgaDash
```

```bash
cd C:\dev\mtga\MtgaDash
```

```bash
dotnet run --project src/MtgaDash
```

Then open <http://localhost:5080>. On first start the app creates `C:\dev\mtga\data\mtga.db`, imports the
accounts CSV if the database has no accounts, and ingests every `*.log` in `C:\dev\mtga\logs` plus the live
log. The console reports files, bytes, logins and snapshots as it goes.

`nuget.config` clears every package source but nuget.org, so a private feed in your global NuGet config
cannot break the restore.

## Configuration

All settings are in `src/MtgaDash/appsettings.json` (next to `MtgaDash.exe` once published), under
`MtgaDash`. Paths may contain environment variables such as `%USERPROFILE%`; missing folders are created.
Any setting can be overridden on the command line, e.g. `--MtgaDash:Port=5090`.

| Setting | Default | What it does |
|---|---|---|
| `LogFolder` | `C:\dev\mtga\logs` | Every `*.log` here is ingested. The archiver writes `Player_yyyyMMdd_HHmmss.log`. |
| `LiveLogPath` | `%USERPROFILE%\AppData\LocalLow\Wizards Of The Coast\MTGA\Player.log` | MTGA's own log. |
| `WatchLiveLog` | `true` | Also ingest the live log. |
| `PollSeconds` | `5` | How often files are checked for growth. |
| `DatabasePath` | `C:\dev\mtga\data\mtga.db` | SQLite database (WAL mode). |
| `AccountsCsvPath` | `C:\dev\mtga\data\mtga_accounts_20261112.csv` | Columns `Status, Email, Notes, Misc Notes, Note Source`. |
| `StaleDays` | `30` | Default for the Stale view and the amber/red boundary on last login. |
| `ExportFolder` | *(empty)* | When set, a static export is written 30 s after data changes. |
| `AllowLan` | `false` | Listen on `0.0.0.0` instead of `localhost`. |
| `Port` | `5080` | HTTP port. |
| `CardDbPath` | *(empty)* | MTGA's card database for card names; see below. |
| `ExceptionsPath` | *(empty)* | Where Review-tab mappings are saved; empty means `account-exceptions.json` beside the database. |

### How ingestion works

Each poll compares every file's length and a hash of its first 4 KB with what was last processed:

- grown, same start → read only the new bytes and continue with the saved parser state;
- shrunk, or a different start (MTGA replaced the live log) → re-parse from the beginning.

Only complete lines are parsed; a half-written last line waits for the next poll. Every write is an upsert
on a natural key, so re-ingesting the same content (the live log and its archived copy, or a re-parse)
never creates duplicates. Within one session a later log line wins, so a lagging archive copy cannot roll
an inventory back. Decks seen in an older file never overwrite a newer version.

Deck lists are not deleted when a deck disappears from the client's summary: an old archive re-parsed
later would otherwise resurrect or remove decks depending on order.

### Accounts and matching

Emails are matched to MTGA display names by alias: `base+Allerclear@gmail.com` has alias `allerclear`,
which matches `Allerclear#92603`. An alias shared by two accounts is **ambiguous**; a display name with no
alias match is **unmatched**. Both appear on the Review tab.

**Re-import** (Accounts tab, or the palette) upserts the CSV by email and never deletes an account.

### Adding an exception mapping

On the Review tab:

- **Ambiguous alias**: choose **Use this email** beside the right candidate.
- **Unmatched name**: type or pick the account's email in the box and choose **Map**. An email that is not
  in the accounts CSV is refused, so a typo cannot create a mapping to nothing.

The mapping applies at once and is saved in `account-exceptions.json` beside the database (setting
`ExceptionsPath`), so it survives rebuilds, republishing and CSV re-imports. **Exception mappings** on the
same tab lists every mapping with a **Remove** button. The file can also be edited by hand; changes are
picked up on the next request. A file that is not valid JSON is ignored, logged, and never overwritten.

Mappings can also be compiled in: paste a line such as `["Corsair#12345"] = "base2+corsair@example.com",`
into `DisplayNameToEmail` in `src/MtgaDash/Accounts/AccountExceptions.cs`, then rebuild and restart. When
both name the same display name, the file wins. `ExcludedEmails` (in either place) lists accounts the alias
rules must never match on their own. An exception always beats the alias rules.

### Card names (optional)

Set `CardDbPath` to MTGA's card database, for example
`C:\Program Files\Wizards of the Coast\MTGA\MTGA_Data\Downloads\Raw\Raw_CardDatabase_*.mtga`. The wildcard
picks the newest file. The schema is inspected at runtime for a card table (grpId and title id) and an
English localization table; if it cannot be resolved, card ids are shown alone and the console says why.

## Publish and run at logon

```bash
dotnet publish src/MtgaDash -c Release -r win-x64 --self-contained false -o C:\dev\mtga\app
```

Register a Task Scheduler task that starts it hidden at logon (from an elevated PowerShell):

```powershell
.\tools\Register-MtgaDashTask.ps1 -ExePath "C:\dev\mtga\app\MtgaDash.exe" -TaskName "MtgaDash" -Port 5080
```

The task runs as you with logon type S4U, so no window appears, restarts the app up to three times on
failure and never times out. `-Unregister` removes it. The app also supports running as a Windows service
(`sc.exe create MtgaDash binPath= "C:\dev\mtga\app\MtgaDash.exe"`), but the logon task is the tested path.

## LAN access

Set `"AllowLan": true` in `appsettings.json`, restart, and allow the port through the firewall (elevated
PowerShell):

```powershell
New-NetFirewallRule -DisplayName "MtgaDash 5080" -Direction Inbound -Protocol TCP -LocalPort 5080 -Action Allow -Profile Private
```

There is no authentication: enable this only on a private network. Over plain HTTP the browser blocks the
clipboard API, so Copy buttons fall back to an older copy method.

To keep the protection against DNS rebinding (below) on the LAN, list the names you browse to in
`appsettings.json`, e.g. `"AllowedHosts": "localhost;127.0.0.1;MYPC;192.168.1.20"`. Without that line any
host name is accepted while `AllowLan` is on.

## Security

The app has no login, so it protects itself from other websites open in the same browser:

- **Host check**: with `AllowLan` off, only requests addressed to `localhost`, `127.0.0.1` or `[::1]` are
  answered. A page that points its own domain at your PC (DNS rebinding) gets `400 Bad Request` instead of
  your account list.
- **Write header**: every request that changes something (re-import, export, mappings) must carry the
  header `X-MtgaDash: 1`, which the page adds. Another site can send a plain form POST to localhost, but
  not a custom header, so it cannot trigger those actions.
- **Export is POST-only**, so an image tag on another page cannot write files.

## Status tab

The Status tab (key `5`) shows the last poll and how long it took, every log file with its size, how much
has been read, who is logged in and whether inventory arrived, and the parse warnings of the last 24 hours.
Its tab shows a red count when something needs attention:

- a logged-in session in a file written in the last 15 minutes, with no inventory 5 minutes after the
  client started: MTGA's **Detailed Logs (Plugin Support)** is off;
- a file that could not be read;
- no poll for a while (ingestion stuck);
- the live log missing at `LiveLogPath`.

## Activity tab: "I played and nothing changed"

The Activity tab (key `6`) shows what the app read, refreshed every 3 seconds while it is open:

- **Display names seen**: every name found in a log, with its account or **unmatched**, last login, last
  inventory, and latest gems, gold and rare/mythic wildcards. The Accounts tab lists only accounts from the
  CSV, so an unmatched name's data shows here; map it on the Review tab.
- **Messages from MTGA**: every request and response the client logged, how often, and how many the app
  read. A **highlighted** row mentions inventory (gold, gems, wildcards, boosters) but is not read yet: the
  likely reason numbers did not move after a match.
- **Recent activity**: the newest 50 logins, inventory updates, deck lists, ranks, warnings and
  **not read** messages, each with its file and line. **Show all** lists up to 500.
- **Last lines of a log**: the end of any log file the app reads, long lines cut to 400 characters.

The activity is kept in memory and starts empty when the app restarts. Nothing in it is written to disk.

## Static export (phone, PC off)

With `ExportFolder` set, 30 s after data changes the app writes:

- `mtga_dashboard.html`: the whole dashboard with the data embedded, read-only, no network needed;
- `mtga_export_yyyyMMdd_HHmm.json`: the same data as JSON.

The full paths are printed after each export. **Write the static export** in the command palette (or
`POST /api/export`) exports immediately, into `export` beside the data folder if `ExportFolder` is empty.

## Command palette and shortcuts

`Ctrl+K` (or `⌘K`) opens a palette that lists every action with its shortcut and, below the commands,
the accounts matching what you type. Enter runs the highlighted row; **Show only the n accounts matching…**
applies the text as the Accounts filter, which the search box in the header then shows with a ✕ to clear.

| Key | Action |
|---|---|
| `Ctrl+K`, `/` | Open the palette on the current filter |
| `Ctrl+Shift+P` | Open the palette empty |
| `1` `2` `3` `4` `5` `6` | Accounts, Stale, Totals, Review, Status, Activity |
| `Esc` | Back to Accounts from an account; closes any dialog |
| `F` | Focus the table filter |
| `R` | Refresh now |
| `C` | Copy the current account's email |

Single-key shortcuts are ignored while typing in a field; `Ctrl+K` and `Ctrl+Shift+P` work everywhere.
Firefox reserves `Ctrl+Shift+P` for a private window, so use `Ctrl+K` there.

## Settings

**Settings** in the header changes three things, each remembered on this device (browser storage):

- **Theme**: System (follows the device), Light, Dark, OLED (true black) and High contrast.
- **Density**: Compact, Normal or Cozy: spacing, and at Cozy, larger touch targets.
- **Text size**: 90%, 100%, 115% or 130%: type only, independent of density.

They are applied before the first paint, so the page never flashes the defaults.

## Changelog

The footer shows the version, the build time and the newest entry of `src/MtgaDash/wwwroot/updates.txt`;
clicking the entry lists them all, newest first. The file has one entry per line, **oldest first**, so
shipping one is an append:

```text
2026-11-14T16:22:00Z - What changed, as a plain sentence
```

Get the timestamp with `date -u +%Y-%m-%dT%H:%M:00Z`. A missing or empty file only hides the entry.

## Development

```bash
dotnet build MtgaDash.sln
```

```bash
dotnet test MtgaDash.sln
```

CI (`.github/workflows/ci.yml`) runs the same two commands on `windows-latest`, with Node for the page's
JavaScript tests, on every pull request and on pushes to `main`.

Tests that read the real logs in `testdata/` (`Player.log`, `Player-prev.log`, the accounts CSV) report
Inconclusive when those files are absent. `testdata/`, `data/`, `logs/`, `export/`, `*.csv` and `*.db` are
git-ignored because they contain email addresses.
