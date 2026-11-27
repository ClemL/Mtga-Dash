using System.Collections.Concurrent;
using System.Diagnostics;

namespace MtgaDash.Usage;

/// <summary>How long each API endpoint takes, since the app started, for the usage report's performance section.</summary>
public sealed class RequestTimings
{
    private sealed class Stat
    {
        public long Count, Slow;
        public double TotalMs, MaxMs;
    }

    public sealed record Row(string Endpoint, long Count, double MeanMs, double MaxMs, long Over500Ms);

    private readonly ConcurrentDictionary<string, Stat> _stats = new(StringComparer.Ordinal);

    public void Record(string endpoint, double ms)
    {
        var s = _stats.GetOrAdd(endpoint, _ => new Stat());
        lock (s)
        {
            s.Count++;
            s.TotalMs += ms;
            s.MaxMs = Math.Max(s.MaxMs, ms);
            if (ms > 500) s.Slow++;
        }
    }

    public IReadOnlyList<Row> Rows() => _stats
        .Select(kv => { lock (kv.Value) return new Row(kv.Key, kv.Value.Count, Math.Round(kv.Value.TotalMs / kv.Value.Count, 1), Math.Round(kv.Value.MaxMs, 1), kv.Value.Slow); })
        .OrderByDescending(r => r.Count).ToList();

    /// <summary>Times every request that reached an API endpoint, keyed by its route pattern, never its values.</summary>
    public static async Task Middleware(HttpContext ctx, RequestDelegate next)
    {
        var clock = Stopwatch.StartNew();
        await next(ctx);
        if (ctx.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } pattern } && pattern.StartsWith("/api", StringComparison.Ordinal))
            ctx.RequestServices.GetRequiredService<RequestTimings>().Record(ctx.Request.Method + " " + pattern, clock.Elapsed.TotalMilliseconds);
    }
}
