using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MtgaDash.Accounts;
using MtgaDash.Parsing;

namespace MtgaDash.Data;

/// <summary>
/// Applies parser events. Every statement is an upsert on a natural key, so feeding the same events twice
/// leaves the database as it was: re-ingesting a file can never create duplicates.
/// </summary>
public static class Writer
{
    public sealed record Counts(int Logins, int Snapshots, int DeckLists, int Ranks, int Warnings, int NewPlayers)
    {
        public bool Any => Logins + Snapshots + DeckLists + Ranks > 0;
    }

    public static Counts Apply(SqliteConnection c, SqliteTransaction tx, IEnumerable<LogEvent> events, string sourceFile, Action<ParseWarning>? onWarning = null)
    {
        int logins = 0, snapshots = 0, lists = 0, ranks = 0, warnings = 0, newPlayers = 0;
        foreach (var e in events)
        {
            switch (e)
            {
                case LoggedIn l:
                    newPlayers += TouchPlayer(c, tx, l.DisplayName, l.AtUtc);
                    logins++;
                    break;
                case InventorySnapshot s:
                    newPlayers += TouchPlayer(c, tx, s.DisplayName, s.CapturedAtUtc);
                    UpsertSnapshot(c, tx, s, sourceFile);
                    snapshots++;
                    break;
                case InventoryDelta delta:
                    // Only onto a snapshot this session already has, and only from a later line than the one that
                    // last set it: re-reading the same log, or its archived copy, can never apply a change twice.
                    snapshots += c.Execute("""
                        UPDATE Snapshots SET
                            Gems = CASE WHEN Gems IS NULL THEN NULL ELSE Gems + @Gems END,
                            Gold = CASE WHEN Gold IS NULL THEN NULL ELSE Gold + @Gold END,
                            WcCommon = CASE WHEN WcCommon IS NULL THEN NULL ELSE WcCommon + @WcCommon END,
                            WcUncommon = CASE WHEN WcUncommon IS NULL THEN NULL ELSE WcUncommon + @WcUncommon END,
                            WcRare = CASE WHEN WcRare IS NULL THEN NULL ELSE WcRare + @WcRare END,
                            WcMythic = CASE WHEN WcMythic IS NULL THEN NULL ELSE WcMythic + @WcMythic END,
                            SourceLine = @Line, SourceFile = @SourceFile
                        WHERE DisplayName = @DisplayName AND CapturedAtUtc = @At AND SourceLine < @Line
                        """, new
                    {
                        delta.Gems, delta.Gold, delta.WcCommon, delta.WcUncommon, delta.WcRare, delta.WcMythic,
                        delta.Line, SourceFile = sourceFile, delta.DisplayName, At = Time.Format(delta.CapturedAtUtc),
                    }, tx);
                    break;
                case DeckSummaries d:
                    newPlayers += TouchPlayer(c, tx, d.DisplayName, d.SeenAtUtc);
                    foreach (var deck in d.Decks) UpsertDeck(c, tx, d.DisplayName, deck, d.SeenAtUtc, null);
                    break;
                case DeckList dl:
                    newPlayers += TouchPlayer(c, tx, dl.DisplayName, dl.SeenAtUtc);
                    var cards = JsonSerializer.Serialize(new
                    {
                        MainDeck = dl.MainDeck,
                        Sideboard = dl.Sideboard,
                        CommandZone = dl.CommandZone,
                        Companions = dl.Companions,
                    });
                    UpsertDeck(c, tx, dl.DisplayName, dl.Summary, dl.SeenAtUtc, cards);
                    c.Execute("""
                        INSERT INTO DeckLists(DisplayName, DeckId, Version, CardsJson, SeenAtUtc)
                        VALUES (@DisplayName, @DeckId, @Version, @CardsJson, @SeenAtUtc)
                        ON CONFLICT(DisplayName, DeckId, Version) DO UPDATE SET CardsJson = excluded.CardsJson
                        """, new
                    {
                        dl.DisplayName,
                        dl.Summary.DeckId,
                        Version = dl.Summary.LastUpdated ?? Time.Format(dl.SeenAtUtc),
                        CardsJson = cards,
                        SeenAtUtc = Time.Format(dl.SeenAtUtc),
                    }, tx);
                    lists++;
                    break;
                case RankInfo r:
                    newPlayers += TouchPlayer(c, tx, r.DisplayName, r.CapturedAtUtc);
                    c.Execute("""
                        INSERT INTO Ranks(DisplayName, CapturedAtUtc, Season, ConstructedClass, ConstructedLevel, LimitedClass, LimitedLevel, SourceLine)
                        VALUES (@DisplayName, @At, @Season, @ConstructedClass, @ConstructedLevel, @LimitedClass, @LimitedLevel, @Line)
                        ON CONFLICT(DisplayName, CapturedAtUtc) DO UPDATE SET
                            Season = excluded.Season, ConstructedClass = excluded.ConstructedClass,
                            ConstructedLevel = excluded.ConstructedLevel, LimitedClass = excluded.LimitedClass,
                            LimitedLevel = excluded.LimitedLevel, SourceLine = excluded.SourceLine
                        WHERE excluded.SourceLine >= Ranks.SourceLine
                        """, new { r.DisplayName, At = Time.Format(r.CapturedAtUtc), r.Season, r.ConstructedClass, r.ConstructedLevel, r.LimitedClass, r.LimitedLevel, r.Line }, tx);
                    ranks++;
                    break;
                case ParseWarning w:
                    warnings++;
                    onWarning?.Invoke(w);
                    break;
            }
        }
        return new Counts(logins, snapshots, lists, ranks, warnings, newPlayers);
    }

