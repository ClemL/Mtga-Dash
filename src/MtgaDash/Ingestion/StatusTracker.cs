using System.Text.Json;
using Dapper;
using MtgaDash.Data;
using MtgaDash.Parsing;

namespace MtgaDash.Ingestion;

public sealed record WarningEntry(string AtUtc, string Path, long Line, string Message);

public sealed record FileStatus(
    string Path, string Name, bool Exists, long Length, long Processed, string? LastWriteUtc,
    string? DisplayName, string? SessionTimeUtc, string? Error);

public sealed record IngestStatus(
    string? LastPollUtc, double LastPollMs, int PollSeconds, IReadOnlyList<FileStatus> Files,
    int Warnings24h, IReadOnlyList<WarningEntry> RecentWarnings, IReadOnlyList<string> Issues);

/// <summary>
/// What the Status tab shows: whether polling runs, how far each file is read, recent parse warnings, and
/// the one failure that is otherwise silent: MTGA running with Detailed Logs off, which records logins
/// but no inventory.
/// </summary>
public sealed class StatusTracker(Database db, MtgaDashOptions options)
{
    private const int MaxWarnings = 200;

    /// <summary>How long a logged-in session may go without a StartHook before it is reported.</summary>
    internal static readonly TimeSpan InventoryGrace = TimeSpan.FromMinutes(5);

    /// <summary>Only files written this recently count as a session in progress.</summary>
    internal static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();
    private readonly LinkedList<(DateTime At, WarningEntry Entry)> _warnings = new();
    private readonly Dictionary<string, string> _fileErrors = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _lastPollUtc;
    private double _lastPollMs;
    private IReadOnlyList<string> _polledFiles = [];

    public void Warning(string path, ParseWarning w)
    {
        lock (_gate)
        {
            _warnings.AddLast((DateTime.UtcNow, new WarningEntry(Time.Format(DateTime.UtcNow), path, w.LineNumber, w.Message)));
            while (_warnings.Count > MaxWarnings) _warnings.RemoveFirst();
        }
    }

    public void FileError(string path, string? message)
    {
        lock (_gate)
        {
            if (message is null) _fileErrors.Remove(path);
            else _fileErrors[path] = message;
        }
    }

    public void Polled(IReadOnlyList<string> files, TimeSpan took)
    {
        lock (_gate)
        {
            _lastPollUtc = DateTime.UtcNow;
            _lastPollMs = took.TotalMilliseconds;
            _polledFiles = files;
        }
    }

    private sealed record Row(string Path, long LengthProcessed, string ParserStateJson);

    public IngestStatus Snapshot(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        Dictionary<string, Row> rows;
        using (var c = db.Open())
            rows = c.Query<Row>("SELECT Path, LengthProcessed, ParserStateJson FROM IngestedFiles").ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            var issues = new List<string>();
            // Once a poll has run, only the files it read: a file excluded by LogFilePattern since is history.
            IEnumerable<string> source = _lastPollUtc is null ? rows.Keys : _polledFiles;
            var paths = source.Order(StringComparer.OrdinalIgnoreCase);
            var files = new List<FileStatus>();
            foreach (var path in paths)
            {
                var info = new FileInfo(path);
                rows.TryGetValue(path, out var row);
                var state = row is null ? null : TryState(row.ParserStateJson);
                _fileErrors.TryGetValue(path, out var error);
                files.Add(new FileStatus(path, info.Name, info.Exists, info.Exists ? info.Length : 0, row?.LengthProcessed ?? 0,
                    info.Exists ? Time.Format(info.LastWriteTimeUtc) : null, state?.DisplayName, Time.Format(state?.SessionTimeUtc), error));
                if (error is not null) issues.Add($"{info.Name} could not be read: {error}");
                if (info.Exists && MissingLogin(state, info.LastWriteTimeUtc, now))
                    issues.Add($"MTGA is writing {info.Name} but no login has been recognized since the client started, so this session's inventory, decks and rank are being skipped. If you are logged in, look for the login line in the log (Activity tab, Last lines of a log) and report it.");
                if (info.Exists && MissingInventory(state, info.LastWriteTimeUtc, now))
                    issues.Add($"{state!.DisplayName} is logged in ({info.Name}) but no inventory has been logged. Turn on Detailed Logs (Plugin Support) in MTGA: Options, Account.");
            }

            if (_lastPollUtc is { } last && now - last > TimeSpan.FromSeconds(Math.Max(30, options.PollSeconds * 6)))
                issues.Add($"The last poll was {(int)(now - last).TotalSeconds} s ago; ingestion may be stuck. See the console window.");
            if (options.WatchLiveLog && !File.Exists(MtgaDashOptions.Resolve(options.LiveLogPath)))
                issues.Add("The live log was not found at LiveLogPath; only archived logs are read.");

            var since = now.AddHours(-24);
            return new IngestStatus(Time.Format(_lastPollUtc), Math.Round(_lastPollMs, 1), options.PollSeconds, files,
                _warnings.Count(w => w.At >= since), _warnings.Reverse().Take(20).Select(w => w.Entry).ToList(), issues);
        }
    }

    /// <summary>A login whose session has not produced a StartHook, in a file still being written.</summary>
    internal static bool MissingInventory(LogParserState? state, DateTime lastWriteUtc, DateTime nowUtc) =>
        state is { DisplayName: not null, SessionTimeUtc: null, BlockStartedUtc: { } started }
        && nowUtc - lastWriteUtc <= ActiveWindow
        && nowUtc - started >= InventoryGrace;

    /// <summary>A client session in a file still being written, past the grace period, with no login recognized.</summary>
    internal static bool MissingLogin(LogParserState? state, DateTime lastWriteUtc, DateTime nowUtc) =>
        state is { DisplayName: null, BlockStartedUtc: { } started }
        && nowUtc - lastWriteUtc <= ActiveWindow
        && nowUtc - started >= InventoryGrace;

    private static LogParserState? TryState(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LogParserState>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
