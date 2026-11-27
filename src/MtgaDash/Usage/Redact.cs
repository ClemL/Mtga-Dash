using System.Text.RegularExpressions;

namespace MtgaDash.Usage;

/// <summary>
/// Removes what identifies an account from text bound for the usage report: emails and MTGA display names
/// ("Name#12345"). The report is meant to be pasted into an LLM, so it must hold neither.
/// </summary>
public static partial class Redact
{
    [GeneratedRegex(@"[^\s@<>""'(),;]+@[^\s@<>""'(),;]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"[^\s#<>""'(),;:]{1,40}#\d{1,6}\b")]
    private static partial Regex DisplayName();

    [GeneratedRegex(@"[A-Za-z]:\\[^\s""']*|/(home|Users)/[^\s""']*")]
    private static partial Regex LocalPath();

    public static string Text(string? text, int max = 200)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = Email().Replace(text, "<email>");
        t = DisplayName().Replace(t, "<player>");
        t = LocalPath().Replace(t, "<path>");
        t = t.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
        return t.Length <= max ? t : t[..max] + "…";
    }
}
