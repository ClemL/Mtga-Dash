using MtgaDash.Parsing;

namespace MtgaDash.Tests;

/// <summary>
/// Checks against the owner's real logs in testdata/ (git-ignored: they contain email-linked display
/// names). Inconclusive where the files are not present, such as CI or a fresh clone.
/// </summary>
[TestClass]
public sealed class RealLogTests
{
    private static List<LogEvent> ParseFile(string name)
    {
        var path = Fixtures.TestData(name);
        if (!File.Exists(path)) Assert.Inconclusive($"testdata/{name} is not present");
        return new LogParser().ParseAll(File.ReadLines(path)).ToList();
    }

    [TestMethod]
    public void Player_log_has_Allerclear_inventory_and_decks()
    {
        var events = ParseFile("Player.log");
        var snap = events.OfType<InventorySnapshot>().Where(s => s.DisplayName == "Allerclear#92603" && s.Boosters is not null).FirstOrDefault();
        Assert.IsNotNull(snap, "a StartHook snapshot for Allerclear#92603");
        Assert.AreEqual((450, 2150), (snap.Gems, snap.Gold));
        Assert.AreEqual((39, 25, 8, 7), (snap.WcCommon, snap.WcUncommon, snap.WcRare, snap.WcMythic));
        CollectionAssert.AreEquivalent(new[] { "BRO:1", "TRK:1" }, snap.Boosters!.Select(b => $"{b.SetCode}:{b.Count}").ToArray());
        var decks = events.OfType<DeckSummaries>().First(d => d.DisplayName == "Allerclear#92603").Decks;
        Assert.AreEqual(120, decks.Count);
        Assert.AreEqual(4, decks.Count(d => !d.IsPrecon));
        Assert.AreEqual(0, events.OfType<ParseWarning>().Count(), string.Join("\n", events.OfType<ParseWarning>().Select(w => w.Message)));
    }

    [TestMethod]
    public void Player_prev_log_has_two_logins_and_no_snapshots()
    {
        var events = ParseFile("Player-prev.log");
        var names = events.OfType<LoggedIn>().Select(l => l.DisplayName).Distinct().ToList();
        CollectionAssert.IsSubsetOf(new[] { "winnie#88467", "stacy#75572" }, names);
        Assert.IsTrue(events.OfType<SessionStarted>().Count() >= 2);
        Assert.AreEqual(0, events.OfType<InventorySnapshot>().Count());
    }

    [TestMethod]
    public void Accounts_csv_reads()
    {
        var path = Fixtures.TestData("mtga_accounts_20261112.csv");
        if (!File.Exists(path)) Assert.Inconclusive("testdata/mtga_accounts_20261112.csv is not present");
        var rows = MtgaDash.Accounts.AccountsCsv.Read(path);
        Assert.IsTrue(rows.Count > 0);
        Assert.IsTrue(rows.All(r => r.Email.Contains('@')));
    }
}
