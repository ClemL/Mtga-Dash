using System.Diagnostics;

namespace MtgaDash.Web;

/// <summary>
/// Once the app is listening: writes the dashboard's address to the console and, when there is a desktop to
/// show it on, opens it in the default browser. Under the hidden logon task or a service a browser would
/// open nowhere, so it is skipped and the reason logged.
/// </summary>
public sealed class StartPage(MtgaDashOptions options, ILogger<StartPage> logger)
{
    /// <summary>Replaced in tests so no browser opens.</summary>
    internal Func<string, bool> Open { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }) is not null;

    internal Func<bool> HasDesktop { get; set; } = () => Environment.UserInteractive;

    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// When the browser was last opened, beside the database. The logon task restarts a crashed dashboard
    /// within a minute; without this every restart would open another tab.
    /// </summary>
    internal string MarkerPath => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(MtgaDashOptions.Resolve(options.DatabasePath)) ?? ".", "browser-opened.txt");

    private DateTime? LastOpenedUtc()
    {
        try
        {
            return File.Exists(MarkerPath) && DateTime.TryParse(File.ReadAllText(MarkerPath).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The address a person types: a wildcard bind (AllowLan) becomes localhost, and the configured page
    /// (default the Activity tab) is appended.
    /// </summary>
    internal static string Url(IEnumerable<string> listening, string startPage)
    {
        var first = listening.FirstOrDefault() ?? "http://localhost:5080";
        var uri = new UriBuilder(first);
        if (uri.Host is "0.0.0.0" or "[::]" or "::" or "+" or "*") uri.Host = "localhost";
        var page = string.IsNullOrWhiteSpace(startPage) ? "/" : startPage.Trim();
        if (!page.StartsWith('/')) page = "/" + page;
        return uri.Uri.GetLeftPart(UriPartial.Authority) + page;
    }

    /// <returns>The address, for the caller to log or test.</returns>
    public string Announce(IEnumerable<string> listening)
    {
        var url = Url(listening, options.StartPage);
        // Written plainly as well as logged, so it stands out in the console window and can be clicked there.
        Console.WriteLine();
        Console.WriteLine($"  MtgaDash is running: {url}");
        Console.WriteLine();
        if (!options.OpenBrowser) return url;
        if (!HasDesktop())
        {
            logger.LogInformation("Not opening a browser: no desktop (hidden logon task or service). Open {Url} yourself.", url);
            return url;
        }
        if (LastOpenedUtc() is { } last && UtcNow() - last < TimeSpan.FromMinutes(Math.Max(0, options.BrowserReopenMinutes)))
        {
            logger.LogInformation("Not opening another browser tab: one was opened {Minutes:N0} min ago (a restart?). Open {Url} if it is closed.",
                (UtcNow() - last).TotalMinutes, url);
            return url;
        }
        try
        {
            if (!Open(url)) logger.LogWarning("Windows did not open a browser for {Url}", url);
            else
            {
                try
                {
                    File.WriteAllText(MarkerPath, Data.Time.Format(UtcNow()));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Only costs an extra tab after the next restart.
                    logger.LogDebug("Could not write {Path}: {Message}", MarkerPath, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("Could not open a browser for {Url}: {Message}", url, ex.Message);
        }
        return url;
    }
}
