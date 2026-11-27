using System.Text.Json;
using System.Text.RegularExpressions;
using MtgaDash.Web;

namespace MtgaDash.Tests;

[TestClass]
public sealed partial class PageTests
{
    private static string IndexHtml => File.ReadAllText(Path.Combine(Fixtures.RepoRoot(), "src", "MtgaDash", "wwwroot", "index.html"));

    [GeneratedRegex("""<script id="pure">(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex PureBlock();

    /// <summary>The page's pure helpers, extracted from index.html and run in Node with assertions.</summary>
    private static void RunPure(string assertions)
    {
        var pure = PureBlock().Match(IndexHtml).Groups[1].Value;
        Assert.IsFalse(string.IsNullOrWhiteSpace(pure), "index.html has a <script id=\"pure\"> block");
        Assert.AreEqual("ok", Node.Run($"""
            {pure}
            const P = module.exports;
            const assert = require("node:assert/strict");
            {assertions}
            console.log("ok");
            """));
    }

    [TestMethod]
    public void Changelog_parse_drops_blank_lines_and_carriage_returns()
    {
        RunPure("""
            assert.deepEqual(P.parseChangelog("a\r\n\r\n  b  \n   \nc"), ["a", "b", "c"]);
            assert.deepEqual(P.parseChangelog("\n\n   \n"), []);
            assert.deepEqual(P.parseChangelog(""), []);
            """);
        CollectionAssert.AreEqual(new[] { "a", "b" }, Changelog.Parse("a\r\n\r\n b \n").ToArray());
    }

    [TestMethod]
    public void Commands_match_every_term_across_group_and_label_in_any_order()
    {
        RunPure("""
            const cmds = [
              { id: "1", group: "Export", label: "Write the static export" },
              { id: "2", group: "Table", label: "Sort by gems" },
              { id: "3", group: "Data", label: "Re-import the accounts CSV" },
              { id: "4", group: "Table", label: "Sort by gold" },
            ];
            assert.deepEqual(P.filterCommands(cmds, "export").map(c => c.id), ["1"]);
            assert.deepEqual(P.filterCommands(cmds, "table gems").map(c => c.id), ["2"]);
            assert.deepEqual(P.filterCommands(cmds, "GEMS table").map(c => c.id), ["2"]);
            assert.deepEqual(P.filterCommands(cmds, "sort zzz"), []);
            """);
    }

    [TestMethod]
    public void Labels_starting_with_the_query_sort_first_then_registration_order()
    {
        RunPure("""
            const cmds = [
              { id: "a", group: "View", label: "Go to Stale" },
              { id: "b", group: "Stale", label: "Copy all stale emails" },
              { id: "c", group: "Table", label: "Stale filter" },
            ];
            assert.deepEqual(P.filterCommands(cmds, "stale").map(c => c.id), ["c", "a", "b"]);
            assert.deepEqual(P.filterCommands(cmds, "").map(c => c.id), ["a", "b", "c"]);
            assert.deepEqual(P.filterCommands(cmds, "   ").length, 3);
            """);
    }

    [TestMethod]
    public void Snippet_picks_the_matching_line_strips_markers_and_cuts_around_the_match()
    {
        RunPure("""
            assert.equal(P.snippet("first\n## Heading with **gold** here\nlast", "gold"), "Heading with gold here");
            assert.equal(P.snippet("> quoted line", "quoted"), "quoted line");
            assert.equal(P.snippet("- list item", "item"), "list item");
            assert.equal(P.snippet("1. numbered", "numbered"), "numbered");
            const long = "x".repeat(100) + " needle " + "y".repeat(100);
            const s = P.snippet(long, "needle", 40);
            assert.ok(s.startsWith("…") && s.endsWith("…"), s);
            assert.ok(s.includes("needle"), s);
            assert.ok(s.length <= 42, String(s.length));
            assert.ok(!P.snippet("needle " + "y".repeat(100), "needle", 40).startsWith("…"));
            assert.equal(P.snippet("no match here", "zzz"), "no match here");
            """);
    }

    [TestMethod]
    public void Account_matcher_and_login_classes()
    {
        RunPure("""
            const a = { email: "x+Allerclear@gmail.com", displayName: "Allerclear#92603", status: "Active", notes: "Has rares" };
            assert.ok(P.matchesAccount(a, "allerclear rares"));
            assert.ok(P.matchesAccount(a, ""));
            assert.ok(!P.matchesAccount(a, "allerclear zzz"));
            const now = Date.parse("2026-11-14T00:00:00Z");
            assert.equal(P.loginClass("2026-11-10T00:00:00Z", now, 30), "fresh");
            assert.equal(P.loginClass("2026-10-20T00:00:00Z", now, 30), "aging");
            assert.equal(P.loginClass("2026-09-01T00:00:00Z", now, 30), "old");
            assert.equal(P.loginClass(null, now, 30), "never");
            assert.ok(P.isStale(null, now, 30));
            assert.ok(!P.isStale("2026-11-10T00:00:00Z", now, 30));
            assert.equal(P.relativeTime("2026-11-11T00:00:00Z", now), "3d ago");
            """);
    }

    [TestMethod]
    public void Card_filters_by_type_lands_and_alchemy()
    {
        RunPure("""
            const any = { type: "any", ignoreLands: false, alchemy: "include" };
            const creature = { types: "Legendary Creature — Elf Druid", alchemy: false };
            const land = { types: "Land", alchemy: false };
            const dfcLand = { types: "Legendary Artifact Land", alchemy: false };
            const alch = { types: "Instant", alchemy: true };
            const unknown = { types: null, alchemy: false };
            assert.ok(P.cardMatches(creature, any));
            assert.ok(!P.cardMatches(land, { ...any, ignoreLands: true }));
            assert.ok(!P.cardMatches(dfcLand, { ...any, ignoreLands: true }));
            assert.ok(P.cardMatches(unknown, { ...any, ignoreLands: true }), "unknown type is not a land");
            assert.ok(P.cardMatches(creature, { ...any, type: "creature" }));
            assert.ok(!P.cardMatches(alch, { ...any, type: "creature" }));
            assert.ok(!P.cardMatches({ types: "Enchantment Creature" , alchemy: false }, { ...any, type: "artifact" }));
            assert.ok(P.cardMatches(unknown, { ...any, type: "creature" }), "no type line: the type filter cannot apply");
            assert.ok(!P.cardMatches(alch, { ...any, alchemy: "exclude" }));
            assert.ok(P.cardMatches(alch, { ...any, alchemy: "only" }));
            assert.ok(!P.cardMatches(creature, { ...any, alchemy: "only" }));
            """);
    }

    [TestMethod]
    public void Holders_group_four_or_more_then_three_two_one()
    {
        RunPure("""
            const g = P.groupByCopies([{ copies: 6 }, { copies: 4 }, { copies: 2 }, { copies: 1 }, { copies: 2 }]);
            assert.deepEqual(g.map(x => [x.copies, x.holders.length]), [[4, 2], [2, 2], [1, 1]]);
            assert.deepEqual(P.groupByCopies([]), []);
            """);
    }

    [TestMethod]
    public void An_accounts_hover_shows_its_3_plus_copy_cards_4s_and_images_first_after_the_filters()
    {
        RunPure("""
            const f = { type: "any", ignoreLands: true, alchemy: "include" };
            const card = (name, copies, extra) => Object.assign({ cardId: name.length, name, types: "Creature", holders: [{ displayName: "a#1", copies }, { displayName: "b#2", copies: 4 }] }, extra);
            const cards = [
              card("Three no image", 3), card("Three with image", 3, { image: "/i/3.jpg" }), card("Four", 4),
              card("Two", 2), card("Land four", 4, { types: "Land" }), card("Six", 6, { image: "/i/6.jpg" }),
            ];
            const top = P.topCards(cards, "a#1", f, 3);
            assert.equal(top.total, 4, "the 2-of and the land are left out");
            assert.deepEqual(top.cards.map(e => e.card.name), ["Six", "Four", "Three with image"]);
            assert.equal(P.topCards(cards, "nobody", f, 8).total, 0);
            assert.equal(P.topCards(null, "a#1", f, 8).total, 0);
            """);
    }

    [TestMethod]
    public void Usage_labels_and_paths_carry_no_account_or_value()
    {
        RunPure("""
            assert.equal(P.usageLabel("Copy 12 emails"), "Copy # emails");
            assert.equal(P.usageLabel("  Copy  base1+x@example.com  "), "Copy <email>");
            assert.equal(P.usageLabel("Open Corsair#10001"), "Open <player>");
            assert.equal(P.usageLabel(null), "");
            assert.equal(P.usageLabel("4", true), "4", "a menu's value keeps its digits");
            assert.equal(P.usageLabel("x@example.com", true), "<email>");
            assert.equal(P.usageLabel("x".repeat(60)).length, 40);
            assert.equal(P.routePattern("/api/accounts/base1%2Bx%40example.com"), "/api/accounts/{value}");
            assert.equal(P.routePattern("/api/cards/123/holders"), "/api/cards/{value}/holders");
            assert.equal(P.routePattern("/api/cards?minCopies=3&rarity=mythic,rare"), "/api/cards");
            assert.equal(P.routePattern("/api/decknames/hidden/Keep%20the%20Peace"), "/api/decknames/hidden/{value}");
            """);
    }

    [TestMethod]
    public void Preview_cards_are_as_large_as_fit_and_never_past_the_image_size()
    {
        RunPure("""
            const chrome = { x: 40, y: 64 };
            assert.equal(P.previewCardWidth(1, 3840, 2160, chrome), 488, "one card on a big screen: its own size");
            const eight = P.previewCardWidth(8, 1920, 1080, chrome);
            assert.ok(eight * 4 + 3 * 8 + 40 <= 1920 && (eight * 680 / 488) * 2 + 8 + 64 <= 1080, "8 cards fit 1920x1080: " + eight);
            assert.ok(eight > 300, "and are still large: " + eight);
            assert.equal(P.previewCardWidth(3, 10000, 10000, chrome), 488);
            assert.ok(P.previewCardWidth(8, 390, 700, chrome) >= 60);
            """);
    }

    private static string Boot(string stored, string storage = "ok") => Node.Run($$"""
        const vm = require("node:vm");
        const stored = {{stored}};
        const run = { dataset: {}, meta: "unset", listeners: 0 };
        const localStorage = { getItem(k) { if ("{{storage}}" === "getItem-throws") throw new Error("denied"); return k in stored ? stored[k] : null; } };
        const window = "{{storage}}" === "blocked"
          ? Object.defineProperty({}, "localStorage", { get() { throw new Error("SecurityError"); } })
          : { localStorage };
        const document = {
          documentElement: { dataset: run.dataset },
          querySelectorAll: () => [{ setAttribute: (_, v) => { run.meta = v; } }],
          addEventListener: () => { run.listeners++; },
        };
        vm.runInNewContext({{JsonSerializer.Serialize(DisplayOptions.BootScript())}}, { window, document });
        console.log(JSON.stringify(run));
        """);

    [TestMethod]
    public void Boot_script_applies_saved_settings_and_the_theme_color()
    {
        var json = Boot($$"""{ "{{DisplayOptions.ThemeKey}}": "oled", "{{DisplayOptions.DensityKey}}": "cozy", "{{DisplayOptions.TextSizeKey}}": "130" }""");
        using var doc = JsonDocument.Parse(json);
        var ds = doc.RootElement.GetProperty("dataset");
        Assert.AreEqual("oled", ds.GetProperty("theme").GetString());
        Assert.AreEqual("cozy", ds.GetProperty("density").GetString());
        Assert.AreEqual("130", ds.GetProperty("text").GetString());
        Assert.AreEqual("#000000", doc.RootElement.GetProperty("meta").GetString());
    }

    [TestMethod]
    public void Boot_script_ignores_defaults_and_unknown_values()
    {
        var json = Boot($$"""{ "{{DisplayOptions.ThemeKey}}": "constructor", "{{DisplayOptions.DensityKey}}": "<b>", "{{DisplayOptions.TextSizeKey}}": "999" }""");
        StringAssert.StartsWith(json, """{"dataset":{},"meta":"unset""");
        json = Boot($$"""{ "{{DisplayOptions.ThemeKey}}": "system", "{{DisplayOptions.DensityKey}}": "normal", "{{DisplayOptions.TextSizeKey}}": "100" }""");
        StringAssert.StartsWith(json, """{"dataset":{},"meta":"unset""");
    }

    [TestMethod]
    [DataRow("getItem-throws")]
    [DataRow("blocked")]
    public void Boot_script_survives_storage_that_throws(string storage)
    {
        var json = Boot($$"""{ "{{DisplayOptions.ThemeKey}}": "oled" }""", storage);
        StringAssert.StartsWith(json, """{"dataset":{}""");
    }

    [TestMethod]
    public void Template_has_each_placeholder_exactly_once()
    {
        var html = IndexHtml;
        foreach (var marker in new[] { "<!--BOOT-->", "/*CONFIG*/null", "/*DATA*/null" })
            Assert.AreEqual(1, Regex.Matches(html, Regex.Escape(marker)).Count, marker);
    }

    [TestMethod]
    public void Every_css_font_size_scales_with_the_text_setting()
    {
        var style = Regex.Match(IndexHtml, "<style>(.*?)</style>", RegexOptions.Singleline).Groups[1].Value;
        var fixedSizes = Regex.Matches(style, @"font-size:\s*([^;]+);").Select(m => m.Groups[1].Value.Trim())
            .Where(v => v != "inherit" && !Regex.IsMatch(v, @"^calc\(\d+(\.\d+)?px \* var\(--text-scale\)\)$"))
            .ToList();
        Assert.AreEqual(0, fixedSizes.Count, string.Join("\n", fixedSizes));
    }

    [TestMethod]
    public void The_changelog_has_well_formed_entries_oldest_first()
    {
        var lines = Changelog.Parse(File.ReadAllText(Path.Combine(Fixtures.RepoRoot(), "src", "MtgaDash", "wwwroot", "updates.txt")));
        Assert.IsTrue(lines.Count > 0);
        foreach (var l in lines) StringAssert.Matches(l, new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:00Z - \S"));
        CollectionAssert.AreEqual(lines.OrderBy(l => l[..17], StringComparer.Ordinal).ToList(), lines.ToList());
    }
}
