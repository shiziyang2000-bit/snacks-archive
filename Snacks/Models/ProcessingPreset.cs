using System.Text.Json;

namespace Snacks.Models;

/// <summary>A portable snapshot of every supported processing setting.</summary>
public sealed class ProcessingPreset
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool BuiltIn { get; set; }
    public int SchemaVersion { get; set; } = 2;
    // Raw JSON retains unrecognized fields when sharing a newer preset.
    public JsonElement Options { get; set; }
    public JsonElement? Archive { get; set; }
    public int RuleVersion { get; set; } = 1;
    public PresetRequirements Requirements { get; set; } = new();
    public PresetScope Scope { get; set; } = new();
    public bool Legacy { get; set; }
}

public sealed class ProcessingPresetRequest
{
    public string Name { get; set; } = "";
    public JsonElement Options { get; set; }
    public JsonElement? Archive { get; set; }
}

public sealed class ApplyProcessingPresetRequest
{
    public string? Key { get; set; }
    public string? Name { get; set; }
}

public sealed class PresetSelection
{
    public string? Key { get; set; }
    public string? Name { get; set; }
}

public sealed class PresetRequirements
{
    public int RuleVersion { get; set; } = 1;
    public List<PresetRequirement> Items { get; set; } = [];
}

public sealed record PresetRequirement(string Key, string Title, string Description);

public sealed class PresetScope
{
    public List<string> Included { get; set; } = [];
    public List<string> Preserved { get; set; } = [];
    public List<string> IgnoredForMatching { get; set; } = [];
}

public sealed class CurrentProcessingPreset
{
    public string? SelectedKey { get; set; }
    public string? SelectedName { get; set; }
    public string? ActiveKey { get; set; }
    public bool Modified { get; set; }
    public JsonElement Options { get; set; }
    public JsonElement Archive { get; set; }
    public PresetRequirements Requirements { get; set; } = new();
    public PresetScope Scope { get; set; } = new();
}
