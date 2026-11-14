using MtgaDash.Accounts;

namespace MtgaDash.Tests;

[TestClass]
public sealed class MatcherTests
{
    private static readonly Dictionary<string, string> NoExceptions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> NoExclusions = new(StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    [DataRow("base1+Allerclear@example.com", "allerclear")]
    [DataRow("base2+AHK@example.com", "ahk")]
    [DataRow("plain@gmail.com", null)]
    [DataRow("a+@gmail.com", null)]
    public void Alias_is_between_plus_and_at_lowercased(string email, string? alias) =>
        Assert.AreEqual(alias, AccountMatcher.AliasOf(email));

    [TestMethod]
    public void NamePart_is_before_hash_lowercased() =>
        Assert.AreEqual("allerclear", AccountMatcher.NamePartOf("Allerclear#92603"));

    [TestMethod]
    public void Unique_alias_matches_case_insensitively()
    {
        var r = AccountMatcher.Match(["base1+Allerclear@example.com", "x+winnie@gmail.com"], ["ALLERCLEAR#92603"], NoExceptions, NoExclusions);
        var p = r.Players.Single();
        Assert.AreEqual((MatchStates.Matched, "base1+Allerclear@example.com"), (p.State, p.Email));
        Assert.AreEqual("ALLERCLEAR#92603", r.Accounts.Single(a => a.Email.Contains("allerclear", StringComparison.OrdinalIgnoreCase)).DisplayName);
        Assert.AreEqual(MatchStates.Unmatched, r.Accounts.Single(a => a.Email.StartsWith("x+")).State);
    }

    [TestMethod]
    public void Shared_alias_across_base_addresses_is_ambiguous()
    {
        var r = AccountMatcher.Match(["base1+corsair@example.com", "base2+corsair@example.com"], ["Corsair#1"], NoExceptions, NoExclusions);
        var p = r.Players.Single();
        Assert.AreEqual(MatchStates.Ambiguous, p.State);
        Assert.IsNull(p.Email);
        Assert.AreEqual(2, p.Candidates.Count);
        Assert.IsTrue(r.Accounts.All(a => a.State == MatchStates.Ambiguous && a.DisplayName is null));
    }

    [TestMethod]
    public void Exception_map_overrides_alias_and_ambiguity()
    {
        var exceptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["corsair#1"] = "Base2+Corsair@example.com" };
        var r = AccountMatcher.Match(["base1+corsair@example.com", "base2+corsair@example.com"], ["Corsair#1"], exceptions, NoExclusions);
        var p = r.Players.Single();
        Assert.AreEqual(MatchStates.Exception, p.State);
        Assert.AreEqual("base2+corsair@example.com", p.Email, "the stored email's own casing is kept");
        Assert.AreEqual(MatchStates.Exception, r.Accounts.Single(a => a.Email.StartsWith("base2")).State);
    }

    [TestMethod]
    public void Exception_wins_even_when_an_alias_match_came_first()
    {
        var exceptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Someone#9"] = "a+packs@gmail.com" };
        var r = AccountMatcher.Match(["a+packs@gmail.com"], ["Packs#1", "Someone#9"], exceptions, NoExclusions);
        var account = r.Accounts.Single();
        Assert.AreEqual((MatchStates.Exception, "Someone#9"), (account.State, account.DisplayName));
    }

    [TestMethod]
    public void No_alias_email_and_unknown_name_stay_unmatched()
    {
        var r = AccountMatcher.Match(["plain@gmail.com"], ["plain#1"], NoExceptions, NoExclusions);
        Assert.AreEqual(MatchStates.Unmatched, r.Players.Single().State);
        Assert.AreEqual(MatchStates.Unmatched, r.Accounts.Single().State);
    }

    [TestMethod]
    public void Excluded_emails_are_never_matched_by_alias()
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A+Main@gmail.com" };
        var r = AccountMatcher.Match(["a+main@gmail.com", "b+main@gmail.com"], ["main#1"], NoExceptions, excluded);
        Assert.AreEqual((MatchStates.Matched, "b+main@gmail.com"), (r.Players.Single().State, r.Players.Single().Email));
    }

    [TestMethod]
    public void Exception_line_is_valid_csharp() =>
        Assert.AreEqual("[\"Name#12\"] = \"a+b@gmail.com\",", AccountMatcher.ExceptionLine("Name#12", "a+b@gmail.com"));
}
