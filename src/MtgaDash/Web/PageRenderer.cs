using System.Text.Json;

namespace MtgaDash.Web;

/// <summary>
/// Serves wwwroot/index.html with three placeholders filled: the display-settings boot script, the page
/// config (build info, settings lists) and, for the static export only, the embedded data. The page is a
/// template rather than a static file so the build info is stamped once, by the build, never formatted
/// in the browser.
/// </summary>
public sealed class PageRenderer(IWebHostEnvironment env, MtgaDashOptions options)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string BootMarker = "<!--BOOT-->";
    private const string ConfigMarker = "/*CONFIG*/null";
    private const string DataMarker = "/*DATA*/null";

    public string Template => File.ReadAllText(Path.Combine(env.WebRootPath, "index.html"));

    public string Render(object? embeddedData = null, IReadOnlyList<string>? changelog = null)
    {
        var config = new
        {
            appName = BuildInfo.AppName,
            version = BuildInfo.Version,
            buildTime = BuildInfo.BuildTime,
            staleDays = options.StaleDays,
            readOnly = embeddedData is not null,
            // Only the static export embeds the changelog; the live page fetches /updates.txt.
            changelog,
            keys = new { theme = DisplayOptions.ThemeKey, density = DisplayOptions.DensityKey, text = DisplayOptions.TextSizeKey },
            themes = DisplayOptions.Themes,
            densities = DisplayOptions.Densities,
            textSizes = DisplayOptions.TextSizes,
        };
        // The default encoder escapes <, > and &, so embedded JSON cannot close the <script> it sits in.
        return Template
            .Replace(BootMarker, $"<script>{DisplayOptions.BootScript()}</script>")
            .Replace(ConfigMarker, JsonSerializer.Serialize(config, JsonOptions))
            .Replace(DataMarker, embeddedData is null ? "null" : JsonSerializer.Serialize(embeddedData, JsonOptions));
    }

    public IReadOnlyList<string> ReadChangelog()
    {
        var path = Path.Combine(env.WebRootPath, "updates.txt");
        return File.Exists(path) ? Changelog.Parse(File.ReadAllText(path)) : [];
    }
}

public static class Changelog
{
    /// <summary>One entry per line, oldest first; blank lines and <c>\r</c> dropped. Mirrors the page's own parser.</summary>
    public static IReadOnlyList<string> Parse(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}
