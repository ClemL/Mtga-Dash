namespace MtgaDash.Parsing;

/// <summary>What the parser recognised on a line. Every event after a login carries that login's display name.</summary>
public abstract record LogEvent
{
    /// <summary>
    /// The line within its file that produced the event. Within one session a later line wins, so a lagging
    /// archive copy ingested after the live log cannot roll an inventory back.
    /// </summary>
    public long Line { get; internal set; }
}

/// <summary>A <c>Startup Timestamp:</c> line: a new client session, possibly within the same file.</summary>
public sealed record SessionStarted(DateTime? StartedAtUtc) : LogEvent;

public sealed record LoggedIn(string DisplayName, DateTime? AtUtc, string? ClientVersion) : LogEvent
{
    /// <summary>Which kind of line identified the login, shown on the Activity tab.</summary>
    public string Source { get; init; } = "login line";
}

public sealed record BoosterCount(string SetCode, int CollationId, int Count);

/// <summary>
/// An inventory as reported by StartHook or any later response carrying <c>InventoryInfo</c>. Fields a
/// response leaves out are null, so a partial update never zeroes what an earlier one reported.
/// </summary>
public sealed record InventorySnapshot(
    string DisplayName,
    DateTime CapturedAtUtc,
    int? Gems,
    int? Gold,
    int? WcCommon,
    int? WcUncommon,
    int? WcRare,
    int? WcMythic,
    double? VaultProgress,
    int? WcTrackPosition,
    string? CustomTokensJson,
    IReadOnlyList<BoosterCount>? Boosters) : LogEvent;

/// <summary>
/// A change reported after a match or reward ("goldDelta": 250), applied to the session's latest inventory.
/// MTGA logs these instead of a full inventory during play; the next full inventory supersedes them.
/// </summary>
public sealed record InventoryDelta(
    string DisplayName, DateTime CapturedAtUtc, string Method,
    int Gems, int Gold, int WcCommon, int WcUncommon, int WcRare, int WcMythic) : LogEvent
{
    public bool IsEmpty => Gems == 0 && Gold == 0 && WcCommon == 0 && WcUncommon == 0 && WcRare == 0 && WcMythic == 0;
}

public sealed record DeckSummary(
    string DeckId,
    string Name,
    string? Format,
    bool IsPrecon,
    string? LastPlayed,
    string? LastUpdated);

public sealed record DeckSummaries(string DisplayName, DateTime SeenAtUtc, IReadOnlyList<DeckSummary> Decks) : LogEvent;

public sealed record DeckCard(int CardId, int Quantity);

public sealed record DeckList(
    string DisplayName,
    DateTime SeenAtUtc,
    DeckSummary Summary,
    IReadOnlyList<DeckCard> MainDeck,
    IReadOnlyList<DeckCard> Sideboard,
    IReadOnlyList<DeckCard> CommandZone,
    IReadOnlyList<DeckCard> Companions) : LogEvent;

public sealed record RankInfo(
    string DisplayName,
    DateTime CapturedAtUtc,
    int? Season,
    string? ConstructedClass,
    int? ConstructedLevel,
    string? LimitedClass,
    int? LimitedLevel) : LogEvent;

/// <summary>
/// Any request or response line, whether or not the parser read anything from it. Nothing is stored; the
/// Activity tab counts these to show what MTGA writes, including messages the parser ignores.
/// </summary>
public sealed record MessageSeen(
    string Direction,
    string Method,
    string? DisplayName,
    bool Handled,
    IReadOnlyList<string> Hints,
    int Bytes) : LogEvent;

/// <summary>A line the parser could not use. Ingestion logs it and carries on.</summary>
public sealed record ParseWarning(long LineNumber, string Message) : LogEvent;
