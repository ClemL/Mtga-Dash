using System.Text.Json;
using MtgaDash.Data;
using MtgaDash.Ingestion;

namespace MtgaDash.Web;

/// <summary>
/// Writes a self-contained, read-only copy of the dashboard (data embedded as JSON) for viewing on a phone
/// while the PC is off. Runs 30 s after the last change when ExportFolder is set, or on demand.
/// </summary>
public sealed class StaticExporter : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(30);

    private readonly MtgaDashOptions _options;
    private readonly Queries _queries;
    private readonly PageRenderer _renderer;
    private readonly ILogger<StaticExporter> _logger;
    private readonly Timer _timer;
    private readonly Lock _gate = new();

    public StaticExporter(MtgaDashOptions options, Queries queries, PageRenderer renderer, ChangeNotifier notifier, ILogger<StaticExporter> logger)
    {
        _options = options;
        _queries = queries;
        _renderer = renderer;
        _logger = logger;
        _timer = new Timer(_ => SafeExport(), null, Timeout.Infinite, Timeout.Infinite);
        if (!string.IsNullOrWhiteSpace(options.ExportFolder))
            notifier.Changed += () => _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>ExportFolder, or <c>export</c> beside the database's folder when it is not set.</summary>
    public string Folder => !string.IsNullOrWhiteSpace(_options.ExportFolder)
        ? MtgaDashOptions.Resolve(_options.ExportFolder)
        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(MtgaDashOptions.Resolve(_options.DatabasePath)) ?? ".", "..", "export"));

    public object Snapshot()
    {
        var accounts = _queries.Accounts();
        return new
        {
            generatedAtUtc = Time.Format(DateTime.UtcNow),
            accounts,
            totals = _queries.Totals(),
            review = _queries.Review(),
            statuses = _queries.Statuses(),
            details = accounts.ToDictionary(a => a.Email, a => _queries.Account(a.Email)),
        };
    }

    public (string Html, string Json) Export()
    {
        lock (_gate)
        {
            var folder = Folder;
            Directory.CreateDirectory(folder);
            var data = Snapshot();
            var html = Path.Combine(folder, "mtga_dashboard.html");
            var json = Path.Combine(folder, $"mtga_export_{DateTime.Now:yyyyMMdd_HHmm}.json");
            File.WriteAllText(html, _renderer.Render(data, _renderer.ReadChangelog()));
            File.WriteAllText(json, JsonSerializer.Serialize(data, new JsonSerializerOptions(PageRenderer.JsonOptions) { WriteIndented = true }));
            _logger.LogInformation("Exported {Html} and {Json}", html, json);
            Console.WriteLine($"Static export: {html}");
            Console.WriteLine($"Data export:   {json}");
            Console.WriteLine("Upload to OneDrive: https://inscriptrx-my.sharepoint.com/");
            return (html, json);
        }
    }

    private void SafeExport()
    {
        try
        {
            Export();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Static export to {Folder} failed", Folder);
        }
    }

    public void Dispose() => _timer.Dispose();
}
