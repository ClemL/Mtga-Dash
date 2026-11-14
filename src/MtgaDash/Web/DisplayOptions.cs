using System.Text.Json;

namespace MtgaDash.Web;

public sealed record Choice(string Id, string Label, string Tip, string? Color = null);

/// <summary>
/// The display settings as data: the boot script, the Settings menu and the tests all read these lists.
/// The first entry of each list is the default and is stored as "no attribute", so the base stylesheet
/// is the default look. Adding a theme means an entry here and a CSS block in index.html.
/// </summary>
public static class DisplayOptions
{
    public const string ThemeKey = "mtgadash:theme";
    public const string DensityKey = "mtgadash:density";
    public const string TextSizeKey = "mtgadash:text-size";

    /// <summary><c>Color</c> is the browser's theme color (the Android address bar), matching the header.</summary>
    public static readonly Choice[] Themes =
    [
        new("system", "System", "Light or dark, following the device", null),
        new("light", "Light", "Plain light, in the system sans-serif", "#f6f7f9"),
        new("dark", "Dark", "Dark grey panels for evening use", "#15181d"),
        new("oled", "OLED", "True black for AMOLED screens, with dimmed text", "#000000"),
        new("contrast", "High contrast", "Black on white, strong borders and focus rings", "#ffffff"),
    ];

    public static readonly Choice[] Densities =
    [
        new("normal", "Normal", "The default spacing"),
        new("compact", "Compact", "Tighter spacing, more rows on screen"),
        new("cozy", "Cozy", "Roomier spacing and larger touch targets"),
    ];

    public static readonly Choice[] TextSizes =
    [
        new("100", "100%", "The default text size"),
        new("90", "90%", "Smaller text"),
        new("115", "115%", "Larger text"),
        new("130", "130%", "Largest text"),
    ];

    /// <summary>
    /// The inline script that applies saved settings before first paint. Only ids from the allowlists apply,
    /// so a stale or tampered value sets nothing; each read has its own try, so one bad key costs only itself.
    /// </summary>
    public static string BootScript()
    {
        static string J(object o) => JsonSerializer.Serialize(o);
        var themes = Themes.Skip(1).Select(t => t.Id).ToArray();
        var colors = Themes.Skip(1).ToDictionary(t => t.Id, t => t.Color);
        var densities = Densities.Skip(1).Select(d => d.Id).ToArray();
        var sizes = TextSizes.Skip(1).Select(t => t.Id).ToArray();
        return string.Concat(
            "(function(){var d=document.documentElement,s;",
            "try{s=window.localStorage}catch(e){return}",
            "function g(k){try{return s.getItem(k)}catch(e){return null}}",
            $"var t=g({J(ThemeKey)});if({J(themes)}.indexOf(t)>=0){{d.dataset.theme=t;",
            // The theme-color tags come after this script in <head>, so set them again once it is parsed.
            $"var c={J(colors)}[t],m=function(){{var e=document.querySelectorAll('meta[name=\"theme-color\"]');for(var i=0;i<e.length;i++)e[i].setAttribute('content',c)}};",
            "m();document.addEventListener('DOMContentLoaded',m)}",
            $"var n=g({J(DensityKey)});if({J(densities)}.indexOf(n)>=0)d.dataset.density=n;",
            $"var z=g({J(TextSizeKey)});if({J(sizes)}.indexOf(z)>=0)d.dataset.text=z;",
            "})()");
    }
}
