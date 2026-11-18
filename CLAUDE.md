# CLAUDE.md

Guidance for Claude Code (and anyone else) working in this repository.

## Working agreements

* **Always start a new branch for each request.** Do not commit directly to `main`, and do not stack an
  unrelated request's changes onto a branch created for a previous one. If a request's PR has already been
  merged, restart from the latest `main`.
* **Open a pull request when the work is done**, against `main`, summarizing what changed and why.
* **Verify before opening the PR.** CI (`.github/workflows/ci.yml`, `windows-latest`) runs the same two;
  run them locally first:
  * `dotnet build MtgaDash.sln`
  * `dotnet test MtgaDash.sln` — the page's JavaScript tests need `node` on the PATH (Inconclusive otherwise)
* **Add a line to `src/MtgaDash/wwwroot/updates.txt` when you ship a user-visible change.** Newest last;
  the footer reads the file and shows the final line. Internal changes (refactors, tests, tooling) do not
  get a line. Format: `<date -u +%Y-%m-%dT%H:%M:00Z> - <what changed>`.
* **Register every new action in the command palette** (`buildCommands` in `index.html`), with its hotkey
  if it has one, built from the same handler the button calls.
* **Never commit personal data.** Logs, the accounts CSV and the database hold the owner's email addresses;
  `testdata/`, `data/`, `logs/`, `export/`, `*.csv` and `*.db` are git-ignored for that reason. Test
  fixtures are synthetic (`tests/MtgaDash.Tests/Fixtures.cs`) and use `example.com` or made-up names.
* **Post the links at the end of every response:** the repo, https://github.com/ClemL/Mtga-Dash, and the
  pipeline, https://github.com/ClemL/Mtga-Dash/actions. The app runs only on the owner's PC
  (`http://localhost:5080`); there is no hosted app.
* **Comments explain *why*, not *what*.**

## What this project is

A local Windows dashboard for ~700 MTG Arena accounts. A `BackgroundService` polls archived and live MTGA
`Player.log` files, parses them into SQLite, and an ASP.NET Core minimal API serves one vanilla-JS page that
refreshes over Server-Sent Events. .NET 10, `Microsoft.Data.Sqlite` + Dapper with hand-written SQL (no EF
Core), MSTest. The page must work offline: no npm, no CDN, no build step. See [README.md](README.md) for
settings, publishing and the Task Scheduler setup.

## Layout

```
src/MtgaDash/
  AppHost.cs    builds the app: DI, host filtering, every endpoint (Program.cs only calls it)
  Parsing/      LogParser (pure: lines in, typed events out), Json helpers
  Ingestion/    LogIngestor (offsets, rotation, batching), IngestionService (poll loop), ChangeNotifier (SSE),
                StatusTracker (Status tab), ActivityLog (Activity tab: in-memory feed and message counts)
  Data/         Database (schema + migrations), Writer (upserts, matching), Queries (read side)
  Accounts/     AccountMatcher (pure), AccountsCsv, AccountExceptions (compiled overrides),
                ExceptionStore (Review-tab overrides in account-exceptions.json)
  Cards/        CardNames: optional names, rarity and set from MTGA's own SQLite card DB, schema found at
                runtime; KnownCollection (pure): cards owned, as a minimum, from saved deck lists
  Web/          PageRenderer (fills index.html placeholders), StaticExporter, DisplayOptions, BuildInfo
  wwwroot/      index.html (the whole UI), updates.txt (changelog)
tests/MtgaDash.Tests/   MSTest; Fixtures.cs builds synthetic logs
tools/Register-MtgaDashTask.ps1   logon task for the published exe
```

## Conventions that matter here

* **The parser is pure.** `LogParser` takes lines and returns events; no I/O, no clock, the time zone is
  injected. Log-format changes belong there, with a test in `LogParserTests`.
* **The parser reports every message, read or not** (`MessageSeen`), so the Activity tab shows what MTGA
  writes. When you add support for a message, check its Activity row turns from "not read" to read.
* **Owned counts are a minimum, from saved decks.** `KnownCollection` takes the most copies in any one deck
  version, never a sum across decks. MTGA's rarity numbers are mapped in `CardNames.RarityName`; an
  unknown value stays null rather than guessed.
* **Every write is an idempotent upsert on a natural key.** Re-ingesting a file, or the live log and its
  archived copy, must change nothing. `IngestionTests.Dump` compares whole databases: use it for new tables.
