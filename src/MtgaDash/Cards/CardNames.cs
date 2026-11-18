using Dapper;
using Microsoft.Data.Sqlite;

namespace MtgaDash.Cards;

/// <summary>What MTGA's card database says about one card. Any field it could not resolve is null.</summary>
public sealed record CardInfo(string? Name, string? Rarity, string? Set);

/// <summary>A card database found on this PC, and whether it could be read.</summary>
public sealed record CardDbCandidate(string Pattern, string File, bool Ok, int Cards, string Message);

/// <summary>What the setup dialog shows: the setting, the file it resolved to, and how that went.</summary>
public sealed record CardDbState(string Configured, string? ResolvedPath, int Cards, bool RarityKnown, string? Status);

/// <summary>
/// Optional card names, rarities and sets from MTGA's own card database. Its schema is not documented and
/// changes between client versions, so nothing about it is assumed: the tables are inspected at runtime for a
/// card table (a grpId and a title id) and an English localization table (a loc id and text). If either cannot
/// be found, card data stays off and the reason is logged; the dashboard then shows card ids alone.
/// </summary>
public sealed class CardNames
{
    private readonly ILogger _logger;
    private readonly Func<string> _pattern;
    private readonly Lock _gate = new();
    private Dictionary<int, CardInfo>? _cards;
    private string? _loadedFrom;

    public CardNames(string pathOrPattern, ILogger logger) : this(() => pathOrPattern, logger) { }

    /// <param name="pattern">Read on every lookup, so a CardDbPath saved in appsettings applies without a restart.</param>
    public CardNames(Func<string> pattern, ILogger logger)
    {
        _pattern = pattern;
        _logger = logger;
    }

    public CardDbState State()
    {
        var cards = Load();
        return new CardDbState(_pattern() ?? "", _loadedFrom, cards?.Count ?? 0, cards?.Values.Any(i => i.Rarity is not null) ?? false, Status);
    }

    public string? Status { get; private set; }

    /// <summary>Whether rarities are known, so the page can say why it cannot filter by rarity.</summary>
    public bool HasRarity => Load() is { } c && c.Values.Any(i => i.Rarity is not null);

    public IReadOnlyDictionary<int, string> Lookup(IEnumerable<int> ids) =>
        Info(ids).Where(kv => kv.Value.Name is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Name!);

    public IReadOnlyDictionary<int, CardInfo> Info(IEnumerable<int> ids)
    {
        var cards = Load();
        var result = new Dictionary<int, CardInfo>();
        if (cards is null) return result;
        foreach (var id in ids)
            if (cards.TryGetValue(id, out var c)) result[id] = c;
        return result;
    }

    private Dictionary<int, CardInfo>? Load()
    {
        lock (_gate) return LoadLocked();
    }

    private Dictionary<int, CardInfo>? LoadLocked()
    {
        var pattern = _pattern();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            _loadedFrom = null;
            _cards = null;
            SetStatus("CardDbPath is not set");
            return null;
        }
        var path = Resolve(pattern);
        if (path is null)
        {
            _loadedFrom = null;
            _cards = null;
            SetStatus($"no card database matches {pattern}");
            return null;
        }
        // MTGA writes a new Raw_CardDatabase_<hash>.mtga with each update; the wildcard then resolves to it here.
        if (path == _loadedFrom) return _cards;

