using Dapper;
using Microsoft.Data.Sqlite;

namespace MtgaDash.Data;

/// <summary>
/// Opens connections and owns the schema. Each migration runs once, in order, recorded in
/// <c>SchemaVersions</c>; add a new entry at the end rather than editing an old one.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        // WAL lets the dashboard read while ingestion writes; busy_timeout rides out the brief write locks.
        c.Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
        return c;
    }

    public void Migrate()
    {
        using var c = Open();
        c.Execute("CREATE TABLE IF NOT EXISTS SchemaVersions(Version INTEGER PRIMARY KEY, AppliedAtUtc TEXT NOT NULL)");
        var current = c.ExecuteScalar<long?>("SELECT MAX(Version) FROM SchemaVersions") ?? 0;
        for (var i = (int)current; i < Migrations.Length; i++)
        {
            using var tx = c.BeginTransaction();
            c.Execute(Migrations[i], transaction: tx);
            c.Execute("INSERT INTO SchemaVersions(Version, AppliedAtUtc) VALUES (@v, @t)",
                new { v = i + 1, t = Time.Format(DateTime.UtcNow) }, tx);
            tx.Commit();
        }
    }

    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE Accounts(
            Email TEXT PRIMARY KEY COLLATE NOCASE,
            Alias TEXT,
            DisplayName TEXT NULL,
            Status TEXT,
            Notes TEXT,
            MiscNotes TEXT,
            NoteSource TEXT,
            MatchState TEXT NOT NULL DEFAULT 'Unmatched');
        CREATE INDEX IX_Accounts_DisplayName ON Accounts(DisplayName);

        CREATE TABLE Players(
            DisplayName TEXT PRIMARY KEY,
            NamePart TEXT NOT NULL,
            FirstSeenUtc TEXT,
            LastLoginUtc TEXT,
            Email TEXT NULL);

        CREATE TABLE Snapshots(
            Id INTEGER PRIMARY KEY,
            DisplayName TEXT NOT NULL,
            CapturedAtUtc TEXT NOT NULL,
            Gems INT, Gold INT,
            WcCommon INT, WcUncommon INT, WcRare INT, WcMythic INT,
            VaultProgress REAL, WcTrackPosition INT,
            CustomTokensJson TEXT,
            SourceFile TEXT,
            SourceLine INT NOT NULL DEFAULT 0,
            UNIQUE(DisplayName, CapturedAtUtc));

        CREATE TABLE Boosters(
            SnapshotId INT NOT NULL REFERENCES Snapshots(Id) ON DELETE CASCADE,
            SetCode TEXT NOT NULL,
            CollationId INT NOT NULL,
            Count INT NOT NULL);
        CREATE INDEX IX_Boosters_Snapshot ON Boosters(SnapshotId);

        CREATE TABLE Decks(
            DeckId TEXT NOT NULL,
            DisplayName TEXT NOT NULL,
            Name TEXT,
            Format TEXT,
            IsPrecon INT NOT NULL DEFAULT 0,
            LastPlayed TEXT NULL,
            LastUpdated TEXT NULL,
            CardsJson TEXT NULL,
            SeenAtUtc TEXT NOT NULL,
            PRIMARY KEY(DisplayName, DeckId));

        -- Every version of a deck's card list seen, keyed by its LastUpdated, so re-ingesting adds nothing.
        CREATE TABLE DeckLists(
            DisplayName TEXT NOT NULL,
            DeckId TEXT NOT NULL,
            Version TEXT NOT NULL,
            CardsJson TEXT NOT NULL,
            SeenAtUtc TEXT NOT NULL,
            PRIMARY KEY(DisplayName, DeckId, Version));

        CREATE TABLE Ranks(
            DisplayName TEXT NOT NULL,
            CapturedAtUtc TEXT NOT NULL,
            Season INT,
            ConstructedClass TEXT, ConstructedLevel INT,
            LimitedClass TEXT, LimitedLevel INT,
            SourceLine INT NOT NULL DEFAULT 0,
            PRIMARY KEY(DisplayName, CapturedAtUtc));

        CREATE TABLE IngestedFiles(
            Path TEXT PRIMARY KEY,
            FirstBytesHash TEXT NOT NULL,
            LengthProcessed INTEGER NOT NULL,
            LastWriteUtc TEXT NOT NULL,
            ParserStateJson TEXT NOT NULL);

        CREATE VIEW vAccountLatest AS
        SELECT
            a.Email, a.Alias, a.DisplayName, a.Status, a.Notes, a.MiscNotes, a.NoteSource, a.MatchState,
            p.FirstSeenUtc, p.LastLoginUtc,
            s.Id AS SnapshotId, s.CapturedAtUtc,
            s.Gems, s.Gold, s.WcCommon, s.WcUncommon, s.WcRare, s.WcMythic, s.VaultProgress, s.WcTrackPosition,
            (SELECT COALESCE(SUM(b.Count), 0) FROM Boosters b WHERE b.SnapshotId = s.Id) AS BoosterTotal,
            r.Season, r.ConstructedClass, r.ConstructedLevel, r.LimitedClass, r.LimitedLevel,
            (SELECT COUNT(*) FROM Decks d WHERE d.DisplayName = a.DisplayName AND d.IsPrecon = 0) AS CustomDeckCount
        FROM Accounts a
        LEFT JOIN Players p ON p.DisplayName = a.DisplayName
        LEFT JOIN Snapshots s ON s.Id = (
            SELECT s2.Id FROM Snapshots s2 WHERE s2.DisplayName = a.DisplayName ORDER BY s2.CapturedAtUtc DESC LIMIT 1)
        LEFT JOIN Ranks r ON r.DisplayName = a.DisplayName AND r.CapturedAtUtc = (
            SELECT MAX(r2.CapturedAtUtc) FROM Ranks r2 WHERE r2.DisplayName = a.DisplayName);
        """,
        // 2: the Activity tab's feed, kept for 24 hours so it survives a restart.
        """
        CREATE TABLE ActivityEntries(
            Id INTEGER PRIMARY KEY,
            AtUtc TEXT NOT NULL,
            LogTimeUtc TEXT NULL,
            File TEXT NOT NULL,
            Line INTEGER NOT NULL,
            Kind TEXT NOT NULL,
            DisplayName TEXT NULL,
            Summary TEXT NOT NULL,
            UNIQUE(File, Line, Kind, Summary));
        CREATE INDEX IX_ActivityEntries_At ON ActivityEntries(AtUtc);
        """,
        // 3: deck names treated like precons (starter decks every account has), and the view that counts custom decks.
        """
        CREATE TABLE IgnoredDeckNames(Name TEXT PRIMARY KEY COLLATE NOCASE, AddedUtc TEXT NOT NULL);
        INSERT INTO IgnoredDeckNames(Name, AddedUtc) VALUES
            ('Aerial Domination', '2026-11-19T00:00:00.000Z'),
            ('Forest''s Might', '2026-11-19T00:00:00.000Z'),
            ('Imported Deck', '2026-11-19T00:00:00.000Z'),
            ('Keep the Peace', '2026-11-19T00:00:00.000Z');
        DROP VIEW vAccountLatest;
        CREATE VIEW vAccountLatest AS
        SELECT
            a.Email, a.Alias, a.DisplayName, a.Status, a.Notes, a.MiscNotes, a.NoteSource, a.MatchState,
            p.FirstSeenUtc, p.LastLoginUtc,
            s.Id AS SnapshotId, s.CapturedAtUtc,
            s.Gems, s.Gold, s.WcCommon, s.WcUncommon, s.WcRare, s.WcMythic, s.VaultProgress, s.WcTrackPosition,
            (SELECT COALESCE(SUM(b.Count), 0) FROM Boosters b WHERE b.SnapshotId = s.Id) AS BoosterTotal,
            r.Season, r.ConstructedClass, r.ConstructedLevel, r.LimitedClass, r.LimitedLevel,
            (SELECT COUNT(*) FROM Decks d WHERE d.DisplayName = a.DisplayName AND d.IsPrecon = 0
                AND NOT EXISTS (SELECT 1 FROM IgnoredDeckNames i WHERE i.Name = d.Name)) AS CustomDeckCount
        FROM Accounts a
        LEFT JOIN Players p ON p.DisplayName = a.DisplayName
        LEFT JOIN Snapshots s ON s.Id = (
            SELECT s2.Id FROM Snapshots s2 WHERE s2.DisplayName = a.DisplayName ORDER BY s2.CapturedAtUtc DESC LIMIT 1)
        LEFT JOIN Ranks r ON r.DisplayName = a.DisplayName AND r.CapturedAtUtc = (
            SELECT MAX(r2.CapturedAtUtc) FROM Ranks r2 WHERE r2.DisplayName = a.DisplayName);
        """,
        // 4: the other starter decks the owner hid by hand, hidden by default on any new database.
        """
        INSERT OR IGNORE INTO IgnoredDeckNames(Name, AddedUtc) VALUES
            ('Cold-Blooded Killers', '2026-11-23T00:00:00.000Z'),
            ('Destructive Firepower', '2026-11-23T00:00:00.000Z'),
            ('Goblins Everywhere!', '2026-11-23T00:00:00.000Z'),
            ('Large and in Charge', '2026-11-23T00:00:00.000Z'),
            ('Out for Blood', '2026-11-23T00:00:00.000Z');
        """,
        // 5: what the owner does on the page (views, buttons, settings), for the usage report an LLM can read.
        """
        CREATE TABLE UsageEvents(
            Id INTEGER PRIMARY KEY,
            AtUtc TEXT NOT NULL,
            Session TEXT NOT NULL,
            Kind TEXT NOT NULL,
            Name TEXT NOT NULL,
            Value TEXT NULL);
        CREATE INDEX IX_UsageEvents_At ON UsageEvents(AtUtc);
        """,
    ];
}

/// <summary>One text format for every stored time, so string order is time order.</summary>
public static class Time
{
    public static string Format(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static string? Format(DateTime? utc) => utc is { } u ? Format(u) : null;
}
