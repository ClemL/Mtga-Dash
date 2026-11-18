using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MtgaDash.Game;

namespace MtgaDash.Tests;

/// <summary>Every test replaces the process start, so nothing is ever launched.</summary>
[TestClass]
public sealed class GameLauncherTests
{
    private static (GameLauncher Launcher, List<ProcessStartInfo> Started) Make(string path, bool launchGame = true, bool desktop = true, bool running = false)
    {
        var started = new List<ProcessStartInfo>();
        var launcher = new GameLauncher(new MtgaDashOptions { LaunchGame = launchGame, GamePath = path }, NullLogger<GameLauncher>.Instance)
        {
            Start = psi => { started.Add(psi); return true; },
            HasDesktop = () => desktop,
            IsRunning = () => running,
        };
        return (launcher, started);
    }

    private static string FakeGame(TempDb t)
    {
        var path = t.File("MTGA.exe");
        File.WriteAllText(path, "");
        return path;
    }

    [TestMethod]
    public void Starts_the_game_with_its_own_folder_as_working_directory()
    {
        using var t = new TempDb();
        var (launcher, started) = Make(FakeGame(t));
        var (ok, message) = launcher.Launch(onStartup: true);
        Assert.IsTrue(ok, message);
        Assert.AreEqual(t.Folder, started.Single().WorkingDirectory);
        Assert.IsTrue(started.Single().UseShellExecute);
    }

    [TestMethod]
    public void Never_starts_a_second_copy()
    {
        using var t = new TempDb();
        var (launcher, started) = Make(FakeGame(t), running: true);
        Assert.AreEqual("MTGA is already running", launcher.Launch(onStartup: true).Message);
        Assert.AreEqual(0, started.Count);
    }

    [TestMethod]
    public void Refuses_without_a_desktop_and_says_how_to_fix_it()
    {
        using var t = new TempDb();
        var (launcher, started) = Make(FakeGame(t), desktop: false);
        var (ok, message) = launcher.Launch(onStartup: true);
        Assert.IsFalse(ok);
        StringAssert.Contains(message, "-Interactive");
        Assert.AreEqual(0, started.Count);
        Assert.IsFalse(launcher.Status().CanLaunch);
    }

    [TestMethod]
    public void A_missing_game_is_reported_not_started()
    {
        using var t = new TempDb();
        var (launcher, started) = Make(t.File("nope.exe"));
        StringAssert.Contains(launcher.Launch().Message, "MTGA was not found");
        Assert.AreEqual(0, started.Count);
    }

    [TestMethod]
    public void At_startup_it_waits_for_the_setting_but_the_button_does_not()
    {
        using var t = new TempDb();
        var (launcher, started) = Make(FakeGame(t), launchGame: false);
        Assert.IsFalse(launcher.Launch(onStartup: true).Started);
        Assert.IsTrue(launcher.Launch(onStartup: false).Started, "Start MTGA on the Status tab works with LaunchGame off");
        Assert.AreEqual(1, started.Count);
    }

    [TestMethod]
    public void A_launcher_uri_is_passed_to_windows_as_is()
    {
        var (launcher, started) = Make("steam://rungameid/2141910");
        Assert.IsTrue(launcher.Launch().Started);
        Assert.AreEqual("steam://rungameid/2141910", started.Single().FileName);
        Assert.IsTrue(GameLauncher.IsUri("steam://rungameid/2141910"));
        Assert.IsFalse(GameLauncher.IsUri(@"C:\Program Files\Wizards of the Coast\MTGA\MTGA.exe"));
    }
}
