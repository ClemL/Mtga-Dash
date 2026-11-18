using MtgaDash.Accounts;
using MtgaDash.Cards;
using MtgaDash.Data;
using MtgaDash.Ingestion;
using MtgaDash.Web;

namespace MtgaDash;

/// <summary>Builds the whole app, so the tests can start the real HTTP pipeline on a free port.</summary>
public static class AppHost
{
    /// <summary>Every state-changing request must carry this header. See <see cref="RequireAppHeader"/>.</summary>
    public const string MutationHeader = "X-MtgaDash";

    public static WebApplication Build(string[] args)
    {
        // The content root is the exe's folder, not the working directory: Task Scheduler and the service
        // control manager start the app from C:\Windows\System32, where neither wwwroot nor appsettings.json exist.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Host.UseWindowsService(o => o.ServiceName = "MtgaDash");

        var options = builder.Configuration.GetSection(MtgaDashOptions.Section).Get<MtgaDashOptions>() ?? new MtgaDashOptions();
        // Kestrel cannot pick a free port for "localhost", so port 0 (the tests) binds the loopback address.
        var host = options.AllowLan ? "0.0.0.0" : options.Port == 0 ? "127.0.0.1" : "localhost";
        builder.WebHost.UseUrls($"http://{host}:{options.Port}");

        // DNS rebinding: a web page can point its own domain at 127.0.0.1 and read this API as same-origin.
        // Refusing any Host header but the loopback names stops that. On the LAN, list the PC's names in
        // AllowedHosts to keep the protection; without it any Host is accepted.
        if (!options.AllowLan) builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
        else if (string.IsNullOrWhiteSpace(builder.Configuration["AllowedHosts"])) builder.Configuration["AllowedHosts"] = "*";

        var db = new Database(MtgaDashOptions.Resolve(options.DatabasePath));
        db.Migrate();

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<ChangeNotifier>();
        builder.Services.AddSingleton(sp => new ExceptionStore(ExceptionsPath(options), sp.GetRequiredService<ILogger<ExceptionStore>>()));
        builder.Services.AddSingleton<StatusTracker>();
        builder.Services.AddSingleton<ActivityLog>();
        builder.Services.AddSingleton(sp => new CardNames(options.CardDbPath, sp.GetRequiredService<ILogger<CardNames>>()));
        builder.Services.AddSingleton(sp => new Queries(db, sp.GetRequiredService<CardNames>(), sp.GetRequiredService<ExceptionStore>()));
        builder.Services.AddSingleton(sp => new LogIngestor(db, sp.GetRequiredService<ILogger<LogIngestor>>(),
            onWarning: sp.GetRequiredService<StatusTracker>().Warning, onEvent: sp.GetRequiredService<ActivityLog>().Record));
        builder.Services.AddSingleton<PageRenderer>();
        builder.Services.AddSingleton<StaticExporter>();
        builder.Services.AddSingleton<IngestionService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestionService>());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

        var app = builder.Build();
        app.Services.GetRequiredService<StaticExporter>(); // subscribes to changes when ExportFolder is set
        app.Logger.LogInformation("Database {Path}; logs {Folder}; live log {Live}; exception mappings {Exceptions}",
            db.Path, MtgaDashOptions.Resolve(options.LogFolder),
            options.WatchLiveLog ? MtgaDashOptions.Resolve(options.LiveLogPath) : "(off)", ExceptionsPath(options));

