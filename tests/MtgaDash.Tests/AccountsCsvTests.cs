using Dapper;
using MtgaDash.Accounts;
using MtgaDash.Data;

namespace MtgaDash.Tests;

[TestClass]
public sealed class AccountsCsvTests
{
    private const string Csv = "﻿Status,Email,Notes,Misc Notes,Note Source\r\n" +
        "Active,base1+Allerclear@example.com,\"Has 8 rares, keep\",,sheet\r\n" +
        "No deck / unused,base2+corsair@example.com,\"Quote \"\"inside\"\"\",\"multi\nline\",\r\n" +
        ",,blank email row,,\r\n";

    [TestMethod]
    public void Reads_quoted_fields_and_skips_rows_without_an_email()
    {
        var rows = AccountsCsv.Read(new StringReader(Csv));
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual("Has 8 rares, keep", rows[0].Notes);
        Assert.AreEqual("Quote \"inside\"", rows[1].Notes);
        Assert.AreEqual("multi\nline", rows[1].MiscNotes);
        Assert.AreEqual("No deck / unused", rows[1].Status);
    }

    [TestMethod]
    public void Reimport_upserts_and_never_deletes()
    {
        using var t = new TempDb();
        using var c = t.Db.Open();
        Writer.ImportAccounts(c, AccountsCsv.Read(new StringReader(Csv)));
        Writer.ImportAccounts(c, [new AccountRow("BASE1+allerclear@example.com", "Retired", "", "", "")]);
        Assert.AreEqual(2, c.ExecuteScalar<int>("SELECT COUNT(*) FROM Accounts"));
        Assert.AreEqual("Retired", c.ExecuteScalar<string>("SELECT Status FROM Accounts WHERE Email = 'base1+Allerclear@example.com'"));
        Assert.AreEqual("allerclear", c.ExecuteScalar<string>("SELECT Alias FROM Accounts WHERE Status = 'Retired'"));
    }

    [TestMethod]
    public void Review_flags_unused_accounts_with_decks_and_lists_unmatched_names()
    {
        using var t = new TempDb();
        File.WriteAllText(t.File("p.log"), Fixtures.Text([
            Fixtures.Startup(), Fixtures.Login("Corsair#5"), .. Fixtures.StartHook(),
            Fixtures.Startup("11/13/2026 2:00:00 AM"), Fixtures.Login("Stranger#7"),
        ]));
        t.Ingestor.IngestFile(t.File("p.log"));
        using (var c = t.Db.Open())
        {
            Writer.ImportAccounts(c, AccountsCsv.Read(new StringReader(Csv)));
            Writer.RunMatching(c);
        }

        var review = t.Queries.Review();
        Assert.AreEqual("Stranger#7", review.Unmatched.Single().DisplayName);
        StringAssert.StartsWith(review.Unmatched.Single().ExceptionLine, "[\"Stranger#7\"] = \"\",");
        Assert.AreEqual("base2+corsair@example.com", review.StatusMismatches.Single().Email);

        var account = t.Queries.Account("base2+corsair@example.com")!;
        Assert.AreEqual(4, account.CustomDecks.Count);
        Assert.AreEqual(116, account.PreconCount);
        Assert.AreEqual(2, account.Boosters.Count);
        Assert.AreEqual(1, account.Snapshots.Count);

        var totals = t.Queries.Totals(new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc));
        Assert.AreEqual(450, totals.Gems);
        Assert.AreEqual(1, totals.LoginBuckets["≤7d"]);
        Assert.AreEqual(1, totals.LoginBuckets["never"]);

        var stale = t.Queries.Accounts(stale: true, staleDays: 30, nowUtc: new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc));
        Assert.AreEqual("base1+Allerclear@example.com", stale.Single().Email);
        Assert.AreEqual(1, t.Queries.Accounts(search: "CORSAIR unused").Count);
    }
}
