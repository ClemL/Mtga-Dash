using MtgaDash.Data;
using MtgaDash.Ingestion;

namespace MtgaDash.Cards;

/// <summary>
/// Fetches missing card images without a click: AutoDownloadDelaySeconds after new log data stops arriving,
/// which in practice is just after a play session ends, and once at startup to catch up. It asks only for
/// cards that are missing, so a quiet period with nothing new costs no requests at all.
/// </summary>
public sealed class CardImageAutoDownload : IHostedService, IDisposable
{
    private readonly MtgaDashOptions _options;
    private readonly Queries _queries;
    private readonly CardImageStore _images;
    private readonly ChangeNotifier _notifier;
    private readonly ILogger<CardImageAutoDownload> _logger;
    private readonly Timer _timer;
    private TimeSpan QuietPeriod => TimeSpan.FromSeconds(Math.Max(5, _options.AutoDownloadDelaySeconds));

    public CardImageAutoDownload(MtgaDashOptions options, Queries queries, CardImageStore images, ChangeNotifier notifier, ILogger<CardImageAutoDownload> logger)
    {
        _options = options;
        _queries = queries;
        _images = images;
        _notifier = notifier;
        _logger = logger;
        _timer = new Timer(_ => RunOnce(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Starts a download if anything is missing; true if one started. Public for tests and the timer.</summary>
    public bool RunOnce()
    {
        try
        {
            var wanted = _queries.ImageWanted().ToList();
            var missing = _images.Missing(wanted).Count;
            if (missing == 0) return false;
            var started = _images.StartDownload(wanted);
            if (started) _logger.LogInformation("Downloading {Count} missing card images in the background", missing);
            return started;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogWarning("Automatic card image download skipped: {Message}", ex.Message);
            return false;
        }
    }

    // Every change pushes the run back, so it happens once, after the session, not on every poll during it.
    private void OnChanged() => _timer.Change(QuietPeriod, Timeout.InfiniteTimeSpan);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutoDownloadCardImages) return Task.CompletedTask;
        _notifier.Changed += OnChanged;
        _timer.Change(QuietPeriod, Timeout.InfiniteTimeSpan); // catch up once after startup's first ingest
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _notifier.Changed -= OnChanged;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer.Dispose();
}