    /// <returns>1 if the display name is new.</returns>
    private static int TouchPlayer(SqliteConnection c, SqliteTransaction tx, string name, DateTime? at)
    {
        var existed = c.ExecuteScalar<long>("SELECT COUNT(*) FROM Players WHERE DisplayName = @name", new { name }, tx) > 0;
        c.Execute("""
            INSERT INTO Players(DisplayName, NamePart, FirstSeenUtc, LastLoginUtc) VALUES (@name, @part, @at, @at)
            ON CONFLICT(DisplayName) DO UPDATE SET
                FirstSeenUtc = CASE WHEN FirstSeenUtc IS NULL OR (excluded.FirstSeenUtc IS NOT NULL AND excluded.FirstSeenUtc < FirstSeenUtc)
                                    THEN excluded.FirstSeenUtc ELSE FirstSeenUtc END,
                LastLoginUtc = CASE WHEN LastLoginUtc IS NULL OR (excluded.LastLoginUtc IS NOT NULL AND excluded.LastLoginUtc > LastLoginUtc)
                                    THEN excluded.LastLoginUtc ELSE LastLoginUtc END
            """, new { name, part = AccountMatcher.NamePartOf(name), at = Time.Format(at) }, tx);
        return existed ? 0 : 1;
    }

    private static void UpsertSnapshot(SqliteConnection c, SqliteTransaction tx, InventorySnapshot s, string sourceFile)
    {
        // A later InventoryInfo in the same session updates that session's snapshot; fields it leaves out
        // keep what StartHook reported.
        var id = c.ExecuteScalar<long?>("""
            INSERT INTO Snapshots(DisplayName, CapturedAtUtc, Gems, Gold, WcCommon, WcUncommon, WcRare, WcMythic,
                                  VaultProgress, WcTrackPosition, CustomTokensJson, SourceFile, SourceLine)
            VALUES (@DisplayName, @At, @Gems, @Gold, @WcCommon, @WcUncommon, @WcRare, @WcMythic,
                    @VaultProgress, @WcTrackPosition, @CustomTokensJson, @SourceFile, @Line)
            ON CONFLICT(DisplayName, CapturedAtUtc) DO UPDATE SET
                Gems = COALESCE(excluded.Gems, Gems), Gold = COALESCE(excluded.Gold, Gold),
                WcCommon = COALESCE(excluded.WcCommon, WcCommon), WcUncommon = COALESCE(excluded.WcUncommon, WcUncommon),
                WcRare = COALESCE(excluded.WcRare, WcRare), WcMythic = COALESCE(excluded.WcMythic, WcMythic),
                VaultProgress = COALESCE(excluded.VaultProgress, VaultProgress),
                WcTrackPosition = COALESCE(excluded.WcTrackPosition, WcTrackPosition),
                CustomTokensJson = COALESCE(excluded.CustomTokensJson, CustomTokensJson),
                SourceFile = excluded.SourceFile, SourceLine = excluded.SourceLine
            WHERE excluded.SourceLine >= Snapshots.SourceLine
            RETURNING Id
            """, new
        {
            s.DisplayName,
            At = Time.Format(s.CapturedAtUtc),
            s.Gems, s.Gold, s.WcCommon, s.WcUncommon, s.WcRare, s.WcMythic, s.VaultProgress, s.WcTrackPosition,
            s.CustomTokensJson,
            SourceFile = sourceFile,
            s.Line,
        }, tx);

        // No id: an earlier line of the session than the one already stored, so nothing changed.
        if (id is null || s.Boosters is null) return;
        c.Execute("DELETE FROM Boosters WHERE SnapshotId = @id", new { id }, tx);
        foreach (var b in s.Boosters)
            c.Execute("INSERT INTO Boosters(SnapshotId, SetCode, CollationId, Count) VALUES (@id, @SetCode, @CollationId, @Count)",
                new { id, b.SetCode, b.CollationId, b.Count }, tx);
    }

