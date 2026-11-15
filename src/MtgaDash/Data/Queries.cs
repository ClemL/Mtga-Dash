using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MtgaDash.Accounts;

namespace MtgaDash.Data;

public sealed record AccountLatest(
    string Email, string? Alias, string? DisplayName, string? Status, string? Notes, string? MiscNotes, string? NoteSource,
    string MatchState, string? FirstSeenUtc, string? LastLoginUtc, long? SnapshotId, string? CapturedAtUtc,
    long? Gems, long? Gold, long? WcCommon, long? WcUncommon, long? WcRare, long? WcMythic,
    double? VaultProgress, long? WcTrackPosition, long BoosterTotal,
    long? Season, string? ConstructedClass, long? ConstructedLevel, string? LimitedClass, long? LimitedLevel,
    long CustomDeckCount);

public sealed record SnapshotPoint(string CapturedAtUtc, long? Gems, long? Gold, long? WcCommon, long? WcUncommon, long? WcRare, long? WcMythic, double? VaultProgress);

public sealed record BoosterRow(string SetCode, long CollationId, long Count);

public sealed record DeckRow(string DeckId, string Name, string? Format, long IsPrecon, string? LastPlayed, string? LastUpdated, string? CardsJson);

public sealed record RankRow(string CapturedAtUtc, long? Season, string? ConstructedClass, long? ConstructedLevel, string? LimitedClass, long? LimitedLevel);

public sealed record AccountDetail(
    AccountLatest Account,
    IReadOnlyList<SnapshotPoint> Snapshots,
    IReadOnlyList<BoosterRow> Boosters,
    IReadOnlyList<DeckRow> CustomDecks,
    int PreconCount,
    IReadOnlyList<RankRow> Ranks,
    IReadOnlyDictionary<int, string> CardNames);

public sealed record SetTotal(string SetCode, long Count, long Accounts);

public sealed record Totals(
    long Gems, long Gold, long WcCommon, long WcUncommon, long WcRare, long WcMythic,
    IReadOnlyList<SetTotal> BoostersBySet, IReadOnlyDictionary<string, int> LoginBuckets,
    int Accounts, int Players);

public sealed record UnmatchedPlayer(string DisplayName, string? LastLoginUtc, string ExceptionLine);

public sealed record AmbiguousPlayer(string DisplayName, string? LastLoginUtc, IReadOnlyList<string> Candidates, IReadOnlyList<string> ExceptionLines);

public sealed record StatusMismatch(string Email, string? DisplayName, string? Status, long CustomDeckCount, long? WcRare, long? WcMythic);

public sealed record MappingRow(string DisplayName, string Email, string Source, bool PlayerSeen, bool AccountExists);

public sealed record Review(IReadOnlyList<UnmatchedPlayer> Unmatched, IReadOnlyList<AmbiguousPlayer> Ambiguous, IReadOnlyList<StatusMismatch> StatusMismatches, IReadOnlyList<MappingRow> Mappings);

