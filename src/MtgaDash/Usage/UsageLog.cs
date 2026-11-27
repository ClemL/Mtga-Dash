using Dapper;
using MtgaDash.Data;

namespace MtgaDash.Usage;

/// <summary>One thing done on the page, as the page reports it.</summary>
public sealed record UsageEventIn(string? At, string? Session, string? Kind, string? Name, string? Value);

/// <summary>
/// Stores the page's usage events in <c>UsageEvents</c>. Accepts only known kinds and short values, redacts
/// emails and display names again (the page already leaves them out), and deletes what is older than
/// <see cref="MtgaDashOptions.UsageRetentionDays"/>.
/// </summary>
public sealed class UsageLog(Database db, MtgaDashOptions options)
{
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
        { "load", "display", "view", "command", "click", "change", "search", "hover", "error" };
    internal const int MaxBatch = 200;
    private DateTime _lastPruneUtc;

    public int Add(IEnumerable<UsageEventIn>? events, DateTime? nowUtc = null)
    {
        if (!options.TrackUsage || events is null) return 0;
        var now = nowUtc ?? DateTime.UtcNow;
        var rows = events.Take(MaxBatch)
            .Where(e => e.Kind is { } k && Kinds.Contains(k) && !string.IsNullOrWhiteSpace(e.Name))
            .Select(e => new
            {
                at = Time.Format(ParseTime(e.At, now)),
                session = Redact.Text(e.Session, 16),
                kind = e.Kind!,
                name = Redact.Text(e.Name, 80),
                value = e.Value is null ? null : Redact.Text(e.Value, 80),
            })
            .ToList();
        if (rows.Count == 0) return 0;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("INSERT INTO UsageEvents(AtUtc, Session, Kind, Name, Value) VALUES (@at, @session, @kind, @name, @value)", rows, tx);
        if (now - _lastPruneUtc > TimeSpan.FromHours(1))
        {
            c.Execute("DELETE FROM UsageEvents WHERE AtUtc < @cutoff",
                new { cutoff = Time.Format(now.AddDays(-Math.Max(1, options.UsageRetentionDays))) }, tx);
            _lastPruneUtc = now;
        }
        tx.Commit();
        return rows.Count;
    }

    // The page's clock is the PC's clock, but a bad value must not land far outside the retention window.
    private static DateTime ParseTime(string? at, DateTime now) =>
        DateTime.TryParse(at, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
        && Math.Abs((t - now).TotalDays) < 1 ? t : now;
}
