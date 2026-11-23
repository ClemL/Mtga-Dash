using System.Net;
using System.Text;
using System.Text.Json;

namespace MtgaDash.Cards;

public sealed record CardImageStatus(
    string Folder, int Wanted, int Have, int Missing, int NotFound, bool Running, int Done, int ToDo, string? LastMessage);

/// <summary>
/// A local folder of card images, one JPEG per card name, and the downloader that fills it from Scryfall.
/// Images are kept by name, not by MTGA id, because the owner wants each card's newest art whichever
/// printing their decks hold. A card Scryfall has no image for is remembered for <see cref="RetryNotFoundAfter"/>
/// so it is not asked for on every run; the page shows a placeholder for it meanwhile.
/// </summary>
public sealed class CardImageStore
{
    /// <summary>Scryfall asks for 50–100 ms between API requests; this stays at the slow end.</summary>
    internal static readonly TimeSpan RequestGap = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan RetryNotFoundAfter = TimeSpan.FromDays(7);
    private const string NotFoundFile = "not-found.json";

    private readonly HttpClient _http;
    private readonly string _api;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private int _done, _toDo;
    private string? _lastMessage;
    private Task? _running;

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

    public CardImageStatus Status(IReadOnlyCollection<string> wanted)
    {
        var notFound = ReadNotFound();
        var have = wanted.Count(Has);
        var missing = Missing(wanted).Count;
        lock (_gate)
            return new CardImageStatus(Folder, wanted.Count, have, missing,
                wanted.Count(n => !Has(n) && notFound.ContainsKey(n)), _running is { IsCompleted: false }, _done, _toDo, _lastMessage);
    }

    /// <summary>Starts a download in the background unless one is running. Returns false if one was.</summary>
    public bool StartDownload(IReadOnlyCollection<string> wanted)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) return false;
            _running = Task.Run(() => DownloadMissingAsync(wanted, null, CancellationToken.None));
            return true;
        }
    }

    public sealed record Report(int Downloaded, int NotFound, int Failed);

    /// <summary>Downloads the newest art of every missing card, one at a time, at Scryfall's requested pace.</summary>
    public async Task<Report> DownloadMissingAsync(IEnumerable<string> wanted, Action<string>? progress, CancellationToken cancel)
    {
        var missing = Missing(wanted);
        var notFound = ReadNotFound();
        int downloaded = 0, absent = 0, failed = 0;
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
                    absent++;
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
                    downloaded++;
                    Say($"{name}: saved");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                failed++;
                Say($"{name}: failed ({ex.Message}); it will be tried again next time");
                _logger.LogWarning("Card image for {Name} failed: {Message}", name, ex.Message);
            }
            lock (_gate) _done++;
        }
        if (notFound.Count > 0) WriteNotFound(notFound);
        Say($"Done: {downloaded} saved, {absent} with no image yet, {failed} failed.");
        return new Report(downloaded, absent, failed);
    }

    /// <summary>
    /// Scryfall search for the exact name, one result per distinct artwork, newest release first: the first
    /// result is the card's most recent art. A double-faced card's image is its front face.
    /// </summary>
    internal async Task<string?> FindNewestImageAsync(string name, CancellationToken cancel)
    {
        var q = Uri.EscapeDataString($"!\"{name}\"");
        var url = $"{_api}/cards/search?q={q}&unique=art&order=released&dir=desc";
        using var response = await SendWithRetryAsync(url, cancel);
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
    /// One request at Scryfall's pace. 404 is "no such card" (null); 429 waits and tries once more; any other
    /// failure throws so the card is retried on the next run instead of being marked as having no image.
    /// </summary>
    private async Task<HttpResponseMessage?> SendWithRetryAsync(string url, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            await Task.Delay(RequestGap, cancel);
            var response = await _http.GetAsync(url, cancel);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                return null;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                response.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(2), cancel);
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
}
