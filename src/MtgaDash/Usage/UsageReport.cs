using System.Globalization;
using System.Text;
using Dapper;
using MtgaDash.Cards;
using MtgaDash.Data;
using MtgaDash.Ingestion;
using MtgaDash.Web;

namespace MtgaDash.Usage;

/// <summary>What the page knows and the server does not: every command it offers, and every view.</summary>
public sealed record UsageReportRequest(string[]? Commands, string[]? Views);

/// <summary>
/// A Markdown report of how the dashboard is used, written to be pasted into an LLM that helps design features:
/// what the app is, its settings, the shape of the data, what the owner does on the page, what goes wrong and
/// what is slow. It never contains an email, a display name, a note, a deck or file path: counts and
/// categories only, and every free-text value goes through <see cref="Redact"/>.
/// </summary>
public sealed class UsageReport(
    Database db, MtgaDashOptions options, Queries queries, StatusTracker status, ActivityLog activity,
    CardImageStore images, RequestTimings timings, Func<IReadOnlyList<string>> changelog, Func<string> exportFolder, ILogger logger)
{
    /// <summary>Gaps longer than this end a view's time: the page was left open, not used.</summary>
    internal static readonly TimeSpan IdleCap = TimeSpan.FromMinutes(10);

    /// <summary>An <c>llm</c> folder inside the static export's folder.</summary>
    public string Folder => Path.Combine(exportFolder(), "llm");

    public (string Path, string Markdown) Write(UsageReportRequest? request, DateTime? nowUtc = null)
    {
        var markdown = Build(request, nowUtc);
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"mtgadash_usage_{DateTime.Now:yyyyMMdd_HHmm}.md");
        File.WriteAllText(path, markdown);
        logger.LogInformation("Usage report written to {Path}", path);
        Console.WriteLine($"Usage report: {path}");
        Console.WriteLine("Upload to OneDrive: https://inscriptrx-my.sharepoint.com/");
        return (path, markdown);
    }

    public string Build(UsageReportRequest? request, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var days = Math.Max(1, options.UsageRetentionDays);
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');
        string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

        Line("# MtgaDash usage report");
        Line();
        var offset = TimeZoneInfo.Local.GetUtcOffset(now);
        Line($"Generated {Time.Format(now)} (the PC is at UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}; hours of day below are local). " +
             $"Version {BuildInfo.Version}. Usage covers the last {days} days.");
        Line();
        Line("## How to read this");
        Line();
        Line("You are helping the owner of MtgaDash decide what to build or change next. MtgaDash is a local Windows " +
             "dashboard for about 700 Magic: The Gathering Arena (MTGA) accounts that one person plays in turn. It reads " +
             "MTGA's Player.log files into SQLite and serves one page with tabs: Accounts, Stale (accounts not played " +
             "lately, with a login queue), Totals (leaderboards), Review (matching display names to emails), Status, " +
             "Activity (what ingestion read) and Cards (rares and mythics each account holds at 3+ copies, from saved " +
             "decks, with images). It is .NET 10, Dapper, SQLite and one vanilla-JavaScript page; it runs only on the owner's PC.");
        Line();
        Line("Look for: features that are never used, steps repeated by hand that could be one action, views visited " +
             "only to reach another, errors, slow endpoints, and data the app collects but does not show. Propose " +
             "specific changes and say which numbers below support each. Ask for what is missing.");
        Line();
        Line("Privacy: no emails, display names, notes, deck names or file paths are in this report; typed text is never recorded.");
        Line();

        using var c = db.Open();
        var since = Time.Format(now.AddDays(-days));

        // ---- Data
        Line("## The data");
        Line();
        var counts = queries.Counts();
        var match = c.Query<(string State, long Count)>("SELECT MatchState, COUNT(*) FROM Accounts GROUP BY MatchState ORDER BY 2 DESC").ToList();
        Line($"- Accounts in the CSV: {N(counts.Accounts)} ({string.Join(", ", match.Select(m => $"{m.State} {N(m.Count)}"))})");
        Line($"- Display names seen in logs: {N(counts.Players)}; inventory snapshots: {N(counts.Snapshots)}; rank records: {N(counts.Ranks)}");
        var hidden = c.ExecuteScalar<long>("SELECT COUNT(*) FROM IgnoredDeckNames");
        var custom = c.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM Decks d WHERE d.IsPrecon = 0 AND NOT EXISTS (SELECT 1 FROM IgnoredDeckNames i WHERE i.Name = d.Name)
            """);
        var withLists = c.ExecuteScalar<long>("SELECT COUNT(DISTINCT DisplayName) FROM DeckLists");
        Line($"- Decks: {N(counts.Decks)} ({N(custom)} custom, after {N(hidden)} hidden starter deck names); saved deck lists: {N(counts.DeckLists)} on {N(withLists)} accounts");
        var logins = c.QuerySingle<(long Day, long Week, long Month, long Ever)>("""
            SELECT
              (SELECT COUNT(*) FROM Players WHERE LastLoginUtc >= @d1),
              (SELECT COUNT(*) FROM Players WHERE LastLoginUtc >= @d7),
              (SELECT COUNT(*) FROM Players WHERE LastLoginUtc >= @d30),
              (SELECT COUNT(*) FROM Players WHERE LastLoginUtc IS NOT NULL)
            """, new { d1 = Time.Format(now.AddDays(-1)), d7 = Time.Format(now.AddDays(-7)), d30 = Time.Format(now.AddDays(-30)) });
        Line($"- Accounts whose last login was within 1 day: {N(logins.Day)}, 7 days: {N(logins.Week)}, 30 days: {N(logins.Month)}; ever logged: {N(logins.Ever)}; stale threshold {options.StaleDays} days");
        var statuses = c.Query<(string? Status, long Count)>("SELECT Status, COUNT(*) FROM Accounts GROUP BY Status ORDER BY 2 DESC LIMIT 12").ToList();
        if (statuses.Count > 0)
            Line($"- Status values in the CSV: {string.Join(", ", statuses.Select(s => $"{(string.IsNullOrWhiteSpace(s.Status) ? "(blank)" : Redact.Text(s.Status, 30))} {N(s.Count)}"))}");
        var cards = queries.Cards(3, ["mythic", "rare"]);
        var wanted = queries.ImageWanted().ToList();
        var img = images.Status(wanted);
        Line($"- Rares and mythics held at 3+ copies somewhere: {N(cards.Cards.Count)} cards; images {N(img.Have)} of {N(img.Wanted)}, {N(img.NotFound)} with none on Scryfall");
        if (img.LastRun is { } run)
            Line($"- Last image download: {run.Trigger}, finished {run.FinishedUtc}, {run.Saved.Count} saved, {run.NotFound.Count} not found, {run.Failed.Count} failed{(run.Completed ? "" : ", stopped early")}");
        Line();

        // ---- Settings
        Line("## Settings");
        Line();
        Line("Paths are shown only as set or not, and whether they exist.");
        Line();
        string P(string path, bool folder = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return "not set";
            var full = MtgaDashOptions.Resolve(path);
            return (folder ? Directory.Exists(full) : File.Exists(full) || path.Contains('*')) ? "set, exists" : "set, not found";
        }
        Line($"- PollSeconds {options.PollSeconds}; WatchLiveLog {options.WatchLiveLog}; WatchPrevLog {options.WatchPrevLog}; LogFolder {P(options.LogFolder, true)}; AccountsCsvPath {P(options.AccountsCsvPath)}");
        Line($"- CardDbPath {P(options.CardDbPath)}; AutoDownloadCardImages {options.AutoDownloadCardImages} (after {options.AutoDownloadDelaySeconds} s)");
        Line($"- LaunchGame {options.LaunchGame}; OpenBrowser {options.OpenBrowser}; StartPage {Redact.Text(options.StartPage, 40)}; AllowLan {options.AllowLan}; ExportFolder {(string.IsNullOrWhiteSpace(options.ExportFolder) ? "not set" : "set")}");
        Line($"- Alerts: gems drop {options.AlertGemsDrop}, gold drop {options.AlertGoldDrop}, wildcard drop {options.AlertWildcardDrop}, over {options.AlertDays} days");
        Line($"- TrackUsage {options.TrackUsage}; UsageRetentionDays {options.UsageRetentionDays}");
        Line();

        // ---- Usage
        Line("## What the owner does on the page");
        Line();
        var events = c.Query<UsageRow>("SELECT AtUtc, Session, Kind, Name, Value FROM UsageEvents WHERE AtUtc >= @since ORDER BY Session, AtUtc, Id", new { since }).ToList();
        if (!options.TrackUsage) Line("Usage tracking is off (TrackUsage), so this section is empty.");
        if (events.Count == 0)
        {
            Line("No usage recorded yet. Use the dashboard for a few days, then export again.");
            Line();
        }
        else
        {
            var sessions = events.GroupBy(e => e.Session).ToList();
            var activeDays = events.Select(e => LocalDay(e.AtUtc)).Distinct().Count();
            var lengths = sessions.Select(s => Active(s.Select(e => Parse(e.AtUtc)).ToList())).OrderBy(t => t).ToList();
            Line($"- {N(sessions.Count)} page sessions on {N(activeDays)} days, {N(events.Count)} events. Active time per session (gaps over {IdleCap.TotalMinutes:0} min dropped): median {Minutes(lengths[lengths.Count / 2])}, longest {Minutes(lengths[^1])}.");
            var first = events.Min(e => e.AtUtc);
            Line($"- First event {first}, last {events.Max(e => e.AtUtc)}.");
            Line();

            Line("### Views (tabs)");
            Line();
            Line("| View | Opened | Active minutes |");
            Line("|---|---:|---:|");
            var viewTime = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var s in sessions)
            {
                string? current = null;
                DateTime at = default;
                foreach (var e in s)
                {
                    var t = Parse(e.AtUtc);
                    if (current is not null) viewTime[current] = viewTime.GetValueOrDefault(current) + Math.Min((t - at).TotalMinutes, IdleCap.TotalMinutes);
                    if (e.Kind == "view") current = e.Name;
                    at = t;
                }
            }
            var opened = events.Where(e => e.Kind == "view").GroupBy(e => e.Name).ToDictionary(g => g.Key, g => (long)g.Count());
            foreach (var v in opened.Keys.Union(request?.Views ?? []).Distinct().OrderByDescending(v => opened.GetValueOrDefault(v)))
                Line($"| {Redact.Text(v, 30)} | {N(opened.GetValueOrDefault(v))} | {viewTime.GetValueOrDefault(v):0.#} |");
            Line();

            Line("### Actions");
            Line();
            Line("Commands are from the command palette (Ctrl+K); clicks are buttons, with digits shown as #; changes are menus, checkboxes and options.");
            Line();
            Line("| Kind | Action | Detail | Count |");
            Line("|---|---|---|---:|");
            foreach (var g in events.Where(e => e.Kind is "command" or "click" or "change" or "search" or "hover")
                         .GroupBy(e => (e.Kind, e.Name, e.Value)).OrderByDescending(g => g.Count()).Take(60))
                Line($"| {g.Key.Kind} | {g.Key.Name} | {g.Key.Value ?? ""} | {N(g.Count())} |");
            Line();

            if (request?.Commands is { Length: > 0 } offered)
            {
                var used = events.Where(e => e.Kind == "command").Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
                // A command id that needed redacting names an account; it says nothing about which features are used.
                offered = offered.Where(x => !string.IsNullOrWhiteSpace(x) && Redact.Text(x, 60) == x).Distinct().ToArray();
                var never = offered.Where(x => !used.Contains(x)).Order(StringComparer.Ordinal).ToList();
                Line($"### Palette commands never run ({never.Count} of {offered.Length})");
                Line();
                Line(never.Count == 0 ? "Every command has been run." : string.Join(", ", never));
                Line();
            }

            var device = events.Where(e => e.Kind is "display" or "load").GroupBy(e => (e.Kind, e.Name)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Name).ToList();
            if (device.Count > 0)
            {
                Line("### Display and device (per page load)");
                Line();
                foreach (var g in device)
                    Line($"- {g.Key.Kind} {g.Key.Name}: {string.Join(", ", g.GroupBy(e => e.Value ?? "").OrderByDescending(x => x.Count()).Select(x => $"{x.Key} ×{x.Count()}"))}");
                Line();
            }

            Line("### When (local time, events per hour of day)");
            Line();
            var byHour = events.GroupBy(e => Parse(e.AtUtc).ToLocalTime().Hour).ToDictionary(g => g.Key, g => g.Count());
            Line(string.Join(" · ", Enumerable.Range(0, 24).Where(byHour.ContainsKey).Select(h => $"{h:00}h {byHour[h]}")));
            Line();

            var errors = events.Where(e => e.Kind == "error").GroupBy(e => (e.Name, e.Value)).OrderByDescending(g => g.Count()).Take(20).ToList();
            Line("### Page errors");
            Line();
            if (errors.Count == 0) Line("None recorded.");
            foreach (var g in errors) Line($"- {g.Key.Name}{(g.Key.Value is null ? "" : " — " + g.Key.Value)} (×{g.Count()}, last {g.Max(e => e.AtUtc)})");
            Line();
        }

        // ---- Ingestion
        Line("## Ingestion health");
        Line();
        var st = status.Snapshot();
        Line($"- Last poll {st.LastPollUtc ?? "never"}, took {st.LastPollMs} ms, every {st.PollSeconds} s; parse warnings in 24 h: {st.Warnings24h}");
        Line($"- Log files: {st.Files.Count}, {N(st.Files.Sum(f => f.Length) / 1024)} KB in all, {st.Files.Count(f => f.Processed < f.Length)} not fully read, {st.Files.Count(f => f.Error is not null)} unreadable");
        foreach (var issue in st.Issues) Line($"- Issue: {Redact.Text(issue)}");
        var kinds = c.Query<(string Kind, long Count)>("SELECT Kind, COUNT(*) FROM ActivityEntries GROUP BY Kind ORDER BY 2 DESC").ToList();
        if (kinds.Count > 0) Line($"- Activity feed, last 24 h: {string.Join(", ", kinds.Select(k => $"{k.Kind} {N(k.Count)}"))}");
        Line();
        var messages = activity.Messages();
        if (messages.Count > 0)
        {
            Line("### MTGA messages since the app started");
            Line();
            Line("\"Read\" means the parser uses it; an unread message with a large count may hold data worth showing.");
            Line();
            Line("| Direction | Message | Count | Read | Largest bytes |");
            Line("|---|---|---:|---|---:|");
            foreach (var m in messages.OrderByDescending(m => m.Count).Take(40))
                Line($"| {m.Direction} | {Redact.Text(m.Method, 60)} | {N(m.Count)} | {(m.Handled > 0 ? "yes" : "no")} | {N(m.LargestBytes)} |");
            Line();
        }

        // ---- Performance
        var rows = timings.Rows();
        Line("## Server timings since the app started");
        Line();
        if (rows.Count == 0) Line("No API requests yet.");
        else
        {
            Line("| Endpoint | Requests | Mean ms | Max ms | Over 500 ms |");
            Line("|---|---:|---:|---:|---:|");
            foreach (var r in rows) Line($"| {r.Endpoint} | {N(r.Count)} | {r.MeanMs} | {r.MaxMs} | {N(r.Over500Ms)} |");
        }
        Line();

        // ---- Features
        Line("## Recent changes (changelog, newest last)");
        Line();
        foreach (var entry in changelog().TakeLast(25)) Line($"- {Redact.Text(entry, 240)}");
        return sb.ToString();
    }

    private sealed class UsageRow
    {
        public string AtUtc { get; set; } = "";
        public string Session { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Value { get; set; }
    }

    private static DateTime Parse(string utc) =>
        DateTime.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static DateOnly LocalDay(string utc) => DateOnly.FromDateTime(Parse(utc).ToLocalTime());

    internal static TimeSpan Active(IReadOnlyList<DateTime> times)
    {
        var total = TimeSpan.Zero;
        for (var i = 1; i < times.Count; i++)
        {
            var gap = times[i] - times[i - 1];
            if (gap <= IdleCap) total += gap;
        }
        return total;
    }

    private static string Minutes(TimeSpan t) => t.TotalMinutes < 1 ? "under 1 min" : $"{t.TotalMinutes:0} min";
}
