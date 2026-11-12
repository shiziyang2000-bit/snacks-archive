using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Atomic JSON load/save for top-level config files (notifications.json,
///     integrations.json, auth.json). Follows the same write-then-rename pattern
///     used by settings.json, including a .bak fallback on corruption.
/// </summary>
public sealed class ConfigFileService
{
    private readonly FileService _fileService;
    private readonly object      _lock = new();
    // Encoder settings, archive rules and the selected preset share this gate.
    public object SyncRoot => _lock;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
    };

    public ConfigFileService(FileService fileService)
    {
        ArgumentNullException.ThrowIfNull(fileService);
        _fileService = fileService;
        RecoverSettingsBatch();
    }

    private const string SettingsJournal = "processing-settings-transaction.json";
    private sealed class SettingsJournalData
    {
        public Dictionary<string, string?> Originals { get; set; } = new();
    }
    private static bool IsProcessingConfig(string name) => name is "settings.json" or "archive.json" or "preset-selection.json"
        or "settings.json.bak" or "archive.json.bak" or "preset-selection.json.bak";

    /// <summary>Commit a complete processing preset, or roll all files back. The
    /// durable undo journal is removed only after every new file reaches disk.
    /// Startup rolls back any interrupted transaction before ArchiveService loads.</summary>
    public void SaveSettingsBatch(IReadOnlyDictionary<string, string> replacements)
    {
        lock (_lock)
        {
            RecoverSettingsBatch();
            if (replacements.Keys.Any(n => !IsProcessingConfig(n) || n.EndsWith(".bak")))
                throw new ArgumentException("Only processing settings can be committed as a preset.");
            var writes = new Dictionary<string,string>(replacements);
            foreach (var name in replacements.Keys)
                if (File.Exists(GetConfigPath(name))) writes[name + ".bak"] = File.ReadAllText(GetConfigPath(name));
            var journal = new SettingsJournalData();
            foreach (var name in writes.Keys)
                journal.Originals[name] = File.Exists(GetConfigPath(name)) ? File.ReadAllText(GetConfigPath(name)) : null;
            WriteDurableAtomic(GetConfigPath(SettingsJournal), JsonSerializer.Serialize(journal, _jsonOptions));
            try
            {
                foreach (var entry in writes) WriteDurableAtomic(GetConfigPath(entry.Key), entry.Value);
                File.Delete(GetConfigPath(SettingsJournal)); // commit point
            }
            catch
            {
                // Keep the journal if rollback itself fails; startup will retry it.
                RestoreSettingsJournal(journal);
                File.Delete(GetConfigPath(SettingsJournal));
                throw;
            }
        }
    }
    private void RecoverSettingsBatch()
    {
        lock (_lock)
        {
            var path = GetConfigPath(SettingsJournal);
            if (!File.Exists(path)) return;
            var journal = JsonSerializer.Deserialize<SettingsJournalData>(File.ReadAllText(path), _jsonOptions)
                ?? throw new IOException("Cannot recover the interrupted processing settings transaction.");
            RestoreSettingsJournal(journal);
            File.Delete(path);
        }
    }
    private void RestoreSettingsJournal(SettingsJournalData journal)
    {
        if (journal.Originals.Keys.Any(n => !IsProcessingConfig(n)))
            throw new IOException("Invalid processing settings transaction journal.");
        foreach (var entry in journal.Originals)
        {
            var path = GetConfigPath(entry.Key);
            if (entry.Value == null) { if (File.Exists(path)) File.Delete(path); }
            else WriteDurableAtomic(path, entry.Value);
        }
    }
    private static void WriteDurableAtomic(string path, string contents)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var data = System.Text.Encoding.UTF8.GetBytes(contents);
                stream.Write(data); stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /******************************************************************
     *  Public API
     ******************************************************************/

    /// <summary>
    ///     Returns the absolute path for the given config filename under the
    ///     application's config directory, creating the directory if it does not exist.
    /// </summary>
    /// <param name="filename"> The config file name (e.g. "notifications.json"). </param>
    public string GetConfigPath(string filename)
    {
        var configDir = Path.Combine(_fileService.GetWorkingDirectory(), "config");
        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, filename);
    }

    /// <summary>
    ///     Deserializes a config file, falling back to the <c>.bak</c> copy on corruption
    ///     and to a default instance if neither file exists or can be parsed.
    /// </summary>
    /// <typeparam name="T"> The config model type. Must have a parameterless constructor. </typeparam>
    /// <param name="filename"> The config file name to load. </param>
    public T Load<T>(string filename) where T : new()
    {
        lock (_lock) return LoadCore<T>(filename);
    }

    private T LoadCore<T>(string filename) where T : new()
    {
        var path   = GetConfigPath(filename);
        var backup = path + ".bak";

        foreach (var candidate in new[] { path, backup })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var json   = File.ReadAllText(candidate);
                var parsed = JsonSerializer.Deserialize<T>(json, _jsonOptions);
                if (parsed != null) return parsed;
            }
            catch (Exception ex)
            {
                Log.Warning($"Config file corrupted ({candidate}): {ex.Message}");
            }
        }
        return new T();
    }

    /// <summary>
    ///     Serializes <paramref name="value"/> and writes it atomically via a temp file,
    ///     preserving the previous version as <c>.bak</c>.
    /// </summary>
    /// <typeparam name="T"> The config model type. </typeparam>
    /// <param name="filename"> The config file name to write. </param>
    /// <param name="value"> The config value to persist. </param>
    public void Save<T>(string filename, T value)
        => SaveCore(filename, value, secret: false);

    /// <summary>
    ///     Atomic save for a credential-bearing config. On Unix the temporary,
    ///     primary, and backup files are owner-readable/writable from the instant
    ///     they are created, rather than being tightened only after the write.
    /// </summary>
    /// <typeparam name="T"> The config model type. </typeparam>
    /// <param name="filename"> The config file name to write. </param>
    /// <param name="value"> The config value to persist. </param>
    public void SaveSecret<T>(string filename, T value)
        => SaveCore(filename, value, secret: true);

    private void SaveCore<T>(string filename, T value, bool secret)
    {
        var path   = GetConfigPath(filename);
        var backup = path + ".bak";
        var temp   = path + ".tmp";
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(value, _jsonOptions);
            if (secret && !OperatingSystem.IsWindows())
            {
                if (File.Exists(temp)) File.Delete(temp);
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                };
                using var stream = new FileStream(temp, options);
                using var writer = new StreamWriter(stream);
                writer.Write(json);
            }
            else
            {
                File.WriteAllText(temp, json);
            }
            if (File.Exists(path))
                File.Copy(path, backup, overwrite: true);
            File.Move(temp, path, overwrite: true);

            if (secret && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (File.Exists(backup))
                    File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }
}
