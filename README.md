# MtgaDash

A local Windows dashboard for a few hundred MTG Arena accounts. It watches a folder of archived MTGA
`Player.log` files and the live log, parses logins, inventory, decks and rank into SQLite, and serves a
single-page dashboard on `http://localhost:5080` that updates live.

- **Accounts**: every account from the CSV, with gems, gold, wildcards, boosters, custom decks and a
  color-coded last login, most recent login first. Search, filter by status, sort by any column, click for
  the detail. Hover over a row to see up to 8 of the account's rares and mythics held at 3+ copies, 4-ofs
  first, as large as the window allows.
- **Stale**: accounts never logged in or not logged in for N days, oldest first, with one-click email copy.
- **Totals**: gems, gold and wildcards across all accounts, last-login buckets, and the 20 accounts with the
  most boosters, rare wildcards, draft tokens and event tokens (token boards appear once any account has
  that kind).
- **Account detail**: gems, gold, rare and mythic wildcards, vault and boosters; the account's rares and
  mythics at 3+ copies as card images (placeholders where there is no image yet); its known rares and mythics;
  and its custom decks with card lists (precons collapsed).
- **Review**: unmatched and ambiguous display names, mapped to their account with one click, and
  accounts marked unused that still hold decks or rare wildcards.
- **Status**: when the last poll ran, how far each log file has been read, recent parse warnings, and a
  warning when MTGA is running with Detailed Logs off.
- **Activity**: what the app found in the logs as you play, for troubleshooting; see below.
- **Cards**: rares and mythics each account is known to own at 3+ copies, from its saved decks; see below.

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
| `WatchPrevLog` | `true` | Also ingest `Player-prev.log` beside it: the previous session, which MTGA keeps until its next start. |
| `LogFilePattern` | `Player*.log` | Which files in `LogFolder` are logs; the archiver's own `archiver_*.log` files are skipped. |
| `PollSeconds` | `5` | How often files are checked for growth. |
| `DatabasePath` | `C:\dev\mtga\data\mtga.db` | SQLite database (WAL mode). |
| `AccountsCsvPath` | `C:\dev\mtga\data\mtga_accounts_20261112.csv` | Columns `Status, Email, Notes, Misc Notes, Note Source`. |
| `StaleDays` | `30` | Default for the Stale view and the amber/red boundary on last login. |
| `ExportFolder` | *(empty)* | When set, a static export is written 30 s after data changes. |
| `AllowLan` | `false` | Listen on `0.0.0.0` instead of `localhost`. |
| `Port` | `5080` | HTTP port. |
| `CardDbPath` | *(empty)* | MTGA's card database for card names; see below. |
| `AlertGemsDrop` / `AlertGoldDrop` / `AlertWildcardDrop` | `1000` / `5000` / `2` | How large a fall between two snapshots is listed under Inventory drops on the Review tab. |
| `AlertDays` | `30` | How far back the Review tab looks for drops. |
| `BrowserReopenMinutes` | `30` | No new browser tab within this many minutes of the last, so a crash restart opens none. |
| `OpenBrowser` | `true` | Open the dashboard in your default browser once it is running (only where there is a desktop). |
| `StartPage` | `/#/activity` | The page opened at startup and written to the console, e.g. `/#/accounts`. |
| `LaunchGame` | `true` | Start MTGA when the dashboard starts, unless it is already running; see below. |
| `GamePath` | `C:\Program Files\Wizards of the Coast\MTGA\MTGA.exe` | The game to start, or a launcher URI such as `steam://rungameid/2141910`. |
| `CardImageFolder` | *(empty)* | Where card images are kept; empty means `card-images` beside the database. |
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

Logs name cards by id only (e.g. `92275`). MTGA ships its own card database, a SQLite file named
`Raw_CardDatabase_<hash>.mtga` in `MTGA_Data\Downloads\Raw` under the install folder; `CardDbPath` points at
it and turns ids into names, rarity and set.

