using System.Globalization;
using System.Text.Json;

namespace MtgaDash.Parsing;

/// <summary>
/// Everything the parser needs to resume mid-file. Ingestion saves it as JSON next to the byte offset,
/// so the next poll continues exactly where this one stopped.
/// </summary>
public sealed class LogParserState
{
    public long LineNumber { get; set; }
    public string? DisplayName { get; set; }
    public string? ClientVersion { get; set; }
    public DateTime? BlockStartedUtc { get; set; }

    /// <summary>StartHook's <c>ServerTimeInternal</c> for the current login; the key of its snapshot.</summary>
    public DateTime? SessionTimeUtc { get; set; }

    /// <summary>A <c>&lt;== Method(id)</c> header was seen and its body is expected on the next line.</summary>
    public string? PendingResponse { get; set; }
}

/// <summary>
/// Turns MTGA <c>Player.log</c> lines into typed events. Pure: no I/O, no clock, so whole logs play out in a
/// unit test. Feed it complete lines in order; malformed JSON becomes a <see cref="ParseWarning"/>, never an
/// exception.
/// </summary>
public sealed class LogParser
{
    private const string StartupPrefix = "Startup Timestamp:";
    private const string LoginMarker = "[Accounts - Login] Logged in successfully. Display Name:";
    private const string RequestMarker = "==> ";
    private const string ResponseMarker = "<== ";
    private const string PreconPrefix = "?=?Loc/Decks/Precon/";

    private static readonly string[] ResponsesOfInterest = ["StartHook", "DeckGetDeckSummariesV3", "RankGetCombinedRankInfo"];

    private readonly TimeZoneInfo _logTimeZone;

    public LogParser(LogParserState? state = null, TimeZoneInfo? logTimeZone = null)
    {
        State = state ?? new LogParserState();
        // Startup Timestamp is written in the PC's local time; tests pin a zone so they pass anywhere.
        _logTimeZone = logTimeZone ?? TimeZoneInfo.Local;
    }

    public LogParserState State { get; }

    public IEnumerable<LogEvent> ParseAll(IEnumerable<string> lines) => lines.SelectMany(ParseLine);

    public IReadOnlyList<LogEvent> ParseLine(string rawLine)
    {
        State.LineNumber++;
        var events = ParseLineCore(rawLine);
        foreach (var e in events) e.Line = State.LineNumber;
        return events;
    }

    private List<LogEvent> ParseLineCore(string rawLine)
    {
        var line = rawLine.TrimEnd('\r', '\n');
        var events = new List<LogEvent>();

        if (State.PendingResponse is { } pending)
        {
            State.PendingResponse = null;
            var body = line.TrimStart();
            if (body.StartsWith('{') || body.StartsWith('"'))
            {
                HandleResponse(pending, body, events);
                return events;
            }
            // No body after the header: fall through and treat the line on its own.
        }

        var trimmed = line.TrimStart();

        if (trimmed.StartsWith(StartupPrefix, StringComparison.Ordinal))
        {
            State.BlockStartedUtc = ParseStartupTimestamp(trimmed[StartupPrefix.Length..].Trim());
            State.DisplayName = null;
            State.SessionTimeUtc = null;
            events.Add(new SessionStarted(State.BlockStartedUtc));
            return events;
        }

        if (trimmed.StartsWith("Version: ", StringComparison.Ordinal) && trimmed.Length > 9 && char.IsDigit(trimmed[9]))
        {
            var rest = trimmed[9..];
            var slash = rest.IndexOf(' ');
            State.ClientVersion = (slash > 0 ? rest[..slash] : rest).Trim();
            return events;
        }

        var login = trimmed.IndexOf(LoginMarker, StringComparison.Ordinal);
        if (login >= 0)
        {
            var name = trimmed[(login + LoginMarker.Length)..].Trim();
            if (name.Length > 0)
            {
                State.DisplayName = name;
                State.SessionTimeUtc = null;
                events.Add(new LoggedIn(name, State.BlockStartedUtc, State.ClientVersion));
            }
            return events;
        }

        var response = ResponseHeaderIndex(trimmed);
        if (response >= 0)
        {
            var header = trimmed[(response + ResponseMarker.Length)..];
            var paren = header.IndexOf('(');
            var method = (paren > 0 ? header[..paren] : header.Split(' ', 2)[0]).Trim();
            var close = header.IndexOf(')');
            var sameLineBody = close >= 0 ? header[(close + 1)..].Trim() : "";
            if (sameLineBody.StartsWith('{')) HandleResponse(method, sameLineBody, events);
            else State.PendingResponse = method;
            return events;
        }

        var request = trimmed.IndexOf(RequestMarker, StringComparison.Ordinal);
        if (request >= 0)
        {
            var rest = trimmed[(request + RequestMarker.Length)..];
            var space = rest.IndexOf(' ');
            if (space > 0 && rest[..space] == "DeckUpsertDeckV3")
                HandleDeckUpsert(rest[(space + 1)..].Trim(), events);
        }

        return events;
    }

