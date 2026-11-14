using MtgaDash.Parsing;

namespace MtgaDash.Tests;

[TestClass]
public sealed class LogParserTests
{
    private static List<LogEvent> Parse(IEnumerable<string> lines) =>
        new LogParser(logTimeZone: Fixtures.Utc).ParseAll(lines).ToList();

    [TestMethod]
    public void StartHook_yields_inventory_boosters_and_deck_summaries()
    {
        var events = Parse(Fixtures.AllerclearLog());

        var login = events.OfType<LoggedIn>().Single();
        Assert.AreEqual("Allerclear#92603", login.DisplayName);
        Assert.AreEqual("2026.65.0.14672", login.ClientVersion);
        Assert.AreEqual(new DateTime(2026, 11, 13, 0, 43, 30, DateTimeKind.Utc), login.AtUtc);

        var snap = events.OfType<InventorySnapshot>().Single();
        Assert.AreEqual((450, 2150, 39, 25, 8, 7), (snap.Gems, snap.Gold, snap.WcCommon, snap.WcUncommon, snap.WcRare, snap.WcMythic));
        Assert.AreEqual(new DateTime(2026, 11, 13, 5, 44, 1, 123, DateTimeKind.Utc), snap.CapturedAtUtc);
        CollectionAssert.AreEqual(new[] { "BRO:1", "TRK:1" }, snap.Boosters!.Select(b => $"{b.SetCode}:{b.Count}").ToArray());
        StringAssert.Contains(snap.CustomTokensJson, "BonusPackProgress");

        var decks = events.OfType<DeckSummaries>().Single().Decks;
        Assert.AreEqual(120, decks.Count);
        Assert.AreEqual(4, decks.Count(d => !d.IsPrecon));
        var custom = decks.First(d => !d.IsPrecon);
        Assert.AreEqual("Standard", custom.Format);
        Assert.AreEqual("2026-11-01T10:20:00.15-05:00", custom.LastPlayed);
    }

    [TestMethod]
    public void Two_logins_across_blocks_with_detailed_logs_off_yield_no_snapshots()
    {
        var events = Parse(Fixtures.TwoLoginsDetailedOff());
        CollectionAssert.AreEqual(new[] { "winnie#88467", "stacy#75572" }, events.OfType<LoggedIn>().Select(l => l.DisplayName).ToArray());
        Assert.AreEqual(2, events.OfType<SessionStarted>().Count());
        Assert.AreEqual(0, events.OfType<InventorySnapshot>().Count());
    }

