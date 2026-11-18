using MtgaDash.Accounts;
using MtgaDash.Data;

namespace MtgaDash.Ingestion;

/// <summary>Polls the archive folder and the live log every PollSeconds and ingests what grew.</summary>
public sealed class IngestionService(
    MtgaDashOptions options,
    Database db,
    LogIngestor ingestor,
    ChangeNotifier notifier,
    ExceptionStore exceptions,
    StatusTracker status,
    ILogger<IngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the startup thread, so Kestrel starts listening while a large first pass runs.
        await Task.Run(SeedAccounts, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Run(PollOnce, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ingestion poll failed; retrying next poll");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), stoppingToken);
        }
    }

    public void SeedAccounts()
    {
        using var c = db.Open();
        var empty = Dapper.SqlMapper.ExecuteScalar<long>(c, "SELECT COUNT(*) FROM Accounts") == 0;
        // Matching runs on every start, because the exception map is compiled in and may have changed;
        // an import runs it itself.
        if (empty && ImportAccounts(notify: false) > 0) return;
        LogMatching(Writer.RunMatching(c, exceptions));
    }

    public int ImportAccounts(bool notify = true)
    {
        var path = MtgaDashOptions.Resolve(options.AccountsCsvPath);
        if (!File.Exists(path))
        {
            logger.LogWarning("Accounts CSV not found at {Path}; the Accounts tab stays empty until it is imported", path);
            return 0;
        }
        var rows = AccountsCsv.Read(path);
        using var c = db.Open();
        var n = Writer.ImportAccounts(c, rows);
        logger.LogInformation("Imported {Count} accounts from {Path}", n, path);
        LogMatching(Writer.RunMatching(c, exceptions));
        if (notify) notifier.Notify();
        return n;
    }

    private void LogMatching(MatchResult r) =>
        logger.LogInformation("Matching: {Matched} matched, {Exception} by exception, {Ambiguous} ambiguous, {Unmatched} unmatched display names",
            r.Players.Count(p => p.State == MatchStates.Matched), r.Players.Count(p => p.State == MatchStates.Exception),
            r.Players.Count(p => p.State == MatchStates.Ambiguous), r.Players.Count(p => p.State == MatchStates.Unmatched));

    public IEnumerable<string> Files()
    {
        var folder = MtgaDashOptions.Resolve(options.LogFolder);
        if (folder.Length > 0)
        {
            Directory.CreateDirectory(folder);
            var pattern = string.IsNullOrWhiteSpace(options.LogFilePattern) ? "*.log" : options.LogFilePattern.Trim();
            foreach (var f in Directory.EnumerateFiles(folder, pattern).Order(StringComparer.OrdinalIgnoreCase)) yield return f;
        }
        if (options.WatchLiveLog && MtgaDashOptions.Resolve(options.LiveLogPath) is { Length: > 0 } live)
        {
            // MTGA renames Player.log to Player-prev.log each time it starts, so a session played while the
            // dashboard was off is still here until the next game start overwrites it.
            if (options.WatchPrevLog && Path.Combine(Path.GetDirectoryName(live) ?? "", "Player-prev.log") is var prev && File.Exists(prev))
                yield return prev;
            if (File.Exists(live)) yield return live;
        }
    }

    /// <summary>Re-runs matching after an exception mapping changed, and tells open pages.</summary>
    public void Rematch()
    {
        using var c = db.Open();
        LogMatching(Writer.RunMatching(c, exceptions));
        notifier.Notify();
    }

    public void PollOnce()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var polled = new List<string>();
        var changed = false;
        var newPlayers = 0;
        var files = 0;
        foreach (var file in Files())
        {
            files++;
            polled.Add(file);
            try
            {
                var r = ingestor.IngestFile(file);
                status.FileError(file, null);
                changed |= r.Counts.Any || r.Restarted;
                newPlayers += r.Counts.NewPlayers;
            }
            catch (IOException ex)
            {
                logger.LogWarning("{Path}: {Message}; retrying next poll", file, ex.Message);
                status.FileError(file, ex.Message);
            }
        }
        logger.LogDebug("Scanned {Files} log files", files);
        if (newPlayers > 0)
        {
            using var c = db.Open();
            LogMatching(Writer.RunMatching(c, exceptions));
        }
        status.Polled(polled, System.Diagnostics.Stopwatch.GetElapsedTime(started));
        if (changed) notifier.Notify();
    }
}
