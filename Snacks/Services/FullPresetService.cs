using System.Text.Json;
using System.Text.Json.Nodes;
using Snacks.Controllers;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
/// Owns preset definitions, selection metadata and a single commit of encoder +
/// archive settings. Applying never scans, enqueues, re-evaluates, or starts work.
/// </summary>
public sealed class FullPresetService
{
    public const string DefaultKey = "my-archive";
    public const int RuleVersion = 1;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] LocalOptions = ["outputDirectory", "encodeDirectory", "hardwareDevicePath", "music.outputDirectory"];
    private static readonly string[] LocalArchive = ["outputDirectory", "backupDirectory", "cacheDirectory", "revision"];
    private readonly ConfigFileService _config;
    private readonly ArchiveService _archive;
    private readonly TranscodingService? _transcoding;
    private readonly FfmpegCapabilityService? _capabilities;
    private readonly AutoScanService? _autoScan;

    public FullPresetService(ConfigFileService config, ArchiveService archive,
        TranscodingService? transcoding = null, FfmpegCapabilityService? capabilities = null, AutoScanService? autoScan = null)
    { _config = config; _archive = archive; _transcoding = transcoding; _capabilities = capabilities; _autoScan = autoScan; }

    /// <summary>Only a brand-new profile receives the default preset automatically.</summary>
    public bool InitializeFreshProfile()
    {
        lock (_config.SyncRoot)
        {
            if (new[] { "settings.json", "archive.json", "preset-selection.json" }
                .Any(n => File.Exists(_config.GetConfigPath(n)) || File.Exists(_config.GetConfigPath(n + ".bak")))) return false;
            Apply(new ApplyProcessingPresetRequest { Key = DefaultKey });
            return true;
        }
    }

    public object Catalog()
    {
        lock (_config.SyncRoot)
            return new { presets = LoadPresets(), builtins = Builtins(), defaultKey = DefaultKey, current = Current() };
    }

    public ProcessingPreset Definition(string key) => Builtins().FirstOrDefault(p => p.Key == key)
        ?? throw new KeyNotFoundException("No built-in preset with that key.");

    public CurrentProcessingPreset Current()
    {
        lock (_config.SyncRoot)
        {
            var options = ReadOptions();
            var effective = options.Deserialize<EncoderOptions>(Json)!;
            EnvConfigOverrides.Apply(effective, EnvConfigOverrides.SettingsPrefix);
            var effectiveNode = CompleteWithUnknown(JsonSerializer.SerializeToNode(effective, Json)!, options).AsObject();
            effectiveNode["_envLocked"] = JsonSerializer.SerializeToNode(EnvConfigOverrides.LockedPaths(EnvConfigOverrides.SettingsPrefix, typeof(EncoderOptions)), Json);
            var archive = JsonSerializer.SerializeToNode(_archive.GetSettings(), Json)!.AsObject();
            var selection = _config.Load<PresetSelection>("preset-selection.json");
            var all = Builtins().Concat(LoadPresets()).ToList();
            var selected = all.FirstOrDefault(p => p.Key == selection.Key);
            var hasSelection = !string.IsNullOrEmpty(selection.Key);
            var active = hasSelection
                ? selected != null && Matches(selected, options, archive) ? selected : null
                : all.FirstOrDefault(p => Matches(p, options, archive));
            return new CurrentProcessingPreset
            {
                SelectedKey = selection.Key, SelectedName = selection.Name,
                ActiveKey = active?.Key, Modified = hasSelection && active == null,
                Options = Element(effectiveNode), Archive = Element(archive),
                Requirements = Requirements(), Scope = Scope(includeEnvironment: true),
            };
        }
    }

    public CurrentProcessingPreset Apply(ApplyProcessingPresetRequest request)
    {
        lock (_config.SyncRoot)
        {
            ProcessingPreset preset;
            if (!string.IsNullOrWhiteSpace(request.Key))
                preset = Builtins().Concat(LoadPresets()).FirstOrDefault(p => p.Key == request.Key)
                    ?? throw new KeyNotFoundException("No preset with that key.");
            else if (!string.IsNullOrWhiteSpace(request.Name))
                preset = LoadPresets().FirstOrDefault(p => p.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException("No preset with that name.");
            else throw new ArgumentException("A preset key or name is required.");

            var current = ReadOptions();
            var options = NormalizeOptions(preset.Options);
            // Unknown local options survive; only supported fields have executable meaning.
            options = CompleteWithUnknown(options, current).AsObject();
            foreach (var path in LocalOptions.Concat(EnvConfigOverrides.LockedPaths(EnvConfigOverrides.SettingsPrefix, typeof(EncoderOptions))))
                CopyPath(current, options, path);
            var parsed = options.Deserialize<EncoderOptions>(Json)!;
            ValidateOptions(parsed, checkFolderReferences: true);

            var currentArchive = _archive.GetSettings();
            var archive = preset.Legacy || preset.Archive == null
                ? ArchiveService.Clone(currentArchive)
                : NormalizeArchive(preset.Archive.Value).Deserialize<ArchiveSettings>(Json)!;
            if (preset.Legacy || preset.Archive == null) archive.Mode = "original";
            archive.OutputDirectory = currentArchive.OutputDirectory;
            archive.BackupDirectory = currentArchive.BackupDirectory;
            archive.CacheDirectory = currentArchive.CacheDirectory;
            var preparedArchive = _archive.PrepareSettings(archive);
            var selection = new PresetSelection { Key = preset.Key, Name = preset.Name };
            _config.SaveSettingsBatch(new Dictionary<string, string>
            {
                ["settings.json"] = options.ToJsonString(Json),
                ["archive.json"] = JsonSerializer.Serialize(preparedArchive, Json),
                ["preset-selection.json"] = JsonSerializer.Serialize(selection, Json),
            });
            // In-memory state changes only after all three files have committed.
            _archive.AcceptPersistedSettings(preparedArchive);
            EnvConfigOverrides.Apply(parsed, EnvConfigOverrides.SettingsPrefix);
            _transcoding?.UpdateOptions(parsed);
            _capabilities?.Invalidate();
            return Current();
        }
    }

    public ProcessingPreset Save(ProcessingPresetRequest request)
    {
        lock (_config.SyncRoot)
        {
            ValidateName(request.Name);
            if (request.Options.ValueKind != JsonValueKind.Object) throw new ArgumentException("Preset options must be an object.");
            // A form may omit non-rendered fields. Capture their current values too.
            var options = ReadOptions();
            var incoming = JsonNode.Parse(request.Options.GetRawText())!.AsObject();
            EnvConfigOverrides.StripLockedPaths(incoming, EnvConfigOverrides.SettingsPrefix, typeof(EncoderOptions));
            Overlay(options, incoming);
            var archive = JsonSerializer.SerializeToNode(_archive.GetSettings(), Json)!.AsObject();
            if (request.Archive is { ValueKind: JsonValueKind.Object } a) Overlay(archive, JsonNode.Parse(a.GetRawText())!.AsObject());
            else if (request.Archive is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
                throw new ArgumentException("Preset archive settings must be an object.");
            var preset = Create(request.Name.Trim(), Element(options), Element(archive), legacy: false);
            Store(preset);
            return preset;
        }
    }

    public ProcessingPreset Import(JsonElement file)
    {
        lock (_config.SyncRoot)
        {
            if (file.ValueKind != JsonValueKind.Object) throw new ArgumentException("Not a Snacks preset file.");
            var node = JsonNode.Parse(file.GetRawText())!.AsObject();
            var version = Find(node, "snacksPreset")?.GetValue<int>() ?? 0;
            if (version is not (1 or 2)) throw new ArgumentException("Unsupported Snacks preset version.");
            var name = Find(node, "name")?.GetValue<string>() ?? "";
            var options = Find(node, "options") as JsonObject ?? throw new ArgumentException("Preset options must be an object.");
            var archive = Find(node, "archive") as JsonObject;
            var legacy = version == 1 || Find(node, "legacy")?.GetValue<bool>() == true;
            if (!legacy && archive == null) throw new ArgumentException("A version 2 preset must contain archive settings.");
            if (!legacy && (Find(node, "ruleVersion")?.GetValue<int>() ?? RuleVersion) != RuleVersion)
                throw new ArgumentException("This archive rule version is not supported by this app.");
            // Incoming descriptions cannot redefine executable archival rules.
            var preset = Create(name.Trim(), Element(options), legacy ? null : Element(archive!), legacy);
            Store(preset);
            return preset;
        }
    }

    public ProcessingPreset Export(string name)
    {
        lock (_config.SyncRoot)
            return LoadPresets().Concat(Builtins()).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Key == name)
                ?? throw new KeyNotFoundException("No preset with that name.");
    }

    public void Delete(string name)
    {
        lock (_config.SyncRoot)
        {
            var presets = LoadPresets();
            if (presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0)
                throw new KeyNotFoundException("No preset with that name.");
            _config.Save("presets.json", presets);
        }
    }

    private void Store(ProcessingPreset preset)
    {
        var presets = LoadPresets();
        var previous = presets.FirstOrDefault(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
        preset.Key = previous?.Key ?? "saved-" + Guid.NewGuid().ToString("N");
        presets.RemoveAll(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
        if (presets.Count >= 50) throw new ArgumentException("Preset limit reached (50) — delete one first.");
        presets.Add(preset);
        _config.Save("presets.json", presets);
    }

    private List<ProcessingPreset> LoadPresets()
    {
        var result = new List<ProcessingPreset>();
        foreach (var stored in _config.Load<List<ProcessingPreset>>("presets.json"))
        {
            try
            {
                var preset = Create(stored.Name, stored.Options, stored.Archive,
                    stored.Legacy || stored.Archive is not { ValueKind: JsonValueKind.Object });
                // Stable deterministic identity for pre-v2 files, without rewriting on read.
                preset.Key = string.IsNullOrEmpty(stored.Key)
                    ? "saved-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stored.Name.ToUpperInvariant())))[..24].ToLowerInvariant()
                    : stored.Key;
                preset.CreatedAt = stored.CreatedAt;
                result.Add(preset);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or ArchiveException)
            { Log.Warning("Could not load preset {Name}: {Error}", stored.Name, ex.Message); }
        }
        return result;
    }

    private ProcessingPreset Create(string name, JsonElement options, JsonElement? archive, bool legacy)
    {
        ValidateName(name);
        var normalized = NormalizeOptions(options);
        ValidateOptions(normalized.Deserialize<EncoderOptions>(Json)!, checkFolderReferences: false);
        foreach (var path in LocalOptions) RemovePath(normalized, path);
        JsonObject? normalizedArchive = null;
        if (!legacy)
        {
            normalizedArchive = NormalizeArchive(archive ?? throw new ArgumentException("Archive settings are required."));
            foreach (var path in LocalArchive) RemovePath(normalizedArchive, path);
        }
        return new ProcessingPreset
        {
            Name = name, Options = Element(normalized), Archive = normalizedArchive == null ? null : Element(normalizedArchive),
            Legacy = legacy, Requirements = Requirements(), Scope = Scope(),
        };
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) throw new ArgumentException("Preset name must be 1–80 characters.");
    }

    private void ValidateOptions(EncoderOptions options, bool checkFolderReferences)
    {
        var validation = AdvancedVideoValidator.Validate(options.AdvancedVideo);
        if (!validation.IsValid) throw new ArgumentException("Preset contains invalid advanced video settings: " + string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
        if (checkFolderReferences && _autoScan != null)
            foreach (var folder in _autoScan.GetConfig().Directories)
                if (folder.EncodingOverrides is { AdvancedVideoPolicy: AdvancedVideoFolderPolicy.Profile } policy
                    && (!policy.AdvancedVideoProfileId.HasValue || !options.AdvancedVideo.Profiles.Any(p => p.Id == policy.AdvancedVideoProfileId.Value)))
                    throw new ArgumentException("A watched folder references an advanced video profile absent from this preset. Reassign that folder before applying the preset.");
    }

    private JsonObject ReadOptions()
    {
        foreach (var suffix in new[] { "", ".bak" })
        {
            var path = _config.GetConfigPath("settings.json" + suffix);
            if (!File.Exists(path)) continue;
            try { return NormalizeOptions(JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path))); }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            { Log.Warning("Settings file corrupted: {Path}", path); }
        }
        return JsonSerializer.SerializeToNode(new EncoderOptions(), Json)!.AsObject();
    }

    internal static JsonObject NormalizeOptions(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) throw new ArgumentException("Preset options must be an object.");
        var options = raw.Deserialize<EncoderOptions>(Json)!;
        options.AdvancedVideo ??= new(); options.Music ??= new();
        options.AudioOutputs ??= []; options.AudioLanguagesToKeep ??= []; options.SubtitleLanguagesToKeep ??= [];
        SettingsController.MigrateLegacyAudioIfNeeded(options, raw.GetRawText());
        var normalized = CompleteWithUnknown(JsonSerializer.SerializeToNode(options, Json)!, JsonNode.Parse(raw.GetRawText())!).AsObject();
        RemovePath(normalized, "_envLocked");
        return normalized;
    }

    private static JsonObject NormalizeArchive(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) throw new ArgumentException("Preset archive settings must be an object.");
        var archive = raw.Deserialize<ArchiveSettings>(Json)!;
        if (archive.RuleVersion != RuleVersion) throw new ArgumentException("This archive rule version is not supported by this app.");
        ArchiveService.ValidateSettings(archive);
        return CompleteWithUnknown(JsonSerializer.SerializeToNode(archive, Json)!, JsonNode.Parse(raw.GetRawText())!).AsObject();
    }

    private static bool Matches(ProcessingPreset preset, JsonObject options, JsonObject archive)
    {
        // Known supported settings are compared in full; unknown future fields
        // remain shareable but cannot claim to be executable in this version.
        var actualOptions = JsonSerializer.SerializeToNode(options.Deserialize<EncoderOptions>(Json), Json)!.AsObject();
        var expectedOptions = JsonSerializer.SerializeToNode(preset.Options.Deserialize<EncoderOptions>(Json), Json)!.AsObject();
        foreach (var path in LocalOptions.Concat(EnvConfigOverrides.LockedPaths(EnvConfigOverrides.SettingsPrefix, typeof(EncoderOptions))))
        { RemovePath(actualOptions, path); RemovePath(expectedOptions, path); }
        if (!JsonNode.DeepEquals(actualOptions, expectedOptions)) return false;
        if (preset.Legacy || preset.Archive == null) return archive["mode"]?.GetValue<string>() == "original";
        var expectedArchive = JsonSerializer.SerializeToNode(preset.Archive.Value.Deserialize<ArchiveSettings>(Json), Json)!.AsObject();
        var actualArchive = archive.DeepClone().AsObject();
        foreach (var path in LocalArchive) { RemovePath(actualArchive, path); RemovePath(expectedArchive, path); }
        return JsonNode.DeepEquals(actualArchive, expectedArchive);
    }

    private static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node, Json);
    private static string? FindKey(JsonObject node, string name) => node.Select(p => p.Key).LastOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static JsonNode? Find(JsonObject node, string name) => FindKey(node, name) is { } key ? node[key] : null;

    /// <summary>Normalized known values win; unrecognized fields are carried through.</summary>
    private static JsonNode CompleteWithUnknown(JsonNode normalized, JsonNode raw)
    {
        if (normalized is JsonObject target && raw is JsonObject source)
            foreach (var (key, value) in source)
            {
                var known = FindKey(target, key);
                if (known == null) target[key] = value?.DeepClone();
                else if (target[known] != null && value != null) CompleteWithUnknown(target[known]!, value);
            }
        else if (normalized is JsonArray targets && raw is JsonArray sources)
            for (var i = 0; i < Math.Min(targets.Count, sources.Count); i++)
                if (targets[i] != null && sources[i] != null) CompleteWithUnknown(targets[i]!, sources[i]!);
        return normalized;
    }

    private static void Overlay(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            var canonical = FindKey(target, key) ?? key;
            if (target[canonical] is JsonObject existing && value is JsonObject incoming) Overlay(existing, incoming);
            else target[canonical] = value?.DeepClone();
        }
    }

    private static void RemovePath(JsonObject node, string path)
    {
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length - 1; i++)
        { if (Find(node, segments[i]) is not JsonObject next) return; node = next; }
        foreach (var key in node.Select(p => p.Key).Where(k => k.Equals(segments[^1], StringComparison.OrdinalIgnoreCase)).ToArray()) node.Remove(key);
    }

    private static void CopyPath(JsonObject source, JsonObject target, string path)
    {
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (Find(source, segments[i]) is not JsonObject next) { RemovePath(target, string.Join('.', segments.Skip(i))); return; }
            source = next;
            var key = FindKey(target, segments[i]) ?? segments[i];
            if (target[key] is not JsonObject) target[key] = new JsonObject();
            target = target[key]!.AsObject();
        }
        var leaf = FindKey(source, segments[^1]);
        RemovePath(target, segments[^1]);
        if (leaf != null) target[segments[^1]] = source[leaf]?.DeepClone();
    }

    public static List<ProcessingPreset> Builtins()
    {
        ProcessingPreset Make(string key, string name, EncoderOptions options, bool custom = false)
        {
            var opt = JsonSerializer.SerializeToNode(options, Json)!.AsObject();
            var archive = JsonSerializer.SerializeToNode(new ArchiveSettings { Mode = custom ? "custom" : "original", BackupEnabled = !custom }, Json)!.AsObject();
            foreach (var path in LocalOptions) RemovePath(opt, path);
            foreach (var path in LocalArchive) RemovePath(archive, path);
            return new ProcessingPreset { Key = key, Name = name, BuiltIn = true, CreatedAt = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc), Options = Element(opt), Archive = Element(archive), Requirements = Requirements(), Scope = Scope() };
        }
        return
        [
            Make(DefaultKey, "我的归档方案", new EncoderOptions(), custom: true),
            Make("space-saver", "Space Saver", new EncoderOptions { TargetBitrate = 2500, FourKBitrateMultiplier = 3, FfmpegQualityPreset = "slow" }),
            Make("balanced", "Balanced", new EncoderOptions()),
            Make("quality-first", "Quality First", new EncoderOptions { TargetBitrate = 6000, FfmpegQualityPreset = "slow" }),
            Make("max-compat", "Max Compatibility", new EncoderOptions { Format = "mp4", Codec = "h264", Encoder = "libx264", TargetBitrate = 4500 }),
            Make("ipod-classic", "iPod Classic", new EncoderOptions { Format = "mp4", Codec = "h264", Encoder = "libx264", HardwareAcceleration = "none", TargetBitrate = 1500, StrictBitrate = true, FourKBitrateMultiplier = 1, FixedFrameSize = "640x480", VideoProfile = "baseline", VideoLevel = "3.0", MaxFrameRate = 30, TonemapHdrToSdr = true, PreserveOriginalAudio = false, AudioOutputs = [new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 160, SampleRateHz = 48000 }] }),
        ];
    }

    private static PresetScope Scope(bool includeEnvironment = false) => new()
    {
        Included =
        [
            "All supported original-mode video, audio, subtitle, mux, advanced video, queue, log and music settings.",
            "Reusable archive settings, the processing mode and archive rule version 1.",
            "In custom mode the archive rules govern video processing; original-mode defaults are retained for switching back.",
        ],
        Preserved =
        [
            "Local output, temporary, backup and cache directories; machine-specific device paths.",
            "Connections, authentication, accounts, integrations, watched folders, schedules and device configuration.",
            "Current environment-locked settings; these remain effective and do not affect preset matching.",
            "Existing jobs, frozen archive rules, history and task state. Applying a preset does not scan, start or re-evaluate the queue.",
            "Unknown future option fields round-trip in saved presets but have no executable meaning in this version.",
        ],
        IgnoredForMatching = LocalOptions.Select(p => "options." + p).Concat(LocalArchive.Select(p => "archive." + p))
            .Concat(includeEnvironment ? EnvConfigOverrides.LockedPaths(EnvConfigOverrides.SettingsPrefix, typeof(EncoderOptions)).Select(p => "options." + p) : []).ToList(),
    };

    private static PresetRequirements Requirements() => new()
    {
        Items =
        [
            new("codecRules", "Codec-specific archive rules", "HEVC is skipped below 1.25 times the ceiling; H.264 targets half the source bitrate, MPEG-2 one quarter, ProRes one thirty-fourth and DNxHR (reported as dnxhd) one thirty-third, each capped at the configured ceiling. Unknown codecs and alpha video stay unchanged."),
            new("contentChecks", "No additional content checks", "Read stream headers to choose encoding parameters; estimate bitrate from container metadata when unavailable. Do not scan all packets, hash media, decode for acceptance, or apply output bitrate, duration, frame-count or size-reduction gates."),
            new("preservation", "Preserve source specifications and tracks", "Keep the source container, resolution, frame rate, pixel format, bit depth, color information, all audio and subtitle tracks, chapters and supported metadata. Skip unsupported preservation cases. Prefer compatible Apple hardware; otherwise use software HEVC."),
            new("outputBackup", "Output and original backups", "Default to in-place output with complete original backups on the same volume and a 3-day cleanup reminder. Copy errors stop replacement; backups are never overwritten. Reminders never delete backups automatically."),
            new("restore", "Restore registered backups", "Restore explicitly selected registered original backups without content comparison. Keep the backup and refuse conflicting transaction paths."),
            new("history", "Persistent history and duplicate protection", "Keep durable tasks and history using paths, file lengths and modification times. Changed files at a reused path can be scanned again. Legacy records without file identity require an explicit history action. No media hashing or recognition of renamed duplicates is performed."),
            new("sampling", "Historical scores only", "Automatic scoring and manual rescoring are disabled. Existing historical scores remain visible; no comparison samples are created."),
            new("cache", "Bounded comparison cache", "The existing cache limit stays at 5 GB. Old registered cache can be cleared; this version creates no comparison samples."),
            new("concurrency", "One shared queue and concurrency limit", "Archive tasks use the existing persistent queue and shared slot ledger. Default global concurrency is 10, adjustable from 1 to 20. Each new task freezes its settings."),
            new("storageShutdown", "Storage interruption and safe exit", "Keep path ownership, nonempty outputs, copy errors and staged replacement handling. Stop on storage errors and safely finish file moves before exiting; no content verification is performed."),
            new("watchAndStart", "Manual preview before start", "Applying this preset never enables folder monitoring, scans media or starts work. Preview the selected files, review skips and failures, then explicitly start the task."),
        ],
    };
}
