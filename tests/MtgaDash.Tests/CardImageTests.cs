using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MtgaDash.Cards;

namespace MtgaDash.Tests;

/// <summary>A stand-in for Scryfall: answers from a table and records every request. Nothing leaves the machine.</summary>
internal sealed class FakeScryfall : HttpMessageHandler
{
    public readonly List<HttpRequestMessage> Requests = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Answer(request));
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Jpeg(string marker) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("JPEG:" + marker)) };
}

[TestClass]
public sealed class CardImageTests
{
    private static (CardImageStore Store, FakeScryfall Fake) Make(TempDb t)
    {
        var fake = new FakeScryfall();
        return (new CardImageStore(t.File("card-images"), "https://scryfall.test", CardImageStore.CreateClient(fake), NullLogger.Instance), fake);
    }

    private const string Search = """{"object":"list","data":[{"name":"Elf Lord","released_at":"2026-10-01","image_uris":{"normal":"https://img.test/new.jpg"}},{"name":"Elf Lord","released_at":"2019-01-01","image_uris":{"normal":"https://img.test/old.jpg"}}]}""";

    [TestMethod]
    [DataRow("Elf Lord", "Elf Lord")]
    [DataRow("A-Tide Caller", "A-Tide Caller")]
    [DataRow("Fire // Ice", "Fire __ Ice")]
    [DataRow("Jace, the Mind Sculptor", "Jace, the Mind Sculptor")]
    [DataRow("../../etc", "______etc")]
    public void Keys_are_safe_file_names(string name, string key) => Assert.AreEqual(key, CardImageStore.KeyFor(name));

    [TestMethod]
    public void Only_a_key_the_store_could_write_is_served()
    {
        using var t = new TempDb();
        var (store, _) = Make(t);
        Directory.CreateDirectory(store.Folder);
        File.WriteAllText(Path.Combine(store.Folder, "Elf Lord.jpg"), "x");
        Assert.IsNotNull(store.FileForKey("Elf Lord"));
        foreach (var bad in new[] { "..", "../Elf Lord", "a/b", "a\\b", "", "Elf Lord.jpg" })
            Assert.IsNull(store.FileForKey(bad), bad);
    }

    [TestMethod]
    public async Task The_newest_art_is_saved_and_a_second_run_asks_for_nothing()
    {
        using var t = new TempDb();
        var (store, fake) = Make(t);
        fake.Answer = r => r.RequestUri!.Host == "scryfall.test" ? FakeScryfall.Json(Search) : FakeScryfall.Jpeg(r.RequestUri.AbsolutePath);

        var report = await store.DownloadMissingAsync(["Elf Lord"], null, CancellationToken.None);
        Assert.AreEqual(new CardImageStore.Report(1, 0, 0), report);
        Assert.AreEqual("JPEG:/new.jpg", File.ReadAllText(store.PathFor("Elf Lord")), "the first result, newest release, is used");

        var search = fake.Requests[0].RequestUri!.AbsoluteUri;
        StringAssert.Contains(search, "/cards/search?q=%21%22Elf%20Lord%22");
        StringAssert.Contains(search, "unique=art&order=released&dir=desc");
        StringAssert.StartsWith(fake.Requests[0].Headers.UserAgent.ToString(), "MtgaDash/");
        Assert.IsTrue(fake.Requests[0].Headers.Accept.Any(), "Scryfall asks for an Accept header");

        fake.Requests.Clear();
        await store.DownloadMissingAsync(["Elf Lord"], null, CancellationToken.None);
        Assert.AreEqual(0, fake.Requests.Count);
        Assert.IsFalse(File.Exists(store.PathFor("Elf Lord") + ".part"));
    }

    [TestMethod]
    public async Task A_double_faced_card_uses_its_front_face()
    {
        using var t = new TempDb();
        var (store, fake) = Make(t);
        fake.Answer = r => r.RequestUri!.Host == "scryfall.test"
            ? FakeScryfall.Json("""{"data":[{"name":"Day // Night","card_faces":[{"image_uris":{"normal":"https://img.test/front.jpg"}},{"image_uris":{"normal":"https://img.test/back.jpg"}}]}]}""")
            : FakeScryfall.Jpeg(r.RequestUri.AbsolutePath);
        await store.DownloadMissingAsync(["Day // Night"], null, CancellationToken.None);
        Assert.AreEqual("JPEG:/front.jpg", File.ReadAllText(store.PathFor("Day // Night")));
    }

    [TestMethod]
    public async Task A_card_with_no_image_gets_a_placeholder_and_is_retried_a_week_later()
    {
        using var t = new TempDb();
        var (store, fake) = Make(t);
        fake.Answer = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var report = await store.DownloadMissingAsync(["Brand New Card"], null, CancellationToken.None);
        Assert.AreEqual(new CardImageStore.Report(0, 1, 0), report);
        Assert.AreEqual(0, store.Missing(["Brand New Card"]).Count, "not asked for again tomorrow");
        Assert.AreEqual(1, store.Missing(["Brand New Card"], DateTime.UtcNow.AddDays(8)).Count, "asked again after 7 days");
        Assert.AreEqual(1, store.Status(["Brand New Card"]).NotFound);
    }

    [TestMethod]
    public async Task A_server_error_is_retried_next_run_not_marked_as_no_image()
    {
        using var t = new TempDb();
        var (store, fake) = Make(t);
        fake.Answer = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var report = await store.DownloadMissingAsync(["Elf Lord"], null, CancellationToken.None);
        Assert.AreEqual(new CardImageStore.Report(0, 0, 1), report);
        Assert.AreEqual(1, store.Missing(["Elf Lord"]).Count);
    }

    [TestMethod]
    public async Task Too_many_requests_waits_and_tries_once_more()
    {
        using var t = new TempDb();
        var (store, fake) = Make(t);
        var first = true;
        fake.Answer = r =>
        {
            if (first) { first = false; return new HttpResponseMessage(HttpStatusCode.TooManyRequests); }
            return r.RequestUri!.Host == "scryfall.test" ? FakeScryfall.Json(Search) : FakeScryfall.Jpeg("x");
        };
        Assert.AreEqual(1, (await store.DownloadMissingAsync(["Elf Lord"], null, CancellationToken.None)).Downloaded);
    }
}
