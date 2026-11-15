using Microsoft.Extensions.Logging.Abstractions;
using MtgaDash.Accounts;
using MtgaDash.Ingestion;
using MtgaDash.Parsing;

namespace MtgaDash.Tests;

[TestClass]
public sealed class ExceptionStoreTests
{
    [TestMethod]
    public void Saved_mappings_survive_a_new_store_and_are_case_insensitive()
    {
        using var t = new TempDb();
        var path = t.File("account-exceptions.json");
        new ExceptionStore(path, NullLogger.Instance).Set("Corsair#5", "base2+corsair@example.com");
        var (map, _) = new ExceptionStore(path, NullLogger.Instance).Current();
        Assert.AreEqual("base2+corsair@example.com", map["CORSAIR#5"]);
        Assert.AreEqual("file", new ExceptionStore(path, NullLogger.Instance).Mappings().Single().Source);
    }

    [TestMethod]
    public void A_hand_edit_applies_without_a_restart()
    {
        using var t = new TempDb();
        var path = t.File("account-exceptions.json");
        var store = new ExceptionStore(path, NullLogger.Instance);
        Assert.AreEqual(0, store.Current().Map.Count - AccountExceptions.DisplayNameToEmail.Count);
        File.WriteAllText(path, """{ "displayNameToEmail": { "Quill#1": "a+quill@example.com" }, "excludedEmails": ["x@example.com"] }""");
        var (map, excluded) = store.Current();
        Assert.AreEqual("a+quill@example.com", map["quill#1"]);
        Assert.IsTrue(excluded.Contains("X@example.com"));
    }

    [TestMethod]
    public void A_malformed_file_is_ignored_and_never_overwritten()
    {
        using var t = new TempDb();
        var path = t.File("account-exceptions.json");
        File.WriteAllText(path, "{ not json");
        var store = new ExceptionStore(path, NullLogger.Instance);
        Assert.AreEqual(AccountExceptions.DisplayNameToEmail.Count, store.Current().Map.Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Set("a#1", "a+b@example.com"));
        Assert.AreEqual("{ not json", File.ReadAllText(path));
    }

    [TestMethod]
    public void Remove_reports_whether_anything_was_removed()
    {
        using var t = new TempDb();
        var store = new ExceptionStore(t.File("e.json"), NullLogger.Instance);
        store.Set("a#1", "a+b@example.com");
        Assert.IsTrue(store.Remove("A#1"));
        Assert.IsFalse(store.Remove("a#1"));
    }

    [TestMethod]
    public void Missing_inventory_is_reported_only_for_an_active_session_past_the_grace_period()
    {
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var loggedIn = new LogParserState { DisplayName = "a#1", BlockStartedUtc = now.AddMinutes(-10) };
        Assert.IsTrue(StatusTracker.MissingInventory(loggedIn, now.AddMinutes(-1), now));
        Assert.IsFalse(StatusTracker.MissingInventory(loggedIn, now.AddHours(-2), now), "an old archive is not a live session");
        Assert.IsFalse(StatusTracker.MissingInventory(new LogParserState { DisplayName = "a#1", BlockStartedUtc = now.AddMinutes(-1) }, now, now), "still within the grace period");
        Assert.IsFalse(StatusTracker.MissingInventory(new LogParserState { DisplayName = "a#1", BlockStartedUtc = now.AddMinutes(-10), SessionTimeUtc = now }, now, now));
        Assert.IsFalse(StatusTracker.MissingInventory(new LogParserState { BlockStartedUtc = now.AddMinutes(-10) }, now, now), "not logged in");
    }
}
