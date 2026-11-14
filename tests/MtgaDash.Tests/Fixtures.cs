using System.Text.Json;

namespace MtgaDash.Tests;

/// <summary>Builds small, synthetic Player.log content in the shape verified from real logs.</summary>
internal static class Fixtures
{
    public static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    public static string Startup(string when = "11/13/2026 12:43:30 AM") => $"Startup Timestamp: {when}";

    public static string Login(string name) => $"[Accounts - Login] Logged in successfully. Display Name: {name}";

    public static string[] StartHook(string serverTime = "2026-11-13T05:44:01.123Z", int gems = 450, int gold = 2150,
        int c = 39, int u = 25, int r = 8, int m = 7, int precons = 116, int custom = 4)
    {
        var decks = new List<object>();
        for (var i = 0; i < precons; i++)
            decks.Add(Deck($"p{i:000}", $"?=?Loc/Decks/Precon/Precon_{i}", "Standard", null));
        for (var i = 0; i < custom; i++)
            decks.Add(Deck($"c{i}", $"My deck {i}", i % 2 == 0 ? "Standard" : "Historic", "2026-11-01T10:20:00.15-05:00"));
        var body = new
        {
            InventoryInfo = new
            {
                Gems = gems, Gold = gold, TotalVaultProgress = 12.5, WcTrackPosition = 3,
                WildCardCommons = c, WildCardUnCommons = u, WildCardRares = r, WildCardMythics = m,
                CustomTokens = new Dictionary<string, int> { ["BonusPackProgress"] = 2 },
                Boosters = new[] { new { CollationId = 100025, SetCode = "BRO", Count = 1 }, new { CollationId = 100031, SetCode = "TRK", Count = 1 } },
            },
            DeckSummaries = decks,
            ServerTimeInternal = serverTime,
        };
        return ["<== StartHook(5a7c1b2e-0000-4000-8000-000000000001)", JsonSerializer.Serialize(body)];
    }

    public static object Deck(string id, string name, string format, string? lastPlayed) => new
    {
        DeckId = id,
        Name = name,
        Attributes = new[]
        {
            new { name = "Format", value = JsonSerializer.Serialize(format) },
            new { name = "LastPlayed", value = JsonSerializer.Serialize(lastPlayed ?? "0001-01-01T00:00:00") },
            new { name = "LastUpdated", value = JsonSerializer.Serialize("2022-12-02T10:20:00.15-05:00") },
        },
        Description = "",
        DeckTileId = 12345,
    };

    /// <summary>A DeckUpsertDeckV3 request; <paramref name="encodings"/> is how many times "request" is JSON-encoded.</summary>
    public static string DeckUpsert(string deckId = "c0", int main = 60, int side = 7, int encodings = 1)
    {
        var payload = new
        {
            Summary = new { DeckId = deckId, Name = "Mono red", Attributes = new[] { new { name = "Format", value = "\"Standard\"" }, new { name = "LastUpdated", value = "\"2026-11-13T00:50:00-05:00\"" } } },
            Deck = new
            {
                MainDeck = Cards(80000, main),
                Sideboard = Cards(90000, side),
                CommandZone = Array.Empty<object>(),
                Companions = Array.Empty<object>(),
            },
        };
        object request = JsonSerializer.Serialize(payload);
        for (var i = 1; i < encodings; i++) request = JsonSerializer.Serialize(request);
        var line = JsonSerializer.Serialize(new { id = "9b1e0c4a-0000-4000-8000-000000000002", request });
        return $"[UnityCrossThreadLogger]==> DeckUpsertDeckV3 {line}";
    }

    /// <summary><paramref name="total"/> cards as four-ofs plus one remainder entry.</summary>
    private static object[] Cards(int baseId, int total)
    {
        var list = new List<object>();
        var id = baseId;
        while (total > 0)
        {
            var q = Math.Min(4, total);
            list.Add(new { cardId = id++, quantity = q });
            total -= q;
        }
        return list.ToArray();
    }

    public static string[] EventJoin(int gems, int gold) =>
    [
        "<== EventJoin(1d2e3f40-0000-4000-8000-000000000003)",
        JsonSerializer.Serialize(new { Course = new { CourseId = "x" }, InventoryInfo = new { gems, gold, wcTrackPosition = 4 } }),
    ];

    public static string[] Rank(string? constructedClass = "Gold", int level = 2) =>
    [
        "<== RankGetCombinedRankInfo(77777777-0000-4000-8000-000000000004)",
        constructedClass is null
            ? """{"constructedSeasonOrdinal":95,"constructedLevel":0,"constructedMatchesLost":0,"limitedSeasonOrdinal":95,"limitedLevel":0}"""
            : JsonSerializer.Serialize(new { constructedSeasonOrdinal = 95, constructedClass, constructedLevel = level, constructedMatchesLost = 3, limitedSeasonOrdinal = 95, limitedClass = "Bronze", limitedLevel = 4 }),
    ];

    /// <summary>One block, Allerclear with detailed logs on: StartHook, a later EventJoin, a deck save and a rank.</summary>
    public static string[] AllerclearLog() =>
    [
        Startup(),
        "DETAILED LOGS: ENABLED",
        "Version: 2026.65.0.14672 / 2026.65.0.14672.1335447 /",
        Login("Allerclear#92603"),
        .. StartHook(),
        "[UnityCrossThreadLogger]some unrelated line",
        .. Rank(),
        DeckUpsert(),
    ];

    /// <summary>Two accounts in one file, detailed logs off: logins only, no responses.</summary>
    public static string[] TwoLoginsDetailedOff() =>
    [
        Startup("11/12/2026 9:01:02 PM"),
        "DETAILED LOGS: DISABLED",
        Login("winnie#88467"),
        "[UnityCrossThreadLogger]Client.SceneChange",
        Startup("11/12/2026 9:30:00 PM"),
        "DETAILED LOGS: DISABLED",
        Login("stacy#75572"),
    ];

    public static string Text(IEnumerable<string> lines) => string.Join("\n", lines) + "\n";

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MtgaDash.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("MtgaDash.sln not found above the test output");
    }

    public static string TestData(string name) => Path.Combine(RepoRoot(), "testdata", name);
}