* **Matching is recomputed from scratch** (`Writer.RunMatching`) on startup, on import, when a new display
  name appears and when a mapping changes. Exceptions (compiled ones overlaid by `ExceptionStore`'s file) are
  applied first, so nothing can overwrite them. Pass the store to every `RunMatching` and `Review` call.
* **Every state-changing endpoint goes in the `write` group in `AppHost`**, which requires the `X-MtgaDash: 1`
  header, and the page sends it through `mutate()`. Never add a GET that changes state: another site can
  trigger a GET with an image tag. `ApiTests` covers both rules.
* **Schema changes are a new entry at the end of `Database.Migrations`.** Never edit an applied migration:
  existing databases on the owner's PC would not get the change.
* **The page builds DOM with `h()` and `textContent`, never `innerHTML`.** Notes, emails and deck names come
  from a CSV and logs.
* **Pure page helpers live in `<script id="pure">`** and are unit-tested by extracting that block and running
  it in Node (`PageTests`). Keep DOM and browser APIs out of it.
* **Every font size is `calc(<n>px * var(--text-scale))`, and spacing uses the density variables.** A fixed
  size or padding ignores the Settings menu; `PageTests` fails on a fixed font size.
* **Display settings are applied before first paint** by the boot script that `DisplayOptions.BootScript()`
  generates from the themes, densities and text-size lists. Adding a theme means a list entry there and its
  CSS block in `index.html`.
* **Charts are inline SVG with no library.** Two measures of different scale get two charts, never two axes.

## Things that will bite you

**The content root is the exe's folder, not the working directory.** Task Scheduler and the service manager
start the app in `C:\Windows\System32`; `Program.cs` sets `ContentRootPath = AppContext.BaseDirectory`, and
the csproj copies `wwwroot` and `appsettings.json` into the build output for that reason.

**MTGA holds `Player.log` open.** Open log files with `FileShare.ReadWrite | FileShare.Delete`, and rely on
polling: `FileSystemWatcher` misses writes to a file another process holds open.

**A small file changes its "first 4 KB" just by growing.** `IngestedFiles.FirstBytesHash` is stored as
`<bytes hashed>:<sha256>` and compared over that many bytes; hashing a fixed 4096 would flag every growing
small file as rotated.

**Within a session, a later log line wins, not a later ingest.** Snapshots and ranks carry `SourceLine`;
without it a lagging archive copy ingested after the live log rolled the inventory back to StartHook's.

**`Startup Timestamp` is local time; `ServerTimeInternal` is UTC.** The parser converts the former with the
injected zone; tests pin UTC so they pass on any machine.

**Dapper maps records by constructor, with exact types.** SQLite integers are `Int64`: a `bool` or `int`
parameter in a query record throws at runtime (`DeckRow.IsPrecon` is `long` for this reason).

**`request` in a client request line is JSON inside a JSON string, sometimes encoded twice.** Use
`Json.Unwrap`, which re-parses until the value is no longer a string.

**Host filtering reads `AllowedHosts` from configuration.** `AppHost` sets it to the loopback names unless
`AllowLan` is on; a request with any other `Host` gets 400. Tests must address the app as `127.0.0.1`
(port 0 binds there, because Kestrel cannot pick a free port for `localhost`).

**`nuget.config` must keep `<clear />`.** The owner's global config has a private Azure Artifacts feed that
answers 401 and breaks restores.

## Testing

```bash
dotnet build MtgaDash.sln   # compiles both projects
dotnet test MtgaDash.sln    # parser, ingestion, matching, CSV, card names, HTTP API, page JS (via node)
```

`ApiTests` start the real app on a free port against a temp folder. `RealLogTests` read `testdata/Player.log`, `testdata/Player-prev.log` and the accounts CSV when present and
are Inconclusive otherwise. Check UI changes in a real browser at desktop width and at 390 px wide in every
theme: no sideways page scroll, tables scroll inside themselves.

## Style

* American English in code and docs; UI text is plain, sentence case, no exclamation marks.
* Comments explain why; XML doc comments on public types say what they guarantee.
* No new dependencies without a reason: three NuGet packages (Dapper, Microsoft.Data.Sqlite,
  Microsoft.Extensions.Hosting.WindowsServices) and MSTest; nothing on the page.
