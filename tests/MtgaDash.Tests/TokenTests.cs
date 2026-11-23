using System.Text.Json;
using MtgaDash.Data;

namespace MtgaDash.Tests;

[TestClass]
public sealed class TokenTests
{
    [TestMethod]
    [DataRow("Token_Draft", "Draft", "draft")]
    [DataRow("DraftTokenPremier", "Draft Token Premier", "draft")]
    [DataRow("Token_EventEntry", "Event Entry", "event")]
    [DataRow("BonusPackProgress", "Bonus Pack Progress", "other")]
    public void Token_names_are_labeled_and_sorted_into_kinds(string key, string label, string kind)
    {
        Assert.AreEqual(label, Queries.TokenLabel(key));
        Assert.AreEqual(kind, Queries.TokenKind(key));
    }

    [TestMethod]
    public void Only_counts_above_zero_are_kept_and_bad_json_is_empty()
    {
        var t = Queries.Tokens("""{"Token_Draft":2,"Token_Sealed":0,"EventToken":"x","Token_EventEntry":1}""");
        CollectionAssert.AreEqual(new[] { "Token_Draft:2", "Token_EventEntry:1" }, t.Select(x => $"{x.Key}:{x.Count}").ToArray());
        Assert.AreEqual(0, Queries.Tokens("{ broken").Count);
        Assert.AreEqual(0, Queries.Tokens(null).Count);
    }

    private static string WithTokens(string startup, string server, string name, object tokens) =>
        Fixtures.Text([Fixtures.Startup(startup), Fixtures.Login(name), "<== StartHook(x)",
            JsonSerializer.Serialize(new { InventoryInfo = new { Gems = 1, Gold = 1, CustomTokens = tokens }, ServerTimeInternal = server })]);

    [TestMethod]
    public void Accounts_and_totals_show_draft_and_event_tokens_when_found()
    {
        using var t = new TempDb();
        File.WriteAllText(t.File("p.log"),
            WithTokens("11/20/2026 1:00:00 PM", "2026-11-20T18:00:00Z", "a#1", new Dictionary<string, int> { ["Token_Draft"] = 3, ["BonusPackProgress"] = 2 }) +
            WithTokens("11/20/2026 2:00:00 PM", "2026-11-20T19:00:00Z", "b#2", new Dictionary<string, int> { ["Token_Draft"] = 1, ["Token_EventEntry"] = 2 }));
        t.Ingestor.IngestFile(t.File("p.log"));
        using (var c = t.Db.Open())
        {
            Writer.ImportAccounts(c, [new Accounts.AccountRow("x+a@example.com", "", "", "", ""), new Accounts.AccountRow("x+b@example.com", "", "", "", "")]);
            Dapper.SqlMapper.Execute(c, "UPDATE Accounts SET DisplayName = CASE Email WHEN 'x+a@example.com' THEN 'a#1' ELSE 'b#2' END");
        }
        var q = new Queries(t.Db);

        var tokens = q.Account("x+a@example.com")!.Tokens!;
        CollectionAssert.AreEqual(new[] { "draft:3", "other:2" }, tokens.Select(x => $"{x.Kind}:{x.Count}").ToArray());

        var totals = q.Totals();
        CollectionAssert.AreEqual(new[] { "x+a@example.com:3", "x+b@example.com:1" }, totals.MostDraftTokens!.Select(l => $"{l.Email}:{l.Count}").ToArray());
        Assert.AreEqual("x+b@example.com:2", totals.MostEventTokens!.Select(l => $"{l.Email}:{l.Count}").Single());
        Assert.AreEqual(4, totals.TokenTotals!.Single(x => x.Key == "Token_Draft").Count);
    }

    [TestMethod]
    public void The_starter_decks_the_owner_named_are_hidden_by_default()
    {
        using var t = new TempDb();
        var hidden = new Queries(t.Db).DeckNameLists().Hidden;
        foreach (var name in new[] { "Aerial Domination", "Cold-Blooded Killers", "Destructive Firepower", "Forest's Might", "Goblins Everywhere!",
                                     "Imported Deck", "Keep the Peace", "Large and in Charge", "Out for Blood" })
            CollectionAssert.Contains(hidden.ToList(), name);
    }
}
