using System.Net;
using System.Text;
using System.Text.Json;

namespace MtgaDash.Cards;

public sealed record CardImageStatus(
    string Folder, int Wanted, int Have, int Missing, int NotFound, bool Running, int Done, int ToDo, string? LastMessage,
    CardImageRun? LastRun, string? PausedUntilUtc);

/// <summary>
/// The latest finished download, whatever started it: <see cref="CardImageStore.Automatic"/>,
/// <see cref="CardImageStore.Button"/> or <see cref="CardImageStore.CommandLine"/>. Kept in the image folder
/// so a command-line run and a restart still show on the Status tab.
/// </summary>
public sealed record CardImageRun(
    string Trigger, string StartedUtc, string FinishedUtc, bool Completed, List<string> Saved, List<string> NotFound, List<string> Failed,
    string? PausedUntilUtc = null);

/// <summary>
/// A local folder of card images, one JPEG per card name, and the downloader that fills it from Scryfall.
/// Images are kept by name, not by MTGA id, because the owner wants each card's newest art whichever
/// printing their decks hold. A card Scryfall has no image for is remembered for <see cref="RetryNotFoundAfter"/>
/// so it is not asked for on every run; the page shows a placeholder for it meanwhile.
/// </summary>
public sealed class CardImageStore
{
    /// <summary>
    /// Time between searches. Scryfall allows its search endpoint far fewer requests than the 10 a second it
    /// allows elsewhere, and a run of searches at 10 a second drew HTTP 429s on the owner's first download.
    /// </summary>
    internal TimeSpan SearchGap { get; init; } = TimeSpan.FromMilliseconds(600);
    /// <summary>Time between image downloads, which come from Scryfall's file servers, not its API.</summary>
    internal TimeSpan RequestGap { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>How long to wait on a 429 with no Retry-After header before the single retry.</summary>
    internal TimeSpan TooManyRequestsWait { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>After a second 429, no run starts for this long: Scryfall may ban an address that keeps going.</summary>
    internal static readonly TimeSpan PauseAfterLimit = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan RetryNotFoundAfter = TimeSpan.FromDays(7);
    private const string NotFoundFile = "not-found.json";
    private const string LastRunFile = "last-run.json";
    public const string Automatic = "automatic", Button = "button", CommandLine = "command line";

    private readonly HttpClient _http;
    private readonly string _api;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private int _done, _toDo;
    private string? _lastMessage;
    private Task? _running;
    private DateTime _pausedUntilUtc;

    public CardImageStore(string folder, string apiBase, HttpClient http, ILogger logger)
    {
        Folder = folder;
        _api = apiBase.TrimEnd('/');
        _http = http;
        _logger = logger;
    }

    public string Folder { get; }

    /// <summary>Scryfall requires an identifying User-Agent and an Accept header on API calls.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MtgaDash/" + Web.BuildInfo.Version);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json;q=0.9,*/*;q=0.8");
        return http;
    }

    /// <summary>
    /// The file name for a card: letters, digits, spaces and a few safe marks kept, everything else "_". The
    /// result can never contain a path separator or "..", so it cannot point outside the folder.
    /// </summary>
    public static string KeyFor(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '\'' or ',' ? ch : '_');
        var key = sb.ToString().Trim().TrimEnd('.');
        return key.Length == 0 ? "_" : key;
    }

    public string PathFor(string name) => Path.Combine(Folder, KeyFor(name) + ".jpg");

    public bool Has(string name) => File.Exists(PathFor(name));

