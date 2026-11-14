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

public sealed record LoggedIn(string DisplayName, DateTime? AtUtc, string? ClientVersion) : LogEvent;

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

/// <summary>A line the parser could not use. Ingestion logs it and carries on.</summary>
public sealed record ParseWarning(long LineNumber, string Message) : LogEvent;
