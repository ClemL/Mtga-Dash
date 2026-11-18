using Microsoft.Extensions.Logging.Abstractions;
using MtgaDash.Web;

namespace MtgaDash.Tests;

/// <summary>Every test replaces the browser opener, so nothing opens.</summary>
[TestClass]
public sealed class StartPageTests
{
    [TestMethod]
    [DataRow("http://localhost:5080", "/#/activity", "http://localhost:5080/#/activity")]
    [DataRow("http://0.0.0.0:5080", "/#/activity", "http://localhost:5080/#/activity")]
    [DataRow("http://127.0.0.1:51234", "#/status", "http://127.0.0.1:51234/#/status")]
    [DataRow("http://localhost:5090", "", "http://localhost:5090/")]
    public void The_address_is_one_a_person_can_open(string listening, string page, string expected) =>
        Assert.AreEqual(expected, StartPage.Url([listening], page));

    private static (StartPage Page, List<string> Opened) Make(bool openBrowser = true, bool desktop = true, string? folder = null)
    {
        var opened = new List<string>();
        folder ??= Path.Combine(Path.GetTempPath(), "mtgadash-startpage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var page = new StartPage(new MtgaDashOptions { OpenBrowser = openBrowser, DatabasePath = Path.Combine(folder, "mtga.db") }, NullLogger<StartPage>.Instance)
        {
            Open = url => { opened.Add(url); return true; },
            HasDesktop = () => desktop,
        };
        return (page, opened);
    }

    [TestMethod]
    public void Opens_the_activity_tab_and_writes_it_to_the_console()
    {
        var (page, opened) = Make();
        var console = new StringWriter();
        var original = Console.Out;
        Console.SetOut(console);
        try
        {
            page.Announce(["http://localhost:5080"]);
        }
        finally
        {
            Console.SetOut(original);
        }
        CollectionAssert.AreEqual(new[] { "http://localhost:5080/#/activity" }, opened);
        StringAssert.Contains(console.ToString(), "MtgaDash is running: http://localhost:5080/#/activity");
    }

    [TestMethod]
    public void No_browser_without_a_desktop_or_when_turned_off()
    {
        var (hidden, openedHidden) = Make(desktop: false);
        hidden.Announce(["http://localhost:5080"]);
        Assert.AreEqual(0, openedHidden.Count);

        var (off, openedOff) = Make(openBrowser: false);
        Assert.AreEqual("http://localhost:5080/#/activity", off.Announce(["http://localhost:5080"]), "the address is still written out");
        Assert.AreEqual(0, openedOff.Count);
    }

    [TestMethod]
    public void A_restart_soon_after_does_not_open_another_tab()
    {
        using var t = new TempDb();
        var now = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
        var (first, opened) = Make(folder: t.Folder);
        first.UtcNow = () => now;
        first.Announce(["http://localhost:5080"]);
        Assert.AreEqual(1, opened.Count);

        var (crashRestart, openedAgain) = Make(folder: t.Folder);
        crashRestart.UtcNow = () => now.AddMinutes(1);
        crashRestart.Announce(["http://localhost:5080"]);
        Assert.AreEqual(0, openedAgain.Count);

        var (nextDay, openedNextDay) = Make(folder: t.Folder);
        nextDay.UtcNow = () => now.AddDays(1);
        nextDay.Announce(["http://localhost:5080"]);
        Assert.AreEqual(1, openedNextDay.Count);
    }
}
