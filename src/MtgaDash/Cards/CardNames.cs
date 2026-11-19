using Dapper;
using Microsoft.Data.Sqlite;

namespace MtgaDash.Cards;

/// <summary>
/// What MTGA's card database says about one card. Any field it could not resolve is null: Types is the
/// English type line ("Legendary Creature — Elf"), Cost uses {} symbols ("{2}{U}{U}"), Colors is letters in
/// WUBRG order ("WU"; "C" for colorless).
/// </summary>
public sealed record CardInfo(string? Name, string? Rarity, string? Set,
    string? Types = null, string? Cost = null, string? Colors = null, bool Alchemy = false);

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
public sealed partial class CardNames
{
    /// <summary>Alchemy's digital-only sets are coded Y followed by a year or a set (Y22, YMID, YONE).</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^Y[A-Z0-9]{2,4}$")]
    private static partial System.Text.RegularExpressions.Regex AlchemySet();

    /// <summary>"o2oUoU" (MTGA's mana text) → "{2}{U}{U}". Anything not in that shape is kept as written.</summary>
    internal static string? ManaCost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.Contains('{')) return raw;
        if (!raw.StartsWith('o')) return raw;
        var parts = raw.Split('o', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => "{" + p.Trim('(', ')') + "}"));
    }

    private static readonly string[] ColorOrder = ["W", "U", "B", "R", "G"];

    /// <summary>
    /// The Colors column as numbers (1 white … 5 green, MTGA's order) or letters; without that column, the
    /// colored symbols in the cost. An empty Colors value is colorless ("C"); null means the column is missing.
    /// </summary>
    internal static string? ColorLetters(string? colors, string? cost)
    {
        var found = new HashSet<string>();
        if (colors is not null)
        {
            foreach (var part in colors.Split(',', ' ', ';'))
            {
                var p = part.Trim();
                if (int.TryParse(p, out var n) && n is >= 1 and <= 5) found.Add(ColorOrder[n - 1]);
                else if (p.Length == 1 && ColorOrder.Contains(p.ToUpperInvariant())) found.Add(p.ToUpperInvariant());
            }
        }
        else if (cost is not null)
        {
            foreach (var letter in ColorOrder)
                if (cost.Contains(letter, StringComparison.Ordinal)) found.Add(letter);
        }
        else return null;
        return found.Count == 0 ? "C" : string.Concat(ColorOrder.Where(found.Contains));
    }

    /// <summary>
    /// Only when the database has no type line text: MTGA's card type numbers, in the order its rules engine
    /// uses. Inferred, so the type line text is always preferred.
    /// </summary>
    internal static string? TypesFromEnum(string? types)
    {
        if (string.IsNullOrWhiteSpace(types)) return null;
        string? Name(string n) => n.Trim() switch
        {
            "1" => "Artifact", "2" => "Creature", "3" => "Enchantment", "4" => "Instant", "5" => "Land",
            "8" => "Planeswalker", "10" => "Sorcery", "11" => "Kindred", "14" => "Battle", _ => null,
        };
        var names = types.Split(',').Select(Name).OfType<string>().ToList();
        return names.Count > 0 ? string.Join(" ", names) : null;
    }

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
        string? CardCol(params string[] names) => Col(cardTable.Table, names);
        var rarityCol = CardCol("Rarity");
        var setCol = CardCol("ExpansionCode", "Set", "SetCode");
        var typeTextCol = CardCol("TypeTextId");
        var typesCol = CardCol("Types");
        var costCol = CardCol("OldSchoolManaText", "ManaCost", "CastingCost");
        var colorsCol = CardCol("Colors");
        var rebalancedCol = CardCol("IsRebalanced");

        // English text lives either in a per-language table (Localizations_enUS with a Loc column) or in an
        // enUS column of a shared table.
        var locTable = tables
            .Select(t => (Table: t, Id: Col(t, "LocId", "loc_id", "Id"), Text: Col(t, "enUS", "Loc", "Text", "Value"), Formatted: Col(t, "Formatted")))
            .Where(x => x.Id is not null && x.Text is not null && x.Table != cardTable.Table)
            .Where(x => x.Table.Contains("Loc", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Table.Contains("enUS", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Text, "enUS", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (locTable.Table is null) throw new InvalidDataException($"no localization table with a loc id and English text (tables: {string.Join(", ", tables)})");

        // Titles and type lines share one text table. Several formatted variants can share a loc id; ordering by
        // Formatted descending leaves the lowest, the plain text, as the last one written.
        var text = new Dictionary<long, string>();
        var order = locTable.Formatted is not null ? $"\"{locTable.Formatted}\" DESC" : "1";
        using (var r = c.ExecuteReader($"SELECT \"{locTable.Id}\", \"{locTable.Text}\" FROM \"{locTable.Table}\" ORDER BY {order}"))
            while (r.Read())
                if (!r.IsDBNull(0) && !r.IsDBNull(1) && r.GetValue(1) is string v && !string.IsNullOrWhiteSpace(v)) text[r.GetInt64(0)] = v;

        static string Or(string? col) => col is null ? "NULL" : $"\"{col}\"";
        var sql = $"""
            SELECT "{cardTable.Grp}", "{cardTable.Title}", {Or(rarityCol)}, {Or(setCol)}, {Or(typeTextCol)}, {Or(typesCol)},
                   {Or(costCol)}, {Or(colorsCol)}, {Or(rebalancedCol)}
            FROM "{cardTable.Table}"
            """;
        var cards = new Dictionary<int, CardInfo>();
        using (var reader = c.ExecuteReader(sql))
        {
            object? Val(int i) => reader.IsDBNull(i) ? null : reader.GetValue(i);
            string? Text(object? id) => id is long n && text.TryGetValue(n, out var t) ? t
                : id is string s2 && long.TryParse(s2, out var m) && text.TryGetValue(m, out var t2) ? t2 : null;
            while (reader.Read())
            {
                if (reader.IsDBNull(0)) continue;
                var id = (int)reader.GetInt64(0);
                var name = Text(Val(1));
                var set = Val(3) is { } sv ? Convert.ToString(sv, System.Globalization.CultureInfo.InvariantCulture) : null;
                var cost = ManaCost(Val(6) as string);
                var types = Text(Val(4)) ?? TypesFromEnum(Val(5) as string);
                var alchemy = Val(8) is long rb && rb != 0
                    || (name?.StartsWith("A-", StringComparison.Ordinal) ?? false)
                    || (set is not null && AlchemySet().IsMatch(set));
                cards[id] = new CardInfo(name, RarityName(Val(2)), set, types, cost,
                    ColorLetters(colorsCol is null ? null : Convert.ToString(Val(7), System.Globalization.CultureInfo.InvariantCulture) ?? "", cost), alchemy);
            }
        }
        description = $"{cardTable.Table}.{cardTable.Grp} → {locTable.Table}.{locTable.Text}"
            + (rarityCol is null ? ", no rarity column" : $", rarity from {rarityCol}")
            + (setCol is null ? "" : $", set from {setCol}")
            + (typeTextCol is null ? (typesCol is null ? ", no type column" : $", types from {typesCol} (inferred)") : $", type line from {typeTextCol}")
            + (costCol is null ? ", no cost column" : $", cost from {costCol}");
        return cards;
    }
}
