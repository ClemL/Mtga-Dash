using Microsoft.Extensions.Logging.Abstractions;
using MtgaDash.Data;
using MtgaDash.Ingestion;

namespace MtgaDash.Tests;

/// <summary>A throwaway folder with a migrated database and an ingestor pinned to UTC.</summary>
internal sealed class TempDb : IDisposable
{
    public TempDb()
    {
        Folder = Path.Combine(Path.GetTempPath(), "mtgadash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        Db = new Database(Path.Combine(Folder, "test.db"));
        Db.Migrate();
        Ingestor = new LogIngestor(Db, NullLogger.Instance, Fixtures.Utc);
        Queries = new Queries(Db);
    }

    public string Folder { get; }
    public Database Db { get; }
    public LogIngestor Ingestor { get; }
    public Queries Queries { get; }

    public string File(string name) => Path.Combine(Folder, name);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
