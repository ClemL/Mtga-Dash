using MtgaDash.Data;

namespace MtgaDash.Tests;

[TestClass]
public sealed class DropsAndDecksTests
{
    private static string Session(string startup, string serverTime, int gems, int gold, int rare, int mythic) =>
        Fixtures.Text([Fixtures.Startup(startup), Fixtures.Login("a#1"), .. Fixtures.StartHook(serverTime, gems: gems, gold: gold, r: rare, m: mythic)]);

    [TestMethod]
    public void Large_drops_between_snapshots_are_flagged_small_ones_are_not()
    {
        using var t = new TempDb();
        File.WriteAllText(t.File("p.log"),
            Session("11/10/2026 9:00:00 AM", "2026-11-10T14:00:00Z", 3000, 20000, 8, 7) +
            Session("11/11/2026 9:00:00 AM", "2026-11-11T14:00:00Z", 2800, 19000, 8, 7) +   // normal spending
            Session("11/12/2026 9:00:00 AM", "2026-11-12T14:00:00Z", 1500, 9000, 5, 6));    // a raid
        t.Ingestor.IngestFile(t.File("p.log"));

        var queries = new Queries(t.Db, options: new MtgaDashOptions());
        using var c = t.Db.Open();
        var drop = queries.Drops(c, new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc)).Single();
        Assert.AreEqual(("2026-11-11T14:00:00.000Z", "2026-11-12T14:00:00.000Z"), (drop.FromUtc, drop.ToUtc));
        Assert.AreEqual((-1300L, -10000L, -3L, -1L), (drop.GemsChange, drop.GoldChange, drop.RareChange, drop.MythicChange));
        CollectionAssert.AreEqual(new[] { "gems down 1,300", "gold down 10,000", "rare and mythic wildcards down 4" }, drop.Reasons.ToArray());

        Assert.AreEqual(0, queries.Drops(c, new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Count, "older than AlertDays");
        Assert.AreEqual(1, queries.Review().Drops.Count);
    }

    [TestMethod]
    public void Custom_decks_without_a_saved_list_are_listed_to_open_and_save()
    {
        using var t = new TempDb();
        // StartHook lists 4 custom decks (c0..c3); only c0 is saved.
        File.WriteAllText(t.File("p.log"), Fixtures.Text([Fixtures.Startup(), Fixtures.Login("a#1"), .. Fixtures.StartHook(), Fixtures.DeckUpsert("c0")]));
        t.Ingestor.IngestFile(t.File("p.log"));
        var u = new Queries(t.Db).Cards().Unrecorded.Single();
        Assert.AreEqual(("a#1", 3, 4), (u.DisplayName, u.Count, u.Total));
        CollectionAssert.AreEqual(new[] { "My deck 1", "My deck 2", "My deck 3" }, u.DeckNames.ToArray());
    }
}

[TestClass]
public sealed class EmptyDatabaseTests
{
    /// <summary>
    /// With no rows SQLite reports computed columns without a type; every read must still work, because a first
    /// run starts with an empty database (no CSV, no logs yet).
    /// </summary>
    [TestMethod]
    public void Every_query_works_on_an_empty_database()
    {
        using var t = new TempDb();
        var q = new Queries(t.Db, options: new MtgaDashOptions());
        Assert.AreEqual(0, q.Accounts().Count);
        Assert.AreEqual(0, q.Players().Count);
        var review = q.Review();
        Assert.AreEqual(0, review.Drops.Count + review.Unmatched.Count + review.StatusMismatches.Count);
        Assert.AreEqual(0, q.Totals().Accounts);
        Assert.AreEqual(0, q.Cards().Cards.Count);
        Assert.IsNull(q.Account("nobody@example.com"));
        Assert.AreEqual(0, q.CardHolders(1).Count);
        Assert.AreEqual(4, q.DeckNameLists().Hidden.Count, "the four starter names are hidden from the start");

        // An account with no decks: the deck query returns nothing, and must still map.
        using (var c = t.Db.Open())
            Writer.ImportAccounts(c, [new Accounts.AccountRow("solo@example.com", "Active", "", "", "")]);
        Assert.AreEqual(0, q.Account("solo@example.com")!.CustomDecks.Count);
    }
}