    /// <summary>The image file for a key from a URL, or null if the key is not one KeyFor could produce.</summary>
    public string? FileForKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || KeyFor(key) != key) return null;
        var path = Path.Combine(Folder, key + ".jpg");
        return File.Exists(path) ? path : null;
    }

    private Dictionary<string, DateTime> ReadNotFound()
    {
        try
        {
            var path = Path.Combine(Folder, NotFoundFile);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private void WriteNotFound(Dictionary<string, DateTime> notFound)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, NotFoundFile), JsonSerializer.Serialize(notFound, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Names with no image yet, leaving out those Scryfall recently had nothing for.</summary>
    public List<string> Missing(IEnumerable<string> wanted, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var notFound = ReadNotFound();
        return wanted.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !Has(n) && !(notFound.TryGetValue(n, out var at) && now - at < RetryNotFoundAfter))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public CardImageRun? LastRun()
    {
        try
        {
            var path = Path.Combine(Folder, LastRunFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<CardImageRun>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private void WriteLastRun(CardImageRun run)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, LastRunFile), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException ex)
        {
            // The record is only for the Status tab; failing to write it must not fail the download.
            _logger.LogWarning("Could not record the card image run: {Message}", ex.Message);
        }
    }

    public CardImageStatus Status(IReadOnlyCollection<string> wanted)
    {
        var notFound = ReadNotFound();
        var have = wanted.Count(Has);
        var missing = Missing(wanted).Count;
        var paused = PausedUntil;
        lock (_gate)
            return new CardImageStatus(Folder, wanted.Count, have, missing,
                wanted.Count(n => !Has(n) && notFound.ContainsKey(n)), _running is { IsCompleted: false }, _done, _toDo, _lastMessage,
                LastRun(), Data.Time.Format(paused));
    }

    /// <summary>Starts a download in the background unless one is running. Returns false if one was.</summary>
    public bool StartDownload(IReadOnlyCollection<string> wanted, string trigger = Button)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false } || PausedUntil is not null) return false;
            _running = Task.Run(() => DownloadMissingAsync(wanted, null, CancellationToken.None, trigger));
            return true;
        }
    }

    /// <summary>When Scryfall last said "too many requests" twice running, the time until which no run starts.</summary>
    public DateTime? PausedUntil
    {
        get
        {
            // The file covers a pause set by the command-line tool, which runs in another process.
            DateTime until;
            lock (_gate) until = _pausedUntilUtc;
            if (LastRun()?.PausedUntilUtc is { } s && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var fromFile) && fromFile > until)
                until = fromFile;
            return until > DateTime.UtcNow ? until : null;
        }
    }

    public sealed record Report(int Downloaded, int NotFound, int Failed);

    /// <summary>Downloads the newest art of every missing card, one at a time, at Scryfall's requested pace.</summary>
    public async Task<Report> DownloadMissingAsync(IEnumerable<string> wanted, Action<string>? progress, CancellationToken cancel, string trigger = CommandLine)
    {
        var started = DateTime.UtcNow;
        var missing = Missing(wanted);
        var notFound = ReadNotFound();
        List<string> saved = [], absentNames = [], failedNames = [];
        var completed = false;
        try
        {
            await DownloadAsync(missing, notFound, saved, absentNames, failedNames, progress, cancel);
            completed = true;
        }
        catch (TooManyRequestsException)
        {
            // Already reported and logged; the run ends early and says so in its record.
        }
        finally
        {
            // Written even when cut off, so the Status tab says what a stopped run did fetch.
            DateTime paused;
            lock (_gate) paused = _pausedUntilUtc;
            WriteLastRun(new CardImageRun(trigger, Data.Time.Format(started), Data.Time.Format(DateTime.UtcNow), completed, saved, absentNames, failedNames,
                paused > DateTime.UtcNow ? Data.Time.Format(paused) : null));
        }
        return new Report(saved.Count, absentNames.Count, failedNames.Count);
    }

    private async Task DownloadAsync(List<string> missing, Dictionary<string, DateTime> notFound,
        List<string> saved, List<string> absentNames, List<string> failedNames, Action<string>? progress, CancellationToken cancel)
    {
        lock (_gate)
        {
            _done = 0;
            _toDo = missing.Count;
        }
        Directory.CreateDirectory(Folder);
        void Say(string message)
        {
            lock (_gate) _lastMessage = message;
            progress?.Invoke(message);
        }
        Say(missing.Count == 0 ? "Every card already has an image." : $"Downloading {missing.Count} card images to {Folder}");

        foreach (var name in missing)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var imageUrl = await FindNewestImageAsync(name, cancel);
                if (imageUrl is null)
                {
                    notFound[name] = DateTime.UtcNow;
                    WriteNotFound(notFound);
                    absentNames.Add(name);
                    Say($"{name}: no image on Scryfall yet; showing a placeholder");
                }
                else
                {
                    var bytes = await SendWithRetryAsync(imageUrl, cancel) is { } r ? await r.Content.ReadAsByteArrayAsync(cancel) : null;
                    if (bytes is null || bytes.Length == 0) throw new HttpRequestException("empty image");
                    // Write then rename, so a cut-off download never leaves a half image the page would show.
                    var path = PathFor(name);
                    await File.WriteAllBytesAsync(path + ".part", bytes, cancel);
                    File.Move(path + ".part", path, overwrite: true);
                    notFound.Remove(name);
                    saved.Add(name);
                    Say($"{name}: saved");
                }
            }
            catch (TooManyRequestsException ex)
            {
                // Every further request would draw another 429 and risk a ban, so the run stops here; the cards
                // not reached stay missing and are asked for on the next run.
                lock (_gate) _pausedUntilUtc = DateTime.UtcNow + PauseAfterLimit;
                failedNames.Add(name);
                var left = missing.Count - saved.Count - absentNames.Count - failedNames.Count;
                var cards = left == 1 ? "1 card" : $"{left} cards";
                Say($"Scryfall asked to slow down ({ex.Message}). Stopped with {cards} left; the next run, in {PauseAfterLimit.TotalMinutes:0} minutes at the earliest, continues.");
                _logger.LogWarning("Scryfall answered 429 twice; card image downloads paused for {Minutes} minutes with {Cards} left",
                    PauseAfterLimit.TotalMinutes, cards);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                failedNames.Add(name);
                Say($"{name}: failed ({ex.Message}); it will be tried again next time");
                _logger.LogWarning("Card image for {Name} failed: {Message}", name, ex.Message);
            }
            lock (_gate) _done++;
        }
        if (notFound.Count > 0) WriteNotFound(notFound);
        Say($"Done: {saved.Count} saved, {absentNames.Count} with no image yet, {failedNames.Count} failed.");
    }

    /// <summary>
    /// Scryfall search for the exact name, one result per distinct artwork, newest release first: the first
    /// result is the card's most recent art. A double-faced card's image is its front face.
    /// </summary>
    internal async Task<string?> FindNewestImageAsync(string name, CancellationToken cancel)
    {
        var q = Uri.EscapeDataString($"!\"{name}\"");
        var url = $"{_api}/cards/search?q={q}&unique=art&order=released&dir=desc";
        using var response = await SendWithRetryAsync(url, cancel, SearchGap);
        if (response is null) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var card in data.EnumerateArray())
        {
            if (Normal(card) is { } u) return u;
            if (card.TryGetProperty("card_faces", out var faces) && faces.ValueKind == JsonValueKind.Array)
                foreach (var face in faces.EnumerateArray())
                    if (Normal(face) is { } f) return f;
        }
        return null;

        static string? Normal(JsonElement el) =>
            el.TryGetProperty("image_uris", out var uris) && uris.TryGetProperty("normal", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
    }

    /// <summary>
    /// One request at Scryfall's pace. 404 is "no such card" (null). 429 waits as long as Retry-After says (or
    /// <see cref="TooManyRequestsWait"/>) and tries once more; a second 429 throws
    /// <see cref="TooManyRequestsException"/>, which stops the run. Any other failure throws so the card is retried
    /// on the next run instead of being marked as having no image.
    /// </summary>
    private async Task<HttpResponseMessage?> SendWithRetryAsync(string url, CancellationToken cancel, TimeSpan? gap = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            await Task.Delay(gap ?? RequestGap, cancel);
            var response = await _http.GetAsync(url, cancel);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                return null;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = RetryAfter(response) ?? TooManyRequestsWait;
                response.Dispose();
                if (attempt > 0) throw new TooManyRequestsException($"HTTP 429 from {new Uri(url).Host}");
                await Task.Delay(wait, cancel);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"HTTP {code} from {new Uri(url).Host}");
            }
            return response;
        }
    }

    // Capped so a bad header cannot stall a run for hours; a longer wait than this ends in the run pausing anyway.
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        var wait = ra?.Delta ?? (ra?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        return wait is { } w ? TimeSpan.FromTicks(Math.Clamp(w.Ticks, 0, TimeSpan.FromMinutes(2).Ticks)) : null;
    }
}

/// <summary>Scryfall said "too many requests" again after the wait: the run must stop, not move on to the next card.</summary>
public sealed class TooManyRequestsException(string message) : HttpRequestException(message);
