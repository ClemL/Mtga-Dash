namespace MtgaDash;

/// <summary>The <c>MtgaDash</c> section of appsettings.json. Paths may contain %ENVIRONMENT% variables.</summary>
public sealed class MtgaDashOptions
{
    public const string Section = "MtgaDash";

    public string LogFolder { get; set; } = @"C:\dev\mtga\logs";
    public string LiveLogPath { get; set; } = @"%USERPROFILE%\AppData\LocalLow\Wizards Of The Coast\MTGA\Player.log";
    public bool WatchLiveLog { get; set; } = true;

    /// <summary>Also read Player-prev.log beside the live log: the previous game session, kept until the next start.</summary>
    public bool WatchPrevLog { get; set; } = true;

    /// <summary>Which files in LogFolder are logs. The archiver's own <c>archiver_*.log</c> files are not.</summary>
    public string LogFilePattern { get; set; } = "Player*.log";
    public int PollSeconds { get; set; } = 5;
    public string DatabasePath { get; set; } = @"C:\dev\mtga\data\mtga.db";
    public string AccountsCsvPath { get; set; } = @"C:\dev\mtga\data\mtga_accounts_20261112.csv";
    public int StaleDays { get; set; } = 30;
    public string ExportFolder { get; set; } = "";
    public bool AllowLan { get; set; }
    public int Port { get; set; } = 5080;

    /// <summary>
    /// Optional path to MTGA's card database, for card names. May end in a wildcard; the newest match wins,
    /// because MTGA writes a new <c>Raw_CardDatabase_&lt;hash&gt;.mtga</c> with each update.
    /// </summary>
    public string CardDbPath { get; set; } = "";

    /// <summary>Where card images are kept. Empty means <c>card-images</c> beside the database.</summary>
    public string CardImageFolder { get; set; } = "";

    /// <summary>The Scryfall API the image downloader asks; changed only for testing.</summary>
    public string CardImageApi { get; set; } = "https://api.scryfall.com";

    /// <summary>
    /// Where Review-tab exception mappings are saved. Empty means <c>account-exceptions.json</c> beside the
    /// database, outside the publish folder so republishing cannot erase it.
    /// </summary>
    public string ExceptionsPath { get; set; } = "";

    /// <summary>Review-tab alert: gems fell by at least this much between two snapshots of one account.</summary>
    public int AlertGemsDrop { get; set; } = 1000;

    /// <summary>Review-tab alert: gold fell by at least this much.</summary>
    public int AlertGoldDrop { get; set; } = 5000;

    /// <summary>Review-tab alert: rare and mythic wildcards together fell by at least this many.</summary>
    public int AlertWildcardDrop { get; set; } = 2;

    /// <summary>How far back the Review tab looks for drops.</summary>
    public int AlertDays { get; set; } = 30;

    /// <summary>Open the dashboard in the default browser once it is listening (only where there is a desktop).</summary>
    public bool OpenBrowser { get; set; } = true;

    /// <summary>Do not open the browser again within this many minutes, so a crash restart adds no tab.</summary>
    public int BrowserReopenMinutes { get; set; } = 30;

    /// <summary>The page opened at startup and written to the console; the Activity tab by default.</summary>
    public string StartPage { get; set; } = "/#/activity";

    /// <summary>Start MTGA when the dashboard starts, unless it is already running.</summary>
    public bool LaunchGame { get; set; }

    /// <summary>
    /// The game to start: MTGA.exe for the standalone install, or a launcher URI such as
    /// <c>steam://rungameid/2141910</c> for Steam.
    /// </summary>
    public string GamePath { get; set; } = @"C:\Program Files\Wizards of the Coast\MTGA\MTGA.exe";

    /// <summary>Expands environment variables and makes the path absolute. Empty stays empty.</summary>
    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        // %USERPROFILE% is unset off Windows; fall back to the home folder so tests and Linux runs still resolve.
        if (expanded.Contains("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
            expanded = expanded.Replace("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase);
        return Path.GetFullPath(expanded);
    }
}
