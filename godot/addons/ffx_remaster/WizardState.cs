#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>One texture of a map's wizard pass: what was chosen and why, the review's decision, the staged entry.</summary>
public sealed class WizardTextureState
{
    public string Id { get; set; } = "";

    /// <summary>The classifier's kind and starter profile (empty: keep classic), its score, lead and reasons.</summary>
    public string Kind { get; set; } = "";
    public string AutoProfile { get; set; } = "";
    public float Score { get; set; }
    public float Margin { get; set; }
    public string Why { get; set; } = "";

    /// <summary>The profile the staged material was made with (empty: none made: classic).</summary>
    public string Profile { get; set; } = "";

    /// <summary>"auto" (as chosen), "accepted", "rejected" (classic, or made again with another profile: then accepted).</summary>
    public string Decision { get; set; } = "auto";

    /// <summary>The staged entry (<see cref="TextureStore.ToDictionary"/> as Godot's variant text; empty: none), and its albedo and normal maps (the store's paths).</summary>
    public string Entry { get; set; } = "";
    public string Albedo { get; set; } = "";
    public string Normal { get; set; } = "";

    /// <summary>The map's surfaces drawing it and their area (render units²).</summary>
    public int Surfaces { get; set; }
    public float Area { get; set; }

    public string Error { get; set; } = "";

    /// <summary>Whether accepting the map gives the project this texture's material.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Applies => Decision != "rejected" && Entry.Length > 0 && Profile.Length > 0;
}

/// <summary>One map's wizard pass, staged until accepted.</summary>
public sealed class WizardMapState
{
    public string Key { get; set; } = "";
    public string Level { get; set; } = "";

    /// <summary>"making", "staged", "accepted", "discarded", "skipped" (2D layers only), "failed".</summary>
    public string State { get; set; } = "making";
    public string Reason { get; set; } = "";

    /// <summary>When the pass ran and how long it took (ms: analysis, bakes, pictures).</summary>
    public string When { get; set; } = "";
    public double Ms { get; set; }
    public double BakeMs { get; set; }

    public List<WizardTextureState> Textures { get; set; } = new();

    /// <summary>The staged lighting: the <see cref="MapLighting"/> fields that differ from the defaults (name → Godot's variant text).</summary>
    public Dictionary<string, string> Lighting { get; set; } = new();

    /// <summary>The colour check (classic against remastered pictures): its line, whether it passed, and the pictures (empty: no renderer).</summary>
    public string Colour { get; set; } = "";
    public bool ColourOk { get; set; } = true;
    public List<string> Before { get; set; } = new();
    public List<string> After { get; set; } = new();

    /// <summary>The note the pass writes into the map's notes on accept.</summary>
    public string Note { get; set; } = "";

    public MapLighting LightingResource()
    {
        var m = (MapLighting)LightingLayers.Defaults.Duplicate(true);
        m.Level = Key;
        foreach (var (k, v) in Lighting)
            m.Set(k, GD.StrToVar(v));
        return m;
    }

    public static Dictionary<string, string> LightingFields(MapLighting m)
    {
        var d = new Dictionary<string, string>();
        var defaults = LightingLayers.Defaults;
        foreach (var p in m.GetPropertyList())
        {
            var name = p["name"].AsString();
            if (name is "Level" or "Volumes" || !((PropertyUsageFlags)p["usage"].AsInt64()).HasFlag(PropertyUsageFlags.ScriptVariable))
                continue;
            var v = GD.VarToStr(m.Get(name));
            if (v != GD.VarToStr(defaults.Get(name)))
                d[name] = v;
        }
        return d;
    }
}

/// <summary>The batch: the maps in order, how far it is, whether it is paused.</summary>
public sealed class WizardBatch
{
    public List<string> Maps { get; set; } = new();
    public int Next { get; set; }
    public bool Paused { get; set; }

