using System.Globalization;
using MtgaDash.Data;
using MtgaDash.Parsing;

namespace MtgaDash.Ingestion;

public sealed record ActivityEntry(
    long Id, string AtUtc, string? LogTimeUtc, string File, long Line, string Kind, string? DisplayName, string Summary);

public sealed record MessageCount(
    string Direction, string Method, long Count, long Handled, IReadOnlyList<string> Hints,
    string LastSeenUtc, string LastFile, string? LastDisplayName, long LargestBytes);

/// <summary>
/// What ingestion found, as it happens: each login, inventory, deck and rank, and a count of every message
/// MTGA wrote, read or not. Held in memory only (it restarts with the app), because it exists to answer
/// "why did nothing change?" while you play, not to keep history.
/// </summary>
public sealed class ActivityLog
{
    private const int MaxEntries = 500;
    private const int MaxMethods = 400;

    private readonly Lock _gate = new();
    private readonly LinkedList<ActivityEntry> _entries = new();
    private readonly Dictionary<(string, string), Counter> _messages = [];
    private long _nextId = 1;

    private sealed class Counter
    {
        public long Count;
        public long Handled;
        public readonly HashSet<string> Hints = new(StringComparer.OrdinalIgnoreCase);
        public DateTime LastSeenUtc;
        public string LastFile = "";
        public string? LastDisplayName;
        public long LargestBytes;
    }

    public void Record(string path, LogEvent e)
    {
        var file = Path.GetFileName(path);
        lock (_gate)
        {
            switch (e)
            {
                case MessageSeen m:
                    Count(m, file);
                    // A message that mentions inventory but produced nothing is the likely answer to
                    // "I played and the numbers did not move".
                    if (!m.Handled && m.Hints.Count > 0 && m.DisplayName is not null)
                        Add(file, e.Line, null, "not read", m.DisplayName,
                            $"{m.Method} ({m.Direction}) mentions {string.Join(", ", m.Hints)}, but the parser does not read it yet");
                    break;
                case SessionStarted s:
                    Add(file, e.Line, s.StartedAtUtc, "client start", null, "MTGA started a new session in this log");
                    break;
                case LoggedIn l:
                    Add(file, e.Line, l.AtUtc, "login", l.DisplayName, l.ClientVersion is null ? "Logged in" : $"Logged in, client {l.ClientVersion}");
                    break;
                case InventorySnapshot i:
                    Add(file, e.Line, i.CapturedAtUtc, "inventory", i.DisplayName, Describe(i));
                    break;
                case DeckSummaries d:
                    Add(file, e.Line, d.SeenAtUtc, "decks", d.DisplayName, $"{d.Decks.Count} decks, {d.Decks.Count(x => !x.IsPrecon)} custom");
                    break;
                case DeckList dl:
                    Add(file, e.Line, dl.SeenAtUtc, "deck saved", dl.DisplayName,
                        $"{dl.Summary.Name}: {dl.MainDeck.Sum(c => c.Quantity)} main, {dl.Sideboard.Sum(c => c.Quantity)} sideboard");
                    break;
                case RankInfo r:
                    Add(file, e.Line, r.CapturedAtUtc, "rank", r.DisplayName,
                        $"Constructed {r.ConstructedClass ?? "unranked"} {r.ConstructedLevel}, limited {r.LimitedClass ?? "unranked"} {r.LimitedLevel}".Replace("  ", " "));
                    break;
                case ParseWarning w:
                    Add(file, e.Line, null, "warning", null, w.Message);
                    break;
            }
        }
    }

    internal static string Describe(InventorySnapshot i)
    {
        static string N(int? v) => v is { } x ? x.ToString("N0", CultureInfo.InvariantCulture) : "?";
        var parts = new List<string>();
        if (i.Gems is not null) parts.Add($"gems {N(i.Gems)}");
        if (i.Gold is not null) parts.Add($"gold {N(i.Gold)}");
        if (i.WcCommon is not null || i.WcRare is not null)
            parts.Add($"wildcards {N(i.WcCommon)}/{N(i.WcUncommon)}/{N(i.WcRare)}/{N(i.WcMythic)}");
        if (i.Boosters is not null) parts.Add($"{i.Boosters.Sum(b => b.Count)} boosters");
        var text = parts.Count > 0 ? string.Join(", ", parts) : "no inventory fields";
        // A partial update changes only what it lists; the rest keeps the session's earlier values.
        return i.Boosters is null ? text + " (partial update)" : text;
    }

    private void Count(MessageSeen m, string file)
    {
        var key = (m.Direction, m.Method);
        if (!_messages.TryGetValue(key, out var c))
        {
            if (_messages.Count >= MaxMethods) return;
            _messages[key] = c = new Counter();
        }
        c.Count++;
        if (m.Handled) c.Handled++;
        c.Hints.UnionWith(m.Hints);
        c.LastSeenUtc = DateTime.UtcNow;
        c.LastFile = file;
        c.LastDisplayName = m.DisplayName ?? c.LastDisplayName;
        c.LargestBytes = Math.Max(c.LargestBytes, m.Bytes);
    }

    private void Add(string file, long line, DateTime? logTime, string kind, string? name, string summary)
    {
        _entries.AddFirst(new ActivityEntry(_nextId++, Time.Format(DateTime.UtcNow), Time.Format(logTime), file, line, kind, name, summary));
        while (_entries.Count > MaxEntries) _entries.RemoveLast();
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ActivityEntry> Entries(int max = 300)
    {
        lock (_gate) return _entries.Take(max).ToList();
    }

    /// <summary>Most recently seen first.</summary>
    public IReadOnlyList<MessageCount> Messages()
    {
        lock (_gate)
        {
            return _messages.OrderByDescending(kv => kv.Value.LastSeenUtc).ThenBy(kv => kv.Key.Item2)
                .Select(kv => new MessageCount(kv.Key.Item1, kv.Key.Item2, kv.Value.Count, kv.Value.Handled,
                    kv.Value.Hints.Order(StringComparer.OrdinalIgnoreCase).ToList(), Time.Format(kv.Value.LastSeenUtc),
                    kv.Value.LastFile, kv.Value.LastDisplayName, kv.Value.LargestBytes))
                .ToList();
        }
    }

    /// <summary>
    /// The last lines of a log file, each cut to <paramref name="maxChars"/>: StartHook bodies run to megabytes.
    /// Reads only the file's tail, sharing it with MTGA like the ingestor does.
    /// </summary>
    public static IReadOnlyList<string> Tail(string path, int lines, int maxChars = 400)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Long lines can fill the tail on their own, so read more as needed, up to 8 MB.
        for (long window = 256 * 1024; ; window *= 4)
        {
            var start = Math.Max(0, fs.Length - window);
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - start];
            fs.ReadExactly(buffer);
            var text = System.Text.Encoding.UTF8.GetString(buffer);
            var all = text.Split('\n');
            // The first piece is a partial line unless the window reached the start of the file.
            var complete = start > 0 ? all.Skip(1) : all;
            var result = complete.Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(lines)
                .Select(l => l.Length > maxChars ? l[..maxChars] + $" … ({l.Length:N0} characters)" : l).ToList();
            if (result.Count >= lines || start == 0 || window >= 8 * 1024 * 1024) return result;
        }
    }
}
