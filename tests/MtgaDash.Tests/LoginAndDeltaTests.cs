using Dapper;
using MtgaDash.Ingestion;
using MtgaDash.Parsing;

namespace MtgaDash.Tests;

[TestClass]
public sealed class LoginAndDeltaTests
{
    private static List<LogEvent> Parse(IEnumerable<string> lines) => new LogParser(logTimeZone: Fixtures.Utc).ParseAll(lines).ToList();

    [TestMethod]
    [DataRow("[Accounts - AutoLogin] Display Name: Auto#12345")]
    [DataRow("[UnityCrossThreadLogger]Successfully logged in to account: Auto#12345")]
    public void Other_login_lines_are_recognized_and_their_inventory_kept(string line)
    {
        var events = Parse([Fixtures.Startup(), line, .. Fixtures.StartHook()]);
        var login = events.OfType<LoggedIn>().Single();
        Assert.AreEqual(("Auto#12345", "other login line"), (login.DisplayName, login.Source));
        Assert.AreEqual(1, events.OfType<InventorySnapshot>().Count());
    }

    [TestMethod]
    public void A_login_response_names_the_player_when_no_login_line_was_written()
    {
        var events = Parse([
            Fixtures.Startup(),
            "<== AuthenticateResponse(abc)", """{"sessionId":"s","screenName":"Silent#4242"}""",
            .. Fixtures.StartHook(),
        ]);
        var login = events.OfType<LoggedIn>().Single();
        Assert.AreEqual(("Silent#4242", "AuthenticateResponse response"), (login.DisplayName, login.Source));
        Assert.AreEqual("Silent#4242", events.OfType<InventorySnapshot>().Single().DisplayName);
    }

    [TestMethod]
    public void An_opponents_name_never_becomes_the_login()
    {
        var events = Parse([
            Fixtures.Startup(),
            "<== MatchGameRoomStateChangedEvent(x)", """{"reservedPlayers":[{"screenName":"Opponent#1111"}]}""",
            Fixtures.Login("Me#2222"),
            "<== AuthenticateResponse(y)", """{"screenName":"Opponent#1111"}""",
            "[UnityCrossThreadLogger] Opponent Opponent#1111 joined",
        ]);
        CollectionAssert.AreEqual(new[] { "Me#2222" }, events.OfType<LoggedIn>().Select(l => l.DisplayName).ToArray());
    }

    [TestMethod]
    public void Missing_login_is_reported_only_for_an_active_session_past_the_grace_period()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var started = new LogParserState { BlockStartedUtc = now.AddMinutes(-10) };
        Assert.IsTrue(StatusTracker.MissingLogin(started, now.AddMinutes(-1), now));
        Assert.IsFalse(StatusTracker.MissingLogin(started, now.AddHours(-1), now), "an old file is not a session in progress");
        Assert.IsFalse(StatusTracker.MissingLogin(new LogParserState { BlockStartedUtc = now.AddMinutes(-1) }, now, now), "still on the login screen");
        Assert.IsFalse(StatusTracker.MissingLogin(new LogParserState { DisplayName = "a#1", BlockStartedUtc = now.AddMinutes(-10) }, now, now));
    }

    private static readonly string[] Prize =
    [
        "<== EventClaimPrize(p1)",
        """{"updates":[{"delta":{"goldDelta":250,"gemsDelta":0,"wcRareDelta":1}},{"delta":{"goldDelta":100}}]}""",
    ];

    private static (long Gold, long Gems, long Rare) Inventory(TempDb t)
    {
        using var c = t.Db.Open();
        return c.QuerySingle<(long, long, long)>("SELECT Gold, Gems, WcRare FROM Snapshots");
    }

    [TestMethod]
    public void A_gold_change_after_a_match_moves_the_inventory_once_however_often_it_is_read()
    {
        using var t = new TempDb();
        var log = Fixtures.Text([Fixtures.Startup(), Fixtures.Login("a#1"), .. Fixtures.StartHook(), .. Prize]);
        File.WriteAllText(t.File("Player.log"), log);
        t.Ingestor.IngestFile(t.File("Player.log"));
        Assert.AreEqual((2500L, 450L, 9L), Inventory(t), "2150 + 250 + 100 gold, 8 + 1 rare wildcards");

        // The same lines again: the archived copy, and a re-parse after a rotation.
        File.WriteAllText(t.File("Player_copy.log"), log);
        t.Ingestor.IngestFile(t.File("Player_copy.log"));
        File.WriteAllText(t.File("Player.log"), log.Replace("12:43:30", "12:43:31"));
        t.Ingestor.IngestFile(t.File("Player.log"));
        Assert.AreEqual((2500L, 450L, 9L), Inventory(t));
    }

    [TestMethod]
    public void A_change_without_a_snapshot_or_beside_a_full_inventory_is_not_applied_twice()
    {
        var before = Parse([Fixtures.Startup(), Fixtures.Login("a#1"), .. Prize]);
        Assert.AreEqual(1, before.OfType<InventoryDelta>().Count(), "parsed, but the writer has no snapshot to apply it to");

        var both = Parse([Fixtures.Startup(), Fixtures.Login("a#1"), .. Fixtures.StartHook(),
            "<== EventJoin(j)", """{"InventoryInfo":{"Gold":900},"Changes":[{"GoldDelta":-500}]}"""]);
        Assert.AreEqual(0, both.OfType<InventoryDelta>().Count(), "the full inventory already includes the change");

        using var t = new TempDb();
        File.WriteAllText(t.File("p.log"), Fixtures.Text([Fixtures.Startup(), Fixtures.Login("a#1"), .. Prize]));
        t.Ingestor.IngestFile(t.File("p.log"));
        using var c = t.Db.Open();
        Assert.AreEqual(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM Snapshots"));
    }

    [TestMethod]
    public void Only_player_logs_are_read_and_the_previous_session_too()
    {
        using var t = new TempDb();
        var logs = Directory.CreateDirectory(t.File("logs")).FullName;
        var mtga = Directory.CreateDirectory(t.File("mtga")).FullName;
        foreach (var f in new[] { "Player_20261113_0052.log", "archiver_20261113_0053.log", "notes.txt" }) File.WriteAllText(Path.Combine(logs, f), "");
        File.WriteAllText(Path.Combine(mtga, "Player.log"), "");
        File.WriteAllText(Path.Combine(mtga, "Player-prev.log"), "");
        var options = new MtgaDashOptions { LogFolder = logs, LiveLogPath = Path.Combine(mtga, "Player.log") };
        var service = new IngestionService(options, null!, null!, null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<IngestionService>.Instance);

        CollectionAssert.AreEqual(new[] { "Player_20261113_0052.log", "Player-prev.log", "Player.log" }, service.Files().Select(Path.GetFileName).ToArray());

        options.WatchPrevLog = false;
        options.LogFilePattern = "*.log";
        CollectionAssert.AreEqual(new[] { "archiver_20261113_0053.log", "Player_20261113_0052.log", "Player.log" }, service.Files().Select(Path.GetFileName).ToArray());
    }
}