/// <summary>Read side of the API. Every query is plain SQL against the schema in <see cref="Database"/>.</summary>
public sealed partial class Queries(Database db, Cards.CardNames? cards = null, ExceptionStore? exceptions = null)
{
    public IReadOnlyList<AccountLatest> Accounts(string? search = null, string? status = null, bool? stale = null, int staleDays = 30, DateTime? nowUtc = null)
    {
        using var c = db.Open();
        var rows = c.Query<AccountLatest>("SELECT * FROM vAccountLatest ORDER BY Email").ToList();
        IEnumerable<AccountLatest> q = rows;
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(r => Matches(r, search));
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase));
        if (stale is { } s)
        {
            var cutoff = Time.Format((nowUtc ?? DateTime.UtcNow).AddDays(-staleDays));
            q = q.Where(r => (r.LastLoginUtc is null || string.CompareOrdinal(r.LastLoginUtc, cutoff) < 0) == s);
        }
        return q.ToList();
    }

    /// <summary>
    /// The one matcher for accounts, shared by the table filter and the command palette (which runs the
    /// same rule in the page): every whitespace-separated term must occur in email, display name, status or notes.
    /// </summary>
    public static bool Matches(AccountLatest r, string query)
    {
        var hay = $"{r.Email} {r.DisplayName} {r.Status} {r.Notes} {r.MiscNotes}".ToLowerInvariant();
        return query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(hay.Contains);
    }

    public AccountDetail? Account(string email)
    {
        using var c = db.Open();
        var a = c.QuerySingleOrDefault<AccountLatest>("SELECT * FROM vAccountLatest WHERE Email = @email", new { email });
        if (a is null) return null;
        var name = a.DisplayName;
        var snaps = c.Query<SnapshotPoint>(
            "SELECT CapturedAtUtc, Gems, Gold, WcCommon, WcUncommon, WcRare, WcMythic, VaultProgress FROM Snapshots WHERE DisplayName = @name ORDER BY CapturedAtUtc",
            new { name }).ToList();
        var boosters = a.SnapshotId is { } sid
            ? c.Query<BoosterRow>("SELECT SetCode, CollationId, Count FROM Boosters WHERE SnapshotId = @sid ORDER BY SetCode", new { sid }).ToList()
            : [];
        var decks = c.Query<DeckRow>(
            "SELECT DeckId, Name, Format, IsPrecon, LastPlayed, LastUpdated, CardsJson FROM Decks WHERE DisplayName = @name ORDER BY IsPrecon, COALESCE(LastPlayed, '') DESC, Name",
            new { name }).ToList();
        var ranks = c.Query<RankRow>(
            "SELECT CapturedAtUtc, Season, ConstructedClass, ConstructedLevel, LimitedClass, LimitedLevel FROM Ranks WHERE DisplayName = @name ORDER BY CapturedAtUtc DESC",
            new { name }).ToList();
        var custom = decks.Where(d => d.IsPrecon == 0).ToList();
        var names = cards?.Lookup(custom.SelectMany(d => CardIds(d.CardsJson))) ?? new Dictionary<int, string>();
        return new AccountDetail(a, snaps, boosters, custom, decks.Count - custom.Count, ranks, names);
    }

    private static IEnumerable<int> CardIds(string? cardsJson)
    {
        if (cardsJson is null) yield break;
        using var doc = JsonDocument.Parse(cardsJson);
        foreach (var zone in doc.RootElement.EnumerateObject())
            foreach (var card in zone.Value.EnumerateArray())
                if (card.TryGetProperty("CardId", out var id) && id.TryGetInt32(out var i)) yield return i;
    }

    public Totals Totals(DateTime? nowUtc = null)
    {
        using var c = db.Open();
        // Inventory totals cover every display name seen, matched to an account or not: it is all real inventory.
        var latest = """
            SELECT s.* FROM Snapshots s
            WHERE s.Id = (SELECT s2.Id FROM Snapshots s2 WHERE s2.DisplayName = s.DisplayName ORDER BY s2.CapturedAtUtc DESC LIMIT 1)
            """;
        var sums = c.QuerySingle<(long, long, long, long, long, long)>($"""
            SELECT COALESCE(SUM(Gems),0), COALESCE(SUM(Gold),0), COALESCE(SUM(WcCommon),0), COALESCE(SUM(WcUncommon),0),
                   COALESCE(SUM(WcRare),0), COALESCE(SUM(WcMythic),0) FROM ({latest})
            """);
        var sets = c.Query<SetTotal>($"""
            SELECT b.SetCode, SUM(b.Count) AS Count, COUNT(DISTINCT l.DisplayName) AS Accounts
            FROM Boosters b JOIN ({latest}) l ON l.Id = b.SnapshotId
            GROUP BY b.SetCode ORDER BY Count DESC, b.SetCode
            """).ToList();

        var now = nowUtc ?? DateTime.UtcNow;
        var buckets = new Dictionary<string, int> { ["≤7d"] = 0, ["≤30d"] = 0, ["≤90d"] = 0, ["older"] = 0, ["never"] = 0 };
        var logins = c.Query<string?>("SELECT p.LastLoginUtc FROM Accounts a LEFT JOIN Players p ON p.DisplayName = a.DisplayName").ToList();
        foreach (var l in logins) buckets[Bucket(l, now)]++;

        var players = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Players");
        return new Totals(sums.Item1, sums.Item2, sums.Item3, sums.Item4, sums.Item5, sums.Item6, sets, buckets, logins.Count, players);
    }

    public static string Bucket(string? lastLoginUtc, DateTime nowUtc)
    {
        if (lastLoginUtc is null || !DateTime.TryParse(lastLoginUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)) return "never";
        var days = (nowUtc - t).TotalDays;
        return days <= 7 ? "≤7d" : days <= 30 ? "≤30d" : days <= 90 ? "≤90d" : "older";
    }

    [GeneratedRegex(@"no\s*deck|unused", RegexOptions.IgnoreCase)]
    private static partial Regex UnusedStatus();

    public Review Review()
    {
        using var c = db.Open();
        var emails = c.Query<string>("SELECT Email FROM Accounts").ToList();
        var players = c.Query<(string DisplayName, string? LastLoginUtc)>("SELECT DisplayName, LastLoginUtc FROM Players ORDER BY LastLoginUtc DESC").ToList();
        var lastLogin = players.ToDictionary(p => p.DisplayName, p => p.LastLoginUtc);
        var (map, excluded) = exceptions?.Current() ?? (AccountExceptions.DisplayNameToEmail, AccountExceptions.ExcludedEmails);
        var match = AccountMatcher.Match(emails, players.Select(p => p.DisplayName), map, excluded);

        var unmatched = match.Players.Where(p => p.State == MatchStates.Unmatched)
            .Select(p => new UnmatchedPlayer(p.DisplayName, lastLogin.GetValueOrDefault(p.DisplayName),
                AccountMatcher.ExceptionLine(p.DisplayName, "") + " // fill in the email"))
            .ToList();
        var ambiguous = match.Players.Where(p => p.State == MatchStates.Ambiguous)
            .Select(p => new AmbiguousPlayer(p.DisplayName, lastLogin.GetValueOrDefault(p.DisplayName), p.Candidates,
                p.Candidates.Select(e => AccountMatcher.ExceptionLine(p.DisplayName, e)).ToList()))
            .ToList();
        var mismatches = c.Query<AccountLatest>("SELECT * FROM vAccountLatest")
            .Where(a => a.Status is not null && UnusedStatus().IsMatch(a.Status)
                && (a.CustomDeckCount > 0 || (a.WcRare ?? 0) > 0 || (a.WcMythic ?? 0) > 0))
            .Select(a => new StatusMismatch(a.Email, a.DisplayName, a.Status, a.CustomDeckCount, a.WcRare, a.WcMythic))
            .ToList();
        var lookup = match.Players.ToDictionary(p => p.DisplayName, StringComparer.OrdinalIgnoreCase);
        var mappings = (exceptions?.Mappings() ?? AccountExceptions.DisplayNameToEmail.Select(kv => new ExceptionMapping(kv.Key, kv.Value, "code")).ToList())
            .Select(m => new MappingRow(m.DisplayName, m.Email, m.Source, lookup.ContainsKey(m.DisplayName),
                emails.Contains(m.Email, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        return new Review(unmatched, ambiguous, mismatches, mappings);
    }

    public IReadOnlyList<string> Statuses()
    {
        using var c = db.Open();
        return c.Query<string>("SELECT DISTINCT Status FROM Accounts WHERE Status IS NOT NULL AND Status <> '' ORDER BY Status").ToList();
    }

    public (int Accounts, int Players, int Snapshots, int Decks, int CustomDecks, int DeckLists, int Ranks) Counts()
    {
        using var c = db.Open();
        return c.QuerySingle<(int, int, int, int, int, int, int)>("""
            SELECT (SELECT COUNT(*) FROM Accounts), (SELECT COUNT(*) FROM Players), (SELECT COUNT(*) FROM Snapshots),
                   (SELECT COUNT(*) FROM Decks), (SELECT COUNT(*) FROM Decks WHERE IsPrecon = 0),
                   (SELECT COUNT(*) FROM DeckLists), (SELECT COUNT(*) FROM Ranks)
            """);
    }
}