The easiest way: **Card names…** on the Cards tab (also on the Status tab, and `Ctrl+K` → "Set up card names").
The dialog shows whether names are on, lists the card databases it finds in the usual install folders
(MTGA's installer, Steam, Epic) with the number of cards each holds, gives the exact line to paste and the
file to paste it in (`appsettings.Production.json` beside the exe), and looks up any card id to confirm.

Manually: add to `appsettings.Production.json`

```json
{ "MtgaDash": { "CardDbPath": "C:\\Program Files\\Wizards of the Coast\\MTGA\\MTGA_Data\\Downloads\\Raw\\Raw_CardDatabase_*.mtga" } }
```

Usual install folders: `C:\Program Files\Wizards of the Coast\MTGA`, `C:\Program Files (x86)\Steam\steamapps\common\MTGA`,
`C:\Program Files\Epic Games\MagicTheGathering`. Keep the `*`: the file name changes with every MTGA update,
and the wildcard picks the newest. The file is reloaded on save, so no restart is needed. The schema is
inspected at runtime for a card table (grpId and title id) and an English localization table; if it cannot be
resolved, card ids are shown alone and the dialog says why.

## Updating

After a change is merged, one command stops the dashboard, pulls `main`, republishes and starts it again
(settings in `appsettings.Production.json`, the database and mappings are kept):

```powershell
.\tools\Update-MtgaDash.ps1 -RepoPath "C:\dev\mtga\MtgaDash" -AppPath "C:\dev\mtga\app" -TaskName "MtgaDash" -Branch "main"
```

Add `-RunTests` to run the tests first; a failure leaves the running version in place.

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

## What happens at startup

Once the dashboard is listening it writes its address to the console window:

```text
  MtgaDash is running: http://localhost:5080/#/activity
```

and opens that page (the Activity tab, setting `StartPage`) in your default browser. Set `"OpenBrowser": false`
to stop that. The hidden logon task has no desktop, so there the browser is skipped and the console line is
the only output; register the task with `-Interactive` (below) to get the browser at logon too.

## Start MTGA with the dashboard

Each time the dashboard starts it starts MTGA too, unless MTGA is already running: it never opens a second
copy. If MTGA is not in the default folder, set `GamePath` (in `appsettings.Production.json` beside the
published exe); to stop it, set `"LaunchGame": false` there. The Status tab shows whether MTGA is running and has a
**Start MTGA** button (also `Ctrl+K` → "Start MTGA"), which works even with `LaunchGame` off.

The hidden logon task has no desktop, so a game it started would be invisible; the dashboard refuses and
says so on the Status tab. To start the game at logon, register the task with `-Interactive`, which runs it
in your desktop session with the dashboard's console window hidden:

```powershell
.\tools\Register-MtgaDashTask.ps1 -ExePath "C:\dev\mtga\app\MtgaDash.exe" -TaskName "MtgaDash" -Port 5080 -Interactive
```

An interactive task runs only while you are logged on, which is when you play anyway.

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

## Login queue (Stale tab)

The top of the Stale tab is a work list for your login script: **Next** is the account that has gone longest
without a login. **Copy and next** (or `N` anywhere, or `Ctrl+K` → "Copy the next account to log in") copies
its email and moves on; **Skip** moves on without copying. An account leaves the queue on its own once its
login is read. Handed-out emails are remembered for today on this device; **Reset today's queue** starts over.

## Inventory drops (Review tab)

Each account's snapshots are compared in time order. A fall of at least 1,000 gems, 5,000 gold or 2 rare and
mythic wildcards together (settings `AlertGemsDrop`, `AlertGoldDrop`, `AlertWildcardDrop`) in the last 30
days is listed with its dates: spending you did not plan, or someone else using the account.

## Gold and gems during play

At login MTGA logs a full inventory; after a match or reward it logs changes instead (`"goldDelta": 250`).
Both are read: changes are added to that session's inventory, each exactly once however often the log is
read, and the next login's full inventory corrects any drift.

## Logins the parser recognizes

The usual `Logged in successfully. Display Name: …` line; any other plain line with `Display Name:` or
"logged in" next to a `name#1234` handle; and, when no login line was written, the player's handle in the
server's login or session response (never from match messages, which name opponents). If MTGA is being
played and none of these appear within 5 minutes of the client starting, the Status tab says so: that
session's data is being skipped, and the line to support is in the log (Activity tab, Last lines of a log).

## Activity tab: "I played and nothing changed"

The Activity tab (key `6`) shows what the app read, refreshed every 3 seconds while it is open:

- **Display names seen**: every name found in a log, with its account or **unmatched**, last login, last
  inventory, and latest gems, gold and rare/mythic wildcards. The Accounts tab lists only accounts from the
  CSV, so an unmatched name's data shows here; map it on the Review tab.
- **Messages from MTGA**: every request and response the client logged, how often, and how many the app
  read. A **highlighted** row mentions inventory (gold, gems, wildcards, boosters) but is not read yet: the
  likely reason numbers did not move after a match.
- **Recent activity**: the newest 50 logins, inventory updates and changes, deck lists, ranks, warnings and
  **not read** messages, each with its file and line. **Show all** lists up to 500.
- **Last lines of a log**: the end of any log file the app reads, long lines cut to 400 characters.

Recent activity is kept for 24 hours in the database, so a restart does not empty it; the message counts
start over with the app.

## Cards tab: rares and mythics owned at 3+ copies

MTGA no longer writes the collection to its log, but it does log a deck's full card list each time you save
the deck. A saved deck is taken as proof of ownership, so:

- an account owns **at least** the most copies of a card that any single saved deck held (main deck,
  sideboard, commander and companion added up; never summed across decks, which share copies);
- every version of every deck counts, so cutting a card from a deck later does not lower the number;
- cards never saved in a deck are missing: every number is a minimum;
- printings of the same card (reprints, alternate art) count as one card: 2 + 2 in one deck is 4 copies;
- only rares and mythics are listed (once the card database gives rarity).

To record an account's cards, open each deck in MTGA's deck builder and press **Done**. **Decks not recorded
yet**, at the bottom of the Cards tab and on each account's page, lists the custom decks still to do.

The **Cards** tab (key `7`) has two views:

- **By card**: each card held at the chosen copies (1–4), with its cost, color, type line and who holds it.
  **Click a card** to see every account that has it, grouped 4+, 3, 2 and 1 copies, with a button to copy
  each group's emails.
- **By account**: accounts ranked by how many qualifying cards they hold, each card clickable the same way.

Filters, remembered on this device and shared with each account's **Known rares and mythics** table and the
**R/M 3+** column on the Accounts table: **Type** (creature, instant, sorcery, enchantment, artifact,
planeswalker, battle, land), **Ignore lands** (on by default) and **Alchemy** (include, exclude, or only
Alchemy: rebalanced `A-` cards and the digital-only Y sets).

Names, rarity, cost, color and type come from MTGA's card database: set `CardDbPath` (above). Without it,
cards are listed by id, every rarity is included and the type filters cannot apply.

### Draft and event tokens

Each login's inventory lists the account's tokens (MTGA's `CustomTokens`). A token whose name contains
"draft" counts as a draft token and one containing "event" as an event token; they show as tiles on the
account's page and as leaderboards on Totals. Any other token is listed by name under the tiles and on
Totals, so if MTGA names a draft or event token differently it is still visible.

### Card images

Each account's page shows its rares and mythics held at 3 or more copies as card images, 4-ofs first. The
images are downloaded once, from [Scryfall](https://scryfall.com/docs/api), into `card-images` beside the
database (setting `CardImageFolder`), one JPEG per card name with the card's **newest art**. Only rares and
mythics some account holds at 3+ copies are fetched; card names need `CardDbPath`.

**Automatically:** 2 minutes after new log data stops arriving (in practice, just after a play session) and
once after the dashboard starts, missing images are downloaded in the background. Settings:
`AutoDownloadCardImages` (default `true`) and `AutoDownloadDelaySeconds` (default `120`). It only asks for
what is missing, so a quiet period with nothing new makes no requests. Thumbnails appear in the Cards tab's
By card and By account views and in each account's Known rares table. Hover over a thumbnail or a card name
on the Cards tab to see the card at full size; click it for every account holding the card.

The Status tab shows the last download, whatever started it (automatic, the button or the command line): when
it finished, and the names it saved, found no image for and failed on. It is kept in `card-images\last-run.json`,
so it survives a restart and includes command-line runs.

Two ways to fetch them by hand:

- **Download missing images** on an account's page, or on the Status tab, or `Ctrl+K` → "Download missing
  card images". Progress shows on the page.
- From a command prompt, while the dashboard runs or not:

  ```powershell
  C:\dev\mtga\app\MtgaDash.exe --download-card-images
  ```

  It prints each card as it goes and exits. Run it again after saving new decks; it only fetches what is
  missing.

Searches are paced at under 2 a second, and image files at 10 a second. If Scryfall still answers "too many
requests" (HTTP 429), the download waits as long as Scryfall's `Retry-After` header says (30 seconds if it says
nothing) and tries once more. If Scryfall refuses again, the run stops and no download starts for 30 minutes,
because an address that keeps going after a 429 risks a ban. The Status tab and the account page show until
when; cards not reached stay missing and are fetched by the next run. A card Scryfall has no image for yet (a brand-new or
digital-only card) shows a placeholder with its name, cost and type, and is asked for again after a week. The
list of missing names is also at `http://localhost:5080/api/cardimages/missing` (one per line) for any other
tool. The static export shows placeholders: images are not embedded.

### Starter deck names

Decks every account has are treated like precons. Hidden from the start: Aerial Domination, Cold-Blooded
Killers, Destructive Firepower, Forest's Might, Goblins Everywhere!, Imported Deck, Keep the Peace, Large and
in Charge and Out for Blood. Hidden decks are not counted as custom decks, not listed as decks to record, and are folded into the precon count. **Deck
names** at the bottom of the Cards tab lists the custom deck names found on the most accounts with a **Hide**
button, and the hidden ones with **Show**; each deck on an account's page also has **Hide this name**.

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
| `1` … `7` | Accounts, Stale, Totals, Review, Status, Activity, Cards |
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