    private static void UpsertDeck(SqliteConnection c, SqliteTransaction tx, string displayName, DeckSummary d, DateTime seenAt, string? cardsJson)
    {
        // The SeenAtUtc guard makes order irrelevant: re-parsing an old archive cannot roll a deck back.
        c.Execute("""
            INSERT INTO Decks(DeckId, DisplayName, Name, Format, IsPrecon, LastPlayed, LastUpdated, CardsJson, SeenAtUtc)
            VALUES (@DeckId, @DisplayName, @Name, @Format, @IsPrecon, @LastPlayed, @LastUpdated, @CardsJson, @SeenAtUtc)
            ON CONFLICT(DisplayName, DeckId) DO UPDATE SET
                Name = excluded.Name, Format = COALESCE(excluded.Format, Format), IsPrecon = excluded.IsPrecon,
                LastPlayed = COALESCE(excluded.LastPlayed, LastPlayed), LastUpdated = COALESCE(excluded.LastUpdated, LastUpdated),
                CardsJson = COALESCE(excluded.CardsJson, CardsJson), SeenAtUtc = excluded.SeenAtUtc
            WHERE excluded.SeenAtUtc >= Decks.SeenAtUtc OR (excluded.CardsJson IS NOT NULL AND Decks.CardsJson IS NULL)
            """, new
        {
            d.DeckId,
            DisplayName = displayName,
            d.Name,
            d.Format,
            IsPrecon = d.IsPrecon ? 1 : 0,
            d.LastPlayed,
            d.LastUpdated,
            CardsJson = cardsJson,
            SeenAtUtc = Time.Format(seenAt),
        }, tx);
    }

    public static void HideDeckName(SqliteConnection c, string name) =>
        c.Execute("INSERT OR IGNORE INTO IgnoredDeckNames(Name, AddedUtc) VALUES (@name, @at)", new { name = name.Trim(), at = Time.Format(DateTime.UtcNow) });

    public static bool ShowDeckName(SqliteConnection c, string name) =>
        c.Execute("DELETE FROM IgnoredDeckNames WHERE Name = @name", new { name = name.Trim() }) > 0;

    /// <summary>Upserts by email and never deletes, so a trimmed CSV cannot lose an account's history.</summary>
    public static int ImportAccounts(SqliteConnection c, IEnumerable<AccountRow> rows)
    {
        using var tx = c.BeginTransaction();
        var n = 0;
        foreach (var r in rows)
        {
            n += c.Execute("""
                INSERT INTO Accounts(Email, Alias, Status, Notes, MiscNotes, NoteSource, MatchState)
                VALUES (@Email, @Alias, @Status, @Notes, @MiscNotes, @NoteSource, 'Unmatched')
                ON CONFLICT(Email) DO UPDATE SET
                    Alias = excluded.Alias, Status = excluded.Status, Notes = excluded.Notes,
                    MiscNotes = excluded.MiscNotes, NoteSource = excluded.NoteSource
                """, new { r.Email, Alias = AccountMatcher.AliasOf(r.Email), r.Status, r.Notes, r.MiscNotes, r.NoteSource }, tx);
        }
        tx.Commit();
        return n;
    }

    /// <summary>Recomputes every match from scratch; see <see cref="AccountMatcher"/>.</summary>
    public static MatchResult RunMatching(SqliteConnection c, ExceptionStore? store = null)
    {
        var emails = c.Query<string>("SELECT Email FROM Accounts").ToList();
        var players = c.Query<string>("SELECT DisplayName FROM Players ORDER BY LastLoginUtc DESC").ToList();
        var (map, excluded) = store?.Current() ?? (AccountExceptions.DisplayNameToEmail, AccountExceptions.ExcludedEmails);
        var result = AccountMatcher.Match(emails, players, map, excluded);

        using var tx = c.BeginTransaction();
        c.Execute("UPDATE Players SET Email = NULL", transaction: tx);
        foreach (var p in result.Players.Where(p => p.Email is not null))
            c.Execute("UPDATE Players SET Email = @Email WHERE DisplayName = @DisplayName", p, tx);
        foreach (var a in result.Accounts)
            c.Execute("UPDATE Accounts SET MatchState = @State, DisplayName = @DisplayName WHERE Email = @Email", a, tx);
        tx.Commit();
        return result;
    }
}
