using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

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
public sealed partial class LogParser
{
    /// <summary>An Arena handle: a name, '#', and digits.</summary>
    [GeneratedRegex(@"(?<name>[^\s""#:,{}\[\]]{1,40}#\d{3,6})(?![\d])")]
    private static partial Regex Handle();

    /// <summary>
    /// Login lines other than the one MTGA writes for a manual login, e.g. after an automatic login with saved
    /// credentials: "Display Name: X#1" or "logged in" next to a handle anywhere on a plain line.
    /// </summary>
    [GeneratedRegex(@"Display Name:\s*(?<name>[^\s""#:,{}]{1,40}#\d{3,6})|logged in[^{}]{0,80}?\b(?<name>[^\s""#:,{}\[\]]{1,40}#\d{3,6})", RegexOptions.IgnoreCase)]
    private static partial Regex OtherLogin();

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
                Seen("response", pending, body, events, () => HandleResponse(pending, body, events));
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
            if (name.Length > 0) SetLogin(name, "login line", events);
            return events;
        }

        // Plain lines only: a JSON body or a request/response line can name other players.
        if (!trimmed.StartsWith('{') && !trimmed.Contains("==>", StringComparison.Ordinal) && !trimmed.Contains("<==", StringComparison.Ordinal)
            && OtherLogin().Match(trimmed) is { Success: true } other)
        {
            var name = other.Groups["name"].Value;
            if (!string.Equals(name, State.DisplayName, StringComparison.Ordinal)) SetLogin(name, "other login line", events);
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
            if (sameLineBody.StartsWith('{')) Seen("response", method, sameLineBody, events, () => HandleResponse(method, sameLineBody, events));
            else State.PendingResponse = method;
            return events;
        }

        var request = trimmed.IndexOf(RequestMarker, StringComparison.Ordinal);
        if (request >= 0)
        {
            var rest = trimmed[(request + RequestMarker.Length)..];
            var space = rest.IndexOf(' ');
            var method = (space > 0 ? rest[..space] : rest).Trim();
            var payload = space > 0 ? rest[(space + 1)..].Trim() : "";
            if (method.Length > 0)
                Seen("request", method, payload, events, () =>
                {
                    if (method == "DeckUpsertDeckV3") HandleDeckUpsert(payload, events);
                });
        }

        return events;
    }

    /// <summary>
    /// Words that suggest a message carries inventory, so the Activity tab can point at a message the parser
    /// does not read yet (for example per-match gold changes) instead of leaving it invisible.
    /// </summary>
    internal static readonly string[] InventoryHints =
        ["InventoryInfo", "InventoryUpdate", "GoldDelta", "GemsDelta", "WildCard", "Booster", "VaultProgress"];

    /// <summary>Runs a handler and reports the message to the Activity tab, read or not.</summary>
    private void Seen(string direction, string method, string body, List<LogEvent> events, Action handle)
    {
        var before = events.Count;
        handle();
        var handled = events.Skip(before).Any(e => e is not ParseWarning);
        var hints = InventoryHints.Where(h => body.Contains(h, StringComparison.OrdinalIgnoreCase)).ToArray();
        events.Add(new MessageSeen(direction, method, State.DisplayName, handled, hints, body.Length));
    }

    /// <summary>A response header starts the line, optionally after a <c>[Logger]</c> tag.</summary>
    private static int ResponseHeaderIndex(string trimmed)
    {
        if (trimmed.StartsWith(ResponseMarker, StringComparison.Ordinal)) return 0;
        if (!trimmed.StartsWith('[')) return -1;
        var end = trimmed.IndexOf(']');
        return end > 0 && trimmed.AsSpan(end + 1).StartsWith(ResponseMarker) ? end + 1 : -1;
    }

    private void SetLogin(string name, string source, List<LogEvent> events)
    {
        State.DisplayName = name;
        State.SessionTimeUtc = null;
        events.Add(new LoggedIn(name, State.BlockStartedUtc, State.ClientVersion) { Source = source });
    }

    /// <summary>
    /// With no login seen yet, the server's own login or session response may still name the player. Only
    /// then, and only from a response whose name says login, session or StartHook: match messages also carry
    /// handles, the opponent's among them.
    /// </summary>
    private void TryLoginFromResponse(string method, string body, List<LogEvent> events)
    {
        if (State.DisplayName is not null) return;
        if (!(method.Contains("Auth", StringComparison.OrdinalIgnoreCase) || method.Contains("Login", StringComparison.OrdinalIgnoreCase)
              || method.Contains("Session", StringComparison.OrdinalIgnoreCase) || method == "StartHook")) return;
        if (!body.Contains("screenName", StringComparison.OrdinalIgnoreCase) && !body.Contains("displayName", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            using var unwrapped = Json.Unwrap(doc.RootElement);
            foreach (var key in new[] { "screenName", "displayName" })
            {
                if (Json.Find(unwrapped.Element, key, maxDepth: 3) is { ValueKind: JsonValueKind.String } v
                    && v.GetString() is { } candidate && Handle().Match(candidate) is { Success: true } m && m.Value == candidate.Trim())
                {
                    SetLogin(candidate.Trim(), $"{method} response", events);
                    return;
                }
            }
        }
        catch (JsonException)
        {
            // The main handler reports malformed JSON; nothing to add here.
        }
    }

    private void HandleResponse(string method, string body, List<LogEvent> events)
    {
        TryLoginFromResponse(method, body, events);
        var interesting = ResponsesOfInterest.Contains(method)
            || body.Contains("InventoryInfo", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Delta", StringComparison.OrdinalIgnoreCase);
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
            // A full inventory in the same message already includes any change it reports.
            else if (ReadDelta(name, when, method, root) is { IsEmpty: false } delta)
                events.Add(delta);

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

    /// <summary>Adds up every currency and wildcard "…Delta" number anywhere in the message.</summary>
    private static InventoryDelta ReadDelta(string name, DateTime at, string method, JsonElement root)
    {
        int gems = 0, gold = 0, c = 0, u = 0, r = 0, m = 0;
        void Walk(JsonElement el, int depth)
        {
            if (depth > 8) return;
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray()) Walk(item, depth + 1);
                return;
            }
            if (el.ValueKind != JsonValueKind.Object) return;
            foreach (var p in el.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n))
                {
                    switch (p.Name.ToLowerInvariant())
                    {
                        case "gemsdelta": gems += n; break;
                        case "golddelta": gold += n; break;
                        case "wccommondelta": c += n; break;
                        case "wcuncommondelta": u += n; break;
                        case "wcraredelta": r += n; break;
                        case "wcmythicdelta": m += n; break;
                    }
                }
                else Walk(p.Value, depth + 1);
            }
        }
        Walk(root, 0);
        return new InventoryDelta(name, at, method, gems, gold, c, u, r, m);
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