        MapEndpoints(app, options);
        return app;
    }

    /// <summary>ExceptionsPath, or <c>account-exceptions.json</c> beside the database, which survives republishing.</summary>
    public static string ExceptionsPath(MtgaDashOptions o) => !string.IsNullOrWhiteSpace(o.ExceptionsPath)
        ? MtgaDashOptions.Resolve(o.ExceptionsPath)
        : Path.Combine(Path.GetDirectoryName(MtgaDashOptions.Resolve(o.DatabasePath)) ?? ".", "account-exceptions.json");

    /// <summary>
    /// Another site can send a form POST to localhost without asking, but not a custom header: that needs a
    /// CORS preflight, which this app never grants. So every state-changing endpoint requires the header.
    /// </summary>
    private static async ValueTask<object?> RequireAppHeader(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next) =>
        ctx.HttpContext.Request.Headers[MutationHeader] == "1"
            ? await next(ctx)
            : Results.Problem($"Missing the {MutationHeader} header.", statusCode: StatusCodes.Status403Forbidden);

    public sealed record MappingRequest(string? DisplayName, string? Email);

    private static void MapEndpoints(WebApplication app, MtgaDashOptions options)
    {
        IResult Page(PageRenderer r) => Results.Content(r.Render(), "text/html; charset=utf-8");
        app.MapGet("/", Page);
        app.MapGet("/index.html", Page);

        // Revalidate every static file, so an edited updates.txt shows on the next load.
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
        });

        var api = app.MapGroup("/api");
        api.MapGet("/accounts", (Queries q, string? search, string? status, bool? stale, int? staleDays) =>
            q.Accounts(search, status, stale, staleDays ?? options.StaleDays));
        api.MapGet("/accounts/{email}", (Queries q, string email) => q.Account(email) is { } d ? Results.Ok(d) : Results.NotFound());
        api.MapGet("/totals", (Queries q) => q.Totals());
        api.MapGet("/review", (Queries q) => q.Review());
        api.MapGet("/statuses", (Queries q) => q.Statuses());
        api.MapGet("/status", (StatusTracker s) => s.Snapshot());
        api.MapGet("/activity", (ActivityLog a, Queries q) => new { entries = a.Entries(), messages = a.Messages(), players = q.Players() });
        // Only a file the app already reads can be tailed, so the name cannot be used to read anything else.
        api.MapGet("/logtail", (StatusTracker s, string? file, int? lines) =>
        {
            var files = s.Snapshot().Files.Where(f => f.Exists).ToList();
            var live = MtgaDashOptions.Resolve(options.LiveLogPath);
            var target = string.IsNullOrEmpty(file)
                ? files.FirstOrDefault(f => string.Equals(f.Path, live, StringComparison.OrdinalIgnoreCase)) ?? files.LastOrDefault()
                : files.FirstOrDefault(f => string.Equals(f.Name, file, StringComparison.OrdinalIgnoreCase));
            if (target is null) return Results.NotFound();
            try
            {
                return Results.Ok(new { file = target.Name, path = target.Path, length = target.Length, lines = ActivityLog.Tail(target.Path, Math.Clamp(lines ?? 60, 1, 500)) });
            }
            catch (IOException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        var write = api.MapGroup("").AddEndpointFilter(RequireAppHeader);
        write.MapPost("/reimport", (IngestionService s) => Results.Ok(new { imported = s.ImportAccounts() }));
        write.MapPost("/export", (StaticExporter e) =>
        {
            var (html, json) = e.Export();
            return Results.Ok(new { html, json });
        });
        write.MapPost("/exceptions", (MappingRequest req, ExceptionStore store, Database db, IngestionService ingestion) =>
        {
            var name = req.DisplayName?.Trim();
            var email = req.Email?.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email))
                return Results.Problem("Both displayName and email are required.", statusCode: StatusCodes.Status400BadRequest);
            using (var c = db.Open())
            {
                // Store the account's own spelling, and refuse a typo rather than map to an account that does not exist.
                var known = Dapper.SqlMapper.QueryFirstOrDefault<string>(c, "SELECT Email FROM Accounts WHERE Email = @email", new { email });
                if (known is null) return Results.Problem($"No account {email} in the accounts CSV.", statusCode: StatusCodes.Status400BadRequest);
                email = known;
            }
            try
            {
                store.Set(name, email);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            ingestion.Rematch();
            return Results.Ok(new { displayName = name, email });
        });
        write.MapDelete("/exceptions/{displayName}", (string displayName, ExceptionStore store, IngestionService ingestion) =>
        {
            bool removed;
            try
            {
                removed = store.Remove(displayName);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            if (!removed) return Results.NotFound();
            ingestion.Rematch();
            return Results.NoContent();
        });

        api.MapGet("/events", async (HttpContext ctx, ChangeNotifier notifier, CancellationToken cancel) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var reader = notifier.Subscribe(cancel);
            await ctx.Response.WriteAsync($"event: hello\ndata: {Time.Format(notifier.LastChangedUtc)}\n\n", cancel);
            await ctx.Response.Body.FlushAsync(cancel);
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    // A comment every 20 s keeps proxies and the browser from timing the stream out.
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    wait.CancelAfter(TimeSpan.FromSeconds(20));
                    string message;
                    try
                    {
                        message = await reader.ReadAsync(wait.Token);
                    }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", cancel);
                        await ctx.Response.Body.FlushAsync(cancel);
                        continue;
                    }
                    await ctx.Response.WriteAsync($"event: changed\ndata: {message}\n\n", cancel);
                    await ctx.Response.Body.FlushAsync(cancel);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException)
            {
                // The browser closed the tab.
            }
        });
    }
}