    /// <summary>2D-layer maps: "skip" (not in the pass) or "classic" (noted as kept classic).</summary>
    public string TwoD { get; set; } = "skip";
    public string Started { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Finished => Next >= Maps.Count;
}

/// <summary>
/// The wizard's state (docs/remaster-editor.md, "The remaster wizard"), saved in the project's
/// remaster folder (<c>res://remaster/wizard/state.json</c>, or <c>--remaster-wizard-dir=DIR</c>) so
/// a batch pauses and resumes across editor sessions: the batch and each map's staged pass (IDs,
/// profiles, scores, the entries as made, the lighting's changed fields). Text, sorted keys. The
/// pictures are the player's game: kept in <c>user://remaster/wizard/</c> (the check's DIR/pictures).
/// </summary>
public sealed class WizardState
{
    public int Version { get; set; } = 1;
    public WizardBatch? Batch { get; set; }
    public SortedDictionary<string, WizardMapState> Maps { get; set; } = new();

    public static string Dir => GameSettings.Arg("remaster-wizard-dir") is { Length: > 0 } d ? d.TrimEnd('/') : "res://remaster/wizard";

    public static string PathOf => $"{Dir}/state.json";

    /// <summary>Where a map's pictures go (the player's: never in git).</summary>
    public static string PicturesDir(string key) =>
        GameSettings.Arg("remaster-wizard-dir") is { Length: > 0 } ? $"{Dir}/pictures/{key}" : $"user://remaster/wizard/{key}";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static WizardState Load()
    {
        if (!FileAccess.FileExists(PathOf))
            return new WizardState();
        try
        {
            return JsonSerializer.Deserialize<WizardState>(FileAccess.GetFileAsString(PathOf), Options) ?? new WizardState();
        }
        catch (JsonException e)
        {
            GD.PrintErr($"ffx remaster: wizard: {PathOf} does not read ({e.Message}); starting afresh");
            return new WizardState();
        }
    }

    public void Save()
    {
        DirAccess.MakeDirRecursiveAbsolute(GameSettings.Globalize(Dir));
        System.IO.File.WriteAllText(GameSettings.Globalize(PathOf), JsonSerializer.Serialize(this, Options) + "\n");
    }

    public static string MapText(WizardMapState? m) => m == null ? "" : JsonSerializer.Serialize(m, Options);

    public static WizardMapState? MapFrom(string text) => text.Length == 0 ? null : JsonSerializer.Deserialize<WizardMapState>(text, Options);

    public static string BatchText(WizardBatch? b) => b == null ? "" : JsonSerializer.Serialize(b, Options);

    public static WizardBatch? BatchFrom(string text) => text.Length == 0 ? null : JsonSerializer.Deserialize<WizardBatch>(text, Options);

    /// <summary>The batch's summary: maps done, staged, accepted, skipped, failed; materials per profile; the mean time per map.</summary>
    public string Summary()
    {
        var maps = Batch?.Maps.Select(k => Maps.GetValueOrDefault(k)).Where(m => m != null).Cast<WizardMapState>().ToList() ?? Maps.Values.ToList();
        var done = maps.Where(m => m.State is "staged" or "accepted" or "discarded").ToList();
        var profiles = done.SelectMany(m => m.Textures).Where(t => t.Applies).GroupBy(t => t.Profile).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => $"{g.Key} {g.Count()}");
        int kept = done.SelectMany(m => m.Textures).Count(t => !t.Applies);
        var timed = done.Where(m => m.Ms > 0).ToList();
        return $"{done.Count} map(s) done ({maps.Count(m => m.State == "staged")} to review, {maps.Count(m => m.State == "accepted")} accepted), "
            + $"{maps.Count(m => m.State == "skipped")} skipped (2D layers), {maps.Count(m => m.State == "failed")} failed"
            + (Batch != null ? $", {Batch.Maps.Count - Batch.Next} to go{(Batch.Paused ? " (paused)" : "")}" : "")
            + $"; materials: {string.Join(", ", profiles)}{(kept > 0 ? $"; {kept} texture(s) kept classic" : "")}"
            + (timed.Count > 0 ? $"; {timed.Average(m => m.Ms) / 1000:0.0} s a map ({string.Join(", ", timed.Select(m => $"{m.Key} {m.Ms / 1000:0.0} s"))})" : "")
            + (maps.Any(m => m.State == "failed") ? $"; failures: {string.Join("; ", maps.Where(m => m.State == "failed").Select(m => $"{m.Key}: {m.Reason}"))}" : "");
    }
}
#endif
