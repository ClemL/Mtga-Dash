using MtgaDash;
using MtgaDash.Cards;
using MtgaDash.Data;
using MtgaDash.Ingestion;
using MtgaDash.Web;

// The content root is the exe's folder, not the working directory: Task Scheduler and the service control
// manager start the app from C:\Windows\System32, where neither wwwroot nor appsettings.json exist.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService(o => o.ServiceName = "MtgaDash");

var options = builder.Configuration.GetSection(MtgaDashOptions.Section).Get<MtgaDashOptions>() ?? new MtgaDashOptions();
builder.WebHost.UseUrls($"http://{(options.AllowLan ? "0.0.0.0" : "localhost")}:{options.Port}");

var db = new Database(MtgaDashOptions.Resolve(options.DatabasePath));
db.Migrate();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(db);
builder.Services.AddSingleton<ChangeNotifier>();
builder.Services.AddSingleton(sp => new CardNames(options.CardDbPath, sp.GetRequiredService<ILogger<CardNames>>()));
builder.Services.AddSingleton(sp => new Queries(db, sp.GetRequiredService<CardNames>()));
builder.Services.AddSingleton(sp => new LogIngestor(db, sp.GetRequiredService<ILogger<LogIngestor>>()));
builder.Services.AddSingleton<PageRenderer>();
builder.Services.AddSingleton<StaticExporter>();
builder.Services.AddSingleton<IngestionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestionService>());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();
app.Services.GetRequiredService<StaticExporter>(); // subscribes to changes when ExportFolder is set
app.Logger.LogInformation("Database {Path}; logs {Folder}; live log {Live}",
    db.Path, MtgaDashOptions.Resolve(options.LogFolder), options.WatchLiveLog ? MtgaDashOptions.Resolve(options.LiveLogPath) : "(off)");

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
api.MapPost("/reimport", (IngestionService s) => Results.Ok(new { imported = s.ImportAccounts() }));
api.MapGet("/export", (StaticExporter e) =>
{
    var (html, json) = e.Export();
    return Results.Ok(new { html, json });
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

app.Run();
