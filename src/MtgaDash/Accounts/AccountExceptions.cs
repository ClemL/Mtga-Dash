namespace MtgaDash.Accounts;

/// <summary>
/// Hand-maintained overrides for email ↔ display-name matching. Edit, rebuild and restart: matching re-runs
/// on startup and an entry here always wins over the alias rules. The Review tab prints the exact line to
/// paste for each unmatched or ambiguous display name.
/// </summary>
public static class AccountExceptions
{
    /// <summary>Full display name (case-insensitive) → email.</summary>
    public static readonly Dictionary<string, string> DisplayNameToEmail = new(StringComparer.OrdinalIgnoreCase)
    {
        // ["SomeName#12345"] = "clem.leung+ahk@gmail.com",
    };

    /// <summary>Accounts the alias rules must never match on their own. An exception entry above still can.</summary>
    public static readonly HashSet<string> ExcludedEmails = new(StringComparer.OrdinalIgnoreCase)
    {
        // "clem.leung@gmail.com",
    };
}