    /// <summary>A response header starts the line, optionally after a <c>[Logger]</c> tag.</summary>
    private static int ResponseHeaderIndex(string trimmed)
    {
        if (trimmed.StartsWith(ResponseMarker, StringComparison.Ordinal)) return 0;
        if (!trimmed.StartsWith('[')) return -1;
        var end = trimmed.IndexOf(']');
        return end > 0 && trimmed.AsSpan(end + 1).StartsWith(ResponseMarker) ? end + 1 : -1;
    }

    private void HandleResponse(string method, string body, List<LogEvent> events)
    {
        var interesting = ResponsesOfInterest.Contains(method)
            || body.Contains("InventoryInfo", StringComparison.OrdinalIgnoreCase);
        if (!interesting) return;

        // Responses before any login in a block belong to no account.
        if (State.DisplayName is not { } name) return;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            events.Add(new ParseWarning(State.LineNumber, $"{method}: malformed JSON ({ex.Message})"));
            return;
        }

        using (doc)
        {
            using var unwrapped = Json.Unwrap(doc.RootElement);
            var root = unwrapped.Element;
            if (root.ValueKind != JsonValueKind.Object)
            {
                events.Add(new ParseWarning(State.LineNumber, $"{method}: body is not an object"));
                return;
            }

            if (method == "StartHook" && Json.Get(root, "ServerTimeInternal") is { ValueKind: JsonValueKind.String } st
                && TryParseUtc(st.GetString(), out var serverTime))
            {
                State.SessionTimeUtc = serverTime;
            }

            var at = State.SessionTimeUtc ?? State.BlockStartedUtc;
            if (at is not { } when)
            {
                events.Add(new ParseWarning(State.LineNumber, $"{method}: no session time yet, skipped"));
                return;
            }

            if (Json.Find(root, "InventoryInfo", maxDepth: 3) is { ValueKind: JsonValueKind.Object } inv)
                events.Add(ReadInventory(name, when, inv));

            var summaries = method switch
            {
                "StartHook" => Json.Get(root, "DeckSummaries"),
                "DeckGetDeckSummariesV3" => Json.Get(root, "Summaries"),
                _ => null,
            };
            if (summaries is { ValueKind: JsonValueKind.Array } arr)
            {
                var decks = arr.EnumerateArray().Select(ReadDeckSummary).OfType<DeckSummary>().ToList();
                events.Add(new DeckSummaries(name, when, decks));
            }

            if (method == "RankGetCombinedRankInfo")
            {
                events.Add(new RankInfo(
                    name,
                    when,
                    Json.Int(root, "constructedSeasonOrdinal"),
                    Json.String(root, "constructedClass"),
                    Json.Int(root, "constructedLevel"),
                    Json.String(root, "limitedClass"),
                    Json.Int(root, "limitedLevel")));
            }
        }
    }

    private void HandleDeckUpsert(string payload, List<LogEvent> events)
    {
        if (State.DisplayName is not { } name) return;
        if ((State.SessionTimeUtc ?? State.BlockStartedUtc) is not { } when) return;

        try
        {
            using var outer = JsonDocument.Parse(payload);
            var requestElement = Json.Get(outer.RootElement, "request") ?? outer.RootElement;
            // "request" is a JSON string holding JSON, sometimes encoded twice.
            using var request = Json.Unwrap(requestElement);
            var root = request.Element;
            var summaryEl = Json.Get(root, "Summary");
            var deckEl = Json.Get(root, "Deck");
            if (summaryEl is not { ValueKind: JsonValueKind.Object } s || deckEl is not { ValueKind: JsonValueKind.Object } d)
            {
                events.Add(new ParseWarning(State.LineNumber, "DeckUpsertDeckV3: no Summary or Deck"));
                return;
            }
            if (ReadDeckSummary(s) is not { } summary)
            {
                events.Add(new ParseWarning(State.LineNumber, "DeckUpsertDeckV3: summary has no deck id"));
                return;
            }
            events.Add(new DeckList(
                name,
                when,
                summary,
                ReadCards(Json.Get(d, "MainDeck")),
                ReadCards(Json.Get(d, "Sideboard")),
                ReadCards(Json.Get(d, "CommandZone")),
                ReadCards(Json.Get(d, "Companions"))));
        }
        catch (JsonException ex)
        {
            events.Add(new ParseWarning(State.LineNumber, $"DeckUpsertDeckV3: malformed JSON ({ex.Message})"));
        }
    }

    private static InventorySnapshot ReadInventory(string name, DateTime at, JsonElement inv)
    {
        string? tokens = Json.Get(inv, "CustomTokens") is { ValueKind: JsonValueKind.Object } t ? t.GetRawText() : null;
        List<BoosterCount>? boosters = null;
        if (Json.Get(inv, "Boosters") is { ValueKind: JsonValueKind.Array } b)
        {
            boosters = b.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => new BoosterCount(Json.String(x, "SetCode") ?? "?", Json.Int(x, "CollationId") ?? 0, Json.Int(x, "Count") ?? 0))
                .ToList();
        }
        return new InventorySnapshot(
            name,
            at,
            Json.Int(inv, "Gems"),
            Json.Int(inv, "Gold"),
            Json.Int(inv, "WildCardCommons"),
            Json.Int(inv, "WildCardUnCommons"),
            Json.Int(inv, "WildCardRares"),
            Json.Int(inv, "WildCardMythics"),
            Json.Double(inv, "TotalVaultProgress"),
            Json.Int(inv, "WcTrackPosition"),
            tokens,
            boosters);
    }

    private static DeckSummary? ReadDeckSummary(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var id = Json.String(el, "DeckIdInternal") ?? Json.String(el, "DeckId");
        if (string.IsNullOrEmpty(id)) return null;
        var name = Json.String(el, "Name") ?? "";

        string? format = null, lastPlayed = null, lastUpdated = null;
        if (Json.Get(el, "Attributes") is { ValueKind: JsonValueKind.Array } attrs)
        {
            foreach (var a in attrs.EnumerateArray())
            {
                var key = Json.String(a, "name");
                var value = Json.UnquoteAttribute(Json.String(a, "value"));
                switch (key)
                {
                    case "Format": format = value; break;
                    case "LastPlayed": lastPlayed = value; break;
                    case "LastUpdated": lastUpdated = value; break;
                }
            }
        }
        return new DeckSummary(id, name, format, name.StartsWith(PreconPrefix, StringComparison.Ordinal), lastPlayed, lastUpdated);
    }

    /// <summary>Cards come as <c>[{cardId, quantity}]</c>; older clients wrote a flat <c>[id, qty, id, qty]</c>.</summary>
    private static List<DeckCard> ReadCards(JsonElement? el)
    {
        var cards = new List<DeckCard>();
        if (el is not { ValueKind: JsonValueKind.Array } arr) return cards;
        var items = arr.EnumerateArray().ToList();
        if (items.Count > 0 && items[0].ValueKind == JsonValueKind.Number)
        {
            for (var i = 0; i + 1 < items.Count; i += 2)
                if (items[i].TryGetInt32(out var id) && items[i + 1].TryGetInt32(out var qty)) cards.Add(new DeckCard(id, qty));
            return cards;
        }
        foreach (var c in items)
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            if (Json.Int(c, "cardId") is { } id) cards.Add(new DeckCard(id, Json.Int(c, "quantity") ?? 1));
        }
        return cards;
    }

    private DateTime? ParseStartupTimestamp(string text)
    {
        if (!DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var local)) return null;
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _logTimeZone);
        }
        catch (ArgumentException)
        {
            // A local time skipped by a daylight-saving change; an hour off is better than nothing.
            return DateTime.SpecifyKind(local - _logTimeZone.BaseUtcOffset, DateTimeKind.Utc);
        }
    }

    internal static bool TryParseUtc(string? text, out DateTime utc)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }
}