    [TestMethod]
    public void DeckUpsert_yields_sixty_main_and_seven_sideboard_cards()
    {
        var list = Parse(Fixtures.AllerclearLog()).OfType<DeckList>().Single();
        Assert.AreEqual(60, list.MainDeck.Sum(c => c.Quantity));
        Assert.AreEqual(7, list.Sideboard.Sum(c => c.Quantity));
        Assert.AreEqual("c0", list.Summary.DeckId);
        Assert.AreEqual("Standard", list.Summary.Format);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void Request_strings_encoded_several_times_parse(int encodings)
    {
        var list = Parse([Fixtures.Startup(), Fixtures.Login("a#1"), Fixtures.DeckUpsert(encodings: encodings)]).OfType<DeckList>().Single();
        Assert.AreEqual(60, list.MainDeck.Sum(c => c.Quantity));
    }

    [TestMethod]
    public void Flat_card_arrays_parse()
    {
        var line = """[UnityCrossThreadLogger]==> DeckUpsertDeckV3 {"id":"x","request":"{\"Summary\":{\"DeckId\":\"d1\",\"Name\":\"n\"},\"Deck\":{\"MainDeck\":[101,4,102,2],\"Sideboard\":[]}}"}""";
        var list = Parse([Fixtures.Startup(), Fixtures.Login("a#1"), line]).OfType<DeckList>().Single();
        CollectionAssert.AreEqual(new[] { new DeckCard(101, 4), new DeckCard(102, 2) }, list.MainDeck.ToArray());
    }

    [TestMethod]
    public void Later_InventoryInfo_with_other_casing_updates_the_same_session_snapshot()
    {
        var events = Parse([Fixtures.Startup(), Fixtures.Login("a#1"), .. Fixtures.StartHook(), .. Fixtures.EventJoin(300, 900)]);
        var snaps = events.OfType<InventorySnapshot>().ToList();
        Assert.AreEqual(2, snaps.Count);
        Assert.AreEqual(snaps[0].CapturedAtUtc, snaps[1].CapturedAtUtc, "keyed by the session, so the writer merges them");
        Assert.AreEqual((300, 900, 4), (snaps[1].Gems, snaps[1].Gold, snaps[1].WcTrackPosition));
        Assert.IsNull(snaps[1].WcRare, "fields the response leaves out stay null");
        Assert.IsNull(snaps[1].Boosters);
    }

    [TestMethod]
    public void Responses_before_a_login_belong_to_no_account()
    {
        var events = Parse([Fixtures.Startup(), .. Fixtures.StartHook(), Fixtures.Login("late#2")]);
        Assert.AreEqual(0, events.OfType<InventorySnapshot>().Count());
        Assert.AreEqual(1, events.OfType<LoggedIn>().Count());
    }

    [TestMethod]
    public void A_new_block_forgets_the_previous_login()
    {
        var events = Parse([Fixtures.Startup(), Fixtures.Login("a#1"), Fixtures.Startup("11/13/2026 1:00:00 AM"), .. Fixtures.StartHook()]);
        Assert.AreEqual(0, events.OfType<InventorySnapshot>().Count());
    }

    [TestMethod]
    public void Malformed_json_is_a_warning_not_an_exception()
    {
        var events = Parse([Fixtures.Startup(), Fixtures.Login("a#1"), "<== StartHook(abc)", "{\"InventoryInfo\": {\"Gems\": 1", .. Fixtures.Rank()]);
        Assert.AreEqual(1, events.OfType<ParseWarning>().Count());
        Assert.AreEqual(1, events.OfType<RankInfo>().Count(), "parsing carries on after the bad line");
    }

    [TestMethod]
    public void Rank_without_class_is_null()
    {
        var rank = Parse([Fixtures.Startup(), Fixtures.Login("a#1"), .. Fixtures.Rank(null)]).OfType<RankInfo>().Single();
        Assert.IsNull(rank.ConstructedClass);
        Assert.AreEqual(95, rank.Season);
        var ranked = Parse([Fixtures.Startup(), Fixtures.Login("a#1"), .. Fixtures.Rank("Platinum", 3)]).OfType<RankInfo>().Single();
        Assert.AreEqual(("Platinum", 3, "Bronze", 4), (ranked.ConstructedClass, ranked.ConstructedLevel, ranked.LimitedClass, ranked.LimitedLevel));
    }

    [TestMethod]
    public void DeckGetDeckSummariesV3_refreshes_the_deck_list()
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { Summaries = new[] { Fixtures.Deck("z", "Fresh", "Alchemy", null) } });
        var decks = Parse([Fixtures.Startup(), Fixtures.Login("a#1"), "<== DeckGetDeckSummariesV3(q)", body]).OfType<DeckSummaries>().Single();
        Assert.AreEqual("Alchemy", decks.Decks.Single().Format);
    }

    [TestMethod]
    public void Parser_state_survives_a_json_round_trip_mid_response()
    {
        var lines = Fixtures.AllerclearLog();
        var split = Array.IndexOf(lines, lines.First(l => l.StartsWith("<== StartHook"))) + 1;
        var first = new LogParser(logTimeZone: Fixtures.Utc);
        var a = lines[..split].SelectMany(first.ParseLine).ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(first.State);
        var second = new LogParser(System.Text.Json.JsonSerializer.Deserialize<LogParserState>(json), Fixtures.Utc);
        var b = lines[split..].SelectMany(second.ParseLine).ToList();
        Assert.AreEqual(Parse(lines).Count, a.Count + b.Count);
        Assert.AreEqual(1, b.OfType<InventorySnapshot>().Count());
    }
}
