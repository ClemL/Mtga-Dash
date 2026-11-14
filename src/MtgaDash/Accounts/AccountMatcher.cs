namespace MtgaDash.Accounts;

public static class MatchStates
{
    public const string Matched = "Matched";
    public const string Exception = "Exception";
    public const string Unmatched = "Unmatched";
    public const string Ambiguous = "Ambiguous";
}

/// <summary>The outcome for one display name.</summary>
public sealed record PlayerMatch(string DisplayName, string State, string? Email, IReadOnlyList<string> Candidates);

/// <summary>The outcome for one account: which display name it is now tied to, if any.</summary>
public sealed record AccountMatch(string Email, string State, string? DisplayName);

public sealed record MatchResult(IReadOnlyList<PlayerMatch> Players, IReadOnlyList<AccountMatch> Accounts);

/// <summary>
/// Pure email ↔ display-name matching. Recomputed from scratch on every run, exceptions first, so an
/// exception can never be overwritten by an alias match and the outcome does not depend on history.
/// </summary>
public static class AccountMatcher
{
    /// <summary><c>base+Alias@gmail.com</c> → <c>alias</c>; no <c>+</c> means no alias.</summary>
    public static string? AliasOf(string email)
    {
        var at = email.IndexOf('@');
        var plus = email.IndexOf('+');
        if (plus < 0 || at < 0 || plus > at) return null;
        var alias = email[(plus + 1)..at].Trim().ToLowerInvariant();
        return alias.Length == 0 ? null : alias;
    }

    /// <summary><c>Allerclear#92603</c> → <c>allerclear</c>.</summary>
    public static string NamePartOf(string displayName)
    {
        var hash = displayName.IndexOf('#');
        return (hash >= 0 ? displayName[..hash] : displayName).Trim().ToLowerInvariant();
    }

    /// <param name="emails">Every account email.</param>
    /// <param name="players">Display names, most recently seen first, so an account matched by two names
    /// keeps the newest.</param>
    public static MatchResult Match(
        IEnumerable<string> emails,
        IEnumerable<string> players,
        IReadOnlyDictionary<string, string>? exceptions = null,
        IReadOnlySet<string>? excluded = null)
    {
        exceptions ??= AccountExceptions.DisplayNameToEmail;
        excluded ??= AccountExceptions.ExcludedEmails;

        var emailList = emails.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var canonical = emailList.ToDictionary(e => e, e => e, StringComparer.OrdinalIgnoreCase);
        var byAlias = emailList
            .Where(e => !excluded.Contains(e))
            .Select(e => (Email: e, Alias: AliasOf(e)))
            .Where(x => x.Alias is not null)
            .GroupBy(x => x.Alias!)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Email).OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList());

        var playerResults = new List<PlayerMatch>();
        var accountResults = new Dictionary<string, AccountMatch>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in players.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (exceptions.TryGetValue(name, out var forced))
            {
                var email = canonical.GetValueOrDefault(forced, forced);
                playerResults.Add(new PlayerMatch(name, MatchStates.Exception, email, [email]));
                // An exception beats any alias match recorded earlier for the same account.
                if (!accountResults.TryGetValue(email, out var prior) || prior.State != MatchStates.Exception)
                    accountResults[email] = new AccountMatch(email, MatchStates.Exception, name);
                continue;
            }

            var candidates = byAlias.GetValueOrDefault(NamePartOf(name)) ?? [];
            switch (candidates.Count)
            {
                case 1:
                    var email = candidates[0];
                    playerResults.Add(new PlayerMatch(name, MatchStates.Matched, email, candidates));
                    if (!accountResults.ContainsKey(email))
                        accountResults[email] = new AccountMatch(email, MatchStates.Matched, name);
                    break;
                case > 1:
                    playerResults.Add(new PlayerMatch(name, MatchStates.Ambiguous, null, candidates));
                    foreach (var c in candidates)
                        accountResults.TryAdd(c, new AccountMatch(c, MatchStates.Ambiguous, null));
                    break;
                default:
                    playerResults.Add(new PlayerMatch(name, MatchStates.Unmatched, null, []));
                    break;
            }
        }

        foreach (var email in emailList)
            accountResults.TryAdd(email, new AccountMatch(email, MatchStates.Unmatched, null));

        return new MatchResult(playerResults, accountResults.Values.OrderBy(a => a.Email, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>The line to paste into <see cref="AccountExceptions.DisplayNameToEmail"/>.</summary>
    public static string ExceptionLine(string displayName, string email) =>
        $"[\"{Escape(displayName)}\"] = \"{Escape(email)}\",";

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
