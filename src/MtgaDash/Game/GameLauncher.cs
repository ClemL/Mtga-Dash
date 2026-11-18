using System.Diagnostics;

namespace MtgaDash.Game;

public enum LaunchDecision
{
    Disabled,
    NoPath,
    PathNotFound,
    NoDesktop,
    AlreadyRunning,
    Start,
}

public sealed record GameStatus(bool Enabled, string Path, bool Running, string? LastResult, string? LastResultUtc, bool CanLaunch, string? CannotLaunchReason);

/// <summary>
/// Starts MTGA when the dashboard starts (setting <c>LaunchGame</c>) or on request, so one action begins a
/// play session with collection running. A game started without a desktop (the hidden S4U logon task, a
/// Windows service) would run invisibly, so it is refused there with the reason, never started blind.
/// </summary>
public sealed class GameLauncher(MtgaDashOptions options, ILogger<GameLauncher> logger) : IHostedService
{
    /// <summary>MTGA's process name, the same for the standalone, Steam and Epic installs.</summary>
    public const string ProcessName = "MTGA";

    private readonly Lock _gate = new();
    private string? _lastResult;
    private DateTime? _lastResultUtc;

    /// <summary>Starts the process; replaced in tests so nothing is launched.</summary>
    internal Func<ProcessStartInfo, bool> Start { get; set; } = psi => Process.Start(psi) is not null;

    internal Func<bool> IsRunning { get; set; } = () =>
    {
        var found = Process.GetProcessesByName(ProcessName);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    };

    /// <summary>Whether this process can show a window: false under a service or a hidden (S4U) task.</summary>
    internal Func<bool> HasDesktop { get; set; } = () => Environment.UserInteractive;

    public string Path => options.GamePath.Trim();

    /// <summary>A URI such as <c>steam://rungameid/2141910</c> is handed to Windows as is; anything else is a file.</summary>
    internal static bool IsUri(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme.Length > 1;

    internal LaunchDecision Decide(bool onStartup)
    {
        if (onStartup && !options.LaunchGame) return LaunchDecision.Disabled;
        if (Path.Length == 0) return LaunchDecision.NoPath;
        if (!IsUri(Path) && !File.Exists(MtgaDashOptions.Resolve(Path))) return LaunchDecision.PathNotFound;
        if (!HasDesktop()) return LaunchDecision.NoDesktop;
        if (IsRunning()) return LaunchDecision.AlreadyRunning;
        return LaunchDecision.Start;
    }

    internal static string Describe(LaunchDecision d, string path) => d switch
    {
        LaunchDecision.Disabled => "LaunchGame is off",
        LaunchDecision.NoPath => "GamePath is empty",
        LaunchDecision.PathNotFound => $"MTGA was not found at {path}; set GamePath in appsettings",
        LaunchDecision.NoDesktop => "the dashboard is running without a desktop (hidden task or service), where the game window would be invisible; register the logon task with -Interactive",
        LaunchDecision.AlreadyRunning => "MTGA is already running",
        LaunchDecision.Start => "MTGA started",
        _ => d.ToString(),
    };

    /// <summary>Starts the game if it can and should; returns what happened, in words.</summary>
    public (bool Started, string Message) Launch(bool onStartup = false)
    {
        lock (_gate)
        {
            var decision = Decide(onStartup);
            var started = false;
            var message = Describe(decision, Path);
            if (decision == LaunchDecision.Start)
            {
                try
                {
                    var target = IsUri(Path) ? Path : MtgaDashOptions.Resolve(Path);
                    started = Start(new ProcessStartInfo(target)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = IsUri(Path) ? "" : System.IO.Path.GetDirectoryName(target) ?? "",
                    });
                    message = started ? $"MTGA started from {target}" : $"Windows did not start {target}";
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    message = $"Starting MTGA failed: {ex.Message}";
                }
            }
            if (decision != LaunchDecision.Disabled)
            {
                _lastResult = message;
                _lastResultUtc = DateTime.UtcNow;
                if (decision is LaunchDecision.Start or LaunchDecision.AlreadyRunning) logger.LogInformation("Game: {Message}", message);
                else logger.LogWarning("Game: {Message}", message);
            }
            return (started, message);
        }
    }

    public GameStatus Status()
    {
        var reason = Decide(onStartup: false) switch
        {
            LaunchDecision.Start or LaunchDecision.AlreadyRunning => null,
            var d => Describe(d, Path),
        };
        lock (_gate)
            return new GameStatus(options.LaunchGame, Path, IsRunning(), _lastResult, Data.Time.Format(_lastResultUtc), reason is null, reason);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // After the host is up, so a slow game start never delays the dashboard.
        if (options.LaunchGame) _ = Task.Run(() => Launch(onStartup: true), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
