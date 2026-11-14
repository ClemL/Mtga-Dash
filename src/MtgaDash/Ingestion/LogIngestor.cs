using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MtgaDash.Data;
using MtgaDash.Parsing;

namespace MtgaDash.Ingestion;

public sealed record IngestResult(string Path, long BytesRead, bool Restarted, Writer.Counts Counts)
{
    public static IngestResult Nothing(string path) => new(path, 0, false, new Writer.Counts(0, 0, 0, 0, 0, 0));
}

/// <summary>
/// Reads one log file from where the last poll stopped. Polling length and the first bytes' hash is the
/// source of truth: FileSystemWatcher misses writes to a file another process holds open.
/// </summary>
public sealed class LogIngestor(Database db, ILogger logger, TimeZoneInfo? logTimeZone = null)
{
    /// <summary>The prefix hashed to tell a rotated live log from a grown one.</summary>
    public const int HashBytes = 4096;

    /// <summary>Commit every few megabytes, so a first pass over a huge log survives an interruption.</summary>
    private const int BatchBytes = 8 * 1024 * 1024;

    private sealed record FileRow(string Path, string FirstBytesHash, long LengthProcessed, string LastWriteUtc, string ParserStateJson);

    public IngestResult IngestFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return IngestResult.Nothing(path);

        // MTGA and the archiver keep the file open for writing; share everything so neither side blocks.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var length = fs.Length;

        FileRow? row;
        using (var c = db.Open())
            row = c.QuerySingleOrDefault<FileRow>("SELECT * FROM IngestedFiles WHERE Path = @path", new { path });

        var restarted = false;
        long offset = 0;
        LogParserState? state = null;
        if (row is not null)
        {
            var (hashedLength, hash) = SplitHash(row.FirstBytesHash);
            var sameStart = length >= hashedLength && HashPrefix(fs, hashedLength) == hash;
            if (length < row.LengthProcessed || !sameStart)
            {
                // Shrunk, or a different file under the same name: MTGA rotated the live log.
                restarted = true;
                logger.LogInformation("{Path} was replaced or truncated; re-parsing from the start", path);
            }
            else if (length == row.LengthProcessed)
            {
                return IngestResult.Nothing(path);
            }
            else
            {
                offset = row.LengthProcessed;
                state = JsonSerializer.Deserialize<LogParserState>(row.ParserStateJson);
            }
        }

        var parser = new LogParser(state, logTimeZone);
        var prefixLength = (int)Math.Min(HashBytes, length);
        var prefixHash = $"{prefixLength}:{HashPrefix(fs, prefixLength)}";
        var lastWrite = Time.Format(info.LastWriteTimeUtc);

        var totals = new Writer.Counts(0, 0, 0, 0, 0, 0);
        var events = new List<LogEvent>();
        var pendingLine = new MemoryStream();
        var buffer = new byte[1 << 20];
        var position = offset;          // bytes read so far
        var committedTo = offset;       // offset just past the last complete line handed to the parser
        long sinceCommit = 0;

        fs.Seek(offset, SeekOrigin.Begin);
        int read;
        while (position < length && (read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, length - position))) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                pendingLine.Write(buffer, start, i - start);
                var line = Encoding.UTF8.GetString(pendingLine.GetBuffer(), 0, (int)pendingLine.Length);
                pendingLine.SetLength(0);
                events.AddRange(parser.ParseLine(line));
                start = i + 1;
                committedTo = position + i + 1;
            }
            // A trailing partial line is left for the next poll: it is re-read from committedTo.
            pendingLine.Write(buffer, start, read - start);
            position += read;
            sinceCommit += read;

            if (sinceCommit >= BatchBytes)
            {
                totals = Add(totals, Commit(path, events, parser.State, committedTo, prefixHash, lastWrite));
                events.Clear();
                sinceCommit = 0;
            }
        }

        totals = Add(totals, Commit(path, events, parser.State, committedTo, prefixHash, lastWrite));
        var bytesRead = committedTo - offset;
        if (bytesRead > 0 || restarted)
        {
            logger.LogInformation(
                "{Path}: {Bytes:N0} bytes, {Logins} logins, {Snapshots} snapshots, {Decks} deck lists, {Ranks} ranks, {Warnings} warnings",
                path, bytesRead, totals.Logins, totals.Snapshots, totals.DeckLists, totals.Ranks, totals.Warnings);
        }
        return new IngestResult(path, bytesRead, restarted, totals);
    }

    private Writer.Counts Commit(string path, List<LogEvent> events, LogParserState state, long offset, string prefixHash, string lastWrite)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var counts = Writer.Apply(c, tx, events, path, w => logger.LogWarning("{Path} line {Line}: {Message}", path, w.LineNumber, w.Message));
        // The offset and the parser state are saved with the rows they produced, so a crash between the two
        // cannot skip or double-apply a line.
        c.Execute("""
            INSERT INTO IngestedFiles(Path, FirstBytesHash, LengthProcessed, LastWriteUtc, ParserStateJson)
            VALUES (@path, @prefixHash, @offset, @lastWrite, @stateJson)
            ON CONFLICT(Path) DO UPDATE SET FirstBytesHash = excluded.FirstBytesHash, LengthProcessed = excluded.LengthProcessed,
                LastWriteUtc = excluded.LastWriteUtc, ParserStateJson = excluded.ParserStateJson
            """, new { path, prefixHash, offset, lastWrite, stateJson = JsonSerializer.Serialize(state) }, tx);
        tx.Commit();
        return counts;
    }

    private static Writer.Counts Add(Writer.Counts a, Writer.Counts b) =>
        new(a.Logins + b.Logins, a.Snapshots + b.Snapshots, a.DeckLists + b.DeckLists, a.Ranks + b.Ranks, a.Warnings + b.Warnings, a.NewPlayers + b.NewPlayers);

    private static (int Length, string Hash) SplitHash(string stored)
    {
        var colon = stored.IndexOf(':');
        return colon > 0 && int.TryParse(stored[..colon], out var n) ? (n, stored[(colon + 1)..]) : (HashBytes, stored);
    }

    /// <summary>
    /// Hashes the first <paramref name="count"/> bytes. The stored hash records how many bytes it covers,
    /// because a file shorter than 4 KB changes its first 4 KB simply by growing.
    /// </summary>
    private static string HashPrefix(FileStream fs, int count)
    {
        var bytes = new byte[count];
        fs.Seek(0, SeekOrigin.Begin);
        var total = 0;
        while (total < count)
        {
            var n = fs.Read(bytes, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, total)));
    }
}
