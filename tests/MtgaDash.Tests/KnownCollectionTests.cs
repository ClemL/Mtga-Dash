using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MtgaDash.Cards;
using MtgaDash.Data;

namespace MtgaDash.Tests;

[TestClass]
public sealed class KnownCollectionTests
{
    private static string Cards(object main, object? side = null) =>
        JsonSerializer.Serialize(new { MainDeck = main, Sideboard = side ?? Array.Empty<object>(), CommandZone = Array.Empty<object>(), Companions = Array.Empty<object>() });

    private static object C(int id, int qty) => new { CardId = id, Quantity = qty };

    [TestMethod]
    public void Copies_are_the_most_in_any_one_deck_never_a_sum_across_decks()
    {
        var known = KnownCollection.For([
            new SavedDeck("a#1", "d1", "Mono red", Cards(new[] { C(1, 4), C(2, 2) })),
            new SavedDeck("a#1", "d2", "Gruul", Cards(new[] { C(1, 3), C(2, 2) })),
        ]);
        var one = known.Single(k => k.CardId == 1);
        Assert.AreEqual(4, one.Copies, "the same four copies can sit in two decks");
        Assert.AreEqual(2, one.Decks);
        CollectionAssert.AreEqual(new[] { "Mono red", "Gruul" }, one.DeckNames.ToArray());
        Assert.AreEqual(2, known.Single(k => k.CardId == 2).Copies);
    }

    [TestMethod]
    public void Main_deck_and_sideboard_copies_of_one_deck_add_up()
    {
        var known = KnownCollection.For([new SavedDeck("a#1", "d1", "x", Cards(new[] { C(7, 2) }, new[] { C(7, 2) }))]);
        Assert.AreEqual(4, known.Single().Copies);
    }

    [TestMethod]
    public void Every_saved_version_counts_even_after_the_deck_was_cut_down()
    {
        var known = KnownCollection.For([
            new SavedDeck("a#1", "d1", "x", Cards(new[] { C(9, 4) })),
            new SavedDeck("a#1", "d1", "x", Cards(new[] { C(9, 1) })),
        ]);
        var card = known.Single();
        Assert.AreEqual((4, 1), (card.Copies, card.Decks));
    }

    [TestMethod]
    public void A_corrupt_list_is_skipped_and_accounts_stay_apart()
    {
        var byAccount = KnownCollection.ByAccount([
            new SavedDeck("a#1", "d1", "x", "{ broken"),
            new SavedDeck("a#1", "d2", "y", Cards(new[] { C(5, 3) })),
            new SavedDeck("b#2", "d3", "z", Cards(new[] { C(5, 1) })),
        ]);
        Assert.AreEqual(3, byAccount["a#1"].Single().Copies);
        Assert.AreEqual(1, byAccount["b#2"].Single().Copies);
    }

    [TestMethod]
    [DataRow(5L, "mythic")]
    [DataRow(4L, "rare")]
    [DataRow(3L, "uncommon")]
    [DataRow(2L, "common")]
    [DataRow(1L, "land")]
    [DataRow(9L, null)]
    [DataRow("Mythic", "mythic")]
    [DataRow("4", "rare")]
    [DataRow("weird", null)]
    public void Rarity_values_map_or_stay_unknown(object value, string? expected) =>
        Assert.AreEqual(expected, CardNames.RarityName(value));

    private static string CardDb(string folder)
    {
        var path = Path.Combine(folder, "Raw_CardDatabase_test.mtga");
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        c.Execute("""
            CREATE TABLE Cards(GrpId INTEGER, TitleId INTEGER, Rarity INTEGER, ExpansionCode TEXT);
            CREATE TABLE Localizations_enUS(LocId INTEGER, Formatted INTEGER, Loc TEXT);
            INSERT INTO Cards VALUES (80000, 1, 5, 'BRO'), (80001, 2, 4, 'BRO'), (80002, 3, 2, 'BRO'), (90000, 4, 4, 'DMU');
            INSERT INTO Localizations_enUS VALUES (1, 0, 'Mythic One'), (2, 0, 'Rare Two'), (3, 0, 'Common Three'), (4, 0, 'Side Rare');
            """);
        return path;
    }

    [TestMethod]
    public void Saved_decks_become_rares_and_mythics_at_three_or_more_per_account_and_across_accounts()
    {
        using var t = new TempDb();
        // Main deck: 80000..80014 as four-ofs. Sideboard: 90000 x4, 90001 x3.
        File.WriteAllText(t.File("p.log"), Fixtures.Text([
            Fixtures.Startup(), Fixtures.Login("Corsair#5"), .. Fixtures.StartHook(), Fixtures.DeckUpsert("c0"),
            Fixtures.Startup("11/13/2026 2:00:00 AM"), Fixtures.Login("Quill#9"), .. Fixtures.StartHook("2026-11-13T07:00:00Z"),
            Fixtures.DeckUpsert("q0", main: 8, side: 0),
        ]));
        t.Ingestor.IngestFile(t.File("p.log"));
        using (var c = t.Db.Open())
        {
            Writer.ImportAccounts(c, [new Accounts.AccountRow("base1+corsair@example.com", "Active", "", "", "")]);
            Writer.RunMatching(c);
        }
        var queries = new Queries(t.Db, new CardNames(CardDb(t.Folder), NullLogger.Instance));

        var report = queries.Cards(minCopies: 3);
        Assert.IsTrue(report.RarityKnown);
        CollectionAssert.AreEqual(new[] { "Mythic One", "Rare Two", "Side Rare" }, report.Cards.Select(x => x.Name).Order().ToArray(), "the common is filtered out");
        var mythic = report.Cards.Single(x => x.Name == "Mythic One");
        Assert.AreEqual(("mythic", "BRO", 2), (mythic.Rarity, mythic.Set, mythic.Holders.Count));
        Assert.AreEqual("base1+corsair@example.com", mythic.Holders.Single(h => h.DisplayName == "Corsair#5").Email);
        Assert.IsNull(mythic.Holders.Single(h => h.DisplayName == "Quill#9").Email, "an unmatched account still counts");
        Assert.AreEqual((3, 2), (report.PerAccount["Corsair#5"], report.PerAccount["Quill#9"]));

        Assert.AreEqual(4, queries.Cards(minCopies: 3, rarities: ["mythic", "rare", "common"]).Cards.Count(x => x.Holders.Any(h => h.DisplayName == "Corsair#5")), "adding commons adds Common Three");

        var detail = queries.Account("base1+corsair@example.com")!;
        Assert.IsTrue(detail.CardRarityKnown);
        var side = detail.KnownCards.Single(k => k.CardId == 90000);
        Assert.AreEqual((4, "rare", "Side Rare"), (side.Copies, side.Rarity, side.Name));
        Assert.AreEqual(3, detail.KnownCards.Single(k => k.CardId == 90001).Copies);
    }

    [TestMethod]
    public void Without_the_card_database_every_card_qualifies_and_rarity_is_unknown()
    {
        using var t = new TempDb();
        File.WriteAllText(t.File("p.log"), Fixtures.Text([Fixtures.Startup(), Fixtures.Login("a#1"), .. Fixtures.StartHook(), Fixtures.DeckUpsert("c0", main: 8, side: 0)]));
        t.Ingestor.IngestFile(t.File("p.log"));
        var report = new Queries(t.Db).Cards(minCopies: 4);
        Assert.IsFalse(report.RarityKnown);
        Assert.AreEqual(2, report.Cards.Count);
        Assert.IsTrue(report.Cards.All(x => x.Rarity is null && x.Name is null));
    }
}
