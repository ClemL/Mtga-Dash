using System.Text.Json;

namespace MtgaDash.Accounts;

public sealed record ExceptionMapping(string DisplayName, string Email, string Source);

/// <summary>
/// Exception mappings made from the Review tab, kept in a JSON file beside the database so they survive
/// rebuilds, republishes and CSV re-imports. They are merged over the compiled-in
/// <see cref="AccountExceptions"/>: a file entry wins, because it is the more recent decision.
/// </summary>
public sealed class ExceptionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private (DateTime WriteUtc, long Length) _loadedStamp;
    private FileContent _content = new();
    private bool _unreadable;

    public ExceptionStore(string path, ILogger logger)
    {
        Path = path;
        _logger = logger;
    }

    public string Path { get; }

    private sealed class FileContent
    {
        public Dictionary<string, string> DisplayNameToEmail { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ExcludedEmails { get; set; } = [];
    }

    /// <summary>Compiled-in entries overlaid with the file's.</summary>
    public (IReadOnlyDictionary<string, string> Map, IReadOnlySet<string> Excluded) Current()
    {
        lock (_gate)
        {
            Load();
            var map = new Dictionary<string, string>(AccountExceptions.DisplayNameToEmail, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, email) in _content.DisplayNameToEmail) map[name] = email;
            var excluded = new HashSet<string>(AccountExceptions.ExcludedEmails, StringComparer.OrdinalIgnoreCase);
            excluded.UnionWith(_content.ExcludedEmails);
            return (map, excluded);
        }
    }

    public IReadOnlyList<ExceptionMapping> Mappings()
    {
        lock (_gate)
        {
            Load();
            return AccountExceptions.DisplayNameToEmail
                .Where(kv => !_content.DisplayNameToEmail.ContainsKey(kv.Key))
                .Select(kv => new ExceptionMapping(kv.Key, kv.Value, "code"))
                .Concat(_content.DisplayNameToEmail.Select(kv => new ExceptionMapping(kv.Key, kv.Value, "file")))
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Set(string displayName, string email) => Change(c => c.DisplayNameToEmail[displayName.Trim()] = email.Trim());

    public bool Remove(string displayName)
    {
        var removed = false;
        Change(c => removed = c.DisplayNameToEmail.Remove(displayName.Trim()));
        return removed;
    }

    private void Change(Action<FileContent> change)
    {
        lock (_gate)
        {
            Load();
            // Never overwrite a file we could not read: it may hold mappings someone typed by hand.
            if (_unreadable) throw new InvalidOperationException($"{Path} is not valid JSON; fix or delete it first.");
            change(_content);
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Write then rename, so a crash mid-write cannot leave half a file.
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_content, Json));
            File.Move(temp, Path, overwrite: true);
            var info = new FileInfo(Path);
            _loadedStamp = (info.LastWriteTimeUtc, info.Length);
        }
    }

    /// <summary>Re-reads the file when it changed on disk, so hand edits apply without a restart.</summary>
    private void Load()
    {
        var info = new FileInfo(Path);
        if (!info.Exists)
        {
            _content = new FileContent();
            _unreadable = false;
            _loadedStamp = default;
            return;
        }
        var stamp = (info.LastWriteTimeUtc, info.Length);
        if (stamp == _loadedStamp) return;
        _loadedStamp = stamp;
        try
        {
            var read = JsonSerializer.Deserialize<FileContent>(File.ReadAllText(Path), Json) ?? new FileContent();
            _content = new FileContent
            {
                DisplayNameToEmail = new Dictionary<string, string>(read.DisplayNameToEmail ?? [], StringComparer.OrdinalIgnoreCase),
                ExcludedEmails = read.ExcludedEmails ?? [],
            };
            _unreadable = false;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning("Ignoring exception mappings in {Path}: {Message}", Path, ex.Message);
            _content = new FileContent();
            _unreadable = true;
        }
    }
}