        _loadedFrom = path;
        _cards = null;
        try
        {
            _cards = Read(path, out var description);
            SetStatus($"{_cards.Count:N0} cards from {Path.GetFileName(path)} ({description})");
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or IOException)
        {
            SetStatus($"card data unavailable from {path}: {ex.Message}");
        }
        return _cards;
    }

    private void SetStatus(string status)
    {
        if (status == Status) return;
        Status = status;
        _logger.LogInformation("Card database: {Status}", status);
    }

    /// <summary>The usual MTGA install folders: the Wizards installer, Steam and Epic, 64- and 32-bit Program Files.</summary>
    public static IEnumerable<string> DefaultInstallFolders(string? gamePath = null)
    {
        var roots = new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") }
            .Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(gamePath) && !gamePath.Contains("://", StringComparison.Ordinal)
            && Path.GetDirectoryName(MtgaDashOptions.Resolve(gamePath)) is { Length: > 0 } gameDir)
            folders.Add(gameDir);
        foreach (var r in roots)
        {
            folders.Add(Path.Combine(r!, "Wizards of the Coast", "MTGA"));
            folders.Add(Path.Combine(r!, "Steam", "steamapps", "common", "MTGA"));
            folders.Add(Path.Combine(r!, "Epic Games", "MagicTheGathering"));
        }
        return folders.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Each install folder that holds a card database, with the newest one tested.</summary>
    public static List<CardDbCandidate> FindCandidates(IEnumerable<string> installFolders)
    {
        var found = new List<CardDbCandidate>();
        foreach (var folder in installFolders)
        {
            var pattern = Path.Combine(folder, "MTGA_Data", "Downloads", "Raw", "Raw_CardDatabase_*.mtga");
            if (Resolve(pattern) is not { } file) continue;
            found.Add(Probe(pattern, file));
        }
        return found;
    }

    /// <summary>Reads a card database once to say whether it works, without changing what is in use.</summary>
    public static CardDbCandidate Probe(string pattern, string file)
    {
        try
        {
            var cards = Read(file, out var description);
            return new CardDbCandidate(pattern, file, true, cards.Count, description);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or IOException)
        {
            return new CardDbCandidate(pattern, file, false, 0, ex.Message);
        }
    }

    /// <summary>A literal path, or a folder plus a wildcard file name; the newest match wins.</summary>
    internal static string? Resolve(string pattern)
    {
        var full = MtgaDashOptions.Resolve(pattern);
        if (!full.Contains('*') && !full.Contains('?')) return File.Exists(full) ? full : null;
        var dir = Path.GetDirectoryName(full);
        if (dir is null || !Directory.Exists(dir)) return null;
        return new DirectoryInfo(dir).GetFiles(Path.GetFileName(full))
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    /// <summary>
    /// MTGA stores rarity as a small number. The mapping below (1 basic land … 5 mythic) is the order the client
    /// uses in its card data; text values ("Mythic") are accepted too. Anything else stays null rather than guessed.
    /// </summary>
    internal static string? RarityName(object? value) => value switch
    {
        null => null,
        long n => n switch { 0 => "token", 1 => "land", 2 => "common", 3 => "uncommon", 4 => "rare", 5 => "mythic", _ => null },
        string s when long.TryParse(s, out var n) => RarityName(n),
        string s => s.Trim().ToLowerInvariant() switch
        {
            "mythic" or "mythicrare" or "mythic rare" => "mythic",
            "rare" => "rare",
            "uncommon" => "uncommon",
            "common" => "common",
            "basicland" or "basic land" or "land" => "land",
            "token" => "token",
            _ => null,
        },
        _ => null,
    };

    internal static Dictionary<int, CardInfo> Read(string path, out string description)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();

        var tables = c.Query<string>("SELECT name FROM sqlite_master WHERE type IN ('table','view')").ToList();
        var columns = tables.ToDictionary(t => t, t => c.Query<string>($"SELECT name FROM pragma_table_info('{t.Replace("'", "''")}')").ToList());

        string? Col(string table, params string[] names) =>
            columns[table].FirstOrDefault(col => names.Any(n => string.Equals(col, n, StringComparison.OrdinalIgnoreCase)));

        var cardTable = tables
            .Select(t => (Table: t, Grp: Col(t, "GrpId", "grp_id", "GrpID"), Title: Col(t, "TitleId", "title_id", "TitleID")))
            .Where(x => x.Grp is not null && x.Title is not null)
            .OrderByDescending(x => string.Equals(x.Table, "Cards", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (cardTable.Table is null) throw new InvalidDataException($"no table with GrpId and TitleId columns (tables: {string.Join(", ", tables)})");
        var rarityCol = Col(cardTable.Table, "Rarity");
        var setCol = Col(cardTable.Table, "ExpansionCode", "Set", "SetCode");

        // English text lives either in a per-language table (Localizations_enUS with a Loc column) or in an
        // enUS column of a shared table.
        var locTable = tables
            .Select(t => (Table: t, Id: Col(t, "LocId", "loc_id", "Id"), Text: Col(t, "enUS", "Loc", "Text", "Value"), Formatted: Col(t, "Formatted")))
            .Where(x => x.Id is not null && x.Text is not null && x.Table != cardTable.Table)
            .Where(x => x.Table.Contains("Loc", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Table.Contains("enUS", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Text, "enUS", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (locTable.Table is null) throw new InvalidDataException($"no localization table with a loc id and English text (tables: {string.Join(", ", tables)})");

        // Several formatted variants can share a loc id; ordering by Formatted descending leaves the lowest,
        // the plain title, as the last one written.
        var order = locTable.Formatted is not null ? $"l.\"{locTable.Formatted}\" DESC" : "1";
        var rarity = rarityCol is not null ? $"c.\"{rarityCol}\"" : "NULL";
        var set = setCol is not null ? $"c.\"{setCol}\"" : "NULL";
        var sql = $"""
            SELECT c."{cardTable.Grp}" AS Id, l."{locTable.Text}" AS Name, {rarity} AS Rarity, {set} AS SetCode
            FROM "{cardTable.Table}" c LEFT JOIN "{locTable.Table}" l ON l."{locTable.Id}" = c."{cardTable.Title}"
            ORDER BY {order}
            """;
        var cards = new Dictionary<int, CardInfo>();
        using (var reader = c.ExecuteReader(sql))
        {
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var id = (int)reader.GetInt64(0);
                var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                var info = new CardInfo(
                    string.IsNullOrWhiteSpace(name) ? cards.GetValueOrDefault(id)?.Name : name,
                    RarityName(reader.IsDBNull(2) ? null : reader.GetValue(2)),
                    reader.IsDBNull(3) ? null : Convert.ToString(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture));
                cards[id] = info;
            }
        }
        description = $"{cardTable.Table}.{cardTable.Grp} → {locTable.Table}.{locTable.Text}"
            + (rarityCol is null ? ", no rarity column" : $", rarity from {rarityCol}")
            + (setCol is null ? "" : $", set from {setCol}");
        return cards;
    }
}
