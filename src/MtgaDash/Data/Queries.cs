using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MtgaDash.Accounts;
using MtgaDash.Cards;

namespace MtgaDash.Data;

/// <summary>
/// One row of vAccountLatest. Settable properties, not a positional record: with no rows, SQLite reports computed
/// columns (counts, totals) without a type and Dapper's constructor matching fails; property mapping converts.
/// </summary>
public sealed record AccountLatest
{
    public string Email { get; set; } = "";
    public string? Alias { get; set; }
    public string? DisplayName { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
    public string? MiscNotes { get; set; }
    public string? NoteSource { get; set; }
    public string MatchState { get; set; } = "";
    public string? FirstSeenUtc { get; set; }
    public string? LastLoginUtc { get; set; }
    public long? SnapshotId { get; set; }
    public string? CapturedAtUtc { get; set; }
    public long? Gems { get; set; }
    public long? Gold { get; set; }
    public long? WcCommon { get; set; }
    public long? WcUncommon { get; set; }
    public long? WcRare { get; set; }
    public long? WcMythic { get; set; }
    public double? VaultProgress { get; set; }
    public long? WcTrackPosition { get; set; }
    public long BoosterTotal { get; set; }
    public long? Season { get; set; }
    public string? ConstructedClass { get; set; }
    public long? ConstructedLevel { get; set; }
    public string? LimitedClass { get; set; }
    public long? LimitedLevel { get; set; }
    public long CustomDeckCount { get; set; }
}

public sealed record SnapshotPoint(string CapturedAtUtc, long? Gems, long? Gold, long? WcCommon, long? WcUncommon, long? WcRare, long? WcMythic, double? VaultProgress);

public sealed record BoosterRow(string SetCode, long CollationId, long Count);

/// <summary>One deck. Property-mapped: IsPrecon is a CASE expression, which has no type when an account has no decks.</summary>
public sealed record DeckRow
{
    public string DeckId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Format { get; set; }
    public long IsPrecon { get; set; }
    public string? LastPlayed { get; set; }
    public string? LastUpdated { get; set; }
    public string? CardsJson { get; set; }
}

public sealed record RankRow(string CapturedAtUtc, long? Season, string? ConstructedClass, long? ConstructedLevel, string? LimitedClass, long? LimitedLevel);

public sealed record AccountDetail(
    AccountLatest Account,
    IReadOnlyList<SnapshotPoint> Snapshots,
    IReadOnlyList<BoosterRow> Boosters,
    IReadOnlyList<DeckRow> CustomDecks,
    int PreconCount,
    IReadOnlyList<RankRow> Ranks,
    IReadOnlyDictionary<int, string> CardNames,
    IReadOnlyList<OwnedCard> KnownCards,
    bool CardRarityKnown);

/// <summary>A card known to be owned (from saved decks), with what the card database says about it.</summary>
public sealed record OwnedCard(int CardId, int Copies, int Decks, IReadOnlyList<string> DeckNames, string? Name, string? Rarity, string? Set,
    string? Types = null, string? Cost = null, string? Colors = null, bool Alchemy = false, string? Image = null);

public sealed record CardHolder(string? Email, string DisplayName, int Copies);

public sealed record CardAcrossAccounts(int CardId, string? Name, string? Rarity, string? Set, IReadOnlyList<CardHolder> Holders,
    string? Types = null, string? Cost = null, string? Colors = null, bool Alchemy = false);

public sealed record CardsReport(
    bool RarityKnown, string? CardDbStatus, int MinCopies, IReadOnlyList<string> Rarities,
    IReadOnlyList<CardAcrossAccounts> Cards, IReadOnlyDictionary<string, int> PerAccount, int AccountsWithDecks,
    IReadOnlyList<UnrecordedDecks> Unrecorded);

/// <summary>Custom decks whose card list has never been logged: open and save each to count its cards.</summary>
public sealed record UnrecordedDecks(string? Email, string DisplayName, int Count, int Total, IReadOnlyList<string> DeckNames);

/// <summary>Boosters of one set across accounts. Property-mapped: SUM and COUNT columns have no type when empty.</summary>
public sealed record SetTotal
{
    public string SetCode { get; set; } = "";
    public long Count { get; set; }
    public long Accounts { get; set; }
}

public sealed record Totals(
    long Gems, long Gold, long WcCommon, long WcUncommon, long WcRare, long WcMythic,
    IReadOnlyList<SetTotal> BoostersBySet, IReadOnlyDictionary<string, int> LoginBuckets,
    int Accounts, int Players, IReadOnlyList<Leader> MostBoosters, IReadOnlyList<Leader> MostRareWildcards);

/// <summary>An account on a Totals leaderboard, with the numbers both boards show.</summary>
public sealed record Leader(string Email, string? DisplayName, long Boosters, long WcRare, long WcMythic, string? LastLoginUtc);

public sealed record UnmatchedPlayer(string DisplayName, string? LastLoginUtc, string ExceptionLine);

public sealed record AmbiguousPlayer(string DisplayName, string? LastLoginUtc, IReadOnlyList<string> Candidates, IReadOnlyList<string> ExceptionLines);

public sealed record StatusMismatch(string Email, string? DisplayName, string? Status, long CustomDeckCount, long? WcRare, long? WcMythic);

/// <summary>
/// A display name with its latest inventory. Property-mapped for the same reason as <see cref="AccountLatest"/>.
/// </summary>
public sealed record PlayerRow
{
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string? FirstSeenUtc { get; set; }
    public string? LastLoginUtc { get; set; }
    public string? LastInventoryUtc { get; set; }
    public long? Gems { get; set; }
    public long? Gold { get; set; }
    public long? WcRare { get; set; }
    public long? WcMythic { get; set; }
    public long Snapshots { get; set; }
}

public sealed record MappingRow(string DisplayName, string Email, string Source, bool PlayerSeen, bool AccountExists);

public sealed record Review(IReadOnlyList<UnmatchedPlayer> Unmatched, IReadOnlyList<AmbiguousPlayer> Ambiguous, IReadOnlyList<StatusMismatch> StatusMismatches, IReadOnlyList<MappingRow> Mappings, IReadOnlyList<InventoryDrop> Drops);

/// <summary>A fall between two consecutive snapshots of one account, large enough to look at.</summary>
public sealed record InventoryDrop(
    string? Email, string DisplayName, string FromUtc, string ToUtc,
    long GemsChange, long GoldChange, long RareChange, long MythicChange, IReadOnlyList<string> Reasons);

/// <summary>Read side of the API. Every query is plain SQL against the schema in <see cref="Database"/>.</summary>
public sealed partial class Queries(Database db, Cards.CardNames? cards = null, ExceptionStore? exceptions = null, MtgaDashOptions? options = null,
    CardImageStore? images = null)
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
            $"SELECT DeckId, Name, Format, {PreconSql} AS IsPrecon, LastPlayed, LastUpdated, CardsJson FROM Decks d WHERE DisplayName = @name ORDER BY 4, COALESCE(LastPlayed, '') DESC, Name",
            new { name }).ToList();
        var ranks = c.Query<RankRow>(
            "SELECT CapturedAtUtc, Season, ConstructedClass, ConstructedLevel, LimitedClass, LimitedLevel FROM Ranks WHERE DisplayName = @name ORDER BY CapturedAtUtc DESC",
            new { name }).ToList();
        var custom = decks.Where(d => d.IsPrecon == 0).ToList();
        var names = cards?.Lookup(custom.SelectMany(d => CardIds(d.CardsJson))) ?? new Dictionary<int, string>();
        var saved = name is null ? [] : SavedDecks(c, name).ToList();
        var (canonical, info) = Canonical(saved);
        var known = KnownCollection.For(saved, canonical);
        var owned = known.Select(k =>
        {
            var o = ToOwned(k, info);
            // Only a card with a downloaded image gets a URL; the page draws a placeholder for the rest.
            return o.Name is { } n && images?.Has(n) == true
                ? o with { Image = "/card-images/" + Uri.EscapeDataString(CardImageStore.KeyFor(n)) + ".jpg" }
                : o;
        }).ToList();
        return new AccountDetail(a, snaps, boosters, custom, decks.Count - custom.Count, ranks, names, owned, cards?.HasRarity ?? false);
    }

    private static OwnedCard ToOwned(KnownCard k, IReadOnlyDictionary<int, CardInfo> info)
    {
        var i = info.GetValueOrDefault(k.CardId);
        return new OwnedCard(k.CardId, k.Copies, k.Decks, k.DeckNames, i?.Name, i?.Rarity, i?.Set, i?.Types, i?.Cost, i?.Colors, i?.Alchemy ?? false);
    }

    /// <summary>Every saved version of every deck, with the deck's current name for display.</summary>
    private static IEnumerable<SavedDeck> SavedDecks(Microsoft.Data.Sqlite.SqliteConnection c, string? displayName = null) =>
        c.Query<SavedDeck>($"""
            SELECT l.DisplayName, l.DeckId, d.Name AS DeckName, l.CardsJson
            FROM DeckLists l LEFT JOIN Decks d ON d.DisplayName = l.DisplayName AND d.DeckId = l.DeckId
            {(displayName is null ? "" : "WHERE l.DisplayName = @displayName")}
            """, new { displayName });

    public static readonly string[] DefaultRarities = ["mythic", "rare"];

    /// <summary>A deck counts as a precon when MTGA says so or its name is on the hidden list (starter decks).</summary>
    private const string PreconSql = "CASE WHEN d.IsPrecon = 1 OR EXISTS (SELECT 1 FROM IgnoredDeckNames i WHERE i.Name = d.Name) THEN 1 ELSE 0 END";

    public sealed record DeckNameCount(string Name, long Accounts);

    public sealed record DeckNames(IReadOnlyList<string> Hidden, IReadOnlyList<DeckNameCount> Common);

    /// <summary>
    /// The hidden names, and the custom deck names found on the most accounts: a name on dozens of accounts is
    /// almost always a starter deck, so it is offered for hiding.
    /// </summary>
    public DeckNames DeckNameLists(int top = 25)
    {
        using var c = db.Open();
        var hidden = c.Query<string>("SELECT Name FROM IgnoredDeckNames ORDER BY Name COLLATE NOCASE").ToList();
        var common = c.Query<(string Name, long Accounts)>("""
            SELECT d.Name, COUNT(DISTINCT d.DisplayName) AS Accounts FROM Decks d
            WHERE d.IsPrecon = 0 AND NOT EXISTS (SELECT 1 FROM IgnoredDeckNames i WHERE i.Name = d.Name)
            GROUP BY d.Name HAVING COUNT(DISTINCT d.DisplayName) >= 2
            ORDER BY Accounts DESC, d.Name LIMIT @top
            """, new { top }).Select(x => new DeckNameCount(x.Name, x.Accounts)).ToList();
        return new DeckNames(hidden, common);
    }

    /// <summary>
    /// Cards held at <paramref name="minCopies"/> or more, across accounts, filtered by rarity when the card
    /// database gives one. Without rarities the filter cannot apply, so every card qualifies.
    /// </summary>
    /// <summary>
    /// One id per card name (the lowest), so reprints and alternate printings count as one card. Ids the card
    /// database does not know stay as they are.
    /// </summary>
    private (Func<int, int> Canonical, IReadOnlyDictionary<int, CardInfo> Info) Canonical(IReadOnlyList<SavedDeck> decks)
    {
        var ids = decks.SelectMany(d => KnownCollection.Totals(d.CardsJson).Keys).Distinct().ToList();
        var info = cards?.Info(ids) ?? new Dictionary<int, CardInfo>();
        var byName = info.Where(kv => kv.Value.Name is not null)
            .GroupBy(kv => kv.Value.Name!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(kv => kv.Key), StringComparer.Ordinal);
        int Map(int id) => info.TryGetValue(id, out var i) && i.Name is { } n ? byName[n] : id;
        return (Map, info);
    }

    /// <summary>
    /// The cards worth an image: rares and mythics some account holds at 3 or more copies, by name. Cards the
    /// card database cannot name are left out, since an image is found by name.
    /// </summary>
    public IReadOnlyList<string> ImageWanted() =>
        Cards(minCopies: 3).Cards.Where(c => c.Name is not null && (c.Rarity is "rare" or "mythic"))
            .Select(c => c.Name!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every account holding at least one copy of a card (any printing), most copies first.</summary>
    public IReadOnlyList<CardHolder> CardHolders(int cardId)
    {
        using var c = db.Open();
        var saved = SavedDecks(c).ToList();
        var (canonical, _) = Canonical(saved);
        var target = canonical(cardId);
        var emails = c.Query<(string DisplayName, string? Email)>("SELECT DisplayName, Email FROM Players").ToDictionary(p => p.DisplayName, p => p.Email);
        return KnownCollection.ByAccount(saved, canonical)
            .SelectMany(kv => kv.Value.Where(k => k.CardId == target).Select(k => new CardHolder(emails.GetValueOrDefault(kv.Key), kv.Key, k.Copies)))
            .OrderByDescending(h => h.Copies).ThenBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public CardsReport Cards(int minCopies = 3, IReadOnlyList<string>? rarities = null)
    {
        rarities = rarities is { Count: > 0 } ? rarities.Select(r => r.ToLowerInvariant()).ToList() : DefaultRarities;
        using var c = db.Open();
        var saved = SavedDecks(c).ToList();
        var (canonical, info) = Canonical(saved);
        var byAccount = KnownCollection.ByAccount(saved, canonical);
        var emails = c.Query<(string DisplayName, string? Email)>("SELECT DisplayName, Email FROM Players").ToDictionary(p => p.DisplayName, p => p.Email);
        var rarityKnown = cards?.HasRarity ?? false;

        bool Wanted(KnownCard k) => k.Copies >= minCopies
            && (!rarityKnown || info.GetValueOrDefault(k.CardId)?.Rarity is { } r && rarities.Contains(r));

        var perAccount = new Dictionary<string, int>(StringComparer.Ordinal);
        var holders = new Dictionary<int, List<CardHolder>>();
        foreach (var (name, known) in byAccount)
        {
            var hits = known.Where(Wanted).ToList();
            perAccount[name] = hits.Count;
            foreach (var k in hits)
            {
                if (!holders.TryGetValue(k.CardId, out var list)) holders[k.CardId] = list = [];
                list.Add(new CardHolder(emails.GetValueOrDefault(name), name, k.Copies));
            }
        }
        var report = holders
            .Select(kv =>
            {
                var i = info.GetValueOrDefault(kv.Key);
                return new CardAcrossAccounts(kv.Key, i?.Name, i?.Rarity, i?.Set,
                    kv.Value.OrderByDescending(h => h.Copies).ThenBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
                    i?.Types, i?.Cost, i?.Colors, i?.Alchemy ?? false);
            })
            .OrderByDescending(x => x.Holders.Count).ThenBy(x => x.Name ?? "~").ThenBy(x => x.CardId)
            .ToList();
        return new CardsReport(rarityKnown, cards?.Status, minCopies, rarities, report, perAccount, byAccount.Count, Unrecorded(c, emails));
    }

    /// <summary>
    /// Per account, custom decks known from the login summary with no saved card list. Precons are left out:
    /// they are the same for every account and add commons, not the rares this view is for.
    /// </summary>
    private static List<UnrecordedDecks> Unrecorded(Microsoft.Data.Sqlite.SqliteConnection c, IReadOnlyDictionary<string, string?> emails)
    {
        var rows = c.Query<(string DisplayName, string Name, long Recorded)>("""
            SELECT d.DisplayName, d.Name,
                   EXISTS(SELECT 1 FROM DeckLists l WHERE l.DisplayName = d.DisplayName AND l.DeckId = d.DeckId) AS Recorded
            FROM Decks d WHERE d.IsPrecon = 0 AND NOT EXISTS (SELECT 1 FROM IgnoredDeckNames i WHERE i.Name = d.Name)
            """).ToList();
        return rows.GroupBy(r => r.DisplayName)
            .Select(g =>
            {
                var missing = g.Where(r => r.Recorded == 0).Select(r => r.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
                return new UnrecordedDecks(emails.GetValueOrDefault(g.Key), g.Key, missing.Count, g.Count(), missing.Take(8).ToList());
            })
            .Where(u => u.Count > 0)
            .OrderByDescending(u => u.Count).ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        // From the latest snapshot of each account in the CSV, so every leader can be opened and logged into.
        var accountsLatest = c.Query<AccountLatest>("SELECT * FROM vAccountLatest WHERE SnapshotId IS NOT NULL").ToList();
        static Leader L(AccountLatest a) => new(a.Email, a.DisplayName, a.BoosterTotal, a.WcRare ?? 0, a.WcMythic ?? 0, a.LastLoginUtc);
        var mostBoosters = accountsLatest.Where(a => a.BoosterTotal > 0).OrderByDescending(a => a.BoosterTotal).ThenByDescending(a => a.WcRare).Take(LeaderCount).Select(L).ToList();
        var mostRares = accountsLatest.Where(a => (a.WcRare ?? 0) > 0).OrderByDescending(a => a.WcRare).ThenByDescending(a => a.WcMythic).Take(LeaderCount).Select(L).ToList();
        return new Totals(sums.Item1, sums.Item2, sums.Item3, sums.Item4, sums.Item5, sums.Item6, sets, buckets, logins.Count, players, mostBoosters, mostRares);
    }

    public const int LeaderCount = 20;

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
        return new Review(unmatched, ambiguous, mismatches, mappings, Drops(c));
    }

    /// <summary>
    /// Every display name seen in a log, matched or not, with its latest inventory: the Accounts tab only
    /// lists accounts from the CSV, so this is where an unmatched name's data shows.
    /// </summary>
    public IReadOnlyList<PlayerRow> Players()
    {
        using var c = db.Open();
        return c.Query<PlayerRow>("""
            SELECT p.DisplayName, p.Email, p.FirstSeenUtc, p.LastLoginUtc,
                   s.CapturedAtUtc AS LastInventoryUtc, s.Gems, s.Gold, s.WcRare, s.WcMythic,
                   (SELECT COUNT(*) FROM Snapshots x WHERE x.DisplayName = p.DisplayName) AS Snapshots
            FROM Players p
            LEFT JOIN Snapshots s ON s.Id = (SELECT s2.Id FROM Snapshots s2 WHERE s2.DisplayName = p.DisplayName ORDER BY s2.CapturedAtUtc DESC LIMIT 1)
            ORDER BY COALESCE(p.LastLoginUtc, '') DESC, p.DisplayName
            """).ToList();
    }

    /// <summary>
    /// Settable properties, not a positional record: SQLite gives window-function columns no declared type, so
    /// Dapper's constructor matching fails on them, while property mapping converts each value.
    /// </summary>
    private sealed class SnapshotPair
    {
        public string DisplayName { get; set; } = "";
        public string? Email { get; set; }
        public string CapturedAtUtc { get; set; } = "";
        public long? Gems { get; set; }
        public long? Gold { get; set; }
        public long? WcRare { get; set; }
        public long? WcMythic { get; set; }
        public string? PrevAt { get; set; }
        public long? PrevGems { get; set; }
        public long? PrevGold { get; set; }
        public long? PrevRare { get; set; }
        public long? PrevMythic { get; set; }
    }

    /// <summary>
    /// Drops between consecutive snapshots in the last AlertDays: spending you did not mean, or an account
    /// someone else used. Only fields both snapshots have are compared, so a partial update raises nothing.
    /// </summary>
    public IReadOnlyList<InventoryDrop> Drops(Microsoft.Data.Sqlite.SqliteConnection c, DateTime? nowUtc = null)
    {
        var o = options ?? new MtgaDashOptions();
        var cutoff = Time.Format((nowUtc ?? DateTime.UtcNow).AddDays(-Math.Max(1, o.AlertDays)));
        var pairs = c.Query<SnapshotPair>("""
            SELECT * FROM (
                SELECT s.DisplayName, p.Email, s.CapturedAtUtc, s.Gems, s.Gold, s.WcRare, s.WcMythic,
                       LAG(s.CapturedAtUtc) OVER w AS PrevAt, LAG(s.Gems) OVER w AS PrevGems, LAG(s.Gold) OVER w AS PrevGold,
                       LAG(s.WcRare) OVER w AS PrevRare, LAG(s.WcMythic) OVER w AS PrevMythic
                FROM Snapshots s LEFT JOIN Players p ON p.DisplayName = s.DisplayName
                WINDOW w AS (PARTITION BY s.DisplayName ORDER BY s.CapturedAtUtc))
            WHERE PrevAt IS NOT NULL AND CapturedAtUtc >= @cutoff
            """, new { cutoff });

        static long Change(long? now, long? before) => now is { } a && before is { } b ? a - b : 0;
        var drops = new List<InventoryDrop>();
        foreach (var p in pairs)
        {
            var gems = Change(p.Gems, p.PrevGems);
            var gold = Change(p.Gold, p.PrevGold);
            var rare = Change(p.WcRare, p.PrevRare);
            var mythic = Change(p.WcMythic, p.PrevMythic);
            var reasons = new List<string>();
            if (-gems >= o.AlertGemsDrop) reasons.Add($"gems down {-gems:N0}");
            if (-gold >= o.AlertGoldDrop) reasons.Add($"gold down {-gold:N0}");
            if (-(rare + mythic) >= o.AlertWildcardDrop) reasons.Add($"rare and mythic wildcards down {-(rare + mythic)}");
            if (reasons.Count > 0)
                drops.Add(new InventoryDrop(p.Email, p.DisplayName, p.PrevAt!, p.CapturedAtUtc, gems, gold, rare, mythic, reasons));
        }
        return drops.OrderByDescending(d => d.ToUtc, StringComparer.Ordinal).ToList();
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
