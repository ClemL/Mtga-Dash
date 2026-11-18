using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MtgaDash.Cards;

namespace MtgaDash.Tests;

[TestClass]
public sealed class CardNamesTests
{
    private static string MakeDb(string folder, string schema)
    {
        var path = Path.Combine(folder, "Raw_CardDatabase_abc123.mtga");
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        c.Execute(schema);
        return path;
    }

    [TestMethod]
    public void Resolves_a_per_language_localization_table_through_a_wildcard()
    {
        using var t = new TempDb();
        MakeDb(t.Folder, """
            CREATE TABLE Cards(GrpId INTEGER, TitleId INTEGER, ExpansionCode TEXT);
            CREATE TABLE Localizations_enUS(LocId INTEGER, Formatted INTEGER, Loc TEXT);
            INSERT INTO Cards VALUES (80000, 1, 'BRO'), (80001, 2, 'BRO');
            INSERT INTO Localizations_enUS VALUES (1, 0, 'Lightning Bolt'), (1, 1, '<i>Lightning Bolt</i>'), (2, 0, 'Shock');
            """);
        var names = new CardNames(Path.Combine(t.Folder, "Raw_CardDatabase_*.mtga"), NullLogger.Instance);
        var found = names.Lookup([80000, 80001, 99999]);
        Assert.AreEqual("Lightning Bolt", found[80000]);
        Assert.AreEqual("Shock", found[80001]);
        Assert.IsFalse(found.ContainsKey(99999));
        StringAssert.Contains(names.Status, "Localizations_enUS.Loc");
    }

    [TestMethod]
    public void Resolves_an_enUS_column()
    {
        using var t = new TempDb();
        var path = MakeDb(t.Folder, """
            CREATE TABLE Cards(GrpId INTEGER, TitleId INTEGER);
            CREATE TABLE Localizations(LocId INTEGER, Formatted INTEGER, enUS TEXT, frFR TEXT);
            INSERT INTO Cards VALUES (5, 9);
            INSERT INTO Localizations VALUES (9, 0, 'Opt', 'Option');
            """);
        Assert.AreEqual("Opt", new CardNames(path, NullLogger.Instance).Lookup([5])[5]);
    }

    [TestMethod]
    public void An_unknown_schema_leaves_names_off_and_says_why()
    {
        using var t = new TempDb();
        var path = MakeDb(t.Folder, "CREATE TABLE Something(Id INTEGER);");
        var names = new CardNames(path, NullLogger.Instance);
        Assert.AreEqual(0, names.Lookup([1]).Count);
        StringAssert.Contains(names.Status, "no table with GrpId and TitleId");
    }

    [TestMethod]
    public void No_setting_means_no_names()
    {
        var names = new CardNames("", NullLogger.Instance);
        Assert.AreEqual(0, names.Lookup([1]).Count);
        Assert.AreEqual("CardDbPath is not set", names.Status);
    }

    [TestMethod]
    public void A_setting_saved_while_running_applies_without_a_restart()
    {
        using var t = new TempDb();
        var path = MakeDb(t.Folder, """
            CREATE TABLE Cards(GrpId INTEGER, TitleId INTEGER);
            CREATE TABLE Localizations_enUS(LocId INTEGER, Formatted INTEGER, Loc TEXT);
            INSERT INTO Cards VALUES (92275, 1);
            INSERT INTO Localizations_enUS VALUES (1, 0, 'Test Card');
            """);
        var setting = "";
        var names = new CardNames(() => setting, NullLogger.Instance);
        Assert.AreEqual(0, names.State().Cards);
        setting = Path.Combine(t.Folder, "Raw_CardDatabase_*.mtga");
        Assert.AreEqual("Test Card", names.Lookup([92275])[92275]);
        var state = names.State();
        Assert.AreEqual((1, path), (state.Cards, state.ResolvedPath));
        setting = "";
        Assert.AreEqual(0, names.Lookup([92275]).Count, "removing the setting turns names off again");
    }

    [TestMethod]
    public void Card_databases_are_found_in_install_folders_and_tested()
    {
        using var t = new TempDb();
        var good = Directory.CreateDirectory(Path.Combine(t.Folder, "Wizards", "MTGA_Data", "Downloads", "Raw")).FullName;
        MakeDb(good, "CREATE TABLE Cards(GrpId INTEGER, TitleId INTEGER); CREATE TABLE Localizations_enUS(LocId INTEGER, Loc TEXT); INSERT INTO Cards VALUES (1, 1); INSERT INTO Localizations_enUS VALUES (1, 'A');");
        var bad = Directory.CreateDirectory(Path.Combine(t.Folder, "Steam", "MTGA_Data", "Downloads", "Raw")).FullName;
        MakeDb(bad, "CREATE TABLE Other(Id INTEGER);");

        var found = CardNames.FindCandidates([Path.Combine(t.Folder, "Wizards"), Path.Combine(t.Folder, "Steam"), Path.Combine(t.Folder, "Epic")]);
        Assert.AreEqual(2, found.Count, "the folder without MTGA is skipped");
        Assert.IsTrue(found[0].Ok);
        Assert.AreEqual(1, found[0].Cards);
        StringAssert.EndsWith(found[0].Pattern, "Raw_CardDatabase_*.mtga");
        Assert.IsFalse(found[1].Ok);
        StringAssert.Contains(found[1].Message, "no table with GrpId");
    }

    [TestMethod]
    public void The_game_folder_and_program_files_are_searched()
    {
        var folders = CardNames.DefaultInstallFolders(@"D:\Games\MTGA\MTGA.exe").ToList();
        Assert.IsTrue(folders.Count >= 1);
        Assert.IsFalse(CardNames.DefaultInstallFolders("steam://rungameid/2141910").Any(f => f.Contains("steam:", StringComparison.Ordinal)));
    }
}
