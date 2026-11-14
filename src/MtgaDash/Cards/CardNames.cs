using Dapper;
using Microsoft.Data.Sqlite;

namespace MtgaDash.Cards;

/// <summary>
/// Optional card names from MTGA's own card database. Its schema is not documented and changes between
/// client versions, so nothing about it is assumed: the tables are inspected at runtime for a card table
/// (a grpId and a title id) and an English localization table (a loc id and text). If either cannot be
/// found, names stay off and the reason is logged; the dashboard then shows card ids alone.
/// </summary>
public sealed class CardNames
{
    private readonly ILogger _logger;
    private readonly string _pattern;
    private Dictionary<int, string>? _names;
    private string? _loadedFrom;

    public CardNames(string pathOrPattern, ILogger logger)
    {
        _pattern = pathOrPattern;
        _logger = logger;
    }

    public string? Status { get; private set; }

    public IReadOnlyDictionary<int, string> Lookup(IEnumerable<int> ids)
    {
        var names = Load();
        var result = new Dictionary<int, string>();
        if (names is null) return result;
        foreach (var id in ids)
            if (names.TryGetValue(id, out var n)) result[id] = n;
        return result;
    }

    private Dictionary<int, string>? Load()
    {
        if (string.IsNullOrWhiteSpace(_pattern)) return null;
        var path = Resolve(_pattern);
        if (path is null)
        {
            SetStatus($"no card database matches {_pattern}");
            return null;
        }
        if (path == _loadedFrom) return _names;

        _loadedFrom = path;
        _names = null;
        try
        {
            _names = Read(path, out var description);
            SetStatus($"{_names.Count:N0} card names from {Path.GetFileName(path)} ({description})");
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or IOException)
        {
            SetStatus($"card names unavailable from {path}: {ex.Message}");
        }
        return _names;
    }

    private void SetStatus(string status)
    {
        if (status == Status) return;
        Status = status;
        _logger.LogInformation("Card names: {Status}", status);
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

    internal static Dictionary<int, string> Read(string path, out string description)
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

        // English text lives either in a per-language table (Localizations_enUS with a Loc column) or in an
        // enUS column of a shared table.
        var locTable = tables
            .Select(t => (Table: t, Id: Col(t, "LocId", "loc_id", "Id"), Text: Col(t, "enUS", "Loc", "Text", "Value"), Formatted: Col(t, "Formatted")))
            .Where(x => x.Id is not null && x.Text is not null && x.Table != cardTable.Table)
            .Where(x => x.Table.Contains("Loc", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Table.Contains("enUS", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Text, "enUS", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (locTable.Table is null) throw new InvalidDataException($"no localization table with a loc id and English text (tables: {string.Join(", ", tables)})");

        // Several formatted variants can share a loc id; the lowest Formatted value is the plain title.
        var order = locTable.Formatted is not null ? $"l.\"{locTable.Formatted}\"" : "1";
        var sql = $"""
            SELECT c."{cardTable.Grp}" AS Id, l."{locTable.Text}" AS Name
            FROM "{cardTable.Table}" c JOIN "{locTable.Table}" l ON l."{locTable.Id}" = c."{cardTable.Title}"
            ORDER BY {order} DESC
            """;
        var names = new Dictionary<int, string>();
        foreach (var (id, name) in c.Query<(long, string?)>(sql))
            if (!string.IsNullOrWhiteSpace(name)) names[(int)id] = name;
        description = $"{cardTable.Table}.{cardTable.Grp} → {locTable.Table}.{locTable.Text}";
        return names;
    }
}
