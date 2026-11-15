using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace MtgaDash.Tests;

/// <summary>
/// Runs the real app (Kestrel, host filtering, endpoint filters, ingestion) on a free port against a temp
/// folder, so the security rules are tested where they are enforced: in the HTTP pipeline.
/// </summary>
[TestClass]
public sealed class ApiTests
{
    private static WebApplication _app = null!;
    private static HttpClient _http = null!;
    private static string _folder = null!;

    private const string Csv = "Status,Email,Notes,Misc Notes,Note Source\n" +
        "Active,base1+corsair@example.com,,,\nActive,base2+corsair@example.com,,,\nActive,base1+quill@example.com,,,\n";

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        _folder = Path.Combine(Path.GetTempPath(), "mtgadash-api", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_folder, "logs"));
        File.WriteAllText(Path.Combine(_folder, "accounts.csv"), Csv);
        File.WriteAllText(Path.Combine(_folder, "logs", "Player_1.log"), Fixtures.Text([
            Fixtures.Startup(), Fixtures.Login("Corsair#5"), .. Fixtures.StartHook(),
            Fixtures.Startup("11/13/2026 2:00:00 AM"), Fixtures.Login("Stranger#7"),
        ]));
        _app = AppHost.Build([
            "--MtgaDash:Port=0",
            $"--MtgaDash:DatabasePath={Path.Combine(_folder, "data", "mtga.db")}",
            $"--MtgaDash:LogFolder={Path.Combine(_folder, "logs")}",
            $"--MtgaDash:AccountsCsvPath={Path.Combine(_folder, "accounts.csv")}",
            $"--MtgaDash:ExportFolder={Path.Combine(_folder, "export")}",
            "--MtgaDash:WatchLiveLog=false",
            "--MtgaDash:PollSeconds=1",
            "--Logging:LogLevel:Default=Warning",
        ]);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        await Until(async () => (await Review()).GetProperty("ambiguous").GetArrayLength() == 1);
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("timed out waiting for ingestion");
            await Task.Delay(100);
        }
    }

    private static async Task<JsonElement> Review() => await _http.GetFromJsonAsync<JsonElement>("/api/review");

    private static HttpRequestMessage Write(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("X-MtgaDash", "1");
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    [TestMethod]
    public async Task A_foreign_Host_header_is_refused()
    {
        Assert.AreEqual(HttpStatusCode.OK, (await _http.GetAsync("/api/totals")).StatusCode);
        var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        rebinding.Headers.Host = "attacker.example";
        Assert.AreEqual(HttpStatusCode.BadRequest, (await _http.SendAsync(rebinding)).StatusCode);
    }

    [TestMethod]
    public async Task Writes_without_the_app_header_are_refused()
    {
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _http.PostAsync("/api/reimport", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _http.PostAsJsonAsync("/api/exceptions", new { displayName = "Corsair#5", email = "base2+corsair@example.com" })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await _http.DeleteAsync("/api/exceptions/Corsair%235")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await _http.SendAsync(Write(HttpMethod.Post, "/api/reimport"))).StatusCode);
    }

    [TestMethod]
    public async Task Export_is_a_post_only()
    {
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, (await _http.GetAsync("/api/export")).StatusCode);
        var r = await _http.SendAsync(Write(HttpMethod.Post, "/api/export"));
        Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
        Assert.IsTrue(File.Exists(Path.Combine(_folder, "export", "mtga_dashboard.html")));
    }

    [TestMethod]
    public async Task Mapping_from_the_review_tab_applies_at_once_and_can_be_removed()
    {
        var map = await _http.SendAsync(Write(HttpMethod.Post, "/api/exceptions", new { displayName = "Corsair#5", email = "BASE2+corsair@example.com" }));
        Assert.AreEqual(HttpStatusCode.OK, map.StatusCode, await map.Content.ReadAsStringAsync());

        var review = await Review();
        Assert.AreEqual(0, review.GetProperty("ambiguous").GetArrayLength());
        var mapping = review.GetProperty("mappings").EnumerateArray().Single();
        Assert.AreEqual("base2+corsair@example.com", mapping.GetProperty("email").GetString(), "stored with the CSV's own spelling");
        Assert.AreEqual("file", mapping.GetProperty("source").GetString());
        var account = await _http.GetFromJsonAsync<JsonElement>("/api/accounts/" + Uri.EscapeDataString("base2+corsair@example.com"));
        Assert.AreEqual("Exception", account.GetProperty("account").GetProperty("matchState").GetString());
        Assert.AreEqual(450, account.GetProperty("account").GetProperty("gems").GetInt32());
        StringAssert.Contains(File.ReadAllText(Path.Combine(_folder, "data", "account-exceptions.json")), "Corsair#5");

        Assert.AreEqual(HttpStatusCode.NoContent, (await _http.SendAsync(Write(HttpMethod.Delete, "/api/exceptions/" + Uri.EscapeDataString("Corsair#5")))).StatusCode);
        Assert.AreEqual(1, (await Review()).GetProperty("ambiguous").GetArrayLength());
        Assert.AreEqual(HttpStatusCode.NotFound, (await _http.SendAsync(Write(HttpMethod.Delete, "/api/exceptions/" + Uri.EscapeDataString("Corsair#5")))).StatusCode);
    }

    [TestMethod]
    public async Task Mapping_to_an_email_not_in_the_csv_is_refused()
    {
        var r = await _http.SendAsync(Write(HttpMethod.Post, "/api/exceptions", new { displayName = "Stranger#7", email = "typo+stranger@example.com" }));
        Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode);
        StringAssert.Contains(await r.Content.ReadAsStringAsync(), "No account typo+stranger@example.com");
    }

    [TestMethod]
    public async Task Status_reports_the_poll_and_each_file()
    {
        await Until(async () => (await _http.GetFromJsonAsync<JsonElement>("/api/status")).GetProperty("lastPollUtc").ValueKind == JsonValueKind.String);
        var s = await _http.GetFromJsonAsync<JsonElement>("/api/status");
        var file = s.GetProperty("files").EnumerateArray().Single();
        Assert.AreEqual("Player_1.log", file.GetProperty("name").GetString());
        Assert.AreEqual(file.GetProperty("length").GetInt64(), file.GetProperty("processed").GetInt64());
        Assert.AreEqual("Stranger#7", file.GetProperty("displayName").GetString());
    }

    [TestMethod]
    public async Task The_page_is_served_with_the_boot_script_and_config()
    {
        var html = await _http.GetStringAsync("/");
        StringAssert.Contains(html, "mtgadash:theme");
        StringAssert.Contains(html, "\"version\":");
        Assert.IsFalse(html.Contains("/*CONFIG*/"));
    }
}
